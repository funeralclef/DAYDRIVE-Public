namespace IntelligentScheduler.Application;

public static class EventTimeRange
{
    public static (DateTime StartLocal, DateTime EndLocal) ResolveLocal(
        DateOnly date,
        TimeSpan startTime,
        TimeSpan endTime)
    {
        if (startTime == endTime)
            throw new ArgumentException("An event must have a non-zero duration.", nameof(endTime));

        var start = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue).Add(startTime), DateTimeKind.Unspecified);
        var end = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue).Add(endTime), DateTimeKind.Unspecified);
        if (end < start) end = end.AddDays(1);
        return (start, end);
    }
}
