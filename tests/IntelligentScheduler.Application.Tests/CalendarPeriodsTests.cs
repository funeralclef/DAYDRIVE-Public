using IntelligentScheduler.Application;
using Xunit;

namespace IntelligentScheduler.Application.Tests;

public sealed class CalendarPeriodsTests
{
    [Theory]
    [InlineData(2026, 8, 17)]
    [InlineData(2026, 8, 18)]
    [InlineData(2026, 8, 23)]
    public void CalendarWeekAlwaysStartsOnMonday(int year, int month, int day)
        => Assert.Equal(new DateOnly(2026, 8, 17), CalendarWeek.Start(new(year, month, day)));

    [Fact]
    public void CalendarWeekRunsFromMondayThroughSunday()
    {
        var period = AnalyticsPeriod.ForCalendarWeek(new DateOnly(2026, 8, 19));

        Assert.Equal(new DateOnly(2026, 8, 17), period.From);
        Assert.Equal(new DateOnly(2026, 8, 23), period.To);
        Assert.Equal(new DateOnly(2026, 8, 10), period.PreviousFrom);
        Assert.Equal(new DateOnly(2026, 8, 16), period.PreviousTo);
    }

    [Theory]
    [InlineData(2026, 8, 17)]
    [InlineData(2026, 8, 18)]
    [InlineData(2026, 8, 23)]
    public void BackwardWeekNavigationSelectsPreviousSunday(int year, int month, int day)
    {
        var target = CalendarWeek.NavigationTarget(new(year, month, day), -1);

        Assert.Equal(new DateOnly(2026, 8, 16), target);
        Assert.Equal(DayOfWeek.Sunday, target.DayOfWeek);
    }

    [Theory]
    [InlineData(2026, 8, 17)]
    [InlineData(2026, 8, 18)]
    [InlineData(2026, 8, 23)]
    public void ForwardWeekNavigationSelectsNextMonday(int year, int month, int day)
    {
        var target = CalendarWeek.NavigationTarget(new(year, month, day), 1);

        Assert.Equal(new DateOnly(2026, 8, 24), target);
        Assert.Equal(DayOfWeek.Monday, target.DayOfWeek);
    }

    [Theory]
    [InlineData(2026, 2, 10, 2026, 2, 1, 2026, 2, 28, 2026, 1, 1, 2026, 1, 31)]
    [InlineData(2024, 2, 29, 2024, 2, 1, 2024, 2, 29, 2024, 1, 1, 2024, 1, 31)]
    public void MonthUsesItsExactFirstAndLastDay(
        int year, int month, int day,
        int fromYear, int fromMonth, int fromDay,
        int toYear, int toMonth, int toDay,
        int previousFromYear, int previousFromMonth, int previousFromDay,
        int previousToYear, int previousToMonth, int previousToDay)
    {
        var period = AnalyticsPeriod.ForCalendarMonths(new(year, month, day), 1);

        Assert.Equal(new DateOnly(fromYear, fromMonth, fromDay), period.From);
        Assert.Equal(new DateOnly(toYear, toMonth, toDay), period.To);
        Assert.Equal(new DateOnly(previousFromYear, previousFromMonth, previousFromDay), period.PreviousFrom);
        Assert.Equal(new DateOnly(previousToYear, previousToMonth, previousToDay), period.PreviousTo);
    }

    [Fact]
    public void ThreeMonthsUseCompleteCalendarMonthBoundaries()
    {
        var period = AnalyticsPeriod.ForCalendarMonths(new DateOnly(2026, 8, 19), 3);

        Assert.Equal(new DateOnly(2026, 6, 1), period.From);
        Assert.Equal(new DateOnly(2026, 8, 31), period.To);
        Assert.Equal(new DateOnly(2026, 3, 1), period.PreviousFrom);
        Assert.Equal(new DateOnly(2026, 5, 31), period.PreviousTo);
        Assert.Equal(92, period.DayCount);
        Assert.Equal(92, period.PreviousDayCount);
    }
}
