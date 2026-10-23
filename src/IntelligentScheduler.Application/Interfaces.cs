using IntelligentScheduler.Domain;

namespace IntelligentScheduler.Application;

public interface ITaskRepository
{
    Task<IReadOnlyList<TaskItem>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TaskItem>> GetActiveAsync(CancellationToken cancellationToken = default);
    Task AddAsync(TaskItem task, CancellationToken cancellationToken = default);
    Task UpdateAsync(TaskItem task, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface ICalendarRepository
{
    Task<IReadOnlyList<CalendarEvent>> GetRangeAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CalendarEvent>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Specific events, wherever they are now. A project step points at the event booked for it,
    /// and that event may have been moved to any day since — or deleted, which comes back as absent.
    /// </summary>
    Task<IReadOnlyList<CalendarEvent>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default);

    /// <summary>
    /// Which of these repeating series still have an occurrence on the calendar. A checklist kept
    /// for a whole series is only an orphan once the last of its occurrences is gone.
    /// </summary>
    Task<IReadOnlySet<Guid>> GetLiveSeriesAsync(IReadOnlyCollection<Guid> seriesIds, CancellationToken cancellationToken = default);
    Task AddAsync(CalendarEvent calendarEvent, CancellationToken cancellationToken = default);
    Task AddRangeAsync(IReadOnlyCollection<CalendarEvent> events, CancellationToken cancellationToken = default);
    Task<int> ShiftRangeAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, TimeSpan offset, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a set of already-resolved moves in one transaction. The caller decides where each
    /// event lands, so the same path serves "half an hour later" and "onto Thursday" and cannot
    /// leave half a selection moved when one row is missing.
    /// </summary>
    Task<int> MoveEventsAsync(IReadOnlyCollection<EventMove> moves, CancellationToken cancellationToken = default);

    /// <summary>Removes a chosen set of events, ignoring ids that are already gone.</summary>
    Task<int> DeleteManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default);

    Task UpdateAsync(CalendarEvent calendarEvent, CancellationToken cancellationToken = default);
    Task UpdateScopeAsync(CalendarEvent original, CalendarEvent updated, EventSeriesScope scope, CancellationToken cancellationToken = default);
    Task ReplaceScopeAsync(CalendarEvent original, EventSeriesScope scope, IReadOnlyCollection<CalendarEvent> replacements, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task DeleteScopeAsync(CalendarEvent calendarEvent, EventSeriesScope scope, CancellationToken cancellationToken = default);
}

public interface IBlockRepository
{
    /// <summary>
    /// Blocks to draw on the calendar. Skipped and cancelled ones are left out because they no
    /// longer occupy the grid.
    /// </summary>
    Task<IReadOnlyList<TimeBlock>> GetRangeAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Everything that actually happened, skipped sessions included. Statistics need those:
    /// without them the completion rate is always 100% by construction.
    /// </summary>
    Task<IReadOnlyList<TimeBlock>> GetHistoryAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TaskScheduleExclusion>> GetExclusionsAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken = default);
    Task AddAsync(TimeBlock block, CancellationToken cancellationToken = default);
    Task AddExclusionAsync(TaskScheduleExclusion exclusion, CancellationToken cancellationToken = default);
    Task UpdateAsync(TimeBlock block, CancellationToken cancellationToken = default);
    Task<TaskBlockCompletionResult> CompleteTaskBlockAsync(Guid blockId, int actualMinutes, CancellationToken cancellationToken = default);
    Task ApplyPlanAsync(ScheduleResult result, DateTimeOffset horizonStartUtc, DateTimeOffset horizonEndUtc, CancellationToken cancellationToken = default);
}

public interface IAvailabilityRepository
{
    Task<IReadOnlyList<AvailabilityWindow>> GetWindowsAsync(DateTimeOffset horizonStartUtc, DateTimeOffset horizonEndUtc, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AvailabilityRule>> GetRulesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AvailabilityException>> GetExceptionsAsync(CancellationToken cancellationToken = default);
    Task UpdateRuleAsync(AvailabilityRule rule, CancellationToken cancellationToken = default);
    Task AddExceptionAsync(AvailabilityException exception, CancellationToken cancellationToken = default);
    Task DeleteExceptionAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface ISettingsRepository
{
    Task<PlanningProfile> GetPlanningProfileAsync(CancellationToken cancellationToken = default);
    Task UpdatePlanningProfileAsync(PlanningProfile profile, CancellationToken cancellationToken = default);
    Task<AppSettings> GetAppSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the preferences a person sets. Fields a service owns outright are left as stored —
    /// see <see cref="PatchAppSettingsAsync"/> — so a copy of the settings loaded at startup
    /// cannot write an old value back over a new one.
    /// </summary>
    Task UpdateAppSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes a few fields in one read-modify-write and leaves everything else as it is. The
    /// only way to write what a service owns: the Google sign-in, the sync revision and
    /// fingerprint, and the release check's stamps.
    /// </summary>
    Task PatchAppSettingsAsync(Action<AppSettings> change, CancellationToken cancellationToken = default);
}

/// <summary>Records what actually happened to calendar events.</summary>
public interface IEventOutcomeRepository
{
    Task<IReadOnlyList<EventOutcome>> GetRangeAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken = default);
    Task<EventOutcome?> GetForEventAsync(Guid eventId, CancellationToken cancellationToken = default);
    /// <summary>
    /// Verdicts for a specific set of events. Ranges key off the planned start, which stops
    /// matching as soon as an event is moved, so the day list looks its rows up by id instead.
    /// </summary>
    Task<IReadOnlyList<EventOutcome>> GetForEventsAsync(IReadOnlyCollection<Guid> eventIds, CancellationToken cancellationToken = default);
    /// <summary>Inserts or replaces the verdict for one event and returns the stored row.</summary>
    Task<EventOutcome> RecordAsync(EventOutcome outcome, CancellationToken cancellationToken = default);
    Task<bool> ClearAsync(Guid eventId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The continuation point and the postponement history of an event — the part of the work that
/// survives the slot it was booked into.
/// </summary>
public interface IEventFollowUpRepository
{
    Task<IReadOnlyList<EventFollowUp>> GetForEventsAsync(IReadOnlyCollection<Guid> eventIds, CancellationToken cancellationToken = default);
    Task<EventFollowUp?> GetForEventAsync(Guid eventId, CancellationToken cancellationToken = default);

    /// <summary>Writes the continuation point, leaving the postponement history untouched.</summary>
    Task<EventFollowUp> SetNextStepAsync(Guid eventId, string? nextStep, CancellationToken cancellationToken = default);

    /// <summary>Counts one more postponement and returns the row, so the caller can see the new total.</summary>
    Task<EventFollowUp> RecordDeferralAsync(Guid eventId, CancellationToken cancellationToken = default);

    /// <summary>Records what the user said was in the way.</summary>
    Task<EventFollowUp> SetBlockerAsync(Guid eventId, EventBlocker blocker, CancellationToken cancellationToken = default);

    Task<bool> ClearAsync(Guid eventId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Projects — tasks and events broken into steps — and the steps they are written down as.
///
/// An unscheduled project's step has a day and a length that are the user's until it is booked at
/// an hour; from then on its event carries both, and <see cref="ProjectStepReconciler"/> keeps the
/// step in line with it. A scheduled project's steps are ticked per occurrence of their event, and
/// those ticks are kept here too.
/// </summary>
public interface IProjectRepository
{
    Task<IReadOnlyList<Project>> GetProjectsAsync(CancellationToken cancellationToken = default);

    /// <summary>Every step of every project.</summary>
    Task<IReadOnlyList<ProjectStep>> GetStepsAsync(CancellationToken cancellationToken = default);
    Task AddProjectAsync(Project project, CancellationToken cancellationToken = default);

    /// <summary>Writes the project's colour and what it is attached to.</summary>
    Task UpdateProjectAsync(Project project, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the project with all of its steps and their ticks. Events booked for its steps stay
    /// on the calendar: each is a real hour somebody set aside.
    /// </summary>
    Task DeleteProjectAsync(Guid id, CancellationToken cancellationToken = default);
    Task AddStepAsync(ProjectStep step, CancellationToken cancellationToken = default);

    /// <summary>Several new steps in one write, in the order given.</summary>
    Task AddStepsAsync(IReadOnlyCollection<ProjectStep> steps, CancellationToken cancellationToken = default);

    /// <summary>A change the user made, written with a fresh stamp.</summary>
    Task UpdateStepAsync(ProjectStep step, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes steps exactly as given, stamps included. Bringing a step in line with its event is
    /// not the user changing it, and must not read as a change newer than the event's verdict.
    /// </summary>
    Task SaveStepsAsync(IReadOnlyCollection<ProjectStep> steps, CancellationToken cancellationToken = default);

    /// <summary>Removes a step and every tick it was given.</summary>
    Task DeleteStepAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Hands steps to another project, after the steps it already has, as open steps with no day
    /// and no slot: where they are going, they have not been planned yet.
    /// </summary>
    Task MoveStepsAsync(IReadOnlyCollection<Guid> stepIds, Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>The ticks given in these occurrences.</summary>
    Task<IReadOnlyList<ProjectStepCheck>> GetChecksAsync(IReadOnlyCollection<Guid> eventIds, CancellationToken cancellationToken = default);

    /// <summary>Ticks steps in one occurrence, all with one stamp. Steps already ticked there keep theirs.</summary>
    Task CheckAsync(Guid eventId, IReadOnlyCollection<Guid> stepIds, DateTimeOffset completedAtUtc, CancellationToken cancellationToken = default);

    /// <summary>Takes a tick back.</summary>
    Task UncheckAsync(Guid eventId, Guid stepId, CancellationToken cancellationToken = default);

    /// <summary>Takes back the ticks that were written together at <paramref name="completedAtUtc"/>.</summary>
    Task<int> UncheckBatchAsync(Guid eventId, DateTimeOffset completedAtUtc, CancellationToken cancellationToken = default);

    /// <summary>Forgets every tick given in an occurrence that no longer exists.</summary>
    Task ClearChecksAsync(IReadOnlyCollection<Guid> eventIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gives each project written before projects were attached to anything a task of its own, so
    /// it comes back as an unscheduled project rather than disappearing. Returns how many there were.
    /// </summary>
    Task<int> AdoptHostlessAsync(TimeZoneInfo zone, CancellationToken cancellationToken = default);
}

public interface IChatRepository
{
    Task<IReadOnlyList<ChatConversation>> GetConversationsAsync(bool includeArchived = false, CancellationToken cancellationToken = default);
    Task<ChatConversation?> GetConversationAsync(Guid id, CancellationToken cancellationToken = default);
    Task<ChatConversation> CreateConversationAsync(string title, CancellationToken cancellationToken = default);
    Task RenameConversationAsync(Guid id, string title, CancellationToken cancellationToken = default);
    Task SetConversationPinnedAsync(Guid id, bool pinned, CancellationToken cancellationToken = default);
    Task DeleteConversationAsync(Guid id, CancellationToken cancellationToken = default);
    Task DeleteAllConversationsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default);
    Task AppendMessageAsync(ChatMessage message, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ChatMessage>> SearchMessagesAsync(string query, int limit = 40, CancellationToken cancellationToken = default);
    /// <summary>Recent user turns across every thread; used to build follow-up suggestions.</summary>
    Task<IReadOnlyList<ChatMessage>> GetRecentUserMessagesAsync(int limit, CancellationToken cancellationToken = default);
}

public interface IUserMemoryRepository
{
    Task<IReadOnlyList<UserMemoryEntry>> GetAllAsync(CancellationToken cancellationToken = default);
    /// <summary>Inserts or replaces the entry with the same key and returns the stored row.</summary>
    Task<UserMemoryEntry> UpsertAsync(UserMemoryEntry entry, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task<bool> DeleteByKeyAsync(string key, CancellationToken cancellationToken = default);
    Task SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}

public interface ISchedulerDiagnosticsRepository
{
    Task SaveAsync(PlanningRequest request, ScheduleResult result, CancellationToken cancellationToken = default);
}

public interface ITaskExecutionRepository
{
    Task RecordAsync(TaskExecution execution, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TaskExecution>> GetRecentAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken = default);
}

public interface IAIService
{
    Task<TaskDraft> ParseTaskAsync(string input, UserPlanningContext context, CancellationToken cancellationToken = default);
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
}

public interface INotificationService
{
    /// <summary>
    /// Raises one reminder. <paramref name="eventId"/> is what the reminder is about, carried
    /// through so the surface that shows it can point at the thing itself rather than only
    /// repeating its name — the shell marks that event in the day when the reminder lands.
    /// </summary>
    Task<bool> ShowAsync(string title, string body, Guid? eventId = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One line of a tray menu, already worded in the user's language. The tray implementations
/// know how to draw a list; they know nothing about what is on it.
///
/// A line may carry a <see cref="Submenu"/>, which is how the menu stays short: the four
/// lengths a quiet spell can be set to are one line that opens rather than four lines that are
/// always there. <see cref="Detail"/> is the state that line is currently in, said on the line
/// itself so the submenu does not have to be opened to read it.
/// </summary>
public sealed record TrayMenuEntry(string Label, Action? Invoke = null, bool IsChecked = false, bool IsEnabled = true)
{
    public static TrayMenuEntry Separator { get; } = new(string.Empty) { IsSeparator = true };

    public bool IsSeparator { get; private init; }

    /// <summary>Secondary text at the end of the line: what the line is currently set to.</summary>
    public string? Detail { get; init; }

    /// <summary>The lines this one opens onto, or null for an ordinary line.</summary>
    public IReadOnlyList<TrayMenuEntry>? Submenu { get; init; }

    /// <summary>Whether a submenu should already be open when the menu appears.</summary>
    public bool IsExpanded { get; init; }
}

/// <summary>One shortcut, ready to be claimed from the rest of the desktop.</summary>
public readonly record struct GlobalHotkey(ShellShortcutAction Action, string Gesture, Action Invoke);

public interface IGlobalHotkeyService : IDisposable
{
    bool IsSupported { get; }

    /// <summary>
    /// Claims exactly this set and nothing else, replacing whatever was claimed before. Returns
    /// the actions the system refused — almost always because something else already holds the
    /// combination — so the settings page can say which row is not going to work rather than
    /// leaving the user pressing keys at nothing.
    /// </summary>
    IReadOnlyList<ShellShortcutAction> Apply(IReadOnlyList<GlobalHotkey> hotkeys);

    void Unregister();
}

public interface IAutostartService
{
    bool IsSupported { get; }
    Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default);
    Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
}

public interface IAppTrayService : IDisposable
{
    bool IsSupported { get; }

    /// <summary>
    /// Puts the icon in the notification area. The menu is asked for each time it is opened
    /// rather than built once, because what it offers depends on whether the shell is currently
    /// quiet and for how much longer.
    /// </summary>
    void Start(Action activate, Func<IReadOnlyList<TrayMenuEntry>> buildMenu);

    /// <summary>Called when the state behind the menu changed and a rebuilt one is wanted.</summary>
    void Refresh();
}

public interface IBackupService
{
    Task<string> CreateBackupAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> ListBackupsAsync(CancellationToken cancellationToken = default);
}
