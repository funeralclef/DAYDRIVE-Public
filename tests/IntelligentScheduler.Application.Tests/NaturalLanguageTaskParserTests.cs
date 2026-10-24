using IntelligentScheduler.Application;
using IntelligentScheduler.Domain;
using Xunit;

namespace IntelligentScheduler.Application.Tests;

public sealed class NaturalLanguageTaskParserTests
{
    private static readonly UserPlanningContext Context = new(
        new DateTimeOffset(2026, 8, 17, 9, 0, 0, TimeSpan.FromHours(3)), "Europe/Kyiv");

    [Fact]
    public async Task RussianPhrase_BecomesStructuredHighPriorityDraft()
    {
        var draft = await new NaturalLanguageTaskParser().ParseTaskAsync(
            "завтра вечером 2 часа Blender, важно", Context, TestContext.Current.CancellationToken);

        Assert.Equal("Blender", draft.Title);
        Assert.Equal(120, draft.EstimatedDurationMin);
        Assert.Equal(85, draft.Priority);
        Assert.True(draft.Splittable);
        Assert.Equal(PreferredTimeOfDay.Evening, draft.PreferredTimeOfDay);
        Assert.Equal(18, draft.DeadlineLocal!.Value.Day);
        Assert.Equal(22, draft.DeadlineLocal.Value.Hour);
    }

    [Fact]
    public async Task MissingDuration_UsesExplicitAssumption()
    {
        var draft = await new NaturalLanguageTaskParser().ParseTaskAsync(
            "Review portfolio tomorrow", Context, TestContext.Current.CancellationToken);

        Assert.Equal(30, draft.EstimatedDurationMin);
        Assert.Contains(draft.Assumptions, x => x.Contains("Duration assumed", StringComparison.Ordinal));
        Assert.True(draft.Confidence.Duration < 0.5);
    }

    [Fact]
    public async Task ExactMinuteDuration_IsNotRoundedToFiveMinutes()
    {
        var draft = await new NaturalLanguageTaskParser().ParseTaskAsync(
            "Review notes for 17 minutes", Context, TestContext.Current.CancellationToken);

        Assert.Equal(17, draft.EstimatedDurationMin);
    }}
