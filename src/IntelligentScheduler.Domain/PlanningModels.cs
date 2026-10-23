namespace IntelligentScheduler.Domain;

public sealed record AvailabilityWindow(DateTimeOffset StartUtc, DateTimeOffset EndUtc);

public sealed record PlanningRequest(
    DateTimeOffset HorizonStartUtc,
    DateTimeOffset HorizonEndUtc,
    string TimeZoneId,
    IReadOnlyList<TaskItem> Tasks,
    IReadOnlyList<CalendarEvent> FixedEvents,
    IReadOnlyList<TimeBlock> ExistingBlocks,
    IReadOnlyList<AvailabilityWindow> AvailabilityWindows,
    int SchedulingUnitMinutes = 5,
    int MaxSolveTimeMs = 1200,
    int BufferBeforeFixedMin = 5,
    int BufferAfterFixedMin = 5,
    IReadOnlyList<TaskScheduleExclusion>? TaskExclusions = null);

public sealed record UnscheduledTask(Guid TaskId, string Title, int MissingMinutes, UnscheduledReason ReasonCode);

public sealed record TaskBlockCompletionResult(int PlannedMinutes, int ActualMinutes, int RemainingTaskMinutes, bool TaskCompleted);

public sealed record ScheduleResult(
    PlanStatus Status,
    Guid RevisionId,
    IReadOnlyList<TimeBlock> PlannedBlocks,
    IReadOnlyList<UnscheduledTask> UnscheduledTasks,
    long ObjectiveScore,
    long SolveDurationMs,
    IReadOnlyList<string> ExplanationHints);

public sealed record TaskDraftConfidence(double Duration, double Deadline, double Priority);

public sealed record TaskDraft(
    string Title,
    int EstimatedDurationMin,
    DateTimeOffset? DeadlineLocal,
    int Priority,
    bool Splittable,
    int MinBlockMin,
    PreferredTimeOfDay PreferredTimeOfDay,
    TaskDraftConfidence Confidence,
    IReadOnlyList<string> Assumptions);

public sealed record UserPlanningContext(DateTimeOffset NowLocal, string TimeZoneId, int DefaultDurationMin = 30);
