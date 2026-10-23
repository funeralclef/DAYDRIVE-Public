using IntelligentScheduler.Application;
using IntelligentScheduler.Domain;
using Xunit;

namespace IntelligentScheduler.Application.Tests;

public sealed class EventRecurrenceExpanderTests
{
    [Fact]
    public void Weekdays_SkipWeekendAndKeepSeriesIdentity()
    {
        var friday = new DateTimeOffset(2026, 8, 21, 9, 17, 0, TimeSpan.Zero);
        var template = new CalendarEvent
        {
            Title = "Daily stand-up",
            StartUtc = friday,
            EndUtc = friday.AddMinutes(23),
            Category = EventCategory.Work,
            Priority = EventPriority.High,
            Recurrence = EventRecurrence.Weekdays
        };

        var result = EventRecurrenceExpander.Expand(template, friday.AddDays(8));

        Assert.Equal(6, result.Count);
        Assert.Single(result.Select(item => item.SeriesId).Distinct());
        Assert.DoesNotContain(result, item => item.StartUtc.ToLocalTime().DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
        Assert.All(result, item => Assert.Equal(23, (int)(item.EndUtc - item.StartUtc).TotalMinutes));
        Assert.All(result, item => Assert.Equal(EventCategory.Work, item.Category));
        Assert.All(result, item => Assert.Equal(EventPriority.High, item.Priority));
    }
}
