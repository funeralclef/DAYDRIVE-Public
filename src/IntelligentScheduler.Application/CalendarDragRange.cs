namespace IntelligentScheduler.Application;

/// <summary>Normalizes a calendar drag into one continuous interval starting inside a visible week.</summary>
public readonly record struct CalendarDragRange(
    int StartDayIndex,
    int StartMinuteOfDay,
    int EndDayIndex,
    int EndMinuteOfDay)
{
    public int EndDayOffset => EndDayIndex - StartDayIndex;

    /// <summary>
    /// The day the selection may run into past the last one on the board. A drag that carries
    /// on below midnight on Sunday means the small hours of the following Monday, the same way
    /// it means Thursday morning on any other night — the week on screen is what is being
    /// looked at, not a wall the event has to stop at.
    /// </summary>
    public const int LastDayIndex = 7;

    public static CalendarDragRange Create(
        int anchorDay,
        int anchorMinute,
        int pointerDay,
        int pointerMinute,
        int cycleDelta = 0,
        int minimumMinutes = 30)
    {
        const int minutesPerDay = 1440;
        // One day past the end of the week: the last night on the board has a morning after it.
        const int limit = (LastDayIndex + 1) * minutesPerDay;

        anchorDay = Math.Clamp(anchorDay, 0, 6);
        pointerDay = Math.Clamp(pointerDay, 0, 6);
        anchorMinute = Math.Clamp(anchorMinute, 0, minutesPerDay - 1);
        pointerMinute = Math.Clamp(pointerMinute, 0, minutesPerDay);

        var anchor = anchorDay * minutesPerDay + anchorMinute;
        var pointer = pointerDay * minutesPerDay + pointerMinute + cycleDelta * minutesPerDay;
        pointer = Math.Clamp(pointer, 0, limit);

        var start = Math.Min(anchor, pointer);
        var end = Math.Max(anchor, pointer);
        if (end - start < 1)
            end = Math.Min(limit, start + Math.Max(1, minimumMinutes));

        var startDay = Math.Min(6, start / minutesPerDay);
        var startMinute = start % minutesPerDay;
        var endDay = Math.Min(LastDayIndex, end / minutesPerDay);
        var endMinute = end % minutesPerDay;

        // 24:00 at the very end of the range belongs to the day before it for display, but
        // remains 1440.
        if (end == limit)
        {
            endDay = LastDayIndex;
            endMinute = minutesPerDay;
        }

        return new(startDay, startMinute, endDay, endMinute);
    }
}
