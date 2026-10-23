using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IntelligentScheduler.Application;
using IntelligentScheduler.Desktop.Localization;
using IntelligentScheduler.Domain;

namespace IntelligentScheduler.Desktop.ViewModels;

/// <summary>One line describing a tool the assistant used, shown folded under its reply.</summary>
public sealed class AssistantToolTraceViewModel(AssistantToolTraceEntry entry, LocalizationService localization)
{
    public string Name { get; } = entry.Name;
    public string Detail { get; } = entry.Summary;
    public bool IsExternal { get; } = entry.IsExternal;
    public bool IsError { get; } = entry.IsError;
    public string StateText { get; } = localization.Text(
        entry.IsError ? "ChatToolFailed"
        : entry.Applied ? "ChatToolApplied"
        : entry.IsProposal ? "ChatToolProposed"
        : "ChatToolRead");
    public bool IsApplied { get; } = entry.Applied && !entry.IsError;
}

public sealed partial class AssistantMessageViewModel : ObservableObject
{
    private readonly Func<AssistantMessageViewModel, Task>? _copy;
    private readonly Func<AssistantMessageViewModel, Task>? _retry;
    private readonly Func<AssistantMessageViewModel, string, Task>? _edit;

    public AssistantMessageViewModel(
        string text,
        bool fromUser,
        string? meta = null,
        DateTimeOffset? timestamp = null,
        bool isError = false,
        string? attachmentName = null,
        IEnumerable<AssistantToolTraceViewModel>? trace = null,
        Func<AssistantMessageViewModel, Task>? copy = null,
        Func<AssistantMessageViewModel, Task>? retry = null,
        Func<AssistantMessageViewModel, string, Task>? edit = null)
    {
        Text = text;
        FromUser = fromUser;
        Meta = meta;
        Timestamp = timestamp ?? DateTimeOffset.Now;
        IsError = isError;
        AttachmentName = attachmentName;
        _copy = copy;
        _retry = retry;
        _edit = edit;
        _draft = text;
        if (trace is not null) foreach (var item in trace) Trace.Add(item);
    }

    /// <summary>
    /// The message body, which for an answer being written is whatever has arrived so far.
    /// The bubble is put up on the first few characters and grows in place from there.
    /// </summary>
    [ObservableProperty] private string _text;

    public bool FromUser { get; }
    public bool FromAssistant => !FromUser;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMeta))]
    private string? _meta;

    public DateTimeOffset Timestamp { get; }

    [ObservableProperty] private bool _isError;

    /// <summary>True while the answer is still arriving.</summary>
    [ObservableProperty] private bool _isStreaming;

    public string? AttachmentName { get; }
    public bool HasAttachment => !string.IsNullOrWhiteSpace(AttachmentName);
    public ObservableCollection<AssistantToolTraceViewModel> Trace { get; } = [];
    public bool HasTrace => Trace.Count > 0;
    public bool HasMeta => !string.IsNullOrWhiteSpace(Meta);

    /// <summary>
    /// Turns the bubble that was being written into the finished answer: the exact final text,
    /// the provider stamp, and whatever the assistant did along the way. Replacing the bubble
    /// instead would make the reply appear to arrive twice.
    /// </summary>
    public void Complete(string text, string? meta, bool isError, IEnumerable<AssistantToolTraceViewModel> trace)
    {
        Text = text;
        Meta = meta;
        IsError = isError;
        Trace.Clear();
        foreach (var item in trace) Trace.Add(item);
        OnPropertyChanged(nameof(HasTrace));
        IsStreaming = false;
    }
    public bool CanRetry => FromUser && _retry is not null;
    /// <summary>
    /// A question can be asked again in different words. Rewriting one is not a correction of
    /// the record — what was asked was asked — so the edited wording is put as a fresh turn
    /// underneath, exactly as retyping it by hand would be, only without the retyping.
    /// </summary>
    public bool CanEdit => FromUser && _edit is not null;
    public string TimeText => Timestamp.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TraceGlyph))]
    private bool _isTraceExpanded;

    [ObservableProperty] private bool _wasCopied;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsMessageBody))]
    private bool _isEditing;

    /// <summary>The wording being worked on, which is the original until it is changed.</summary>
    [ObservableProperty] private string _draft;

    /// <summary>False only while the bubble has been handed over to the editor.</summary>
    public bool ShowsMessageBody => !IsEditing;

    public string TraceGlyph => IsTraceExpanded ? "⌃" : "⌄";

    [RelayCommand] private void ToggleTrace() => IsTraceExpanded = !IsTraceExpanded;
    [RelayCommand] private Task CopyAsync() => _copy?.Invoke(this) ?? Task.CompletedTask;
    [RelayCommand] private Task RetryAsync() => _retry?.Invoke(this) ?? Task.CompletedTask;

    [RelayCommand]
    private void BeginEdit()
    {
        if (!CanEdit) return;
        Draft = Text;
        IsEditing = true;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        Draft = Text;
        IsEditing = false;
    }

    [RelayCommand]
    private async Task CommitEditAsync()
    {
        var edited = Draft.Trim();
        IsEditing = false;
        if (edited.Length == 0 || _edit is null) { Draft = Text; return; }
        Draft = Text;
        await _edit(this, edited);
    }

    public void MarkCopied() => WasCopied = true;
}

/// <summary>A saved thread in the history drawer.</summary>
public sealed partial class ChatConversationViewModel : ObservableObject
{
    private readonly Func<ChatConversationViewModel, Task> _open;
    private readonly Func<ChatConversationViewModel, Task> _delete;
    private readonly Func<ChatConversationViewModel, Task> _togglePin;
    private readonly Func<ChatConversationViewModel, string, Task> _rename;
    private readonly LocalizationService _localization;

    public ChatConversationViewModel(
        ChatConversation conversation,
        LocalizationService localization,
        Func<ChatConversationViewModel, Task> open,
        Func<ChatConversationViewModel, Task> delete,
        Func<ChatConversationViewModel, Task> togglePin,
        Func<ChatConversationViewModel, string, Task> rename)
    {
        Conversation = conversation;
        _localization = localization;
        _open = open;
        _delete = delete;
        _togglePin = togglePin;
        _rename = rename;
        _title = string.IsNullOrWhiteSpace(conversation.Title)
            ? localization.Text("ChatNewChat")
            : conversation.Title;
        _isPinned = conversation.IsPinned;
        _draftTitle = _title;
    }

    public ChatConversation Conversation { get; }
    public Guid Id => Conversation.Id;
    public string Preview => Conversation.Preview;
    public bool HasPreview => !string.IsNullOrWhiteSpace(Preview);

    [ObservableProperty] private string _title;
    [ObservableProperty] private string _draftTitle;
    [ObservableProperty] private bool _isPinned;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isRenaming;
    [ObservableProperty] private bool _isDeletePending;

    public string MetaText => _localization.Format("ChatMessageCount", Conversation.MessageCount);

    /// <summary>Relative day bucket used to group the list ("Today", "Yesterday", …).</summary>
    public string Group
    {
        get
        {
            var days = (DateTime.Today - Conversation.UpdatedAtUtc.ToLocalTime().Date).Days;
            return _localization.Text(days switch
            {
                <= 0 => "ChatGroupToday",
                1 => "ChatGroupYesterday",
                <= 7 => "ChatGroupWeek",
                _ => "ChatGroupOlder"
            });
        }
    }

    public string PinTooltip => _localization.Text(IsPinned ? "ChatUnpin" : "ChatPin");
    partial void OnIsPinnedChanged(bool value) => OnPropertyChanged(nameof(PinTooltip));

    [RelayCommand] private Task OpenAsync() => _open(this);
    [RelayCommand] private Task TogglePinAsync() => _togglePin(this);
    [RelayCommand] private void RequestDelete() => IsDeletePending = true;
    [RelayCommand] private void CancelDelete() => IsDeletePending = false;
    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        IsDeletePending = false;
        await _delete(this);
    }

    [RelayCommand]
    private void BeginRename()
    {
        DraftTitle = Title;
        IsRenaming = true;
    }

    [RelayCommand] private void CancelRename() => IsRenaming = false;

    [RelayCommand]
    private async Task CommitRenameAsync()
    {
        IsRenaming = false;
        var value = DraftTitle.Trim();
        if (value.Length == 0 || value == Title) return;
        Title = value;
        await _rename(this, value);
    }
}

/// <summary>A durable note the assistant keeps about the user.</summary>
public sealed partial class UserMemoryViewModel : ObservableObject
{
    private readonly Func<UserMemoryViewModel, Task> _delete;
    private readonly Func<UserMemoryViewModel, bool, Task> _setEnabled;
    private readonly LocalizationService _localization;
    private bool _suppress;

    public UserMemoryViewModel(
        UserMemoryEntry entry,
        LocalizationService localization,
        Func<UserMemoryViewModel, Task> delete,
        Func<UserMemoryViewModel, bool, Task> setEnabled)
    {
        Entry = entry;
        _localization = localization;
        _delete = delete;
        _setEnabled = setEnabled;
        _suppress = true;
        _isEnabled = entry.IsEnabled;
        _suppress = false;
    }

    public UserMemoryEntry Entry { get; }
    public Guid Id => Entry.Id;
    public string Key => Entry.Key;
    public string Value => Entry.Value;
    public string Category => _localization.Text($"MemoryCategory{Entry.Category}");
    public string Origin => _localization.Text(Entry.IsFromAssistant ? "ChatMemoryFromAssistant" : "ChatMemoryFromUser");

    [ObservableProperty] private bool _isEnabled;

    partial void OnIsEnabledChanged(bool value)
    {
        if (_suppress) return;
        _ = _setEnabled(this, value);
    }

    [RelayCommand] private Task DeleteAsync() => _delete(this);
}

/// <summary>A one-tap prompt built from the user's own history and schedule.</summary>
public sealed partial class ChatSuggestionViewModel(string text, string glyph, Func<string, Task> send) : ObservableObject
{
    public string Text { get; } = text;
    public string Glyph { get; } = glyph;
    [RelayCommand] private Task SendAsync() => send(Text);
}

/// <summary>One tool the current configuration exposes, listed by the settings probe.</summary>
public sealed class AssistantToolProbeViewModel(AssistantToolProbe probe, LocalizationService localization)
{
    public string Name { get; } = probe.Name;
    public string Description { get; } = probe.Description;
    public string Badge { get; } = probe.IsExternal ? "MCP" : localization.Text("ToolBuiltIn");
    public bool IsExternal { get; } = probe.IsExternal;
}
