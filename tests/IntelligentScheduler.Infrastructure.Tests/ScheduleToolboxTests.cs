using System.Text.Json;
using IntelligentScheduler.Application;
using IntelligentScheduler.Domain;
using Xunit;

namespace IntelligentScheduler.Infrastructure.Tests;

public sealed class ScheduleToolboxTests
{
    private static AssistantToolInvocation Call(string name, string arguments = "{}")
        => new("call_1", name, arguments);

    [Fact]
    public void EveryToolSchemaIsValidJsonSchemaObject()
    {
        Assert.NotEmpty(ScheduleToolbox.Definitions);
        foreach (var tool in ScheduleToolbox.Definitions)
        {
            using var schema = JsonDocument.Parse(tool.ParametersJson);
            Assert.Equal(JsonValueKind.Object, schema.RootElement.ValueKind);
            Assert.Equal("object", schema.RootElement.GetProperty("type").GetString());
            Assert.True(schema.RootElement.TryGetProperty("properties", out _), $"{tool.Name} has no properties");
            // Tool names must satisfy the character set every OpenAI-compatible endpoint enforces.
            Assert.Matches("^[a-zA-Z0-9_-]{1,64}$", tool.Name);
        }
    }

    [Fact]
    public async Task GetSchedule_ReturnsRealEventsWithIds()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var (dayStart, _) = ScheduleActionExecutor.DayRange(today);
        await harness.Store.AddAsync(new CalendarEvent
        {
            Title = "Стендап",
            StartUtc = dayStart.AddHours(9),
            EndUtc = dayStart.AddHours(9).AddMinutes(30),
            Category = EventCategory.Work
        }, cancellationToken);

        var result = await harness.Toolbox.InvokeAsync(
            Call("get_schedule", $$"""{"fromDate":"{{today:yyyy-MM-dd}}","toDate":"{{today:yyyy-MM-dd}}"}"""), cancellationToken);

        Assert.False(result.IsError);
        using var json = JsonDocument.Parse(result.Content);
        var found = json.RootElement.GetProperty("events").EnumerateArray().ToList();
        Assert.Single(found);
        Assert.Equal("Стендап", found[0].GetProperty("title").GetString());
        Assert.True(Guid.TryParse(found[0].GetProperty("id").GetString(), out _));
    }

    [Fact]
    public async Task FindFreeSlots_SkipsTimeThatIsAlreadyBooked()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        var date = DateOnly.FromDateTime(DateTime.Today).AddDays(3);
        var (dayStart, _) = ScheduleActionExecutor.DayRange(date);
        await harness.Store.AddAsync(new CalendarEvent
        {
            Title = "Занято",
            StartUtc = dayStart.AddHours(10),
            EndUtc = dayStart.AddHours(12)
        }, cancellationToken);

        var result = await harness.Toolbox.InvokeAsync(
            Call("find_free_slots", $$"""{"fromDate":"{{date:yyyy-MM-dd}}","durationMinutes":60,"earliestHour":9,"latestHour":18}"""),
            cancellationToken);

        using var json = JsonDocument.Parse(result.Content);
        var slots = json.RootElement.GetProperty("freeSlots").EnumerateArray()
            .Select(slot => (
                Start: DateTimeOffset.Parse(slot.GetProperty("start").GetString()!),
                End: DateTimeOffset.Parse(slot.GetProperty("end").GetString()!)))
            .ToList();

        Assert.NotEmpty(slots);
        var busyStart = dayStart.AddHours(10).ToLocalTime();
        var busyEnd = dayStart.AddHours(12).ToLocalTime();
        Assert.All(slots, slot => Assert.False(slot.Start < busyEnd && slot.End > busyStart,
            $"slot {slot.Start:t}-{slot.End:t} overlaps the booked window"));
    }

    [Fact]
    public async Task CriticalTool_UnderAutoSafe_IsProposedNotApplied()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        await harness.ConfigureAsync(settings => settings.AssistantAccessLevel = AssistantAccessLevel.AutoSafe, cancellationToken);
        var (dayStart, dayEnd) = ScheduleActionExecutor.DayRange(DateOnly.FromDateTime(DateTime.Today));
        await harness.Store.AddAsync(new CalendarEvent { Title = "Важное", StartUtc = dayStart.AddHours(9), EndUtc = dayStart.AddHours(10) }, cancellationToken);
        var stored = Assert.Single(await harness.Store.GetRangeAsync(dayStart, dayEnd, cancellationToken));

        var deletion = await harness.Toolbox.InvokeAsync(
            Call("delete_event", $$"""{"targetId":"{{stored.Id}}"}"""), cancellationToken);

        Assert.NotNull(deletion.PendingAction);
        Assert.False(deletion.Applied);
        Assert.Single(await harness.Store.GetRangeAsync(dayStart, dayEnd, cancellationToken));
    }

    [Fact]
    public async Task SafeTool_UnderAutoSafe_IsAppliedImmediately()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        await harness.ConfigureAsync(settings => settings.AssistantAccessLevel = AssistantAccessLevel.AutoSafe, cancellationToken);

        var result = await harness.Toolbox.InvokeAsync(
            Call("create_task", """{"title":"Написать отчёт","durationMinutes":45,"priority":70}"""), cancellationToken);

        Assert.True(result.Applied);
        Assert.Null(result.PendingAction);
        var task = Assert.Single(await harness.Store.GetAllAsync(cancellationToken));
        Assert.Equal("Написать отчёт", task.Title);
        Assert.Equal(45, task.EstimatedDurationMin);
    }

    [Fact]
    public async Task CreateEvent_AcceptsDurationInsteadOfAnEndTime()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        var start = DateTimeOffset.Now.Date.AddDays(1).AddHours(14);

        var summary = await harness.Executor.ApplyAsync(new AssistantScheduleAction(
            AssistantActionKind.CreateEvent, Title: "Ревью",
            StartLocal: new DateTimeOffset(start, TimeZoneInfo.Local.GetUtcOffset(start)),
            DurationMinutes: 90), cancellationToken);

        Assert.Contains("Ревью", summary, StringComparison.Ordinal);
        var stored = Assert.Single(await harness.Store.GetRangeAsync(
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(3), cancellationToken));
        Assert.Equal(90, (stored.EndUtc - stored.StartUtc).TotalMinutes);
    }

    [Fact]
    public async Task UpdateEvent_MovingWithoutAnEndKeepsTheOriginalLength()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        var (dayStart, _) = ScheduleActionExecutor.DayRange(DateOnly.FromDateTime(DateTime.Today).AddDays(1));
        await harness.Store.AddAsync(new CalendarEvent { Title = "Созвон", StartUtc = dayStart.AddHours(9), EndUtc = dayStart.AddHours(10) }, cancellationToken);
        var stored = Assert.Single(await harness.Store.GetRangeAsync(dayStart, dayStart.AddDays(1), cancellationToken));

        var moved = stored.StartUtc.AddHours(4).ToLocalTime();
        await harness.Executor.ApplyAsync(new AssistantScheduleAction(
            AssistantActionKind.UpdateEvent, TargetId: stored.Id, StartLocal: moved), cancellationToken);

        var updated = Assert.Single(await harness.Store.GetRangeAsync(dayStart, dayStart.AddDays(1), cancellationToken));
        Assert.Equal(60, (updated.EndUtc - updated.StartUtc).TotalMinutes);
        Assert.Equal(moved.ToUniversalTime(), updated.StartUtc);
    }

    [Fact]
    public async Task StaleTargetId_ProducesAnActionableError()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);

        var result = await harness.Toolbox.InvokeAsync(
            Call("update_event", """{"targetId":"22222222-2222-2222-2222-222222222222","title":"Новое"}"""), cancellationToken);

        Assert.True(result.IsError);
        Assert.Contains("no longer", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MemoryTools_RoundTripThroughTheStore()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);

        await harness.Toolbox.InvokeAsync(
            Call("remember_about_user", """{"key":"sport","value":"Бегает по вторникам","category":"Routine"}"""), cancellationToken);
        var listed = await harness.Toolbox.InvokeAsync(Call("list_memories"), cancellationToken);
        Assert.Contains("Бегает по вторникам", listed.Content, StringComparison.Ordinal);

        var forgotten = await harness.Toolbox.InvokeAsync(Call("forget_about_user", """{"key":"sport"}"""), cancellationToken);
        Assert.True(forgotten.Applied);
        Assert.Empty(await harness.Chat.GetAllAsync(cancellationToken));
    }

    [Fact]
    public async Task MemoryTool_RespectsTheSettingsToggle()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        await harness.ConfigureAsync(settings => settings.AssistantMemoryEnabled = false, cancellationToken);

        var result = await harness.Toolbox.InvokeAsync(
            Call("remember_about_user", """{"key":"x","value":"y"}"""), cancellationToken);

        Assert.True(result.IsError);
        Assert.Empty(await harness.Chat.GetAllAsync(cancellationToken));
    }

    [Fact]
    public async Task WithoutConsent_ReadingToolsAreNeitherOfferedNorAnswered()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        await harness.ConfigureAsync(settings =>
        {
            settings.AssistantProvider = AssistantProvider.OpenAiCompatible;
            settings.ShareContextWithExternalAi = false;
        }, cancellationToken);
        var (dayStart, _) = ScheduleActionExecutor.DayRange(DateOnly.FromDateTime(DateTime.Today));
        await harness.Store.AddAsync(new CalendarEvent
        {
            Title = "Приватная встреча", StartUtc = dayStart.AddHours(9), EndUtc = dayStart.AddHours(10)
        }, cancellationToken);

        var offered = await harness.Toolbox.GetToolsAsync(cancellationToken);
        Assert.DoesNotContain(offered, tool => tool.Name == "get_schedule");
        Assert.Contains(offered, tool => tool.Name == "create_event");

        // Calling it by name anyway must still not leak the title.
        var result = await harness.Toolbox.InvokeAsync(Call("get_schedule"), cancellationToken);
        Assert.True(result.IsError);
        Assert.DoesNotContain("Приватная встреча", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithConsent_ReadingToolsComeBack()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        await harness.ConfigureAsync(settings =>
        {
            settings.AssistantProvider = AssistantProvider.OpenAiCompatible;
            settings.ShareContextWithExternalAi = true;
        }, cancellationToken);

        var offered = await harness.Toolbox.GetToolsAsync(cancellationToken);

        Assert.Contains(offered, tool => tool.Name == "get_schedule");
        Assert.Equal(ScheduleToolbox.Definitions.Count, offered.Count);
    }

    [Fact]
    public async Task UnknownTool_IsReportedRatherThanThrown()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);

        var result = await harness.Toolbox.InvokeAsync(Call("teleport"), cancellationToken);

        Assert.True(result.IsError);
        Assert.Contains("teleport", result.Content, StringComparison.Ordinal);
    }
}
