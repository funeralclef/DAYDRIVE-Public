using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using IntelligentScheduler.Application;
using IntelligentScheduler.Domain;
using SkiaSharp;
using UglyToad.PdfPig;

namespace IntelligentScheduler.Infrastructure;

/// <summary>Live progress so the chat panel can say what the assistant is doing right now.</summary>
public sealed record AssistantProgress(AssistantProgressStage Stage, string? Detail = null);

public enum AssistantProgressStage { Connecting, Loading, Thinking, UsingTool, Writing, Done }

/// <summary>
/// The answer as far as it has arrived. A reply of a few hundred tokens takes seconds to
/// generate whatever the endpoint, and waiting for the last one before showing the first is
/// most of what "even simple questions are slow" actually is.
/// </summary>
public sealed record AssistantStreamUpdate(string Text);

/// <summary>
/// The conversation engine.
///
/// Every provider that can hold a chat runs the same loop: send the transcript plus the tool
/// catalogue, execute whatever the model calls, feed the results back, repeat until it answers
/// in words. That is what lets one thread mix ordinary conversation with real schedule changes
/// — the earlier design forced *every* reply through a single JSON envelope, so a planning
/// request either produced unparsable output or an exception that was silently swallowed into
/// an "offline" fallback, which is exactly what looked like the connection dropping.
///
/// Failures are now reported as failures, with the provider's own message attached.
/// </summary>
public sealed class AssistantService(
    ISettingsRepository settingsRepository,
    ITaskRepository tasks,
    ICalendarRepository events,
    IAIService localParser,
    ILocalModelRuntime localModels,
    ISecretProtector secrets,
    IAssistantToolbox toolbox,
    McpToolGateway mcp,
    IChatRepository? chatRepository = null,
    IUserMemoryRepository? memoryRepository = null) : IAssistantService, IDisposable
{
    /// <summary>
    /// One client for the life of the app, with its socket pool kept warm.
    ///
    /// A conversation is a burst of requests to the same host: the tool loop alone is several,
    /// and the handshake for a fresh TLS connection is worth more than the whole of a short
    /// answer. Connections are therefore held open between turns rather than retired at the
    /// default two minutes, and only the connect itself has a deadline — the request's own
    /// limit is the user's timeout setting, applied per call.
    /// </summary>
    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(15),
        EnableMultipleHttp2Connections = true,
        AutomaticDecompression = System.Net.DecompressionMethods.All
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
    };
    private const int MaxExtractedCharacters = 24_000;
    private const int DefaultToolIterations = 12;
    private const int DefaultHistoryTurns = 24;
    private const int DefaultMaxTokens = 4000;

    /// <summary>
    /// Reads the request-shaping options for whoever will answer, clamped to sane bounds. The
    /// managed local model and a hosted API keep separate sets: what suits a 9B model on this
    /// machine's own card — a shorter answer, a shorter memory, a longer wait — is not what
    /// suits a model in a data centre, and one set of numbers used to serve both badly.
    /// </summary>
    private static (double Temperature, int MaxTokens, double? TopP, int Iterations, int History, TimeSpan Timeout)
        Tuning(AppSettings settings)
    {
        var local = settings.AssistantProvider == AssistantProvider.Local;
        var temperature = local ? settings.LocalTemperature : settings.AssistantTemperature;
        var maxTokens = local ? settings.LocalMaxTokens : settings.AssistantMaxTokens;
        var topP = local ? settings.LocalTopP : settings.AssistantTopP;
        var iterations = local ? settings.LocalMaxToolIterations : settings.AssistantMaxToolIterations;
        var history = local ? settings.LocalHistoryTurns : settings.AssistantHistoryTurns;
        var timeout = local ? settings.LocalTimeoutMinutes : settings.AssistantTimeoutMinutes;
        return (
            Math.Clamp(temperature, 0, 2),
            Math.Clamp(maxTokens <= 0 ? DefaultMaxTokens : maxTokens, 64, 32_000),
            // 1.0 means nucleus sampling is off, and the field is then left out of the request.
            topP is > 0 and < 1 ? Math.Clamp(topP, 0.05, 1) : null,
            Math.Clamp(iterations <= 0 ? DefaultToolIterations : iterations, 1, 16),
            Math.Clamp(history <= 0 ? DefaultHistoryTurns : history, 2, 64),
            TimeSpan.FromMinutes(Math.Clamp(timeout <= 0 ? 5 : timeout, 1, 30)));
    }

    /// <summary>Builds the selection and launch options for the managed local server.</summary>
    private static (LocalModelSelection Selection, LocalRuntimeOptions Options) LocalSetup(AppSettings settings)
        => (new LocalModelSelection(settings.LocalModelId),
            new LocalRuntimeOptions(
                Math.Clamp(settings.LocalModelContextTokens <= 0 ? 8192 : settings.LocalModelContextTokens, 2048, 131_072),
                Math.Max(0, settings.LocalModelThreads),
                MayUseProjector(settings),
                Math.Max(0, settings.LocalModelIdleMinutes),
                settings.CustomModelPath,
                settings.CustomProjectorPath,
                settings.LocalModelGpuEnabled));

    /// <summary>
    /// Image input, narrowed to what this particular model can honour. Asked for from a model
    /// whose repository ships no projector, the switch alone used to make the engine refuse to
    /// start at all — waiting on a file that was never going to exist.
    /// </summary>
    private static bool MayUseProjector(AppSettings settings)
        => settings.LocalModelVisionEnabled
           && (LocalModelCatalog.IsCustom(settings.LocalModelId)
               ? !string.IsNullOrWhiteSpace(settings.CustomProjectorPath)
               : LocalModelCatalog.Find(settings.LocalModelId).SupportsVision);

    private readonly object _conversationGate = new();
    private readonly List<AssistantConversationTurn> _conversation = [];

    /// <summary>Raised on the calling thread while a request is in flight.</summary>
    public event EventHandler<AssistantProgress>? ProgressChanged;

    /// <summary>Raised as the answer arrives, carrying everything written so far.</summary>
    public event EventHandler<AssistantStreamUpdate>? ReplyStreaming;

    public Task<AssistantReply> AskAsync(string prompt, CancellationToken cancellationToken = default)
        => AskAsync(new AssistantAsk(prompt), cancellationToken);

    public Task<AssistantReply> AskAsync(string prompt, AssistantAttachment attachment, CancellationToken cancellationToken = default)
        => AskAsync(new AssistantAsk(prompt, null, attachment), cancellationToken);

    public async Task<AssistantReply> AskAsync(AssistantAsk ask, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ask.Prompt))
            throw new ArgumentException("Write a short request for the assistant.", nameof(ask));

        var prompt = ask.Prompt.Trim();
        var settings = await settingsRepository.GetAppSettingsAsync(cancellationToken);
        var mayShareContext = settings.AssistantProvider == AssistantProvider.Local || settings.ShareContextWithExternalAi;
        var conversationId = await EnsureConversationAsync(ask.ConversationId, prompt, cancellationToken);

        Report(AssistantProgressStage.Connecting);
        var request = await BuildRequestAsync(prompt, ask.Attachment, settings, mayShareContext, conversationId, cancellationToken);
        var timer = Stopwatch.StartNew();

        AssistantReply reply;
        try
        {
            reply = settings.AssistantProvider switch
            {
                AssistantProvider.Local when localModels.GetStatus(
                        LocalSetup(settings).Selection, settings.CustomModelPath).ModelInstalled
                    => await AskManagedLocalAsync(settings, request, cancellationToken),
                AssistantProvider.Ollama => await AskOllamaAsync(settings, request, cancellationToken),
                AssistantProvider.OpenAiCompatible => await AskOpenAiCompatibleAsync(settings, request, cancellationToken),
                AssistantProvider.Mcp => await AskMcpBridgeAsync(settings, request, cancellationToken),
                _ => await AskOfflineParserAsync(request, cancellationToken)
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (AssistantTransportException exception)
        {
            // Only a genuine transport failure falls back, and the user is told exactly why.
            var fallback = await AskOfflineParserAsync(request, cancellationToken);
            reply = fallback with
            {
                Message = $"{ProviderLabel(settings)} is unreachable: {exception.Message}\n\n{fallback.Message}",
                Provider = $"Offline · {ProviderLabel(settings)} unreachable",
                IsError = true
            };
        }
        catch (Exception exception)
        {
            reply = new AssistantReply(DescribeFailure(settings, exception), [], ProviderLabel(settings), TimeSpan.Zero, IsError: true);
        }
        timer.Stop();

        lock (_conversationGate)
        {
            _conversation.Add(new("user", prompt));
            _conversation.Add(new("assistant", reply.Message));
            var keep = Tuning(settings).History;
            if (_conversation.Count > keep) _conversation.RemoveRange(0, _conversation.Count - keep);
        }

        reply = reply with { Elapsed = timer.Elapsed, ConversationId = conversationId };
        await PersistAssistantMessageAsync(conversationId, reply, cancellationToken);
        Report(AssistantProgressStage.Done);
        return reply;
    }

    // ---------------------------------------------------------------- connection checks

    public async Task<AssistantConnectionResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = await settingsRepository.GetAppSettingsAsync(cancellationToken);
            if (settings.AssistantProvider == AssistantProvider.Local)
            {
                var (selection, options) = LocalSetup(settings);
                if (!localModels.GetStatus(selection, settings.CustomModelPath).ModelInstalled)
                    return new(false, selection.IsCustom
                        ? "Pick a GGUF file first. The lightweight offline parser remains available meanwhile."
                        : "Choose and download a local model first. The lightweight offline parser remains available meanwhile.");
                var endpoint = await localModels.EnsureReadyAsync(selection, options, cancellationToken);
                using var health = await _http.GetAsync(new Uri(endpoint, "/health"), cancellationToken);
                health.EnsureSuccessStatusCode();
                localModels.NotifyActivity(options.IdleMinutes);
                var name = selection.IsCustom
                    ? Path.GetFileName(settings.CustomModelPath)
                    : LocalModelCatalog.Find(selection.ModelId).DisplayName;
                return new(true, $"{name} is running locally.");
            }

            if (settings.AssistantProvider == AssistantProvider.Mcp) return await mcp.ProbeAsync(cancellationToken);

            using var message = new HttpRequestMessage(HttpMethod.Get,
                settings.AssistantProvider == AssistantProvider.Ollama
                    ? CombineEndpoint(settings.AssistantEndpoint, "/api/tags")
                    : CombineEndpoint(settings.AssistantEndpoint, "/v1/models"));
            AddAuthorization(message, settings);
            using var response = await _http.SendAsync(message, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return new(false, $"{ProviderLabel(settings)} answered {(int)response.StatusCode} {response.ReasonPhrase}: " +
                                  $"{Snippet(await response.Content.ReadAsStringAsync(cancellationToken))}");
            return new(true, $"Connected to {ProviderLabel(settings)}.");
        }
        catch (Exception exception)
        {
            return new(false, FriendlyError(exception));
        }
    }

    /// <summary>Lists every tool the current configuration would hand to the model, MCP included.</summary>
    public async Task<AssistantConnectionResult> ProbeToolsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await settingsRepository.GetAppSettingsAsync(cancellationToken);
        var builtIn = (await toolbox.GetToolsAsync(cancellationToken))
            .Select(tool => new AssistantToolProbe(tool.Name, tool.Description, false)).ToList();

        if (settings.AssistantProvider == AssistantProvider.Mcp && !settings.McpToolsEnabled)
            return await mcp.ProbeAsync(cancellationToken);

        if (!settings.McpToolsEnabled || !McpToolGateway.IsConfigured(settings))
            return new(true, $"{builtIn.Count} built-in tool(s). MCP is off or not configured.", builtIn);

        var external = await mcp.ProbeAsync(cancellationToken);
        var all = builtIn.Concat(external.Tools ?? []).ToList();
        return new(external.IsSuccess,
            external.IsSuccess
                ? $"{builtIn.Count} built-in + {all.Count - builtIn.Count} MCP tool(s) available to the model."
                : $"{builtIn.Count} built-in tool(s). {external.Message}",
            all);
    }

    // ---------------------------------------------------------------- request assembly

    private async Task<AssistantRequest> BuildRequestAsync(
        string prompt,
        AssistantAttachment? attachment,
        AppSettings settings,
        bool mayShareContext,
        Guid? conversationId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        var from = now.Date.AddDays(-7).ToUniversalTime();
        var to = now.Date.AddDays(35).ToUniversalTime();

        var conversation = await LoadHistoryAsync(conversationId, mayShareContext,
            Tuning(settings).History, cancellationToken);
        var memories = mayShareContext && settings.AssistantMemoryEnabled && memoryRepository is not null
            ? (await memoryRepository.GetAllAsync(cancellationToken)).Where(entry => entry.IsEnabled).ToList()
            : [];

        return new AssistantRequest(prompt, now, TimeZoneInfo.Local.Id,
            mayShareContext ? await tasks.GetAllAsync(cancellationToken) : [],
            mayShareContext ? await events.GetRangeAsync(from, to, cancellationToken) : [],
            attachment,
            conversation,
            settings.AssistantAccessLevel,
            memories,
            settings.LanguageCode);
    }

    private async Task<IReadOnlyList<AssistantConversationTurn>> LoadHistoryAsync(
        Guid? conversationId, bool mayShareContext, int historyTurns, CancellationToken cancellationToken)
    {
        if (!mayShareContext) return [];
        if (chatRepository is null || conversationId is not { } id)
        {
            lock (_conversationGate) return _conversation.TakeLast(historyTurns).ToArray();
        }

        var stored = await chatRepository.GetMessagesAsync(id, cancellationToken);
        return stored
            .Where(message => message.Role is ChatRole.User or ChatRole.Assistant && !message.IsError)
            .TakeLast(historyTurns)
            // The freshly stored user turn is sent separately as the live prompt.
            .SkipLast(1)
            .Select(message => new AssistantConversationTurn(
                message.Role == ChatRole.User ? "user" : "assistant", message.Content))
            .ToList();
    }

    private async Task<Guid?> EnsureConversationAsync(Guid? conversationId, string prompt, CancellationToken cancellationToken)
    {
        if (chatRepository is null) return null;
        var id = conversationId;
        if (id is null || await chatRepository.GetConversationAsync(id.Value, cancellationToken) is null)
            id = (await chatRepository.CreateConversationAsync(BuildTitle(prompt), cancellationToken)).Id;

        await chatRepository.AppendMessageAsync(new ChatMessage
        {
            ConversationId = id.Value,
            Role = ChatRole.User,
            Content = prompt
        }, cancellationToken);
        return id;
    }

    private async Task PersistAssistantMessageAsync(Guid? conversationId, AssistantReply reply, CancellationToken cancellationToken)
    {
        if (chatRepository is null || conversationId is not { } id) return;
        await chatRepository.AppendMessageAsync(new ChatMessage
        {
            ConversationId = id,
            Role = ChatRole.Assistant,
            Content = reply.Message,
            Meta = reply.Provider,
            IsError = reply.IsError,
            ToolTraceJson = reply.Trace.Count == 0 ? null : AssistantToolJson.Serialize(reply.Trace)
        }, cancellationToken);
    }

    internal static string BuildTitle(string prompt)
    {
        var flat = string.Join(' ', prompt.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (flat.Length <= 48) return flat;
        var cut = flat[..48];
        var lastSpace = cut.LastIndexOf(' ');
        return (lastSpace > 20 ? cut[..lastSpace] : cut) + "…";
    }

    // ---------------------------------------------------------------- providers

    private async Task<AssistantReply> AskManagedLocalAsync(AppSettings settings, AssistantRequest request, CancellationToken cancellationToken)
    {
        var (selection, options) = LocalSetup(settings);
        var model = LocalModelCatalog.Find(selection.ModelId);
        // Several gigabytes have to come off the disk before the first word can be written, and
        // a shell that says only "connecting" for the length of that is a shell that looks
        // broken. It is said plainly instead, and only when it is actually happening.
        if (localModels.Status.State is not LocalModelRuntimeState.Running)
            Report(AssistantProgressStage.Loading, model.DisplayName);
        var endpoint = await localModels.EnsureReadyAsync(selection, options, cancellationToken);
        localModels.NotifyActivity(options.IdleMinutes);
        try
        {
            var label = selection.IsCustom
                ? $"Local · {Path.GetFileNameWithoutExtension(settings.CustomModelPath)}"
                : $"Local · {model.DisplayName} {model.Quant.Tag}";
            var session = new ChatSession(
                new Uri(endpoint, "/v1/chat/completions"),
                model.DisplayName,
                label,
                ChatProtocol.OpenAi,
                Authorize: false);
            return await RunChatAsync(session, settings, request, cancellationToken);
        }
        finally { localModels.NotifyActivity(options.IdleMinutes); }
    }

    private Task<AssistantReply> AskOllamaAsync(AppSettings settings, AssistantRequest request, CancellationToken cancellationToken)
        => RunChatAsync(new ChatSession(
            new Uri(CombineEndpoint(settings.AssistantEndpoint, "/api/chat")),
            settings.AssistantModel,
            "Ollama",
            ChatProtocol.Ollama,
            Authorize: false), settings, request, cancellationToken);

    private Task<AssistantReply> AskOpenAiCompatibleAsync(AppSettings settings, AssistantRequest request, CancellationToken cancellationToken)
        => RunChatAsync(new ChatSession(
            new Uri(CombineEndpoint(settings.AssistantEndpoint, "/v1/chat/completions")),
            settings.AssistantModel,
            string.IsNullOrWhiteSpace(settings.AssistantModel) ? "API" : $"API · {settings.AssistantModel}",
            ChatProtocol.OpenAi,
            Authorize: true), settings, request, cancellationToken);

    /// <summary>
    /// Legacy MCP bridge: the server itself answers through one prompt+context tool. Kept so
    /// servers written against the earlier contract keep working; tool federation is the
    /// modern path and lives in <see cref="McpToolGateway"/>.
    /// </summary>
    private async Task<AssistantReply> AskMcpBridgeAsync(AppSettings settings, AssistantRequest request, CancellationToken cancellationToken)
    {
        Report(AssistantProgressStage.Thinking, settings.McpToolName);
        var raw = await mcp.CallBridgeToolAsync(settings, request.Prompt, BuildContextJson(request), cancellationToken);
        var parsed = ParseReply(raw, $"MCP · {settings.McpToolName}");
        var trace = parsed.ScheduleActions
            .Select(action => new AssistantToolTraceEntry(action.Kind.ToString(), action.Reason ?? action.Title ?? string.Empty, false, false, true))
            .ToList();
        return parsed with { ToolTrace = trace };
    }

    private async Task<AssistantReply> AskOfflineParserAsync(AssistantRequest request, CancellationToken cancellationToken)
    {
        var promptLower = request.Prompt.ToLowerInvariant();

        if (LooksLikeTaskCapture(request.Prompt))
        {
            var cleaned = StripCaptureVerb(request.Prompt);
            var draft = await localParser.ParseTaskAsync(cleaned,
                new UserPlanningContext(request.Now, request.TimeZoneId), cancellationToken);
            var suggestion = new AssistantTaskSuggestion(draft.Title, draft.EstimatedDurationMin, draft.Priority,
                draft.DeadlineLocal, draft.PreferredTimeOfDay,
                draft.Assumptions.Count == 0 ? "Parsed locally" : string.Join(" ", draft.Assumptions));
            return new AssistantReply("I prepared a task suggestion. Use the action below to add it to your plan.", [suggestion], "Local", TimeSpan.Zero);
        }

        var open = request.Tasks.Where(task => task.Status is not Domain.TaskStatus.Done and not Domain.TaskStatus.Cancelled)
            .OrderByDescending(task => task.Priority).ThenBy(task => task.DeadlineUtc).Take(5).ToList();

        if (promptLower.Contains("plan"))
        {
            if (open.Count == 0)
            {
                var suggestion = new AssistantTaskSuggestion("Weekly planning & priority review", 30, 70, null, PreferredTimeOfDay.Morning, "Recommended focus");
                return new AssistantReply("You don't have unscheduled tasks yet. I prepared an optional planning task.", [suggestion], "Local", TimeSpan.Zero);
            }

            var titles = string.Join(", ", open.Take(3).Select(task => $"\"{task.Title}\""));
            return new AssistantReply($"Unscheduled tasks to consider: {titles}. I can discuss possible times, but nothing is placed in the calendar automatically.", [], "Local", TimeSpan.Zero);
        }

        if (promptLower.Contains("schedule") || promptLower.Contains("today"))
        {
            var today = DateOnly.FromDateTime(request.Now.LocalDateTime);
            var todayEvents = request.Events.Where(e => DateOnly.FromDateTime(e.StartUtc.ToLocalTime().Date) == today).ToList();
            var eventInfo = todayEvents.Count > 0
                ? $"Today you have {todayEvents.Count} event(s): {string.Join(", ", todayEvents.Select(e => e.Title))}. "
                : "No fixed calendar events today. ";

            var taskInfo = open.Count > 0
                ? $"{open.Count} priority tasks waiting, e.g.: \"{open[0].Title}\"."
                : "Task list is clear.";

            return new AssistantReply(eventInfo + taskInfo, [], "Local", TimeSpan.Zero);
        }

        if (open.Count == 0)
            return new AssistantReply("Your task list is clear. Tell me what you want to schedule (e.g. 'write report tomorrow at 3pm for 45 minutes') and I'll prepare it.", [], "Local", TimeSpan.Zero);

        var next = string.Join(", ", open.Take(3).Select(task => task.Title));
        return new AssistantReply($"Focus on: {next}. You can also type a new task or request day planning.", [], "Local", TimeSpan.Zero);
    }

    // ---------------------------------------------------------------- the tool loop

    private enum ChatProtocol { OpenAi, Ollama }

    private sealed record ChatSession(Uri Endpoint, string Model, string Label, ChatProtocol Protocol, bool Authorize);

    /// <summary>Options the endpoint rejected, so the retry can drop them instead of failing outright.</summary>
    private sealed class ChatCapabilities
    {
        public bool SupportsTools { get; set; } = true;
        public bool SupportsTemperature { get; set; } = true;
        public bool UsesLegacyMaxTokens { get; set; } = true;
        /// <summary>Gateways that cannot stream say so with a 400; the retry then buffers.</summary>
        public bool SupportsStreaming { get; set; } = true;
    }

    private async Task<AssistantReply> RunChatAsync(
        ChatSession session, AppSettings settings, AssistantRequest request, CancellationToken cancellationToken)
    {
        var capabilities = new ChatCapabilities();
        var tuning = Tuning(settings);
        var tools = await CollectToolsAsync(settings, cancellationToken);
        var messages = await BuildMessagesAsync(session, settings, request, cancellationToken);

        var trace = new List<AssistantToolTraceEntry>();
        var pending = new List<AssistantScheduleAction>();
        var appliedAny = false;

        for (var iteration = 0; iteration < tuning.Iterations; iteration++)
        {
            Report(iteration == 0 ? AssistantProgressStage.Thinking : AssistantProgressStage.Writing);
            var turn = await SendTurnAsync(session, settings, messages, tools, capabilities, cancellationToken);

            if (turn.ToolCalls.Count == 0)
            {
                var text = turn.Content;
                // A model without tool support may still answer with the legacy JSON envelope.
                var parsed = ParseReply(text, session.Label);
                foreach (var action in parsed.ScheduleActions)
                {
                    if (AssistantPermissionPolicy.MayApplyAutomatically(settings.AssistantAccessLevel, action)) pending.Add(action);
                    else pending.Add(action);
                }
                return parsed with
                {
                    Actions = pending.Count == 0 ? parsed.Actions : pending,
                    ToolTrace = trace,
                    Provider = session.Label + (appliedAny ? " · applied" : string.Empty)
                };
            }

            messages.Add(turn.RawAssistantMessage);
            Report(AssistantProgressStage.UsingTool, turn.ToolCalls[0].Name);
            var results = await InvokeToolsAsync(turn.ToolCalls, settings, cancellationToken);
            for (var index = 0; index < turn.ToolCalls.Count; index++)
            {
                var call = turn.ToolCalls[index];
                var result = results[index];
                pending.AddRange(result.Proposals);
                if (result.Applied) appliedAny = true;
                trace.Add(new AssistantToolTraceEntry(call.Name, Snippet(result.Content, 160), result.Applied, result.IsError,
                    call.Name.StartsWith(McpToolGateway.Prefix, StringComparison.Ordinal),
                    result.Proposals.Count > 0));
                messages.Add(ToolResultMessage(session.Protocol, call, result));
            }
        }

        return new AssistantReply(
            "I ran out of steps while working on this. Tell me which part to finish and I'll continue.",
            [], session.Label, TimeSpan.Zero, pending, trace);
    }

    private async Task<IReadOnlyList<AssistantToolDefinition>> CollectToolsAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var builtIn = await toolbox.GetToolsAsync(cancellationToken);
        if (!settings.McpToolsEnabled || !McpToolGateway.IsConfigured(settings)) return builtIn;
        var external = await mcp.GetToolsAsync(cancellationToken);
        return external.Count == 0 ? builtIn : [.. builtIn, .. external];
    }

    /// <summary>
    /// Runs one turn's worth of calls, reads together and writes in order.
    ///
    /// A model asked to plan a week wants the calendar, the task list and the free slots, and
    /// it asks for all three at once. Answering them one after another made the user wait for
    /// three database round trips laid end to end for no reason: none of the three can affect
    /// another. Anything that writes still goes in the order the model asked for it, because
    /// there the order is the meaning.
    /// </summary>
    private async Task<AssistantToolResult[]> InvokeToolsAsync(
        IReadOnlyList<AssistantToolInvocation> calls, AppSettings settings, CancellationToken cancellationToken)
    {
        var results = new AssistantToolResult[calls.Count];
        var together = new List<Task>();
        for (var index = 0; index < calls.Count; index++)
        {
            if (!ScheduleToolbox.IsReadOnly(calls[index].Name)) continue;
            var slot = index;
            together.Add(Task.Run(async () =>
                results[slot] = await InvokeToolAsync(calls[slot], settings, cancellationToken), cancellationToken));
        }
        if (together.Count > 0) await Task.WhenAll(together);

        for (var index = 0; index < calls.Count; index++)
        {
            if (results[index] is not null) continue;
            cancellationToken.ThrowIfCancellationRequested();
            Report(AssistantProgressStage.UsingTool, calls[index].Name);
            results[index] = await InvokeToolAsync(calls[index], settings, cancellationToken);
        }
        return results;
    }

    private async Task<AssistantToolResult> InvokeToolAsync(
        AssistantToolInvocation call, AppSettings settings, CancellationToken cancellationToken)
    {
        if (call.Name.StartsWith(McpToolGateway.Prefix, StringComparison.Ordinal))
        {
            if (!settings.McpToolsEnabled)
                return new(call.CallId, call.Name, "MCP tools are switched off in settings.", IsError: true);
            return await mcp.InvokeAsync(call, cancellationToken);
        }
        return await toolbox.InvokeAsync(call, cancellationToken);
    }

    private sealed record ChatTurn(string Content, IReadOnlyList<AssistantToolInvocation> ToolCalls, object RawAssistantMessage);

    private async Task<ChatTurn> SendTurnAsync(
        ChatSession session,
        AppSettings settings,
        List<object> messages,
        IReadOnlyList<AssistantToolDefinition> tools,
        ChatCapabilities capabilities,
        CancellationToken cancellationToken)
    {
        var timeout = Tuning(settings).Timeout;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var payload = BuildPayload(session, messages, tools, capabilities, Tuning(settings));
            using var message = new HttpRequestMessage(HttpMethod.Post, session.Endpoint)
            {
                // Relaxed escaping keeps non-Latin text as real UTF-8 in the request body,
                // which is smaller and far easier to read when debugging a provider.
                Content = JsonContent.Create(payload, options: PayloadJson)
            };
            if (session.Authorize) AddAuthorization(message, settings);

            // The client itself has no timeout so the limit can follow the setting.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);

            HttpResponseMessage response;
            try
            {
                // Headers first: a streamed answer is read as it is written, and waiting for the
                // last token before showing the first is most of the delay that is actually felt.
                response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new AssistantTransportException("the request timed out.");
            }
            catch (HttpRequestException exception)
            {
                throw new AssistantTransportException(exception.Message, exception);
            }
            catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new AssistantTransportException("the request timed out.", exception);
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    try
                    {
                        // An endpoint that ignored "stream" answers with one whole JSON body, and
                        // that path is read exactly as it always was.
                        return IsIncremental(response)
                            ? await ReadStreamedTurnAsync(session.Protocol, response, deadline.Token)
                            : ReadTurn(session.Protocol, await response.Content.ReadAsStringAsync(deadline.Token));
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new AssistantTransportException("the answer stopped arriving before it was finished.");
                    }
                    catch (IOException exception)
                    {
                        throw new AssistantTransportException(exception.Message, exception);
                    }
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                // Gateways differ in which optional fields they accept. Rather than failing the
                // whole turn, drop the offending option once and try again.
                if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity
                    && Downgrade(capabilities, body))
                    continue;

                throw new AssistantProviderException(
                    $"{session.Label} answered {(int)response.StatusCode} {response.ReasonPhrase}: {ExtractError(body)}");
            }
        }

        throw new AssistantProviderException($"{session.Label} rejected the request three times in a row.");
    }

    /// <summary>Whether the body arrives in pieces: server-sent events, or Ollama's line-per-token JSON.</summary>
    private static bool IsIncremental(HttpResponseMessage response)
        => response.Content.Headers.ContentType?.MediaType is "text/event-stream" or "application/x-ndjson";

    /// <summary>
    /// Assembles one turn out of a stream, raising the text as it lands.
    ///
    /// Both protocols split a reply across many frames, and tool calls come apart the same way —
    /// a few characters of the argument JSON at a time, keyed by position rather than by id — so
    /// they are put back together here before anything is executed.
    /// </summary>
    private async Task<ChatTurn> ReadStreamedTurnAsync(
        ChatProtocol protocol, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var partial = new SortedDictionary<int, PartialToolCall>();
        var lastReport = Stopwatch.StartNew();
        var reported = 0;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (line.Length == 0) continue;

            var frame = line;
            if (protocol == ChatProtocol.OpenAi)
            {
                // Server-sent events also carry comments and event names; only data matters.
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                frame = line[5..].Trim();
                if (frame.Length == 0) continue;
                if (frame == "[DONE]") break;
            }

            JsonElement root;
            try
            {
                using var json = JsonDocument.Parse(frame);
                root = json.RootElement.Clone();
            }
            // A truncated or non-JSON frame is not worth failing a whole answer over.
            catch (JsonException) { continue; }

            if (ReadStreamError(root) is { } error) throw new AssistantProviderException(error);

            var node = protocol == ChatProtocol.OpenAi
                ? OpenAiDelta(root)
                : root.TryGetProperty("message", out var ollama) ? ollama : default;
            if (node.ValueKind != JsonValueKind.Object) continue;

            text.Append(ReadContent(node));
            MergeToolCallDeltas(node, partial);

            // The panel redraws from this and re-parses markdown each time, so the stream is
            // sampled rather than mirrored frame for frame.
            if (text.Length > reported && lastReport.ElapsedMilliseconds >= 70)
            {
                reported = text.Length;
                lastReport.Restart();
                ReplyStreaming?.Invoke(this, new AssistantStreamUpdate(text.ToString()));
            }
        }

        var content = text.ToString();
        if (content.Length > reported) ReplyStreaming?.Invoke(this, new AssistantStreamUpdate(content));

        var calls = partial.Values
            .Where(call => !string.IsNullOrWhiteSpace(call.Name))
            .Select((call, index) => new AssistantToolInvocation(
                string.IsNullOrWhiteSpace(call.Id) ? $"call_{index}" : call.Id!,
                call.Name!,
                call.Arguments.Length == 0 ? "{}" : call.Arguments.ToString()))
            .ToList();

        return new(content, calls, BuildAssistantEcho(protocol, content, calls));
    }

    private static JsonElement OpenAiDelta(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) return default;
        var choice = choices[0];
        if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object) return delta;
        // A gateway that streams whole messages rather than deltas still parses here.
        return choice.TryGetProperty("message", out var whole) ? whole : default;
    }

    /// <summary>A tool call still being spelled out across frames.</summary>
    private sealed class PartialToolCall
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public StringBuilder Arguments { get; } = new();
    }

    private static void MergeToolCallDeltas(JsonElement node, SortedDictionary<int, PartialToolCall> partial)
    {
        if (!node.TryGetProperty("tool_calls", out var calls) || calls.ValueKind != JsonValueKind.Array) return;
        var fallbackIndex = partial.Count;
        foreach (var call in calls.EnumerateArray())
        {
            if (call.ValueKind != JsonValueKind.Object) continue;
            var index = call.TryGetProperty("index", out var indexNode) && indexNode.ValueKind == JsonValueKind.Number
                        && indexNode.TryGetInt32(out var parsed)
                ? parsed
                : fallbackIndex++;
            if (!partial.TryGetValue(index, out var entry)) partial[index] = entry = new PartialToolCall();

            if (call.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(id.GetString()))
                entry.Id = id.GetString();
            if (!call.TryGetProperty("function", out var function) || function.ValueKind != JsonValueKind.Object) continue;
            if (function.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(name.GetString()))
                entry.Name = name.GetString();
            if (!function.TryGetProperty("arguments", out var arguments)) continue;
            // OpenAI streams the argument object as text, a few characters per frame; Ollama
            // sends it whole, as a real object, in a single frame.
            if (arguments.ValueKind == JsonValueKind.String) entry.Arguments.Append(arguments.GetString());
            else if (arguments.ValueKind == JsonValueKind.Object) entry.Arguments.Append(arguments.GetRawText());
        }
    }

    /// <summary>Some gateways report a failure mid-stream, after the 200 has already gone out.</summary>
    private static string? ReadStreamError(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out var error)) return null;
        if (error.ValueKind == JsonValueKind.String) return error.GetString();
        if (error.ValueKind != JsonValueKind.Object) return null;
        return AssistantToolJson.String(error, "message") ?? error.GetRawText();
    }

    /// <summary>Removes one unsupported request option based on the endpoint's own error text.</summary>
    private static bool Downgrade(ChatCapabilities capabilities, string body)
    {
        var lower = body.ToLowerInvariant();
        if (capabilities.SupportsTools && (lower.Contains("tool") || lower.Contains("function")) &&
            (lower.Contains("not supported") || lower.Contains("unsupported") || lower.Contains("does not support")
             || lower.Contains("unknown parameter") || lower.Contains("unrecognized")))
        {
            capabilities.SupportsTools = false;
            return true;
        }
        if (capabilities.SupportsTemperature && lower.Contains("temperature"))
        {
            capabilities.SupportsTemperature = false;
            return true;
        }
        if (capabilities.UsesLegacyMaxTokens && lower.Contains("max_tokens"))
        {
            capabilities.UsesLegacyMaxTokens = false;
            return true;
        }
        if (capabilities.SupportsStreaming && lower.Contains("stream"))
        {
            capabilities.SupportsStreaming = false;
            return true;
        }
        return false;
    }

    private static object BuildPayload(
        ChatSession session, List<object> messages, IReadOnlyList<AssistantToolDefinition> tools,
        ChatCapabilities capabilities,
        (double Temperature, int MaxTokens, double? TopP, int Iterations, int History, TimeSpan Timeout) tuning)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = session.Model,
            ["messages"] = messages,
            ["stream"] = capabilities.SupportsStreaming
        };
        if (session.Protocol == ChatProtocol.OpenAi)
        {
            if (capabilities.SupportsTemperature) payload["temperature"] = tuning.Temperature;
            payload[capabilities.UsesLegacyMaxTokens ? "max_tokens" : "max_completion_tokens"] = tuning.MaxTokens;
            if (tuning.TopP is { } topP) payload["top_p"] = topP;
            if (capabilities.SupportsTools && tools.Count > 0)
            {
                payload["tools"] = tools.Select(ToOpenAiTool).ToArray();
                payload["tool_choice"] = "auto";
            }
        }
        else
        {
            // Ollama takes its sampling settings inside "options"; a temperature sent at the top
            // level, where OpenAI wants it, is simply ignored — so the slider did nothing here.
            var options = new Dictionary<string, object?> { ["num_predict"] = tuning.MaxTokens };
            if (capabilities.SupportsTemperature) options["temperature"] = tuning.Temperature;
            if (tuning.TopP is { } ollamaTopP) options["top_p"] = ollamaTopP;
            payload["options"] = options;
            if (capabilities.SupportsTools && tools.Count > 0) payload["tools"] = tools.Select(ToOpenAiTool).ToArray();
        }
        return payload;
    }

    private static object ToOpenAiTool(AssistantToolDefinition tool) => new
    {
        type = "function",
        function = new
        {
            name = tool.Name,
            description = tool.Description,
            parameters = JsonSerializer.Deserialize<JsonElement>(tool.ParametersJson)
        }
    };

    private static ChatTurn ReadTurn(ChatProtocol protocol, string body)
    {
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        var messageNode = protocol == ChatProtocol.OpenAi
            ? root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
                ? choices[0].TryGetProperty("message", out var node) ? node : default
                : default
            : root.TryGetProperty("message", out var ollamaNode) ? ollamaNode : default;

        if (messageNode.ValueKind != JsonValueKind.Object)
            throw new AssistantProviderException($"The response had no message: {Snippet(body, 240)}");

        var content = ReadContent(messageNode);
        var calls = ReadToolCalls(messageNode);
        return new(content, calls, BuildAssistantEcho(protocol, content, calls));
    }

    /// <summary>Handles plain strings, OpenAI content-part arrays, and reasoning-only replies.</summary>
    private static string ReadContent(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content)) return string.Empty;
        switch (content.ValueKind)
        {
            case JsonValueKind.String:
                return content.GetString() ?? string.Empty;
            case JsonValueKind.Array:
            {
                var builder = new StringBuilder();
                foreach (var part in content.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.String) builder.Append(part.GetString());
                    else if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty("text", out var text) &&
                             text.ValueKind == JsonValueKind.String) builder.Append(text.GetString());
                }
                return builder.ToString();
            }
            default:
                return string.Empty;
        }
    }

    private static IReadOnlyList<AssistantToolInvocation> ReadToolCalls(JsonElement message)
    {
        if (!message.TryGetProperty("tool_calls", out var calls) || calls.ValueKind != JsonValueKind.Array) return [];
        var result = new List<AssistantToolInvocation>();
        var index = 0;
        foreach (var call in calls.EnumerateArray())
        {
            if (!call.TryGetProperty("function", out var function) || function.ValueKind != JsonValueKind.Object) continue;
            var name = function.TryGetProperty("name", out var nameNode) && nameNode.ValueKind == JsonValueKind.String
                ? nameNode.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(name)) continue;

            // OpenAI sends arguments as a JSON string; Ollama sends a real object.
            var argumentsJson = "{}";
            if (function.TryGetProperty("arguments", out var argumentsNode))
                argumentsJson = argumentsNode.ValueKind switch
                {
                    JsonValueKind.String => argumentsNode.GetString() ?? "{}",
                    JsonValueKind.Object => argumentsNode.GetRawText(),
                    _ => "{}"
                };

            var id = call.TryGetProperty("id", out var idNode) && idNode.ValueKind == JsonValueKind.String
                ? idNode.GetString()!
                : $"call_{index}";
            result.Add(new(id, name!, argumentsJson));
            index++;
        }
        return result;
    }

    /// <summary>
    /// The assistant turn as it has to be echoed back on the next request. Rebuilt from what was
    /// understood rather than forwarded verbatim, because a streamed turn never existed as a
    /// single message in the first place.
    /// </summary>
    private static object BuildAssistantEcho(
        ChatProtocol protocol, string content, IReadOnlyList<AssistantToolInvocation> calls)
    {
        if (calls.Count == 0) return new { role = "assistant", content };

        if (protocol == ChatProtocol.Ollama)
            return new
            {
                role = "assistant",
                content,
                tool_calls = calls.Select(call => new
                {
                    function = new { name = call.Name, arguments = JsonSerializer.Deserialize<JsonElement>(call.ArgumentsJson) }
                }).ToArray()
            };

        return new
        {
            role = "assistant",
            content = string.IsNullOrEmpty(content) ? null : content,
            tool_calls = calls.Select(call => new
            {
                id = call.CallId,
                type = "function",
                function = new { name = call.Name, arguments = call.ArgumentsJson }
            }).ToArray()
        };
    }

    private static object ToolResultMessage(ChatProtocol protocol, AssistantToolInvocation call, AssistantToolResult result)
        => protocol == ChatProtocol.Ollama
            ? new { role = "tool", tool_name = call.Name, content = result.Content }
            : new { role = "tool", tool_call_id = call.CallId, content = result.Content };

    private async Task<List<object>> BuildMessagesAsync(
        ChatSession session, AppSettings settings, AssistantRequest request, CancellationToken cancellationToken)
    {
        var messages = new List<object> { new { role = "system", content = BuildSystemPrompt(settings, request) } };

        foreach (var turn in request.Conversation ?? [])
        {
            if (string.IsNullOrWhiteSpace(turn.Content)) continue;
            messages.Add(new { role = turn.Role == "user" ? "user" : "assistant", content = turn.Content });
        }

        var prepared = request.Attachment is null
            ? null
            : await PrepareAttachmentAsync(request.Attachment, VisionSupport.For(settings), cancellationToken);

        // The clock and the schedule ride on the live turn, not in front of everything else.
        var live = BuildLiveContext(settings, request);
        var prompt = live.Length == 0 ? request.Prompt : $"{live}\n{request.Prompt}";

        if (prepared is { ImageDataUrl: not null } && session.Protocol == ChatProtocol.OpenAi)
        {
            messages.Add(new
            {
                role = "user",
                content = new object[]
                {
                    new { type = "text", text = prompt },
                    new { type = "image_url", image_url = new { url = prepared.ImageDataUrl } }
                }
            });
        }
        else if (prepared is { ImageDataUrl: not null } && session.Protocol == ChatProtocol.Ollama)
        {
            var base64 = prepared.ImageDataUrl[(prepared.ImageDataUrl.IndexOf(",", StringComparison.Ordinal) + 1)..];
            messages.Add(new { role = "user", content = prompt, images = new[] { base64 } });
        }
        else if (prepared is { ExtractedText: not null })
        {
            messages.Add(new
            {
                role = "user",
                content = $"{prompt}\n\n--- Attached document ({prepared.FileName}) ---\n{prepared.ExtractedText}"
            });
        }
        else
        {
            messages.Add(new { role = "user", content = prompt });
        }

        return messages;
    }

    /// <summary>
    /// Everything the model is told that does not change from one message to the next.
    ///
    /// This is the expensive half of the prompt — the instructions and, rendered in front of
    /// them by the chat template, the whole tool catalogue — and it is why it is kept free of
    /// the clock and the calendar. An engine answering on this machine re-reads the prompt only
    /// as far as it differs from the one before it, so a single "local time 14:07" line at the
    /// top used to invalidate every token behind it: five thousand of them re-read from scratch
    /// on a message as small as "hello", before a single word of the answer could be produced.
    /// The volatile half rides on the live turn instead, in <see cref="BuildLiveContext"/>,
    /// where only what follows it has to be read again.
    /// </summary>
    internal static string BuildSystemPrompt(AppSettings settings, AssistantRequest request)
    {
        var sharesContext = ScheduleToolbox.MayShareContext(settings);
        var builder = new StringBuilder();
        builder.AppendLine("You are DAYDRIVE, the assistant living inside a compact local-first calendar on the user's computer.");
        builder.AppendLine("Talk like a capable, concise colleague. Ordinary conversation, advice, questions about an attached");
        builder.AppendLine("document or image are all perfectly normal — you are not restricted to scheduling.");
        if (!string.IsNullOrWhiteSpace(request.LanguageCode))
            builder.AppendLine(CultureInfo.InvariantCulture, $"Interface language: {request.LanguageCode}. Reply in the language the user writes in.");
        builder.AppendLine();
        builder.AppendLine("The message you are answering opens with the current date and time and, when the user shares");
        builder.AppendLine("it, a snapshot of their schedule. That snapshot is authoritative and current: answer from it");
        builder.AppendLine("directly — including ids for editing — and only call a reading tool for dates outside it, for");
        builder.AppendLine("numbers it does not carry, or to re-check after you have changed something.");
        builder.AppendLine();

        builder.AppendLine("## How this app models time");
        builder.AppendLine("- An **event** is a real slot on the calendar with a start and an end. Only events occupy time.");
        builder.AppendLine("- A **task** is an intention with no slot yet. Creating a task never books time.");
        builder.AppendLine("- Nothing is placed automatically. If the user wants time reserved, create an event.");
        builder.AppendLine();
        builder.AppendLine("## Working with tools");
        builder.AppendLine("- Answer without any tool at all when nothing needs looking up: greetings, thanks, general");
        builder.AppendLine("  knowledge, advice, questions about an attachment, and anything the snapshot above already");
        builder.AppendLine("  answers. A tool call the user did not need is a round trip they wait through for nothing.");
        if (!sharesContext)
        {
            builder.AppendLine("- The user has NOT allowed their schedule to be shared with an external model, so the");
            builder.AppendLine("  reading tools are unavailable. Never guess what is in their calendar. Ask them to paste");
            builder.AppendLine("  the details, or to enable context sharing in settings.");
        }
        else
        {
            builder.AppendLine("- Beyond the snapshot, never guess: `get_schedule`, `get_tasks`, `search_schedule`,");
            builder.AppendLine("  `find_free_slots` and `get_analytics` return the real thing.");
        }
        builder.AppendLine("- Ask for everything you need in ONE turn. Several calls sent together run at the same time");
        builder.AppendLine("  and cost one wait; the same calls sent one after another cost one wait each.");
        builder.AppendLine("- Creating several events is `create_events`, once — not `create_event` over and over.");
        builder.AppendLine("- Use exact ids when updating or deleting.");
        builder.AppendLine("- After the tools have run, answer the user in prose. Do not describe the tool calls mechanically.");
        builder.AppendLine();
        builder.AppendLine("## When the request is vague, broad or self-contradictory");
        builder.AppendLine("Do not stall on it and do not answer a smaller question instead. Work it out:");
        builder.AppendLine("1. Say in one line what you take the request to mean, and name any assumption you had to make.");
        builder.AppendLine("2. Gather what you need in a single batch of tool calls.");
        builder.AppendLine("3. Do the part that is unambiguous, and propose the rest concretely — real dates, real times,");
        builder.AppendLine("   real durations — so the user answers by approving or correcting rather than by explaining.");
        builder.AppendLine("4. Ask at most one question, and only when no reasonable assumption exists. Two contradictory");
        builder.AppendLine("   instructions are worth naming; ordinary vagueness is worth a sensible default instead.");
        builder.AppendLine("Split a large request into steps yourself. \"Sort out my week\" is: read the week, find what is");
        builder.AppendLine("overloaded or empty, propose the moves as a short numbered list, then apply what you are allowed to.");
        builder.AppendLine();
        builder.AppendLine("## Permission");
        builder.AppendLine(settings.AssistantAccessLevel switch
        {
            AssistantAccessLevel.FullAccess =>
                "- Access level: full. Write tools take effect immediately; report what you changed.",
            AssistantAccessLevel.AutoSafe =>
                "- Access level: automatic for safe changes. Creating and editing applies immediately; deletions and whole-day operations come back as \"proposed\" and need the user's confirmation in the app.",
            _ =>
                "- Access level: confirm everything. Write tools do NOT take effect — they return \"proposed\" and the user confirms in the app. Never claim a change is saved unless the tool result says it was applied."
        });
        builder.AppendLine();

        if (request.Memories is { Count: > 0 })
        {
            builder.AppendLine("## What you already know about this user");
            foreach (var memory in request.Memories.Take(40))
                builder.AppendLine(CultureInfo.InvariantCulture, $"- [{memory.Category}] {memory.Key}: {memory.Value}");
            builder.AppendLine("Use these naturally. Call `remember_about_user` when the user reveals a durable preference,");
            builder.AppendLine("routine, constraint or goal worth keeping — not for one-off details.");
            builder.AppendLine();
        }
        else if (settings.AssistantMemoryEnabled)
        {
            builder.AppendLine("## Memory");
            builder.AppendLine("Nothing is stored about this user yet. Call `remember_about_user` when they reveal a durable");
            builder.AppendLine("preference, routine, constraint or goal worth keeping across conversations.");
            builder.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(settings.AssistantExtraInstructions))
        {
            builder.AppendLine("## Extra instructions from the user");
            builder.AppendLine(settings.AssistantExtraInstructions.Trim());
            builder.AppendLine("Follow these unless they conflict with the permission rules above.");
            builder.AppendLine();
        }

        builder.AppendLine("## If tool calling is unavailable");
        builder.AppendLine("Then, and only then, answer with a single JSON object:");
        builder.AppendLine("""{"message":"natural reply","tasks":[],"actions":[{"kind":"create_event","title":"…","startLocal":"ISO-8601","endLocal":"ISO-8601","reason":"…"}]}""");
        return builder.ToString();
    }

    /// <summary>
    /// The clock and the calendar, put in front of the user's own words on the turn they belong
    /// to. Everything before it — the instructions, the tool catalogue and every earlier turn —
    /// is then identical to the last request and does not have to be read again.
    /// </summary>
    internal static string BuildLiveContext(AppSettings settings, AssistantRequest request)
    {
        var now = request.Now;
        var builder = new StringBuilder();
        builder.AppendLine("## Right now");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Local date and time: {now:yyyy-MM-dd HH:mm} ({now.DayOfWeek}), time zone {request.TimeZoneId}.");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Today is {now:yyyy-MM-dd}, tomorrow is {now.AddDays(1):yyyy-MM-dd}.");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- This week runs {StartOfWeek(now):yyyy-MM-dd} (Mon) to {StartOfWeek(now).AddDays(6):yyyy-MM-dd} (Sun); next week starts {StartOfWeek(now).AddDays(7):yyyy-MM-dd}.");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- The coming weekend is {StartOfWeek(now).AddDays(5):yyyy-MM-dd} and {StartOfWeek(now).AddDays(6):yyyy-MM-dd}. End of this month: {EndOfMonth(now):yyyy-MM-dd}.");
        builder.AppendLine();

        // The snapshot is why an ordinary question can be answered in one round trip. Reading
        // the next fortnight costs one local query that this request already made; asking the
        // model to fetch the same thing over the network costs the user a whole extra turn.
        if (ScheduleToolbox.MayShareContext(settings))
        {
            builder.AppendLine("## The user's actual schedule, as of this message");
            builder.Append(BuildScheduleDigest(request));
            builder.AppendLine();
        }
        return builder.ToString();
    }

    private static DateOnly StartOfWeek(DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        return today.AddDays(-((int)today.DayOfWeek + 6) % 7);
    }

    private static DateOnly EndOfMonth(DateTimeOffset now)
    {
        var local = now.LocalDateTime;
        return new DateOnly(local.Year, local.Month, DateTime.DaysInMonth(local.Year, local.Month));
    }

    /// <summary>
    /// Today plus a fortnight, and the open task list, written out small enough to sit in the
    /// system prompt. Ids are included deliberately: with them a request like "move my 3pm to 4"
    /// is one call, and without them it is a lookup, a wait, and then the call.
    /// </summary>
    private static string BuildScheduleDigest(AssistantRequest request)
    {
        const int horizonDays = 14;
        const int maximumEvents = 60;
        const int maximumTasks = 20;

        var today = DateOnly.FromDateTime(request.Now.LocalDateTime);
        var last = today.AddDays(horizonDays);
        var builder = new StringBuilder();

        var upcoming = request.Events
            .Select(item => (Event: item, Start: item.StartUtc.ToLocalTime(), End: item.EndUtc.ToLocalTime()))
            .Where(item => DateOnly.FromDateTime(item.Start.Date) >= today && DateOnly.FromDateTime(item.Start.Date) <= last)
            .OrderBy(item => item.Start)
            .Take(maximumEvents)
            .ToList();

        if (upcoming.Count == 0)
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"Events {today:yyyy-MM-dd} to {last:yyyy-MM-dd}: none. The calendar is completely clear.");
        }
        else
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"Events {today:yyyy-MM-dd} to {last:yyyy-MM-dd}:");
            DateOnly? heading = null;
            foreach (var item in upcoming)
            {
                var day = DateOnly.FromDateTime(item.Start.Date);
                if (heading != day)
                {
                    heading = day;
                    var label = day == today ? " (today)" : day == today.AddDays(1) ? " (tomorrow)" : string.Empty;
                    builder.AppendLine(CultureInfo.InvariantCulture,
                        $"  {day:yyyy-MM-dd} {day.DayOfWeek}{label}:");
                }
                builder.AppendLine(CultureInfo.InvariantCulture,
                    $"    {item.Start:HH:mm}-{item.End:HH:mm} {item.Event.Title} [{item.Event.Category}] id={item.Event.Id}");
            }
            if (request.Events.Count > upcoming.Count)
                builder.AppendLine("  (list truncated — call get_schedule for the rest)");
        }

        var open = request.Tasks
            .Where(task => task.Status is not Domain.TaskStatus.Done and not Domain.TaskStatus.Cancelled)
            .OrderByDescending(task => task.Priority)
            .ThenBy(task => task.DeadlineUtc ?? DateTimeOffset.MaxValue)
            .Take(maximumTasks)
            .ToList();

        if (open.Count == 0)
        {
            builder.AppendLine("Unscheduled tasks: none.");
        }
        else
        {
            builder.AppendLine("Unscheduled tasks (no time booked for these):");
            foreach (var task in open)
            {
                var deadline = task.DeadlineUtc is { } due
                    ? $", due {due.ToLocalTime():yyyy-MM-dd HH:mm}"
                    : string.Empty;
                builder.AppendLine(CultureInfo.InvariantCulture,
                    $"    {task.Title} — {task.RemainingDurationMin}min, priority {task.Priority}{deadline} id={task.Id}");
            }
        }

        return builder.ToString();
    }

    /// <summary>Compact snapshot handed to the legacy MCP bridge, which has no tool loop.</summary>
    private static string BuildContextJson(AssistantRequest request) => JsonSerializer.Serialize(new
    {
        request.Prompt,
        now = request.Now,
        request.TimeZoneId,
        accessLevel = request.AccessLevel.ToString(),
        conversation = request.Conversation?.Select(turn => new { turn.Role, turn.Content }),
        memories = request.Memories?.Select(memory => new { memory.Key, memory.Value, category = memory.Category.ToString() }),
        tasks = request.Tasks.Take(100).Select(task => new
        {
            task.Id, task.Title, task.Status, task.Priority, task.RemainingDurationMin, task.DeadlineUtc, task.EarliestStartUtc,
            preferredTime = task.PreferredTimeOfDay.ToString()
        }),
        events = request.Events.Take(150).Select(item => new
        {
            item.Id, item.Title, item.StartUtc, item.EndUtc,
            category = item.Category.ToString(), priority = item.Priority.ToString(),
            recurrence = item.Recurrence.ToString(), item.SeriesId, item.IsRecurrenceException
        })
    });

    // ---------------------------------------------------------------- attachments

    private static async Task<PreparedAttachment> PrepareAttachmentAsync(AssistantAttachment attachment,
        VisionSupport vision, CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(attachment.FilePath);
        if (!File.Exists(path)) throw new FileNotFoundException("The attached file is no longer available.", path);
        var info = new FileInfo(path);
        if (info.Length > AttachmentSupport.MaximumBytes)
            throw new InvalidOperationException(
                $"Attachments are limited to {AttachmentSupport.MaximumBytes / (1024 * 1024)} MB.");

        var kind = AttachmentSupport.Classify(path);
        if (kind == AttachmentKind.Image)
        {
            if (!vision.IsEnabled) throw new InvalidOperationException(vision.Reason);
            var (bytes, mime) = await EncodeImageAsync(path, cancellationToken);
            return new(attachment.FileName, null, $"data:{mime};base64,{Convert.ToBase64String(bytes)}");
        }

        if (kind == AttachmentKind.Unsupported)
            throw new InvalidOperationException(
                "Supported attachments: PNG, JPEG, WebP, BMP, GIF images, plus PDF, DOCX and text files.");

        var extension = Path.GetExtension(path).ToLowerInvariant();
        string text;
        if (extension == ".docx") text = ExtractDocx(path);
        else if (extension == ".pdf") text = ExtractPdf(path, cancellationToken);
        else text = await File.ReadAllTextAsync(path, cancellationToken);
        text = text.Trim();
        if (text.Length == 0) throw new InvalidOperationException("No readable text was found in the attached document.");
        if (text.Length > MaxExtractedCharacters) text = text[..MaxExtractedCharacters] + "\n[Document truncated]";
        return new(attachment.FileName, text, null);
    }

    /// <summary>
    /// Decodes whatever the user picked and re-encodes it to something every endpoint accepts.
    ///
    /// Sending the original file was the root of most "image input does not work": a phone
    /// photo is several megabytes, base64 inflates it by a third, and endpoints answer 413 or
    /// simply time out. Formats such as BMP and GIF were rejected outright even though the
    /// pixels are perfectly ordinary. Both problems disappear once the bytes are normalised.
    /// </summary>
    private static async Task<(byte[] Bytes, string Mime)> EncodeImageAsync(string path, CancellationToken cancellationToken)
    {
        var original = await File.ReadAllBytesAsync(path, cancellationToken);
        try
        {
            using var source = SKBitmap.Decode(original);
            if (source is null)
                throw new InvalidOperationException("That image could not be read. Try saving it as PNG or JPEG.");

            var longest = Math.Max(source.Width, source.Height);
            var scale = longest > AttachmentSupport.MaximumImageEdge
                ? AttachmentSupport.MaximumImageEdge / (double)longest
                : 1d;

            using var scaled = scale >= 1d
                ? source.Copy()
                : source.Resize(
                    new SKImageInfo(Math.Max(1, (int)Math.Round(source.Width * scale)),
                        Math.Max(1, (int)Math.Round(source.Height * scale))),
                    new SKSamplingOptions(SKCubicResampler.Mitchell));

            if (scaled is null)
                throw new InvalidOperationException("That image could not be resized. Try saving it as PNG or JPEG.");

            using var image = SKImage.FromBitmap(scaled);
            // JPEG at 88 is visually indistinguishable here and a fraction of the size; images
            // that need their alpha channel keep it by going out as PNG instead.
            var hasAlpha = scaled.AlphaType is not SKAlphaType.Opaque;
            using var encoded = image.Encode(hasAlpha ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg, 88);
            if (encoded is null)
                throw new InvalidOperationException("That image could not be encoded. Try saving it as PNG or JPEG.");
            return (encoded.ToArray(), hasAlpha ? "image/png" : "image/jpeg");
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"That image could not be prepared: {exception.Message}");
        }
    }

    /// <summary>Whether images may be sent at all, and what to tell the user when they may not.</summary>
    private readonly record struct VisionSupport(bool IsEnabled, string Reason)
    {
        public static VisionSupport For(AppSettings settings) => settings.AssistantProvider switch
        {
            AssistantProvider.Local when !settings.LocalModelVisionEnabled => new(false,
                "Turn on image input for the local model in settings, then download its vision projector."),
            AssistantProvider.Local when !MayUseProjector(settings) => new(false,
                LocalModelCatalog.IsCustom(settings.LocalModelId)
                    ? "This GGUF has no projector attached. Pick its mmproj file in settings to send images."
                    : "This model ships no vision projector. Choose a model marked for image input instead."),
            AssistantProvider.Mcp => new(false,
                "The MCP bridge has no way to carry an image. Switch to a local model or an API connection to send one."),
            _ => new(true, string.Empty)
        };
    }

    private static string ExtractDocx(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry("word/document.xml") ?? throw new InvalidDataException("The DOCX document has no main content.");
        using var stream = entry.Open();
        var document = XDocument.Load(stream);
        XNamespace word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        return string.Join("\n", document.Descendants(word + "p")
            .Select(paragraph => string.Concat(paragraph.Descendants(word + "t").Select(node => node.Value)))
            .Where(line => !string.IsNullOrWhiteSpace(line)));
    }

    private static string ExtractPdf(string path, CancellationToken cancellationToken)
    {
        using var document = PdfDocument.Open(path);
        var result = new StringBuilder();
        foreach (var page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.AppendLine(page.Text);
            if (result.Length >= MaxExtractedCharacters) break;
        }
        return result.ToString();
    }

    private sealed record PreparedAttachment(string FileName, string? ExtractedText, string? ImageDataUrl);

    // ---------------------------------------------------------------- legacy JSON envelope

    /// <summary>
    /// Fallback for models that cannot call tools. Every read is defensive: a model that puts a
    /// number where the schema asked for text used to throw straight out of the request, and the
    /// blanket catch upstream turned that into a bogus "connection lost".
    /// </summary>
    public static AssistantReply ParseReply(string raw, string provider)
    {
        raw = (raw ?? string.Empty).Trim();
        if (raw.Length == 0) return new AssistantReply("The assistant returned an empty response.", [], provider, TimeSpan.Zero, IsError: true);

        var candidate = ExtractJsonObject(raw);
        if (candidate is null) return new AssistantReply(raw, [], provider, TimeSpan.Zero);

        try
        {
            using var json = JsonDocument.Parse(candidate);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new AssistantReply(raw, [], provider, TimeSpan.Zero);

            var message = AssistantToolJson.String(root, "message");
            var suggestions = new List<AssistantTaskSuggestion>();
            if (root.TryGetProperty("tasks", out var tasksNode) && tasksNode.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in tasksNode.EnumerateArray())
                {
                    var title = AssistantToolJson.String(item, "title");
                    if (string.IsNullOrWhiteSpace(title)) continue;
                    suggestions.Add(new(title.Trim(),
                        AssistantToolJson.Int(item, "durationMinutes", 5, 720) ?? 30,
                        AssistantToolJson.Int(item, "priority", 0, 100) ?? 50,
                        AssistantToolJson.Moment(item, "deadlineLocal"),
                        AssistantToolJson.Enum<PreferredTimeOfDay>(item, "preferredTime") ?? PreferredTimeOfDay.Any,
                        AssistantToolJson.String(item, "reason")));
                }
            }

            var actions = new List<AssistantScheduleAction>();
            if (root.TryGetProperty("actions", out var actionsNode) && actionsNode.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in actionsNode.EnumerateArray())
                {
                    if (!TryParseActionKind(AssistantToolJson.String(item, "kind"), out var kind)) continue;
                    actions.Add(new(kind,
                        AssistantToolJson.Id(item, "targetId"),
                        AssistantToolJson.String(item, "title")?.Trim(),
                        AssistantToolJson.Moment(item, "startLocal"),
                        AssistantToolJson.Moment(item, "endLocal"),
                        AssistantToolJson.Int(item, "durationMinutes", 1, 10_080),
                        AssistantToolJson.Int(item, "priority", 0, 100),
                        AssistantToolJson.Enum<EventCategory>(item, "category"),
                        AssistantToolJson.Date(item, "sourceDate"),
                        AssistantToolJson.Date(item, "targetDate"),
                        AssistantToolJson.Int(item, "offsetMinutes", -43_200, 43_200),
                        AssistantToolJson.String(item, "reason"),
                        AssistantToolJson.Enum<PreferredTimeOfDay>(item, "preferredTime"),
                        AssistantToolJson.Moment(item, "deadlineLocal")));
                }
            }

            // A bare JSON blob with nothing recognisable in it is more useful shown as text.
            if (string.IsNullOrWhiteSpace(message) && suggestions.Count == 0 && actions.Count == 0)
                return new AssistantReply(raw, [], provider, TimeSpan.Zero);

            return new AssistantReply(
                string.IsNullOrWhiteSpace(message) ? "Here is a suggestion." : message,
                suggestions, provider, TimeSpan.Zero, actions);
        }
        catch (JsonException)
        {
            return new AssistantReply(raw, [], provider, TimeSpan.Zero);
        }
    }

    /// <summary>Pulls the JSON envelope out of a reply that may be fenced or wrapped in prose.</summary>
    internal static string? ExtractJsonObject(string raw)
    {
        if (raw.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = raw.IndexOf('\n');
            var closing = raw.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLine >= 0 && closing > firstLine) raw = raw[(firstLine + 1)..closing].Trim();
        }
        if (raw.StartsWith('{') && raw.EndsWith('}')) return raw;

        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        var candidate = raw[start..(end + 1)];
        // Only treat it as the envelope if it actually looks like one.
        return candidate.Contains("\"message\"", StringComparison.Ordinal)
               || candidate.Contains("\"actions\"", StringComparison.Ordinal)
               || candidate.Contains("\"tasks\"", StringComparison.Ordinal)
            ? candidate
            : null;
    }

    private static bool TryParseActionKind(string? value, out AssistantActionKind kind)
    {
        var normalized = value?.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        return Enum.TryParse(normalized, true, out kind);
    }

    private static bool LooksLikeTaskCapture(string prompt)
    {
        var value = prompt.TrimStart();
        return new[] { "add ", "create ", "remember ", "task ", "todo " }
            .Any(prefix => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static string StripCaptureVerb(string prompt)
    {
        var firstSpace = prompt.IndexOf(' ');
        return firstSpace >= 0 && firstSpace < prompt.Length - 1 ? prompt[(firstSpace + 1)..].Trim() : prompt.Trim();
    }

    // ---------------------------------------------------------------- plumbing

    private void Report(AssistantProgressStage stage, string? detail = null)
        => ProgressChanged?.Invoke(this, new AssistantProgress(stage, detail));

    private void AddAuthorization(HttpRequestMessage message, AppSettings settings)
    {
        var key = ReadApiKey(settings);
        if (!string.IsNullOrWhiteSpace(key)) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    private string ReadApiKey(AppSettings settings) => string.IsNullOrWhiteSpace(settings.AssistantApiKeyCiphertext)
        ? Environment.GetEnvironmentVariable("INTELLIGENT_SCHEDULER_API_KEY") ?? string.Empty
        : secrets.Unprotect(settings.AssistantApiKeyCiphertext);

    internal static string CombineEndpoint(string endpoint, string path)
    {
        endpoint = (endpoint ?? string.Empty).Trim().TrimEnd('/');
        if (endpoint.EndsWith(path, StringComparison.OrdinalIgnoreCase)) return endpoint;
        if (path.StartsWith("/v1/", StringComparison.OrdinalIgnoreCase) && endpoint.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            return endpoint + path[3..];
        return endpoint + path;
    }

    private static string ProviderLabel(AppSettings settings) => settings.AssistantProvider switch
    {
        AssistantProvider.Local => "The local model",
        AssistantProvider.Ollama => "Ollama",
        AssistantProvider.OpenAiCompatible => "The API",
        AssistantProvider.Mcp => "The MCP server",
        _ => "The assistant"
    };

    private static string DescribeFailure(AppSettings settings, Exception exception) => exception switch
    {
        AssistantProviderException => exception.Message,
        InvalidOperationException => $"{ProviderLabel(settings)} could not handle the request: {exception.Message}",
        JsonException => $"{ProviderLabel(settings)} returned a response this app could not read: {exception.Message}",
        _ => $"{ProviderLabel(settings)} failed: {exception.Message}"
    };

    /// <summary>Pulls the human-readable part out of a provider error body.</summary>
    internal static string ExtractError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "(empty response body)";
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("error", out var error))
                {
                    if (error.ValueKind == JsonValueKind.String) return error.GetString() ?? body;
                    var text = AssistantToolJson.String(error, "message");
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }
                var direct = AssistantToolJson.String(root, "message") ?? AssistantToolJson.String(root, "detail");
                if (!string.IsNullOrWhiteSpace(direct)) return direct;
            }
        }
        catch (JsonException) { /* not JSON — show the raw text below */ }
        return Snippet(body, 400);
    }

    private static string Snippet(string? value, int length = 240)
    {
        value = (value ?? string.Empty).Trim();
        if (value.Length <= length) return value;
        return value[..length] + "…";
    }

    private static string FriendlyError(Exception exception) => exception switch
    {
        HttpRequestException => $"Connection failed: {exception.Message}",
        TaskCanceledException => "Connection timed out.",
        _ => exception.Message
    };

    public void ClearConversation()
    {
        lock (_conversationGate) _conversation.Clear();
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>The endpoint could not be reached at all, so an offline fallback is reasonable.</summary>
public sealed class AssistantTransportException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The endpoint answered, but refused or malformed the request. Never hidden from the user.</summary>
public sealed class AssistantProviderException(string message) : Exception(message);
