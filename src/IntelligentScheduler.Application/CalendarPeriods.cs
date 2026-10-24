namespace IntelligentScheduler.Application;

/// <summary>Calendar-week arithmetic shared by schedule navigation and analytics.</summary>
public static class CalendarWeek
{
    public static DateOnly Start(DateOnly date)
        => date.AddDays(-((int)date.DayOfWeek + 6) % 7);

    public static DateOnly End(DateOnly date) => Start(date).AddDays(6);

    /// <summary>
    /// Selects the edge through which the user enters another week: Sunday when moving back,
    /// Monday when moving forward. This keeps navigation chronological at both boundaries.
    /// </summary>
    public static DateOnly NavigationTarget(DateOnly selectedDate, int weekOffset)
    {
        if (weekOffset == 0) return selectedDate;
        var targetStart = Start(selectedDate).AddDays(weekOffset * 7);
        return weekOffset < 0 ? End(targetStart) : targetStart;
    }
}

/// <summary>
/// The visible analytics interval and the interval it is compared with. Keeping both explicit
/// preserves calendar semantics when adjacent months contain different numbers of days.
/// </summary>
public sealed record AnalyticsPeriod(
    DateOnly From,
    DateOnly To,
    DateOnly PreviousFrom,
    DateOnly PreviousTo)
{
    public int DayCount => Math.Max(1, To.DayNumber - From.DayNumber + 1);
    public int PreviousDayCount => Math.Max(1, PreviousTo.DayNumber - PreviousFrom.DayNumber + 1);

    public static AnalyticsPeriod Rolling(DateOnly to, int days)
    {
        days = Math.Clamp(days, 1, 366);
        var from = to.AddDays(-(days - 1));
        return new(from, to, from.AddDays(-days), to.AddDays(-days));
    }

    /// <summary>
    /// Builds a complete Monday-first calendar week and compares it with the preceding week.
    /// </summary>
    public static AnalyticsPeriod ForCalendarWeek(DateOnly weekDate)
    {
        var from = CalendarWeek.Start(weekDate);
        var to = CalendarWeek.End(weekDate);
        return new(from, to, from.AddDays(-7), to.AddDays(-7));
    }

    /// <summary>
    /// Builds one or more complete calendar months ending with the month that contains
    /// <paramref name="monthDate"/>. The comparison is the same number of preceding months,
    /// whose day count may legitimately differ.
    /// </summary>
    public static AnalyticsPeriod ForCalendarMonths(DateOnly monthDate, int monthCount)
    {
        monthCount = Math.Clamp(monthCount, 1, 12);
        var finalMonth = new DateOnly(monthDate.Year, monthDate.Month, 1);
        var from = finalMonth.AddMonths(-(monthCount - 1));
        var to = finalMonth.AddMonths(1).AddDays(-1);
        var previousFrom = from.AddMonths(-monthCount);
        return new(from, to, previousFrom, from.AddDays(-1));
    }
}

/// <summary>Local calendar-day boundaries converted independently so DST keeps wall-clock dates intact.</summary>
public static class LocalDateRange
{
    public static DateTimeOffset StartUtc(DateOnly date)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUniversalTime();
    }

    public static (DateTimeOffset Start, DateTimeOffset End) Utc(DateOnly date)
        => (StartUtc(date), StartUtc(date.AddDays(1)));
}
