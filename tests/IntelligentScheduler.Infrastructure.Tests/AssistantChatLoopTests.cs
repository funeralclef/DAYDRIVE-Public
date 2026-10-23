using System.Text.Json;
using IntelligentScheduler.Application;
using IntelligentScheduler.Domain;
using Xunit;

namespace IntelligentScheduler.Infrastructure.Tests;

/// <summary>
/// End-to-end coverage of the conversation loop against a scripted OpenAI-compatible endpoint.
/// These are the cases that used to surface as "the API connection just drops".
/// </summary>
public sealed class AssistantChatLoopTests
{
    private static async Task<AssistantTestHarness> ApiHarnessAsync(FakeChatEndpoint endpoint, CancellationToken cancellationToken,
        AssistantAccessLevel accessLevel = AssistantAccessLevel.ConfirmAll)
    {
        var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        await harness.ConfigureAsync(settings =>
        {
            settings.AssistantProvider = AssistantProvider.OpenAiCompatible;
            settings.AssistantEndpoint = endpoint.BaseAddress;
            settings.AssistantModel = "test-model";
            settings.ShareContextWithExternalAi = true;
            settings.AssistantAccessLevel = accessLevel;
            settings.McpToolsEnabled = false;
        }, cancellationToken);
        return harness;
    }

    /// <summary>
    /// The next fortnight rides along with the question. Without it, "what have I got today"
    /// costs a tool call and a second full round trip to answer something the app already knew
    /// before it opened the connection.
    /// </summary>
    [Fact]
    public async Task TheScheduleTravelsWithTheQuestion()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var endpoint = new FakeChatEndpoint();
        endpoint.EnqueueText("Сегодня у вас стендап в 09:00.");

        await using var harness = await ApiHarnessAsync(endpoint, cancellationToken);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var (dayStart, _) = ScheduleActionExecutor.DayRange(today);
        var standup = new CalendarEvent
        {
            Title = "Стендап",
            StartUtc = dayStart.AddHours(9),
            EndUtc = dayStart.AddHours(9).AddMinutes(30)
        };
        await harness.Store.AddAsync(standup, cancellationToken);
        await harness.Store.AddAsync(new TaskItem { Title = "Дописать отчёт" }, cancellationToken);

        var reply = await harness.CreateAssistant().AskAsync(new AssistantAsk("Что у меня сегодня?"), cancellationToken);

        Assert.False(reply.IsError);
        // One request, no tool call: everything needed was already in the prompt.
        var sent = Assert.Single(endpoint.Requests);
        Assert.Contains("Стендап", sent, StringComparison.Ordinal);
        Assert.Contains("Дописать отчёт", sent, StringComparison.Ordinal);
        // Ids travel too, so editing something visible does not need a lookup first.
        Assert.Contains(standup.Id.ToString(), sent, StringComparison.Ordinal);
        Assert.Empty(reply.Trace);
    }

    /// <summary>Consent withheld means the snapshot is not built at all, not merely not shown.</summary>
    [Fact]
    public async Task WithoutConsent_NoScheduleIsSentToAnExternalModel()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var endpoint = new FakeChatEndpoint();
        endpoint.EnqueueText("Расскажите, что у вас запланировано.");

        await using var harness = await ApiHarnessAsync(endpoint, cancellationToken);
        await harness.ConfigureAsync(settings => settings.ShareContextWithExternalAi = false, cancellationToken);
        var (dayStart, _) = ScheduleActionExecutor.DayRange(DateOnly.FromDateTime(DateTime.Today));
        await harness.Store.AddAsync(new CalendarEvent
        {
            Title = "Совершенно секретно",
            StartUtc = dayStart.AddHours(9),
            EndUtc = dayStart.AddHours(10)
        }, cancellationToken);

        await harness.CreateAssistant().AskAsync(new AssistantAsk("Что у меня сегодня?"), cancellationToken);

        var sent = Assert.Single(endpoint.Requests);
        Assert.DoesNotContain("Совершенно секретно", sent, StringComparison.Ordinal);
        // The reading tools are not offered either, so the model cannot plan around them.
        Assert.DoesNotContain("\"name\":\"get_schedule\"", sent, StringComparison.Ordinal);
    }

    /// <summary>
    /// Several reads asked for in one turn are answered in one turn. Each of them coming back as
    /// its own round trip is the difference between one wait and three.
    /// </summary>
    [Fact]
    public async Task SeveralReadsInOneTurn_AreAllAnsweredBeforeTheNextRequest()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var today = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd");
        using var endpoint = new FakeChatEndpoint();
        endpoint
            .Enqueue(System.Text.Json.JsonSerializer.Serialize(new
            {
                choices = new[]
                {
                    new
                    {
                        message = new
                        {
                            role = "assistant",
                            content = (string?)null,
                            tool_calls = new object[]
                            {
                                new { id = "a", type = "function", function = new { name = "get_schedule", arguments = $"{{\"fromDate\":\"{today}\"}}" } },
                                new { id = "b", type = "function", function = new { name = "get_tasks", arguments = "{}" } },
                                new { id = "c", type = "function", function = new { name = "find_free_slots", arguments = "{}" } }
                            }
                        }
                    }
                }
            }))
            .EnqueueText("Вот план.");

        await using var harness = await ApiHarnessAsync(endpoint, cancellationToken);
        var reply = await harness.CreateAssistant().AskAsync(new AssistantAsk("Спланируй день"), cancellationToken);

        Assert.False(reply.IsError);
        Assert.Equal(3, reply.Trace.Count);
        Assert.Equal(["get_schedule", "get_tasks", "find_free_slots"], reply.Trace.Select(entry => entry.Name));
        Assert.All(reply.Trace, entry => Assert.False(entry.IsError));
        // Two requests total: the turn that asked, and the turn that answered.
        Assert.Equal(2, endpoint.Requests.Count);
        foreach (var id in new[] { "a", "b", "c" })
            Assert.Contains($"\"tool_call_id\":\"{id}\"", endpoint.Requests[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanningRequest_RunsToolLoopAndAnswersInProse()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var endpoint = new FakeChatEndpoint();
        endpoint
            .EnqueueToolCall("get_schedule", """{"fromDate":"2026-08-24","toDate":"2026-08-24"}""")
            .EnqueueText("Утро свободно — предлагаю поставить блок с 10:00 до 11:00.");

        await using var harness = await ApiHarnessAsync(endpoint, cancellationToken);
        var assistant = harness.CreateAssistant();

        var reply = await assistant.AskAsync(new AssistantAsk("Составь расписание на завтра"), cancellationToken);

        Assert.False(reply.IsError);
        Assert.Contains("10:00", reply.Message, StringComparison.Ordinal);
        var trace = Assert.Single(reply.Trace);
        Assert.Equal("get_schedule", trace.Name);
        Assert.False(trace.IsError);

        // The second request must carry the tool result back to the model.
        Assert.Equal(2, endpoint.Requests.Count);
        Assert.Contains("\"role\":\"tool\"", endpoint.Requests[1], StringComparison.Ordinal);
        Assert.Contains("\"tool_call_id\":\"call_1\"", endpoint.Requests[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteTool_UnderConfirmAll_IsProposedRatherThanApplied()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var endpoint = new FakeChatEndpoint();
        endpoint
            .EnqueueToolCall("create_event",
                """{"title":"Глубокая работа","startLocal":"2026-08-24T10:00:00","endLocal":"2026-08-24T11:00:00","category":"Work"}""")
            .EnqueueText("Подготовил событие, подтвердите его в приложении.");

        await using var harness = await ApiHarnessAsync(endpoint, cancellationToken);
        var assistant = harness.CreateAssistant();

        var reply = await assistant.AskAsync(new AssistantAsk("Поставь глубокую работу завтра в 10"), cancellationToken);

        var action = Assert.Single(reply.ScheduleActions);
        Assert.Equal(AssistantActionKind.CreateEvent, action.Kind);
        Assert.Equal("Глубокая работа", action.Title);
        Assert.Empty(await harness.Store.GetRangeAsync(DateTimeOffset.UtcNow.AddYears(-1), DateTimeOffset.UtcNow.AddYears(1), cancellationToken));
        Assert.Contains("NOT saved", endpoint.Requests[1], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WriteTool_UnderFullAccess_IsAppliedImmediately()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var endpoint = new FakeChatEndpoint();
        endpoint
            .EnqueueToolCall("create_event",
                """{"title":"Спорт","startLocal":"2026-08-24T19:00:00","endLocal":"2026-08-24T20:00:00","category":"Health"}""")
            .EnqueueText("Готово, поставил тренировку.");

        await using var harness = await ApiHarnessAsync(endpoint, cancellationToken, AssistantAccessLevel.FullAccess);
        var assistant = harness.CreateAssistant();

        var reply = await assistant.AskAsync(new AssistantAsk("Поставь тренировку"), cancellationToken);

        Assert.Empty(reply.ScheduleActions);
        Assert.True(Assert.Single(reply.Trace).Applied);
        var stored = await harness.Store.GetRangeAsync(
            new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.Zero), cancellationToken);
        Assert.Equal("Спорт", Assert.Single(stored).Title);
    }

    [Fact]
    public async Task ProviderError_IsReportedWithItsOwnMessageInsteadOfSilentOfflineFallback()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var endpoint = new FakeChatEndpoint();
        endpoint.Enqueue("""{"error":{"message":"model 'test-model' does not exist","type":"invalid_request_error"}}""", status: 404);

        await using var harness = await ApiHarnessAsync(endpoint, cancellationToken);
        var assistant = harness.CreateAssistant();

        var reply = await assistant.AskAsync(new AssistantAsk("Составь расписание"), cancellationToken);

        Assert.True(reply.IsError);
        Assert.Contains("404", reply.Message, StringComparison.Ordinal);
        Assert.Contains("does not exist", reply.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnsupportedToolParameter_IsDroppedAndTheTurnStillSucceeds()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var endpoint = new FakeChatEndpoint();
        endpoint
            .Enqueue("""{"error":{"message":"Unknown parameter: 'tools'. This model does not support function calling."}}""", status: 400)
            .EnqueueText("Могу обсудить расписание словами.");

        await using var harness = await ApiHarnessAsync(endpoint, cancellationToken);
        var assistant = harness.CreateAssistant();

        var reply = await assistant.AskAsync(new AssistantAsk("Что у меня завтра?"), cancellationToken);

        Assert.False(reply.IsError);
        Assert.Contains("расписание", reply.Message, StringComparison.Ordinal);
        Assert.Contains("\"tools\"", endpoint.Requests[0], StringComparison.Ordinal);
        Assert.DoesNotContain("\"tools\"", endpoint.Requests[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnsupportedTemperature_IsDroppedOnRetry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var endpoint = new FakeChatEndpoint();
        endpoint
            .Enqueue("""{"error":{"message":"Unsupported value: 'temperature' does not support 0.3 with this model."}}""", status: 400)
            .EnqueueText("Понял.");

        await using var harness = await ApiHarnessAsync(endpoint, cancellationToken);
        var assistant = harness.CreateAssistant();

        var reply = await assistant.AskAsync(new AssistantAsk("Привет"), cancellationToken);

        Assert.False(reply.IsError);
        Assert.Contains("\"temperature\"", endpoint.Requests[0], StringComparison.Ordinal);
        Assert.DoesNotContain("\"temperature\"", endpoint.Requests[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnreachableEndpoint_FallsBackOfflineAndSaysSo()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        await harness.ConfigureAsync(settings =>
        {
            settings.AssistantProvider = AssistantProvider.OpenAiCompatible;
            // Port 1 on loopback refuses connections immediately.
            settings.AssistantEndpoint = "http://127.0.0.1:1";
            settings.AssistantModel = "test-model";
            settings.McpToolsEnabled = false;
        }, cancellationToken);
        var assistant = harness.CreateAssistant();

        var reply = await assistant.AskAsync(new AssistantAsk("Что дальше?"), cancellationToken);

        Assert.True(reply.IsError);
        Assert.Contains("unreachable", reply.Provider, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Conversation_IsPersistedAndReplayedIntoTheNextTurn()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var endpoint = new FakeChatEndpoint();
        endpoint.EnqueueText("Меня зовут DAYDRIVE.").EnqueueText("Ты спрашивал моё имя.");

        await using var harness = await ApiHarnessAsync(endpoint, cancellationToken);
        var assistant = harness.CreateAssistant();

        var first = await assistant.AskAsync(new AssistantAsk("Как тебя зовут?"), cancellationToken);
        Assert.NotNull(first.ConversationId);

        var second = await assistant.AskAsync(new AssistantAsk("О чём я спрашивал?", first.ConversationId), cancellationToken);
        Assert.Equal(first.ConversationId, second.ConversationId);

        var stored = await harness.Chat.GetMessagesAsync(first.ConversationId!.Value, cancellationToken);
        Assert.Equal(4, stored.Count);
        Assert.Equal(ChatRole.User, stored[0].Role);
        Assert.Equal("Как тебя зовут?", stored[0].Content);

        // The second call must resend the earlier exchange as prior turns.
        using var payload = JsonDocument.Parse(endpoint.Requests[1]);
        var roles = payload.RootElement.GetProperty("messages").EnumerateArray()
            .Select(message => message.GetProperty("role").GetString()).ToList();
        Assert.Equal(["system", "user", "assistant", "user"], roles);
    }

    [Fact]
    public async Task MemoryTool_StoresAndSurfacesFactsInTheNextSystemPrompt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var endpoint = new FakeChatEndpoint();
        endpoint
            .EnqueueToolCall("remember_about_user",
                """{"key":"deep-work-window","value":"Работает лучше всего с 9 до 12","category":"Routine"}""")
            .EnqueueText("Запомнил.")
            .EnqueueText("Учту твоё утреннее окно.");

        await using var harness = await ApiHarnessAsync(endpoint, cancellationToken);
        var assistant = harness.CreateAssistant();

        await assistant.AskAsync(new AssistantAsk("Запомни: я работаю лучше всего с 9 до 12"), cancellationToken);
        var stored = Assert.Single(await harness.Chat.GetAllAsync(cancellationToken));
        Assert.Equal("deep-work-window", stored.Key);

        await assistant.AskAsync(new AssistantAsk("Когда мне поработать?"), cancellationToken);
        Assert.Contains("deep-work-window", endpoint.Requests[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolFailure_IsReportedToTheModelRatherThanEndingTheTurn()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var endpoint = new FakeChatEndpoint();
        endpoint
            .EnqueueToolCall("delete_event", """{"targetId":"11111111-1111-1111-1111-111111111111"}""")
            .EnqueueText("Такого события больше нет.");

        await using var harness = await ApiHarnessAsync(endpoint, cancellationToken, AssistantAccessLevel.FullAccess);
        var assistant = harness.CreateAssistant();

        var reply = await assistant.AskAsync(new AssistantAsk("Удали встречу"), cancellationToken);

        Assert.False(reply.IsError);
        Assert.True(Assert.Single(reply.Trace).IsError);
        Assert.Contains("no longer", endpoint.Requests[1], StringComparison.OrdinalIgnoreCase);
    }
}
