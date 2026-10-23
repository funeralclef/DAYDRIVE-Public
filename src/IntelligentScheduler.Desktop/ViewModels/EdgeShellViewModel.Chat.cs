using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IntelligentScheduler.Application;
using IntelligentScheduler.Domain;
using IntelligentScheduler.Infrastructure;
using DomainTaskStatus = IntelligentScheduler.Domain.TaskStatus;

namespace IntelligentScheduler.Desktop.ViewModels;

/// <summary>
/// Everything that makes the assistant panel a real chat: saved threads, a memory drawer,
/// suggestions built from the user's own history, and live progress while a turn runs.
/// </summary>
public sealed partial class EdgeShellViewModel
{
    private bool _chatInitialized;
    private bool _progressAttached;

    [ObservableProperty] private string _assistantStage = string.Empty;
    [ObservableProperty] private Guid? _activeConversationId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowChatMain))]
    [NotifyPropertyChangedFor(nameof(ShowChatIntro))]
    private bool _isChatHistoryOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowChatMain))]
    [NotifyPropertyChangedFor(nameof(ShowChatIntro))]
    private bool _isMemoryPanelOpen;

    [ObservableProperty] private string _chatSearchQuery = string.Empty;
    [ObservableProperty] private string _newMemoryKey = string.Empty;
    [ObservableProperty] private string _newMemoryValue = string.Empty;
    [ObservableProperty] private bool _isMemoryClearPending;
    [ObservableProperty] private string _activeConversationTitle = string.Empty;

    public ObservableCollection<ChatConversationViewModel> Conversations { get; } = [];
    public ObservableCollection<UserMemoryViewModel> Memories { get; } = [];
    public ObservableCollection<ChatSuggestionViewModel> DynamicSuggestions { get; } = [];

    public bool ShowChatMain => !IsChatHistoryOpen && !IsMemoryPanelOpen && !IsWrapUpOpen;

    /// <summary>Whether the thread has anything in it yet.</summary>
    public bool HasConversationTurns => Messages.Count > 0;

    /// <summary>
    /// How big the words of a turn are set. Bigger in the widened panel, because that panel is
    /// the one somebody reads a paragraph in: 12.5 is a size for a column three hundred pixels
    /// wide, and holding it at a measure twice that makes the text look like a caption for
    /// something else on the page. It is a number rather than a style because the two things
    /// that carry it — the Markdown renderer and the question above it — build their own text.
    /// </summary>
    public double ChatBodyFontSize => (IsChatExpanded ? 13.5 : 12.5) * ThemeTextScale / 100d;
    public double ChatBodyLineHeight => 19.5 * ThemeTextScale / 100d;

    /// <summary>
    /// The opening screen, shown in place of the thread rather than as the first line of it.
    ///
    /// A new conversation used to begin with a message from the assistant saying what it was
    /// for — a turn nobody said, sitting in the transcript for the rest of the thread and
    /// scrolling away with it. The same words work better as a page: they are the whole of what
    /// is on screen before the first question, they can sit in the middle of it with the
    /// suggestions under them, and they leave the moment there is a real turn to show.
    /// </summary>
    public bool ShowChatIntro => ShowChatMain && !HasConversationTurns;
    public bool HasConversations => Conversations.Count > 0;
    public bool HasMemories => Memories.Count > 0;
    /// <summary>How many notes there are, stamped on the drawer so the list has a known end.</summary>
    public string MemoryCountText => Memories.Count.ToString(localization.CurrentCulture);
    public bool HasDynamicSuggestions => DynamicSuggestions.Count > 0 && !IsBusy;
    public bool HasStage => !string.IsNullOrWhiteSpace(AssistantStage);

    /// <summary>Raised when a message should be placed on the clipboard; the view owns that API.</summary>
    public event EventHandler<string>? CopyRequested;

    partial void OnAssistantStageChanged(string value) => OnPropertyChanged(nameof(HasStage));

    // ---------------------------------------------------------------- lifecycle

    private async Task InitializeChatAsync()
    {
        if (_chatInitialized) return;
        _chatInitialized = true;

        if (!_progressAttached && assistant is AssistantService concrete)
        {
            concrete.ProgressChanged += OnAssistantProgress;
            concrete.ReplyStreaming += OnAssistantStreaming;
            _progressAttached = true;
        }

        Conversations.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasConversations));
        Memories.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasMemories));
            OnPropertyChanged(nameof(MemoryCountText));
        };
        DynamicSuggestions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasDynamicSuggestions));
        Messages.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasConversationTurns));
            OnPropertyChanged(nameof(ShowChatIntro));
        };

        try
        {
            await RefreshConversationsAsync();
            await RefreshMemoriesAsync();

            var restore = _settings?.ActiveConversationId;
            if (restore is { } id && Conversations.Any(item => item.Id == id)) await LoadConversationAsync(id);
            else ShowIntro();
        }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Restoring chat history");
            ShowIntro();
        }
    }

    private void OnAssistantProgress(object? sender, AssistantProgress progress)
        => Dispatcher.UIThread.Post(() => AssistantStage = DescribeStage(progress));

    /// <summary>The bubble being written into, while it is being written into.</summary>
    private AssistantMessageViewModel? _streamingMessage;

    /// <summary>
    /// Shows the answer as it is generated rather than after it. The reply itself takes the
    /// same time either way; what changes is that the wait stops being a blank panel.
    /// </summary>
    private void OnAssistantStreaming(object? sender, AssistantStreamUpdate update)
        => Dispatcher.UIThread.Post(() =>
        {
            // A stream that outlives its request belongs to nothing on screen.
            if (!IsBusy) return;
            if (_streamingMessage is null)
            {
                _streamingMessage = new AssistantMessageViewModel(update.Text, false, copy: CopyMessageAsync)
                {
                    IsStreaming = true
                };
                Messages.Add(_streamingMessage);
            }
            else _streamingMessage.Text = update.Text;
            AssistantStage = localization.Text("StageWriting");
        });

    /// <summary>Takes the half-written bubble off the panel when a turn ends without an answer.</summary>
    private void DiscardStreamingMessage()
    {
        if (_streamingMessage is { } message) Messages.Remove(message);
        _streamingMessage = null;
    }

    private string DescribeStage(AssistantProgress progress) => progress.Stage switch
    {
        AssistantProgressStage.Connecting => localization.Text("StageConnecting"),
        AssistantProgressStage.Loading => localization.Text("StageLoadingModel"),
        AssistantProgressStage.Writing => localization.Text("StageWriting"),
        AssistantProgressStage.Done => string.Empty,
        AssistantProgressStage.UsingTool => localization.Text(progress.Detail switch
        {
            "get_schedule" => "StageToolSchedule",
            "get_tasks" => "StageToolTasks",
            "search_schedule" => "StageToolSearch",
            "find_free_slots" => "StageToolSlots",
            "get_analytics" => "StageToolAnalytics",
            "remember_about_user" or "forget_about_user" or "list_memories" => "StageToolMemory",
            not null when progress.Detail.StartsWith("mcp_", StringComparison.Ordinal) => "StageToolExternal",
            _ => "StageToolWrite"
        }),
        _ => localization.Text("StageThinking")
    };

    private void ShowIntro()
    {
        // Nothing is added. An empty thread is what puts the opening screen on the panel; see
        // ShowChatIntro for why the welcome is a page rather than a turn.
        Messages.Clear();
        AssistantState = localization.Text("AssistantPrivacy");
        ActiveConversationTitle = localization.Text("ChatNewChat");
        _ = RefreshSuggestionsAsync();
    }

    // ---------------------------------------------------------------- messages

    private AssistantMessageViewModel CreateUserMessage(string text, string? attachmentName)
        => new(text, true, attachmentName: attachmentName, copy: CopyMessageAsync,
            retry: RetryMessageAsync, edit: EditMessageAsync);

    private AssistantMessageViewModel CreateAssistantMessage(AssistantReply reply)
        => new(reply.Message, false,
            $"{reply.Provider} · {reply.Elapsed.TotalSeconds:0.0} {localization.Text("SecondsShort")}",
            isError: reply.IsError,
            trace: reply.Trace.Select(entry => new AssistantToolTraceViewModel(entry, localization)),
            copy: CopyMessageAsync);

    private AssistantMessageViewModel Restore(ChatMessage message)
    {
        if (message.Role == ChatRole.User)
            return new(message.Content, true, timestamp: message.CreatedAtUtc,
                attachmentName: message.AttachmentName, copy: CopyMessageAsync,
                retry: RetryMessageAsync, edit: EditMessageAsync);

        var trace = new List<AssistantToolTraceViewModel>();
        if (!string.IsNullOrWhiteSpace(message.ToolTraceJson))
        {
            try
            {
                var entries = System.Text.Json.JsonSerializer.Deserialize<List<AssistantToolTraceEntry>>(
                    message.ToolTraceJson, AssistantToolJson.Options) ?? [];
                trace.AddRange(entries.Select(entry => new AssistantToolTraceViewModel(entry, localization)));
            }
            catch (System.Text.Json.JsonException exception)
            {
                // A stored trace from an older build is not worth failing the restore over.
                crashReporter.Record(exception, "Reading a stored tool trace");
            }
        }

        return new(message.Content, false, message.Meta, message.CreatedAtUtc, message.IsError,
            trace: trace, copy: CopyMessageAsync);
    }

    private Task CopyMessageAsync(AssistantMessageViewModel message)
    {
        CopyRequested?.Invoke(this, message.Text);
        message.MarkCopied();
        Status = localization.Text("ChatCopied");
        return Task.CompletedTask;
    }

    private async Task RetryMessageAsync(AssistantMessageViewModel message)
    {
        if (IsBusy) return;
        AssistantInput = message.Text;
        await AskAssistantAsync();
    }

    /// <summary>
    /// The same question in different words. It goes down as a new turn rather than replacing
    /// the one it was rewritten from: the assistant answered what it was actually asked, and a
    /// thread that quietly rewrites its own history cannot be read back afterwards.
    /// </summary>
    private async Task EditMessageAsync(AssistantMessageViewModel message, string edited)
    {
        if (IsBusy || edited.Length == 0) return;
        AssistantInput = edited;
        await AskAssistantAsync();
    }

    // ---------------------------------------------------------------- conversations

    [RelayCommand]
    private async Task NewChatAsync()
    {
        assistant.ClearConversation();
        ActiveConversationId = null;
        Suggestions.Clear();
        AssistantActions.Clear();
        IsChatHistoryOpen = false;
        IsMemoryPanelOpen = false;
        ShowIntro();
        foreach (var item in Conversations) item.IsSelected = false;
        await PersistActiveConversationAsync();
    }

    [RelayCommand]
    private void ToggleChatHistory()
    {
        IsMemoryPanelOpen = false;
        IsWrapUpOpen = false;
        IsChatHistoryOpen = !IsChatHistoryOpen;
        if (IsChatHistoryOpen) _ = RefreshConversationsAsync();
    }

    [RelayCommand]
    private void ToggleMemoryPanel()
    {
        IsChatHistoryOpen = false;
        IsWrapUpOpen = false;
        IsMemoryPanelOpen = !IsMemoryPanelOpen;
        if (IsMemoryPanelOpen) _ = RefreshMemoriesAsync();
    }

    [RelayCommand] private void CloseChatDrawers() { IsChatHistoryOpen = false; IsMemoryPanelOpen = false; IsWrapUpOpen = false; }

    partial void OnChatSearchQueryChanged(string value) => _ = RefreshConversationsAsync();

    public async Task RefreshConversationsAsync()
    {
        try
        {
            var stored = await chatRepository.GetConversationsAsync();
            var query = ChatSearchQuery.Trim();
            if (query.Length > 0)
            {
                var matchingIds = (await chatRepository.SearchMessagesAsync(query, 200))
                    .Select(message => message.ConversationId).ToHashSet();
                stored = stored.Where(conversation =>
                        matchingIds.Contains(conversation.Id) ||
                        conversation.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            Conversations.Clear();
            foreach (var conversation in stored)
                Conversations.Add(new(conversation, localization,
                    item => LoadConversationAsync(item.Id),
                    DeleteConversationAsync,
                    TogglePinConversationAsync,
                    (item, title) => chatRepository.RenameConversationAsync(item.Id, title))
                {
                    IsSelected = conversation.Id == ActiveConversationId
                });

            if (Conversations.FirstOrDefault(item => item.Id == ActiveConversationId) is { } active)
                ActiveConversationTitle = active.Title;
        }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Loading conversations");
        }
    }

    private async Task LoadConversationAsync(Guid id)
    {
        try
        {
            var stored = await chatRepository.GetMessagesAsync(id);
            ActiveConversationId = id;
            assistant.ClearConversation();
            Messages.Clear();
            Suggestions.Clear();
            AssistantActions.Clear();
            foreach (var message in stored) Messages.Add(Restore(message));
            if (Messages.Count == 0) Messages.Add(new(localization.Text("AssistantIntro"), false, localization.Text("AssistantPrivacy")));

            foreach (var item in Conversations) item.IsSelected = item.Id == id;
            ActiveConversationTitle = Conversations.FirstOrDefault(item => item.Id == id)?.Title
                                     ?? localization.Text("ChatNewChat");
            IsChatHistoryOpen = false;
            AssistantState = localization.Text("ReplyReady");
            await PersistActiveConversationAsync();
            await RefreshSuggestionsAsync();
        }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Opening a conversation");
            Status = exception.Message;
        }
    }

    private async Task DeleteConversationAsync(ChatConversationViewModel item)
    {
        await chatRepository.DeleteConversationAsync(item.Id);
        if (item.Id == ActiveConversationId) await NewChatAsync();
        await RefreshConversationsAsync();
    }

    private async Task TogglePinConversationAsync(ChatConversationViewModel item)
    {
        item.IsPinned = !item.IsPinned;
        await chatRepository.SetConversationPinnedAsync(item.Id, item.IsPinned);
        await RefreshConversationsAsync();
    }

    private async Task PersistActiveConversationAsync()
    {
        if (_settings is null) return;
        _settings.ActiveConversationId = ActiveConversationId;
        await SaveSettingsCoreAsync(includeApiKey: false);
    }

    // ---------------------------------------------------------------- memory

    public async Task RefreshMemoriesAsync()
    {
        try
        {
            var stored = await memoryRepository.GetAllAsync();
            Memories.Clear();
            foreach (var entry in stored)
                Memories.Add(new(entry, localization, DeleteMemoryAsync,
                    (item, enabled) => memoryRepository.SetEnabledAsync(item.Id, enabled)));
        }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Loading assistant memory");
        }
    }

    [RelayCommand]
    private async Task AddMemoryAsync()
    {
        var key = NewMemoryKey.Trim();
        var value = NewMemoryValue.Trim();
        if (key.Length == 0 || value.Length == 0) return;
        try
        {
            await memoryRepository.UpsertAsync(new UserMemoryEntry
            {
                Key = key,
                Value = value,
                IsFromAssistant = false
            });
            NewMemoryKey = string.Empty;
            NewMemoryValue = string.Empty;
            await RefreshMemoriesAsync();
        }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Saving a memory");
            Status = exception.Message;
        }
    }

    private async Task DeleteMemoryAsync(UserMemoryViewModel item)
    {
        await memoryRepository.DeleteAsync(item.Id);
        await RefreshMemoriesAsync();
    }

    [RelayCommand] private void RequestClearMemory() => IsMemoryClearPending = true;
    [RelayCommand] private void CancelClearMemory() => IsMemoryClearPending = false;

    [RelayCommand]
    private async Task ConfirmClearMemoryAsync()
    {
        IsMemoryClearPending = false;
        await memoryRepository.ClearAsync();
        await RefreshMemoriesAsync();
    }

    // ---------------------------------------------------------------- tool catalogue

    public ObservableCollection<AssistantToolProbeViewModel> AvailableTools { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAvailableTools))]
    private bool _areToolsVisible;

    public bool HasAvailableTools => AvailableTools.Count > 0;

    public string ToolsToggleText => localization.Text(AreToolsVisible ? "HideTools" : "ShowTools");

    partial void OnAreToolsVisibleChanged(bool value) => OnPropertyChanged(nameof(ToolsToggleText));

    /// <summary>
    /// Lists exactly what the model would be handed, built-ins and MCP together. This is the
    /// hand-check for an MCP setup: if a server's tools do not appear here, they are not
    /// reaching the model either. Pressing again hides the list.
    /// </summary>
    [RelayCommand]
    private async Task ToggleToolsVisibilityAsync()
    {
        AreToolsVisible = !AreToolsVisible;
        if (AreToolsVisible) await LoadAvailableToolsAsync();
    }

    private async Task LoadAvailableToolsAsync()
    {
        try
        {
            var probe = await assistant.ProbeToolsAsync();
            AvailableTools.Clear();
            foreach (var tool in probe.Tools ?? []) AvailableTools.Add(new(tool, localization));
            OnPropertyChanged(nameof(HasAvailableTools));
            if (!probe.IsSuccess) Status = probe.Message;
        }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Listing assistant tools");
            Status = exception.Message;
        }
    }

    // ---------------------------------------------------------------- suggestions

    /// <summary>
    /// Builds follow-up prompts from what is actually true right now: today's load, overdue
    /// tasks and the topics the user raised recently. No model call, so it costs nothing and
    /// works offline.
    /// </summary>
    public async Task RefreshSuggestionsAsync()
    {
        try
        {
            var suggestions = new List<(string Glyph, string Text)>();
            var now = DateTimeOffset.Now;
            var today = DateOnly.FromDateTime(now.LocalDateTime);
            var (dayStart, dayEnd) = DayRange(today);

            var todayEvents = await calendarRepository.GetRangeAsync(dayStart, dayEnd);
            var busyMinutes = todayEvents.Sum(item =>
            {
                var start = item.StartUtc > dayStart ? item.StartUtc : dayStart;
                var end = item.EndUtc < dayEnd ? item.EndUtc : dayEnd;
                return end > start ? (end - start).TotalMinutes : 0;
            });

            var allTasks = await taskRepository.GetAllAsync();
            var overdue = ScheduleAnalytics.OverdueTaskCount(allTasks, now);

            if (todayEvents.Count == 0) suggestions.Add(("IconSparkle", localization.Text("SuggestEmptyDay")));
            else if (busyMinutes >= 6 * 60) suggestions.Add(("IconClock", localization.Text("SuggestBusyDay")));

            if (overdue > 0) suggestions.Add(("IconTune", localization.Text("SuggestOverdue")));

            // A short recent question is usually still relevant and cheap to resume.
            var recent = await chatRepository.GetRecentUserMessagesAsync(12);
            var followUp = recent
                .Select(message => message.Content.Trim())
                .FirstOrDefault(text => text.Length is > 12 and < 70);
            if (followUp is not null) suggestions.Add(("IconChat", localization.Format("SuggestFollowUp", followUp)));

            suggestions.Add(("IconCalendar", localization.Text("SuggestPlanTomorrow")));
            suggestions.Add(("IconClock", localization.Text("SuggestFreeTime")));
            suggestions.Add(("IconTune", localization.Text("SuggestBalance")));
            if (recent.Count >= 4) suggestions.Add(("IconSparkle", localization.Text("SuggestReview")));

            DynamicSuggestions.Clear();
            foreach (var (glyph, text) in suggestions.Take(4))
                DynamicSuggestions.Add(new(text, glyph, SendQuickPromptAsync));
        }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Building chat suggestions");
        }
    }
}
