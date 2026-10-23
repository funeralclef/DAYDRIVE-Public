using Xunit;

namespace IntelligentScheduler.Application.Tests;

public sealed class CalendarDragRangeTests
{
    [Fact]
    public void Create_TracksEveryDayInForwardMultiDayDrag()
    {
        var range = CalendarDragRange.Create(1, 10 * 60, 4, 11 * 60);

        Assert.Equal(1, range.StartDayIndex);
        Assert.Equal(10 * 60, range.StartMinuteOfDay);
        Assert.Equal(4, range.EndDayIndex);
        Assert.Equal(11 * 60, range.EndMinuteOfDay);
        Assert.Equal(3, range.EndDayOffset);
    }

    [Fact]
    public void Create_NormalizesBackwardDrag()
    {
        var range = CalendarDragRange.Create(5, 18 * 60, 2, 9 * 60 + 17);

        Assert.Equal(new CalendarDragRange(2, 9 * 60 + 17, 5, 18 * 60), range);
    }

    [Fact]
    public void Create_UsesCycleChangeAsAdjacentDayForVerticalMidnightDrag()
    {
        var range = CalendarDragRange.Create(3, 23 * 60, 3, 45, cycleDelta: 1);

        Assert.Equal(new CalendarDragRange(3, 23 * 60, 4, 45), range);
    }

    [Fact]
    public void Create_CarriesADragBelowMidnightOnSundayIntoTheMondayAfterIt()
    {
        // The last night on the board still has a morning after it; the drag used to stop dead
        // at 24:00 on Sunday because the range refused to leave the week it was drawn in.
        var range = CalendarDragRange.Create(6, 23 * 60, 6, 30, cycleDelta: 1);

        Assert.Equal(new CalendarDragRange(6, 23 * 60, 7, 30), range);
        Assert.Equal(1, range.EndDayOffset);
    }

    [Fact]
    public void Create_StopsOneWholeDayPastTheEndOfTheWeek()
    {
        var range = CalendarDragRange.Create(6, 23 * 60, 6, 20 * 60, cycleDelta: 3);

        Assert.Equal(CalendarDragRange.LastDayIndex, range.EndDayIndex);
        Assert.Equal(24 * 60, range.EndMinuteOfDay);
    }

    [Fact]
    public void Create_AllowsMinutePrecisionAroundEleven()
    {
        var range = CalendarDragRange.Create(0, 10 * 60 + 59, 0, 11 * 60 + 1);

        Assert.Equal(10 * 60 + 59, range.StartMinuteOfDay);
        Assert.Equal(11 * 60 + 1, range.EndMinuteOfDay);
    }
}
