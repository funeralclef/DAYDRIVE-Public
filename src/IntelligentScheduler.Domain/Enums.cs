namespace IntelligentScheduler.Domain;

public enum TaskStatus { Inbox, Ready, Scheduled, InProgress, Done, Cancelled }
public enum BlockState { Planned, InProgress, Completed, Skipped, Cancelled }
public enum BlockOrigin { Solver, Manual, External }
public enum TaskScheduleExclusionKind { ManualUnschedule, NotToday }
public enum PlanStatus { Optimal, Feasible, Partial, Infeasible, Error }
public enum UnscheduledReason { NoCapacity, DeadlineImpossible, ConflictingLock, OutsideAvailability }
public enum AvailabilityExceptionKind { Available, Unavailable }
public enum PreferredTimeOfDay { Any, Morning, Afternoon, Evening }
/// <summary>
/// What kind of thing an event is. Nine of them, and each one earns its place by being a
/// different answer to "why is this in my day" rather than a finer shade of an answer already
/// here: work and study are both effort but they compete for different hours; health is what
/// the body needs done and rest is what it needs left alone; errands are the small obligations
/// that pile up, travel is the time between two other things, social is other people.
///
/// The names are stored, not the numbers (see <c>AppDbContext</c>), so this list may grow at
/// either end without disturbing a calendar somebody already has.
/// </summary>
public enum EventCategory { General, Work, Personal, Study, Health, Social, Errands, Travel, Rest }
public enum EventPriority { Normal, High, Critical }
public enum EventFlexibility { Fixed, ProposeMove, Automatic }
public enum EventRecurrence { None, Daily, Weekdays, Weekly, Monthly }
public enum EventSeriesScope { Occurrence, ThisAndFollowing, Series }
public enum AiProviderMode { LocalParser, Disabled }
public enum AssistantProvider { Local, Ollama, OpenAiCompatible, Mcp }
public enum AssistantAccessLevel { ConfirmAll, AutoSafe, FullAccess }
public enum ShellTheme
{
    // Legacy names remain readable in old databases/backups. MARELIAN is the only active theme.
    Celestia = 0,
    Noctalia = 1,
    Windows11 = 2,
    Marelian = 3,
    // Kept as a distinct legacy value so old serialized settings can be migrated deterministically.
    [Obsolete("Use Marelian.")]
    Custom = 4,
    Windows10 = 5
}
public enum ShellThemeMode { Dark, Light }
public enum ThemeColorSource { Preset, Custom, SystemAccent, Wallpaper }
public enum VisualPalette { Vibrant, Accent, Monochrome, Pastel, Earth, Neon, ColorBlind }
public enum EdgeRevealMode { Hover, Click }
public enum ChatRole { User, Assistant, System }
public enum UserMemoryCategory { General, Preference, Routine, Constraint, Goal, Context }
public enum AnalyticsRange { Week, Month, Quarter }
/// <summary>Where the application mirrors its data for another machine to pick up.</summary>
public enum CloudSyncProvider { None, Folder, GoogleDrive }
/// <summary>
/// What a sync attempt did. The last three are journal entries rather than transfers: a staged
/// download taking effect at launch, and a Google account being connected or let go.
/// </summary>
public enum CloudSyncOutcome { UpToDate, Uploaded, Downloaded, Conflict, NotConfigured, Failed, Applied, Connected, Disconnected }
/// <summary>How a calendar event actually went.</summary>
public enum EventOutcomeKind { Started, Completed, Partial, Skipped }
/// <summary>
/// Why an event keeps being pushed onto another day. Asked for only once pushing it has
/// become a habit: one postponement is a bad day, a third is a problem with the task itself.
/// </summary>
public enum EventBlocker { None, NotEnoughTime, TooBig, UnclearStart, NotRelevant }
