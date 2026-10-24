using IntelligentScheduler.Application;
using IntelligentScheduler.Domain;
using Xunit;

namespace IntelligentScheduler.Infrastructure.Tests;

/// <summary>
/// Moving and removing a chosen set of events. Both go through one transaction each, so a
/// selection can never end up half applied.
/// </summary>
public sealed class CalendarBulkEditTests
{
    private static CalendarEvent Event(string title, DateTimeOffset start, int minutes = 60)
        => new() { Title = title, StartUtc = start, EndUtc = start.AddMinutes(minutes) };

    [Fact]
    public async Task MoveEvents_WritesEveryResolvedMove()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        ICalendarRepository calendar = harness.Store;

        var start = new DateTimeOffset(2026, 9, 7, 9, 0, 0, TimeSpan.Zero);
        var first = Event("stand-up", start);
        var second = Event("review", start.AddHours(3));
        await calendar.AddRangeAsync([first, second], cancellationToken);

        var moves = ScheduleShift.By([first, second], TimeSpan.FromMinutes(90));
        var written = await calendar.MoveEventsAsync(moves, cancellationToken);

        Assert.Equal(2, written);
        var stored = (await calendar.GetAllAsync(cancellationToken)).OrderBy(item => item.StartUtc).ToList();
        Assert.Equal(start.AddMinutes(90), stored[0].StartUtc);
        Assert.Equal(start.AddHours(3).AddMinutes(90), stored[1].StartUtc);
        Assert.Equal(60, (int)(stored[0].EndUtc - stored[0].StartUtc).TotalMinutes);
    }

    [Fact]
    public async Task MoveEvents_LeavesEventsThatWereNotChosenAlone()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        ICalendarRepository calendar = harness.Store;

        var start = new DateTimeOffset(2026, 9, 7, 9, 0, 0, TimeSpan.Zero);
        var chosen = Event("chosen", start);
        var other = Event("other", start.AddHours(5));
        await calendar.AddRangeAsync([chosen, other], cancellationToken);

        await calendar.MoveEventsAsync(ScheduleShift.By([chosen], TimeSpan.FromHours(1)), cancellationToken);

        var stored = await calendar.GetAllAsync(cancellationToken);
        Assert.Equal(start.AddHours(5), stored.Single(item => item.Title == "other").StartUtc);
    }

    [Fact]
    public async Task MoveEvents_MarksAMovedOccurrenceAsBreakingFromItsSeries()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        ICalendarRepository calendar = harness.Store;

        var seriesId = Guid.NewGuid();
        var start = new DateTimeOffset(2026, 9, 7, 9, 0, 0, TimeSpan.Zero);
        var occurrence = Event("weekly", start);
        occurrence.SeriesId = seriesId;
        occurrence.Recurrence = EventRecurrence.Weekly;
        await calendar.AddAsync(occurrence, cancellationToken);

        await calendar.MoveEventsAsync(ScheduleShift.By([occurrence], TimeSpan.FromHours(2)), cancellationToken);

        Assert.True((await calendar.GetAllAsync(cancellationToken)).Single().IsRecurrenceException);
    }

    [Fact]
    public async Task MoveEvents_RefusesAMoveThatWouldCollapseAnEvent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        ICalendarRepository calendar = harness.Store;

        var start = new DateTimeOffset(2026, 9, 7, 9, 0, 0, TimeSpan.Zero);
        var item = Event("broken", start);
        await calendar.AddAsync(item, cancellationToken);

        var broken = new EventMove(item.Id, item.Title, item.StartUtc, item.EndUtc, start, start);

        await Assert.ThrowsAsync<ArgumentException>(
            () => calendar.MoveEventsAsync([broken], cancellationToken));
        Assert.Equal(start.AddHours(1), (await calendar.GetAllAsync(cancellationToken)).Single().EndUtc);
    }

    [Fact]
    public async Task MoveEvents_IgnoresIdsThatAreNoLongerThere()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        ICalendarRepository calendar = harness.Store;

        var start = new DateTimeOffset(2026, 9, 7, 9, 0, 0, TimeSpan.Zero);
        var ghost = new EventMove(Guid.NewGuid(), "gone", start, start.AddHours(1),
            start.AddHours(2), start.AddHours(3));

        Assert.Equal(0, await calendar.MoveEventsAsync([ghost], cancellationToken));
    }

    [Fact]
    public async Task DeleteMany_RemovesExactlyTheChosenEvents()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        ICalendarRepository calendar = harness.Store;

        var start = new DateTimeOffset(2026, 9, 7, 9, 0, 0, TimeSpan.Zero);
        var first = Event("first", start);
        var second = Event("second", start.AddHours(2));
        var survivor = Event("survivor", start.AddHours(4));
        await calendar.AddRangeAsync([first, second, survivor], cancellationToken);

        var removed = await calendar.DeleteManyAsync([first.Id, second.Id], cancellationToken);

        Assert.Equal(2, removed);
        Assert.Equal("survivor", (await calendar.GetAllAsync(cancellationToken)).Single().Title);
    }

    [Fact]
    public async Task DeleteMany_WithNothingChosenTouchesNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        ICalendarRepository calendar = harness.Store;

        await calendar.AddAsync(Event("kept", new DateTimeOffset(2026, 9, 7, 9, 0, 0, TimeSpan.Zero)), cancellationToken);

        Assert.Equal(0, await calendar.DeleteManyAsync([], cancellationToken));
        Assert.Single(await calendar.GetAllAsync(cancellationToken));
    }
}
