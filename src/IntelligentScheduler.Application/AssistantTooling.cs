using System.Text.Json;

namespace IntelligentScheduler.Application;

/// <summary>
/// A callable capability offered to the language model. <see cref="ParametersJson"/> is a
/// JSON Schema object, which is what every OpenAI-compatible endpoint, Ollama and MCP expect.
/// </summary>
public sealed record AssistantToolDefinition(
    string Name,
    string Description,
    string ParametersJson,
    /// <summary>False for read-only tools, which always run immediately.</summary>
    bool IsMutating = false,
    /// <summary>Mutating tools that are hard to undo and therefore need explicit confirmation.</summary>
    bool IsCritical = false,
    /// <summary>Set when the tool comes from an MCP server rather than the application itself.</summary>
    string? Source = null)
{
    public bool IsExternal => Source is not null;
}

public sealed record AssistantToolInvocation(string CallId, string Name, string ArgumentsJson);

/// <summary>
/// The outcome the model sees. When the access level requires confirmation the write never
/// happens here: the call becomes one or more <see cref="PendingActions"/> and the model is
/// told to wait. A batch tool proposes several at once, which is why this is a list.
/// </summary>
public sealed record AssistantToolResult(
    string CallId,
    string Name,
    string Content,
    bool IsError = false,
    IReadOnlyList<AssistantScheduleAction>? PendingActions = null,
    bool Applied = false)
{
    public AssistantToolResult(
        string callId, string name, string content, bool isError, AssistantScheduleAction pendingAction, bool applied = false)
        : this(callId, name, content, isError, [pendingAction], applied) { }

    /// <summary>The first proposal, for the callers that only ever expect one.</summary>
    public AssistantScheduleAction? PendingAction => PendingActions is { Count: > 0 } ? PendingActions[0] : null;

    public IReadOnlyList<AssistantScheduleAction> Proposals => PendingActions ?? [];
}

public interface IAssistantToolbox
{
    ValueTask<IReadOnlyList<AssistantToolDefinition>> GetToolsAsync(CancellationToken cancellationToken = default);
    ValueTask<AssistantToolResult> InvokeAsync(AssistantToolInvocation invocation, CancellationToken cancellationToken = default);
}

/// <summary>Applies a single approved schedule change. Shared by the chat loop and the confirm buttons.</summary>
public interface IScheduleActionExecutor
{
    /// <summary>Returns a short human-readable confirmation of what changed.</summary>
    Task<string> ApplyAsync(AssistantScheduleAction action, CancellationToken cancellationToken = default);
}

/// <summary>One line of "what the assistant actually did", shown under its reply and stored with the thread.</summary>
public sealed record AssistantToolTraceEntry(
    string Name,
    string Summary,
    bool Applied,
    bool IsError,
    bool IsExternal = false,
    /// <summary>The call produced a change waiting for the user to confirm it.</summary>
    bool IsProposal = false);

/// <summary>Small helpers shared by the toolbox implementations and their tests.</summary>
public static class AssistantToolJson
{
    /// <summary>
    /// Relaxed escaping keeps Cyrillic and other non-ASCII text readable in tool results
    /// instead of exploding into \uXXXX escapes, which both wastes tokens and confuses models.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static JsonElement Parse(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return EmptyObject();
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return EmptyObject();
        }
    }

    public static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Reads a string tolerantly: models frequently emit a number or a boolean where the
    /// schema asked for text, and <c>GetString</c> would throw on those.
    /// </summary>
    public static string? String(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var node)) return null;
        return node.ValueKind switch
        {
            JsonValueKind.String => node.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => node.GetRawText(),
            _ => null
        };
    }

    /// <summary>Reads an integer whether the model sent 45, 45.0 or "45".</summary>
    public static int? Int(JsonElement element, string name, int min = int.MinValue, int max = int.MaxValue)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var node)) return null;
        switch (node.ValueKind)
        {
            case JsonValueKind.Number when node.TryGetInt32(out var value):
                return Math.Clamp(value, min, max);
            case JsonValueKind.Number when node.TryGetDouble(out var raw):
                return Math.Clamp((int)Math.Round(raw), min, max);
            case JsonValueKind.String when int.TryParse(node.GetString(),
                System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed):
                return Math.Clamp(parsed, min, max);
            case JsonValueKind.String when double.TryParse(node.GetString(),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedRaw):
                return Math.Clamp((int)Math.Round(parsedRaw), min, max);
            default:
                return null;
        }
    }

    public static bool? Bool(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var node)) return null;
        return node.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(node.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    public static DateTimeOffset? Moment(JsonElement element, string name)
    {
        var raw = String(element, name);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeLocal, out var parsed) ? parsed : null;
    }

    public static DateOnly? Date(JsonElement element, string name)
    {
        var raw = String(element, name);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (DateOnly.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeLocal, out var moment)
            ? DateOnly.FromDateTime(moment.LocalDateTime)
            : null;
    }

    public static Guid? Id(JsonElement element, string name)
    {
        var raw = String(element, name);
        return Guid.TryParse(raw, out var parsed) ? parsed : null;
    }

    public static TEnum? Enum<TEnum>(JsonElement element, string name) where TEnum : struct, Enum
    {
        var raw = String(element, name);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return System.Enum.TryParse<TEnum>(raw.Replace("_", string.Empty, StringComparison.Ordinal), true, out var parsed)
            ? parsed
            : null;
    }

    public static string Serialize(object value) => JsonSerializer.Serialize(value, Options);
}
