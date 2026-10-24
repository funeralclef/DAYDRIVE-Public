using IntelligentScheduler.Domain;

namespace IntelligentScheduler.Application;

/// <summary>What a stored snapshot says about itself. Revision is whatever the backend uses to tell one upload from the next.</summary>
public sealed record CloudSnapshot(
    string Revision,
    DateTimeOffset ModifiedUtc,
    long SizeBytes,
    string DeviceName,
    string AppVersion);

/// <summary>
/// Why a sync could not happen, as something the settings page can put into words.
///
/// The backends used to hand back English sentences that went straight onto the screen, which
/// is how a Russian-speaking user ended up reading "Connect the Google account to finish setup"
/// with no idea which of four different failures it stood for. A code lets the shell say it in
/// the reader's own language and name the one thing to do about it.
/// </summary>
public enum CloudSyncIssue
{
    None,
    /// <summary>No destination has been chosen.</summary>
    Off,
    FolderNotChosen,
    FolderMissing,
    FolderNotWritable,
    GoogleClientIdMissing,
    /// <summary>Google requires the secret of a Desktop client in the code exchange.</summary>
    GoogleClientSecretMissing,
    /// <summary>Configured, but nobody has signed in yet.</summary>
    GoogleNotConnected,
    /// <summary>The stored sign-in was revoked, expired or cannot be read on this machine.</summary>
    GoogleReconnect,
    GoogleClientRejected,
    GoogleDriveApiDisabled,
    /// <summary>The consent screen's Drive box was left unticked.</summary>
    GoogleScopeMissing,
    SignInCancelled,
    SignInTimedOut,
    Offline,
    /// <summary>Stopped before it finished — most often the time allowed on the way out ran out.</summary>
    Interrupted,
    Busy,
    NothingInCloud,
    NotADatabase,
    Unexpected
}

/// <summary>Whether a backend can be used right now, and why not when it cannot.</summary>
public sealed record CloudReadiness(bool IsReady, string Message, CloudSyncIssue Issue = CloudSyncIssue.None)
{
    public static CloudReadiness Ready(string message = "") => new(true, message);
    public static CloudReadiness NotReady(CloudSyncIssue issue, string message) => new(false, message, issue);
}

/// <summary>A failure a backend can name. Anything else is reported as <see cref="CloudSyncIssue.Unexpected"/>.</summary>
public sealed class CloudSyncException(CloudSyncIssue issue, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public CloudSyncIssue Issue { get; } = issue;
}

public enum CloudSyncDirection
{
    /// <summary>Work out from the stamps which way the data should move.</summary>
    Auto,
    /// <summary>Overwrite the cloud copy with this machine's data.</summary>
    Upload,
    /// <summary>Overwrite this machine's data with the cloud copy.</summary>
    Download
}

/// <summary>What set a sync going. Only the journal cares: the rules are the same whoever asks.</summary>
public enum CloudSyncTrigger
{
    Manual,
    /// <summary>The periodic check.</summary>
    Automatic,
    /// <summary>The pull shortly after launch.</summary>
    Startup,
    /// <summary>The push on the way out.</summary>
    Exit
}

/// <summary>Result of one sync attempt, in the terms the settings panel reports.</summary>
public sealed record CloudSyncReport(
    CloudSyncOutcome Outcome,
    string Message,
    CloudSnapshot? Snapshot = null,
    /// <summary>True when the downloaded data only takes effect after the app restarts.</summary>
    bool RequiresRestart = false,
    CloudSyncIssue Issue = CloudSyncIssue.None,
    CloudSyncTrigger Trigger = CloudSyncTrigger.Manual)
{
    public bool IsFailure => Outcome is CloudSyncOutcome.Failed or CloudSyncOutcome.NotConfigured;
}

/// <summary>
/// One line of the sync journal. Kept on disk beside the database rather than in it: the
/// journal describes this machine's exchanges, and a database that travels to another machine
/// must not take this one's history with it.
/// </summary>
public sealed record CloudSyncLogEntry(
    DateTimeOffset AtUtc,
    CloudSyncOutcome Outcome,
    CloudSyncIssue Issue,
    CloudSyncTrigger Trigger,
    /// <summary>Size of the snapshot that moved, 0 when nothing did.</summary>
    long Bytes,
    /// <summary>The machine that uploaded the snapshot involved, or the account that was connected.</summary>
    string Subject,
    /// <summary>The backend's own words, for a failure nobody put a name to.</summary>
    string Detail)
{
    public bool IsFailure => Outcome is CloudSyncOutcome.Failed or CloudSyncOutcome.NotConfigured;

    /// <summary>Whether this line says the two copies agreed afterwards.</summary>
    public bool IsSuccess => Outcome is CloudSyncOutcome.UpToDate or CloudSyncOutcome.Uploaded
        or CloudSyncOutcome.Downloaded or CloudSyncOutcome.Applied;
}

/// <summary>
/// Everything the settings page says about sync that can be known without the network: what
/// is set up, whether this machine has something the cloud has not seen, and how the last
/// attempts went. Cheap enough to ask for every time the page is looked at.
/// </summary>
public sealed record CloudSyncStatus(
    CloudSyncProvider Provider,
    /// <summary>What is still missing from the setup, as far as can be told offline.</summary>
    CloudSyncIssue SetupIssue,
    /// <summary>True when this machine holds edits the cloud copy does not have.</summary>
    bool HasLocalChanges,
    /// <summary>The newest edit on this machine, or null for an empty profile.</summary>
    DateTimeOffset? LocalChangedUtc,
    /// <summary>When a sync last finished with both copies in agreement.</summary>
    CloudSyncLogEntry? LastSuccess,
    /// <summary>The most recent attempt of any kind.</summary>
    CloudSyncLogEntry? LastAttempt,
    bool RestartPending,
    bool IsBusy);

/// <summary>
/// A place a snapshot can live. Deliberately dumb: it moves bytes and reports what is there,
/// while every decision about which way data should travel stays in the sync service.
/// </summary>
public interface ICloudSyncBackend
{
    CloudSyncProvider Kind { get; }

    /// <summary>What is missing from the configuration, checked without touching the network.</summary>
    CloudSyncIssue CheckSetup(AppSettings settings);

    /// <summary>Checks configuration and reachability without transferring the payload.</summary>
    Task<CloudReadiness> CheckReadyAsync(AppSettings settings, CancellationToken cancellationToken = default);

    /// <summary>Metadata of the stored snapshot, or null when nothing has been uploaded yet.</summary>
    Task<CloudSnapshot?> GetSnapshotAsync(AppSettings settings, CancellationToken cancellationToken = default);

    /// <summary>Stores <paramref name="payload"/> and returns the snapshot as it now exists remotely.</summary>
    Task<CloudSnapshot> UploadAsync(AppSettings settings, Stream payload, CloudSnapshot metadata,
        CancellationToken cancellationToken = default);

    /// <summary>Opens the stored payload for reading. The caller disposes the stream.</summary>
    Task<Stream> DownloadAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

/// <summary>Orchestrates the exchange and owns the conflict rules.</summary>
public interface ICloudSyncService
{
    /// <summary>Fires whenever a transfer finishes or fails, on the calling thread.</summary>
    event EventHandler<CloudSyncReport>? Reported;

    bool IsBusy { get; }

    /// <summary>True once a download has been staged and the app has not restarted yet.</summary>
    bool HasPendingRestore { get; }

    /// <summary>This machine's recent exchanges, oldest first.</summary>
    IReadOnlyList<CloudSyncLogEntry> History { get; }

    /// <summary>Reads what the configured backend currently holds, without changing anything.</summary>
    Task<CloudSnapshot?> PeekAsync(CancellationToken cancellationToken = default);

    Task<CloudReadiness> CheckReadyAsync(CancellationToken cancellationToken = default);

    /// <summary>The offline half of the picture; see <see cref="CloudSyncStatus"/>.</summary>
    Task<CloudSyncStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<CloudSyncReport> SyncAsync(CloudSyncDirection direction, CancellationToken cancellationToken = default);

    Task<CloudSyncReport> SyncAsync(CloudSyncDirection direction, CloudSyncTrigger trigger,
        CancellationToken cancellationToken = default);

    /// <summary>Runs the Google consent flow in the user's browser and stores the refresh token.</summary>
    Task<CloudSyncReport> AuthorizeGoogleAsync(CancellationToken cancellationToken = default);

    /// <summary>Forgets the stored Google tokens. The remote snapshot is left untouched.</summary>
    Task SignOutGoogleAsync(CancellationToken cancellationToken = default);
}
