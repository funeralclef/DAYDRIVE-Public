using IntelligentScheduler.Application;
using IntelligentScheduler.Domain;
using Xunit;

namespace IntelligentScheduler.Infrastructure.Tests;

/// <summary>
/// A reply is now read as it is written. These cover the two things that has to survive: the
/// text reaching the panel while it is still arriving, and tool calls that come apart across
/// frames being put back together before anything is executed.
/// </summary>
public sealed class AssistantStreamingTests
{
    private static async Task<AssistantTestHarness> ApiHarnessAsync(
        FakeChatEndpoint endpoint, CancellationToken cancellationToken,
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

    [Fact]
    public async Task StreamedText_ArrivesInPiecesAndAddsUpToTheAnswer()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var endpoint = new FakeChatEndpoint();
        endpoint.EnqueueStream(
            FakeChatEndpoint.TextDelta("Утро "),
            FakeChatEndpoint.TextDelta("свободно "),
            FakeChatEndpoint.TextDelta("до 11:00."));

        await using var harness = await ApiHarnessAsync(endpoint, cancellationToken);
        var assistant = harness.CreateAssistant();
        var seen = new List<string>();
        assistant.ReplyStreaming += (_, update) => seen.Add(update.Text);

        var reply = await assistant.AskAsync(new AssistantAsk("Что у меня утром?"), cancellationToken);

        Assert.False(reply.IsError);
        Assert.Equal("Утро свободно до 11:00.", reply.Message);
        // The last thing raised is always the whole answer, whatever the sampling swallowed.
        Assert.NotEmpty(seen);
        Assert.Equal(reply.Message, seen[^1]);
        Assert.Contains("\"stream\":true", endpoint.Requests[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamedToolCall_IsReassembledFromItsFragmentsAndRun()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var day = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd");
        using var endpoint = new FakeChatEndpoint();
        // The argument object arrives as three separate fragments of text, which is exactly how
        // a real endpoint sends it.
        endpoint
            .EnqueueStream(
                FakeChatEndpoint.ToolDelta(0, id: "call_7", name: "get_schedule", arguments: string.Empty),
                FakeChatEndpoint.ToolDelta(0, arguments: "{\"fromDate\":\"" + day + "\""),
                FakeChatEndpoint.ToolDelta(0, arguments: ",\"toDate\":\"" + day + "\"}"))
            .EnqueueStream(FakeChatEndpoint.TextDelta("Сегодня одна встреча."));

        await using var harness = await ApiHarnessAsync(endpoint, cancellationToken);
        var reply = await harness.CreateAssistant().AskAsync(new AssistantAsk("Что сегодня?"), cancellationToken);

        Assert.False(reply.IsError);
        var trace = Assert.Single(reply.Trace);
        Assert.Equal("get_schedule", trace.Name);
        Assert.False(trace.IsError);
        // The tool result has to reach the model under the id the stream gave the call.
        Assert.Equal(2, endpoint.Requests.Count);
        Assert.Contains("\"tool_call_id\":\"call_7\"", endpoint.Requests[1], StringComparison.Ordinal);
        Assert.Equal("Сегодня одна встреча.", reply.Message);
    }

    [Fact]
    public async Task StreamedWriteTool_StillGoesThroughThePermissionCheck()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var day = DateOnly.FromDateTime(DateTime.Today).AddDays(1);
        using var endpoint = new FakeChatEndpoint();
        endpoint
            .EnqueueStream(
                FakeChatEndpoint.ToolDelta(0, id: "call_1", name: "create_event"),
                FakeChatEndpoint.ToolDelta(0, arguments: $$"""{"title":"Глубокая работа","startLocal":"{{day:yyyy-MM-dd}}T10:00:00"}"""))
            .EnqueueStream(FakeChatEndpoint.TextDelta("Подготовил, подтвердите."));

        await using var harness = await ApiHarnessAsync(endpoint, cancellationToken);
        var reply = await harness.CreateAssistant().AskAsync(new AssistantAsk("Поставь работу"), cancellationToken);

        var action = Assert.Single(reply.ScheduleActions);
        Assert.Equal(AssistantActionKind.CreateEvent, action.Kind);
        Assert.Equal("Глубокая работа", action.Title);
        var (start, end) = ScheduleActionExecutor.DayRange(day);
        Assert.Empty(await harness.Store.GetRangeAsync(start, end, cancellationToken));
    }

    /// <summary>An endpoint that ignores the request to stream still answers the old way.</summary>
    [Fact]
    public async Task BufferedAnswer_IsStillUnderstood()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var endpoint = new FakeChatEndpoint();
        endpoint.EnqueueText("Одним куском.");

        await using var harness = await ApiHarnessAsync(endpoint, cancellationToken);
        var assistant = harness.CreateAssistant();
        var streamed = 0;
        assistant.ReplyStreaming += (_, _) => streamed++;

        var reply = await assistant.AskAsync(new AssistantAsk("Привет"), cancellationToken);

        Assert.False(reply.IsError);
        Assert.Equal("Одним куском.", reply.Message);
        Assert.Equal(0, streamed);
    }

    /// <summary>A gateway that fails after the 200 has gone out must not look like a blank answer.</summary>
    [Fact]
    public async Task ErrorInsideTheStream_IsReportedAsAFailure()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var endpoint = new FakeChatEndpoint();
        endpoint.EnqueueStream(
            FakeChatEndpoint.TextDelta("Начал отвечать"),
            """{"error":{"message":"upstream capacity exceeded"}}""");

        await using var harness = await ApiHarnessAsync(endpoint, cancellationToken);
        var reply = await harness.CreateAssistant().AskAsync(new AssistantAsk("Привет"), cancellationToken);

        Assert.True(reply.IsError);
        Assert.Contains("upstream capacity exceeded", reply.Message, StringComparison.Ordinal);
    }
}
