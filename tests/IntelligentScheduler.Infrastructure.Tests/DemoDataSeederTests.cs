using IntelligentScheduler.Application;
using Xunit;

namespace IntelligentScheduler.Infrastructure.Tests;

/// <summary>
/// The demo dataset exists to make the analytics panel testable, so these tests check that it
/// actually fills the panel rather than merely inserting rows.
/// </summary>
public sealed class DemoDataSeederTests
{
    private static async Task<AnalyticsReport> SeedAndBuildAsync(
        AssistantTestHarness harness, int rangeDays, CancellationToken cancellationToken)
    {
        await DemoDataSeeder.SeedAsync(harness.Store, harness.Store, harness.Store, harness.Store,
            cancellationToken: cancellationToken, outcomes: harness.Outcomes);

        var now = DateTimeOffset.Now;
        // Two windows of history so every metric can be compared with the preceding period.
        var from = now.AddDays(-(rangeDays * 2 + 1)).ToUniversalTime();
        var to = now.AddDays(1).ToUniversalTime();

        // SqliteStore implements both repositories, so the interface has to be named explicitly.
        ICalendarRepository calendar = harness.Store;
        IBlockRepository blocks = harness.Store;

        return ScheduleAnalytics.Build(new AnalyticsInput(
            now, rangeDays,
            await calendar.GetRangeAsync(from, to, cancellationToken),
            await blocks.GetHistoryAsync(from, to, cancellationToken),
            await harness.Store.GetRecentAsync(from, cancellationToken),
            await harness.Store.GetAllAsync(cancellationToken),
            Outcomes: await harness.Outcomes.GetRangeAsync(from, to, cancellationToken)));
    }

    [Fact]
    public async Task SeededDatabaseFillsEveryChartAndMetric()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);

        var report = await SeedAndBuildAsync(harness, 28, cancellationToken);

        Assert.True(report.HasData);
        Assert.Equal(28, report.Daily.Count);
        Assert.NotEmpty(report.Categories);
        Assert.Contains(report.Hours, hour => hour.Minutes > 0);
        Assert.Contains(report.Weekdays, day => day.Minutes > 0);
        Assert.InRange(report.BusiestHour, 0, 23);

        // Every headline metric must rest on a real sample, otherwise the tile reads "no data".
        // Skipped sessions must reach the statistics, otherwise completion is 100% by construction.
        Assert.InRange(report.CompletionRate.Value, 0.5, 0.99);
        Assert.True(report.ScheduledHours.HasEnoughData);
        Assert.True(report.CompletionRate.HasEnoughData);
        Assert.True(report.PlanAccuracy.HasEnoughData);
        Assert.True(report.OnTimeStartRate.HasEnoughData);
        Assert.True(report.FocusHours.HasEnoughData);

        // And each one must have a previous window to compare against.
        Assert.NotNull(report.ScheduledHours.PreviousValue);
        Assert.NotNull(report.CompletionRate.PreviousValue);
        Assert.NotNull(report.FocusHours.PreviousValue);
    }

    [Fact]
    public async Task SeededDatabaseTriggersTheInsightsItWasShapedFor()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);

        var report = await SeedAndBuildAsync(harness, 28, cancellationToken);
        var kinds = report.Insights.Select(insight => insight.Kind).ToList();

        // Sessions overrun by ~25% by construction.
        var bias = Assert.Single(report.Insights, insight => insight.Kind == AnalyticsInsightKind.EstimationBias);
        Assert.Equal("over", bias.Arguments[1]);
        // Focus sessions are weighted towards the morning by construction.
        Assert.Contains(AnalyticsInsightKind.StrongTimeOfDay, kinds);
        // Roughly a third of blocks carry an original start, i.e. they were moved.
        Assert.Contains(AnalyticsInsightKind.ScheduleFriction, kinds);

        Assert.All(report.Insights, insight =>
        {
            Assert.True(insight.SampleSize > 0);
            Assert.InRange(insight.Confidence, 0.3, 0.95);
        });
    }

    [Fact]
    public async Task NinetyDayRangeAlsoHasAComparisonWindow()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);

        var report = await SeedAndBuildAsync(harness, 90, cancellationToken);

        Assert.Equal(90, report.Daily.Count);
        Assert.True(report.HasData);
        Assert.NotNull(report.ScheduledHours.PreviousValue);
        Assert.True(report.ScheduledHours.PreviousValue > 0);
    }

    [Fact]
    public async Task SeedingIsDeterministic()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var first = await AssistantTestHarness.CreateAsync(cancellationToken);
        await using var second = await AssistantTestHarness.CreateAsync(cancellationToken);

        var one = await DemoDataSeeder.SeedAsync(first.Store, first.Store, first.Store, first.Store,
            cancellationToken: cancellationToken);
        var two = await DemoDataSeeder.SeedAsync(second.Store, second.Store, second.Store, second.Store,
            cancellationToken: cancellationToken);

        Assert.Equal(one, two);
    }

    [Fact]
    public async Task GeneratedEventsAreMarkedSoTheyCanBeToldApart()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);

        await DemoDataSeeder.SeedAsync(harness.Store, harness.Store, harness.Store, harness.Store,
            new DemoDataOptions(Days: 14, FutureDays: 2), cancellationToken);

        var stored = await ((ICalendarRepository)harness.Store).GetAllAsync(cancellationToken);
        Assert.NotEmpty(stored);
        Assert.All(stored, item => Assert.Equal(DemoDataSeeder.SourceMarker, item.Source));
    }

    [Fact]
    public async Task OverlappingEventsAreStillCountedOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);

        var report = await SeedAndBuildAsync(harness, 28, cancellationToken);

        // 28 days can never hold more than 28 * 24 hours, and the generator books far less;
        // a merge bug would show up here as an impossible figure.
        Assert.InRange(report.ScheduledHours.Value, 20, 28 * 16);
        Assert.InRange(report.Load.Value, 0.02, 0.9);
    }
}
