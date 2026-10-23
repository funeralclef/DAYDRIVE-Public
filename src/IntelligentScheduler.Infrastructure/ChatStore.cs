using IntelligentScheduler.Application;
using IntelligentScheduler.Domain;
using Microsoft.EntityFrameworkCore;

namespace IntelligentScheduler.Infrastructure;

/// <summary>
/// Local persistence for assistant conversations and the durable notes the assistant
/// keeps about the user. Everything stays in the same SQLite file as the schedule.
/// </summary>
public sealed class ChatStore(IDbContextFactory<AppDbContext> factory) : IChatRepository, IUserMemoryRepository
{
    private const int PreviewLength = 220;

    public async Task<IReadOnlyList<ChatConversation>> GetConversationsAsync(
        bool includeArchived = false, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var query = db.ChatConversations.AsNoTracking();
        if (!includeArchived) query = query.Where(conversation => !conversation.IsArchived);
        return await query
            .OrderByDescending(conversation => conversation.IsPinned)
            .ThenByDescending(conversation => conversation.UpdatedAtUtc)
            .Take(200)
            .ToListAsync(cancellationToken);
    }

    public async Task<ChatConversation?> GetConversationAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.ChatConversations.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
    }

    public async Task<ChatConversation> CreateConversationAsync(string title, CancellationToken cancellationToken = default)
    {
        var conversation = new ChatConversation { Title = Trim(title, 200) };
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.ChatConversations.Add(conversation);
        await db.SaveChangesAsync(cancellationToken);
        return conversation;
    }

    public async Task RenameConversationAsync(Guid id, string title, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var conversation = await db.ChatConversations.FindAsync([id], cancellationToken);
        if (conversation is null) return;
        conversation.Title = Trim(title, 200);
        conversation.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetConversationPinnedAsync(Guid id, bool pinned, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var conversation = await db.ChatConversations.FindAsync([id], cancellationToken);
        if (conversation is null) return;
        conversation.IsPinned = pinned;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteConversationAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        // The cascade is declared on the model, but deleting explicitly keeps
        // databases created by older builds consistent as well.
        await db.ChatMessages.Where(message => message.ConversationId == id).ExecuteDeleteAsync(cancellationToken);
        await db.ChatConversations.Where(conversation => conversation.Id == id).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task DeleteAllConversationsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.ChatMessages.ExecuteDeleteAsync(cancellationToken);
        await db.ChatConversations.ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.ChatMessages.AsNoTracking()
            .Where(message => message.ConversationId == conversationId)
            .OrderBy(message => message.Ordinal).ThenBy(message => message.CreatedAtUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task AppendMessageAsync(ChatMessage message, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var conversation = await db.ChatConversations.FindAsync([message.ConversationId], cancellationToken)
            ?? throw new InvalidOperationException("The conversation no longer exists.");

        var lastOrdinal = await db.ChatMessages
            .Where(item => item.ConversationId == message.ConversationId)
            .MaxAsync(item => (int?)item.Ordinal, cancellationToken) ?? -1;
        message.Ordinal = lastOrdinal + 1;
        db.ChatMessages.Add(message);

        conversation.MessageCount = lastOrdinal + 2;
        conversation.UpdatedAtUtc = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(message.Content)) conversation.Preview = Trim(Flatten(message.Content), PreviewLength);
        // The first user line names the thread until the caller renames it.
        if (message.Role == ChatRole.User && string.IsNullOrWhiteSpace(conversation.Title))
            conversation.Title = Trim(Flatten(message.Content), 60);

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ChatMessage>> SearchMessagesAsync(string query, int limit = 40, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var pattern = $"%{query.Trim()}%";
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.ChatMessages.AsNoTracking()
            .Where(message => EF.Functions.Like(message.Content, pattern))
            .OrderByDescending(message => message.CreatedAtUtc)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ChatMessage>> GetRecentUserMessagesAsync(int limit, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.ChatMessages.AsNoTracking()
            .Where(message => message.Role == ChatRole.User)
            .OrderByDescending(message => message.CreatedAtUtc)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<UserMemoryEntry>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.UserMemoryEntries.AsNoTracking()
            .OrderBy(entry => entry.Category).ThenBy(entry => entry.Key)
            .ToListAsync(cancellationToken);
    }

    public async Task<UserMemoryEntry> UpsertAsync(UserMemoryEntry entry, CancellationToken cancellationToken = default)
    {
        entry.Key = Trim(entry.Key, 120);
        entry.Value = Trim(entry.Value, 1000);
        entry.Validate();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var existing = await db.UserMemoryEntries.FirstOrDefaultAsync(item => item.Key == entry.Key, cancellationToken);
        if (existing is null)
        {
            db.UserMemoryEntries.Add(entry);
            await db.SaveChangesAsync(cancellationToken);
            return entry;
        }

        existing.Value = entry.Value;
        existing.Category = entry.Category;
        existing.IsFromAssistant = entry.IsFromAssistant;
        existing.IsEnabled = entry.IsEnabled;
        existing.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return existing;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.UserMemoryEntries.Where(entry => entry.Id == id).ExecuteDeleteAsync(cancellationToken) > 0;
    }

    public async Task<bool> DeleteByKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        var normalized = key.Trim();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.UserMemoryEntries.Where(entry => entry.Key == normalized).ExecuteDeleteAsync(cancellationToken) > 0;
    }

    public async Task SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var entry = await db.UserMemoryEntries.FindAsync([id], cancellationToken);
        if (entry is null) return;
        entry.IsEnabled = enabled;
        entry.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.UserMemoryEntries.ExecuteDeleteAsync(cancellationToken);
    }

    private static string Flatten(string value)
        => string.Join(' ', value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string Trim(string value, int length)
    {
        value = value.Trim();
        return value.Length <= length ? value : value[..length];
    }
}
