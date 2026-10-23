using IntelligentScheduler.Domain;

namespace IntelligentScheduler.Application;

/// <summary>
/// One event's planned move: where it sits now and where it would land. Nothing is written
/// until the user has seen the collisions this produces, so the plan is a value rather than
/// a side effect.
/// </summary>
public sealed record EventMove(
    Guid Id,
    string Title,
    DateTimeOffset FromStartUtc,
    DateTimeOffset FromEndUtc,
    DateTimeOffset ToStartUtc,
    DateTimeOffset ToEndUtc)
{
    public TimeSpan Offset => ToStartUtc - FromStartUtc;

    /// <summary>True when the move carries the event onto a different local day.</summary>
    public bool ChangesDay => DateOnly.FromDateTime(FromStartUtc.ToLocalTime().Date)
                              != DateOnly.FromDateTime(ToStartUtc.ToLocalTime().Date);

    public bool IsNoop => ToStartUtc == FromStartUtc && ToEndUtc == FromEndUtc;
}

/// <summary>
/// Two things that would end up sharing the clock. <see cref="BlockingIsMovingToo"/> separates
/// "you are about to land on top of something you did not select" from "the two things you
/// selected would collide with each other", because only the first is a surprise.
/// </summary>
public sealed record ScheduleCollision(
    Guid MovedId,
    string MovedTitle,
    string BlockingTitle,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    bool BlockingIsMovingToo)
{
    public int Minutes => Math.Max(0, (int)Math.Round((EndUtc - StartUtc).TotalMinutes));
}

/// <summary>
/// Moving a chosen set of events, and working out what that would run into. Kept away from the
/// view model so the arithmetic — which is where day boundaries and daylight saving live — can
/// be tested without a window.
/// </summary>
public static class ScheduleShift
{
    /// <summary>
    /// Slides every event by the same amount of real time. UTC arithmetic on purpose: "half an
    /// hour later" should be half an hour, including across a daylight saving change.
    /// </summary>
    public static IReadOnlyList<EventMove> By(IReadOnlyList<CalendarEvent> selection, TimeSpan offset)
        => [.. selection
            .OrderBy(item => item.StartUtc)
            .Select(item => new EventMove(item.Id, item.Title,
                item.StartUtc, item.EndUtc,
                item.StartUtc + offset, item.EndUtc + offset))];

    /// <summary>
    /// Carries the selection onto another day, keeping every event at its own time of day and
    /// keeping the gaps between days inside a multi-day selection. The earliest event decides
    /// where the target day is measured from, so moving "Monday and Tuesday" onto Thursday puts
    /// them on Thursday and Friday rather than both on Thursday.
    ///
    /// Whole local days rather than a fixed number of hours: an event at nine in the morning
    /// stays at nine in the morning even when the clocks change in between.
    /// </summary>
    public static IReadOnlyList<EventMove> ToDay(IReadOnlyList<CalendarEvent> selection, DateOnly targetDate)
    {
        if (selection.Count == 0) return [];
        var anchor = selection.Min(item => DateOnly.FromDateTime(item.StartUtc.ToLocalTime().Date));
        return ByDays(selection, targetDate.DayNumber - anchor.DayNumber);
    }

    /// <summary>
    /// Slides every event by a whole number of local days, keeping each at its own time of day.
    ///
    /// Whole local days rather than a fixed multiple of twenty-four hours, for the same reason
    /// <see cref="ToDay"/> works this way: nine in the morning is still nine in the morning on
    /// the other side of a clock change, which "plus seventy-two hours" is not.
    /// </summary>
    public static IReadOnlyList<EventMove> ByDays(IReadOnlyList<CalendarEvent> selection, int days)
    {
        if (selection.Count == 0) return [];
        if (days == 0) return [.. selection.OrderBy(item => item.StartUtc)
            .Select(item => new EventMove(item.Id, item.Title, item.StartUtc, item.EndUtc, item.StartUtc, item.EndUtc))];

        return [.. selection
            .OrderBy(item => item.StartUtc)
            .Select(item => new EventMove(item.Id, item.Title,
                item.StartUtc, item.EndUtc,
                AddLocalDays(item.StartUtc, days), AddLocalDays(item.EndUtc, days)))];
    }

    /// <summary>
    /// Everything the plan would overlap. Both directions are reported: what the moved events
    /// land on, and what they land on each other.
    /// </summary>
    public static IReadOnlyList<ScheduleCollision> Collisions(
        IReadOnlyList<EventMove> moves,
        IReadOnlyList<CalendarEvent> everythingElse)
    {
        if (moves.Count == 0) return [];
        var moving = moves.Select(move => move.Id).ToHashSet();
        var stationary = everythingElse.Where(item => !moving.Contains(item.Id)).ToList();
        var collisions = new List<ScheduleCollision>();

        foreach (var move in moves)
        {
            if (move.ToEndUtc <= move.ToStartUtc) continue;

            foreach (var other in stationary)
            {
                if (Overlap(move.ToStartUtc, move.ToEndUtc, other.StartUtc, other.EndUtc) is not { } window) continue;
                collisions.Add(new(move.Id, move.Title, other.Title, window.Start, window.End, false));
            }

            foreach (var sibling in moves)
            {
                // Reported once, from the earlier of the pair, so one clash is not two lines.
                if (sibling.Id == move.Id || sibling.ToStartUtc < move.ToStartUtc) continue;
                if (sibling.ToStartUtc == move.ToStartUtc && sibling.Id.CompareTo(move.Id) <= 0) continue;
                if (Overlap(move.ToStartUtc, move.ToEndUtc, sibling.ToStartUtc, sibling.ToEndUtc) is not { } window) continue;
                collisions.Add(new(move.Id, move.Title, sibling.Title, window.Start, window.End, true));
            }
        }

        return [.. collisions.OrderBy(item => item.StartUtc)];
    }

    /// <summary>The span two ranges share, or null when they merely touch.</summary>
    private static (DateTimeOffset Start, DateTimeOffset End)? Overlap(
        DateTimeOffset firstStart, DateTimeOffset firstEnd,
        DateTimeOffset secondStart, DateTimeOffset secondEnd)
    {
        var start = firstStart > secondStart ? firstStart : secondStart;
        var end = firstEnd < secondEnd ? firstEnd : secondEnd;
        return end > start ? (start, end) : null;
    }

    /// <summary>Adds whole days in local wall time, so the time of day survives a clock change.</summary>
    private static DateTimeOffset AddLocalDays(DateTimeOffset value, int days)
    {
        var local = DateTime.SpecifyKind(value.ToLocalTime().DateTime.AddDays(days), DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUniversalTime();
    }
}
