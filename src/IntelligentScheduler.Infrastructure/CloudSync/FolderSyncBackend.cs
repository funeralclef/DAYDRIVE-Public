using System.Text.Json;
using IntelligentScheduler.Application;
using IntelligentScheduler.Domain;

namespace IntelligentScheduler.Infrastructure.CloudSync;

/// <summary>
/// Writes the snapshot into a folder the user already has mirrored by Google Drive for desktop,
/// OneDrive, Dropbox or anything else. No account, no API key, no token to expire — the desktop
/// client that owns the folder does the actual uploading.
///
/// The manifest is a separate small file so the revision can be read without touching the
/// payload, which matters while a cloud client is still uploading a large database.
/// </summary>
public sealed class FolderSyncBackend : ICloudSyncBackend
{
    private const string FolderName = "DAYDRIVE";
    private const string PayloadName = "daydrive-snapshot.db.gz";
    private const string ManifestName = "daydrive-snapshot.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public CloudSyncProvider Kind => CloudSyncProvider.Folder;

    public CloudSyncIssue CheckSetup(AppSettings settings)
    {
        var root = settings.CloudSyncFolderPath?.Trim();
        if (string.IsNullOrWhiteSpace(root)) return CloudSyncIssue.FolderNotChosen;
        return Directory.Exists(root) ? CloudSyncIssue.None : CloudSyncIssue.FolderMissing;
    }

    public Task<CloudReadiness> CheckReadyAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var root = settings.CloudSyncFolderPath?.Trim();
        switch (CheckSetup(settings))
        {
            case CloudSyncIssue.FolderNotChosen:
                return Task.FromResult(CloudReadiness.NotReady(CloudSyncIssue.FolderNotChosen,
                    "Choose the folder your cloud client keeps in sync."));
            case CloudSyncIssue.FolderMissing:
                return Task.FromResult(CloudReadiness.NotReady(CloudSyncIssue.FolderMissing,
                    $"The folder \"{root}\" no longer exists."));
        }
        try
        {
            // Writing is the only check that means anything: a mapped drive can exist and still refuse.
            var probe = Path.Combine(root!, $".daydrive-write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
        }
        catch (Exception exception)
        {
            return Task.FromResult(CloudReadiness.NotReady(CloudSyncIssue.FolderNotWritable,
                $"That folder cannot be written to: {exception.Message}"));
        }
        return Task.FromResult(CloudReadiness.Ready(Directory.Exists(SyncDirectory(root))
            ? "Folder ready."
            : "Folder ready. The first sync creates the DAYDRIVE subfolder."));
    }

    public async Task<CloudSnapshot?> GetSnapshotAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var directory = SyncDirectory(settings.CloudSyncFolderPath);
        var payload = Path.Combine(directory, PayloadName);
        var manifest = Path.Combine(directory, ManifestName);
        if (!File.Exists(payload)) return null;

        var info = new FileInfo(payload);
        if (File.Exists(manifest))
        {
            try
            {
                var stored = JsonSerializer.Deserialize<FolderManifest>(
                    await File.ReadAllTextAsync(manifest, cancellationToken), Json);
                if (stored is { Revision: not null and not "" })
                    return new CloudSnapshot(stored.Revision, stored.ModifiedUtc, info.Length,
                        stored.DeviceName ?? string.Empty, stored.AppVersion ?? string.Empty);
            }
            catch (JsonException)
            {
                // A half-written manifest is not fatal: the payload's own timestamp still identifies it.
            }
        }

        var fallback = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        return new CloudSnapshot(fallback.ToUnixTimeMilliseconds().ToString(), fallback, info.Length, string.Empty, string.Empty);
    }

    public async Task<CloudSnapshot> UploadAsync(AppSettings settings, Stream payload, CloudSnapshot metadata,
        CancellationToken cancellationToken = default)
    {
        var directory = SyncDirectory(settings.CloudSyncFolderPath);
        Directory.CreateDirectory(directory);

        // Write beside the target and move into place, so a cloud client never picks up a partial file.
        var target = Path.Combine(directory, PayloadName);
        var staging = Path.Combine(directory, $"{PayloadName}.{Guid.NewGuid():N}.part");
        try
        {
            await using (var file = File.Create(staging))
                await payload.CopyToAsync(file, cancellationToken);
            File.Move(staging, target, overwrite: true);
        }
        catch
        {
            if (File.Exists(staging)) TryDelete(staging);
            throw;
        }

        var revision = Guid.NewGuid().ToString("N");
        var stamp = DateTimeOffset.UtcNow;
        await File.WriteAllTextAsync(Path.Combine(directory, ManifestName),
            JsonSerializer.Serialize(new FolderManifest(revision, stamp, metadata.DeviceName, metadata.AppVersion), Json),
            cancellationToken);

        return new CloudSnapshot(revision, stamp, new FileInfo(target).Length, metadata.DeviceName, metadata.AppVersion);
    }

    public Task<Stream> DownloadAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var payload = Path.Combine(SyncDirectory(settings.CloudSyncFolderPath), PayloadName);
        if (!File.Exists(payload))
            throw new CloudSyncException(CloudSyncIssue.NothingInCloud, "No snapshot has been uploaded to this folder yet.");
        return Task.FromResult<Stream>(File.OpenRead(payload));
    }

    private static string SyncDirectory(string? root)
        => Path.Combine((root ?? string.Empty).Trim(), FolderName);

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { /* Leftover staging files are harmless and cleaned up by the next upload. */ }
    }

    private sealed record FolderManifest(string Revision, DateTimeOffset ModifiedUtc, string? DeviceName, string? AppVersion);
}
