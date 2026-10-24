using IntelligentScheduler.Domain;
using Xunit;

namespace IntelligentScheduler.Application.Tests;

public sealed class ProjectPlanningTests
{
    private static readonly DateOnly Today = new(2026, 9, 15);
    private static readonly DateOnly Tomorrow = Today.AddDays(1);

    private static ProjectStep Step(string title, int minutes, DateOnly? day, int order = 0, bool done = false)
        => new() { Title = title, DurationMinutes = minutes, PlannedDate = day, SortOrder = order, IsDone = done };

    private static DateTimeOffset Utc(int day, int hour, int minute = 0)
        => new(2026, 9, day, hour, minute, 0, TimeSpan.Zero);

    private static readonly Dictionary<Guid, EventOutcome> NoVerdicts = new();

    [Fact]
    public void DaysRunInDateOrderWithTheUndatedLast()
    {
        var steps = new[]
        {
            Step("later", 60, Today.AddDays(2), 0),
            Step("someday", 30, null, 1),
            Step("second today", 45, Today, 3),
            Step("first today", 90, Today, 2)
        };

        var days = ProjectSchedule.ByDay(steps);

        Assert.Equal(new DateOnly?[] { Today, Today.AddDays(2), null }, days.Select(day => day.Date));
        Assert.Equal(new[] { "first today", "second today" }, days[0].Steps.Select(step => step.Title));
        Assert.Equal(135, days[0].TotalMinutes);
    }

    [Fact]
    public void ProgressIsWeighedByLengthAndTheNextStepIsTheEarliestOpenOne()
    {
        var steps = new[]
        {
            Step("done long", 180, Today.AddDays(-1), 0, done: true),
            Step("open undated", 60, null, 1),
            Step("open today", 60, Today, 2)
        };

        var summary = ProjectSummary.Build(steps, Today, deadline: Today.AddDays(3));

        Assert.Equal(1, summary.DoneSteps);
        Assert.Equal(3, summary.TotalSteps);
        Assert.Equal(60.0, summary.Percent);
        Assert.Equal("open today", summary.Next?.Title);
        Assert.Equal(Today, summary.LastPlannedDate);
        Assert.False(summary.IsComplete);
        Assert.False(summary.IsOverdue);
    }

    [Fact]
    public void AProjectIsOverdueOnlyWhilePastItsDeadlineAndUnfinished()
    {
        var open = new[] { Step("a", 30, Today.AddDays(-2)) };
        var done = new[] { Step("a", 30, Today.AddDays(-2), done: true) };

        Assert.True(ProjectSummary.Build(open, Today, Today.AddDays(-1)).IsOverdue);
        Assert.False(ProjectSummary.Build(done, Today, Today.AddDays(-1)).IsOverdue);
        Assert.False(ProjectSummary.Build(open, Today, Today).IsOverdue);
        Assert.True(ProjectSummary.Build(done, Today, null).IsComplete);
        Assert.False(ProjectSummary.Build([], Today, null).IsComplete);
    }

    [Fact]
    public void AStepLetsGoOfAnEventThatWasDeleted()
    {
        var step = Step("a", 60, Today);
        step.EventId = Guid.NewGuid();

        var changed = ProjectStepReconciler.Reconcile([step], new Dictionary<Guid, CalendarEvent>(), NoVerdicts, TimeZoneInfo.Utc);

        Assert.Same(step, Assert.Single(changed));
        Assert.Null(step.EventId);
        Assert.Equal(Today, step.PlannedDate);
    }

    [Fact]
    public void ABookedStepFollowsItsEventToAnotherDayAndLength()
    {
        var booked = new CalendarEvent { Title = "a", StartUtc = Utc(17, 14), EndUtc = Utc(17, 15, 30) };
        var events = new Dictionary<Guid, CalendarEvent> { [booked.Id] = booked };
        var step = Step("a", 60, Today);
        step.EventId = booked.Id;

        Assert.Single(ProjectStepReconciler.Reconcile([step], events, NoVerdicts, TimeZoneInfo.Utc));
        Assert.Equal(new DateOnly(2026, 9, 17), step.PlannedDate);
        Assert.Equal(90, step.DurationMinutes);

        // Already in line: there is nothing to write the second time round.
        Assert.Empty(ProjectStepReconciler.Reconcile([step], events, NoVerdicts, TimeZoneInfo.Utc));
    }

    [Fact]
    public void DoneOnTheEventTicksTheStepUnlessTheStepWasChangedAfterwards()
    {
        var booked = new CalendarEvent { Title = "a", StartUtc = Utc(15, 10), EndUtc = Utc(15, 11) };
        var events = new Dictionary<Guid, CalendarEvent> { [booked.Id] = booked };
        var outcomes = new Dictionary<Guid, EventOutcome>
        {
            [booked.Id] = new()
            {
                EventId = booked.Id, Kind = EventOutcomeKind.Completed,
                FinishedAtUtc = Utc(15, 11), UpdatedAtUtc = Utc(15, 11)
            }
        };

        var linkedBefore = Step("a", 60, Today);
        linkedBefore.EventId = booked.Id;
        linkedBefore.UpdatedAtUtc = Utc(15, 9);
        ProjectStepReconciler.Reconcile([linkedBefore], events, outcomes, TimeZoneInfo.Utc);
        Assert.True(linkedBefore.IsDone);
        Assert.Equal(Utc(15, 11), linkedBefore.CompletedAtUtc);

        // Unticked after the verdict: the user's later word stands.
        var untickedAfter = Step("b", 60, Today);
        untickedAfter.EventId = booked.Id;
        untickedAfter.UpdatedAtUtc = Utc(15, 12);
        ProjectStepReconciler.Reconcile([untickedAfter], events, outcomes, TimeZoneInfo.Utc);
        Assert.False(untickedAfter.IsDone);
    }

    [Fact]
    public void ThePartlyAndSkippedVerdictsLeaveTheStepOpen()
    {
        var booked = new CalendarEvent { Title = "a", StartUtc = Utc(15, 10), EndUtc = Utc(15, 11) };
        var step = Step("a", 60, Today);
        step.EventId = booked.Id;
        step.UpdatedAtUtc = Utc(15, 9);

        ProjectStepReconciler.Reconcile([step],
            new Dictionary<Guid, CalendarEvent> { [booked.Id] = booked },
            new Dictionary<Guid, EventOutcome>
            {
                [booked.Id] = new() { EventId = booked.Id, Kind = EventOutcomeKind.Partial, UpdatedAtUtc = Utc(15, 11) }
            },
            TimeZoneInfo.Utc);

        Assert.False(step.IsDone);
    }

    [Fact]
    public void TheSlotIsTheFirstGapLongEnough()
    {
        var busy = new[] { (Utc(16, 9), Utc(16, 10)), (Utc(16, 10, 30), Utc(16, 12)) };

        Assert.Equal(TimeSpan.FromHours(10), StepSlotFinder.Suggest(Tomorrow, 30, busy, Utc(15, 8), TimeZoneInfo.Utc));
        Assert.Equal(TimeSpan.FromHours(12), StepSlotFinder.Suggest(Tomorrow, 60, busy, Utc(15, 8), TimeZoneInfo.Utc));
    }

    [Fact]
    public void AnEmptyDayStartsAtNineAndTodayNeverBeforeNow()
    {
        var none = Array.Empty<(DateTimeOffset, DateTimeOffset)>();

        Assert.Equal(TimeSpan.FromHours(9), StepSlotFinder.Suggest(Tomorrow, 60, none, Utc(15, 13, 7), TimeZoneInfo.Utc));
        Assert.Equal(new TimeSpan(13, 15, 0), StepSlotFinder.Suggest(Today, 60, none, Utc(15, 13, 7), TimeZoneInfo.Utc));
    }

    [Fact]
    public void AFullWorkingDayStillGetsASlotAfterWhatIsBooked()
    {
        var busy = new[] { (Utc(16, 9), Utc(16, 22)) };

        Assert.Equal(TimeSpan.FromHours(22), StepSlotFinder.Suggest(Tomorrow, 60, busy, Utc(15, 8), TimeZoneInfo.Utc));
    }

    [Fact]
    public void AStepThatDoesNotFitTheWorkingDayIsNoSuggestion()
    {
        Assert.True(StepSlotFinder.FitsWorkingDay(TimeSpan.FromHours(20), 90));
        Assert.False(StepSlotFinder.FitsWorkingDay(TimeSpan.FromHours(21), 90));
        Assert.False(StepSlotFinder.FitsWorkingDay(TimeSpan.FromHours(22.5), 15));
    }

    // ---- A sitting: an event broken into steps ------------------------------------------

    private static ProjectStep Line(string title, int minutes, int order) => new() { Title = title, DurationMinutes = minutes, SortOrder = order };

    private static EventOutcome Verdict(EventOutcomeKind kind, int percent = 0) => new() { Kind = kind, ProgressPercent = percent };

    [Fact]
    public void ASittingIsWeighedByLengthAndSaysWhenItsStepsDoNotFitTheEvent()
    {
        var steps = new[] { Line("bug", 20, 0), Line("fix", 45, 1), Line("test", 25, 2), Line("notes", 10, 3) };
        var ticked = new HashSet<Guid> { steps[0].Id, steps[1].Id };

        var sitting = SittingSummary.Build(steps, ticked, slotMinutes: 120);

        Assert.Equal(2, sitting.DoneSteps);
        Assert.Equal(2, sitting.OpenSteps);
        Assert.Equal(100, sitting.TotalMinutes);
        Assert.Equal(35, sitting.OpenMinutes);
        Assert.Equal(65, sitting.ProgressPercent);
        Assert.Equal(0, sitting.OverflowMinutes);
        Assert.False(sitting.IsComplete);

        var tight = SittingSummary.Build(steps, new HashSet<Guid>(), slotMinutes: 60);
        Assert.Equal(40, tight.OverflowMinutes);
        Assert.True(SittingSummary.Build(steps, steps.Select(step => step.Id).ToHashSet(), 120).IsComplete);
        Assert.False(SittingSummary.Build([], new HashSet<Guid>(), 120).IsComplete);
    }

    [Fact]
    public void TheLastTickAnswersDoneOnlyOnceTheEventHasBegun()
    {
        var done = new SittingSummary(3, 3, 60, 60, 60);

        Assert.Equal(SittingFollowUp.AnswerDone, SittingVerdictPolicy.AfterTick(done, null, hasBegun: true));
        Assert.Equal(SittingFollowUp.AnswerDone, SittingVerdictPolicy.AfterTick(done, Verdict(EventOutcomeKind.Started), true));
        // Answered short of done earlier, and finished since: the later word wins.
        Assert.Equal(SittingFollowUp.AnswerDone, SittingVerdictPolicy.AfterTick(done, Verdict(EventOutcomeKind.Partial, 40), true));
        Assert.Equal(SittingFollowUp.AnswerDone, SittingVerdictPolicy.AfterTick(done, Verdict(EventOutcomeKind.Skipped), true));
        // Already done, or ticked the evening before: nothing to say.
        Assert.Equal(SittingFollowUp.None, SittingVerdictPolicy.AfterTick(done, Verdict(EventOutcomeKind.Completed), true));
        Assert.Equal(SittingFollowUp.None, SittingVerdictPolicy.AfterTick(done, null, hasBegun: false));
    }

    [Fact]
    public void ATickTakenBackUnderDoneMakesItPartlyAndPartlyFollowsTheTicks()
    {
        var twoOfThree = new SittingSummary(3, 2, 60, 40, 60);

        Assert.Equal(SittingFollowUp.ReopenAsPartial, SittingVerdictPolicy.AfterTick(twoOfThree, Verdict(EventOutcomeKind.Completed), true));
        Assert.Equal(SittingFollowUp.FollowProgress, SittingVerdictPolicy.AfterTick(twoOfThree, Verdict(EventOutcomeKind.Partial, 50), true));
        Assert.Equal(SittingFollowUp.None, SittingVerdictPolicy.AfterTick(twoOfThree, Verdict(EventOutcomeKind.Partial, 67), true));
        Assert.Equal(SittingFollowUp.None, SittingVerdictPolicy.AfterTick(twoOfThree, null, true));
        Assert.Equal(SittingFollowUp.None, SittingVerdictPolicy.AfterTick(new SittingSummary(0, 0, 0, 0, 60), null, true));
    }

    [Fact]
    public void AnEventHasBegunFifteenMinutesBeforeItsStart()
    {
        var calendarEvent = new CalendarEvent { StartUtc = Utc(15, 10), EndUtc = Utc(15, 11) };

        Assert.True(SittingVerdictPolicy.HasBegun(calendarEvent, Utc(15, 9, 45)));
        Assert.False(SittingVerdictPolicy.HasBegun(calendarEvent, Utc(15, 9, 44)));
    }

    // ---- A task broken into steps ---------------------------------------------------------

    [Fact]
    public void ATaskCarriesItsProjectsLengthWhatIsLeftAndWhetherItIsDone()
    {
        var task = new TaskItem { Title = "Launch", EstimatedDurationMin = 30, RemainingDurationMin = 30 };
        var steps = new List<ProjectStep> { Step("a", 60, Today, 0, done: true), Step("b", 90, Today, 1) };

        Assert.True(ProjectTaskMirror.Apply(task, steps));
        Assert.Equal(150, task.EstimatedDurationMin);
        Assert.Equal(90, task.RemainingDurationMin);
        Assert.Equal(Domain.TaskStatus.Ready, task.Status);
        Assert.False(ProjectTaskMirror.Apply(task, steps));

        steps[1].IsDone = true;
        Assert.True(ProjectTaskMirror.Apply(task, steps));
        Assert.Equal(Domain.TaskStatus.Done, task.Status);
        Assert.Equal(0, task.RemainingDurationMin);

        steps.Add(Step("c", 30, null, 2));
        Assert.True(ProjectTaskMirror.Apply(task, steps));
        Assert.Equal(Domain.TaskStatus.Ready, task.Status);

        // A box opened on a task with nothing written in it yet leaves the task alone.
        Assert.False(ProjectTaskMirror.Apply(new TaskItem { Title = "Plain", EstimatedDurationMin = 45 }, []));
    }

    [Fact]
    public void ATaskMarkedDoneAfterItsStepsFinishesThemButAnOlderOneDoesNot()
    {
        var steps = new List<ProjectStep> { Step("a", 60, Today, 0, done: true), Step("b", 90, Today, 1) };
        foreach (var step in steps) step.UpdatedAtUtc = Utc(15, 9);

        var stale = new TaskItem { Title = "Launch", Status = Domain.TaskStatus.Done, UpdatedAtUtc = Utc(15, 8) };
        Assert.Empty(ProjectTaskMirror.FollowTask(stale, steps));

        var open = new TaskItem { Title = "Launch", Status = Domain.TaskStatus.Ready, UpdatedAtUtc = Utc(15, 10) };
        Assert.Empty(ProjectTaskMirror.FollowTask(open, steps));

        var done = new TaskItem { Title = "Launch", Status = Domain.TaskStatus.Done, UpdatedAtUtc = Utc(15, 10) };
        var changed = ProjectTaskMirror.FollowTask(done, steps);
        Assert.Equal("b", Assert.Single(changed).Title);
        Assert.True(steps[1].IsDone);
        Assert.Equal(Utc(15, 10), steps[1].CompletedAtUtc);
        Assert.Empty(ProjectTaskMirror.FollowTask(done, steps));
    }

    [Fact]
    public void TheLatestBatchIsWhatWasFinishedTogetherLast()
    {
        var steps = new[]
        {
            Step("early", 30, Today, 0, done: true),
            Step("batch one", 30, Today, 1, done: true),
            Step("batch two", 30, Today, 2, done: true),
            Step("open", 30, Today, 3)
        };
        steps[0].CompletedAtUtc = Utc(15, 9);
        steps[1].CompletedAtUtc = Utc(15, 12);
        steps[2].CompletedAtUtc = Utc(15, 12).AddMilliseconds(400);

        var latest = CompletionBatch.Latest(steps, step => step.CompletedAtUtc);

        Assert.Equal(new[] { "batch one", "batch two" }, latest.Select(step => step.Title));
        Assert.Empty(CompletionBatch.Latest(new[] { steps[3] }, step => step.CompletedAtUtc));
    }

    // ---- Hosts ------------------------------------------------------------------------------

    [Fact]
    public void AProjectWhoseTaskEventOrSeriesIsGoneIsAnOrphan()
    {
        var liveTask = Guid.NewGuid();
        var liveEvent = Guid.NewGuid();
        var liveSeries = Guid.NewGuid();
        var projects = new[]
        {
            new Project { Title = "task", TaskId = liveTask },
            new Project { Title = "gone task", TaskId = Guid.NewGuid() },
            new Project { Title = "event", EventId = liveEvent },
            new Project { Title = "gone event", EventId = Guid.NewGuid() },
            new Project { Title = "series", SeriesId = liveSeries },
            new Project { Title = "gone series", SeriesId = Guid.NewGuid() },
            new Project { Title = "from before hosts" }
        };

        var orphans = ProjectHosts.Orphaned(projects, new HashSet<Guid> { liveTask }, new HashSet<Guid> { liveEvent },
            new HashSet<Guid> { liveSeries });

        Assert.Equal(new[] { "gone task", "gone event", "gone series" }, orphans.Select(project => project.Title));
    }

    [Fact]
    public void AProjectFromBeforeHostsBecomesATaskDueAtTheEndOfItsDay()
    {
        var legacy = new Project { Title = " Launch ", Deadline = new DateOnly(2026, 9, 25) };
        var steps = new[] { Step("done", 60, Today, 0, done: true), Step("done too", 30, null, 1, done: true) };

        var task = ProjectHosts.AdoptingTask(legacy, steps, TimeZoneInfo.Utc);

        Assert.Equal("Launch", task.Title);
        Assert.Equal(new DateTimeOffset(2026, 9, 25, 23, 59, 0, TimeSpan.Zero), task.DeadlineUtc);
        Assert.Equal(90, task.EstimatedDurationMin);
        Assert.Equal(Domain.TaskStatus.Done, task.Status);
        Assert.Null(ProjectHosts.AdoptingTask(new Project { Title = "No date" }, [], TimeZoneInfo.Utc).DeadlineUtc);
    }
}
