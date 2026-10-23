using System.Text.Json;
using IntelligentScheduler.Application;
using IntelligentScheduler.Domain;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace IntelligentScheduler.Infrastructure;

/// <summary>
/// Bridges a configured MCP server into the assistant. The server's tools are federated
/// into whichever language model is selected instead of replacing the model, which is how
/// MCP is meant to be used: the model reasons, the server supplies capabilities.
///
/// Names are prefixed with <c>mcp_</c> so an external tool can never shadow a built-in one.
/// The session is cached because spawning a stdio server per message is slow.
/// </summary>
public sealed class McpToolGateway(
    ISettingsRepository settings,
    ISecretProtector secrets,
    /// <summary>Overrides transport creation. Production leaves it null; tests connect an in-process server.</summary>
    Func<AppSettings, IClientTransport>? transportFactory = null) : IAssistantToolbox, IAsyncDisposable, IDisposable
{
    public const string Prefix = "mcp_";

    /// <summary>
    /// How long a server that just failed to connect is left alone. Reaching an unreachable
    /// address costs a full connect timeout, and paying that on every message turns one broken
    /// setting into a permanently slow assistant. Pressing "test connection" ignores this.
    /// </summary>
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private McpClient? _client;
    private string _fingerprint = string.Empty;
    private IReadOnlyList<AssistantToolDefinition> _cachedTools = [];
    private DateTimeOffset _quietUntil = DateTimeOffset.MinValue;
    private string _quietFingerprint = string.Empty;

    public async ValueTask<IReadOnlyList<AssistantToolDefinition>> GetToolsAsync(CancellationToken cancellationToken = default)
    {
        var current = await settings.GetAppSettingsAsync(cancellationToken);
        if (!IsConfigured(current) || IsInCooldown(current)) return [];
        try
        {
            var tools = await ListToolsAsync(current, cancellationToken);
            _quietUntil = DateTimeOffset.MinValue;
            return tools;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // A broken MCP server must never take the whole conversation down — nor be retried
            // from scratch on the very next question the user types.
            BeginCooldown(current);
            await ResetAsync();
            return [];
        }
    }

    /// <summary>True while a server that just refused a connection is being left alone.</summary>
    private bool IsInCooldown(AppSettings current)
        => Fingerprint(current) == _quietFingerprint && DateTimeOffset.UtcNow < _quietUntil;

    private void BeginCooldown(AppSettings current)
    {
        _quietFingerprint = Fingerprint(current);
        _quietUntil = DateTimeOffset.UtcNow + FailureCooldown;
    }

    public async ValueTask<AssistantToolResult> InvokeAsync(
        AssistantToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        var current = await settings.GetAppSettingsAsync(cancellationToken);
        var remoteName = invocation.Name.StartsWith(Prefix, StringComparison.Ordinal)
            ? invocation.Name[Prefix.Length..]
            : invocation.Name;
        try
        {
            var client = await GetClientAsync(current, cancellationToken);
            var arguments = ToArguments(AssistantToolJson.Parse(invocation.ArgumentsJson));
            var result = await client.CallToolAsync(remoteName, arguments, cancellationToken: cancellationToken);
            var text = Flatten(result);
            return new(invocation.CallId, invocation.Name,
                string.IsNullOrWhiteSpace(text) ? "(the MCP tool returned no text)" : text,
                IsError: result.IsError == true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            await ResetAsync();
            return new(invocation.CallId, invocation.Name, $"MCP tool failed: {exception.Message}", IsError: true);
        }
    }

    /// <summary>Connects, lists the server's tools and reports what a real session would see.</summary>
    public async Task<AssistantConnectionResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var current = await settings.GetAppSettingsAsync(cancellationToken);
        if (!IsConfigured(current))
            return new(false, "MCP is not configured. Set a Streamable HTTP endpoint or a stdio command first.");
        _quietUntil = DateTimeOffset.MinValue;
        try
        {
            var tools = await ListToolsAsync(current, cancellationToken, forceRefresh: true);
            var probes = tools.Select(tool => new AssistantToolProbe(tool.Name, tool.Description, true)).ToList();
            return new(true,
                probes.Count == 0
                    ? "MCP connected, but the server exposes no tools."
                    : $"MCP connected · {probes.Count} tool(s): {string.Join(", ", probes.Take(6).Select(tool => tool.Name))}" +
                      (probes.Count > 6 ? "…" : string.Empty),
                probes);
        }
        catch (Exception exception)
        {
            BeginCooldown(current);
            await ResetAsync();
            return new(false, $"MCP connection failed: {Describe(exception)}");
        }
    }

    /// <summary>
    /// Legacy bridge mode: call one tool that takes a prompt and a context blob and answers
    /// on its own. Kept for servers built against the previous DAYDRIVE contract.
    /// </summary>
    public async Task<string> CallBridgeToolAsync(AppSettings current, string prompt, string context, CancellationToken cancellationToken)
    {
        var client = await GetClientAsync(current, cancellationToken);
        var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
        var tool = tools.FirstOrDefault(item => item.Name.Equals(current.McpToolName, StringComparison.OrdinalIgnoreCase))
                   ?? tools.FirstOrDefault(item => item.Name.Contains("assistant", StringComparison.OrdinalIgnoreCase)
                                                   || item.Name.Contains("plan", StringComparison.OrdinalIgnoreCase)
                                                   || item.Name.Contains("task", StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException(
                       $"The MCP server exposes no tool named \"{current.McpToolName}\" and nothing that looks like an assistant tool. " +
                       $"Available: {(tools.Count == 0 ? "none" : string.Join(", ", tools.Select(item => item.Name)))}.");

        var result = await client.CallToolAsync(tool.Name, new Dictionary<string, object?>
        {
            ["prompt"] = prompt,
            ["context"] = context
        }, cancellationToken: cancellationToken);
        if (result.IsError == true) throw new InvalidOperationException($"MCP tool \"{tool.Name}\" reported an error: {Flatten(result)}");
        return Flatten(result);
    }

    /// <summary>
    /// Whether there is an MCP server to talk to at all.
    ///
    /// This used to read <c>AssistantEndpoint</c> — the same field the chat API keeps its URL in
    /// — so configuring any hosted API also declared an MCP server at that address. Federation
    /// is on by default, so every message then opened a Model Context Protocol session against
    /// a chat endpoint, waited for it to fail, and threw the client away before the question was
    /// even sent. MCP has its own address now, and an empty one means no server.
    /// </summary>
    public static bool IsConfigured(AppSettings current)
        => current.McpTransport.Equals("Stdio", StringComparison.OrdinalIgnoreCase)
            ? !string.IsNullOrWhiteSpace(current.McpCommand)
            : Uri.TryCreate(current.McpEndpoint?.Trim(), UriKind.Absolute, out var uri)
              && uri.Scheme is "http" or "https";

    private async Task<IReadOnlyList<AssistantToolDefinition>> ListToolsAsync(
        AppSettings current, CancellationToken cancellationToken, bool forceRefresh = false)
    {
        if (!forceRefresh && _cachedTools.Count > 0 && Fingerprint(current) == _fingerprint) return _cachedTools;
        var client = await GetClientAsync(current, cancellationToken);
        var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
        _cachedTools = tools.Select(tool => new AssistantToolDefinition(
            Prefix + Sanitize(tool.Name),
            string.IsNullOrWhiteSpace(tool.Description) ? $"MCP tool \"{tool.Name}\"." : tool.Description!,
            SchemaOf(tool.ProtocolTool),
            IsMutating: false,
            Source: "MCP")).ToList();
        return _cachedTools;
    }

    private async Task<McpClient> GetClientAsync(AppSettings current, CancellationToken cancellationToken)
    {
        var fingerprint = Fingerprint(current);
        if (_client is not null && fingerprint == _fingerprint) return _client;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_client is not null && fingerprint == _fingerprint) return _client;
            await DisposeClientAsync();
            _client = await McpClient.CreateAsync(CreateTransport(current), cancellationToken: cancellationToken);
            _fingerprint = fingerprint;
            _cachedTools = [];
            return _client;
        }
        finally { _gate.Release(); }
    }

    private IClientTransport CreateTransport(AppSettings current)
    {
        if (transportFactory is not null) return transportFactory(current);
        if (current.McpTransport.Equals("Stdio", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(current.McpCommand))
                throw new InvalidOperationException("Set the MCP stdio command first.");
            return new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "DAYDRIVE",
                Command = current.McpCommand.Trim(),
                Arguments = SplitArguments(current.McpArguments),
                // Never hand a child process the user's secrets.
                InheritEnvironmentVariables = false,
                EnvironmentVariables = StdioClientTransportOptions.GetDefaultEnvironmentVariables()
            });
        }

        if (!Uri.TryCreate(current.McpEndpoint?.Trim(), UriKind.Absolute, out var endpoint))
            throw new InvalidOperationException("Set a valid MCP endpoint URL first.");

        var headers = new Dictionary<string, string>();
        var key = ReadApiKey(current);
        if (!string.IsNullOrWhiteSpace(key)) headers["Authorization"] = $"Bearer {key}";
        return new HttpClientTransport(new HttpClientTransportOptions
        {
            Name = "DAYDRIVE",
            Endpoint = endpoint,
            TransportMode = HttpTransportMode.AutoDetect,
            AdditionalHeaders = headers,
            ConnectionTimeout = TimeSpan.FromSeconds(25)
        });
    }

    private string ReadApiKey(AppSettings current) => string.IsNullOrWhiteSpace(current.AssistantApiKeyCiphertext)
        ? Environment.GetEnvironmentVariable("INTELLIGENT_SCHEDULER_API_KEY") ?? string.Empty
        : secrets.Unprotect(current.AssistantApiKeyCiphertext);

    /// <summary>Drops the cached session so the next call reconnects from scratch.</summary>
    public async Task ResetAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await DisposeClientAsync();
            _fingerprint = string.Empty;
            _cachedTools = [];
        }
        finally { _gate.Release(); }
    }

    private async Task DisposeClientAsync()
    {
        if (_client is null) return;
        try { await _client.DisposeAsync(); }
        catch { /* a dead transport has nothing left to close */ }
        _client = null;
    }

    private static string Fingerprint(AppSettings current)
        => string.Join('|', current.McpTransport, current.McpCommand, current.McpArguments, current.McpEndpoint);

    private static string SchemaOf(Tool tool)
    {
        var schema = tool.InputSchema;
        var raw = schema.ValueKind == JsonValueKind.Undefined ? "{}" : schema.GetRawText();
        return string.IsNullOrWhiteSpace(raw) || raw == "null"
            ? """{"type":"object","properties":{},"required":[]}"""
            : raw;
    }

    /// <summary>OpenAI-style tool names allow letters, digits, underscore and dash only.</summary>
    private static string Sanitize(string name)
    {
        var cleaned = new string(name.Select(character =>
            char.IsLetterOrDigit(character) || character is '_' or '-' ? character : '_').ToArray());
        return cleaned.Length <= 56 ? cleaned : cleaned[..56];
    }

    private static string Flatten(CallToolResult result)
    {
        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        if (!string.IsNullOrWhiteSpace(text)) return text;
        return result.StructuredContent is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } structured
            ? structured.GetRawText()
            : string.Empty;
    }

    private static IReadOnlyDictionary<string, object?> ToArguments(JsonElement element)
    {
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (element.ValueKind != JsonValueKind.Object) return arguments;
        foreach (var property in element.EnumerateObject()) arguments[property.Name] = ToClr(property.Value);
        return arguments;
    }

    private static object? ToClr(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var integer) ? integer : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => element.EnumerateArray().Select(ToClr).ToArray(),
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(item => item.Name, item => ToClr(item.Value)),
        _ => null
    };

    internal static string[] SplitArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return [];
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var character in arguments)
        {
            if (character == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(character) && !quoted)
            {
                if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); }
            }
            else current.Append(character);
        }
        if (current.Length > 0) result.Add(current.ToString());
        return [.. result];
    }

    private static string Describe(Exception exception) => exception switch
    {
        HttpRequestException => $"{exception.Message} (is the server running and reachable?)",
        TaskCanceledException => "the server did not answer in time.",
        _ => exception.Message
    };

    public async ValueTask DisposeAsync()
    {
        await DisposeClientAsync();
        CloseGate();
    }

    /// <summary>
    /// The same teardown for a caller that has no thread to await on. The container this lives
    /// in is disposed synchronously when the application exits, and a service offering only
    /// <see cref="IAsyncDisposable"/> makes that disposal throw — which it did, inside the window
    /// procedure handling Windows' own end-of-session message, where nothing is left to catch it.
    /// That is the "unknown software exception" the machine put on the screen while turning off.
    ///
    /// The wait happens on the pool rather than here: awaited inline, the client's own teardown
    /// would post its continuations back to the very thread this call is blocking. A session that
    /// outlives the wait is left to the operating system, which is about to end it anyway.
    /// </summary>
    public void Dispose()
    {
        try { Task.Run(DisposeClientAsync).Wait(TimeSpan.FromMilliseconds(500)); }
        catch { /* a server that will not let go is not worth holding up the shutdown for */ }
        CloseGate();
    }

    private void CloseGate()
    {
        // Both disposals are reachable — the container picks one, tests may call the other — and
        // a semaphore only tolerates being closed once.
        try { _gate.Dispose(); }
        catch (ObjectDisposedException) { }
    }
}
