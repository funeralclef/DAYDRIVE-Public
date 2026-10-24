using IntelligentScheduler.Domain;

namespace IntelligentScheduler.Application;

public static class EventRecurrenceExpander
{
    public static IReadOnlyList<CalendarEvent> Expand(CalendarEvent template, DateTimeOffset horizonEndUtc, int maximumOccurrences = 400)
    {
        if (template.Recurrence == EventRecurrence.None) return [template];
        if (maximumOccurrences < 1) throw new ArgumentOutOfRangeException(nameof(maximumOccurrences));

        var seriesId = template.SeriesId ?? Guid.NewGuid();
        var occurrences = new List<CalendarEvent>();
        var localStart = template.StartUtc.ToLocalTime();
        var localEnd = template.EndUtc.ToLocalTime();
        var duration = localEnd.LocalDateTime - localStart.LocalDateTime;
        var cursor = localStart.LocalDateTime;

        for (var index = 0; index < maximumOccurrences; index++)
        {
            if (index > 0) cursor = Next(cursor, template.Recurrence);
            var start = ToOffset(cursor).ToUniversalTime();
            if (start >= horizonEndUtc) break;
            var end = ToOffset(cursor + duration).ToUniversalTime();
            occurrences.Add(Clone(template, seriesId, index, start, end));
        }

        return occurrences;
    }

    private static DateTime Next(DateTime current, EventRecurrence recurrence)
        => recurrence switch
        {
            EventRecurrence.Daily => current.AddDays(1),
            EventRecurrence.Weekly => current.AddDays(7),
            EventRecurrence.Monthly => current.AddMonths(1),
            EventRecurrence.Weekdays => NextWeekday(current),
            _ => current
        };

    private static DateTime NextWeekday(DateTime current)
    {
        do current = current.AddDays(1);
        while (current.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
        return current;
    }

    private static DateTimeOffset ToOffset(DateTime local)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private static CalendarEvent Clone(CalendarEvent source, Guid seriesId, int index, DateTimeOffset start, DateTimeOffset end)
        => new()
        {
            Title = source.Title,
            StartUtc = start,
            EndUtc = end,
            Source = source.Source,
            Category = source.Category,
            Priority = source.Priority,
            Flexibility = source.Flexibility,
            Recurrence = source.Recurrence,
            SeriesId = seriesId,
            OccurrenceIndex = index
        };
}