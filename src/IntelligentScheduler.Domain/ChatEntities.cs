namespace IntelligentScheduler.Domain;

/// <summary>
/// A persisted assistant conversation. Every chat the user opens keeps its own
/// history so the panel can restore, rename and delete past threads.
/// </summary>
public sealed class ChatConversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    /// <summary>Short preview of the last exchange, used by the history list.</summary>
    public string Preview { get; set; } = string.Empty;
    public int MessageCount { get; set; }
    public bool IsPinned { get; set; }
    public bool IsArchived { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ChatMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversationId { get; set; }
    public ChatRole Role { get; set; } = ChatRole.User;
    public string Content { get; set; } = string.Empty;
    /// <summary>Provider label and timing shown under an assistant bubble.</summary>
    public string? Meta { get; set; }
    /// <summary>Name of the attachment the user sent with this message, when any.</summary>
    public string? AttachmentName { get; set; }
    /// <summary>Serialized tool activity so a restored thread still shows what the assistant did.</summary>
    public string? ToolTraceJson { get; set; }
    public bool IsError { get; set; }
    public int Ordinal { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A durable fact the user allowed the assistant to keep between conversations.
/// Stored locally next to the schedule; never uploaded on its own.
/// </summary>
public sealed class UserMemoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public UserMemoryCategory Category { get; set; } = UserMemoryCategory.General;
    /// <summary>True when the assistant wrote it; false when the user typed it in the memory editor.</summary>
    public bool IsFromAssistant { get; set; } = true;
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Key)) throw new ArgumentException("A memory needs a short key.");
        if (string.IsNullOrWhiteSpace(Value)) throw new ArgumentException("A memory needs a value.");
    }
}
