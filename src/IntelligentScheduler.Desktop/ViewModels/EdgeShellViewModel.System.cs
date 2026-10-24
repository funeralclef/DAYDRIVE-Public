using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IntelligentScheduler.Application;
using IntelligentScheduler.Desktop.Platform;
using IntelligentScheduler.Domain;

namespace IntelligentScheduler.Desktop.ViewModels;

/// <summary>
/// The parts of settings that are about the application rather than the schedule: what version
/// is running, whether a newer one exists, and where a copy of the data lives.
///
/// The release check is deliberately undemanding. It runs once a day in the background, says
/// nothing at all when there is nothing to say, and a version the user dismisses is never
/// mentioned again. Nothing here ever interrupts what the user is doing.
/// </summary>
public sealed partial class EdgeShellViewModel
{
    // ---------------------------------------------------------------- about

    public string AppName => AppInfo.Name;
    public string AppVersionText => localization.Format("AboutVersionFormat", AppInfo.Version);
    public string AppAuthor => AppInfo.Author;
    public string AppRepositoryUrl => AppInfo.RepositoryUrl;
    public string AppAuthorProfileUrl => AppInfo.AuthorProfileUrl;

    [ObservableProperty] private string _dataDirectoryPath = string.Empty;

    [RelayCommand] private void OpenRepository() => OpenLink(AppInfo.RepositoryUrl);
    [RelayCommand] private void OpenAuthorProfile() => OpenLink(AppInfo.AuthorProfileUrl);
    [RelayCommand] private void OpenIssues() => OpenLink(AppInfo.IssuesUrl);
    [RelayCommand] private void OpenReleases() => OpenLink(AppInfo.ReleasesUrl);
    [RelayCommand] private void OpenGoogleConsole() => OpenLink(Infrastructure.CloudSync.GoogleDriveSyncBackend.ConsoleUrl);

    [RelayCommand]
    private void OpenDataFolder()
    {
        if (!AppInfo.TryOpenFolder(DataDirectoryPath)) Status = localization.Text("CouldNotOpenFolder");
    }

    private void OpenLink(string url)
    {
        if (!AppInfo.TryOpenUrl(url)) Status = localization.Format("CouldNotOpenLink", url);
    }

    // ---------------------------------------------------------------- reminders

    /// <summary>
    /// Lengths offered for a quiet spell, in minutes. Zero is the one that has no length: it
    /// lasts until it is switched off by hand, which is what somebody sitting down to
    /// concentrate for an unknown while actually wants.
    /// </summary>
    public static IReadOnlyList<int> DoNotDisturbLengths { get; } = [30, 60, 120, 480, UntilResumed];

    /// <summary>The length that is not one: quiet until the user says otherwise.</summary>
    public const int UntilResumed = 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotificationsSummary))]
    private bool _notificationsEnabled = true;

    [ObservableProperty] private bool _notificationSoundEnabled = true;

    /// <summary>
    /// The moment the current quiet spell lapses. Null means reminders are arriving, unless
    /// <see cref="DoNotDisturbUntilResumed"/> is set. It is a moment rather than a switch so a
    /// quiet hour survives a restart and lifts itself.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDoNotDisturb))]
    [NotifyPropertyChangedFor(nameof(DoNotDisturbSummary))]
    [NotifyPropertyChangedFor(nameof(NotificationsSummary))]
    private DateTimeOffset? _doNotDisturbUntilUtc;

    /// <summary>Quiet with no end in sight: it lasts until it is switched off by hand.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDoNotDisturb))]
    [NotifyPropertyChangedFor(nameof(DoNotDisturbSummary))]
    [NotifyPropertyChangedFor(nameof(NotificationsSummary))]
    private bool _doNotDisturbUntilResumed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DoNotDisturbLengthText))]
    private int _doNotDisturbMinutes = 60;

    /// <summary>The lengths on offer, as the list the "for how long" control drops down.</summary>
    public ObservableCollection<QuietLengthOptionViewModel> DoNotDisturbLengthOptions { get; } = [];

    public bool IsDoNotDisturb
        => DoNotDisturbUntilResumed || (DoNotDisturbUntilUtc is { } until && DateTimeOffset.UtcNow < until);

    public string DoNotDisturbLengthText => FormatQuietLength(DoNotDisturbMinutes);

    /// <summary>"Quiet until 14:30", "Quiet until you turn it back on", or the invitation.</summary>
    public string DoNotDisturbSummary => DoNotDisturbUntilResumed
        ? localization.Text("DoNotDisturbUntilResumedHint")
        : IsDoNotDisturb && DoNotDisturbUntilUtc is { } until
            ? localization.Format("DoNotDisturbUntil", FormatClockTime(until.ToLocalTime().TimeOfDay))
            : localization.Text("DoNotDisturbOffHint");

    public string NotificationsSummary => !NotificationsEnabled
        ? localization.Text("NotificationsOffHint")
        : DoNotDisturbSummary;

    /// <summary>A plain-language length: "30 min", "1 h", "8 h", or the one with no length.</summary>
    public string FormatQuietLength(int minutes)
        => minutes <= UntilResumed
            ? localization.Text("QuietLengthUntilResumed")
            : minutes >= 60 && minutes % 60 == 0
                ? localization.Format("QuietLengthHours", minutes / 60)
                : localization.Format("QuietLengthMinutes", minutes);

    [RelayCommand]
    private void StartDoNotDisturb(string? minutes)
    {
        var length = int.TryParse(minutes, out var parsed) && parsed >= 0 ? parsed : DoNotDisturbMinutes;
        DoNotDisturbMinutes = length;
        DoNotDisturbUntilResumed = length <= UntilResumed;
        DoNotDisturbUntilUtc = DoNotDisturbUntilResumed ? null : DateTimeOffset.UtcNow.AddMinutes(length);
        PublishNotificationPreferences();
        ScheduleSettingsSave();
    }

    [RelayCommand]
    private void EndDoNotDisturb()
    {
        DoNotDisturbUntilResumed = false;
        DoNotDisturbUntilUtc = null;
        PublishNotificationPreferences();
        ScheduleSettingsSave();
    }

    [RelayCommand]
    private void ToggleDoNotDisturb()
    {
        if (IsDoNotDisturb) EndDoNotDisturb();
        else StartDoNotDisturb(DoNotDisturbMinutes.ToString());
    }

    [RelayCommand]
    private void ToggleNotificationSound() => NotificationSoundEnabled = !NotificationSoundEnabled;

    partial void OnNotificationsEnabledChanged(bool value)
    {
        PublishNotificationPreferences();
        ScheduleSettingsSave();
    }

    partial void OnNotificationSoundEnabledChanged(bool value)
    {
        PublishNotificationPreferences();
        ScheduleSettingsSave();
    }

    partial void OnDoNotDisturbMinutesChanged(int value)
    {
        foreach (var option in DoNotDisturbLengthOptions) option.IsSelected = option.Minutes == value;
        // Changing the length while it is already quiet re-times the current spell rather than
        // waiting for the next one: the answer to "for how long" is about now.
        if (IsDoNotDisturb)
        {
            DoNotDisturbUntilResumed = value <= UntilResumed;
            DoNotDisturbUntilUtc = DoNotDisturbUntilResumed ? null : DateTimeOffset.UtcNow.AddMinutes(value);
        }
        OnPropertyChanged(nameof(SelectedDoNotDisturbLengthOption));
        PublishNotificationPreferences();
        ScheduleSettingsSave();
    }

    /// <summary>
    /// The length currently chosen, as the item a drop-down shows. Settings offered these as a
    /// row of four buttons, which grew a fifth that is a sentence rather than a number and no
    /// longer fitted; a list that drops down holds any of them at the same width.
    /// </summary>
    public QuietLengthOptionViewModel? SelectedDoNotDisturbLengthOption
    {
        get => DoNotDisturbLengthOptions.FirstOrDefault(option => option.Minutes == DoNotDisturbMinutes);
        set { if (value is not null) DoNotDisturbMinutes = value.Minutes; }
    }

    /// <summary>Rebuilt when the language changes, like every other worded list in the shell.</summary>
    private void BuildQuietLengthOptions()
    {
        DoNotDisturbLengthOptions.Clear();
        foreach (var minutes in DoNotDisturbLengths)
            DoNotDisturbLengthOptions.Add(new QuietLengthOptionViewModel(minutes, FormatQuietLength(minutes),
                length => DoNotDisturbMinutes = length) { IsSelected = minutes == DoNotDisturbMinutes });
        OnPropertyChanged(nameof(SelectedDoNotDisturbLengthOption));
    }

    /// <summary>
    /// Hands the three answers to whatever is going to show the next reminder. Pushed rather
    /// than polled: a reminder is decided on a timer tick, and a quiet hour must not cost a
    /// database read every thirty seconds.
    /// </summary>
    public void PublishNotificationPreferences()
    {
        NotificationPreferencesChanged?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(nameof(IsDoNotDisturb));
        OnPropertyChanged(nameof(DoNotDisturbSummary));
        OnPropertyChanged(nameof(NotificationsSummary));
    }

    /// <summary>Raised when the shell should re-read the reminder preferences and the tray menu.</summary>
    public event EventHandler? NotificationPreferencesChanged;

    /// <summary>
    /// Drops a quiet spell that has run out. Called from the clock the shell already ticks, so
    /// the settings page and the tray menu stop claiming to be quiet the minute it ends.
    /// </summary>
    public void ExpireDoNotDisturb()
    {
        // A spell with no end never runs out; only a timed one does.
        if (DoNotDisturbUntilResumed) return;
        if (DoNotDisturbUntilUtc is not { } until || DateTimeOffset.UtcNow < until) return;
        DoNotDisturbUntilUtc = null;
        PublishNotificationPreferences();
        ScheduleSettingsSave();
    }

    // ---------------------------------------------------------------- updates

    [ObservableProperty] private bool _updateCheckEnabled = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate))]
    private string _availableUpdateVersion = string.Empty;

    [ObservableProperty] private string _availableUpdateName = string.Empty;
    [ObservableProperty] private string _availableUpdateNotes = string.Empty;
    [ObservableProperty] private string _updateStatusText = string.Empty;
    [ObservableProperty] private bool _isCheckingForUpdate;

    /// <summary>Drives both the settings banner and the quiet dot on the settings petal.</summary>
    public bool HasUpdate => AvailableUpdateVersion.Length > 0;

    private string _availableUpdateUrl = AppInfo.ReleasesUrl;

    partial void OnUpdateCheckEnabledChanged(bool value)
    {
        if (_loadingSettings) return;
        ScheduleSettingsSave();
        if (value) _ = CheckForUpdatesAsync(force: false);
        else ClearUpdate();
    }

    /// <summary>Runs shortly after startup. A failure here is never shown to the user.</summary>
    public async Task CheckForUpdatesQuietlyAsync()
    {
        try
        {
            var result = await updateService.CheckAsync(force: false);
            ApplyUpdateResult(result, announceFailure: false);
            await ReloadServiceOwnedSettingsAsync(applyToView: false);
        }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Background release check");
        }
    }

    [RelayCommand]
    private Task CheckForUpdatesNowAsync() => CheckForUpdatesAsync(force: true);

    private async Task CheckForUpdatesAsync(bool force)
    {
        if (IsCheckingForUpdate) return;
        IsCheckingForUpdate = true;
        UpdateStatusText = localization.Text("UpdateChecking");
        try
        {
            ApplyUpdateResult(await updateService.CheckAsync(force), announceFailure: true);
            await ReloadServiceOwnedSettingsAsync(applyToView: false);
        }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Release check");
            UpdateStatusText = exception.Message;
        }
        finally { IsCheckingForUpdate = false; }
    }

    private void ApplyUpdateResult(UpdateCheckResult result, bool announceFailure)
    {
        if (result.IsError)
        {
            if (announceFailure) UpdateStatusText = result.Message;
            return;
        }

        if (result.Release is { } release && result.IsUpdateAvailable)
        {
            AvailableUpdateVersion = release.Version;
            AvailableUpdateName = release.Name;
            AvailableUpdateNotes = Summarize(release.Notes);
            _availableUpdateUrl = string.IsNullOrWhiteSpace(release.HtmlUrl) ? AppInfo.ReleasesUrl : release.HtmlUrl;
            UpdateStatusText = localization.Format("UpdateAvailableFormat", release.Version);
            return;
        }

        ClearUpdate();
        if (!string.IsNullOrWhiteSpace(result.Message)) UpdateStatusText = localization.Text("UpdateUpToDate");
    }

    private void ClearUpdate()
    {
        AvailableUpdateVersion = string.Empty;
        AvailableUpdateName = string.Empty;
        AvailableUpdateNotes = string.Empty;
        _availableUpdateUrl = AppInfo.ReleasesUrl;
    }

    /// <summary>Release notes are Markdown; the banner shows the first few plain lines of it.</summary>
    private static string Summarize(string notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return string.Empty;
        var lines = notes.Replace("\r\n", "\n").Split('\n')
            .Select(line => line.Trim().TrimStart('#', '*', '-', ' ').Trim())
            .Where(line => line.Length > 0)
            .Take(4)
            .ToList();
        var summary = string.Join("\n", lines);
        return summary.Length > 420 ? summary[..420].TrimEnd() + "…" : summary;
    }

    [RelayCommand] private void OpenUpdatePage() => OpenLink(_availableUpdateUrl);

    [RelayCommand]
    private async Task SkipUpdateAsync()
    {
        var version = AvailableUpdateVersion;
        if (version.Length == 0) return;
        try
        {
            await updateService.SkipAsync(version);
            await ReloadServiceOwnedSettingsAsync(applyToView: false);
            ClearUpdate();
            UpdateStatusText = localization.Format("UpdateSkippedFormat", version);
        }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Skipping a release");
            UpdateStatusText = exception.Message;
        }
    }

    // ---------------------------------------------------------------- cloud sync

    public ObservableCollection<ChoiceOption> CloudProviderOptions { get; } = [];

    /// <summary>The latest lines of the sync journal, newest first.</summary>
    public ObservableCollection<CloudJournalItemViewModel> CloudJournal { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCloudSyncOff))]
    [NotifyPropertyChangedFor(nameof(IsFolderSync))]
    [NotifyPropertyChangedFor(nameof(IsGoogleDriveSync))]
    private string _cloudSyncProvider = nameof(Domain.CloudSyncProvider.None);

    [ObservableProperty] private ChoiceOption? _selectedCloudProviderOption;
    [ObservableProperty] private string _cloudSyncFolderPath = string.Empty;
    [ObservableProperty] private bool _cloudSyncAutomatic = true;
    [ObservableProperty] private string _cloudSyncDeviceName = string.Empty;
    [ObservableProperty] private string _googleDriveClientId = string.Empty;
    [ObservableProperty] private string _googleDriveClientSecret = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GoogleAccountText))]
    private string _googleDriveAccount = string.Empty;

    /// <summary>
    /// Whether a Google sign-in is actually stored. "Connected" used to be read off the account's
    /// email address, which a sign-in does not always carry and which says nothing about
    /// whether the sign-in behind it can still be used.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGoogleConnected))]
    private bool _hasGoogleSignIn;

    /// <summary>The secret field empties itself once saved; this is what says it was kept.</summary>
    [ObservableProperty] private bool _hasGoogleClientSecret;
    [ObservableProperty] private bool _isGoogleConnecting;

    /// <summary>Progress and outcome of connecting the Google account, shown under its buttons.</summary>
    [ObservableProperty] private string _cloudSyncStatusText = string.Empty;

    [ObservableProperty] private bool _isCloudSyncBusy;
    [ObservableProperty] private bool _isCloudRestartPending;
    [ObservableProperty] private bool _isCloudConflict;

    // The card at the top of the page: one sentence of what is going on, one of what to do.
    [ObservableProperty] private string _cloudStatusTitle = string.Empty;
    [ObservableProperty] private string _cloudStatusDetail = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCloudStatusOk))]
    [NotifyPropertyChangedFor(nameof(IsCloudStatusPending))]
    [NotifyPropertyChangedFor(nameof(IsCloudStatusBusy))]
    [NotifyPropertyChangedFor(nameof(IsCloudStatusError))]
    [NotifyPropertyChangedFor(nameof(IsCloudStatusAttention))]
    private string _cloudStatusKind = CloudStatusKinds.Ok;

    [ObservableProperty] private string _cloudLastSyncText = string.Empty;
    [ObservableProperty] private string _cloudRemoteText = string.Empty;
    [ObservableProperty] private string _cloudLocalText = string.Empty;
    [ObservableProperty] private string _cloudScheduleText = string.Empty;
    [ObservableProperty] private bool _hasCloudJournal;

    public bool IsCloudSyncOff => CloudSyncProvider == nameof(Domain.CloudSyncProvider.None);
    public bool IsFolderSync => CloudSyncProvider == nameof(Domain.CloudSyncProvider.Folder);
    public bool IsGoogleDriveSync => CloudSyncProvider == nameof(Domain.CloudSyncProvider.GoogleDrive);
    public bool IsGoogleConnected => HasGoogleSignIn;
    public string GoogleAccountText => GoogleDriveAccount.Length > 0
        ? GoogleDriveAccount
        : localization.Text("CloudGoogleConnectedNoEmail");

    public bool IsCloudStatusOk => CloudStatusKind == CloudStatusKinds.Ok;
    public bool IsCloudStatusPending => CloudStatusKind == CloudStatusKinds.Pending;
    public bool IsCloudStatusBusy => CloudStatusKind == CloudStatusKinds.Busy;
    public bool IsCloudStatusError => CloudStatusKind == CloudStatusKinds.Error;
    public bool IsCloudStatusAttention => CloudStatusKind == CloudStatusKinds.Attention;

    /// <summary>The last offline reading of where sync stands; see <see cref="RefreshCloudStatusAsync"/>.</summary>
    private CloudSyncStatus? _cloudStatus;
    private CloudSnapshot? _cloudRemote;
    private bool _cloudRemoteKnown;
    /// <summary>Why the cloud copy could not be looked at, when the last look failed.</summary>
    private string? _cloudRemoteProblem;
    private DateTimeOffset _nextAutomaticSyncUtc = DateTimeOffset.MaxValue;
    private DateTimeOffset _lastCloudStatusRefreshUtc = DateTimeOffset.MinValue;
    private (int Count, DateTimeOffset Last, DateOnly Day, string Language) _cloudJournalShape;
    /// <summary>An automatic sync that fails says so once in the status line, not every quarter hour.</summary>
    private bool _announcedCloudFailure;

    /// <summary>Raised so the settings window can open a folder picker on the view model's behalf.</summary>
    public event EventHandler? CloudFolderPickRequested;

    /// <summary>Raised when the shell should close and start again, so a downloaded copy takes effect.</summary>
    public event EventHandler? RestartRequested;

    public string CloudFolderDialogTitle => localization.Text("CloudFolderDialogTitle");

    partial void OnSelectedCloudProviderOptionChanged(ChoiceOption? value)
    {
        // The list is rebuilt with every language change; the same destination under a new
        // name is not a new choice and must not throw away the conflict the page is showing.
        if (value is null || value.Value == CloudSyncProvider) return;
        CloudSyncProvider = value.Value;
        if (_loadingSettings) return;
        IsCloudConflict = false;
        CloudSyncStatusText = string.Empty;
        ForgetCloudRemote();
        ScheduleSettingsSave();
    }

    partial void OnCloudSyncFolderPathChanged(string value)
    {
        ForgetCloudRemote();
        ScheduleSettingsSave();
    }

    partial void OnCloudSyncAutomaticChanged(bool value)
    {
        ScheduleSettingsSave();
        RenderCloudStatus();
    }

    partial void OnCloudSyncDeviceNameChanged(string value) => ScheduleSettingsSave();
    partial void OnGoogleDriveClientIdChanged(string value) => ScheduleSettingsSave();

    partial void OnSettingsSectionChanged(string value)
    {
        if (value == "data" && IsSettings) _ = RefreshCloudStatusAsync();
    }

    [RelayCommand] private void PickCloudFolder() => CloudFolderPickRequested?.Invoke(this, EventArgs.Empty);

    public void SetCloudFolder(string path)
    {
        CloudSyncProvider = nameof(Domain.CloudSyncProvider.Folder);
        SelectedCloudProviderOption = CloudProviderOptions.FirstOrDefault(option => option.Value == CloudSyncProvider);
        CloudSyncFolderPath = path;
    }

    [RelayCommand] private Task SyncNowAsync() => RunCloudSyncAsync(CloudSyncDirection.Auto, CloudSyncTrigger.Manual);
    [RelayCommand] private Task SyncUploadAsync() => RunCloudSyncAsync(CloudSyncDirection.Upload, CloudSyncTrigger.Manual);
    [RelayCommand] private Task SyncDownloadAsync() => RunCloudSyncAsync(CloudSyncDirection.Download, CloudSyncTrigger.Manual);

    [RelayCommand] private void RestartToApplyCloudCopy() => RestartRequested?.Invoke(this, EventArgs.Empty);

    public void ReportRestartFailed() => Status = localization.Text("CloudRestartFailed");

    private async Task RunCloudSyncAsync(CloudSyncDirection direction, CloudSyncTrigger trigger)
    {
        if (IsCloudSyncBusy) return;
        IsCloudSyncBusy = true;
        RenderCloudStatus();
        try
        {
            // The settings the backend reads are the saved ones, not what is typed on screen.
            await FlushSettingsAsync();
            var report = await cloudSync.SyncAsync(direction, trigger);
            if (report.Issue == CloudSyncIssue.Busy) return;

            IsCloudConflict = report.Outcome == CloudSyncOutcome.Conflict;
            IsCloudRestartPending = report.RequiresRestart || cloudSync.HasPendingRestore;
            if (report.Snapshot is { } snapshot)
            {
                _cloudRemote = snapshot;
                _cloudRemoteKnown = true;
                _cloudRemoteProblem = null;
            }
            else if (report.Outcome == CloudSyncOutcome.Failed && report.Issue == CloudSyncIssue.NothingInCloud)
            {
                _cloudRemote = null;
                _cloudRemoteKnown = true;
            }

            if (report.Outcome == CloudSyncOutcome.Downloaded) Status = localization.Text("CloudRestartHint");
            if (report.IsFailure && trigger != CloudSyncTrigger.Manual && !_announcedCloudFailure)
            {
                _announcedCloudFailure = true;
                Status = localization.Format("CloudStatusBarFailed", CloudIssueText(report.Issue, report.Message));
            }
            if (!report.IsFailure) _announcedCloudFailure = false;

            await ReloadServiceOwnedSettingsAsync();
        }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Cloud sync");
            Status = exception.Message;
        }
        finally
        {
            IsCloudSyncBusy = false;
            // The quarter hour is counted from the last exchange, whoever asked for it, so the
            // page's "next at" is always an honest time.
            RestartCloudTimer();
            await RefreshCloudStatusAsync();
        }
    }

    [RelayCommand]
    private async Task ConnectGoogleDriveAsync()
    {
        if (IsGoogleConnecting) return;
        IsGoogleConnecting = true;
        CloudSyncStatusText = localization.Text("CloudGoogleWaiting");
        try
        {
            if (_settings is not null && !string.IsNullOrWhiteSpace(GoogleDriveClientSecret))
            {
                _settings.GoogleDriveClientSecretCiphertext = secrets.Protect(GoogleDriveClientSecret.Trim());
                GoogleDriveClientSecret = string.Empty;
                HasGoogleClientSecret = true;
            }
            await FlushSettingsAsync();

            var report = await cloudSync.AuthorizeGoogleAsync();
            await ReloadServiceOwnedSettingsAsync();
            CloudSyncStatusText = report.Outcome == CloudSyncOutcome.Connected
                ? GoogleDriveAccount.Length > 0
                    ? localization.Format("CloudLogConnectedAs", GoogleDriveAccount)
                    : localization.Text("CloudLogConnected")
                : CloudIssueText(report.Issue, report.Message);
            await RefreshCloudStatusAsync();

            // Connecting ends with the data actually in the cloud rather than with one more
            // button left to find — unless the user keeps syncing in their own hands.
            if (report.Outcome == CloudSyncOutcome.Connected && CloudSyncAutomatic)
                await RunCloudSyncAsync(CloudSyncDirection.Auto, CloudSyncTrigger.Manual);
        }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Connecting Google Drive");
            CloudSyncStatusText = exception.Message;
        }
        finally { IsGoogleConnecting = false; }
    }

    [RelayCommand]
    private async Task DisconnectGoogleDriveAsync()
    {
        try
        {
            await cloudSync.SignOutGoogleAsync();
            await ReloadServiceOwnedSettingsAsync();
            CloudSyncStatusText = localization.Text("CloudGoogleDisconnected");
            ForgetCloudRemote();
            await RefreshCloudStatusAsync();
        }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Disconnecting Google Drive");
            CloudSyncStatusText = exception.Message;
        }
    }

    /// <summary>"Check the cloud": looks at what the cloud holds without moving anything.</summary>
    [RelayCommand]
    private async Task RefreshCloudSnapshotAsync()
    {
        if (IsCloudSyncOff || IsCloudSyncBusy) return;
        IsCloudSyncBusy = true;
        RenderCloudStatus();
        try
        {
            await FlushSettingsAsync();
            var readiness = await cloudSync.CheckReadyAsync();
            if (!readiness.IsReady)
            {
                _cloudRemoteKnown = false;
                _cloudRemoteProblem = CloudIssueText(readiness.Issue, readiness.Message);
                return;
            }
            _cloudRemote = await cloudSync.PeekAsync();
            _cloudRemoteKnown = true;
            _cloudRemoteProblem = null;
        }
        catch (CloudSyncException exception)
        {
            _cloudRemoteKnown = false;
            _cloudRemoteProblem = CloudIssueText(exception.Issue, exception.Message);
        }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Reading the cloud snapshot");
            _cloudRemoteKnown = false;
            _cloudRemoteProblem = CloudIssueText(CloudSyncIssue.Unexpected, exception.Message);
        }
        finally
        {
            IsCloudSyncBusy = false;
            await RefreshCloudStatusAsync();
        }
    }

    private void ForgetCloudRemote()
    {
        _cloudRemote = null;
        _cloudRemoteKnown = false;
        _cloudRemoteProblem = null;
    }

    /// <summary>
    /// Re-reads the fields the update and sync services write straight to the database. The
    /// store no longer takes those fields from the shell's copy, so this is about showing them,
    /// not about protecting them.
    /// </summary>
    private async Task ReloadServiceOwnedSettingsAsync(bool applyToView = true)
    {
        var stored = await settingsRepository.GetAppSettingsAsync();
        if (_settings is not null)
        {
            _settings.GoogleDriveAccount = stored.GoogleDriveAccount;
            _settings.GoogleDriveRefreshTokenCiphertext = stored.GoogleDriveRefreshTokenCiphertext;
            _settings.CloudSyncLastRevision = stored.CloudSyncLastRevision;
            _settings.CloudSyncLastSyncUtc = stored.CloudSyncLastSyncUtc;
            _settings.CloudSyncLocalFingerprint = stored.CloudSyncLocalFingerprint;
            _settings.CloudSyncProvider = stored.CloudSyncProvider;
            _settings.UpdateLastCheckedUtc = stored.UpdateLastCheckedUtc;
            _settings.UpdateSkippedVersion = stored.UpdateSkippedVersion;
        }
        if (!applyToView) return;
        GoogleDriveAccount = stored.GoogleDriveAccount;
        HasGoogleSignIn = !string.IsNullOrWhiteSpace(stored.GoogleDriveRefreshTokenCiphertext);
        HasGoogleClientSecret = !string.IsNullOrWhiteSpace(stored.GoogleDriveClientSecretCiphertext);
        var provider = stored.CloudSyncProvider.ToString();
        if (provider != CloudSyncProvider)
        {
            CloudSyncProvider = provider;
            SelectedCloudProviderOption = CloudProviderOptions.FirstOrDefault(option => option.Value == provider)
                                          ?? CloudProviderOptions.FirstOrDefault();
        }
    }

    /// <summary>
    /// Asks the sync service where things stand — offline, a handful of cheap queries — and
    /// redraws the card, the history and the tray's line from the answer.
    /// </summary>
    private async Task RefreshCloudStatusAsync()
    {
        _lastCloudStatusRefreshUtc = DateTimeOffset.UtcNow;
        try
        {
            _cloudStatus = await cloudSync.GetStatusAsync();
            IsCloudRestartPending = _cloudStatus.RestartPending;
        }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Reading the sync status");
        }
        RebuildCloudJournal();
        RenderCloudStatus();
    }

    /// <summary>Keeps the page's times and the tray's line current, on the clock the shell already runs.</summary>
    private void TickCloudStatus()
    {
        if (_loadingSettings || IsCloudSyncOff || IsCloudSyncBusy) return;
        var interval = IsSettings && IsSettingsData ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(5);
        if (DateTimeOffset.UtcNow - _lastCloudStatusRefreshUtc < interval) return;
        _ = RefreshCloudStatusAsync();
    }

    /// <summary>
    /// Works out the one state the card is in. The order is the order of what the user must
    /// act on first: a restart that is owed, a choice that is owed, setup that is unfinished,
    /// a failure — and only then the ordinary states of waiting changes and all being well.
    /// </summary>
    private void RenderCloudStatus()
    {
        OnPropertyChanged(nameof(CloudTrayDetail));
        var status = _cloudStatus;
        if (IsCloudSyncOff || status is null)
        {
            CloudStatusTitle = string.Empty;
            CloudStatusDetail = string.Empty;
            return;
        }

        var lastSuccess = status.LastSuccess;
        var lastAttempt = status.LastAttempt;
        string kind, title, detail;
        if (IsCloudSyncBusy)
            (kind, title, detail) = (CloudStatusKinds.Busy, T("CloudStateSyncing"), T("CloudStateSyncingHint"));
        else if (IsCloudRestartPending)
            (kind, title, detail) = (CloudStatusKinds.Attention, T("CloudStateRestart"), string.Empty);
        else if (IsCloudConflict)
            (kind, title, detail) = (CloudStatusKinds.Attention, T("CloudConflictTitle"), string.Empty);
        else if (status.SetupIssue != CloudSyncIssue.None)
            (kind, title, detail) = (CloudStatusKinds.Attention, T("CloudStateSetup"), CloudIssueText(status.SetupIssue, string.Empty));
        else if (lastAttempt is { IsFailure: true } failed && (lastSuccess is null || failed.AtUtc > lastSuccess.AtUtc))
            (kind, title, detail) = (CloudStatusKinds.Error, T("CloudStateFailed"),
                CloudIssueText(failed.Issue, failed.Detail) +
                (CloudSyncAutomatic ? " " + localization.Format("CloudStateRetryAuto", NextAutomaticSyncText()) : string.Empty));
        else if (lastSuccess is null)
            (kind, title, detail) = (CloudStatusKinds.Pending, T("CloudStateNever"), T("CloudStateNeverHint"));
        else if (status.HasLocalChanges)
            (kind, title, detail) = (CloudStatusKinds.Pending, T("CloudStatePending"), CloudSyncAutomatic
                ? localization.Format("CloudStatePendingAuto", NextAutomaticSyncText())
                : T("CloudStatePendingManual"));
        else
            (kind, title, detail) = (CloudStatusKinds.Ok, T("CloudStateOk"),
                localization.Format("CloudStateOkHint", CloudWhen(lastSuccess.AtUtc)));

        CloudStatusKind = kind;
        CloudStatusTitle = title;
        CloudStatusDetail = detail;

        CloudLastSyncText = lastSuccess is null
            ? T("CloudFactNever")
            : $"{CloudWhen(lastSuccess.AtUtc)} · {CloudJournalText(lastSuccess)}";
        CloudRemoteText = _cloudRemoteProblem ?? (!_cloudRemoteKnown
            ? T("CloudFactRemoteUnknown")
            : _cloudRemote is null
                ? T("CloudNoSnapshot")
                : string.IsNullOrWhiteSpace(_cloudRemote.DeviceName)
                    ? localization.Format("CloudSnapshotFormat", CloudWhen(_cloudRemote.ModifiedUtc))
                    : localization.Format("CloudSnapshotDeviceFormat", CloudWhen(_cloudRemote.ModifiedUtc),
                        _cloudRemote.DeviceName));
        CloudLocalText = !status.HasLocalChanges
            ? T("CloudFactLocalClean")
            : status.LocalChangedUtc is { } changed
                ? localization.Format("CloudFactLocalChangedSince", CloudWhen(changed))
                : T("CloudFactLocalChanged");
        CloudScheduleText = CloudSyncAutomatic
            ? localization.Format("CloudFactScheduleAuto", NextAutomaticSyncText())
            : T("CloudFactScheduleManual");
        OnPropertyChanged(nameof(CloudTrayDetail));
    }

    private string T(string key) => localization.Text(key);

    private void RebuildCloudJournal(bool force = false)
    {
        var history = cloudSync.History;
        var shape = (history.Count, history.Count > 0 ? history[^1].AtUtc : DateTimeOffset.MinValue,
            DateOnly.FromDateTime(DateTime.Today), localization.CurrentCulture.Name);
        if (!force && shape == _cloudJournalShape) return;
        _cloudJournalShape = shape;

        CloudJournal.Clear();
        foreach (var entry in history.Reverse().Take(8))
            CloudJournal.Add(new CloudJournalItemViewModel(
                CloudJournalWhen(entry.AtUtc),
                CloudJournalText(entry),
                T(entry.Trigger switch
                {
                    CloudSyncTrigger.Automatic => "CloudTriggerAutomatic",
                    CloudSyncTrigger.Startup => "CloudTriggerStartup",
                    CloudSyncTrigger.Exit => "CloudTriggerExit",
                    _ => "CloudTriggerManual"
                }),
                entry.IsFailure ? CloudStatusKinds.Error
                : entry.Outcome == CloudSyncOutcome.Conflict ? CloudStatusKinds.Attention
                : CloudStatusKinds.Ok));
        HasCloudJournal = CloudJournal.Count > 0;
    }

    /// <summary>What one exchange did, in a line.</summary>
    private string CloudJournalText(CloudSyncLogEntry entry) => entry.Outcome switch
    {
        CloudSyncOutcome.Uploaded => localization.Format("CloudLogUploaded", BinarySizeFormatter.Format(entry.Bytes)),
        CloudSyncOutcome.Downloaded => entry.Subject.Length > 0
            ? localization.Format("CloudLogDownloadedFrom", entry.Subject)
            : T("CloudLogDownloaded"),
        CloudSyncOutcome.Applied => T("CloudLogApplied"),
        CloudSyncOutcome.UpToDate => T("CloudLogUpToDate"),
        CloudSyncOutcome.Conflict => T("CloudLogConflict"),
        CloudSyncOutcome.Connected => entry.Subject.Length > 0
            ? localization.Format("CloudLogConnectedAs", entry.Subject)
            : T("CloudLogConnected"),
        CloudSyncOutcome.Disconnected => T("CloudLogDisconnected"),
        _ => localization.Format("CloudLogFailed", CloudIssueText(entry.Issue, entry.Detail))
    };

    /// <summary>What went wrong, and what to do about it, in the reader's language.</summary>
    public string CloudIssueText(CloudSyncIssue issue, string detail) => issue switch
    {
        CloudSyncIssue.None => string.Empty,
        CloudSyncIssue.Unexpected => localization.Format("CloudIssueUnexpected",
            string.IsNullOrWhiteSpace(detail) ? "—" : detail.Trim()),
        _ => T("CloudIssue" + issue)
    };

    /// <summary>"just now", "today at 14:32", "yesterday at 18:05", "12 September at 09:10".</summary>
    private string CloudWhen(DateTimeOffset utc)
    {
        var local = utc.ToLocalTime();
        if (DateTimeOffset.Now - local < TimeSpan.FromMinutes(1)) return T("CloudJustNow");
        return CloudDay(local, todayKey: "CloudTodayAt");
    }

    /// <summary>The history's column: a bare time for today, so the list reads down like a clock.</summary>
    private string CloudJournalWhen(DateTimeOffset utc) => CloudDay(utc.ToLocalTime(), todayKey: null);

    private string CloudDay(DateTimeOffset local, string? todayKey)
    {
        var time = FormatClockTime(local.TimeOfDay);
        if (local.Date == DateTime.Today) return todayKey is null ? time : localization.Format(todayKey, time);
        if (local.Date == DateTime.Today.AddDays(-1)) return localization.Format("CloudYesterdayAt", time);
        var day = local.ToString(localization.CurrentCulture.DateTimeFormat.MonthDayPattern, localization.CurrentCulture);
        return localization.Format("CloudDateAt", day, time);
    }

    private string NextAutomaticSyncText()
    {
        var next = _nextAutomaticSyncUtc == DateTimeOffset.MaxValue
            ? DateTimeOffset.UtcNow + _cloudSyncTimer.Interval
            : _nextAutomaticSyncUtc;
        return FormatClockTime(next.ToLocalTime().TimeOfDay);
    }

    /// <summary>A word or two for the tray menu's sync line, from the same state the page shows.</summary>
    public string CloudTrayDetail => IsCloudSyncOff || _cloudStatus is null
        ? string.Empty
        : CloudStatusKind switch
        {
            CloudStatusKinds.Ok when _cloudStatus.LastSuccess is { } success
                => localization.Format("CloudTrayOk", CloudJournalWhen(success.AtUtc)),
            CloudStatusKinds.Busy => T("CloudTraySyncing"),
            CloudStatusKinds.Pending => T("CloudTrayPending"),
            CloudStatusKinds.Error => T("CloudTrayError"),
            CloudStatusKinds.Attention => T("CloudTrayAttention"),
            _ => string.Empty
        };

    /// <summary>Pushes a change out automatically when the user asked for that.</summary>
    private void QueueAutomaticCloudSync(CloudSyncTrigger trigger)
    {
        if (_loadingSettings || IsCloudSyncOff || !CloudSyncAutomatic || IsCloudSyncBusy || IsCloudRestartPending) return;
        // A choice only the user can make is not the timer's to make, and asking it again every
        // quarter of an hour would only fill the history with the same question. Setup that is
        // plainly unfinished is the same: the card already says what is missing.
        if (IsCloudConflict || _cloudStatus is { SetupIssue: not CloudSyncIssue.None }) return;
        _ = RunCloudSyncAsync(CloudSyncDirection.Auto, trigger);
    }

    private void RestartCloudTimer()
    {
        if (!_choresStarted) return;
        _cloudSyncTimer.Stop();
        _cloudSyncTimer.Start();
        _nextAutomaticSyncUtc = DateTimeOffset.UtcNow + _cloudSyncTimer.Interval;
    }

    private void LoadSystemSettings(AppSettings settings)
    {
        NotificationsEnabled = settings.NotificationsEnabled;
        NotificationSoundEnabled = settings.NotificationSoundEnabled;
        // Zero is a length here, so a stored zero is kept rather than read as "unset".
        DoNotDisturbMinutes = settings.DoNotDisturbMinutes < 0 ? 60 : settings.DoNotDisturbMinutes;
        DoNotDisturbUntilResumed = settings.DoNotDisturbUntilResumed;
        DoNotDisturbUntilUtc = settings.DoNotDisturbUntilUtc is { } until && DateTimeOffset.UtcNow < until
            ? until
            : null;
        LoadFocusSettings(settings);
        UpdateCheckEnabled = settings.UpdateCheckEnabled;
        CloudSyncProvider = settings.CloudSyncProvider.ToString();
        CloudSyncFolderPath = settings.CloudSyncFolderPath;
        CloudSyncAutomatic = settings.CloudSyncAutomatic;
        CloudSyncDeviceName = string.IsNullOrWhiteSpace(settings.CloudSyncDeviceName)
            ? Environment.MachineName
            : settings.CloudSyncDeviceName;
        GoogleDriveClientId = settings.GoogleDriveClientId;
        GoogleDriveAccount = settings.GoogleDriveAccount;
        HasGoogleSignIn = !string.IsNullOrWhiteSpace(settings.GoogleDriveRefreshTokenCiphertext);
        HasGoogleClientSecret = !string.IsNullOrWhiteSpace(settings.GoogleDriveClientSecretCiphertext);
        IsCloudRestartPending = cloudSync.HasPendingRestore;
        // A conflict outlives a restart: it is still the user's to settle, and the journal
        // remembers it was the last thing that happened.
        IsCloudConflict = cloudSync.History.LastOrDefault()?.Outcome == CloudSyncOutcome.Conflict;
    }

    private void CopySystemSettings()
    {
        if (_settings is null) return;
        _settings.NotificationsEnabled = NotificationsEnabled;
        _settings.NotificationSoundEnabled = NotificationSoundEnabled;
        _settings.DoNotDisturbMinutes = DoNotDisturbMinutes <= UntilResumed
            ? UntilResumed
            : Math.Clamp(DoNotDisturbMinutes, 5, 1440);
        _settings.DoNotDisturbUntilResumed = DoNotDisturbUntilResumed;
        _settings.DoNotDisturbUntilUtc = DoNotDisturbUntilUtc;
        CopyFocusSettings();
        _settings.UpdateCheckEnabled = UpdateCheckEnabled;
        _settings.CloudSyncProvider = Parse(CloudSyncProvider, Domain.CloudSyncProvider.None);
        _settings.CloudSyncFolderPath = CloudSyncFolderPath.Trim();
        _settings.CloudSyncAutomatic = CloudSyncAutomatic;
        _settings.CloudSyncDeviceName = CloudSyncDeviceName.Trim();
        _settings.GoogleDriveClientId = GoogleDriveClientId.Trim();
        if (!string.IsNullOrWhiteSpace(GoogleDriveClientSecret))
        {
            _settings.GoogleDriveClientSecretCiphertext = secrets.Protect(GoogleDriveClientSecret.Trim());
            GoogleDriveClientSecret = string.Empty;
            HasGoogleClientSecret = true;
        }
    }

    // ---------------------------------------------------------------- background chores

    private readonly DispatcherTimer _cloudSyncTimer = new() { Interval = TimeSpan.FromMinutes(15) };
    private bool _choresStarted;

    /// <summary>
    /// Starts the two things that run on their own: a release check and, when the user asked
    /// for it, a periodic sync. Both are staggered a little so neither competes with the first
    /// paint of the window.
    /// </summary>
    private void StartBackgroundChores()
    {
        if (_choresStarted) return;
        _choresStarted = true;
        StartHardwareProbe();

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(6));
            await Dispatcher.UIThread.InvokeAsync(CheckForUpdatesQuietlyAsync);
        });

        _cloudSyncTimer.Tick += (_, _) =>
        {
            _nextAutomaticSyncUtc = DateTimeOffset.UtcNow + _cloudSyncTimer.Interval;
            QueueAutomaticCloudSync(CloudSyncTrigger.Automatic);
        };
        _cloudSyncTimer.Start();
        _nextAutomaticSyncUtc = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(3);
        _ = RefreshCloudStatusAsync();

        _ = Task.Run(async () =>
        {
            // Pulling first means a machine that was edited elsewhere catches up before it
            // starts producing changes of its own that would then look like a conflict.
            await Task.Delay(TimeSpan.FromSeconds(3));
            await Dispatcher.UIThread.InvokeAsync(() => QueueAutomaticCloudSync(CloudSyncTrigger.Startup));
        });
    }

    /// <summary>Pushes the session's work out on the way to closing. Never throws at the caller.</summary>
    public async Task SyncBeforeExitAsync()
    {
        _cloudSyncTimer.Stop();
        if (IsCloudSyncOff || !CloudSyncAutomatic || IsCloudSyncBusy || IsCloudRestartPending || IsCloudConflict) return;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await cloudSync.SyncAsync(CloudSyncDirection.Auto, CloudSyncTrigger.Exit, deadline.Token);
        }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Cloud sync on exit");
        }
    }

    private void BuildCloudChoices()
    {
        var current = CloudSyncProvider;
        CloudProviderOptions.Clear();
        CloudProviderOptions.Add(new(nameof(Domain.CloudSyncProvider.None), localization.Text("CloudProviderOff")));
        CloudProviderOptions.Add(new(nameof(Domain.CloudSyncProvider.Folder), localization.Text("CloudProviderFolder")));
        CloudProviderOptions.Add(new(nameof(Domain.CloudSyncProvider.GoogleDrive), "Google Drive"));
        SelectedCloudProviderOption = CloudProviderOptions.FirstOrDefault(option => option.Value == current)
                                      ?? CloudProviderOptions[0];
        // Every sentence on the card is worded; a new language redraws all of them.
        OnPropertyChanged(nameof(GoogleAccountText));
        RebuildCloudJournal(force: true);
        RenderCloudStatus();
    }
}
