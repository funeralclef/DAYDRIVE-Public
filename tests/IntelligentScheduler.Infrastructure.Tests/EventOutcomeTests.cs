using IntelligentScheduler.Application;
using IntelligentScheduler.Domain;
using Xunit;

namespace IntelligentScheduler.Infrastructure.Tests;

/// <summary>
/// Event fixation is what makes completion, plan accuracy and on-time start real numbers
/// rather than permanently empty tiles, so both the store and the analytics path are covered.
/// </summary>
public sealed class EventOutcomeTests
{
    private static async Task<CalendarEvent> AddEventAsync(
        AssistantTestHarness harness, int daysAgo, int hour, int minutes, CancellationToken cancellationToken)
    {
        var date = DateOnly.FromDateTime(DateTime.Today).AddDays(-daysAgo);
        var (dayStart, dayEnd) = ScheduleActionExecutor.DayRange(date);
        await harness.Store.AddAsync(new CalendarEvent
        {
            Title = $"Event {daysAgo}-{hour}",
            StartUtc = dayStart.AddHours(hour),
            EndUtc = dayStart.AddHours(hour).AddMinutes(minutes),
            Category = EventCategory.Work
        }, cancellationToken);

        ICalendarRepository calendar = harness.Store;
        return (await calendar.GetRangeAsync(dayStart, dayEnd, cancellationToken))
            .Last(item => item.Title == $"Event {daysAgo}-{hour}");
    }

    [Fact]
    public async Task VerdictsAreFoundByEventEvenAfterTheEventMovesToAnotherDay()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        var source = await AddEventAsync(harness, 1, 9, 60, cancellationToken);

        await harness.Outcomes.RecordAsync(new EventOutcome
        {
            EventId = source.Id,
            Kind = EventOutcomeKind.Completed,
            PlannedStartUtc = source.StartUtc,
            PlannedMinutes = 60,
            ActualMinutes = 55
        }, cancellationToken);

        // The event moves a week later. Its verdict still describes the old planned start, so a
        // range query over the new day finds nothing — which is why the day list looks up by id.
        ICalendarRepository calendar = harness.Store;
        source.StartUtc = source.StartUtc.AddDays(7);
        source.EndUtc = source.EndUtc.AddDays(7);
        await calendar.UpdateAsync(source, cancellationToken);

        var (movedStart, movedEnd) = ScheduleActionExecutor.DayRange(
            DateOnly.FromDateTime(source.StartUtc.LocalDateTime));
        Assert.Empty(await harness.Outcomes.GetRangeAsync(movedStart, movedEnd, cancellationToken));

        var byId = await harness.Outcomes.GetForEventsAsync([source.Id], cancellationToken);
        Assert.Single(byId);
        Assert.Equal(EventOutcomeKind.Completed, byId[0].Kind);
        Assert.Equal(55, byId[0].ActualMinutes);
    }

    [Fact]
    public async Task LookingUpNoEventsCostsNoQueryAndReturnsNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);

        Assert.Empty(await harness.Outcomes.GetForEventsAsync([], cancellationToken));
        Assert.Empty(await harness.Outcomes.GetForEventsAsync([Guid.NewGuid()], cancellationToken));
    }

    [Fact]
    public async Task RecordingTwiceReplacesTheVerdictAndKeepsTheStartTime()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        var source = await AddEventAsync(harness, 1, 9, 60, cancellationToken);
        var startedAt = source.StartUtc.AddMinutes(4);

        await harness.Outcomes.RecordAsync(new EventOutcome
        {
            EventId = source.Id,
            Kind = EventOutcomeKind.Started,
            PlannedStartUtc = source.StartUtc,
            PlannedMinutes = 60,
            StartedAtUtc = startedAt
        }, cancellationToken);

        var finished = await harness.Outcomes.RecordAsync(new EventOutcome
        {
            EventId = source.Id,
            Kind = EventOutcomeKind.Completed,
            PlannedStartUtc = source.StartUtc,
            PlannedMinutes = 60,
            ActualMinutes = 70
        }, cancellationToken);

        Assert.Equal(EventOutcomeKind.Completed, finished.Kind);
        Assert.Equal(70, finished.ActualMinutes);
        // Finishing must not lose when the work actually began.
        Assert.Equal(startedAt, finished.StartedAtUtc);
        Assert.Equal(4, finished.StartDelayMinutes);
        Assert.Single(await harness.Outcomes.GetRangeAsync(
            source.StartUtc.AddDays(-1), source.StartUtc.AddDays(1), cancellationToken));
    }

    [Fact]
    public async Task ClearingRemovesTheVerdict()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        var source = await AddEventAsync(harness, 1, 9, 30, cancellationToken);
        await harness.Outcomes.RecordAsync(new EventOutcome
        {
            EventId = source.Id, Kind = EventOutcomeKind.Skipped, PlannedStartUtc = source.StartUtc, PlannedMinutes = 30
        }, cancellationToken);

        Assert.True(await harness.Outcomes.ClearAsync(source.Id, cancellationToken));
        Assert.Null(await harness.Outcomes.GetForEventAsync(source.Id, cancellationToken));
        Assert.False(await harness.Outcomes.ClearAsync(source.Id, cancellationToken));
    }

    [Fact]
    public async Task OutcomesAloneDriveCompletionAccuracyAndOnTime()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);

        // Four events: two done on time, one done very late, one skipped. No focus blocks at all,
        // which is the situation a normal user is in.
        var records = new (int DaysAgo, int Hour, EventOutcomeKind Kind, int Actual, int Delay)[]
        {
            (1, 9, EventOutcomeKind.Completed, 60, 2),
            (2, 9, EventOutcomeKind.Completed, 90, 5),
            (3, 9, EventOutcomeKind.Completed, 60, 45),
            (4, 9, EventOutcomeKind.Skipped, 0, 0)
        };

        foreach (var (daysAgo, hour, kind, actual, delay) in records)
        {
            var source = await AddEventAsync(harness, daysAgo, hour, 60, cancellationToken);
            await harness.Outcomes.RecordAsync(new EventOutcome
            {
                EventId = source.Id,
                Kind = kind,
                PlannedStartUtc = source.StartUtc,
                PlannedMinutes = 60,
                ActualMinutes = actual,
                StartedAtUtc = kind == EventOutcomeKind.Skipped ? null : source.StartUtc.AddMinutes(delay),
                FinishedAtUtc = source.EndUtc
            }, cancellationToken);
        }

        var now = DateTimeOffset.Now;
        var from = now.AddDays(-30).ToUniversalTime();
        var to = now.AddDays(1).ToUniversalTime();
        ICalendarRepository calendar = harness.Store;
        IBlockRepository blocks = harness.Store;

        var report = ScheduleAnalytics.Build(new AnalyticsInput(
            now, 14,
            await calendar.GetRangeAsync(from, to, cancellationToken),
            await blocks.GetHistoryAsync(from, to, cancellationToken),
            await harness.Store.GetRecentAsync(from, cancellationToken),
            await harness.Store.GetAllAsync(cancellationToken),
            Outcomes: await harness.Outcomes.GetRangeAsync(from, to, cancellationToken)));

        // Three of four finished events were completed.
        Assert.Equal(4, report.CompletionRate.SampleSize);
        Assert.Equal(0.75, report.CompletionRate.Value, 3);

        // Three carry a duration: 60/60, 90/60 and 60/60 give (1 + 0.5 + 1) / 3.
        Assert.Equal(3, report.PlanAccuracy.SampleSize);
        Assert.Equal(0.8333, report.PlanAccuracy.Value, 3);

        // Three were started; the one delayed by 45 minutes misses the ten-minute tolerance.
        Assert.Equal(3, report.OnTimeStartRate.SampleSize);
        Assert.Equal(2 / 3d, report.OnTimeStartRate.Value, 3);

        Assert.Equal(210 / 60d, report.FocusHours.Value, 3);
    }

    [Fact]
    public async Task AStartedButUnfinishedEventDoesNotCountAsFailed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        var source = await AddEventAsync(harness, 1, 9, 60, cancellationToken);
        await harness.Outcomes.RecordAsync(new EventOutcome
        {
            EventId = source.Id,
            Kind = EventOutcomeKind.Started,
            PlannedStartUtc = source.StartUtc,
            PlannedMinutes = 60,
            StartedAtUtc = source.StartUtc.AddMinutes(3)
        }, cancellationToken);

        var now = DateTimeOffset.Now;
        var from = now.AddDays(-30).ToUniversalTime();
        var to = now.AddDays(1).ToUniversalTime();
        ICalendarRepository calendar = harness.Store;
        IBlockRepository blocks = harness.Store;

        var report = ScheduleAnalytics.Build(new AnalyticsInput(
            now, 14,
            await calendar.GetRangeAsync(from, to, cancellationToken),
            await blocks.GetHistoryAsync(from, to, cancellationToken),
            await harness.Store.GetRecentAsync(from, cancellationToken),
            await harness.Store.GetAllAsync(cancellationToken),
            Outcomes: await harness.Outcomes.GetRangeAsync(from, to, cancellationToken)));

        // Still in progress, so it is not part of the completion sample at all.
        Assert.Equal(0, report.CompletionRate.SampleSize);
    }

    [Fact]
    public async Task TheAssistantsAnalyticsToolSeesRecordedOutcomes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        var source = await AddEventAsync(harness, 2, 10, 60, cancellationToken);
        await harness.Outcomes.RecordAsync(new EventOutcome
        {
            EventId = source.Id,
            Kind = EventOutcomeKind.Completed,
            PlannedStartUtc = source.StartUtc,
            PlannedMinutes = 60,
            ActualMinutes = 60,
            StartedAtUtc = source.StartUtc
        }, cancellationToken);

        var result = await harness.Toolbox.InvokeAsync(
            new AssistantToolInvocation("call_1", "get_analytics", """{"rangeDays":14}"""), cancellationToken);

        Assert.False(result.IsError);
        using var json = System.Text.Json.JsonDocument.Parse(result.Content);
        Assert.Equal(1, json.RootElement.GetProperty("completionRate").GetDouble());
        Assert.True(json.RootElement.GetProperty("focusHours").GetDouble() > 0);
    }

    [Fact]
    public async Task TheAssistantsAnalyticsToolCanReadAnExactPastWeek()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        var currentMonday = CalendarWeek.Start(DateOnly.FromDateTime(DateTime.Today));
        var from = currentMonday.AddDays(-7);
        var to = from.AddDays(6);
        var (dayStart, _) = ScheduleActionExecutor.DayRange(from.AddDays(2));
        await harness.Store.AddAsync(new CalendarEvent
        {
            Title = "Past week event",
            StartUtc = dayStart.AddHours(9),
            EndUtc = dayStart.AddHours(10),
            Category = EventCategory.Work
        }, cancellationToken);

        var result = await harness.Toolbox.InvokeAsync(
            new AssistantToolInvocation("call_1", "get_analytics",
                $$"""{"fromDate":"{{from:yyyy-MM-dd}}","toDate":"{{to:yyyy-MM-dd}}"}"""),
            cancellationToken);

        Assert.False(result.IsError);
        using var json = System.Text.Json.JsonDocument.Parse(result.Content);
        Assert.Equal(from.ToString("yyyy-MM-dd"), json.RootElement.GetProperty("from").GetString());
        Assert.Equal(to.ToString("yyyy-MM-dd"), json.RootElement.GetProperty("to").GetString());
        Assert.Equal(7, json.RootElement.GetProperty("rangeDays").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("scheduledHours").GetDouble());
    }
}
