using IntelligentScheduler.Application;

namespace IntelligentScheduler.Desktop.Platform;

/// <summary>
/// One reminder on its way to the widget. <see cref="EventId"/> is the event it is about, so
/// the shell can mark that event rather than only naming it in a card.
/// </summary>
public sealed record AppNotification(string Title, string Body, DateTimeOffset RaisedAt, Guid? EventId = null);

/// <summary>
/// Where a reminder is decided on and handed to the shell.
///
/// Reminders used to be given to the operating system, which meant a PowerShell process per
/// balloon on Windows and a notification that looked like it belonged to something else. The
/// shell shows them itself now, which also puts the two questions the system used to answer
/// back where the user can reach them: whether it makes a sound, and whether it should arrive
/// at all right now.
///
/// What it does *not* decide any more is how the reminder is presented. A reminder that lands
/// while the widget is open is a different event from one that lands on an empty screen, and
/// only the shell knows which of those is happening — so the sound is played by whoever handles
/// <see cref="Posted"/>, and this class only says whether the user wants one at all.
///
/// Preferences are pushed in from the view model that owns the settings rather than read from
/// the database here: a reminder is checked on the tick of a timer, and a quiet hour must not
/// cost a query every thirty seconds.
///
/// Focus mode is the one thing here that does not simply drop a reminder. A quiet spell is the
/// user saying "not now" about reminders; a game filling the screen is the user saying nothing at
/// all about them, and a meeting that went unmentioned because somebody was playing is the
/// failure that would make focus mode not worth having. So while the screen belongs to something
/// else the reminders are kept, and handed over the moment it gives the screen back.
/// </summary>
public sealed class NotificationCenter : INotificationService
{
    /// <summary>
    /// How many reminders are kept while the screen is somebody else's. A long session produces
    /// more than anyone wants handed back at once, and the oldest are the ones whose moment has
    /// most thoroughly passed — so it is the oldest that are dropped.
    /// </summary>
    private const int HeldLimit = 12;

    private readonly object _gate = new();
    private readonly List<AppNotification> _held = [];
    private bool _enabled = true;
    private bool _quietUntilResumed;
    private DateTimeOffset? _quietUntilUtc;
    private bool _holdingForFocus;

    /// <summary>Raised on whichever thread posted the notification; the shell marshals it.</summary>
    public event EventHandler<AppNotification>? Posted;

    /// <summary>
    /// The screen came back and these had been waiting for it. Raised on the thread that lifted
    /// the hold — the UI thread — with the reminders in the order they were raised. Never raised
    /// empty.
    /// </summary>
    public event EventHandler<IReadOnlyList<AppNotification>>? HeldReleased;

    /// <summary>Whether a reminder may make a sound at all. How loud is the shell's decision.</summary>
    public bool IsSoundEnabled { get; private set; } = true;

    public bool IsQuiet
        => _quietUntilResumed || (_quietUntilUtc is { } until && DateTimeOffset.UtcNow < until);

    /// <summary>Whether reminders are currently being kept back rather than shown.</summary>
    public bool IsHoldingForFocus
    {
        get { lock (_gate) return _holdingForFocus; }
    }

    public void ApplyPreferences(bool enabled, bool soundEnabled, DateTimeOffset? quietUntilUtc,
        bool quietUntilResumed = false)
    {
        _enabled = enabled;
        IsSoundEnabled = soundEnabled;
        _quietUntilUtc = quietUntilUtc;
        _quietUntilResumed = quietUntilResumed;
        // Reminders switched off is an answer about the ones still waiting too. Handing back a
        // pile of them the moment the game closes would be the application arguing with a
        // preference the user had just set.
        if (!enabled) Discard();
    }

    /// <summary>
    /// Whether the screen currently belongs to something else, and whether that is a reason to
    /// keep reminders back. Lifting the hold hands over everything that arrived under it.
    /// </summary>
    public void ApplyFocusMode(bool holding)
    {
        AppNotification[] released;
        lock (_gate)
        {
            if (_holdingForFocus == holding) return;
            _holdingForFocus = holding;
            if (holding) return;
            released = [.. _held];
            _held.Clear();
        }
        if (released.Length > 0 && _enabled) HeldReleased?.Invoke(this, released);
    }

    /// <summary>Forgets whatever is waiting, without handing any of it over.</summary>
    private void Discard()
    {
        lock (_gate) _held.Clear();
    }

    public Task<bool> ShowAsync(string title, string body, Guid? eventId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_enabled || IsQuiet) return Task.FromResult(false);

        lock (_gate)
        {
            if (_holdingForFocus)
            {
                _held.Add(new AppNotification(title, body, DateTimeOffset.Now, eventId));
                if (_held.Count > HeldLimit) _held.RemoveRange(0, _held.Count - HeldLimit);
                // Held is not shown. The reminder service is told so, and the answer is honest:
                // nothing has been put in front of anybody yet.
                return Task.FromResult(false);
            }
        }

        Posted?.Invoke(this, new AppNotification(title, body, DateTimeOffset.Now, eventId));
        return Task.FromResult(true);
    }
}
