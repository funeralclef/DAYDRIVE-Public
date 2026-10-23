namespace IntelligentScheduler.Application;

/// <summary>
/// One column of the daily-load chart. A week and a month draw one day per column; three
/// months draws one week per column, because ninety-one columns in six hundred pixels is not
/// a chart, it is a texture.
/// </summary>
public sealed record AnalyticsBucket(
    DateOnly From,
    DateOnly To,
    int ScheduledMinutes,
    int CompletedMinutes,
    int ItemCount)
{
    public int DayCount => To.DayNumber - From.DayNumber + 1;
    public bool IsSingleDay => From == To;
    public bool IsEmpty => ScheduledMinutes == 0 && CompletedMinutes == 0;
    public bool Contains(DateOnly date) => date >= From && date <= To;
}

/// <summary>
/// Folds the day-by-day report into as many columns as the chart can actually draw. Pure, so
/// the range that used to render as overlapping slivers can be checked without a window.
/// </summary>
public static class AnalyticsSeries
{
    /// <summary>
    /// Groups <paramref name="daily"/> into chart-sized buckets. Long ranges use real calendar
    /// weeks, so every new column begins on Monday rather than seven arbitrary days after the
    /// report happened to start.
    /// </summary>
    public static IReadOnlyList<AnalyticsBucket> Bucket(IReadOnlyList<AnalyticsDayPoint> daily, int maxColumns)
    {
        if (daily.Count == 0) return [];
        maxColumns = Math.Max(1, maxColumns);
        var size = GroupSize(daily.Count, maxColumns);
        if (size == 1)
            return [.. daily.Select(day => new AnalyticsBucket(day.Date, day.Date,
                day.ScheduledMinutes, day.CompletedMinutes, day.ItemCount))];

        return [.. daily
            .GroupBy(day => CalendarWeek.Start(day.Date))
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var slice = group.OrderBy(day => day.Date).ToList();
                return new AnalyticsBucket(
                    slice[0].Date,
                    slice[^1].Date,
                    slice.Sum(day => day.ScheduledMinutes),
                    slice.Sum(day => day.CompletedMinutes),
                    slice.Sum(day => day.ItemCount));
            })];
    }

    /// <summary>
    /// How many days share a column. Anything that does not fit one-day-per-column becomes
    /// weeks: a three-day lump has no name a reader could use, a week does.
    /// </summary>
    public static int GroupSize(int dayCount, int maxColumns)
        => dayCount <= maxColumns ? 1 : 7;

    /// <summary>
    /// How wide one column may be drawn, given the strip it has to fit inside. Bars used to be
    /// a fixed seven pixels whatever the count, which is why the long range drew them on top of
    /// one another.
    /// </summary>
    public static double ColumnWidth(int columnCount, double stripWidth, double gap = 3, double maximum = 22)
    {
        if (columnCount <= 0) return maximum;
        var available = stripWidth / columnCount - gap;
        return Math.Clamp(available, 2, maximum);
    }
}
