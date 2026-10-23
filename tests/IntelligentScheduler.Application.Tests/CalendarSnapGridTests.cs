using IntelligentScheduler.Application;
using Xunit;

namespace IntelligentScheduler.Application.Tests;

public sealed class CalendarSnapGridTests
{
    private const double HourHeight = 52;
    private const double Timeline = 3 * 24 * HourHeight;

    private static int MinutesAt(double offset) => (int)Math.Round(offset / HourHeight * 60);

    [Theory]
    [InlineData(10 * 60 + 7, 10 * 60 + 5)]
    [InlineData(10 * 60 + 8, 10 * 60 + 10)]
    [InlineData(11 * 60 + 23, 11 * 60 + 25)]
    [InlineData(0, 0)]
    public void ADragWithoutShift_LandsOnFiveMinutes(int pointerMinute, int expected)
    {
        var snapped = CalendarSnapGrid.Snap(
            pointerMinute / 60d * HourHeight, HourHeight, CalendarSnapGrid.CoarseStepMinutes, Timeline);

        Assert.Equal(expected, MinutesAt(snapped));
    }

    [Fact]
    public void TheFreeGrid_StartsAtOneMinute()
    {
        var snapped = CalendarSnapGrid.Snap(
            (10 * 60 + 7) / 60d * HourHeight, HourHeight, CalendarSnapGrid.FreeSteps[0], Timeline);

        Assert.Equal(10 * 60 + 7, MinutesAt(snapped));
    }

    [Fact]
    public void TheWheel_WalksTheLadderAndStopsAtItsEnds()
    {
        Assert.Equal(2, CalendarSnapGrid.StepAfter(1, 1));
        Assert.Equal(1, CalendarSnapGrid.StepAfter(1, -1));
        Assert.Equal(60, CalendarSnapGrid.StepAfter(60, 1));
        Assert.Equal(15, CalendarSnapGrid.StepAfter(10, 1));
        Assert.Equal(10, CalendarSnapGrid.StepAfter(15, -1));
    }

    [Fact]
    public void AStepChosenWithTheWheel_IsWhatTheDragRoundsTo()
    {
        var snapped = CalendarSnapGrid.Snap((9 * 60 + 52) / 60d * HourHeight, HourHeight, 15, Timeline);

        Assert.Equal(9 * 60 + 45, MinutesAt(snapped));
    }

    [Fact]
    public void ADragPastTheBoard_StopsAtItsEdge()
    {
        Assert.Equal(0, CalendarSnapGrid.Snap(-400, HourHeight, 5, Timeline));
        Assert.Equal(Timeline, CalendarSnapGrid.Snap(Timeline + 400, HourHeight, 5, Timeline));
    }
}
