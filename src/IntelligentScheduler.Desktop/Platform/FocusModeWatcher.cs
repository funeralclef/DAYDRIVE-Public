using Avalonia.Threading;
using IntelligentScheduler.Application;

namespace IntelligentScheduler.Desktop.Platform;

/// <summary>
/// Whether something else has the whole screen, asked a few times a minute.
///
/// What the asking consists of is the one part of this that differs between desktops, and it
/// lives behind <see cref="IScreenOwnerProbe"/>: Windows answers the question itself through
/// <c>SHQueryUserNotificationState</c>, macOS is measured off the window list, and an X11
/// session is asked in the same words a video player uses to go full screen in the first place.
/// This class is what is left once that difference is taken out — the timer, the switches, the
/// hysteresis, and the name of whatever is in front.
///
/// Nothing here is polled while it is switched off, and the name of the foreground application
/// is only ever looked up when the window it belongs to has changed — a name is the one part of
/// this that can cost a round trip or a process handle, and the state is asked for far more
/// often than it moves. That name goes nowhere: it is shown on the tray line and on the settings
/// page of the machine it was read on, so the user can see what the shell thinks is in front of
/// them.
///
/// One desktop is left out and says so: Wayland does not tell a client what else is on screen,
/// by design. There focus mode is the switch the user throws by hand, which still lets go of the
/// shortcuts, still holds the reminders, and still takes the petal off the edge of the screen.
/// </summary>
public sealed class FocusModeWatcher : IDisposable
{
    /// <summary>
    /// Long enough to cost nothing, short enough that alt-tabbing out of a game hands the
    /// shortcuts back before the user has finished reaching for them.
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How many clear answers in a row it takes to leave focus mode. Arriving is immediate;
    /// leaving is not, because the answer blinks. A window of this application's own asking for
    /// the front of the always-on-top band is a moment during which the desktop no longer
    /// considers the screen covered — so a game that never stopped running would hand the
    /// shortcuts back and put a card up, twice a minute, forever. Against that, staying out of
    /// the way four seconds longer than strictly necessary costs nothing.
    /// </summary>
    private const int ClearAnswersBeforeLeaving = 2;

    private readonly DispatcherTimer _timer = new() { Interval = Interval };
    private readonly IScreenOwnerProbe _probe;
    private bool _automatic;
    private bool _held;
    private int _clearAnswers;
    private nint _namedWindow;
    private string _namedApp = string.Empty;
    private bool _disposed;

    public FocusModeWatcher() : this(ScreenOwnerProbes.ForThisDesktop()) { }

    /// <summary>Handed a probe by the tests, which have no desktop to ask.</summary>
    public FocusModeWatcher(IScreenOwnerProbe probe)
    {
        _probe = probe;
        _timer.Tick += (_, _) => Evaluate(onATick: true);
    }

    /// <summary>Whether this desktop can be asked the question at all.</summary>
    public bool IsSupported => _probe.IsSupported;

    /// <summary>Why it cannot, in a word or two, for the diagnostic log. Empty when it can.</summary>
    public string UnsupportedReason => _probe.UnsupportedReason;

    public FocusModeState Current { get; private set; } = FocusModeState.Off;

    /// <summary>Raised on the UI thread, and only when the answer has actually changed.</summary>
    public event EventHandler<FocusModeState>? Changed;

    /// <summary>
    /// The two switches, pushed in from the view model that owns them. Re-asks the question
    /// straight away rather than waiting for the next tick: a user who has just switched this on
    /// while a game is already running is owed the answer now.
    /// </summary>
    public void Configure(bool automatic, bool held)
    {
        _automatic = automatic;
        _held = held;
        _clearAnswers = 0;
        if (_automatic && IsSupported && !_disposed) _timer.Start();
        else _timer.Stop();
        // Not on a tick: a switch the user has just thrown is answered at once, and waiting for
        // a grace period that is measured in ticks would in any case never end once switching
        // the watching off has stopped the timer.
        Evaluate();
    }

    private void Evaluate(bool onATick = false)
    {
        if (_disposed) return;
        var ownership = _automatic && IsSupported ? _probe.Ask() : ScreenOwnership.Nothing;

        var trigger = FocusModeRules.Decide(_automatic, _held, ownership.Presence, ownership.OwnsTheScreen);
        // A clear answer that follows an automatic focus mode has to be repeated before it is
        // believed. A hand-held one is not made to wait: the user has just switched it off.
        if (onATick && trigger == FocusModeTrigger.None
            && Current.IsActive && Current.Trigger != FocusModeTrigger.Held
            && ++_clearAnswers < ClearAnswersBeforeLeaving) return;
        _clearAnswers = 0;

        // Named only when the window in front is measurably the one covering the screen. A
        // desktop answers a question about the screen, not about focus, and the two can
        // disagree: a full-screen window that never took the foreground leaves something else
        // in front, and that something else is then not what has the screen. Rather than name
        // the wrong application, the sentence says "something" — which is at least true.
        var app = trigger is FocusModeTrigger.None or FocusModeTrigger.Held || !ownership.OwnsTheScreen
            ? string.Empty
            : DescribeWindow(ownership.Window);

        var state = new FocusModeState(trigger, app);
        if (state == Current) return;
        Current = state;
        Changed?.Invoke(this, state);
    }

    /// <summary>
    /// The application's name, looked up at most once per window in front. A window that was
    /// already named keeps its name, which is what makes this cost nothing on the ticks — the
    /// overwhelming majority — where nothing has moved.
    /// </summary>
    private string DescribeWindow(nint window)
    {
        if (window == 0) return string.Empty;
        if (window == _namedWindow) return _namedApp;
        _namedWindow = window;
        _namedApp = _probe.DescribeOwner(window);
        return _namedApp;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _probe.Dispose();
    }
}
