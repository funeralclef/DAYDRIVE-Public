using IntelligentScheduler.Application;
using IntelligentScheduler.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IntelligentScheduler.Infrastructure.Tests;

/// <summary>
/// MCP was never verified before. These tests drive a real MCP server over a real JSON-RPC
/// session: discovery, name prefixing, schema translation, argument marshalling, error
/// propagation, federation into the model's tool list, and the legacy bridge mode.
/// </summary>
public sealed class McpToolGatewayTests
{
    private static async Task<AssistantTestHarness> HarnessAsync(
        InProcessMcpServer server, CancellationToken cancellationToken, bool mcpAsProvider = false)
    {
        var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        await harness.ConfigureAsync(settings =>
        {
            settings.AssistantProvider = mcpAsProvider ? AssistantProvider.Mcp : AssistantProvider.OpenAiCompatible;
            settings.McpTransport = "Stdio";
            // Only used to satisfy the "configured" check; the transport is injected below.
            settings.McpCommand = "in-process";
            settings.McpToolsEnabled = true;
            settings.McpToolName = "assistant";
            settings.ShareContextWithExternalAi = true;
        }, cancellationToken);
        harness.UseMcp(_ => server.CreateClientTransport());
        return harness;
    }

    [Fact]
    public async Task Probe_ListsTheServersToolsOverARealSession()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var server = new InProcessMcpServer(InProcessMcpServer.Echo(), InProcessMcpServer.Failing());
        await using var harness = await HarnessAsync(server, cancellationToken);

        var result = await harness.Mcp!.ProbeAsync(cancellationToken);

        Assert.True(result.IsSuccess, result.Message);
        Assert.NotNull(result.Tools);
        Assert.Equal(2, result.Tools!.Count);
        Assert.Contains(result.Tools, tool => tool.Name == "mcp_echo_note");
        Assert.All(result.Tools, tool => Assert.True(tool.IsExternal));
    }

    [Fact]
    public async Task Tools_AreExposedWithAPrefixAndAUsableSchema()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var server = new InProcessMcpServer(InProcessMcpServer.Echo());
        await using var harness = await HarnessAsync(server, cancellationToken);

        var tools = await harness.Mcp!.GetToolsAsync(cancellationToken);

        var tool = Assert.Single(tools);
        Assert.Equal("mcp_echo_note", tool.Name);
        Assert.Equal("MCP", tool.Source);
        Assert.True(tool.IsExternal);
        // The schema must be valid JSON the chat payload can embed as-is.
        using var schema = System.Text.Json.JsonDocument.Parse(tool.ParametersJson);
        Assert.Equal(System.Text.Json.JsonValueKind.Object, schema.RootElement.ValueKind);
        Assert.True(schema.RootElement.TryGetProperty("properties", out var properties));
        Assert.True(properties.TryGetProperty("note", out _));
    }

    [Fact]
    public async Task Invoke_MarshalsArgumentsAndReturnsTheServersText()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var server = new InProcessMcpServer(InProcessMcpServer.Echo());
        await using var harness = await HarnessAsync(server, cancellationToken);

        var result = await harness.Mcp!.InvokeAsync(
            new AssistantToolInvocation("call_1", "mcp_echo_note", """{"note":"привет"}"""), cancellationToken);

        Assert.False(result.IsError);
        Assert.Equal("echo:привет", result.Content);
    }

    [Fact]
    public async Task Invoke_ReportsAServerSideFailureAsAToolErrorRatherThanThrowing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var server = new InProcessMcpServer(InProcessMcpServer.Failing());
        await using var harness = await HarnessAsync(server, cancellationToken);

        var result = await harness.Mcp!.InvokeAsync(
            new AssistantToolInvocation("call_1", "mcp_always_fails", """{"reason":"test"}"""), cancellationToken);

        Assert.True(result.IsError);
        Assert.Contains("fail", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ModelSeesBuiltInAndMcpToolsTogether()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var endpoint = new FakeChatEndpoint();
        endpoint.EnqueueText("ок");

        await using var server = new InProcessMcpServer(InProcessMcpServer.Echo());
        await using var harness = await HarnessAsync(server, cancellationToken);
        await harness.ConfigureAsync(settings =>
        {
            settings.AssistantEndpoint = endpoint.BaseAddress;
            settings.AssistantModel = "test-model";
        }, cancellationToken);

        var assistant = harness.CreateAssistant();
        await assistant.AskAsync(new AssistantAsk("привет"), cancellationToken);

        var payload = Assert.Single(endpoint.Requests);
        Assert.Contains("\"get_schedule\"", payload, StringComparison.Ordinal);
        Assert.Contains("\"mcp_echo_note\"", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModelCanActuallyCallAnMcpToolMidConversation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var endpoint = new FakeChatEndpoint();
        endpoint
            .EnqueueToolCall("mcp_echo_note", """{"note":"расписание"}""")
            .EnqueueText("Сервер ответил.");

        await using var server = new InProcessMcpServer(InProcessMcpServer.Echo());
        await using var harness = await HarnessAsync(server, cancellationToken);
        await harness.ConfigureAsync(settings =>
        {
            settings.AssistantEndpoint = endpoint.BaseAddress;
            settings.AssistantModel = "test-model";
        }, cancellationToken);

        var assistant = harness.CreateAssistant();
        var reply = await assistant.AskAsync(new AssistantAsk("Спроси сервер"), cancellationToken);

        var trace = Assert.Single(reply.Trace);
        Assert.Equal("mcp_echo_note", trace.Name);
        Assert.True(trace.IsExternal);
        Assert.False(trace.IsError);
        Assert.Contains("echo:расписание", endpoint.Requests[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task McpAsProvider_StillWorksThroughTheLegacyBridgeTool()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var server = new InProcessMcpServer(InProcessMcpServer.Bridge());
        await using var harness = await HarnessAsync(server, cancellationToken, mcpAsProvider: true);

        var assistant = harness.CreateAssistant();
        var reply = await assistant.AskAsync(new AssistantAsk("составь план"), cancellationToken);

        Assert.False(reply.IsError);
        Assert.Contains("bridge saw: составь план", reply.Message, StringComparison.Ordinal);
        Assert.Equal("MCP · assistant", reply.Provider);
    }

    [Fact]
    public async Task MissingBridgeTool_ExplainsWhatTheServerActuallyExposes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var server = new InProcessMcpServer(InProcessMcpServer.Echo());
        await using var harness = await HarnessAsync(server, cancellationToken, mcpAsProvider: true);
        await harness.ConfigureAsync(settings => settings.McpToolName = "nonexistent", cancellationToken);

        var assistant = harness.CreateAssistant();
        var reply = await assistant.AskAsync(new AssistantAsk("привет"), cancellationToken);

        Assert.True(reply.IsError);
        Assert.Contains("nonexistent", reply.Message, StringComparison.Ordinal);
        Assert.Contains("echo_note", reply.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProbeTools_ReportsBuiltInAndExternalCountsTogether()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var server = new InProcessMcpServer(InProcessMcpServer.Echo(), InProcessMcpServer.Failing());
        await using var harness = await HarnessAsync(server, cancellationToken);

        var probe = await harness.CreateAssistant().ProbeToolsAsync(cancellationToken);

        Assert.True(probe.IsSuccess, probe.Message);
        Assert.NotNull(probe.Tools);
        Assert.Equal(ScheduleToolbox.Definitions.Count + 2, probe.Tools!.Count);
        Assert.Contains(probe.Tools, tool => !tool.IsExternal && tool.Name == "find_free_slots");
    }

    [Theory]
    [InlineData("Stdio", "npx", "", true)]
    [InlineData("Stdio", "", "", false)]
    [InlineData("StreamableHttp", "", "http://localhost:9000/mcp", true)]
    [InlineData("StreamableHttp", "", "not-a-url", false)]
    [InlineData("StreamableHttp", "", "", false)]
    public void IsConfigured_MatchesTheTransportItWouldActuallyUse(string transport, string command, string endpoint, bool expected)
        => Assert.Equal(expected, McpToolGateway.IsConfigured(new AppSettings
        {
            McpTransport = transport,
            McpCommand = command,
            McpEndpoint = endpoint
        }));

    /// <summary>
    /// The chat API's address is not an MCP server. It used to be read as one, and federation
    /// then opened a doomed protocol session before every single message the user sent.
    /// </summary>
    [Fact]
    public void IsConfigured_IgnoresTheChatApiAddress()
        => Assert.False(McpToolGateway.IsConfigured(new AppSettings
        {
            McpTransport = "StreamableHttp",
            AssistantEndpoint = "https://api.openai.com/v1",
            McpEndpoint = string.Empty
        }));

    [Fact]
    public void StdioArguments_RespectQuotedSegments()
        => Assert.Equal(["-y", "@scope/server", "C:\\Program Files\\data"],
            McpToolGateway.SplitArguments("""-y @scope/server "C:\Program Files\data" """));

    /// <summary>
    /// The application's container is torn down synchronously, from the event Avalonia raises
    /// while it is inside the window procedure handling Windows' end-of-session message. A
    /// service that offers only <see cref="IAsyncDisposable"/> makes that call throw, and there
    /// is nothing above a window procedure left to catch it: the process died with an "unknown
    /// software exception" box on the screen every time the machine was shut down.
    /// </summary>
    [Fact]
    public async Task ContainerDisposesSynchronously_SoTheSessionCanEndWithoutACrash()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);

        var services = new ServiceCollection();
        services.AddSingleton<ISettingsRepository>(harness.Store);
        services.AddSingleton<ISecretProtector>(harness.Protector);
        services.AddSingleton<McpToolGateway>();
        var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<McpToolGateway>());

        provider.Dispose();
    }
}
