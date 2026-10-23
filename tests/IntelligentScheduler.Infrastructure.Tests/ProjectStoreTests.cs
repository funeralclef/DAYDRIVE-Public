using IntelligentScheduler.Application;
using IntelligentScheduler.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IntelligentScheduler.Infrastructure.Tests;

/// <summary>
/// Projects are three small tables and bare links into the calendar and the task list. Covered:
/// the round trip with its dates, a project belonging to exactly one host, the cascade that has to
/// hold without SQLite's own foreign keys, the reconciled write that keeps its stamp, the ticks a
/// scheduled project's steps get per sitting, steps handed from one project to another, the
/// projects from before hosts existed getting tasks of their own, and finding booked events and
/// repeating series wherever they are.
/// </summary>
public sealed class ProjectStoreTests
{
    private static Project OnTask(string title = "Project", EventCategory category = EventCategory.Work)
        => new() { Title = title, Category = category, TaskId = Guid.NewGuid() };

    [Fact]
    public async Task StepsComeBackWithTheirDaysAndInTheOrderTheyWereWritten()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        IProjectRepository projects = new ProjectStore(harness.ContextFactory);
        var project = OnTask("  Launch  ", EventCategory.Study);
        await projects.AddProjectAsync(project, cancellationToken);
        await projects.AddStepAsync(new ProjectStep
        {
            ProjectId = project.Id, Title = "Second", SortOrder = 1,
            PlannedDate = new DateOnly(2026, 9, 17), DurationMinutes = 90
        }, cancellationToken);
        await projects.AddStepAsync(new ProjectStep { ProjectId = project.Id, Title = "First", SortOrder = 0 }, cancellationToken);

        var stored = Assert.Single(await projects.GetProjectsAsync(cancellationToken));
        Assert.Equal("Launch", stored.Title);
        Assert.Equal(EventCategory.Study, stored.Category);
        Assert.Equal(project.TaskId, stored.TaskId);
        Assert.Equal(ProjectHost.Task, stored.Host);

        var steps = await projects.GetStepsAsync(cancellationToken);
        Assert.Equal(new[] { "First", "Second" }, steps.Select(step => step.Title));
        Assert.Null(steps[0].PlannedDate);
        Assert.Equal(new DateOnly(2026, 9, 17), steps[1].PlannedDate);
        Assert.Equal(90, steps[1].DurationMinutes);
    }

    [Fact]
    public async Task AProjectBelongsToExactlyOneHost()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        IProjectRepository projects = new ProjectStore(harness.ContextFactory);

        await Assert.ThrowsAsync<ArgumentException>(() => projects.AddProjectAsync(new Project { Title = "Nowhere" }, cancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => projects.AddProjectAsync(
            new Project { Title = "Both", TaskId = Guid.NewGuid(), EventId = Guid.NewGuid() }, cancellationToken));

        var sitting = new Project { Title = "Review", SeriesId = Guid.NewGuid() };
        await projects.AddProjectAsync(sitting, cancellationToken);
        sitting.SeriesId = null;
        sitting.EventId = Guid.NewGuid();
        await projects.UpdateProjectAsync(sitting, cancellationToken);

        var stored = Assert.Single(await projects.GetProjectsAsync(cancellationToken));
        Assert.Equal(ProjectHost.Event, stored.Host);
        Assert.True(stored.IsScheduled);
        Assert.Null(stored.SeriesId);
    }

    [Fact]
    public async Task DeletingAProjectTakesItsStepsAndTheirTicksWithIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        IProjectRepository projects = new ProjectStore(harness.ContextFactory);
        var kept = new Project { Title = "Kept", EventId = Guid.NewGuid() };
        var removed = new Project { Title = "Removed", EventId = Guid.NewGuid() };
        await projects.AddProjectAsync(kept, cancellationToken);
        await projects.AddProjectAsync(removed, cancellationToken);
        var stays = new ProjectStep { ProjectId = kept.Id, Title = "Stays" };
        var goes = new ProjectStep { ProjectId = removed.Id, Title = "Goes" };
        await projects.AddStepsAsync([stays, goes], cancellationToken);
        await projects.CheckAsync(kept.EventId!.Value, [stays.Id], DateTimeOffset.UtcNow, cancellationToken);
        await projects.CheckAsync(removed.EventId!.Value, [goes.Id], DateTimeOffset.UtcNow, cancellationToken);

        await projects.DeleteProjectAsync(removed.Id, cancellationToken);

        Assert.Equal("Kept", Assert.Single(await projects.GetProjectsAsync(cancellationToken)).Title);
        Assert.Equal("Stays", Assert.Single(await projects.GetStepsAsync(cancellationToken)).Title);
        Assert.Single(await projects.GetChecksAsync([kept.EventId!.Value, removed.EventId!.Value], cancellationToken));
    }

    [Fact]
    public async Task AStepCannotBeWrittenIntoAProjectThatIsGone()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        IProjectRepository projects = new ProjectStore(harness.ContextFactory);

        await Assert.ThrowsAsync<InvalidOperationException>(() => projects.AddStepAsync(
            new ProjectStep { ProjectId = Guid.NewGuid(), Title = "Orphan" }, cancellationToken));
    }

    [Fact]
    public async Task AReconciledWriteKeepsItsStampAndAUserEditTakesANewOne()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        IProjectRepository projects = new ProjectStore(harness.ContextFactory);
        var project = OnTask("P");
        await projects.AddProjectAsync(project, cancellationToken);
        await projects.AddStepAsync(new ProjectStep { ProjectId = project.Id, Title = "Step" }, cancellationToken);

        var step = Assert.Single(await projects.GetStepsAsync(cancellationToken));
        var stamp = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        step.IsDone = true;
        step.UpdatedAtUtc = stamp;
        await projects.SaveStepsAsync([step], cancellationToken);

        var reconciled = Assert.Single(await projects.GetStepsAsync(cancellationToken));
        Assert.True(reconciled.IsDone);
        Assert.Equal(stamp, reconciled.UpdatedAtUtc);

        await projects.UpdateStepAsync(reconciled, cancellationToken);
        Assert.True(Assert.Single(await projects.GetStepsAsync(cancellationToken)).UpdatedAtUtc > stamp);
    }

    [Fact]
    public async Task TicksAreKeptPerSittingAndADoneTakenBackTakesOnlyItsOwnBatch()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        IProjectRepository projects = new ProjectStore(harness.ContextFactory);
        var review = new Project { Title = "Weekly review", SeriesId = Guid.NewGuid() };
        await projects.AddProjectAsync(review, cancellationToken);
        var inbox = new ProjectStep { ProjectId = review.Id, Title = "Inbox", SortOrder = 0 };
        var week = new ProjectStep { ProjectId = review.Id, Title = "Look back", SortOrder = 1 };
        var plan = new ProjectStep { ProjectId = review.Id, Title = "Plan", SortOrder = 2 };
        await projects.AddStepsAsync([inbox, week, plan], cancellationToken);
        var thisFriday = Guid.NewGuid();
        var lastFriday = Guid.NewGuid();
        var ticked = new DateTimeOffset(2026, 9, 18, 17, 10, 0, TimeSpan.Zero);
        var done = new DateTimeOffset(2026, 9, 18, 17, 40, 0, TimeSpan.Zero);

        await projects.CheckAsync(lastFriday, [inbox.Id, week.Id, plan.Id], done.AddDays(-7), cancellationToken);
        await projects.CheckAsync(thisFriday, [inbox.Id], ticked, cancellationToken);
        // Ticking again is still ticked once, and keeps the first stamp.
        await projects.CheckAsync(thisFriday, [inbox.Id], done, cancellationToken);
        // "Done" on the event ticks what was left, with the verdict's stamp.
        await projects.CheckAsync(thisFriday, [inbox.Id, week.Id, plan.Id], done, cancellationToken);

        var friday = await projects.GetChecksAsync([thisFriday], cancellationToken);
        Assert.Equal(3, friday.Count);
        Assert.Equal(ticked, friday.Single(check => check.StepId == inbox.Id).CompletedAtUtc);

        // Taking the verdict back takes back what it ticked - not the tick made by hand before it,
        // and nothing of last week's sitting.
        Assert.Equal(2, await projects.UncheckBatchAsync(thisFriday, done, cancellationToken));
        Assert.Equal(inbox.Id, Assert.Single(await projects.GetChecksAsync([thisFriday], cancellationToken)).StepId);
        Assert.Equal(3, (await projects.GetChecksAsync([lastFriday], cancellationToken)).Count);

        await projects.UncheckAsync(thisFriday, inbox.Id, cancellationToken);
        Assert.Empty(await projects.GetChecksAsync([thisFriday], cancellationToken));

        // A step deleted from the list leaves no ticks behind, and a sitting that is gone forgets its own.
        await projects.DeleteStepAsync(plan.Id, cancellationToken);
        Assert.Equal(2, (await projects.GetChecksAsync([lastFriday], cancellationToken)).Count);
        await projects.ClearChecksAsync([lastFriday], cancellationToken);
        Assert.Empty(await projects.GetChecksAsync([lastFriday], cancellationToken));
    }

    [Fact]
    public async Task StepsHandedToAnotherProjectArriveOpenAfterWhatItHasInTheOrderTheyHad()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        IProjectRepository projects = new ProjectStore(harness.ContextFactory);
        var sitting = new Project { Title = "Deep work", EventId = Guid.NewGuid() };
        var later = OnTask("Deep work");
        await projects.AddProjectAsync(sitting, cancellationToken);
        await projects.AddProjectAsync(later, cancellationToken);
        var existing = new ProjectStep { ProjectId = later.Id, Title = "Already there", SortOrder = 4 };
        var second = new ProjectStep { ProjectId = sitting.Id, Title = "Second", SortOrder = 2, PlannedDate = new DateOnly(2026, 9, 18) };
        var first = new ProjectStep { ProjectId = sitting.Id, Title = "First", SortOrder = 1, IsDone = true, CompletedAtUtc = DateTimeOffset.UtcNow };
        await projects.AddStepsAsync([existing, second, first], cancellationToken);

        await projects.MoveStepsAsync([second.Id, first.Id], later.Id, cancellationToken);

        var moved = (await projects.GetStepsAsync(cancellationToken)).Where(step => step.ProjectId == later.Id)
            .OrderBy(step => step.SortOrder).ToList();
        Assert.Equal(new[] { "Already there", "First", "Second" }, moved.Select(step => step.Title));
        Assert.Equal(new[] { 4, 5, 6 }, moved.Select(step => step.SortOrder));
        Assert.All(moved.Skip(1), step =>
        {
            Assert.False(step.IsDone);
            Assert.Null(step.PlannedDate);
            Assert.Null(step.EventId);
        });
    }

    [Fact]
    public async Task AProjectFromBeforeHostsExistedIsGivenATaskOfItsOwn()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        IProjectRepository projects = new ProjectStore(harness.ContextFactory);
        var legacy = new Project { Title = "Launch the website", Category = EventCategory.Study, Deadline = new DateOnly(2026, 9, 25) };
        await using (var db = await harness.ContextFactory.CreateDbContextAsync(cancellationToken))
        {
            // Written the way a build from before this change wrote it: no host at all.
            db.Projects.Add(legacy);
            db.ProjectSteps.Add(new ProjectStep { ProjectId = legacy.Id, Title = "Wireframe", DurationMinutes = 90, IsDone = true });
            db.ProjectSteps.Add(new ProjectStep { ProjectId = legacy.Id, Title = "Build", DurationMinutes = 120 });
            await db.SaveChangesAsync(cancellationToken);
        }

        Assert.Equal(1, await projects.AdoptHostlessAsync(TimeZoneInfo.Utc, cancellationToken));
        Assert.Equal(0, await projects.AdoptHostlessAsync(TimeZoneInfo.Utc, cancellationToken));

        var project = Assert.Single(await projects.GetProjectsAsync(cancellationToken));
        Assert.Equal(ProjectHost.Task, project.Host);
        Assert.Equal(EventCategory.Study, project.Category);
        var task = Assert.Single(await harness.Store.GetAllAsync(cancellationToken));
        Assert.Equal(project.TaskId, task.Id);
        Assert.Equal("Launch the website", task.Title);
        Assert.Equal(new DateTimeOffset(2026, 9, 25, 23, 59, 0, TimeSpan.Zero), task.DeadlineUtc);
        Assert.Equal(210, task.EstimatedDurationMin);
        Assert.Equal(120, task.RemainingDurationMin);
        Assert.Equal(Domain.TaskStatus.Ready, task.Status);
    }

    [Fact]
    public async Task BookedEventsAreFoundByIdWhereverTheyAre()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        ICalendarRepository calendar = harness.Store;
        var start = new DateTimeOffset(2026, 9, 15, 9, 0, 0, TimeSpan.Zero);
        CalendarEvent Event(string title, int days) => new()
        {
            Title = title, StartUtc = start.AddDays(days), EndUtc = start.AddDays(days).AddHours(1), Category = EventCategory.Work
        };
        var first = Event("first", 0);
        var second = Event("second", 3);
        var third = Event("third", 40);
        await calendar.AddRangeAsync([first, second, third], cancellationToken);

        var found = await calendar.GetByIdsAsync([first.Id, third.Id, Guid.NewGuid()], cancellationToken);

        Assert.Equal(new[] { first.Id, third.Id }.Order(), found.Select(item => item.Id).Order());
        Assert.Empty(await calendar.GetByIdsAsync([], cancellationToken));
    }

    [Fact]
    public async Task ASeriesIsLiveUntilItsLastOccurrenceIsGone()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        ICalendarRepository calendar = harness.Store;
        var start = new DateTimeOffset(2026, 9, 18, 17, 0, 0, TimeSpan.Zero);
        var template = new CalendarEvent
        {
            Title = "Weekly review", StartUtc = start, EndUtc = start.AddMinutes(45), Recurrence = EventRecurrence.Weekly
        };
        var weekly = EventRecurrenceExpander.Expand(template, start.AddDays(20));
        await calendar.AddRangeAsync(weekly, cancellationToken);
        var series = weekly[0].SeriesId!.Value;
        var elsewhere = Guid.NewGuid();

        Assert.Equal(new[] { series }, await calendar.GetLiveSeriesAsync([series, elsewhere], cancellationToken));

        await calendar.DeleteScopeAsync(weekly[1], EventSeriesScope.Occurrence, cancellationToken);
        Assert.Contains(series, await calendar.GetLiveSeriesAsync([series], cancellationToken));

        await calendar.DeleteScopeAsync(weekly[0], EventSeriesScope.Series, cancellationToken);
        Assert.Empty(await calendar.GetLiveSeriesAsync([series], cancellationToken));
    }
}
