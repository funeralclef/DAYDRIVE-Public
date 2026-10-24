using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using IntelligentScheduler.Desktop.Platform;

namespace IntelligentScheduler.Desktop.ViewModels;

/// <summary>
/// Reminder state shared by the event card and the day list. An automatic notification shows
/// only the event card; the regular widget hides its event timer until the reminder has faded.
/// The window chooses where to present the reminder and whether to close after it expires.
/// </summary>
public sealed partial class EdgeShellViewModel
{
    /// <summary>How long an event stays marked after a reminder about it arrived.</summary>
    private static readonly TimeSpan FlashLife = TimeSpan.FromSeconds(6);

    private NotificationToastViewModel? _notifications;
    /// <summary>The event a reminder has just been raised about, and until when it is marked.</summary>
    private Guid? _flashedEventId;
    private DateTimeOffset _flashUntilUtc;
    private DispatcherTimer? _flashTimer;

    /// <summary>
    /// The reminders currently in the widget. Built on first use rather than in a field
    /// initialiser: it needs the shell's own clock formatter, and a field initialiser has no
    /// <c>this</c> to take it from.
    /// </summary>
    public NotificationToastViewModel Notifications
    {
        get
        {
            if (_notifications is not null) return _notifications;
            _notifications = new NotificationToastViewModel(moment => FormatClockTime(moment.TimeOfDay));
            _notifications.PropertyChanged += (_, args) =>
            {
                // Two moments, not one. Whether the card is wearing a reminder is one question;
                // whether the reminder is still drawn on it is another, and the answer to the
                // second outlives the answer to the first by the length of the fade.
                if (args.PropertyName == nameof(NotificationToastViewModel.IsPresenting))
                    OnPropertyChanged(nameof(HasPeekNotifications));
                else if (args.PropertyName == nameof(NotificationToastViewModel.Current))
                    RefreshReminderShape();
            };
            return _notifications;
        }
    }

    /// <summary>
    /// The widget is carrying only a notification card. Cleared when the user opens the widget,
    /// so the ordinary clock, event controls and next event can appear.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPeekClockBlock))]
    [NotifyPropertyChangedFor(nameof(ShowPeekTrackingRail))]
    [NotifyPropertyChangedFor(nameof(ShowPeekEventTimer))]
    [NotifyPropertyChangedFor(nameof(ShowPeekNextBlock))]
    private bool _isPeekCompact;

    /// <summary>Whether the event card is currently wearing a reminder.</summary>
    public bool HasPeekNotifications => _notifications?.IsPresenting == true;

    /// <summary>
    /// Whether a reminder is still drawn on the card — which stays true for the length of the
    /// fade after the answer above has turned false. Everything the card actually *says* hangs
    /// off this rather than off the wearing, because text swapped back on the first frame of a
    /// fade is text that was never faded.
    /// </summary>
    public bool IsPeekReminderDrawn => _notifications?.Current is not null;

    /// <summary>
    /// The reminder is about the very event the card is describing, which is the ordinary case:
    /// something starts in five minutes and the card is already the thing that says what it is.
    /// The card keeps its own lines, and only the eyebrow above them changes.
    /// </summary>
    public bool IsPeekReminderMine
        => IsPeekReminderDrawn && HasPeekEvent
           && _notifications!.Current!.EventId is { } id && id == PeekEventId;

    /// <summary>
    /// A reminder about something else — a different day is open, or the card is describing the
    /// event that is running while the reminder is about the one after it. Nothing on the card
    /// belongs to it, so for as long as it lasts the reminder is the card.
    /// </summary>
    public bool IsPeekReminderSolo => IsPeekReminderDrawn && !IsPeekReminderMine;

    /// <summary>What the card says above its title: the reminder, for as long as there is one.</summary>
    public string PeekEventEyebrow => _notifications?.Current?.Title ?? NextLabel;

    /// <summary>The reminder's own sentence, for when the card has no lines of its own to wear it.</summary>
    public string PeekReminderBody => _notifications?.Current?.Body ?? string.Empty;

    /// <summary>
    /// The event block exists for the event, and also for a reminder about something the widget
    /// was not otherwise describing — which still has to be said somewhere.
    /// </summary>
    public bool ShowPeekEventBlock => IsPeekReminderDrawn || HasPeekEvent;

    /// <summary>
    /// The card's own lines step aside for a reminder about a different event, so the
    /// title and tracking state always belong to the event named by the reminder.
    /// </summary>
    public bool ShowPeekEventLines => HasPeekEvent && !IsPeekReminderSolo;

    /// <summary>
    /// What the compact shape does without. The clock, what comes next and the outcome rail all
    /// describe the day rather than the reminder, so they wait until the user asks for the
    /// widget itself.
    /// </summary>
    public bool ShowPeekClockBlock => !IsPeekCompact;
    public bool ShowPeekTrackingRail => HasPeekEvent && !IsPeekCompact;

    /// <summary>
    /// Keep the event timer hidden for the entire notification, including its exit animation.
    /// A compact notification never includes a timer, even between presentation states.
    /// </summary>
    public bool ShowPeekEventTimer => HasPeekTimer && !IsPeekReminderDrawn && !IsPeekCompact;
    public bool ShowPeekNextBlock => HasPeekNext && !IsPeekCompact;

    /// <summary>
    /// The one question, asked on the widget too. Stopping a session from the rail leaves the
    /// event unanswered for, and the answer belongs where the stopwatch was - not two panels
    /// away behind a reveal the user did not ask for.
    /// </summary>
    public bool ShowPeekSessionPrompt => IsSessionReviewOpen && HasPeekEvent && !IsPeekCompact
                                         && PeekEventId == SessionReviewEventId;

    /// <summary>
    /// The gentle strip for a session that has stood still too long. It stands down while the
    /// verdict question is up, because the two ask about the same session and only one of them
    /// can be answered at a time.
    /// </summary>
    public bool ShowPeekPausePrompt => IsLongPause && HasPeekEvent && !IsPeekCompact && !IsSessionReviewOpen;

    /// <summary>Everything about the card that a change of reminder can change.</summary>
    private void RefreshReminderShape()
    {
        OnPropertyChanged(nameof(IsPeekReminderDrawn));
        OnPropertyChanged(nameof(IsPeekReminderMine));
        OnPropertyChanged(nameof(IsPeekReminderSolo));
        OnPropertyChanged(nameof(ShowPeekEventBlock));
        OnPropertyChanged(nameof(ShowPeekEventLines));
        OnPropertyChanged(nameof(ShowPeekEventTimer));
        OnPropertyChanged(nameof(ShowPeekSessionPrompt));
        OnPropertyChanged(nameof(ShowPeekPausePrompt));
        OnPropertyChanged(nameof(PeekEventEyebrow));
        OnPropertyChanged(nameof(PeekReminderBody));
    }

    /// <summary>
    /// Marks one event in the day for a few seconds: the light touch a reminder gets when the
    /// shell is open and a card would only be repeating what is already on the screen.
    ///
    /// The mark is kept here rather than only on the row, because the day list is rebuilt from
    /// the database every minute and a mark that lived on the row alone would be swept away by
    /// the first rebuild after it appeared.
    /// </summary>
    public bool FlashEvent(Guid? eventId)
    {
        if (eventId is not { } id || Agenda.All(row => row.Event?.Id != id)) return false;
        _flashedEventId = id;
        _flashUntilUtc = DateTimeOffset.UtcNow + FlashLife;
        ApplyEventFlash();

        _flashTimer?.Stop();
        _flashTimer ??= new DispatcherTimer();
        _flashTimer.Interval = FlashLife;
        _flashTimer.Tick -= OnFlashElapsed;
        _flashTimer.Tick += OnFlashElapsed;
        _flashTimer.Start();
        return true;
    }

    /// <summary>
    /// A reminder arrived while the shell was open. It is delivered as the mark on the event
    /// rather than as a card, because a card here would stand on the same edge as the shell —
    /// and would in any case be naming an event the user is already looking at.
    ///
    /// When the reminder is about something the shown day does not contain — a different day is
    /// open, or the list has moved on — there is nothing to mark, so it is said in the one place
    /// the shell says things without opening anything.
    /// </summary>
    public void MarkReminder(AppNotification notification)
    {
        if (FlashEvent(notification.EventId)) return;
        Status = notification.Body;
    }

    private void OnFlashElapsed(object? sender, EventArgs args)
    {
        _flashTimer?.Stop();
        _flashedEventId = null;
        ApplyEventFlash();
    }

    /// <summary>
    /// Puts the mark on whichever row currently draws the marked event, and takes it off every
    /// other. Called after each rebuild of the day so a row that was replaced mid-flash keeps
    /// the mark its predecessor had.
    /// </summary>
    private void ApplyEventFlash()
    {
        var marked = _flashedEventId is { } id && DateTimeOffset.UtcNow < _flashUntilUtc ? id : (Guid?)null;
        if (marked is null) _flashedEventId = null;
        foreach (var row in Agenda)
            row.IsFlashing = marked is { } value && row.Event?.Id == value;
    }
}
