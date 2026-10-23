using Xunit;

namespace IntelligentScheduler.Application.Tests;

public sealed class AnalyticsSeriesTests
{
    private static IReadOnlyList<AnalyticsDayPoint> Days(
        int count,
        int minutesPerDay = 60,
        DateOnly? start = null)
    {
        var first = start ?? new DateOnly(2026, 6, 1);
        return [.. Enumerable.Range(0, count).Select(index =>
            new AnalyticsDayPoint(first.AddDays(index), minutesPerDay, minutesPerDay / 2, 1))];
    }

    [Fact]
    public void AWeekKeepsOneColumnPerDay()
    {
        var buckets = AnalyticsSeries.Bucket(Days(7), 32);

        Assert.Equal(7, buckets.Count);
        Assert.All(buckets, bucket => Assert.True(bucket.IsSingleDay));
    }

    [Fact]
    public void AMonthStillKeepsOneColumnPerDay()
    {
        var buckets = AnalyticsSeries.Bucket(Days(30), 32);

        Assert.Equal(30, buckets.Count);
        Assert.All(buckets, bucket => Assert.True(bucket.IsSingleDay));
    }

    [Fact]
    public void ThreeMonthsBecomesThirteenWeeksRatherThanNinetyOneSlivers()
    {
        var buckets = AnalyticsSeries.Bucket(Days(91), 32);

        Assert.Equal(13, buckets.Count);
        Assert.All(buckets, bucket => Assert.Equal(7, bucket.DayCount));
    }

    [Fact]
    public void NothingIsThrownAwayWhenDaysAreFoldedIntoWeeks()
    {
        var daily = Days(91, minutesPerDay: 45);

        var buckets = AnalyticsSeries.Bucket(daily, 32);

        Assert.Equal(daily.Sum(day => day.ScheduledMinutes), buckets.Sum(bucket => bucket.ScheduledMinutes));
        Assert.Equal(daily.Sum(day => day.CompletedMinutes), buckets.Sum(bucket => bucket.CompletedMinutes));
    }

    [Fact]
    public void NonAlignedRangesBreakOnlyAtCalendarMondays()
    {
        var buckets = AnalyticsSeries.Bucket(
            Days(15, start: new DateOnly(2026, 6, 3)), 2);

        Assert.Equal(3, buckets.Count);
        Assert.Equal((new DateOnly(2026, 6, 3), new DateOnly(2026, 6, 7)),
            (buckets[0].From, buckets[0].To));
        Assert.Equal(DayOfWeek.Monday, buckets[1].From.DayOfWeek);
        Assert.Equal(DayOfWeek.Sunday, buckets[1].To.DayOfWeek);
        Assert.Equal((new DateOnly(2026, 6, 15), new DateOnly(2026, 6, 17)),
            (buckets[2].From, buckets[2].To));
    }

    [Fact]
    public void BucketsRunForwardsInTime()
    {
        var buckets = AnalyticsSeries.Bucket(Days(91), 32);

        for (var index = 1; index < buckets.Count; index++)
            Assert.True(buckets[index].From > buckets[index - 1].From);
    }

    [Fact]
    public void EmptyHistoryProducesNoColumns()
        => Assert.Empty(AnalyticsSeries.Bucket([], 32));

    [Theory]
    [InlineData(7)]
    [InlineData(13)]
    [InlineData(30)]
    public void ColumnsAlwaysFitTheStripTheyAreDrawnIn(int columns)
    {
        var width = AnalyticsSeries.ColumnWidth(columns, 528);

        Assert.True(width > 0);
        Assert.True(width * columns <= 528);
    }

    [Fact]
    public void AColumnNeverGrowsWiderThanTheCapEvenWithRoomToSpare()
        => Assert.Equal(22, AnalyticsSeries.ColumnWidth(2, 528));

    [Fact]
    public void ABucketKnowsWhichDaysItCovers()
    {
        var bucket = AnalyticsSeries.Bucket(Days(91), 32)[^1];

        Assert.True(bucket.Contains(bucket.From));
        Assert.True(bucket.Contains(bucket.To));
        Assert.False(bucket.Contains(bucket.To.AddDays(1)));
    }
}
