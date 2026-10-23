using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IntelligentScheduler.Application;
using IntelligentScheduler.Domain;
using IntelligentScheduler.Infrastructure.CloudSync;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace IntelligentScheduler.Infrastructure;

/// <summary>
/// Moves the whole SQLite database between this machine and a cloud backend.
///
/// Whole-file rather than row-level on purpose. The data model has no per-row change log, so a
/// merge would have to guess, and guessing wrong loses a user's schedule silently. Instead the
/// service decides which side is ahead, refuses to act when both are, and always writes a local
/// backup before overwriting anything.
///
/// What travels is the schedule, not the machine. A downloaded database is staged next to the
/// live one and swapped in by <see cref="ApplyPendingRestore"/> before the first connection opens
/// on the next launch, and this machine's own settings row is carried across into it: the
/// Google sign-in is encrypted with a key that never leaves this computer, and a sign-in taken
/// from another machine is one that can no longer be read.
/// </summary>
public sealed class CloudSyncService : ICloudSyncService, IDisposable
{
    /// <summary>Name of the staged download, picked up on the next launch.</summary>
    public const string PendingRestoreSuffix = ".cloud-restore";

    /// <summary>This machine's record of its exchanges, beside the database rather than in it.</summary>
    public const string JournalFileName = "cloud-sync-journal.json";

    private const int JournalLength = 40;

    /// <summary>
    /// The tables that make up the schedule — everything the user made. AppSettings stays out
    /// (settings belong to each machine, and sync bookkeeping writes to them), and so does the
    /// planner's diagnostic log, which records runs rather than anything the user did.
    /// </summary>
    private static readonly string[] SyncedTables =
    [
        "Tasks", "CalendarEvents", "TimeBlocks", "TaskScheduleExclusions", "AvailabilityRules",
        "AvailabilityExceptions", "PlanningProfiles", "PlanRevisions", "TaskExecutions",
        "ChatConversations", "ChatMessages", "UserMemoryEntries", "EventOutcomes", "EventFollowUps",
        "Projects", "ProjectSteps", "ProjectStepChecks"
    ];

    private static readonly JsonSerializerOptions JournalJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly ISettingsRepository _settings;
    private readonly ISecretProtector _secrets;
    private readonly FolderSyncBackend _folder;
    private readonly GoogleDriveSyncBackend _google;
    private readonly string _databasePath;
    private readonly string _journalPath;
    private readonly string _appVersion;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _journalGate = new();
    private readonly List<CloudSyncLogEntry> _journal;

    public CloudSyncService(
        IDbContextFactory<AppDbContext> factory,
        ISettingsRepository settings,
        ISecretProtector secrets,
        string databasePath,
        string appVersion)
    {
        _factory = factory;
        _settings = settings;
        _secrets = secrets;
        _databasePath = databasePath;
        _journalPath = JournalPath(databasePath);
        _appVersion = appVersion;
        _folder = new FolderSyncBackend();
        _google = new GoogleDriveSyncBackend(secrets);
        _journal = ReadJournal(_journalPath);
    }

    public event EventHandler<CloudSyncReport>? Reported;

    public bool IsBusy { get; private set; }

    /// <summary>Where a downloaded database waits until the next launch.</summary>
    private string PendingRestorePath => _databasePath + PendingRestoreSuffix;

    public bool HasPendingRestore => File.Exists(PendingRestorePath);

    public IReadOnlyList<CloudSyncLogEntry> History
    {
        get { lock (_journalGate) return _journal.ToList(); }
    }

    private ICloudSyncBackend? Backend(AppSettings settings) => settings.CloudSyncProvider switch
    {
        CloudSyncProvider.Folder => _folder,
        CloudSyncProvider.GoogleDrive => _google,
        _ => null
    };

    // ---------------------------------------------------------------- inspection

    public async Task<CloudReadiness> CheckReadyAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _settings.GetAppSettingsAsync(cancellationToken);
        var backend = Backend(settings);
        return backend is null
            ? CloudReadiness.NotReady(CloudSyncIssue.Off, "Cloud sync is off.")
            : await backend.CheckReadyAsync(settings, cancellationToken);
    }

    public async Task<CloudSnapshot?> PeekAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _settings.GetAppSettingsAsync(cancellationToken);
        var backend = Backend(settings);
        if (backend is null) return null;
        return await backend.GetSnapshotAsync(settings, cancellationToken);
    }

    public async Task<CloudSyncStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _settings.GetAppSettingsAsync(cancellationToken);
        var backend = Backend(settings);
        var (fingerprint, newest) = await LocalStateAsync(cancellationToken);
        var history = History;
        return new CloudSyncStatus(
            settings.CloudSyncProvider,
            backend?.CheckSetup(settings) ?? CloudSyncIssue.Off,
            HasLocalChanges(settings, fingerprint, newest),
            newest == DateTimeOffset.MinValue ? null : newest,
            history.LastOrDefault(entry => entry.IsSuccess),
            history.LastOrDefault(),
            HasPendingRestore,
            IsBusy);
    }

    /// <summary>
    /// Whether this machine holds something the cloud has not seen. A profile synced by an older
    /// release has no fingerprint yet, and falls back to the newest-edit comparison it used then.
    /// </summary>
    private static bool HasLocalChanges(AppSettings settings, string fingerprint, DateTimeOffset newest)
        => string.IsNullOrEmpty(settings.CloudSyncLocalFingerprint)
            ? settings.CloudSyncLastSyncUtc is not { } lastSync || newest > lastSync
            : !string.Equals(fingerprint, settings.CloudSyncLocalFingerprint, StringComparison.Ordinal);

    // ---------------------------------------------------------------- consent

    public async Task<CloudSyncReport> AuthorizeGoogleAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _settings.GetAppSettingsAsync(cancellationToken);
        try
        {
            var authorization = await _google.AuthorizeAsync(settings, cancellationToken);
            await _settings.PatchAppSettingsAsync(stored =>
            {
                // Another account's Drive holds another snapshot; what this machine remembers
                // exchanging with the old one says nothing about the new one.
                if (!string.IsNullOrWhiteSpace(stored.GoogleDriveAccount) &&
                    !string.Equals(stored.GoogleDriveAccount, authorization.Account, StringComparison.OrdinalIgnoreCase))
                    stored.CloudSyncLastRevision = string.Empty;
                stored.GoogleDriveRefreshTokenCiphertext = _secrets.Protect(authorization.RefreshToken);
                stored.GoogleDriveAccount = authorization.Account;
                stored.CloudSyncProvider = CloudSyncProvider.GoogleDrive;
            }, cancellationToken);

            var connected = Publish(new CloudSyncReport(CloudSyncOutcome.Connected,
                string.IsNullOrWhiteSpace(authorization.Account)
                    ? "Google Drive connected."
                    : $"Connected as {authorization.Account}."), subject: authorization.Account);

            // Signed in, and the Drive refused it anyway: two separate facts, and the second is
            // the one the user has to do something about.
            return authorization.Problem is { } problem
                ? Publish(new CloudSyncReport(CloudSyncOutcome.Failed, problem.Message, Issue: problem.Issue))
                : connected;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Publish(new CloudSyncReport(CloudSyncOutcome.Failed, "Sign-in was cancelled.",
                Issue: CloudSyncIssue.SignInCancelled));
        }
        catch (CloudSyncException exception)
        {
            return Publish(new CloudSyncReport(CloudSyncOutcome.Failed, exception.Message, Issue: exception.Issue));
        }
        catch (Exception exception)
        {
            return Publish(new CloudSyncReport(CloudSyncOutcome.Failed, exception.Message, Issue: CloudSyncIssue.Unexpected));
        }
    }

    public async Task SignOutGoogleAsync(CancellationToken cancellationToken = default)
    {
        await _settings.PatchAppSettingsAsync(stored =>
        {
            stored.GoogleDriveRefreshTokenCiphertext = null;
            stored.GoogleDriveAccount = string.Empty;
            stored.CloudSyncLastRevision = string.Empty;
            stored.CloudSyncLastSyncUtc = null;
            stored.CloudSyncLocalFingerprint = string.Empty;
        }, cancellationToken);
        _google.ForgetTokens();
        Publish(new CloudSyncReport(CloudSyncOutcome.Disconnected, "The Google account was disconnected."));
    }

    /// <summary>
    /// Drops a sign-in Google no longer honours, so the settings page offers to connect again
    /// instead of showing an account as connected that cannot be used.
    /// </summary>
    private async Task ForgetGoogleSignInAsync()
    {
        try
        {
            await _settings.PatchAppSettingsAsync(stored => stored.GoogleDriveRefreshTokenCiphertext = null);
            _google.ForgetTokens();
        }
        catch (Exception)
        {
            // The report still says what went wrong; forgetting is a courtesy on top of it.
        }
    }

    // ---------------------------------------------------------------- sync

    public Task<CloudSyncReport> SyncAsync(CloudSyncDirection direction, CancellationToken cancellationToken = default)
        => SyncAsync(direction, CloudSyncTrigger.Manual, cancellationToken);

    public async Task<CloudSyncReport> SyncAsync(CloudSyncDirection direction, CloudSyncTrigger trigger,
        CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(TimeSpan.Zero, cancellationToken))
            return new CloudSyncReport(CloudSyncOutcome.Failed, "A sync is already running.",
                Issue: CloudSyncIssue.Busy, Trigger: trigger);

        IsBusy = true;
        try
        {
            var settings = await _settings.GetAppSettingsAsync(cancellationToken);
            var backend = Backend(settings);
            if (backend is null)
                return Publish(new CloudSyncReport(CloudSyncOutcome.NotConfigured, "Choose where to sync first.",
                    Issue: CloudSyncIssue.Off, Trigger: trigger));

            var readiness = await backend.CheckReadyAsync(settings, cancellationToken);
            if (!readiness.IsReady)
            {
                if (readiness.Issue == CloudSyncIssue.GoogleReconnect) await ForgetGoogleSignInAsync();
                return Publish(new CloudSyncReport(CloudSyncOutcome.NotConfigured, readiness.Message,
                    Issue: readiness.Issue, Trigger: trigger));
            }

            // A state rather than an event, so it is not written to the journal every time the
            // timer asks while the restart is still outstanding.
            if (HasPendingRestore && direction != CloudSyncDirection.Upload)
                return Publish(new CloudSyncReport(CloudSyncOutcome.Downloaded,
                    "A downloaded copy is already waiting. Restart DAYDRIVE to use it.",
                    RequiresRestart: true, Trigger: trigger), journal: false);

            var remote = await backend.GetSnapshotAsync(settings, cancellationToken);
            var (fingerprint, newest) = await LocalStateAsync(cancellationToken);
            var localChanged = HasLocalChanges(settings, fingerprint, newest);
            var remoteChanged = remote is not null &&
                                !string.Equals(remote.Revision, settings.CloudSyncLastRevision, StringComparison.Ordinal);

            var resolved = direction;
            if (resolved == CloudSyncDirection.Auto)
            {
                if (remote is null) resolved = CloudSyncDirection.Upload;
                else if (localChanged && remoteChanged)
                    return Publish(new CloudSyncReport(CloudSyncOutcome.Conflict,
                        $"This machine and the cloud copy both changed. The cloud copy is from " +
                        $"{Describe(remote)}. Choose which one to keep.", remote, Trigger: trigger));
                else if (remoteChanged) resolved = CloudSyncDirection.Download;
                else if (localChanged) resolved = CloudSyncDirection.Upload;
                else
                {
                    // Nothing moved. A profile synced by an older release gets its fingerprint
                    // now, so that from here on a deletion counts as a change.
                    if (string.IsNullOrEmpty(settings.CloudSyncLocalFingerprint))
                        await _settings.PatchAppSettingsAsync(
                            stored => stored.CloudSyncLocalFingerprint = fingerprint, cancellationToken);
                    return Publish(new CloudSyncReport(CloudSyncOutcome.UpToDate, "Everything is already in sync.",
                        remote, Trigger: trigger));
                }
            }

            return resolved == CloudSyncDirection.Download
                ? Publish(await DownloadAsync(backend, settings, remote, trigger, cancellationToken))
                : Publish(await UploadAsync(backend, settings, fingerprint, newest, trigger, cancellationToken));
        }
        catch (OperationCanceledException)
        {
            return Publish(new CloudSyncReport(CloudSyncOutcome.Failed, "The sync was interrupted.",
                Issue: CloudSyncIssue.Interrupted, Trigger: trigger));
        }
        catch (CloudSyncException exception)
        {
            if (exception.Issue == CloudSyncIssue.GoogleReconnect) await ForgetGoogleSignInAsync();
            return Publish(new CloudSyncReport(CloudSyncOutcome.Failed, exception.Message,
                Issue: exception.Issue, Trigger: trigger));
        }
        catch (HttpRequestException exception)
        {
            return Publish(new CloudSyncReport(CloudSyncOutcome.Failed, exception.Message,
                Issue: CloudSyncIssue.Offline, Trigger: trigger));
        }
        catch (Exception exception)
        {
            return Publish(new CloudSyncReport(CloudSyncOutcome.Failed, exception.Message,
                Issue: CloudSyncIssue.Unexpected, Trigger: trigger));
        }
        finally
        {
            IsBusy = false;
            _gate.Release();
        }
    }

    private async Task<CloudSyncReport> UploadAsync(ICloudSyncBackend backend, AppSettings settings,
        string fingerprint, DateTimeOffset newest, CloudSyncTrigger trigger, CancellationToken cancellationToken)
    {
        var staging = Path.Combine(Path.GetTempPath(), $"daydrive-upload-{Guid.NewGuid():N}.db.gz");
        try
        {
            // The fingerprint was taken before the copy. An edit landing in between is then in
            // the upload but not in the fingerprint, and costs one more upload — never a loss.
            await CompressDatabaseAsync(staging, cancellationToken);
            var metadata = new CloudSnapshot(string.Empty, DateTimeOffset.UtcNow,
                new FileInfo(staging).Length, DeviceName(settings), _appVersion);

            CloudSnapshot stored;
            await using (var payload = File.OpenRead(staging))
                stored = await backend.UploadAsync(settings, payload, metadata, cancellationToken);

            await _settings.PatchAppSettingsAsync(state =>
            {
                state.CloudSyncLastRevision = stored.Revision;
                state.CloudSyncLastSyncUtc = newest;
                state.CloudSyncLocalFingerprint = fingerprint;
            }, cancellationToken);

            return new CloudSyncReport(CloudSyncOutcome.Uploaded,
                $"Uploaded {FormatBytes(stored.SizeBytes)} to the cloud.", stored, Trigger: trigger);
        }
        finally
        {
            TryDelete(staging);
        }
    }

    private async Task<CloudSyncReport> DownloadAsync(ICloudSyncBackend backend, AppSettings settings,
        CloudSnapshot? remote, CloudSyncTrigger trigger, CancellationToken cancellationToken)
    {
        if (remote is null)
            return new CloudSyncReport(CloudSyncOutcome.Failed, "There is nothing in the cloud to download yet.",
                Issue: CloudSyncIssue.NothingInCloud, Trigger: trigger);

        var staging = PendingRestorePath;
        TryDelete(staging);
        try
        {
            await using (var download = await backend.DownloadAsync(settings, cancellationToken))
            await using (var decompressed = File.Create(staging))
            await using (var gzip = new GZipStream(download, CompressionMode.Decompress))
                await gzip.CopyToAsync(decompressed, cancellationToken);

            // A truncated or wrong-format download must never reach the swap on next launch.
            if (!LooksLikeSqlite(staging))
            {
                TryDelete(staging);
                return new CloudSyncReport(CloudSyncOutcome.Failed,
                    "The downloaded file is not a DAYDRIVE database. Nothing was changed.",
                    Issue: CloudSyncIssue.NotADatabase, Trigger: trigger);
            }

            await _settings.PatchAppSettingsAsync(state =>
            {
                state.CloudSyncLastRevision = remote.Revision;
                // The staged data is now what this machine is based on, so a later local edit
                // still registers as a change and gets uploaded.
                state.CloudSyncLastSyncUtc = DateTimeOffset.UtcNow;
                // Worked out again once the copy is in place; nothing here describes it yet.
                state.CloudSyncLocalFingerprint = string.Empty;
            }, cancellationToken);

            return new CloudSyncReport(CloudSyncOutcome.Downloaded,
                $"Downloaded the copy from {Describe(remote)}. Restart DAYDRIVE to use it.", remote,
                RequiresRestart: true, Trigger: trigger);
        }
        catch (InvalidDataException)
        {
            TryDelete(staging);
            return new CloudSyncReport(CloudSyncOutcome.Failed,
                "The downloaded file is damaged. Nothing was changed.",
                Issue: CloudSyncIssue.NotADatabase, Trigger: trigger);
        }
        catch
        {
            TryDelete(staging);
            throw;
        }
    }

    // ---------------------------------------------------------------- payload

    /// <summary>
    /// Checkpoints the write-ahead log first, otherwise the copied file is missing whatever is
    /// still only in the WAL — which is most of a session's edits.
    /// </summary>
    private async Task CompressDatabaseAsync(string destination, CancellationToken cancellationToken)
    {
        await using (var db = await _factory.CreateDbContextAsync(cancellationToken))
            await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken);

        var snapshot = Path.Combine(Path.GetTempPath(), $"daydrive-snapshot-{Guid.NewGuid():N}.db");
        try
        {
            File.Copy(_databasePath, snapshot, overwrite: true);
            await using var source = File.OpenRead(snapshot);
            await using var target = File.Create(destination);
            await using var gzip = new GZipStream(target, CompressionLevel.Optimal);
            await source.CopyToAsync(gzip, cancellationToken);
        }
        finally
        {
            TryDelete(snapshot);
        }
    }

    private async Task<(string Fingerprint, DateTimeOffset Newest)> LocalStateAsync(CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            return ReadLocalState((SqliteConnection)db.Database.GetDbConnection());
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// The shape of the synced data: for every table its row count, its highest row id and its
    /// newest stamp. An added row moves the id, an edited one the stamp, a deleted one the count.
    /// Read with plain SQL so the same answer comes out of the live database and out of a
    /// staged copy that no context has opened.
    /// </summary>
    private static (string Fingerprint, DateTimeOffset Newest) ReadLocalState(SqliteConnection connection)
    {
        var shape = new StringBuilder();
        long newest = 0;
        foreach (var table in SyncedTables)
        {
            var columns = Columns(connection, table);
            if (columns.Count == 0) continue;
            var stamp = columns.Contains("UpdatedAtUtc") ? "UpdatedAtUtc"
                : columns.Contains("CreatedAtUtc") ? "CreatedAtUtc"
                : null;

            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*), IFNULL(MAX(rowid), 0){(stamp is null ? "" : $", MAX(\"{stamp}\")")} FROM \"{table}\"";
            using var reader = command.ExecuteReader();
            reader.Read();
            var tableNewest = stamp is not null && !reader.IsDBNull(2) && reader.GetValue(2) is long ticks ? ticks : 0;
            shape.Append(table).Append(':').Append(reader.GetInt64(0)).Append(':').Append(reader.GetInt64(1))
                .Append(':').Append(tableNewest).Append('|');
            newest = Math.Max(newest, tableNewest);
        }

        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(shape.ToString())))[..24];
        var newestStamp = newest > 0 && newest <= DateTimeOffset.MaxValue.UtcTicks
            ? new DateTimeOffset(newest, TimeSpan.Zero)
            : DateTimeOffset.MinValue;
        return (fingerprint, newestStamp);
    }

    private static HashSet<string> Columns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\")";
        using var reader = command.ExecuteReader();
        var columns = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) columns.Add(reader.GetString(1));
        return columns;
    }

    /// <summary>Every SQLite file starts with the same 16-byte header string.</summary>
    private static bool LooksLikeSqlite(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var header = new byte[16];
            if (stream.Read(header, 0, header.Length) < header.Length) return false;
            return Encoding.ASCII.GetString(header).StartsWith("SQLite format 3", StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- restore

    /// <summary>
    /// Swaps a staged download into place. Called from startup, before any connection is opened.
    ///
    /// Three things happen to the copy before it replaces anything: it is brought up to this
    /// release's schema, this machine's settings row is written into it, and its fingerprint is
    /// recorded. The settings are the point. They used to arrive with the download, so a machine
    /// that restored woke up with another computer's Google sign-in — encrypted with a key it
    /// does not have — and another computer's idea of the last exchange, which made its very
    /// next sync report a conflict with the copy it had just taken.
    ///
    /// The replaced database is kept as a dated backup so a bad restore is never a dead end.
    /// </summary>
    public static string? ApplyPendingRestore(string databasePath)
    {
        var staged = databasePath + PendingRestoreSuffix;
        if (!File.Exists(staged)) return null;
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath))!;
            Dictionary<string, object?>? machineSettings = null;
            if (File.Exists(databasePath))
            {
                var backups = Path.Combine(directory, "Backups");
                Directory.CreateDirectory(backups);
                BackUp(databasePath, Path.Combine(backups, $"before-cloud-restore-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.db"));
                machineSettings = ReadSettingsRow(databasePath);
            }

            // The copy came from another machine, possibly another release. Bringing it to this
            // release's schema first means the settings below land in columns that exist.
            MigrateInPlace(staged);
            if (machineSettings is not null) WriteSettingsRow(staged, machineSettings);
            StampFingerprint(staged);
            SqliteConnection.ClearAllPools();

            // Side files describe the database being replaced and must not survive it.
            foreach (var sidecar in new[] { databasePath + "-wal", databasePath + "-shm" })
                if (File.Exists(sidecar)) File.Delete(sidecar);

            File.Copy(staged, databasePath, overwrite: true);
            File.Delete(staged);
            foreach (var sidecar in new[] { staged + "-wal", staged + "-shm" }) TryDelete(sidecar);

            AppendToJournal(databasePath, new CloudSyncLogEntry(DateTimeOffset.UtcNow, CloudSyncOutcome.Applied,
                CloudSyncIssue.None, CloudSyncTrigger.Startup, new FileInfo(databasePath).Length, string.Empty, string.Empty));
            return databasePath;
        }
        catch (Exception exception)
        {
            // A failed swap leaves the working database untouched, which is the safe outcome. The
            // copy is set aside so every later launch does not fail the same way, and the
            // revision is forgotten so the next sync sees the cloud copy as new again rather than
            // as something this machine already has.
            SqliteConnection.ClearAllPools();
            SetAside(staged);
            ForgetRevision(databasePath);
            AppendToJournal(databasePath, new CloudSyncLogEntry(DateTimeOffset.UtcNow, CloudSyncOutcome.Failed,
                CloudSyncIssue.Unexpected, CloudSyncTrigger.Startup, 0, string.Empty,
                $"The downloaded copy could not be applied: {exception.Message}"));
            return null;
        }
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    /// <summary>Through SQLite rather than a file copy, so whatever is still in the WAL comes along.</summary>
    private static void BackUp(string source, string destination)
    {
        using var from = Open(source);
        using var to = Open(destination);
        from.BackupDatabase(to);
    }

    private static Dictionary<string, object?> ReadSettingsRow(string path)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM \"AppSettings\" LIMIT 1";
        using var reader = command.ExecuteReader();
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (!reader.Read()) return values;
        for (var index = 0; index < reader.FieldCount; index++)
            values[reader.GetName(index)] = reader.IsDBNull(index) ? null : reader.GetValue(index);
        return values;
    }

    private static void MigrateInPlace(string path)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString())
            .Options;
        using var db = new AppDbContext(options);
        db.Database.Migrate();
    }

    /// <summary>
    /// Overwrites the copy's settings row with this machine's, column by column where both have
    /// the column. The active conversation stays the copy's: it names a conversation that exists
    /// in the copy, and this machine's may not.
    /// </summary>
    private static void WriteSettingsRow(string path, Dictionary<string, object?> values)
    {
        if (values.Count == 0) return;
        using var connection = Open(path);
        var columns = Columns(connection, "AppSettings");

        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM \"AppSettings\"";
        var isEmpty = Convert.ToInt64(count.ExecuteScalar()) == 0;

        var carried = values.Keys
            .Where(column => columns.Contains(column) &&
                             (isEmpty || column is not ("Id" or "ActiveConversationId")))
            .ToList();
        if (carried.Count == 0) return;

        using var command = connection.CreateCommand();
        command.CommandText = isEmpty
            ? $"INSERT INTO \"AppSettings\" ({string.Join(", ", carried.Select(column => $"\"{column}\""))}) " +
              $"VALUES ({string.Join(", ", carried.Select((_, index) => $"@p{index}"))})"
            : $"UPDATE \"AppSettings\" SET {string.Join(", ", carried.Select((column, index) => $"\"{column}\" = @p{index}"))}";
        for (var index = 0; index < carried.Count; index++)
            command.Parameters.AddWithValue($"@p{index}", values[carried[index]] ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static void StampFingerprint(string path)
    {
        using var connection = Open(path);
        var (fingerprint, _) = ReadLocalState(connection);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE \"AppSettings\" SET \"CloudSyncLocalFingerprint\" = @fingerprint";
        command.Parameters.AddWithValue("@fingerprint", fingerprint);
        command.ExecuteNonQuery();
    }

    private static void ForgetRevision(string databasePath)
    {
        try
        {
            if (!File.Exists(databasePath)) return;
            using var connection = Open(databasePath);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE \"AppSettings\" SET \"CloudSyncLastRevision\" = ''";
            command.ExecuteNonQuery();
        }
        catch (Exception)
        {
            // Best effort: the journal still says the restore failed.
        }
    }

    private static void SetAside(string staged)
    {
        try { File.Move(staged, staged + ".failed", overwrite: true); }
        catch (Exception) { TryDelete(staged); }
    }

    // ---------------------------------------------------------------- journal

    public static string JournalPath(string databasePath)
        => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath))!, JournalFileName);

    private CloudSyncReport Publish(CloudSyncReport report, bool journal = true, string? subject = null)
    {
        if (journal)
            Record(new CloudSyncLogEntry(
                DateTimeOffset.UtcNow,
                report.Outcome,
                report.Issue,
                report.Trigger,
                report.Outcome is CloudSyncOutcome.Uploaded or CloudSyncOutcome.Downloaded ? report.Snapshot?.SizeBytes ?? 0 : 0,
                subject ?? report.Snapshot?.DeviceName ?? string.Empty,
                report.IsFailure ? report.Message : string.Empty));
        Reported?.Invoke(this, report);
        return report;
    }

    private void Record(CloudSyncLogEntry entry)
    {
        List<CloudSyncLogEntry> snapshot;
        lock (_journalGate)
        {
            Append(_journal, entry);
            snapshot = _journal.ToList();
        }
        WriteJournal(_journalPath, snapshot);
    }

    /// <summary>
    /// Adds a line, folding a run of "nothing to do" — or of the same failure — into its latest
    /// one. A check every fifteen minutes would otherwise bury the lines that say something under
    /// ninety-six a day that repeat the one before.
    /// </summary>
    private static void Append(List<CloudSyncLogEntry> journal, CloudSyncLogEntry entry)
    {
        if (journal.Count > 0 && Repeats(journal[^1], entry)) journal.RemoveAt(journal.Count - 1);
        journal.Add(entry);
        if (journal.Count > JournalLength) journal.RemoveRange(0, journal.Count - JournalLength);
    }

    private static bool Repeats(CloudSyncLogEntry previous, CloudSyncLogEntry next)
        => previous.Outcome == next.Outcome &&
           (next.Outcome == CloudSyncOutcome.UpToDate || (next.IsFailure && previous.Issue == next.Issue));

    private static void AppendToJournal(string databasePath, CloudSyncLogEntry entry)
    {
        var path = JournalPath(databasePath);
        var journal = ReadJournal(path);
        Append(journal, entry);
        WriteJournal(path, journal);
    }

    private static List<CloudSyncLogEntry> ReadJournal(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<List<CloudSyncLogEntry>>(File.ReadAllText(path), JournalJson) ?? []
                : [];
        }
        catch (Exception)
        {
            // A damaged journal is history lost, not a reason to stop syncing.
            return [];
        }
    }

    private static void WriteJournal(string path, IReadOnlyList<CloudSyncLogEntry> journal)
    {
        try
        {
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(journal, JournalJson));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The in-memory journal still answers until the next write succeeds.
        }
    }

    // ---------------------------------------------------------------- helpers

    private static string DeviceName(AppSettings settings)
        => string.IsNullOrWhiteSpace(settings.CloudSyncDeviceName)
            ? Environment.MachineName
            : settings.CloudSyncDeviceName.Trim();

    private static string Describe(CloudSnapshot snapshot)
    {
        var when = snapshot.ModifiedUtc.ToLocalTime().ToString("g");
        return string.IsNullOrWhiteSpace(snapshot.DeviceName) ? when : $"{snapshot.DeviceName}, {when}";
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024d * 1024d):0.0} MB",
        >= 1024 => $"{bytes / 1024d:0} KB",
        _ => $"{bytes} B"
    };

    public void Dispose()
    {
        _google.Dispose();
        _gate.Dispose();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* Temporary files are reclaimed by the OS. */ }
        catch (UnauthorizedAccessException) { }
    }
}
