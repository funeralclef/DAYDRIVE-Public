using System.IO.Pipelines;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace IntelligentScheduler.Infrastructure.Tests;

/// <summary>
/// A genuine MCP server running in the test process over a pair of pipes. The client speaks
/// real JSON-RPC to it, so initialization, tools/list and tools/call are all exercised for
/// real — only the process boundary is replaced.
/// </summary>
public sealed class InProcessMcpServer : IAsyncDisposable
{
    private readonly Pipe _toServer = new();
    private readonly Pipe _toClient = new();
    private readonly McpServer _server;
    private readonly Task _running;
    private readonly CancellationTokenSource _shutdown = new();

    public InProcessMcpServer(params McpServerTool[] tools)
    {
        var collection = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var tool in tools) collection.Add(tool);

        var transport = new StreamServerTransport(
            _toServer.Reader.AsStream(),
            _toClient.Writer.AsStream(),
            "daydrive-test-mcp");

        _server = McpServer.Create(transport, new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "daydrive-test-mcp", Version = "1.0.0" },
            ToolCollection = collection
        });
        _running = _server.RunAsync(_shutdown.Token);
    }

    public IClientTransport CreateClientTransport()
        => new StreamClientTransport(_toServer.Writer.AsStream(), _toClient.Reader.AsStream());

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        try { await _server.DisposeAsync(); } catch { /* already torn down */ }
        try { await _running; } catch { /* cancelled */ }
        _shutdown.Dispose();
    }

    /// <summary>Tools used by the MCP tests: one plain echo, one that reports failure.</summary>
    public static McpServerTool Echo() => McpServerTool.Create(
        (string note) => $"echo:{note}",
        new McpServerToolCreateOptions { Name = "echo_note", Description = "Echoes the note back." });

    public static McpServerTool Failing() => McpServerTool.Create(
        (Func<string, string>)(reason => throw new InvalidOperationException($"deliberate failure: {reason}")),
        new McpServerToolCreateOptions { Name = "always_fails", Description = "Always throws." });

    public static McpServerTool Bridge() => McpServerTool.Create(
        (string prompt, string context) =>
            $$"""{"message":"bridge saw: {{prompt}}","tasks":[],"actions":[]}""",
        new McpServerToolCreateOptions { Name = "assistant", Description = "Legacy prompt+context bridge." });
}
