using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using IntelligentScheduler.Desktop.Controls;
using IntelligentScheduler.Desktop.ViewModels;

namespace IntelligentScheduler.Desktop;

/// <summary>
/// The small widget beside the edge handle.
///
/// It used to appear on hover, in two stages, because a widget that arrives on its own has to
/// assume it was not wanted: it came in dim and inert so that brushing the edge of the screen
/// on the way somewhere else could not press anything, and it left through the same two stages
/// in reverse. All of that existed to compensate for never having been asked for.
///
/// It is now asked for — the right button on the handle, the way the left button asks for the
/// panel — and it arrives on exactly the clock every other surface in the shell arrives on: the
/// card is carried by the shared <see cref="WindowRevealAnimator"/>, so the widget and the
/// panels are one gesture rather than two that happen to point the same way. Before that the
/// widget ran its own card transition on its own easing and its own duration, next to a longer
/// stagger inside it — three clocks for one arrival, which is why it read as the slowest thing
/// in the application while in fact starting at the same moment as everything else.
///
/// The blocks settling in sequence is the one thing the widget still does that the panels do
/// not, and it now finishes inside the card's own arrival rather than after it.
///
/// There is a second, smaller way in. A reminder puts the widget on the screen in its compact
/// shape — the event card alone — and the moment the user
/// touches it the rest of the widget unfolds around that card. The reminder section at the foot
/// of this file is what that costs.
/// </summary>
public sealed partial class QuickPeekWindow : Window
{
    /// <summary>
    /// How long the blocks take to settle, and so the one moment nothing in the widget answers
    /// the pointer. Every block arrives at its own point in the timeline, and a pointer already
    /// resting where one of them is about to land used to light it up before it had arrived — a
    /// lit rectangle with nothing in it, which reads as the animation breaking rather than as a
    /// hover. It used to be held shut for 0.44 s, which was long enough to feel like the widget
    /// had opened and then refused the first click.
    /// </summary>
    private static readonly TimeSpan SettleDuration = TimeSpan.FromMilliseconds(320);

    /// <summary>
    /// How long after that the entrance is taken back off the parts. It has to outlast the
    /// animation itself, which finishes at the same moment the widget opens for business.
    /// </summary>
    private static readonly TimeSpan EntranceTail = TimeSpan.FromMilliseconds(120);

    /// <summary>Slack around the blocks, which is also the room their shadows need to fall in.</summary>
    public const double CardInset = 24;

    private readonly WindowRevealAnimator _reveal;
    private CancellationTokenSource? _settling;

    public QuickPeekWindow()
    {
        InitializeComponent();
        _reveal = new WindowRevealAnimator(this, RevealOrigin.Petal);
        LayoutUpdated += (_, _) => { if (IsVisible) PlacementChanged?.Invoke(this, EventArgs.Empty); };
        // Tunnelled, so a press that lands on one of the rail buttons still counts: this is not
        // about what was pressed, only about the widget having been used.
        PeekContent.AddHandler(PointerPressedEvent, Content_OnPointerPressed, RoutingStrategies.Tunnel);
        _entranceParts = [PeekClockBlock, PeekEventFrame, PeekNextBlock, PeekRail];
    }

    /// <summary>
    /// Every part the entrance moves, in the order the styles stagger them: the clock, the event
    /// card, what comes next, and the rail.
    /// </summary>
    private readonly Control[] _entranceParts;

    public event EventHandler? PointerReturned;
    public event EventHandler? PlacementChanged;
    public event EventHandler? PointerDeparted;
    /// <summary>The right button again, this time on the widget: put it away.</summary>
    public event EventHandler? DismissRequested;

    /// <summary>A reminder card was pressed. What it is about is what the shell should open.</summary>
    public event EventHandler<NotificationToastItem>? ReminderOpened;

    /// <summary>
    /// Something in the widget was pressed. A widget that put itself on screen to carry a
    /// reminder becomes the user's own at that point, and stops leaving when the reminder does.
    /// </summary>
    public event EventHandler? Engaged;

    /// <summary>
    /// The widget is off the screen. Anything the widget was the host for — the duration
    /// question it asks, which is a window of its own — has nothing to stand over any more.
    /// </summary>
    public event EventHandler? Retired;

    /// <summary>True while the widget is fading out and a press should bring it back instead.</summary>
    public bool IsDismissing => _reveal.IsDismissing;

    /// <summary>
    /// True while the fade-out was asked for rather than timed out. The pointer crossing a
    /// widget on its way out is not a change of mind, so this is what stops the thing the user
    /// has just closed from opening again under their cursor.
    /// </summary>
    public bool IsDismissalFinal { get; private set; }

    /// <summary>
    /// Shows the widget, ready to be used. <paramref name="show"/> is the caller's own showing
    /// and placing — the widget sizes itself to what it is currently displaying, so where it
    /// belongs is not something this can work out on its own.
    ///
    /// Called again while it is fading out this is not a fresh reveal but a change of mind:
    /// the card springs back from wherever it had got to, and replaying the staggered entrance
    /// on top of that would fight it. That call passes no <paramref name="show"/>, because a
    /// widget that is changing its mind about leaving is already on screen.
    /// </summary>
    public void Reveal(Action? show = null)
    {
        var reversing = IsDismissing;
        IsDismissalFinal = false;
        _reveal.Reveal(show ?? (static () => { }));
        if (reversing)
        {
            // The card never left, so there is no entrance to sit through — and the one that
            // was still playing is abandoned here rather than allowed to finish, so its classes
            // have to come off with it. See ClearEntranceClasses.
            CancelSettling();
            CancelUnfolding();
            ClearEntranceClasses();
            PeekContent.IsHitTestVisible = true;
            return;
        }

        PeekMascot.Restart();
        ReplayReveal();
    }

    /// <summary>
    /// The way out: the arrival played backwards on a shorter clock, with the window hidden only
    /// once it has finished. A fade that timed out is called off by the pointer returning; one
    /// the user asked for is not.
    /// </summary>
    public void BeginDismiss(bool final = false)
    {
        if (!IsVisible || IsDismissing) return;
        CancelSettling();
        IsDismissalFinal = final;
        // Nothing inside it can be pressed on the way out; the card is already leaving.
        PeekContent.IsHitTestVisible = false;
        _reveal.Dismiss(Retire);
    }

    /// <summary>
    /// Puts the widget straight into its settled state with none of the choreography. Used by
    /// the snapshot sweep, which re-hosts the card in a plain window and photographs it in the
    /// same turn — an arrival has no frames to play on there, and the card would be caught
    /// halfway through one or not yet started on it.
    /// </summary>
    public void SettleNow()
    {
        CancelSettling();
        CancelUnfolding();
        _reveal.SettleNow();
        ClearEntranceClasses();
        PeekContent.IsHitTestVisible = true;
        SnapReminder();
    }

    /// <summary>
    /// Steps off the screen so the shell can have its place, and says when that place is free.
    ///
    /// The widget and the shell stand against the same edge and both are translucent, so one
    /// arriving over the other is two pictures stacked rather than a switch — the same reason
    /// the three side panels hand over one at a time. This is the widget's half of it, played
    /// on frames it actually draws, which is also what leaves it able to unfold out of that
    /// edge the next time it is asked for: put away in a single frame instead, the last thing
    /// it painted was its finished picture, and a widget that cannot arrive from nothing is
    /// shown settled. That is the entrance the widget lost whenever the shell took its place.
    /// </summary>
    public void Vacate(Action landed)
    {
        CancelSettling();
        // Handing the screen over is asked for, so the pointer crossing the widget on its way
        // out must not bring it back.
        IsDismissalFinal = true;
        PeekContent.IsHitTestVisible = false;
        _reveal.Vacate(Retire, landed);
    }

    /// <summary>Puts the widget back to hidden without any of the choreography.</summary>
    public void ResetState()
    {
        CancelSettling();
        _reveal.DismissNow(Retire);
    }

    /// <summary>
    /// Restarts the staggered entrance. A style animation only runs when its selector starts
    /// matching, so the class has to stop matching first — and on a later frame, or the two
    /// changes collapse into no change at all and the widget appears without moving.
    /// </summary>
    public void ReplayReveal()
    {
        CancelSettling();
        CancelUnfolding();
        ClearEntranceClasses();
        if (!IsMotionAllowed)
        {
            PeekContent.IsHitTestVisible = true;
            return;
        }

        PeekContent.IsHitTestVisible = false;
        Dispatcher.UIThread.Post(
            () => { if (IsVisible) MarkEntrance("revealing"); },
            DispatcherPriority.Render);
        _settling = new CancellationTokenSource();
        _ = OpenForBusinessAsync(_settling.Token);
    }

    /// <summary>Everything the widget has to forget once it is off the screen.</summary>
    private void Retire()
    {
        Hide();
        IsDismissalFinal = false;
        PeekContent.IsHitTestVisible = false;
        CancelUnfolding();
        ClearEntranceClasses();
        Retired?.Invoke(this, EventArgs.Empty);
        // Last, and after the event rather than before it: off the screen the widget hands its
        // reminders back, and whoever handles Retired is what decides whether any are left. Only
        // then can the card be put into the state it must be found in the next time it is asked
        // for. It is nobody's now either — the next reminder brings its own compact shape with
        // it, and the next deliberate opening gets the whole widget.
        if (_shell is not null) _shell.IsPeekCompact = false;
        SnapReminder();
    }

    private void CancelSettling()
    {
        _settling?.Cancel();
        _settling?.Dispose();
        _settling = null;
    }

    /// <summary>
    /// Starts an entrance, on the parts that are actually making one.
    ///
    /// Only the visible ones, and that is the whole point. These are style animations with a
    /// forward fill, and a part that is off the screen when one starts never gets a frame to
    /// play it on: it stops on the first keyframe, which is zero opacity, and goes on holding
    /// that after the class has come off again. The entrance used to be a single class on the
    /// card with descendant selectors underneath, so every block got one whether it was on the
    /// screen or not — and the countdown, which is exactly the block that comes and goes, was
    /// then invisible for the rest of the widget's life while still taking up its full height.
    ///
    /// A part that appears later simply appears. It has missed the arrival, which is a fair
    /// description of what happened.
    /// </summary>
    private void MarkEntrance(string entrance)
    {
        foreach (var part in _entranceParts)
            if (part.IsVisible) part.Classes.Add(entrance);
    }

    /// <summary>
    /// Takes every entrance back off.
    ///
    /// Each entrance also takes its own class off when it finishes. This is for the paths that
    /// abandon one halfway: a fade reversed under the pointer, an entrance replayed because an
    /// event has just started, a widget unfolded and then asked to arrive again.
    /// </summary>
    private void ClearEntranceClasses()
    {
        foreach (var part in _entranceParts)
        {
            part.Classes.Remove("revealing");
            part.Classes.Remove("unfolding");
        }
    }

    private bool IsMotionAllowed => !Classes.Contains("reduceMotion");

    private async Task OpenForBusinessAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(SettleDuration, token);
            token.ThrowIfCancellationRequested();
            PeekContent.IsHitTestVisible = true;
            // And then the entrance comes off, rather than being left on for as long as the
            // widget is up: a forward fill goes on applying the values it finished on, and
            // every one of those is the value the part would have had anyway.
            await Task.Delay(EntranceTail, token);
            token.ThrowIfCancellationRequested();
            foreach (var part in _entranceParts) part.Classes.Remove("revealing");
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// A press inside the widget adopts it, except for the reminder's dismiss petal: hiding a
    /// notification must not open the dashboard or navigate to its event.
    /// </summary>
    private void Content_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed) return;
        if (e.Source is Visual source && (ReferenceEquals(source, DismissReminderButton)
            || source.GetVisualAncestors().Contains(DismissReminderButton))) return;
        // Whether this press is the one that opens the widget out of its compact shape has to be
        // decided here, in the tunnel, because unfolding is what makes it stop being true.
        _pressAnswered = _shell?.IsPeekCompact == true;
        Engaged?.Invoke(this, EventArgs.Empty);
    }

    private void Card_OnPointerEntered(object? sender, PointerEventArgs e)
        => PointerReturned?.Invoke(this, EventArgs.Empty);

    private void Card_OnPointerExited(object? sender, PointerEventArgs e)
        => PointerDeparted?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// The event card was pressed while it is wearing a reminder. Only the left button: the
    /// right one belongs to the card underneath, which is what closes the widget, and a reminder
    /// must not take that away from the surface it is standing on.
    /// </summary>
    private void Reminder_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed) return;
        if (_shell?.Notifications.Current is not { } item) return;
        // Already answered in the tunnel: this press opened the widget, or dismissed the
        // reminder. Opening the day as well would take the user off what they just asked to see.
        if (_pressAnswered) { e.Handled = true; return; }
        ReminderOpened?.Invoke(this, item);
        e.Handled = true;
    }

    /// <summary>The same button that opened it closes it, wherever the pointer happens to be.</summary>
    private void Card_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.RightButtonPressed) return;
        DismissRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    // Both compact notifications and the full widget are centred beside the handle.

    /// <summary>How long the rest of the widget takes to appear around the card.</summary>
    private static readonly TimeSpan UnfoldDuration = TimeSpan.FromMilliseconds(360);

    /// <summary>
    /// How long the card goes on drawing a reminder that has lapsed. The accent leaves the
    /// block's edge over about the same time, so the two read as one thing letting go rather
    /// than as text swapped under a border that is still lit.
    /// </summary>
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(220);

    /// <summary>How long the arrival plays on the card's contents; see <c>App.axaml</c>.</summary>
    private static readonly TimeSpan ArrivalDuration = TimeSpan.FromMilliseconds(340);

    private EdgeShellViewModel? _shell;
    private CancellationTokenSource? _unfolding;
    private CancellationTokenSource? _fading;
    private CancellationTokenSource? _arriving;
    private bool _pressAnswered;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_shell is not null) _shell.Notifications.PropertyChanged -= OnNotificationsChanged;
        _shell = DataContext as EdgeShellViewModel;
        if (_shell is not null) _shell.Notifications.PropertyChanged += OnNotificationsChanged;
        SnapReminder();
    }

    /// <summary>
    /// Gives the user the whole widget, out of the compact shape a reminder put it in. The
    /// layout does none of the moving: the blocks simply become visible, and a window that sizes
    /// itself to its contents is a different size on the next pass. What is animated is each
    /// block's own arrival, and where the window is put while that happens.
    /// </summary>
    public void Unfold()
    {
        if (_shell is not { IsPeekCompact: true } shell) return;
        shell.IsPeekCompact = false;
        // The clock block, and the creature standing in it, were collapsed for as long as the
        // widget belonged to a reminder — and a drawing that is not on the screen stops its own
        // clock on the first frame it notices, because a widget that is away far more often than
        // it is up has no business animating into a hidden window. Nothing tells it when an
        // ancestor becomes visible again. This is the one place a block goes from hidden to shown
        // while the widget itself stands, so the widget is what has to say so.
        if (IsVisible) PeekMascot.Restart();
        if (!IsVisible || !IsMotionAllowed)
        {
            return;
        }

        CancelUnfolding();
        // The entrance and the unfold move the same blocks, so the widget is never playing both.
        // A widget opened out of its compact shape within the first third of a second of landing
        // would otherwise have two animations arguing over the clock.
        CancelSettling();
        PeekContent.IsHitTestVisible = true;
        ClearEntranceClasses();
        _unfolding = new CancellationTokenSource();
        var token = _unfolding.Token;
        // A style animation only runs when its selector starts matching, so the class has to
        // stop matching first — and on a later frame, or the two changes collapse into none.
        Dispatcher.UIThread.Post(
            () =>
            {
                if (token.IsCancellationRequested || !IsVisible) return;
                MarkEntrance("unfolding");
            },
            DispatcherPriority.Render);
        _ = EndUnfoldAsync(token);
    }

    private async Task EndUnfoldAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(UnfoldDuration, token);
            token.ThrowIfCancellationRequested();
            // Taken off again: a forward fill goes on holding the values it finished on, and
            // every one of them is what the block would have had anyway.
            foreach (var part in _entranceParts) part.Classes.Remove("unfolding");
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Centre the complete widget beside the handle on every layout change.
    /// Use layout sizes so entrance transforms cannot feed back into placement.
    /// </summary>
    public void PlaceOn(PixelRect area, double scale, int cardRight, Size? measuredSize = null)
    {
        var measured = measuredSize is { Width: > 1, Height: > 1 } size ? size : ClientSize;
        if (measured.Width <= 1 || measured.Height <= 1) return;
        var width = (int)Math.Ceiling(measured.Width * scale);
        var height = (int)Math.Ceiling(measured.Height * scale);
        var top = area.Y + (int)Math.Round((area.Height - height) / 2d);
        var position = new PixelPoint(
            Math.Max(area.X, cardRight + (int)Math.Round(CardInset * scale) - width),
            Math.Clamp(top, area.Y, Math.Max(area.Y, area.Bottom - height)));
        if (Position != position) Position = position;
        _reveal.SetOrigin(new Point(
            (area.Right - (EdgeHandleWindow.ScreenGap + EdgeHandleWindow.RestCapsuleWidth / 2) * scale - position.X) / scale,
            (area.Y + area.Height / 2d - position.Y) / scale));
    }

    private void CancelUnfolding()
    {
        _unfolding?.Cancel();
        _unfolding?.Dispose();
        _unfolding = null;
    }

    private void OnNotificationsChanged(object? sender, PropertyChangedEventArgs args)
    {
        switch (args.PropertyName)
        {
            // Whether the card should be wearing a reminder at all.
            case nameof(NotificationToastViewModel.IsPresenting):
                ApplyReminderState();
                break;
            // A different reminder in the same place. The card is already the right shape, so
            // only what it says arrives — which is what makes a second reminder landing on the
            // first read as a replacement rather than as the widget doing anything.
            case nameof(NotificationToastViewModel.Sequence):
                PlayReminderArrival();
                break;
        }
    }

    private void ApplyReminderState()
    {
        CancelFading();
        // The card has just taken a reminder on. Nothing to time: it holds it until it lapses.
        if (_shell?.HasPeekNotifications == true) return;
        // On its way out the widget keeps whatever it is drawing — the card would otherwise
        // change what it says while it is fading, and Retire drops the reminder the moment the
        // widget is off the screen in any case.
        if (IsVisible && IsDismissing) return;
        // Nothing to play a fade on: the widget is away, or it is being photographed.
        if (!IsVisible || !IsMotionAllowed)
        {
            SnapReminder();
            return;
        }
        _fading = new CancellationTokenSource();
        _ = EndPresentationAsync(_fading.Token);
    }

    /// <summary>
    /// Keep the reminder text and dismiss petal in place while the accent fades, then restore
    /// the event card and its ordinary timer together.
    /// </summary>
    private async Task EndPresentationAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(FadeDuration, token);
            token.ThrowIfCancellationRequested();
            _shell?.Notifications.ReleasePresentation();
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Puts the card straight into the state the reminders currently ask for.</summary>
    private void SnapReminder()
    {
        CancelFading();
        CancelArrival();
        PeekEventBody.Classes.Remove("arriving");
        // A reminder that was only still being drawn so that something could fade is finished
        // with once there is nothing to fade: the widget went away, or was never up at all.
        if (_shell?.Notifications is { IsPresenting: false, Current: not null } finished)
            finished.ReleasePresentation();
    }

    /// <summary>
    /// Replays the arrival on the card's contents. A style
    /// animation only runs when its selector starts matching, so the class has to stop matching
    /// first — and on a later frame, or the two changes collapse into no change at all. It is
    /// taken off again at the end, because a forward fill otherwise goes on holding the values
    /// the arrival finished on.
    /// </summary>
    private void PlayReminderArrival()
    {
        CancelArrival();
        if (!IsVisible || !IsMotionAllowed || !PeekEventFrame.IsVisible) return;
        // The card is arriving with the widget itself; it does not also need to arrive inside it.
        if (PeekEventFrame.Classes.Contains("revealing")) return;

        PeekEventBody.Classes.Remove("arriving");
        _arriving = new CancellationTokenSource();
        var token = _arriving.Token;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (token.IsCancellationRequested || !IsVisible) return;
                PeekEventBody.Classes.Add("arriving");
            },
            DispatcherPriority.Render);
        _ = EndArrivalAsync(token);
    }

    private async Task EndArrivalAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(ArrivalDuration, token);
            token.ThrowIfCancellationRequested();
            PeekEventBody.Classes.Remove("arriving");
        }
        catch (OperationCanceledException) { }
    }

    private void CancelFading()
    {
        _fading?.Cancel();
        _fading?.Dispose();
        _fading = null;
    }

    private void CancelArrival()
    {
        _arriving?.Cancel();
        _arriving?.Dispose();
        _arriving = null;
    }
}
