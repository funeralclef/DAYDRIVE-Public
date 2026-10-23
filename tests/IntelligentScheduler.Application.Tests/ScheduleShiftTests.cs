using IntelligentScheduler.Domain;
using Xunit;

namespace IntelligentScheduler.Application.Tests;

public sealed class ScheduleShiftTests
{
    private static DateTimeOffset Local(int year, int month, int day, int hour, int minute = 0)
    {
        var local = DateTime.SpecifyKind(new DateTime(year, month, day, hour, minute, 0), DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUniversalTime();
    }

    private static CalendarEvent Event(string title, int day, int hour, int lengthMinutes = 60)
        => new()
        {
            Title = title,
            StartUtc = Local(2026, 9, day, hour),
            EndUtc = Local(2026, 9, day, hour).AddMinutes(lengthMinutes)
        };

    [Fact]
    public void ShiftingByAnAmountMovesEveryEventTheSameDistance()
    {
        var events = new[] { Event("A", 7, 9), Event("B", 7, 14) };

        var moves = ScheduleShift.By(events, TimeSpan.FromMinutes(45));

        Assert.Equal(2, moves.Count);
        Assert.All(moves, move => Assert.Equal(TimeSpan.FromMinutes(45), move.Offset));
        Assert.All(moves, move => Assert.Equal(move.FromEndUtc - move.FromStartUtc, move.ToEndUtc - move.ToStartUtc));
    }

    [Fact]
    public void ShiftingByZeroIsReportedAsSomethingWorthRefusing()
    {
        var moves = ScheduleShift.By([Event("A", 7, 9)], TimeSpan.Zero);

        Assert.True(moves[0].IsNoop);
    }

    [Fact]
    public void MovingToADayKeepsEachEventAtItsOwnTimeOfDay()
    {
        var events = new[] { Event("morning", 7, 9), Event("evening", 7, 19) };

        var moves = ScheduleShift.ToDay(events, new DateOnly(2026, 9, 10));

        Assert.All(moves, move => Assert.Equal(
            move.FromStartUtc.ToLocalTime().TimeOfDay, move.ToStartUtc.ToLocalTime().TimeOfDay));
        Assert.All(moves, move => Assert.Equal(
            new DateOnly(2026, 9, 10), DateOnly.FromDateTime(move.ToStartUtc.ToLocalTime().Date)));
    }

    [Fact]
    public void MovingASpreadOfDaysKeepsTheGapsBetweenThem()
    {
        // Monday and Wednesday moved onto Friday should land on Friday and Sunday, not both
        // on Friday: the shape of the selection is part of what is being moved.
        var events = new[] { Event("first", 7, 9), Event("third", 9, 9) };

        var moves = ScheduleShift.ToDay(events, new DateOnly(2026, 9, 11));

        Assert.Equal(new DateOnly(2026, 9, 11), DateOnly.FromDateTime(moves[0].ToStartUtc.ToLocalTime().Date));
        Assert.Equal(new DateOnly(2026, 9, 13), DateOnly.FromDateTime(moves[1].ToStartUtc.ToLocalTime().Date));
    }

    [Fact]
    public void MovingByWholeDaysKeepsEveryEventAtItsOwnTimeOfDay()
    {
        var events = new[] { Event("morning", 7, 9), Event("evening", 7, 19) };

        var moves = ScheduleShift.ByDays(events, 3);

        Assert.All(moves, move => Assert.Equal(
            move.FromStartUtc.ToLocalTime().TimeOfDay, move.ToStartUtc.ToLocalTime().TimeOfDay));
        Assert.All(moves, move => Assert.Equal(
            new DateOnly(2026, 9, 10), DateOnly.FromDateTime(move.ToStartUtc.ToLocalTime().Date)));
    }

    [Fact]
    public void MovingBackByWholeDaysIsTheSameJourneyInReverse()
    {
        var moves = ScheduleShift.ByDays([Event("A", 10, 9)], -3);

        Assert.Equal(new DateOnly(2026, 9, 7), DateOnly.FromDateTime(moves[0].ToStartUtc.ToLocalTime().Date));
        Assert.Equal(TimeSpan.FromHours(9), moves[0].ToStartUtc.ToLocalTime().TimeOfDay);
    }

    [Fact]
    public void MovingByNoDaysAtAllChangesNothing()
    {
        var moves = ScheduleShift.ByDays([Event("A", 7, 9)], 0);

        Assert.True(moves[0].IsNoop);
    }

    [Fact]
    public void MovingToTheDayItIsAlreadyOnChangesNothing()
    {
        var moves = ScheduleShift.ToDay([Event("A", 7, 9)], new DateOnly(2026, 9, 7));

        Assert.True(moves[0].IsNoop);
    }

    [Fact]
    public void ChangesDaySeesTheMoveThatCrossesMidnight()
    {
        var moves = ScheduleShift.By([Event("late", 7, 23)], TimeSpan.FromHours(2));

        Assert.True(moves[0].ChangesDay);
    }

    [Fact]
    public void LandingOnAnUntouchedEventIsReportedAsACollision()
    {
        var moving = Event("moving", 7, 9);
        var standing = Event("standing", 7, 11);

        var moves = ScheduleShift.By([moving], TimeSpan.FromHours(2));
        var collisions = ScheduleShift.Collisions(moves, [moving, standing]);

        var collision = Assert.Single(collisions);
        Assert.Equal("moving", collision.MovedTitle);
        Assert.Equal("standing", collision.BlockingTitle);
        Assert.False(collision.BlockingIsMovingToo);
        Assert.Equal(60, collision.Minutes);
    }

    [Fact]
    public void TheEventBeingMovedIsNeverCountedAsBlockingItself()
    {
        var moving = Event("moving", 7, 9, lengthMinutes: 180);

        // A short shift leaves the new window overlapping where the event used to be.
        var moves = ScheduleShift.By([moving], TimeSpan.FromMinutes(30));
        var collisions = ScheduleShift.Collisions(moves, [moving]);

        Assert.Empty(collisions);
    }

    [Fact]
    public void TwoMovedEventsLandingOnEachOtherAreReportedOnce()
    {
        // Both are dragged onto the same hour, so they collide with each other and nothing else.
        var first = Event("first", 7, 9);
        var second = Event("second", 7, 10);
        var moves = new[]
        {
            ScheduleShift.By([first], TimeSpan.FromMinutes(30))[0],
            ScheduleShift.By([second], TimeSpan.FromMinutes(-30))[0]
        };

        var collisions = ScheduleShift.Collisions(moves, [first, second]);

        var collision = Assert.Single(collisions);
        Assert.True(collision.BlockingIsMovingToo);
    }

    [Fact]
    public void EventsThatMerelyTouchDoNotCollide()
    {
        var moving = Event("moving", 7, 9);
        var following = Event("following", 7, 11);

        var moves = ScheduleShift.By([moving], TimeSpan.FromHours(1));
        var collisions = ScheduleShift.Collisions(moves, [moving, following]);

        Assert.Empty(collisions);
    }
}
