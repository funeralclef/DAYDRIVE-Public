using IntelligentScheduler.Domain;
using DomainTaskStatus = IntelligentScheduler.Domain.TaskStatus;

namespace IntelligentScheduler.Application;

/// <summary>Everything the analytics view needs, gathered once so the maths stays pure.</summary>
public sealed record AnalyticsInput(
    DateTimeOffset Now,
    int RangeDays,
    IReadOnlyList<CalendarEvent> Events,
    IReadOnlyList<TimeBlock> Blocks,
    IReadOnlyList<TaskExecution> Executions,
    IReadOnlyList<TaskItem> Tasks,
    /// <summary>Hours per day treated as available when computing load. Defaults to a 16h waking day.</summary>
    double AvailableHoursPerDay = 16,
    /// <summary>What actually happened to calendar events, as recorded from the day card.</summary>
    IReadOnlyList<EventOutcome>? Outcomes = null,
    /// <summary>The exact visible and comparison windows. A seven-day report defaults to a calendar week.</summary>
    AnalyticsPeriod? Period = null);

public sealed record AnalyticsMetric(
    string Key,
    double Value,
    /// <summary>Same metric over the immediately preceding window, when there was enough history.</summary>
    double? PreviousValue,
    int SampleSize)
{
    public double? Delta => PreviousValue is null ? null : Value - PreviousValue.Value;
    public bool HasEnoughData => SampleSize > 0;
}

public sealed record AnalyticsDayPoint(DateOnly Date, int ScheduledMinutes, int CompletedMinutes, int ItemCount);

public sealed record AnalyticsSlice(string Key, int Minutes, double Share);

public sealed record AnalyticsHourPoint(int Hour, int Minutes);

public sealed record AnalyticsWeekdayPoint(DayOfWeek Day, int Minutes, int ItemCount);

public enum AnalyticsInsightKind
{
    EstimationBias,
    StrongTimeOfDay,
    ScheduleFriction,
    StartDelay,
    Overload,
    Underuse,
    CategoryDominance,
    Consistency
}

public sealed record AnalyticsInsight(
    AnalyticsInsightKind Kind,
    /// <summary>Formatted values the UI feeds into its localized template for this kind.</summary>
    IReadOnlyList<string> Arguments,
    double Confidence,
    int SampleSize,
    bool IsPositive = false);

public sealed record AnalyticsReport(
    DateOnly From,
    DateOnly To,
    int RangeDays,
    AnalyticsMetric ScheduledHours,
    AnalyticsMetric CompletionRate,
    AnalyticsMetric PlanAccuracy,
    AnalyticsMetric OnTimeStartRate,
    AnalyticsMetric Load,
    AnalyticsMetric FocusHours,
    int LongestStreakDays,
    int CurrentStreakDays,
    int BusiestHour,
    IReadOnlyList<AnalyticsDayPoint> Daily,
    IReadOnlyList<AnalyticsSlice> Categories,
    IReadOnlyList<AnalyticsHourPoint> Hours,
    IReadOnlyList<AnalyticsWeekdayPoint> Weekdays,
    IReadOnlyList<AnalyticsInsight> Insights)
{
    public bool HasData => Daily.Any(day => day.ScheduledMinutes > 0 || day.CompletedMinutes > 0);
}

/// <summary>
/// Turns the local schedule history into explainable numbers. There is no model here and no
/// invented "productivity score": every figure is a ratio the user can verify by hand, and an
/// insight only appears once the sample is large enough to mean something.
/// </summary>
public static class ScheduleAnalytics
{
    /// <summary>Below this many finished sessions an insight is noise, so none is emitted.</summary>
    private const int MinimumInsightSample = 4;
    private const int OnTimeToleranceMinutes = 10;

    public static AnalyticsReport Build(AnalyticsInput input)
    {
        var requestedDays = Math.Clamp(input.RangeDays, 1, 366);
        var today = DateOnly.FromDateTime(input.Now.LocalDateTime);
        var period = input.Period ?? requestedDays switch
        {
            7 => AnalyticsPeriod.ForCalendarWeek(today),
            30 => AnalyticsPeriod.ForCalendarMonths(today, 1),
            91 => AnalyticsPeriod.ForCalendarMonths(today, 3),
            _ => AnalyticsPeriod.Rolling(today, requestedDays)
        };
        var from = period.From;
        var to = period.To;
        var rangeDays = period.DayCount;

        var current = Window(input, from, to);
        var previous = Window(input, period.PreviousFrom, period.PreviousTo);

        var daily = BuildDaily(current, from, to);
        var categories = BuildCategories(current);
        var hours = BuildHours(current);
        var weekdays = BuildWeekdays(current, from, to);

        var availableMinutes = Math.Max(1, (int)Math.Round(input.AvailableHoursPerDay * 60 * rangeDays));
        var previousAvailableMinutes = Math.Max(1,
            (int)Math.Round(input.AvailableHoursPerDay * 60 * period.PreviousDayCount));
        var scheduledMinutes = MergedMinutes(current.Events, current.Blocks, current.StartUtc, current.EndUtc);
        var previousScheduled = MergedMinutes(previous.Events, previous.Blocks, previous.StartUtc, previous.EndUtc);

        var scheduledHours = new AnalyticsMetric("scheduledHours", scheduledMinutes / 60d,
            previous.HasHistory ? previousScheduled / 60d : null, current.Events.Count + current.Blocks.Count);

        var completionRate = new AnalyticsMetric("completionRate",
            Ratio(current.Finished, session => session.IsCompleted) ?? 0,
            previous.HasHistory ? Ratio(previous.Finished, session => session.IsCompleted) : null,
            current.Finished.Count);

        var accuracyMetric = new AnalyticsMetric("planAccuracy", PlanAccuracy(current.Measured) ?? 0,
            previous.HasHistory ? PlanAccuracy(previous.Measured) : null, current.Measured.Count);

        var startedSessions = current.Finished.Where(session => session.StartDelayMinutes is not null).ToList();
        var onTimeMetric = new AnalyticsMetric("onTimeStart", OnTimeRate(current.Finished) ?? 0,
            previous.HasHistory ? OnTimeRate(previous.Finished) : null, startedSessions.Count);

        var loadMetric = new AnalyticsMetric("load", Math.Min(1, scheduledMinutes / (double)availableMinutes),
            previous.HasHistory ? Math.Min(1, previousScheduled / (double)previousAvailableMinutes) : null, rangeDays);

        var focusMinutes = ActualMinutes(current.TimelineSessions, current.StartUtc, current.EndUtc);
        var previousFocusMinutes = ActualMinutes(previous.TimelineSessions, previous.StartUtc, previous.EndUtc);
        var focusMetric = new AnalyticsMetric("focusHours", focusMinutes / 60d,
            previous.HasHistory ? previousFocusMinutes / 60d : null,
            current.TimelineSessions.Count(session => session.ActualMinutes > 0));

        var (longest, currentStreak) = Streaks(daily, to < today ? to : today);
        var busiestHour = hours.Count == 0 ? -1 : hours.OrderByDescending(hour => hour.Minutes).First().Minutes == 0
            ? -1
            : hours.OrderByDescending(hour => hour.Minutes).First().Hour;

        var insights = BuildInsights(input, current, daily, categories, hours, loadMetric, completionRate, accuracyMetric);

        return new AnalyticsReport(from, to, rangeDays, scheduledHours, completionRate, accuracyMetric,
            onTimeMetric, loadMetric, focusMetric, longest, currentStreak, busiestHour,
            daily, categories, hours, weekdays, insights);
    }

    /// <summary>
    /// One finished-or-not unit of work, whichever surface it came from. Focus blocks and
    /// recorded event outcomes answer the same questions, so the metrics work on this shape
    /// instead of special-casing each source.
    /// </summary>
    private sealed record Session(
        DateTimeOffset When,
        DateTimeOffset ActualStart,
        bool IsFinished,
        bool IsCompleted,
        bool IsSkipped,
        bool WasMoved,
        int PlannedMinutes,
        int ActualMinutes,
        int? StartDelayMinutes)
    {
        public DateTimeOffset ActualEnd => ActualStart.AddMinutes(Math.Max(0, ActualMinutes));
    }

    private sealed record WindowData(
        IReadOnlyList<CalendarEvent> Events,
        IReadOnlyList<TimeBlock> Blocks,
        IReadOnlyList<Session> Sessions,
        IReadOnlyList<Session> TimelineSessions,
        DateTimeOffset StartUtc,
        DateTimeOffset EndUtc,
        bool HasHistory)
    {
        public IReadOnlyList<Session> Finished => [.. Sessions.Where(session => session.IsFinished)];
        public IReadOnlyList<Session> Measured =>
            [.. Sessions.Where(session => session.PlannedMinutes > 0 && session.ActualMinutes > 0)];
    }

    private static WindowData Window(AnalyticsInput input, DateOnly from, DateOnly to)
    {
        if (to < from) return new([], [], [], [], default, default, false);
        var start = StartOfDayUtc(from);
        var end = StartOfDayUtc(to.AddDays(1));

        var events = input.Events.Where(item => item.StartUtc < end && item.EndUtc > start).ToList();
        var blocks = input.Blocks.Where(item => item.StartUtc < end && item.EndUtc > start).ToList();
        var sessions = new List<Session>();
        var timelineSessions = new List<Session>();
        var executionsByBlock = input.Executions
            .Where(item => item.TimeBlockId != Guid.Empty)
            .GroupBy(item => item.TimeBlockId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.CompletedAtUtc).First());

        foreach (var block in blocks)
        {
            var finished = block.State is BlockState.Completed or BlockState.Skipped;
            if (!finished) continue;
            executionsByBlock.TryGetValue(block.Id, out var execution);
            var session = new Session(
                block.StartUtc,
                block.StartUtc,
                IsFinished: true,
                IsCompleted: block.State == BlockState.Completed,
                IsSkipped: block.State == BlockState.Skipped,
                WasMoved: block.PreviousStartUtc is not null,
                PlannedMinutes: execution?.PlannedMinutes ?? block.DurationMinutes,
                ActualMinutes: execution?.ActualMinutes ?? 0,
                StartDelayMinutes: block.PreviousStartUtc is { } original
                    ? (int)Math.Round((block.StartUtc - original).TotalMinutes)
                    : 0);
            if (session.When >= start && session.When < end) sessions.Add(session);
            if (OverlapsActual(session, start, end)) timelineSessions.Add(session);
        }

        foreach (var outcome in input.Outcomes ?? [])
        {
            var session = new Session(
                outcome.PlannedStartUtc,
                outcome.StartedAtUtc ?? outcome.PlannedStartUtc,
                outcome.IsFinished,
                outcome.Kind == EventOutcomeKind.Completed,
                outcome.Kind == EventOutcomeKind.Skipped,
                WasMoved: outcome.StartDelayMinutes is > OnTimeToleranceMinutes,
                outcome.PlannedMinutes,
                outcome.ActualMinutes,
                outcome.StartDelayMinutes);
            if (session.When >= start && session.When < end) sessions.Add(session);
            if (OverlapsActual(session, start, end)) timelineSessions.Add(session);
        }

        // A completed execution with no surviving block still counts as work that happened.
        foreach (var execution in input.Executions.Where(item => input.Blocks.All(block => block.Id != item.TimeBlockId)))
        {
            var session = new Session(
                execution.CompletedAtUtc,
                execution.CompletedAtUtc.AddMinutes(-Math.Max(0, execution.ActualMinutes)),
                true, true, false, false,
                execution.PlannedMinutes, execution.ActualMinutes, 0);
            if (session.When >= start && session.When < end) sessions.Add(session);
            if (OverlapsActual(session, start, end)) timelineSessions.Add(session);
        }

        var hasHistory = events.Count > 0 || blocks.Count > 0 || sessions.Count > 0 || timelineSessions.Count > 0;
        return new(events, blocks, sessions, timelineSessions, start, end, hasHistory);
    }

    private static IReadOnlyList<AnalyticsDayPoint> BuildDaily(WindowData data, DateOnly from, DateOnly to)
    {
        var points = new List<AnalyticsDayPoint>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var start = StartOfDayUtc(date);
            var end = StartOfDayUtc(date.AddDays(1));
            var dayEvents = data.Events.Where(item => item.StartUtc < end && item.EndUtc > start).ToList();
            var dayBlocks = data.Blocks.Where(item => item.StartUtc < end && item.EndUtc > start).ToList();
            var scheduled = MergedMinutes(dayEvents, dayBlocks, start, end);
            // Actual work is an interval too. Assigning all of it to the planned start made a
            // completed 23:00-01:00 event look done on the first day and untouched on the next.
            var completed = ActualMinutes(data.TimelineSessions, start, end);
            points.Add(new(date, scheduled, completed, dayEvents.Count + dayBlocks.Count));
        }
        return points;
    }

    private static IReadOnlyList<AnalyticsSlice> BuildCategories(WindowData data)
    {
        var totals = new Dictionary<string, int>();
        foreach (var item in data.Events)
        {
            var key = item.Category.ToString();
            totals[key] = totals.GetValueOrDefault(key) +
                          OverlapMinutes(item.StartUtc, item.EndUtc, data.StartUtc, data.EndUtc);
        }
        var focusMinutes = data.Blocks.Sum(block =>
            OverlapMinutes(block.StartUtc, block.EndUtc, data.StartUtc, data.EndUtc));
        if (focusMinutes > 0) totals["Focus"] = totals.GetValueOrDefault("Focus") + focusMinutes;

        var total = totals.Values.Sum();
        if (total == 0) return [];
        return totals
            .OrderByDescending(pair => pair.Value)
            .Select(pair => new AnalyticsSlice(pair.Key, pair.Value, pair.Value / (double)total))
            .ToList();
    }

    private static IReadOnlyList<AnalyticsHourPoint> BuildHours(WindowData data)
    {
        var buckets = new int[24];
        foreach (var (startUtc, endUtc) in data.Events.Select(item => (item.StartUtc, item.EndUtc))
                     .Concat(data.Blocks.Select(item => (item.StartUtc, item.EndUtc))))
        {
            var cursor = (startUtc < data.StartUtc ? data.StartUtc : startUtc).ToLocalTime();
            var finish = (endUtc > data.EndUtc ? data.EndUtc : endUtc).ToLocalTime();
            // Walk hour by hour so a long item spreads across every bucket it really occupies.
            var guard = 0;
            while (cursor < finish && guard++ < 24 * 40)
            {
                var nextHour = new DateTimeOffset(cursor.Year, cursor.Month, cursor.Day, cursor.Hour, 0, 0, cursor.Offset).AddHours(1);
                var slice = finish < nextHour ? finish : nextHour;
                buckets[cursor.Hour] += Math.Max(0, (int)(slice - cursor).TotalMinutes);
                cursor = slice;
            }
        }
        return [.. Enumerable.Range(0, 24).Select(hour => new AnalyticsHourPoint(hour, buckets[hour]))];
    }

    private static IReadOnlyList<AnalyticsWeekdayPoint> BuildWeekdays(WindowData data, DateOnly from, DateOnly to)
    {
        var minutes = new int[7];
        var counts = new int[7];
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var start = StartOfDayUtc(date);
            var end = StartOfDayUtc(date.AddDays(1));
            var index = ((int)date.DayOfWeek + 6) % 7;
            var dayEvents = data.Events.Where(item => item.StartUtc < end && item.EndUtc > start).ToList();
            var dayBlocks = data.Blocks.Where(item => item.StartUtc < end && item.EndUtc > start).ToList();
            minutes[index] += MergedMinutes(dayEvents, dayBlocks, start, end);
            counts[index] += dayEvents.Count + dayBlocks.Count;
        }
        return [.. Enumerable.Range(0, 7).Select(index => new AnalyticsWeekdayPoint(
            (DayOfWeek)(((int)DayOfWeek.Monday + index) % 7), minutes[index], counts[index]))];
    }

    /// <summary>Total occupied minutes with overlaps counted once, so a double booking cannot inflate load.</summary>
    private static int MergedMinutes(
        IReadOnlyList<CalendarEvent> events,
        IReadOnlyList<TimeBlock> blocks,
        DateTimeOffset? clipStart = null,
        DateTimeOffset? clipEnd = null)
    {
        var intervals = events.Select(item => (item.StartUtc, item.EndUtc))
            .Concat(blocks.Where(block => block.State != BlockState.Cancelled).Select(item => (item.StartUtc, item.EndUtc)))
            .Select(pair => (
                Start: clipStart is { } lower && pair.StartUtc < lower ? lower : pair.StartUtc,
                End: clipEnd is { } upper && pair.EndUtc > upper ? upper : pair.EndUtc))
            .Where(pair => pair.End > pair.Start)
            .OrderBy(pair => pair.Start)
            .ToList();
        if (intervals.Count == 0) return 0;

        var total = 0d;
        var currentStart = intervals[0].Start;
        var currentEnd = intervals[0].End;
        foreach (var (start, end) in intervals.Skip(1))
        {
            if (start <= currentEnd)
            {
                if (end > currentEnd) currentEnd = end;
                continue;
            }
            total += (currentEnd - currentStart).TotalMinutes;
            currentStart = start;
            currentEnd = end;
        }
        total += (currentEnd - currentStart).TotalMinutes;
        return (int)Math.Round(total);
    }

    private static bool OverlapsActual(Session session, DateTimeOffset start, DateTimeOffset end)
        => session.ActualMinutes > 0 && session.ActualStart < end && session.ActualEnd > start;

    private static int ActualMinutes(
        IReadOnlyList<Session> sessions,
        DateTimeOffset start,
        DateTimeOffset end)
        => sessions.Sum(session => OverlapMinutes(session.ActualStart, session.ActualEnd, start, end));

    private static int OverlapMinutes(
        DateTimeOffset itemStart,
        DateTimeOffset itemEnd,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd)
    {
        var start = itemStart < rangeStart ? rangeStart : itemStart;
        var end = itemEnd > rangeEnd ? rangeEnd : itemEnd;
        return Minutes(start, end);
    }

    private static double? Ratio<T>(IReadOnlyList<T> source, Func<T, bool> predicate)
        => source.Count == 0 ? null : source.Count(predicate) / (double)source.Count;

    /// <summary>1.0 means the actual duration matched the plan; the score falls off with relative error.</summary>
    private static double? PlanAccuracy(IReadOnlyList<Session> measured)
    {
        if (measured.Count == 0) return null;
        var score = measured.Average(session =>
        {
            var error = Math.Abs(session.ActualMinutes - session.PlannedMinutes) / (double)session.PlannedMinutes;
            return Math.Max(0, 1 - error);
        });
        return Math.Clamp(score, 0, 1);
    }

    private static double? OnTimeRate(IReadOnlyList<Session> finished)
    {
        var started = finished.Where(session => session.StartDelayMinutes is not null).ToList();
        if (started.Count == 0) return null;
        return started.Count(session => session.StartDelayMinutes <= OnTimeToleranceMinutes) / (double)started.Count;
    }

    private static (int Longest, int Current) Streaks(IReadOnlyList<AnalyticsDayPoint> daily, DateOnly today)
    {
        var longest = 0;
        var running = 0;
        foreach (var day in daily)
        {
            if (day.Date > today) break;
            if (day.CompletedMinutes > 0 || day.ScheduledMinutes > 0)
            {
                running++;
                longest = Math.Max(longest, running);
            }
            else running = 0;
        }

        var current = 0;
        for (var index = daily.Count - 1; index >= 0; index--)
        {
            var day = daily[index];
            if (day.Date > today) continue;
            if (day.CompletedMinutes == 0 && day.ScheduledMinutes == 0) break;
            current++;
        }
        return (longest, current);
    }

    private static IReadOnlyList<AnalyticsInsight> BuildInsights(
        AnalyticsInput input,
        WindowData data,
        IReadOnlyList<AnalyticsDayPoint> daily,
        IReadOnlyList<AnalyticsSlice> categories,
        IReadOnlyList<AnalyticsHourPoint> hours,
        AnalyticsMetric load,
        AnalyticsMetric completion,
        AnalyticsMetric accuracy)
    {
        var insights = new List<AnalyticsInsight>();

        // Systematic estimation error: are sessions consistently longer or shorter than planned?
        var usable = data.Measured;
        if (usable.Count >= MinimumInsightSample)
        {
            var bias = usable.Average(session =>
                (session.ActualMinutes - session.PlannedMinutes) / (double)session.PlannedMinutes);
            if (Math.Abs(bias) >= 0.15)
                insights.Add(new(AnalyticsInsightKind.EstimationBias,
                    [$"{Math.Abs(bias) * 100:0}", bias > 0 ? "over" : "under"],
                    Confidence(usable.Count), usable.Count));
        }

        // A part of day that carries a clearly larger share of the finished work.
        var worked = data.Sessions.Where(session => session.ActualMinutes > 0).ToList();
        if (worked.Count >= MinimumInsightSample)
        {
            var byPart = worked
                .GroupBy(session => PartOfDay(session.When.ToLocalTime()))
                .Select(group => (Part: group.Key, Minutes: group.Sum(item => item.ActualMinutes), Count: group.Count()))
                .OrderByDescending(item => item.Minutes)
                .ToList();
            var total = byPart.Sum(item => item.Minutes);
            if (total > 0 && byPart[0].Minutes / (double)total >= 0.45 && byPart[0].Count >= 3)
                insights.Add(new(AnalyticsInsightKind.StrongTimeOfDay,
                    [byPart[0].Part, $"{byPart[0].Minutes / (double)total * 100:0}"],
                    Confidence(worked.Count), worked.Count, IsPositive: true));
        }

        // Reschedule friction: how often work had to be moved from its original slot.
        var moved = data.Sessions.Count(session => session.WasMoved);
        if (data.Sessions.Count >= MinimumInsightSample && moved / (double)data.Sessions.Count >= 0.3)
            insights.Add(new(AnalyticsInsightKind.ScheduleFriction,
                [$"{moved}", $"{data.Sessions.Count}"], Confidence(data.Sessions.Count), data.Sessions.Count));

        // Skipped sessions are a different signal from partial ones and deserve their own line.
        var finished = data.Finished;
        var skipped = finished.Count(session => session.IsSkipped);
        if (finished.Count >= MinimumInsightSample && skipped / (double)finished.Count >= 0.25)
            insights.Add(new(AnalyticsInsightKind.StartDelay,
                [$"{skipped}", $"{finished.Count}"], Confidence(finished.Count), finished.Count));

        var activeDays = daily.Count(day => day.ScheduledMinutes > 0);
        if (activeDays >= 3)
        {
            if (load.Value >= 0.62)
                insights.Add(new(AnalyticsInsightKind.Overload, [$"{load.Value * 100:0}"], Confidence(activeDays), activeDays));
            else if (load.Value > 0 && load.Value <= 0.12)
                insights.Add(new(AnalyticsInsightKind.Underuse, [$"{load.Value * 100:0}"], Confidence(activeDays), activeDays));
        }

        // One category swallowing the calendar is worth naming explicitly.
        if (categories.Count >= 2 && categories[0].Share >= 0.55)
            insights.Add(new(AnalyticsInsightKind.CategoryDominance,
                [categories[0].Key, $"{categories[0].Share * 100:0}"], Confidence(categories.Sum(slice => slice.Minutes) / 30), categories.Count));

        // Consistency: does the daily load stay even, or swing wildly?
        var loaded = daily.Where(day => day.ScheduledMinutes > 0).Select(day => (double)day.ScheduledMinutes).ToList();
        if (loaded.Count >= 5)
        {
            var mean = loaded.Average();
            var deviation = Math.Sqrt(loaded.Sum(value => Math.Pow(value - mean, 2)) / loaded.Count);
            var spread = mean <= 0 ? 0 : deviation / mean;
            if (spread <= 0.35)
                insights.Add(new(AnalyticsInsightKind.Consistency,
                    [$"{mean / 60:0.#}"], Confidence(loaded.Count), loaded.Count, IsPositive: true));
        }

        return insights;
    }

    private static string PartOfDay(DateTimeOffset value) => value.Hour switch
    {
        < 12 => nameof(PreferredTimeOfDay.Morning),
        < 18 => nameof(PreferredTimeOfDay.Afternoon),
        _ => nameof(PreferredTimeOfDay.Evening)
    };

    /// <summary>Confidence grows with the sample and saturates near 0.95 so nothing ever reads as certain.</summary>
    private static double Confidence(int sample) => Math.Clamp(1 - 1.0 / (1 + sample / 6.0), 0.3, 0.95);

    private static int Minutes(DateTimeOffset start, DateTimeOffset end)
        => Math.Max(0, (int)Math.Round((end - start).TotalMinutes));

    private static DateTimeOffset StartOfDayUtc(DateOnly date)
        => LocalDateRange.StartUtc(date);

    /// <summary>Open tasks that already missed their deadline, surfaced next to the numbers.</summary>
    public static int OverdueTaskCount(IReadOnlyList<TaskItem> tasks, DateTimeOffset now)
        => tasks.Count(task => task.Status is not DomainTaskStatus.Done and not DomainTaskStatus.Cancelled
                               && task.DeadlineUtc is { } deadline && deadline < now);
}
