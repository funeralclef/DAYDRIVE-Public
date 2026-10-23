using IntelligentScheduler.Domain;
using DomainTaskStatus = IntelligentScheduler.Domain.TaskStatus;

namespace IntelligentScheduler.Application;

/// <summary>One day of a project: the steps planned for it, in the order they were written.</summary>
public sealed record ProjectDay(DateOnly? Date, IReadOnlyList<ProjectStep> Steps)
{
    public int TotalMinutes => Steps.Sum(step => step.DurationMinutes);
    public bool HasOpenSteps => Steps.Any(step => !step.IsDone);
}

/// <summary>What an unscheduled project comes to, read off its steps.</summary>
public sealed record ProjectSummary(
    int TotalSteps,
    int DoneSteps,
    int TotalMinutes,
    int DoneMinutes,
    ProjectStep? Next,
    DateOnly? LastPlannedDate,
    bool IsComplete,
    bool IsOverdue)
{
    public int OpenMinutes => TotalMinutes - DoneMinutes;

    /// <summary>
    /// How much of the planned time is done, 0..100. Weighed by length rather than counted by
    /// steps: ticking off a quarter-hour step moves the bar a little, a whole afternoon moves it
    /// a lot, which is what "how far along is it" actually means.
    /// </summary>
    public double Percent => TotalMinutes == 0 ? 0 : Math.Round(100.0 * DoneMinutes / TotalMinutes, 1);

    public static ProjectSummary Build(IReadOnlyCollection<ProjectStep> steps, DateOnly today, DateOnly? deadline)
    {
        var open = steps.Where(step => !step.IsDone).ToList();
        var complete = steps.Count > 0 && open.Count == 0;
        DateOnly? last = null;
        foreach (var step in steps)
            if (step.PlannedDate is { } date && (last is null || date > last)) last = date;

        return new(
            steps.Count,
            steps.Count - open.Count,
            steps.Sum(step => step.DurationMinutes),
            steps.Where(step => step.IsDone).Sum(step => step.DurationMinutes),
            ProjectSchedule.Order(open).FirstOrDefault(),
            last,
            complete,
            deadline is { } due && due < today && !complete);
    }
}

/// <summary>
/// What one sitting of a scheduled project comes to: the steps of the event's checklist, how many
/// of them were ticked in this occurrence, and how they sit in the time the event gives them.
/// </summary>
public sealed record SittingSummary(int TotalSteps, int DoneSteps, int TotalMinutes, int DoneMinutes, int SlotMinutes)
{
    public int OpenSteps => TotalSteps - DoneSteps;
    public int OpenMinutes => TotalMinutes - DoneMinutes;
    public bool IsComplete => TotalSteps > 0 && DoneSteps == TotalSteps;

    /// <summary>
    /// How far the sitting got, 0..100, by length like an unscheduled project's bar. This is the
    /// figure a "partly" answer is offered with: the steps already say how much of the work the
    /// hour covered, which is the one thing that answer asks for.
    /// </summary>
    public int ProgressPercent => TotalSteps == 0 ? 0
        : TotalMinutes == 0 ? (int)Math.Round(100.0 * DoneSteps / TotalSteps)
        : (int)Math.Round(100.0 * DoneMinutes / TotalMinutes);

    /// <summary>How much more the steps ask for than the event is long; zero while they fit.</summary>
    public int OverflowMinutes => SlotMinutes <= 0 ? 0 : Math.Max(0, TotalMinutes - SlotMinutes);

    public static SittingSummary Build(IEnumerable<ProjectStep> steps, IReadOnlySet<Guid> done, int slotMinutes)
    {
        var total = 0;
        var doneCount = 0;
        var minutes = 0;
        var doneMinutes = 0;
        foreach (var step in steps)
        {
            total++;
            minutes += step.DurationMinutes;
            if (!done.Contains(step.Id)) continue;
            doneCount++;
            doneMinutes += step.DurationMinutes;
        }

        return new(total, doneCount, minutes, doneMinutes, slotMinutes);
    }
}

/// <summary>What ticking a step of a scheduled project says about the event it is part of.</summary>
public enum SittingFollowUp
{
    None,

    /// <summary>The last open step was ticked: the event is done, and is marked so.</summary>
    AnswerDone,

    /// <summary>A step was unticked under a "done": the event is only partly done after all.</summary>
    ReopenAsPartial,

    /// <summary>A "partly" stands, and its figure follows the ticks.</summary>
    FollowProgress
}

/// <summary>
/// Keeps a scheduled project's ticks and its event's verdict one answer rather than two.
///
/// The event asks "how did it go?" and the checklist is the most exact answer there is, so the two
/// are never allowed to disagree: ticking the last step is saying "done", taking a tick back under a
/// "done" makes it "partly", and a "partly" carries the share the ticks come to. Only an event that
/// has begun is answered this way — ticking tomorrow's list tonight is preparing, not reporting.
/// </summary>
public static class SittingVerdictPolicy
{
    /// <summary>How long before its start an event can already be answered for, as everywhere else in the shell.</summary>
    public static readonly TimeSpan Leeway = TimeSpan.FromMinutes(15);

    public static bool HasBegun(CalendarEvent calendarEvent, DateTimeOffset now) => calendarEvent.StartUtc <= now + Leeway;

    public static SittingFollowUp AfterTick(SittingSummary sitting, EventOutcome? outcome, bool hasBegun)
    {
        if (sitting.TotalSteps == 0) return SittingFollowUp.None;
        if (sitting.IsComplete)
            return hasBegun && outcome is not { Kind: EventOutcomeKind.Completed } ? SittingFollowUp.AnswerDone : SittingFollowUp.None;
        return outcome?.Kind switch
        {
            EventOutcomeKind.Completed => SittingFollowUp.ReopenAsPartial,
            EventOutcomeKind.Partial when outcome.ProgressPercent != sitting.ProgressPercent => SittingFollowUp.FollowProgress,
            _ => SittingFollowUp.None
        };
    }
}

/// <summary>
/// Keeps a task that has been broken into steps in line with them. The task is still what the
/// rest of the shell — the assistant, the counts, the order of the list — reads, so it carries the
/// project's figures: its length is the steps' total, what is left of it is what the open steps
/// add up to, and it is done exactly when every step is.
/// </summary>
public static class ProjectTaskMirror
{
    /// <summary>Brings <paramref name="task"/> up to date in place, and says whether anything changed.</summary>
    public static bool Apply(TaskItem task, IReadOnlyCollection<ProjectStep> steps)
    {
        // A project with nothing in it yet says nothing about the task it was opened on.
        if (steps.Count == 0) return false;
        var total = Math.Max(1, steps.Sum(step => step.DurationMinutes));
        var open = steps.Where(step => !step.IsDone).Sum(step => step.DurationMinutes);
        var complete = steps.All(step => step.IsDone);
        var status = complete ? DomainTaskStatus.Done
            : task.Status == DomainTaskStatus.Done ? DomainTaskStatus.Ready
            : task.Status;

        if (task.EstimatedDurationMin == total && task.RemainingDurationMin == open && task.Status == status) return false;
        task.EstimatedDurationMin = total;
        task.RemainingDurationMin = open;
        task.Status = status;
        return true;
    }

    /// <summary>
    /// The other direction, for a task marked done after its steps last changed — by the
    /// assistant, say. That word is the later one, so the steps follow it rather than the task
    /// being put back: what was left is finished, with the task's own stamp. Returns the steps it
    /// changed, to be written back.
    ///
    /// Only "done" is followed. A task that is not done while all its steps are is what this
    /// class itself writes for a moment whenever a step is deleted or a slot's verdict ticks one,
    /// and the task's stamp cannot tell that apart from somebody reopening it.
    /// </summary>
    public static IReadOnlyList<ProjectStep> FollowTask(TaskItem task, IReadOnlyCollection<ProjectStep> steps)
    {
        if (task.Status != DomainTaskStatus.Done || steps.Count == 0
            || task.UpdatedAtUtc <= steps.Max(step => step.UpdatedAtUtc)) return [];
        var changed = steps.Where(step => !step.IsDone).ToList();
        foreach (var step in changed)
        {
            step.IsDone = true;
            step.CompletedAtUtc = task.UpdatedAtUtc;
            step.UpdatedAtUtc = task.UpdatedAtUtc;
        }

        return changed;
    }
}

/// <summary>
/// The things that were finished together most recently. Marking a whole project done ticks what
/// was left in one go, and taking that back should untick exactly those — not every step that was
/// ever ticked, and not only the last one of the batch.
/// </summary>
public static class CompletionBatch
{
    /// <summary>Items stamped within this much of the newest stamp count as one batch.</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(1);

    public static IReadOnlyList<T> Latest<T>(IEnumerable<T> items, Func<T, DateTimeOffset?> stamp)
    {
        var stamped = items.Select(item => (Item: item, At: stamp(item))).Where(pair => pair.At is not null).ToList();
        if (stamped.Count == 0) return [];
        var newest = stamped.Max(pair => pair.At!.Value);
        return [.. stamped.Where(pair => newest - pair.At!.Value <= Tolerance).Select(pair => pair.Item)];
    }
}

/// <summary>
/// Finds the projects whose host is gone. A project is the steps of something; once that thing has
/// been deleted — by the shell, by the assistant, by a sync from another machine — there is no row
/// left to show the steps in, and nothing the user could open to get them back.
/// </summary>
public static class ProjectHosts
{
    public static IReadOnlyList<Project> Orphaned(
        IEnumerable<Project> projects,
        IReadOnlySet<Guid> liveTasks,
        IReadOnlySet<Guid> liveEvents,
        IReadOnlySet<Guid> liveSeries)
        => [.. projects.Where(project => project.Host switch
        {
            ProjectHost.Task => !liveTasks.Contains(project.TaskId!.Value),
            ProjectHost.Event => !liveEvents.Contains(project.EventId!.Value),
            ProjectHost.Series => !liveSeries.Contains(project.SeriesId!.Value),
            _ => false
        })];

    /// <summary>
    /// Where a project written before hosts existed lands: a task of its own, due at the end of the
    /// day the project was due on — the moment a task's deadline picked from the calendar means.
    /// </summary>
    public static TaskItem AdoptingTask(Project project, IReadOnlyCollection<ProjectStep> steps, TimeZoneInfo zone)
    {
        var task = new TaskItem
        {
            Title = string.IsNullOrWhiteSpace(project.Title) ? "Project" : project.Title.Trim(),
            Status = DomainTaskStatus.Ready,
            Priority = 50,
            DeadlineUtc = project.Deadline is { } due ? EndOfDay(due, zone) : null,
            CreatedAtUtc = project.CreatedAtUtc,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        ProjectTaskMirror.Apply(task, steps);
        return task;
    }

    private static DateTimeOffset EndOfDay(DateOnly date, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(new TimeOnly(23, 59)), DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }
}

public static class ProjectSchedule
{
    /// <summary>
    /// Steps in the order they are worked through: by day, the undated ones last, and within a
    /// day in the order they were written.
    /// </summary>
    public static IEnumerable<ProjectStep> Order(IEnumerable<ProjectStep> steps)
        => steps.OrderBy(step => step.PlannedDate is null)
            .ThenBy(step => step.PlannedDate)
            .ThenBy(step => step.SortOrder)
            .ThenBy(step => step.CreatedAtUtc);

    /// <summary>The project laid out day by day — the shape the user plans it in.</summary>
    public static IReadOnlyList<ProjectDay> ByDay(IEnumerable<ProjectStep> steps)
        => [.. Order(steps).GroupBy(step => step.PlannedDate).Select(group => new ProjectDay(group.Key, [.. group]))];
}

/// <summary>
/// Keeps each step in line with the event booked for it.
///
/// A booked step's day and length are its event's: the event is the more concrete of the two,
/// and it is the one the user moves around the calendar — so a step never claims a day its slot
/// has already left. And marking the event done is marking the step done, because answering for
/// the same piece of work twice is exactly the chore the day's own verdicts were built to avoid.
/// </summary>
public static class ProjectStepReconciler
{
    /// <summary>
    /// Brings <paramref name="steps"/> up to date in place and returns the ones that changed, so
    /// only those are written back.
    /// </summary>
    public static IReadOnlyList<ProjectStep> Reconcile(
        IEnumerable<ProjectStep> steps,
        IReadOnlyDictionary<Guid, CalendarEvent> events,
        IReadOnlyDictionary<Guid, EventOutcome> outcomes,
        TimeZoneInfo zone)
    {
        var changed = new List<ProjectStep>();
        foreach (var step in steps)
        {
            if (step.EventId is not { } eventId) continue;
            if (!events.TryGetValue(eventId, out var booked))
            {
                // The slot was deleted. The step goes back to being planned for its day.
                step.EventId = null;
                changed.Add(step);
                continue;
            }

            var dirty = false;
            var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(booked.StartUtc, zone).Date);
            if (step.PlannedDate != day)
            {
                step.PlannedDate = day;
                dirty = true;
            }

            var minutes = (int)Math.Round((booked.EndUtc - booked.StartUtc).TotalMinutes);
            if (minutes > 0 && step.DurationMinutes != minutes)
            {
                step.DurationMinutes = minutes;
                dirty = true;
            }

            // A verdict older than the step's own last change is not taken: that change is the
            // user saying otherwise — unticking a step the event called done, for instance.
            if (!step.IsDone && outcomes.TryGetValue(eventId, out var outcome)
                && outcome.Kind == EventOutcomeKind.Completed && outcome.UpdatedAtUtc > step.UpdatedAtUtc)
            {
                step.IsDone = true;
                step.CompletedAtUtc = outcome.FinishedAtUtc ?? outcome.UpdatedAtUtc;
                dirty = true;
            }

            if (dirty) changed.Add(step);
        }

        return changed;
    }
}

/// <summary>
/// The time a step is first offered at when it is put on the calendar: the earliest gap long
/// enough for it in the working part of the day, and never a moment that has already passed.
/// One suggestion rather than a list — the composer it lands in lets the user move it.
/// </summary>
public static class StepSlotFinder
{
    public static readonly TimeSpan DayStart = TimeSpan.FromHours(9);
    public static readonly TimeSpan DayEnd = TimeSpan.FromHours(22);

    public static TimeSpan Suggest(
        DateOnly date,
        int durationMinutes,
        IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> busy,
        DateTimeOffset now,
        TimeZoneInfo zone)
    {
        var length = TimeSpan.FromMinutes(Math.Max(5, durationMinutes));
        var day = TimeSpan.FromDays(1);
        var from = DayStart;
        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        if (DateOnly.FromDateTime(localNow.Date) == date)
        {
            var soon = RoundUp(localNow.TimeOfDay, 15);
            if (soon > from) from = soon;
        }

        var midnight = date.ToDateTime(TimeOnly.MinValue);
        var candidate = from;
        foreach (var (start, end) in busy
                     .Select(item => (Start: TimeZoneInfo.ConvertTime(item.Start, zone).DateTime - midnight,
                                      End: TimeZoneInfo.ConvertTime(item.End, zone).DateTime - midnight))
                     .Where(item => item.End > item.Start)
                     .OrderBy(item => item.Start))
        {
            if (end <= candidate) continue;
            if (start >= candidate + length) break;
            candidate = end;
        }

        // Nothing fitting before the evening is still better placed after what is booked than on
        // top of it — as long as it ends on the day it was asked for.
        if (candidate + length <= day) return candidate;
        var latest = day - length;
        if (latest < TimeSpan.Zero) latest = TimeSpan.Zero;
        return from <= latest ? from : latest;
    }

    /// <summary>
    /// Whether a suggestion is a real one: it ends inside the working part of the day. What is
    /// left of a sitting is offered for later today only when today still has room for it, and
    /// for tomorrow otherwise — an eleven-at-night slot is a suggestion nobody takes.
    /// </summary>
    public static bool FitsWorkingDay(TimeSpan start, int durationMinutes)
        => start >= DayStart && start + TimeSpan.FromMinutes(Math.Max(5, durationMinutes)) <= DayEnd;

    private static TimeSpan RoundUp(TimeSpan value, int minutes)
        => TimeSpan.FromMinutes(Math.Ceiling(value.TotalMinutes / minutes) * minutes);
}
