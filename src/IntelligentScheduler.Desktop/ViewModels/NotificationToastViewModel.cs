using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IntelligentScheduler.Desktop.Platform;

namespace IntelligentScheduler.Desktop.ViewModels;

/// <summary>One reminder, while the widget's event block is carrying it.</summary>
public sealed partial class NotificationToastItem
{
    private readonly Action<NotificationToastItem> _dismiss;

    public NotificationToastItem(AppNotification notification, string timeText, TimeSpan life,
        Action<NotificationToastItem> dismiss)
    {
        Title = notification.Title;
        Body = notification.Body;
        TimeText = timeText;
        EventId = notification.EventId;
        Life = life;
        ExpiresAtUtc = DateTimeOffset.UtcNow + life;
        _dismiss = dismiss;
    }

    public string Title { get; }
    public string Body { get; }
    public string TimeText { get; }
    /// <summary>The event this is about, so pressing the card can open the day it belongs to.</summary>
    public Guid? EventId { get; }
    /// <summary>How long this card gets on screen while nothing is holding it.</summary>
    public TimeSpan Life { get; }
    public DateTimeOffset ExpiresAtUtc { get; set; }

    [RelayCommand]
    private void Dismiss() => _dismiss(this);
}

/// <summary>
/// The reminders currently in the widget.
///
/// Reminders used to have a window of their own, hanging off whatever else was on that edge of
/// the screen. Two translucent surfaces on one edge is not a card in front of a card — it is
/// two backgrounds stacked — and the placement had to keep measuring the shell and the widget
/// to stay out from under them. They are part of the widget now, and not as a block of their
/// own either: the widget already has a surface that describes the event that is on, and a
/// reminder is more of the same subject rather than a different one. So the event block
/// <em>grows</em> around it and folds back afterwards, and the widget the user opened keeps
/// every other block it had while that happens.
///
/// It holds at most a handful but presents only the newest: the expansion is one place, and
/// when the newest reminder goes the one under it takes that place over without the block
/// changing size. Items lapse on a single timer rather than one apiece, and the timer stops
/// dead when the list empties — an idle shell must not be waking up to check whether nothing
/// has expired.
/// </summary>
public sealed partial class NotificationToastViewModel : ObservableObject
{
    private const int MaxVisible = 3;
    private static readonly TimeSpan Life = TimeSpan.FromSeconds(11);

    private readonly Func<DateTimeOffset, string> _formatTime;
    private readonly DispatcherTimer _expiry;

    public NotificationToastViewModel(Func<DateTimeOffset, string> formatTime)
    {
        _formatTime = formatTime;
        // Also the step the countdown ring moves in. A quarter second is short enough that the
        // transition on it reads as a hand going round rather than as ticking, and the timer
        // only runs at all while a reminder is on screen.
        _expiry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _expiry.Tick += (_, _) => Tick();
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasAny));
    }

    public ObservableCollection<NotificationToastItem> Items { get; } = [];

    /// <summary>Whether any reminder is still owed a place in the widget.</summary>
    public bool HasAny => Items.Count > 0;

    /// <summary>
    /// The reminder the expansion is drawing. It deliberately outlives the queue: the block
    /// folds back over real frames, and the text has to still be in it while it does.
    /// </summary>
    [ObservableProperty] private NotificationToastItem? _current;

    /// <summary>
    /// Whether the event block is expanded around a reminder. This — not <see cref="Current"/> —
    /// is what the widget follows, so that clearing the last card and folding the block away are
    /// two separate moments rather than one.
    /// </summary>
    [ObservableProperty] private bool _isPresenting;

    /// <summary>
    /// Bumped every time a different reminder takes the expansion over. The widget watches it to
    /// replay the arrival on the contents alone, which is what a second reminder landing on top
    /// of the first looks like: the block stays the size it is and only what it says changes.
    /// </summary>
    [ObservableProperty] private int _sequence;

    /// <summary>How much of the current reminder's time is left, 0..100.</summary>
    [ObservableProperty] private double _remainingPercent = 100;

    /// <summary>Raised when the last card goes, so a widget opened for it can leave.</summary>
    public event EventHandler? Emptied;

    /// <summary>Raised when a card is pressed: the shell is what the reminder is about.</summary>
    public event EventHandler<NotificationToastItem>? Opened;

    /// <summary>
    /// Set while the pointer rests on the widget: nothing lapses under the pointer. The widget
    /// as a whole sets this, not the cards, so reading the countdown holds the reminder too.
    /// </summary>
    [ObservableProperty] private bool _isHeld;

    public void Post(AppNotification notification)
    {
        var item = new NotificationToastItem(notification, _formatTime(notification.RaisedAt), Life, Remove);
        Items.Insert(0, item);
        while (Items.Count > MaxVisible) Items.RemoveAt(Items.Count - 1);
        Present(item);
        _expiry.Start();
    }

    /// <summary>A card was pressed. The reminder has been read; the shell takes it from here.</summary>
    public void Open(NotificationToastItem item)
    {
        Opened?.Invoke(this, item);
        Remove(item);
    }

    /// <summary>
    /// Every reminder still queued goes, because the user has answered them: they pressed
    /// something inside the widget, or asked for the widget itself. A reminder exists to be
    /// noticed, and the moment it has been there is nothing left for its clock to run out for -
    /// so the card lets go of the news on the same press that starts a timer or opens the day,
    /// rather than going on wearing it for the rest of its eleven seconds.
    ///
    /// <see cref="Current"/> is deliberately left standing: the card lets go of it over real
    /// frames, and the text has to still be in it while it does.
    /// </summary>
    public void DismissAll()
    {
        if (Items.Count == 0) return;
        Items.Clear();
        Settle();
    }

    /// <summary>
    /// Folds the expansion away while leaving the reminder drawn in it. Called when the widget
    /// is the user's own and is staying: the block returns to describing the event, and only the
    /// part that was added for the reminder goes.
    /// </summary>
    public void EndPresentation()
    {
        // A newer reminder in the gap between the queue emptying and this call keeps the block
        // open — there is something to show in it again.
        if (Items.Count > 0) return;
        IsPresenting = false;
    }

    /// <summary>
    /// Drops the reminder the block was drawing, now that nothing is drawing it: the fold has
    /// finished, or the whole widget went off the screen carrying it.
    /// </summary>
    public void ReleasePresentation()
    {
        if (Items.Count > 0)
        {
            Present(Items[0]);
            return;
        }
        IsPresenting = false;
        Current = null;
        RemainingPercent = 0;
    }

    /// <summary>Hands the expansion to a reminder and starts its clock over.</summary>
    private void Present(NotificationToastItem item)
    {
        Current = item;
        RemainingPercent = 100;
        IsPresenting = true;
        Sequence++;
    }

    private void Remove(NotificationToastItem item)
    {
        if (!Items.Remove(item)) return;
        if (Items.Count > 0)
        {
            if (!ReferenceEquals(Current, Items[0])) Present(Items[0]);
            return;
        }
        Settle();
    }

    private void Tick()
    {
        var now = DateTimeOffset.UtcNow;
        if (IsHeld)
        {
            // Held cards do not age. Their clock restarts when the pointer leaves, so a card
            // read halfway through does not vanish the moment the pointer moves off it.
            foreach (var item in Items) item.ExpiresAtUtc = now + item.Life;
            RefreshRemaining(now);
            return;
        }

        for (var index = Items.Count - 1; index >= 0; index--)
            if (Items[index].ExpiresAtUtc <= now) Items.RemoveAt(index);

        if (Items.Count > 0)
        {
            // Removing straight off the collection skips Remove, so the hand-over to whatever is
            // underneath is done here as well.
            if (!ReferenceEquals(Current, Items[0])) Present(Items[0]);
            RefreshRemaining(now);
            return;
        }

        RemainingPercent = 0;
        Settle();
    }

    /// <summary>The ring beside the reminder: how much of its time on screen is left.</summary>
    private void RefreshRemaining(DateTimeOffset now)
    {
        if (Current is not { } item)
        {
            RemainingPercent = 0;
            return;
        }
        var left = (item.ExpiresAtUtc - now).TotalMilliseconds;
        RemainingPercent = Math.Clamp(left / item.Life.TotalMilliseconds, 0, 1) * 100d;
    }

    private void Settle()
    {
        if (Items.Count > 0) return;
        _expiry.Stop();
        Emptied?.Invoke(this, EventArgs.Empty);
    }
}
