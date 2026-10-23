using IntelligentScheduler.Domain;
using Xunit;

namespace IntelligentScheduler.Infrastructure.Tests;

public sealed class ChatStoreTests
{
    [Fact]
    public async Task Conversation_KeepsOrderPreviewAndCount()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        var conversation = await harness.Chat.CreateConversationAsync(string.Empty, cancellationToken);

        await harness.Chat.AppendMessageAsync(new ChatMessage
        {
            ConversationId = conversation.Id, Role = ChatRole.User, Content = "Первый вопрос\nвторая строка"
        }, cancellationToken);
        await harness.Chat.AppendMessageAsync(new ChatMessage
        {
            ConversationId = conversation.Id, Role = ChatRole.Assistant, Content = "Ответ"
        }, cancellationToken);

        var messages = await harness.Chat.GetMessagesAsync(conversation.Id, cancellationToken);
        Assert.Equal(2, messages.Count);
        Assert.Equal(0, messages[0].Ordinal);
        Assert.Equal(1, messages[1].Ordinal);

        var reloaded = Assert.Single(await harness.Chat.GetConversationsAsync(cancellationToken: cancellationToken));
        Assert.Equal(2, reloaded.MessageCount);
        Assert.Equal("Ответ", reloaded.Preview);
        // An untitled thread takes its name from the first user line, flattened.
        Assert.Equal("Первый вопрос вторая строка", reloaded.Title);
    }

    [Fact]
    public async Task PinnedConversationsSortAboveRecentOnes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        var older = await harness.Chat.CreateConversationAsync("Старый", cancellationToken);
        await harness.Chat.CreateConversationAsync("Новый", cancellationToken);
        await harness.Chat.SetConversationPinnedAsync(older.Id, true, cancellationToken);

        var conversations = await harness.Chat.GetConversationsAsync(cancellationToken: cancellationToken);

        Assert.Equal("Старый", conversations[0].Title);
        Assert.True(conversations[0].IsPinned);
    }

    [Fact]
    public async Task DeletingAConversationRemovesItsMessages()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        var conversation = await harness.Chat.CreateConversationAsync("Тест", cancellationToken);
        await harness.Chat.AppendMessageAsync(new ChatMessage
        {
            ConversationId = conversation.Id, Role = ChatRole.User, Content = "привет"
        }, cancellationToken);

        await harness.Chat.DeleteConversationAsync(conversation.Id, cancellationToken);

        Assert.Empty(await harness.Chat.GetConversationsAsync(cancellationToken: cancellationToken));
        Assert.Empty(await harness.Chat.GetMessagesAsync(conversation.Id, cancellationToken));
    }

    [Fact]
    public async Task SearchFindsMessagesAcrossThreads()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        var first = await harness.Chat.CreateConversationAsync("A", cancellationToken);
        var second = await harness.Chat.CreateConversationAsync("B", cancellationToken);
        await harness.Chat.AppendMessageAsync(new ChatMessage { ConversationId = first.Id, Role = ChatRole.User, Content = "план на неделю" }, cancellationToken);
        await harness.Chat.AppendMessageAsync(new ChatMessage { ConversationId = second.Id, Role = ChatRole.User, Content = "что по спорту" }, cancellationToken);

        var found = await harness.Chat.SearchMessagesAsync("план", cancellationToken: cancellationToken);

        Assert.Equal("план на неделю", Assert.Single(found).Content);
    }

    [Fact]
    public async Task MemoryUpsertReplacesTheValueForTheSameKey()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);

        await harness.Chat.UpsertAsync(new UserMemoryEntry { Key = "sleep", Value = "Ложится в 23:00" }, cancellationToken);
        var updated = await harness.Chat.UpsertAsync(new UserMemoryEntry
        {
            Key = "sleep", Value = "Ложится в 00:30", Category = UserMemoryCategory.Routine
        }, cancellationToken);

        var stored = Assert.Single(await harness.Chat.GetAllAsync(cancellationToken));
        Assert.Equal(updated.Id, stored.Id);
        Assert.Equal("Ложится в 00:30", stored.Value);
        Assert.Equal(UserMemoryCategory.Routine, stored.Category);
    }

    [Fact]
    public async Task DisabledMemoriesStayStoredButAreMarked()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = await AssistantTestHarness.CreateAsync(cancellationToken);
        var entry = await harness.Chat.UpsertAsync(new UserMemoryEntry { Key = "k", Value = "v" }, cancellationToken);

        await harness.Chat.SetEnabledAsync(entry.Id, false, cancellationToken);

        Assert.False(Assert.Single(await harness.Chat.GetAllAsync(cancellationToken)).IsEnabled);
    }
}
