using IntelligentScheduler.Application;
using IntelligentScheduler.Domain;
using Xunit;

namespace IntelligentScheduler.Application.Tests;

public sealed class ScheduleAnalyticsTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 23, 18, 0, 0, TimeSpan.FromHours(3));

    private static DateTimeOffset At(int daysAgo, int hour, int minute = 0)
    {
        var local = Now.LocalDateTime.Date.AddDays(-daysAgo).AddHours(hour).AddMinutes(minute);
        return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified),
            TimeZoneInfo.Local.GetUtcOffset(local)).ToUniversalTime();
    }

    private static CalendarEvent Event(int daysAgo, int hour, int lengthMinutes, EventCategory category = EventCategory.Work)
        => new()
        {
            Title = $"E{daysAgo}-{hour}",
            StartUtc = At(daysAgo, hour),
            EndUtc = At(daysAgo, hour).AddMinutes(lengthMinutes),
            Category = category
        };

    private static DateTimeOffset At(DateOnly date, int hour, int minute = 0)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(new TimeOnly(hour, minute)), DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUniversalTime();
    }

    private static AnalyticsInput Input(
        IEnumerable<CalendarEvent>? events = null,
        IEnumerable<TimeBlock>? blocks = null,
        IEnumerable<TaskExecution>? executions = null,
        int rangeDays = 7,
        IEnumerable<EventOutcome>? outcomes = null,
        AnalyticsPeriod? period = null)
        => new(Now, rangeDays, [.. events ?? []], [.. blocks ?? []], [.. executions ?? []], [],
            Outcomes: [.. outcomes ?? []], Period: period);

    [Fact]
    public void EmptyHistoryProducesAReportThatSaysItHasNoData()
    {
        var report = ScheduleAnalytics.Build(Input());

        Assert.False(report.HasData);
        Assert.Equal(7, report.Daily.Count);
        Assert.Empty(report.Categories);
        Assert.Empty(report.Insights);
        Assert.Equal(0, report.ScheduledHours.Value);
    }

    [Fact]
    public void OverlappingItemsAreCountedOnce()
    {
        // Two events covering 09:00-11:00 and 10:00-12:00 occupy three hours, not four.
        var report = ScheduleAnalytics.Build(Input([Event(1, 9, 120), Event(1, 10, 120)]));

        Assert.Equal(3, report.ScheduledHours.Value, 3);
        Assert.Equal(180, report.Daily.Single(day => day.ScheduledMinutes > 0).ScheduledMinutes);
    }

    [Theory]
    [InlineData(120, 60)]
    [InlineData(90, 30)]
    public void OvernightCompletionIsSplitAcrossTheDaysItActuallyOccupies(
        int actualMinutes,
        int expectedSecondDayMinutes)
    {
        var start = At(1, 23);
        var calendarEvent = new CalendarEvent
        {
            StartUtc = start,
            EndUtc = start.AddMinutes(120),
            Category = EventCategory.Work
        };
        var outcome = new EventOutcome
        {
            EventId = calendarEvent.Id,
            Kind = EventOutcomeKind.Completed,
            PlannedStartUtc = start,
            PlannedMinutes = 120,
            ActualMinutes = actualMinutes
        };

        var report = ScheduleAnalytics.Build(Input([calendarEvent], outcomes: [outcome]));
        var firstDate = DateOnly.FromDateTime(start.LocalDateTime);

        Assert.Equal(60, report.Daily.Single(day => day.Date == firstDate).CompletedMinutes);
        Assert.Equal(expectedSecondDayMinutes,
            report.Daily.Single(day => day.Date == firstDate.AddDays(1)).CompletedMinutes);
        Assert.Equal(actualMinutes, report.Daily.Sum(day => day.CompletedMinutes));
        Assert.Equal(actualMinutes / 60d, report.FocusHours.Value, 3);
        Assert.Equal(1, report.CompletionRate.SampleSize);
        Assert.Equal(1, report.CompletionRate.Value);
    }

    [Fact]
    public void CalendarWeekRunsMondayThroughSundayAndComparesThePriorWeek()
    {
        var today = new DateOnly(2026, 8, 19); // Wednesday
        var now = At(today, 18);
        CalendarEvent On(DateOnly date, int minutes) => new()
        {
            StartUtc = At(date, 9),
            EndUtc = At(date, 9).AddMinutes(minutes),
            Category = EventCategory.Work
        };

        var report = ScheduleAnalytics.Build(new AnalyticsInput(
            now, 7,
            [On(new DateOnly(2026, 8, 18), 60),
             On(new DateOnly(2026, 8, 11), 120),
             On(new DateOnly(2026, 8, 9), 240)],
            [], [], []));

        Assert.Equal(new DateOnly(2026, 8, 17), report.From);
        Assert.Equal(new DateOnly(2026, 8, 23), report.To);
        Assert.Equal(7, report.Daily.Count);
        Assert.Equal(1, report.ScheduledHours.Value, 3);
        Assert.Equal(2, report.ScheduledHours.PreviousValue!.Value, 3);
    }

    [Theory]
    [InlineData(30, 2026, 8, 1, 2026, 8, 31, 31)]
    [InlineData(91, 2026, 6, 1, 2026, 8, 31, 92)]
    public void MonthSelectorsBuildCompleteCalendarPeriods(
        int selectorDays,
        int fromYear, int fromMonth, int fromDay,
        int toYear, int toMonth, int toDay,
        int expectedDays)
    {
        var report = ScheduleAnalytics.Build(Input(rangeDays: selectorDays));

        Assert.Equal(new DateOnly(fromYear, fromMonth, fromDay), report.From);
        Assert.Equal(new DateOnly(toYear, toMonth, toDay), report.To);
        Assert.Equal(expectedDays, report.RangeDays);
        Assert.Equal(expectedDays, report.Daily.Count);
    }

    [Fact]
    public void SundayEventContributesOnlyItsMondaySliceToTheNewWeek()
    {
        var today = new DateOnly(2026, 8, 19);
        var sundayStart = At(new DateOnly(2026, 8, 16), 23);
        var calendarEvent = new CalendarEvent
        {
            StartUtc = sundayStart,
            EndUtc = sundayStart.AddMinutes(120),
            Category = EventCategory.Work
        };
        var outcome = new EventOutcome
        {
            EventId = calendarEvent.Id,
            Kind = EventOutcomeKind.Completed,
            PlannedStartUtc = sundayStart,
            PlannedMinutes = 120,
            ActualMinutes = 120
        };

        var report = ScheduleAnalytics.Build(new AnalyticsInput(
            At(today, 18), 7, [calendarEvent], [], [], [], Outcomes: [outcome]));

        var monday = Assert.Single(report.Daily, day => day.Date == new DateOnly(2026, 8, 17));
        Assert.Equal(60, monday.ScheduledMinutes);
        Assert.Equal(60, monday.CompletedMinutes);
        Assert.Equal(1, report.ScheduledHours.Value, 3);
        Assert.Equal(1, report.FocusHours.Value, 3);
        // The completion verdict belongs to the week in which the event began, so it remains
        // one sample rather than being counted again on Monday.
        Assert.Equal(0, report.CompletionRate.SampleSize);
    }

    [Fact]
    public void HourHistogramSpreadsALongItemAcrossEveryHourItTouches()
    {
        var report = ScheduleAnalytics.Build(Input([Event(1, 9, 150)]));

        Assert.Equal(60, report.Hours[9].Minutes);
        Assert.Equal(60, report.Hours[10].Minutes);
        Assert.Equal(30, report.Hours[11].Minutes);
        Assert.Equal(9, report.BusiestHour);
    }

    [Fact]
    public void CategoryBreakdownSharesAddUpToOne()
    {
        var report = ScheduleAnalytics.Build(Input(
        [
            Event(1, 9, 60, EventCategory.Work),
            Event(2, 9, 60, EventCategory.Work),
            Event(3, 9, 60, EventCategory.Health)
        ]));

        Assert.Equal(1, report.Categories.Sum(slice => slice.Share), 3);
        Assert.Equal("Work", report.Categories[0].Key);
        Assert.Equal(2.0 / 3, report.Categories[0].Share, 3);
    }

    [Fact]
    public void CompletionRateCountsOnlyFinishedSessions()
    {
        var blocks = new[]
        {
            new TimeBlock { StartUtc = At(1, 9), EndUtc = At(1, 10), State = BlockState.Completed },
            new TimeBlock { StartUtc = At(2, 9), EndUtc = At(2, 10), State = BlockState.Completed },
            new TimeBlock { StartUtc = At(3, 9), EndUtc = At(3, 10), State = BlockState.Skipped },
            // Still planned, so it must not drag the rate down.
            new TimeBlock { StartUtc = At(0, 21), EndUtc = At(0, 22), State = BlockState.Planned }
        };

        var report = ScheduleAnalytics.Build(Input(blocks: blocks));

        Assert.Equal(2.0 / 3, report.CompletionRate.Value, 3);
        Assert.Equal(3, report.CompletionRate.SampleSize);
    }

    [Fact]
    public void PlanAccuracyFallsOffWithRelativeError()
    {
        var perfect = ScheduleAnalytics.Build(Input(executions:
        [
            new TaskExecution { PlannedMinutes = 60, ActualMinutes = 60, CompletedAtUtc = At(1, 10) }
        ]));
        Assert.Equal(1, perfect.PlanAccuracy.Value, 3);

        var halfOver = ScheduleAnalytics.Build(Input(executions:
        [
            new TaskExecution { PlannedMinutes = 60, ActualMinutes = 90, CompletedAtUtc = At(1, 10) }
        ]));
        Assert.Equal(0.5, halfOver.PlanAccuracy.Value, 3);
    }

    [Fact]
    public void EstimationBiasInsightAppearsOnlyWithEnoughSamples()
    {
        TaskExecution Over(int daysAgo) => new() { PlannedMinutes = 30, ActualMinutes = 60, CompletedAtUtc = At(daysAgo, 10) };

        var tooFew = ScheduleAnalytics.Build(Input(executions: [Over(1), Over(2)]));
        Assert.DoesNotContain(tooFew.Insights, insight => insight.Kind == AnalyticsInsightKind.EstimationBias);

        var enough = ScheduleAnalytics.Build(Input(executions: [Over(1), Over(2), Over(3), Over(4), Over(5)]));
        var bias = Assert.Single(enough.Insights, insight => insight.Kind == AnalyticsInsightKind.EstimationBias);
        Assert.Equal("100", bias.Arguments[0]);
        Assert.Equal("over", bias.Arguments[1]);
        Assert.True(bias.Confidence is > 0.3 and < 0.96);
    }

    [Fact]
    public void StrongTimeOfDayInsightNamesThePartOfDayThatCarriesTheWork()
    {
        TaskExecution Morning(int daysAgo) => new() { PlannedMinutes = 60, ActualMinutes = 60, CompletedAtUtc = At(daysAgo, 9) };
        TaskExecution Evening(int daysAgo) => new() { PlannedMinutes = 60, ActualMinutes = 20, CompletedAtUtc = At(daysAgo, 20) };

        var report = ScheduleAnalytics.Build(Input(executions:
            [Morning(1), Morning(2), Morning(3), Morning(4), Evening(5)]));

        var insight = Assert.Single(report.Insights, item => item.Kind == AnalyticsInsightKind.StrongTimeOfDay);
        Assert.Equal(nameof(PreferredTimeOfDay.Morning), insight.Arguments[0]);
        Assert.True(insight.IsPositive);
    }

    [Fact]
    public void OverloadAndUnderuseAreMutuallyExclusive()
    {
        // Six days booked 07:00-20:00 is 78h against a 112h waking week: 70% load.
        var packed = Enumerable.Range(1, 6).Select(day => Event(day, 7, 13 * 60)).ToList();
        var busy = ScheduleAnalytics.Build(Input(packed));
        Assert.Contains(busy.Insights, insight => insight.Kind == AnalyticsInsightKind.Overload);
        Assert.DoesNotContain(busy.Insights, insight => insight.Kind == AnalyticsInsightKind.Underuse);

        var light = ScheduleAnalytics.Build(Input(Enumerable.Range(1, 4).Select(day => Event(day, 9, 20))));
        Assert.Contains(light.Insights, insight => insight.Kind == AnalyticsInsightKind.Underuse);
        Assert.DoesNotContain(light.Insights, insight => insight.Kind == AnalyticsInsightKind.Overload);
    }

    [Fact]
    public void StreakStopsAtTheFirstEmptyDay()
    {
        // Days 0, 1 and 2 are active; day 3 is empty, so the current streak is three.
        var report = ScheduleAnalytics.Build(Input([Event(0, 9, 60), Event(1, 9, 60), Event(2, 9, 60), Event(4, 9, 60)]));

        Assert.Equal(3, report.CurrentStreakDays);
        Assert.Equal(3, report.LongestStreakDays);
    }

    [Fact]
    public void PreviousWindowIsUsedForTheComparisonArrow()
    {
        // One hour in the current week, two hours in the one before it.
        var report = ScheduleAnalytics.Build(Input([Event(1, 9, 60), Event(8, 9, 120)]));

        Assert.Equal(1, report.ScheduledHours.Value, 3);
        Assert.Equal(2, report.ScheduledHours.PreviousValue!.Value, 3);
        Assert.Equal(-1, report.ScheduledHours.Delta!.Value, 3);
    }

    [Fact]
    public void WeekdayBreakdownStartsOnMonday()
    {
        var report = ScheduleAnalytics.Build(Input([Event(1, 9, 60)]));

        Assert.Equal(7, report.Weekdays.Count);
        Assert.Equal(DayOfWeek.Monday, report.Weekdays[0].Day);
        Assert.Equal(60, report.Weekdays.Sum(day => day.Minutes));
    }

    [Fact]
    public void OverdueTasksAreCountedFromTheirDeadline()
    {
        var tasks = new[]
        {
            new TaskItem { Title = "late", DeadlineUtc = Now.AddDays(-1) },
            new TaskItem { Title = "upcoming", DeadlineUtc = Now.AddDays(1) },
            new TaskItem { Title = "done late", DeadlineUtc = Now.AddDays(-1), Status = Domain.TaskStatus.Done }
        };

        Assert.Equal(1, ScheduleAnalytics.OverdueTaskCount(tasks, Now));
    }
}
