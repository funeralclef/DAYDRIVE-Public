using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

namespace IntelligentScheduler.Desktop.Controls;

/// <summary>Where a window's contents come from, and where they go back to.</summary>
public enum RevealOrigin
{
    /// <summary>From the edge of the screen the shell lives on: the shell itself.</summary>
    Edge,
    /// <summary>
    /// Out of the edge the shell stands against, folded: the three panels that take each
    /// other's place. They share a right edge and differ only in width, so folding back into
    /// that edge and unfolding out of it again is the shape of the change between them.
    /// </summary>
    Panel,
    /// <summary>Out of the middle of what it covers: the date and time choosers.</summary>
    Center,
    /// <summary>Unfold from the edge handle's actual position.</summary>
    Petal
}

/// <summary>
/// Gives every window the arrival the quick widget already had. The window is put on screen the
/// moment it is asked for — nothing here delays that — but what it contains fades and slides
/// into place rather than being fully there in the first frame, and leaving is that arrival
/// played backwards on a shorter clock.
///
/// The surface inside the window is what moves, never the window: a native window changing its
/// own opacity flickers on Windows, and one changing its size fights the placement code that
/// lines these surfaces up beside each other. So this only ever touches <see cref="Visual.Opacity"/>
/// and <see cref="Visual.RenderTransform"/> on the content, both on the same clock so they can
/// never finish at different moments. Two properties on one visual is also the whole cost of it:
/// no layout is asked for at any point in a move, so nothing under the surface is measured again.
/// </summary>
public sealed class WindowRevealAnimator
{
    /// <summary>
    /// How long a surface takes to arrive. Public because it is also how long the surface has
    /// no thread to spare: the move is a transition driven on the UI thread, so anything else
    /// that runs there while it plays comes out of it as a dropped frame. Whoever fills a panel
    /// that has just been asked for waits this out before touching the interface.
    /// </summary>
    public static readonly TimeSpan EnterDuration = TimeSpan.FromMilliseconds(240);
    /// <summary>Leaving is quicker than arriving: nobody waits to stop looking at something.</summary>
    public static readonly TimeSpan LeaveDuration = TimeSpan.FromMilliseconds(140);

    /// <summary>
    /// A switch between two panels is those two clocks end to end, because they are never on
    /// screen together — see <see cref="Vacate"/>. Whatever wants to wait out an arrival has to
    /// wait out the departure in front of it as well.
    /// </summary>
    public static TimeSpan SwapDuration => LeaveDuration + EnterDuration;

    private static readonly Easing Arriving = new CubicEaseOut();
    private static readonly Easing Leaving = new CubicEaseIn();

    private readonly Window _window;
    private readonly Control? _surface;
    private readonly TransformOperations _away;
    private CancellationTokenSource? _leaving;

    /// <summary>
    /// Whether the last frame this window actually painted is the away state.
    ///
    /// A hidden window is not a blank one: it hands back whatever it last drew for the two or
    /// three frames after <see cref="Window.Show"/> before it draws anything new, and properties
    /// set while it is hidden never reach its surface. So a window whose last painted frame is
    /// the finished picture cannot arrive from nothing — put up in its away state it flashes
    /// that finished picture first, blanks, and only then begins to move. It is shown settled
    /// instead. Nothing has been painted yet at construction, which is as good as away.
    /// </summary>
    private bool _awayIsPainted = true;

    public WindowRevealAnimator(Window window, RevealOrigin origin = RevealOrigin.Edge)
    {
        _window = window;
        _surface = window.Content as Control;
        _away = origin switch
        {
            // Folded into the edge: pulled toward it and narrowed against it, which on surfaces
            // that all end at the same edge and differ only in width is the difference between
            // them made into a movement.
            RevealOrigin.Panel => TransformOperations.Parse("translateX(26px) scaleX(0.93) scaleY(0.975)"),
            RevealOrigin.Center => TransformOperations.Parse("scale(0.955)"),
            RevealOrigin.Petal => TransformOperations.Parse("scaleX(0.03) scaleY(0.08)"),
            _ => TransformOperations.Parse("translateX(22px) scale(0.985)")
        };
        if (_surface is null) return;

        // The shell stands against the right edge of the screen and everything else lines up
        // beside it, so that edge is the one thing on screen that must not appear to move.
        _surface.RenderTransformOrigin = origin == RevealOrigin.Center
            ? RelativePoint.Center
            : new RelativePoint(1, 0.5, RelativeUnit.Relative);
        _surface.Transitions =
        [
            new DoubleTransition { Property = Visual.OpacityProperty, Duration = EnterDuration, Easing = Arriving },
            new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = EnterDuration, Easing = Arriving }
        ];
    }

    /// <summary>True while the contents are on their way out and the window is still up.</summary>
    public bool IsDismissing => _leaving is not null;

    public void SetOrigin(Point point)
    {
        if (_surface is not null)
            _surface.RenderTransformOrigin = new RelativePoint(point, RelativeUnit.Absolute);
    }

    /// <summary>
    /// Puts the window on screen. <paramref name="show"/> does the actual showing and placing —
    /// it is handed in rather than assumed, because every surface here is positioned differently.
    /// </summary>
    public void Reveal(Action show)
    {
        CancelLeaving();
        if (_surface is null || !IsMotionAllowed)
        {
            show();
            Settle(animated: false);
            return;
        }

        // Already up: this is either a repeat of a request or a change of mind partway through
        // leaving. Either way the surface simply springs back from wherever it had got to.
        if (_window.IsVisible)
        {
            show();
            SetPace(EnterDuration, Arriving);
            Settle(animated: true);
            return;
        }

        // Nothing to arrive from: what this window last put on the screen is its finished
        // picture, and it will hand that back before any move could play over it.
        if (!_awayIsPainted)
        {
            RevealNow(show);
            return;
        }

        Place(_away, animated: false);
        show();
        SetPace(EnterDuration, Arriving);
        // Settled in the same turn as the window is shown, the first painted frame can be the
        // finished one — and an arrival nobody sees is not an arrival. This waits for the frame
        // that puts the window up before starting the move onto it.
        Dispatcher.UIThread.Post(
            () => { if (_window.IsVisible && !IsDismissing) Settle(animated: true); },
            DispatcherPriority.Render);
    }

    /// <summary>
    /// Puts the window up with its contents already in place and no motion at all, for a surface
    /// that has no arrival to play — one whose last painted frame is the finished picture, or
    /// one being photographed rather than watched.
    /// </summary>
    public void RevealNow(Action show)
    {
        CancelLeaving();
        Settle(animated: false);
        show();
    }

    /// <summary>
    /// Takes the contents back where they came from and hides the window once that has finished.
    /// <paramref name="hide"/> is the caller's own hiding, run at the end rather than at once.
    /// </summary>
    public void Dismiss(Action hide) => Dismiss(hide, LeaveDuration, Arriving);

    /// <summary>
    /// Steps out so another surface can have this one's place, and says when the place is free.
    ///
    /// It used to hand over by fading out underneath its replacement, on the reasoning that a
    /// window costs a few frames to appear and something had to cover the gap. What that bought
    /// was a tenth of a second of two panels on screen at once — and every surface here is
    /// translucent, so the one underneath does not disappear behind the one over it. It shows
    /// through: a second picture behind the first, and two backgrounds stacked where there
    /// should be one. That is the dark plate that used to slide under a panel every time the
    /// shell was asked for a different one.
    ///
    /// So the two halves of a switch are played one after the other and never together. This
    /// one folds back into the edge it came from — on real frames, which is also what leaves the
    /// window able to unfold out of that edge the next time it is asked for — and only when it
    /// is off the screen entirely does <paramref name="landed"/> run and its replacement begin.
    /// Both ends of the handover are invisible, so the seam between them is not a hole: the
    /// surface leaving is already at nothing when the one arriving starts from nothing.
    /// </summary>
    public void Vacate(Action hide, Action landed)
    {
        if (_surface is null || !_window.IsVisible || !IsMotionAllowed)
        {
            DismissNow(hide);
            landed();
            return;
        }

        CancelLeaving();
        SetPace(LeaveDuration, Leaving);
        Place(_away, animated: true);
        _leaving = new CancellationTokenSource();
        _ = HandOverAsync(hide, landed, _leaving.Token);
    }

    private void Dismiss(Action hide, TimeSpan duration, Easing easing)
    {
        if (_surface is null || !_window.IsVisible || !IsMotionAllowed)
        {
            DismissNow(hide);
            return;
        }

        if (IsDismissing) return;
        SetPace(duration, easing);
        Place(_away, animated: true);
        _leaving = new CancellationTokenSource();
        var token = _leaving.Token;
        _ = FinishAsync(hide, duration, token);
    }

    /// <summary>
    /// Hides with none of the choreography: the whole shell going away, or a panel being put
    /// back before it was ever really up, has nothing to say by fading out first.
    /// </summary>
    public void DismissNow(Action hide)
    {
        CancelLeaving();
        // Whatever it had on the screen is what it will hand back when it is next shown. A
        // window that was not up painted nothing and keeps whatever it was left able to do.
        if (_window.IsVisible) _awayIsPainted = false;
        hide();
        Place(_away, animated: false);
    }

    private async Task FinishAsync(Action hide, TimeSpan duration, CancellationToken token)
    {
        try
        {
            await Task.Delay(duration, token);
            token.ThrowIfCancellationRequested();
            Retire(hide);
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// The same wait, with the promise that whoever is holding the place open is told it is
    /// free even if the departure is called off partway — a switch nobody finishes is a switch
    /// nobody can start either.
    /// </summary>
    private async Task HandOverAsync(Action hide, Action landed, CancellationToken token)
    {
        try
        {
            await Task.Delay(LeaveDuration, token);
            token.ThrowIfCancellationRequested();
            Retire(hide);
        }
        catch (OperationCanceledException) { }
        finally { landed(); }
    }

    private void Retire(Action hide)
    {
        hide();
        CancelLeaving();
        Place(_away, animated: false);
        // It went out over frames it actually drew, so the away state is what it is holding.
        _awayIsPainted = true;
    }

    /// <summary>
    /// Drops the contents straight into place with none of the motion. For a surface that is
    /// photographed rather than watched: the snapshot sweep renders in the same turn it asks for
    /// the surface, and there is no frame there for an arrival to play on.
    /// </summary>
    public void SettleNow()
    {
        CancelLeaving();
        Settle(animated: false);
    }

    private bool IsMotionAllowed => !_window.Classes.Contains("reduceMotion");

    private void Settle(bool animated)
    {
        Place(TransformOperations.Identity, animated, opacity: 1);
        _awayIsPainted = false;
    }

    /// <summary>
    /// Moves the surface to a state, with or without the transition carrying it there. Turning
    /// the transitions off for a jump is what stops the "away" state being animated into on the
    /// way to being animated out of.
    /// </summary>
    private void Place(TransformOperations transform, bool animated, double opacity = 0)
    {
        if (_surface is null) return;
        var transitions = _surface.Transitions;
        if (!animated) _surface.Transitions = null;
        _surface.Opacity = opacity;
        _surface.RenderTransform = transform;
        if (!animated) _surface.Transitions = transitions;
    }

    /// <summary>How long the next move takes and how it is carried; both transitions share it.</summary>
    private void SetPace(TimeSpan duration, Easing easing)
    {
        if (_surface?.Transitions is not { } transitions) return;
        foreach (var transition in transitions)
        {
            switch (transition)
            {
                case DoubleTransition fade: fade.Duration = duration; fade.Easing = easing; break;
                case TransformOperationsTransition move: move.Duration = duration; move.Easing = easing; break;
            }
        }
    }

    private void CancelLeaving()
    {
        _leaving?.Cancel();
        _leaving?.Dispose();
        _leaving = null;
    }
}
