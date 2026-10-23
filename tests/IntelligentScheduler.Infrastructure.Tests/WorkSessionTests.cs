using IntelligentScheduler.Application;
using IntelligentScheduler.Domain;
using Xunit;

namespace IntelligentScheduler.Infrastructure.Tests;

/// <summary>
/// The stopwatch, and what outlives it.
///
/// The whole reason pausing asks nothing is that the work and the pauses are two separate
/// numbers rather than one number with a caveat, so the arithmetic that keeps them apart is
/// worth pinning down: an open pause has to count against the clock as it grows, and stop
/// counting the moment it is released or the session is finished.
/// </summary>
public sealed class WorkSessionTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WorkedTimeExcludesTheBankedPauses()
    {
        var outcome = new EventOutcome
        {
            StartedAtUtc = Noon,
            PausedSeconds = 600
        };

        // An hour on the wall clock, ten minutes of it standing still.
        Assert.Equal(TimeSpan.FromMinutes(50), outcome.WorkedAt(Noon.AddHours(1)));
        Assert.Equal(TimeSpan.FromMinutes(10), outcome.PausedFor(Noon.AddHours(1)));
    }

    [Fact]
    public void AnOpenPauseStopsTheClockWhileItGrows()
    {
        var outcome = new EventOutcome
        {
            StartedAtUtc = Noon,
            PausedAtUtc = Noon.AddMinutes(20)
        };

        // Twenty minutes of work, and it stays twenty however long the pause runs on.
        Assert.Equal(TimeSpan.FromMinutes(20), outcome.WorkedAt(Noon.AddMinutes(30)));
        Assert.Equal(TimeSpan.FromMinutes(20), outcome.WorkedAt(Noon.AddHours(3)));
        Assert.Equal(TimeSpan.FromMinutes(10), outcome.PausedFor(Noon.AddMinutes(30)));
        Assert.Equal(TimeSpan.FromMinutes(10), outcome.CurrentPauseFor(Noon.AddMinutes(30)));
        Assert.True(outcome.IsPaused);
        Assert.False(outcome.IsRunning);
        Assert.True(outcome.IsSessionOpen);
    }

    [Fact]
    public void AFinishedSessionIsMeasuredToItsEndRatherThanToNow()
    {
        var outcome = new EventOutcome
        {
            Kind = EventOutcomeKind.Completed,
            StartedAtUtc = Noon,
            FinishedAtUtc = Noon.AddMinutes(45),
            PausedSeconds = 300
        };

        Assert.Equal(TimeSpan.FromMinutes(40), outcome.WorkedAt(Noon.AddHours(9)));
        Assert.Equal(TimeSpan.FromMinutes(5), outcome.PausedFor(Noon.AddHours(9)));
        Assert.False(outcome.IsSessionOpen);
        Assert.Equal(TimeSpan.Zero, outcome.CurrentPauseFor(Noon.AddHours(9)));
    }

    /// <summary>
    /// Running past the planned end is a measurement, not an error, so nothing clamps it: the
    /// interface reports "plan 45 min, actual 1 h 12 min" rather than cutting the clock off.
    /// </summary>
    [Fact]
    public void RunningPastThePlannedEndIsNotClamped()
    {
        var outcome = new EventOutcome { StartedAtUtc = Noon, PlannedMinutes = 45 };
        Assert.Equal(TimeSpan.FromMinutes(72), outcome.WorkedAt(Noon.AddMinutes(72)));
    }

    [Fact]
    public async Task PausesSurviveAWriteAndAreCappedOnTheWayIn()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        var source = await AddEventAsync(harness, cancellationToken);

        await harness.Outcomes.RecordAsync(new EventOutcome
        {
            EventId = source.Id,
            Kind = EventOutcomeKind.Started,
            PlannedStartUtc = source.StartUtc,
            PlannedMinutes = 60,
            StartedAtUtc = Noon,
            PausedSeconds = 900,
            PausedAtUtc = Noon.AddMinutes(30)
        }, cancellationToken);

        var stored = await harness.Outcomes.GetForEventAsync(source.Id, cancellationToken);
        Assert.NotNull(stored);
        Assert.Equal(900, stored.PausedSeconds);
        Assert.Equal(Noon.AddMinutes(30), stored.PausedAtUtc);
        Assert.True(stored.IsPaused);

        // Releasing the pause and finishing: the same row, rewritten, with the hold cleared.
        await harness.Outcomes.RecordAsync(new EventOutcome
        {
            EventId = source.Id,
            Kind = EventOutcomeKind.Partial,
            PlannedStartUtc = source.StartUtc,
            PlannedMinutes = 60,
            StartedAtUtc = Noon,
            FinishedAtUtc = Noon.AddMinutes(50),
            ActualMinutes = 35,
            PausedSeconds = 900,
            ProgressPercent = 240
        }, cancellationToken);

        var finished = await harness.Outcomes.GetForEventAsync(source.Id, cancellationToken);
        Assert.NotNull(finished);
        Assert.Null(finished.PausedAtUtc);
        Assert.Equal(EventOutcomeKind.Partial, finished.Kind);
        // A percentage is a percentage however it arrives.
        Assert.Equal(100, finished.ProgressPercent);
        Assert.Equal(35, finished.ActualMinutes);
    }

    /// <summary>
    /// The continuation point and the postponement count belong to the work rather than to one
    /// occurrence of it, which is why they live in their own row: clearing a verdict, or moving
    /// the event onto another day, must not take them with it.
    /// </summary>
    [Fact]
    public async Task FollowUpsOutliveTheVerdictAndCountThePostponements()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        var source = await AddEventAsync(harness, cancellationToken);

        await harness.FollowUps.SetNextStepAsync(source.Id, "  Recheck the last three screens  ", cancellationToken);
        var first = await harness.FollowUps.RecordDeferralAsync(source.Id, cancellationToken);
        Assert.Equal("Recheck the last three screens", first.NextStep);
        Assert.Equal(1, first.DeferCount);
        // One postponement is a bad day, and gets no question at all.
        Assert.False(first.NeedsBlockerAnswer);

        var second = await harness.FollowUps.RecordDeferralAsync(source.Id, cancellationToken);
        Assert.Equal(2, second.DeferCount);
        Assert.True(second.NeedsBlockerAnswer);
        Assert.Equal("Recheck the last three screens", second.NextStep);

        // Answering it stops the question being asked again.
        var answered = await harness.FollowUps.SetBlockerAsync(source.Id, EventBlocker.TooBig, cancellationToken);
        Assert.Equal(EventBlocker.TooBig, answered.Blocker);
        Assert.False(answered.NeedsBlockerAnswer);
        Assert.Equal(2, answered.DeferCount);

        // Clearing the verdict is about the occurrence; the follow-up is about the work.
        await harness.Outcomes.RecordAsync(new EventOutcome
        {
            EventId = source.Id,
            Kind = EventOutcomeKind.Skipped,
            PlannedStartUtc = source.StartUtc,
            PlannedMinutes = 60
        }, cancellationToken);
        await harness.Outcomes.ClearAsync(source.Id, cancellationToken);

        var survivor = await harness.FollowUps.GetForEventAsync(source.Id, cancellationToken);
        Assert.NotNull(survivor);
        Assert.Equal(2, survivor.DeferCount);
        Assert.Equal("Recheck the last three screens", survivor.NextStep);

        // A blank continuation point is no continuation point, not an empty one.
        var cleared = await harness.FollowUps.SetNextStepAsync(source.Id, "   ", cancellationToken);
        Assert.Null(cleared.NextStep);
        Assert.False(cleared.HasNextStep);
    }

    [Fact]
    public async Task FollowUpsAreLookedUpInOneQueryAndCleanUpAfterThemselves()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        var first = await AddEventAsync(harness, cancellationToken);
        var second = await AddEventAsync(harness, cancellationToken, hour: 14);

        await harness.FollowUps.SetNextStepAsync(first.Id, "Finish the migration", cancellationToken);
        await harness.FollowUps.RecordDeferralAsync(second.Id, cancellationToken);

        Assert.Empty(await harness.FollowUps.GetForEventsAsync([], cancellationToken));
        var both = await harness.FollowUps.GetForEventsAsync([first.Id, second.Id, Guid.NewGuid()], cancellationToken);
        Assert.Equal(2, both.Count);

        Assert.True(await harness.FollowUps.ClearAsync(first.Id, cancellationToken));
        Assert.False(await harness.FollowUps.ClearAsync(first.Id, cancellationToken));
        Assert.Null(await harness.FollowUps.GetForEventAsync(first.Id, cancellationToken));
        Assert.NotNull(await harness.FollowUps.GetForEventAsync(second.Id, cancellationToken));
    }

    private static async Task<CalendarEvent> AddEventAsync(
        AssistantTestHarness harness, CancellationToken cancellationToken, int hour = 9)
    {
        var date = DateOnly.FromDateTime(DateTime.Today);
        var (dayStart, dayEnd) = ScheduleActionExecutor.DayRange(date);
        var title = $"Session {hour}";
        await harness.Store.AddAsync(new CalendarEvent
        {
            Title = title,
            StartUtc = dayStart.AddHours(hour),
            EndUtc = dayStart.AddHours(hour).AddMinutes(60),
            Category = EventCategory.Work
        }, cancellationToken);

        ICalendarRepository calendar = harness.Store;
        return (await calendar.GetRangeAsync(dayStart, dayEnd, cancellationToken)).Last(item => item.Title == title);
    }
}
