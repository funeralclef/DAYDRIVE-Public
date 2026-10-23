namespace IntelligentScheduler.Domain;

public sealed class TaskItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public TaskStatus Status { get; set; } = TaskStatus.Ready;
    public int Priority { get; set; } = 50;
    public int EstimatedDurationMin { get; set; } = 30;
    public int RemainingDurationMin { get; set; } = 30;
    public DateTimeOffset? DeadlineUtc { get; set; }
    public DateTimeOffset? EarliestStartUtc { get; set; }
    public bool Splittable { get; set; }
    public int MinBlockMin { get; set; } = 30;
    public int MaxSplits { get; set; } = 3;
    public int? EnergyRequired { get; set; }
    public PreferredTimeOfDay PreferredTimeOfDay { get; set; } = PreferredTimeOfDay.Any;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public int Version { get; set; } = 1;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Title)) throw new ArgumentException("Task title is required.");
        if (Priority is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(Priority));
        if (EstimatedDurationMin <= 0) throw new ArgumentOutOfRangeException(nameof(EstimatedDurationMin));
        if (RemainingDurationMin < 0) throw new ArgumentOutOfRangeException(nameof(RemainingDurationMin));
        if (MinBlockMin <= 0) throw new ArgumentOutOfRangeException(nameof(MinBlockMin));
        if (MaxSplits is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(MaxSplits));
    }
}

public sealed class CalendarEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public DateTimeOffset StartUtc { get; set; }
    public DateTimeOffset EndUtc { get; set; }
    public string? ExternalId { get; set; }
    public string Source { get; set; } = "Local";
    public EventCategory Category { get; set; } = EventCategory.General;
    public EventPriority Priority { get; set; } = EventPriority.Normal;
    public EventFlexibility Flexibility { get; set; } = EventFlexibility.Fixed;
    public EventRecurrence Recurrence { get; set; } = EventRecurrence.None;
    public Guid? SeriesId { get; set; }
    public int OccurrenceIndex { get; set; }
    public bool IsRecurrenceException { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class TimeBlock
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TaskId { get; set; }
    public TaskItem? Task { get; set; }
    public string? TitleOverride { get; set; }
    public DateTimeOffset StartUtc { get; set; }
    public DateTimeOffset EndUtc { get; set; }
    public BlockState State { get; set; } = BlockState.Planned;
    public BlockOrigin Origin { get; set; } = BlockOrigin.Manual;
    public bool IsLocked { get; set; }
    public Guid? PlanRevisionId { get; set; }
    public DateTimeOffset? PreviousStartUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public int DurationMinutes => Math.Max(0, (int)(EndUtc - StartUtc).TotalMinutes);
}

public sealed class TaskScheduleExclusion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TaskId { get; set; }
    public DateTimeOffset StartUtc { get; set; }
    public DateTimeOffset EndUtc { get; set; }
    public TaskScheduleExclusionKind Kind { get; set; } = TaskScheduleExclusionKind.ManualUnschedule;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AvailabilityRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DayOfWeek DayOfWeek { get; set; }
    public TimeOnly StartLocal { get; set; }
    public TimeOnly EndLocal { get; set; }
    public bool IsEnabled { get; set; } = true;
}

public sealed class AvailabilityException
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateOnly Date { get; set; }
    public TimeOnly StartLocal { get; set; }
    public TimeOnly EndLocal { get; set; }
    public AvailabilityExceptionKind Kind { get; set; }
}

public sealed class PlanningProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TimeZoneId { get; set; } = TimeZoneInfo.Local.Id;
    public int SchedulingUnitMinutes { get; set; } = 5;
    public int BufferBeforeFixedMin { get; set; } = 5;
    public int BufferAfterFixedMin { get; set; } = 5;
    public int MaxSolveTimeMs { get; set; } = 1200;
}

public sealed class PlanRevision
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset HorizonStartUtc { get; set; }
    public DateTimeOffset HorizonEndUtc { get; set; }
    public PlanStatus Status { get; set; }
    public long ObjectiveScore { get; set; }
    public long SolveDurationMs { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// What actually happened to a calendar event. Events are the only real schedule unit, so
/// this is where "planned versus done" is recorded — one row per event, replaced on change.
/// </summary>
public sealed class EventOutcome
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EventId { get; set; }
    public EventOutcomeKind Kind { get; set; } = EventOutcomeKind.Started;
    /// <summary>Kept alongside the event so statistics survive the event being edited or removed.</summary>
    public DateTimeOffset PlannedStartUtc { get; set; }
    public int PlannedMinutes { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? FinishedAtUtc { get; set; }
    public int ActualMinutes { get; set; }
    public string? Note { get; set; }

    /// <summary>
    /// Time the session spent standing still, kept apart from the work rather than folded into
    /// it. A stopwatch that counts a coffee break as effort is a stopwatch that has to be
    /// answered for afterwards, and the whole point of separating the two is that nothing has
    /// to be asked at the moment the user steps away.
    /// </summary>
    public int PausedSeconds { get; set; }

    /// <summary>When the pause now in force began; null while the session is actually running.</summary>
    public DateTimeOffset? PausedAtUtc { get; set; }

    /// <summary>
    /// How far a partly-done event got, 0..100. Asked instead of a duration: the session
    /// already measured the minutes, so the only thing the user still knows and the
    /// application does not is how much of the work those minutes covered.
    /// </summary>
    public int ProgressPercent { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public bool IsFinished => Kind is not EventOutcomeKind.Started;

    /// <summary>A session that is on but standing still.</summary>
    public bool IsPaused => !IsFinished && StartedAtUtc is not null && PausedAtUtc is not null;

    /// <summary>A session that is on and counting.</summary>
    public bool IsRunning => !IsFinished && StartedAtUtc is not null && PausedAtUtc is null;

    /// <summary>Whether a stopwatch is on at all, moving or held.</summary>
    public bool IsSessionOpen => !IsFinished && StartedAtUtc is not null;

    /// <summary>
    /// Time actually worked, pauses taken out — including the pause still open at the moment
    /// this is read, so the number never creeps while nobody is working.
    /// </summary>
    public TimeSpan WorkedAt(DateTimeOffset now)
    {
        if (StartedAtUtc is not { } started) return TimeSpan.Zero;
        var end = FinishedAtUtc ?? now;
        var paused = TimeSpan.FromSeconds(PausedSeconds);
        if (PausedAtUtc is { } pausedAt && FinishedAtUtc is null && end > pausedAt) paused += end - pausedAt;
        var worked = end - started - paused;
        return worked < TimeSpan.Zero ? TimeSpan.Zero : worked;
    }

    /// <summary>Total time stood still, the pause currently open included.</summary>
    public TimeSpan PausedFor(DateTimeOffset now)
    {
        var paused = TimeSpan.FromSeconds(PausedSeconds);
        if (PausedAtUtc is { } pausedAt && FinishedAtUtc is null && now > pausedAt) paused += now - pausedAt;
        return paused;
    }

    /// <summary>How long the pause now in force has lasted, or zero when nothing is paused.</summary>
    public TimeSpan CurrentPauseFor(DateTimeOffset now)
        => PausedAtUtc is { } pausedAt && !IsFinished && now > pausedAt ? now - pausedAt : TimeSpan.Zero;

    /// <summary>Minutes late relative to the planned start, or null when it was never started.</summary>
    public int? StartDelayMinutes => StartedAtUtc is { } started
        ? (int)Math.Round((started - PlannedStartUtc).TotalMinutes)
        : null;
}

/// <summary>
/// What outlives one slot in the day: where to pick the work up next time, and how often the
/// event has been pushed onto a later day. Kept apart from <see cref="EventOutcome"/> because
/// a verdict describes one occurrence and this describes the work itself — a continuation
/// point written on Monday is still the right note to open on Wednesday.
/// </summary>
public sealed class EventFollowUp
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EventId { get; set; }

    /// <summary>The continuation point, in the user's own words: "recheck the last three screens".</summary>
    public string? NextStep { get; set; }

    /// <summary>How many times this event has been moved to a later day rather than done.</summary>
    public int DeferCount { get; set; }

    public DateTimeOffset? LastDeferredAtUtc { get; set; }

    /// <summary>What the user said was in the way, once being in the way became a pattern.</summary>
    public EventBlocker Blocker { get; set; } = EventBlocker.None;

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public bool HasNextStep => !string.IsNullOrWhiteSpace(NextStep);

    /// <summary>
    /// Worth asking what is in the way. Once is a bad day and gets no question at all; the
    /// second push is the first time the answer could be about the task rather than the week.
    /// </summary>
    public bool NeedsBlockerAnswer => DeferCount >= 2 && Blocker == EventBlocker.None;
}

public sealed class TaskExecution
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TaskId { get; set; }
    public Guid TimeBlockId { get; set; }
    public int PlannedMinutes { get; set; }
    public int ActualMinutes { get; set; }
    public DateTimeOffset CompletedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class SchedulerDiagnostic
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RevisionId { get; set; }
    public DateTimeOffset HorizonStartUtc { get; set; }
    public DateTimeOffset HorizonEndUtc { get; set; }
    public int TaskCount { get; set; }
    public int FixedEventCount { get; set; }
    public int ExistingBlockCount { get; set; }
    public int AvailabilityWindowCount { get; set; }
    public string Status { get; set; } = string.Empty;
    public long ObjectiveScore { get; set; }
    public long SolveDurationMs { get; set; }
    public string ResultSummaryJson { get; set; } = "{}";
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AppSettings
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool NotificationsEnabled { get; set; } = true;
    /// <summary>Whether a reminder makes a sound as well as appearing.</summary>
    public bool NotificationSoundEnabled { get; set; } = true;
    /// <summary>
    /// When the current quiet spell ends. Null, or a moment already past, means reminders are
    /// arriving normally. Storing the end rather than a flag is what lets a quiet hour outlive
    /// a restart and lift itself.
    /// </summary>
    public DateTimeOffset? DoNotDisturbUntilUtc { get; set; }
    /// <summary>
    /// A quiet spell with no end: it lasts until it is switched off by hand. Kept apart from the
    /// moment above rather than written as one far in the future, because "until I say so" is a
    /// different answer from "until half past two" and the interface has to be able to say which.
    /// </summary>
    public bool DoNotDisturbUntilResumed { get; set; }
    /// <summary>How long "do not disturb" lasts when it is switched on.</summary>
    public int DoNotDisturbMinutes { get; set; } = 60;

    // ---- Focus mode -----------------------------------------------------------------
    /// <summary>
    /// Whether the shell stands back on its own when something else takes the whole screen — a
    /// game, a film, a talk being given. On by default: the shell claims keys from the whole
    /// desktop and puts cards on top of everything, and both of those are wrong in the middle
    /// of a boss fight.
    /// </summary>
    public bool FocusModeEnabled { get; set; } = true;

    /// <summary>
    /// Whether standing back includes letting go of the desktop-wide shortcuts. It is the half
    /// that matters most in a game: a combination this application holds is one the game never
    /// sees, and <c>Ctrl+Alt+S</c> is somebody's crouch.
    /// </summary>
    public bool FocusModeReleasesShortcuts { get; set; } = true;

    /// <summary>
    /// Whether reminders wait rather than arrive. They are kept, not dropped, and handed over
    /// when the screen comes back.
    /// </summary>
    public bool FocusModeHoldsReminders { get; set; } = true;

    /// <summary>
    /// Whether the petal leaves the edge of the screen. It is the half nobody thought to ask
    /// for and the one that is felt: letting go of the shortcuts stops the shell answering keys
    /// it was never sent, but the petal is a strip of the screen that still swallows a click,
    /// and the edge of the screen is exactly where a mouse ends up when it is flung at
    /// something. Off leaves it where it is, for anyone who would rather keep the way back.
    /// </summary>
    public bool FocusModeHidesHandle { get; set; } = true;

    public bool AutostartEnabled { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public string QuickAddHotkey { get; set; } = "Ctrl+Alt+Space";

    /// <summary>
    /// Every shortcut the user has changed, as action name to gesture. Empty means "the
    /// defaults", which is what a fresh profile wants and also what a profile that has never
    /// opened the shortcuts page should keep getting as those defaults are improved.
    ///
    /// The single hotkey above predates this and is read as the shell's own binding when the
    /// map has nothing to say about it, so an upgrade keeps whatever was already set.
    /// </summary>
    public string ShortcutsJson { get; set; } = string.Empty;
    public AiProviderMode AiProviderMode { get; set; } = AiProviderMode.LocalParser;
    public int BackupRetentionCount { get; set; } = 7;
    public AssistantProvider AssistantProvider { get; set; } = AssistantProvider.Local;
    public AssistantAccessLevel AssistantAccessLevel { get; set; } = AssistantAccessLevel.ConfirmAll;
    public bool ShareContextWithExternalAi { get; set; }
    public string AssistantEndpoint { get; set; } = "http://localhost:11434";
    public string AssistantModel { get; set; } = "qwen3:4b";
    public string? AssistantApiKeyCiphertext { get; set; }
    public string McpTransport { get; set; } = "StreamableHttp";
    /// <summary>
    /// Where the MCP server lives, kept apart from <see cref="AssistantEndpoint"/>. Sharing one
    /// field meant an ordinary API URL also read as an MCP server, and tool federation then paid
    /// for a doomed session handshake before every single message.
    /// </summary>
    public string McpEndpoint { get; set; } = string.Empty;
    public string McpCommand { get; set; } = string.Empty;
    public string McpArguments { get; set; } = string.Empty;
    public string McpToolName { get; set; } = "assistant";
    /// <summary>
    /// When enabled the configured MCP server is used as an extra tool source for whichever
    /// language model is selected, instead of replacing the model with a single bridge tool.
    /// </summary>
    public bool McpToolsEnabled { get; set; } = true;
    /// <summary>Lets the assistant keep durable notes about the user between conversations.</summary>
    public bool AssistantMemoryEnabled { get; set; } = true;
    /// <summary>Conversation restored the next time the chat panel opens.</summary>
    public Guid? ActiveConversationId { get; set; }
    public string LocalModelId { get; set; } = "qwen35-9b";
    /// <summary>
    /// Whether the local model may run on the graphics card. On installs a GPU build of
    /// llama.cpp beside the processor one and offloads every layer that fits.
    /// </summary>
    public bool LocalModelGpuEnabled { get; set; } = true;
    public bool LocalModelVisionEnabled { get; set; }
    public int LocalModelIdleMinutes { get; set; } = 10;
    /// <summary>llama-server context window. Smaller is faster and needs less RAM.</summary>
    public int LocalModelContextTokens { get; set; } = 8192;
    /// <summary>0 lets llama.cpp choose; otherwise the thread count passed with -t.</summary>
    public int LocalModelThreads { get; set; }
    /// <summary>
    /// Lets the machine choose the local engine's numbers — context, threads, the graphics card,
    /// the answer budget — from what it actually has. Off leaves them exactly as set by hand.
    /// </summary>
    public bool LocalModelAutoTune { get; set; } = true;
    /// <summary>Absolute path to a GGUF the user supplied instead of a catalogue model.</summary>
    public string CustomModelPath { get; set; } = string.Empty;
    public string CustomProjectorPath { get; set; } = string.Empty;

    // Request shaping for the managed local model. Kept apart from the API's below: a small
    // model on this machine wants shorter answers, a shorter memory and a longer wait, and
    // the three of those are chosen for the hardware while LocalModelAutoTune is on.
    public double LocalTemperature { get; set; } = 0.4;
    /// <summary>1.0 means "leave it out of the request".</summary>
    public double LocalTopP { get; set; } = 0.9;
    public int LocalMaxTokens { get; set; } = 3000;
    public int LocalMaxToolIterations { get; set; } = 8;
    public int LocalHistoryTurns { get; set; } = 16;
    public int LocalTimeoutMinutes { get; set; } = 10;

    // Request shaping for a hosted API (OpenAI-compatible or Ollama).
    public double AssistantTemperature { get; set; } = 0.3;
    public int AssistantMaxTokens { get; set; } = 4000;
    /// <summary>1.0 means "leave it out of the request".</summary>
    public double AssistantTopP { get; set; } = 1.0;
    /// <summary>How many tool round trips one answer may take.</summary>
    public int AssistantMaxToolIterations { get; set; } = 12;
    /// <summary>How many earlier messages are replayed to the model.</summary>
    public int AssistantHistoryTurns { get; set; } = 24;
    public int AssistantTimeoutMinutes { get; set; } = 5;
    /// <summary>Appended to the system prompt so the user can shape the assistant's behaviour.</summary>
    public string AssistantExtraInstructions { get; set; } = string.Empty;
    public ShellTheme ShellTheme { get; set; } = ShellTheme.Marelian;
    public ShellThemeMode ShellThemeMode { get; set; } = ShellThemeMode.Dark;
    public ThemeColorSource ThemeColorSource { get; set; } = ThemeColorSource.Preset;
    public VisualPalette VisualPalette { get; set; } = VisualPalette.Vibrant;
    public string AppFontFamily { get; set; } = "builtin:Inter";
    public string CustomAccent { get; set; } = "#D75053";
    public string CustomBackground { get; set; } = "#13110F";
    /// <summary>MARELIAN light/dark profiles, including colours, geometry, text scale and shadows.</summary>
    public string ThemeColorOverridesJson { get; set; } = "{}";
    /// <summary>
    /// The quick widget's mascot. Empty means it keeps taking the accent colour of whatever
    /// theme is on, which is what it did before there was anything to set here.
    /// </summary>
    public string MascotColor { get; set; } = string.Empty;
    /// <summary>
    /// Whether the creature is drawn at all. Off leaves the quick widget's clock block as plain
    /// text; everything else about the widget is unchanged.
    /// </summary>
    public bool ShowMascot { get; set; } = true;
    public bool EnableTransparency { get; set; } = true;
    /// <summary>
    /// How much of the desktop the shell's own surfaces let through, as a percentage. A fresh
    /// profile starts at a trace of it: the glass is what the window is made of rather than a
    /// look it is wearing, and a reader who wants more of it has a slider to say so — whereas
    /// one who first meets the application through a page of text laid over their wallpaper has
    /// nothing to undo it with except the setting they have not found yet.
    /// </summary>
    public double TransparencyIntensity { get; set; } = 10;
    public EdgeRevealMode RevealMode { get; set; } = EdgeRevealMode.Hover;
    /// <summary>
    /// How long the quick widget waits, after the pointer leaves both it and the handle, before
    /// it starts fading away. Long enough to read what it says, short enough not to linger.
    /// </summary>
    public int WidgetHideDelayMs { get; set; } = 1500;
    /// <summary>
    /// Whether the quick widget waits at all. The delay above answers "is this still wanted?"
    /// for a widget that was glanced at on the way past; a widget somebody keeps beside their
    /// work answers that every time they look at it, and with this on it leaves only when it
    /// is closed — the petal's widget button, or the right button on the widget itself.
    /// </summary>
    public bool WidgetKeepOpen { get; set; }
    public bool PinOpen { get; set; }

    // ---- Day wrap-up ----------------------------------------------------------------
    /// <summary>
    /// How many wrap-ups in a row were put away without a single event being answered for.
    /// The offer backs off as this climbs: a prompt nobody uses should ask less often, not
    /// keep asking at the same volume until it is switched off in settings.
    /// </summary>
    public int WrapUpIgnoredStreak { get; set; }

    /// <summary>The last time the wrap-up offered itself, so it offers itself once a day at most.</summary>
    public DateTimeOffset? WrapUpLastOfferedUtc { get; set; }
    // Kept only so databases created by older releases can still be opened.
    // Automatic task placement is no longer part of the application.
    public bool AutoPlan { get; set; }
    // Empty only for a brand-new profile; the desktop resolves and persists the system language.
    public string LanguageCode { get; set; } = string.Empty;
    public bool ReduceMotion { get; set; }
    public bool StartHidden { get; set; }
    public bool KeepOnTop { get; set; } = true;
    public bool Use24HourClock { get; set; } = true;
    public string LastSection { get; set; } = "plan";

    // ---- Release checks -------------------------------------------------------------
    /// <summary>Quietly asks GitHub once a day whether a newer release was published.</summary>
    public bool UpdateCheckEnabled { get; set; } = true;
    /// <summary>A version the user dismissed. It is never announced again.</summary>
    public string UpdateSkippedVersion { get; set; } = string.Empty;
    public DateTimeOffset? UpdateLastCheckedUtc { get; set; }

    // ---- Cloud sync -----------------------------------------------------------------
    public CloudSyncProvider CloudSyncProvider { get; set; } = CloudSyncProvider.None;
    /// <summary>Folder provider: a directory another cloud client already mirrors.</summary>
    public string CloudSyncFolderPath { get; set; } = string.Empty;
    /// <summary>Uploads after changes and pulls on startup instead of waiting for a button.</summary>
    public bool CloudSyncAutomatic { get; set; } = true;
    /// <summary>Stamp of the snapshot this machine last exchanged, used to spot conflicts.</summary>
    public DateTimeOffset? CloudSyncLastSyncUtc { get; set; }
    /// <summary>Revision of the snapshot this machine last uploaded or downloaded.</summary>
    public string CloudSyncLastRevision { get; set; } = string.Empty;
    /// <summary>
    /// Shape of the synced data at the last exchange: row counts and newest stamps per table.
    /// A deletion changes a count without moving any stamp, which is how deleting a task used
    /// to go unnoticed until the next download quietly brought it back.
    /// </summary>
    public string CloudSyncLocalFingerprint { get; set; } = string.Empty;
    /// <summary>Shown next to a snapshot so the user can tell their machines apart.</summary>
    public string CloudSyncDeviceName { get; set; } = string.Empty;
    /// <summary>Desktop OAuth client id the user created in their own Google Cloud project.</summary>
    public string GoogleDriveClientId { get; set; } = string.Empty;
    /// <summary>Google issues one for desktop clients; it is not a secret, but it is not public either.</summary>
    public string? GoogleDriveClientSecretCiphertext { get; set; }
    /// <summary>Refresh token from the consent flow, protected by the local secret store.</summary>
    public string? GoogleDriveRefreshTokenCiphertext { get; set; }
    /// <summary>Google account the refresh token belongs to; display only.</summary>
    public string GoogleDriveAccount { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
