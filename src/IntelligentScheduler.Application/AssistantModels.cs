using IntelligentScheduler.Domain;

namespace IntelligentScheduler.Application;

public sealed record AssistantRequest(
    string Prompt,
    DateTimeOffset Now,
    string TimeZoneId,
    IReadOnlyList<TaskItem> Tasks,
    IReadOnlyList<CalendarEvent> Events,
    AssistantAttachment? Attachment = null,
    IReadOnlyList<AssistantConversationTurn>? Conversation = null,
    AssistantAccessLevel AccessLevel = AssistantAccessLevel.ConfirmAll,
    IReadOnlyList<UserMemoryEntry>? Memories = null,
    string? LanguageCode = null);

public sealed record AssistantConversationTurn(string Role, string Content);

public sealed record AssistantAttachment(string FilePath, string FileName);

/// <summary>A single user turn together with the thread it belongs to.</summary>
public sealed record AssistantAsk(
    string Prompt,
    Guid? ConversationId = null,
    AssistantAttachment? Attachment = null);

public sealed record AssistantTaskSuggestion(
    string Title,
    int DurationMinutes = 30,
    int Priority = 50,
    DateTimeOffset? DeadlineLocal = null,
    PreferredTimeOfDay PreferredTime = PreferredTimeOfDay.Any,
    string? Reason = null);

public enum AssistantActionKind
{
    CreateEvent,
    UpdateEvent,
    DeleteEvent,
    CopyEvent,
    CreateTask,
    UpdateTask,
    DeleteTask,
    ShiftDay,
    CopyDay,
    /// <summary>Moves a task through its lifecycle: started, finished, dropped.</summary>
    SetTaskStatus,
    /// <summary>Reserves working time for an existing task without turning it into an event.</summary>
    CreateFocusBlock
}

public sealed record AssistantScheduleAction(
    AssistantActionKind Kind,
    Guid? TargetId = null,
    string? Title = null,
    DateTimeOffset? StartLocal = null,
    DateTimeOffset? EndLocal = null,
    int? DurationMinutes = null,
    int? Priority = null,
    EventCategory? Category = null,
    DateOnly? SourceDate = null,
    DateOnly? TargetDate = null,
    int? OffsetMinutes = null,
    string? Reason = null,
    PreferredTimeOfDay? PreferredTime = null,
    DateTimeOffset? DeadlineLocal = null,
    /// <summary>Only read for <see cref="AssistantActionKind.SetTaskStatus"/>.</summary>
    Domain.TaskStatus? TaskStatus = null)
{
    public bool IsCritical => Kind is AssistantActionKind.DeleteEvent or AssistantActionKind.DeleteTask
        or AssistantActionKind.ShiftDay or AssistantActionKind.CopyDay;
}

public sealed record AssistantReply(
    string Message,
    IReadOnlyList<AssistantTaskSuggestion> Suggestions,
    string Provider,
    TimeSpan Elapsed,
    IReadOnlyList<AssistantScheduleAction>? Actions = null,
    IReadOnlyList<AssistantToolTraceEntry>? ToolTrace = null,
    /// <summary>True when the reply describes a failure rather than an answer.</summary>
    bool IsError = false,
    /// <summary>Conversation the reply was stored in, so the caller can keep the thread selected.</summary>
    Guid? ConversationId = null)
{
    public IReadOnlyList<AssistantScheduleAction> ScheduleActions => Actions ?? [];
    public IReadOnlyList<AssistantToolTraceEntry> Trace => ToolTrace ?? [];
}

public sealed record AssistantToolProbe(string Name, string Description, bool IsExternal);

public sealed record AssistantConnectionResult(
    bool IsSuccess,
    string Message,
    IReadOnlyList<AssistantToolProbe>? Tools = null);

public interface IAssistantService
{
    Task<AssistantReply> AskAsync(string prompt, CancellationToken cancellationToken = default);
    Task<AssistantReply> AskAsync(string prompt, AssistantAttachment attachment, CancellationToken cancellationToken = default);
    Task<AssistantReply> AskAsync(AssistantAsk ask, CancellationToken cancellationToken = default);
    Task<AssistantConnectionResult> TestConnectionAsync(CancellationToken cancellationToken = default);
    /// <summary>Lists the tools the currently configured setup would expose to the model.</summary>
    Task<AssistantConnectionResult> ProbeToolsAsync(CancellationToken cancellationToken = default);
    void ClearConversation();
}

public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string ciphertext);
}
