namespace IntelligentScheduler.Domain;

/// <summary>
/// The steps a piece of work is written down as, attached to the thing that work already is.
///
/// A project is not a third kind of item beside events and tasks: it is one of them, broken into
/// steps. Which one it belongs to decides what a step means. On an unscheduled task it is a part of
/// a larger job, with a length and a day of its own, booked at an hour when the user gets to it. On
/// a calendar event it is a part of that sitting — a line of the checklist the hour is spent on —
/// and its ticks are the event's own answer to "how did it go". A repeating event carries one list
/// for the whole series, ticked afresh on every occurrence.
///
/// Exactly one of <see cref="TaskId"/>, <see cref="EventId"/> and <see cref="SeriesId"/> is set.
/// The title, the deadline and (for an event) the colour are the host's, read from it rather than
/// copied; the fields of those names here are what a project written before this change carried,
/// kept so it can be handed to a task of its own on the first launch after it.
/// </summary>
public sealed class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>What a project from before hosts existed was called. The host's own title is the one shown.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// The colour an unscheduled project is drawn in, and the category its booked steps take on the
    /// calendar. A task has no category of its own; an event does, and a project on one uses it.
    /// </summary>
    public EventCategory Category { get; set; } = EventCategory.Work;

    /// <summary>The day a project from before hosts existed was due. The task's own deadline is the one used.</summary>
    public DateOnly? Deadline { get; set; }

    /// <summary>The unscheduled task these steps break up.</summary>
    public Guid? TaskId { get; set; }

    /// <summary>The one calendar event these steps are the checklist of.</summary>
    public Guid? EventId { get; set; }

    /// <summary>The repeating series these steps are the checklist of, every occurrence of it.</summary>
    public Guid? SeriesId { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public ProjectHost Host => TaskId is not null ? ProjectHost.Task
        : EventId is not null ? ProjectHost.Event
        : SeriesId is not null ? ProjectHost.Series
        : ProjectHost.None;

    /// <summary>Steps inside the time of a calendar event rather than spread over days.</summary>
    public bool IsScheduled => Host is ProjectHost.Event or ProjectHost.Series;
}

/// <summary>What a project's steps are attached to.</summary>
public enum ProjectHost { None, Task, Event, Series }

/// <summary>
/// One step: what to do and how long it takes.
///
/// On an unscheduled project it also carries a day — a plan rather than a booking, which does not
/// occupy the calendar — and, once it is given an hour, the ordinary event booked for it. From then
/// on that event is the step's slot: the step's day and length are read from it, so the two can
/// never disagree. On a scheduled project the day and the slot stay empty, because the step already
/// lives inside an hour, and whether it is done is kept per sitting in <see cref="ProjectStepCheck"/>.
/// </summary>
public sealed class ProjectStep
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string Title { get; set; } = string.Empty;
    public int DurationMinutes { get; set; } = 60;

    /// <summary>The day the step is planned for, or null while it has not been given one.</summary>
    public DateOnly? PlannedDate { get; set; }

    /// <summary>Where it sits among its project's steps: the order they were written in.</summary>
    public int SortOrder { get; set; }

    /// <summary>Whether an unscheduled project's step is done. A scheduled one's ticks are per sitting.</summary>
    public bool IsDone { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }

    /// <summary>The calendar event booked for this step, once it has been given an hour.</summary>
    public Guid? EventId { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A step of a scheduled project ticked off in one particular sitting.
///
/// Kept apart from the step because the step can be ticked more than once: a checklist on a
/// weekly review is the same list every Friday, and last Friday's ticks are not this Friday's. A
/// one-off event is simply the series with one sitting in it.
/// </summary>
public sealed class ProjectStepCheck
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StepId { get; set; }

    /// <summary>The occurrence the step was done in.</summary>
    public Guid EventId { get; set; }

    /// <summary>
    /// When it was ticked. Ticks written together share one stamp — "done" on the event ticks what
    /// was left all at once — which is how taking that answer back finds exactly those again.
    /// </summary>
    public DateTimeOffset CompletedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
