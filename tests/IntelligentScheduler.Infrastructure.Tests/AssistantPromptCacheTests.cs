using IntelligentScheduler.Application;
using IntelligentScheduler.Domain;
using IntelligentScheduler.Infrastructure;
using Xunit;

namespace IntelligentScheduler.Infrastructure.Tests;

/// <summary>
/// The prompt is in two halves on purpose. A local engine re-reads a prompt only as far as it
/// differs from the one it answered last, and the instructions plus the tool catalogue are
/// thousands of tokens; one line of clock at the top of them costs the whole preamble on every
/// single message, which is what made "hello" take as long as a week of planning.
/// </summary>
public sealed class AssistantPromptCacheTests
{
    private static AppSettings Settings() => new()
    {
        AssistantProvider = AssistantProvider.Local,
        AssistantAccessLevel = AssistantAccessLevel.ConfirmAll,
        LanguageCode = "ru"
    };

    private static AssistantRequest Request(DateTimeOffset now) => new(
        "привет",
        now,
        "Europe/Kyiv",
        [new TaskItem { Title = "Write the report", EstimatedDurationMin = 45 }],
        [
            new CalendarEvent
            {
                Title = "Design review",
                StartUtc = now.AddHours(2),
                EndUtc = now.AddHours(3)
            }
        ],
        LanguageCode: "ru");

    [Fact]
    public void TheSystemPrompt_IsIdenticalAsTheClockMoves()
    {
        var settings = Settings();
        var morning = new DateTimeOffset(2026, 8, 29, 9, 14, 0, TimeSpan.FromHours(3));

        var first = AssistantService.BuildSystemPrompt(settings, Request(morning));
        var later = AssistantService.BuildSystemPrompt(settings, Request(morning.AddMinutes(43)));

        Assert.Equal(first, later);
    }

    [Fact]
    public void TheSystemPrompt_IsIdenticalAsTheScheduleChanges()
    {
        var settings = Settings();
        var now = new DateTimeOffset(2026, 8, 29, 9, 14, 0, TimeSpan.FromHours(3));
        var busier = Request(now) with
        {
            Events = [.. Request(now).Events, new CalendarEvent
            {
                Title = "Standup",
                StartUtc = now.AddHours(5),
                EndUtc = now.AddHours(5).AddMinutes(15)
            }]
        };

        Assert.Equal(AssistantService.BuildSystemPrompt(settings, Request(now)),
            AssistantService.BuildSystemPrompt(settings, busier));
    }

    [Fact]
    public void TheLiveTurn_CarriesTheClockAndTheSchedule()
    {
        var now = new DateTimeOffset(2026, 8, 29, 9, 14, 0, TimeSpan.FromHours(3));

        var live = AssistantService.BuildLiveContext(Settings(), Request(now));

        Assert.Contains("2026-08-29 09:14", live, StringComparison.Ordinal);
        Assert.Contains("Design review", live, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLiveTurn_KeepsTheScheduleFromAModelThatMayNotSeeIt()
    {
        var now = new DateTimeOffset(2026, 8, 29, 9, 14, 0, TimeSpan.FromHours(3));
        var external = Settings();
        external.AssistantProvider = AssistantProvider.OpenAiCompatible;
        external.ShareContextWithExternalAi = false;

        var live = AssistantService.BuildLiveContext(external, Request(now));

        Assert.DoesNotContain("Design review", live, StringComparison.Ordinal);
        Assert.Contains("2026-08-29 09:14", live, StringComparison.Ordinal);
    }
}
