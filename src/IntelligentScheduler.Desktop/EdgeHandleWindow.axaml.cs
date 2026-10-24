using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using IntelligentScheduler.Desktop.Platform;

namespace IntelligentScheduler.Desktop;

/// <summary>
/// The petal on the edge of the screen.
///
/// It is the only surface of this application that is there all day, and for most of that day it
/// has nothing to say: a narrow, symmetric capsule floating just inside the right-hand edge.
/// The same screen gap is preserved when it widens to carry a stopwatch.
/// Both of the things it opens are opened deliberately, with a
/// button — the left one asks for the panel, the right one for the quick widget.
///
/// While a session is running it carries the stopwatch, and that is the whole of what it does
/// differently: it widens and the clock appears in it, stood on end. It does
/// not grow into an object, it does not carry controls, and it does not react to the pointer by
/// changing size. The controls live beside it, in <see cref="PetalRailWindow"/>, for the same
/// reason they live beside the quick widget's card rather than inside it.
///
/// <b>The window is placed once and then left alone.</b> That is the rule the rest of this file
/// exists to keep, and it is worth stating as a rule because breaking it is what went wrong
/// before: a window that resizes and re-places itself on hover, on a press, and again on a timer
/// afterwards fights the placement of every other surface on this edge, and the fighting is
/// visible — the petal flinched whenever anything was opened from it. Nothing here touches the
/// window's size or position except <see cref="PlaceOn"/>, which the shell's own placement calls.
/// Everything that moves is a transition on the capsule inside it: width, corners, two brushes.
///
/// How tall it stands is a fraction of the monitor rather than a number. A hundred and twelve
/// pixels is a third of the height of a laptop lid and a twentieth of a desk monitor turned on
/// its side, and the same sliver cannot be both — so it is a share of the working area, clamped
/// at both ends to stay recognisably the same small thing on either.
/// </summary>
public sealed partial class EdgeHandleWindow : Window
{
    // ---- What it is -------------------------------------------------------------------------
    // One window width and one window height, both settled by the monitor and neither touched
    // again. The capsule has two widths; the difference between them is the entire animation.

    /// <summary>
    /// Room for the larger capsule and its screen-side margin. Kept fixed across both states.
    /// </summary>
    private const double WindowWidth = LiveCapsuleWidth + ScreenGap;

    public const double RestCapsuleWidth = 10;

    /// <summary>Space between the capsule and the working area's right edge, in DIPs.</summary>
    public const double ScreenGap = 6;

    /// <summary>
    /// Two tabular digits with a little space on either side. The input window reserves this
    /// width in both states, so starting a stopwatch never moves the window.
    /// </summary>
    public const double LiveCapsuleWidth = 32;

    private const double HeightShare = 0.115;
    private const double MinHeight = 118;
    private const double MaxHeight = 172;

    /// <summary>What the capsule gives back to the window at the top and the bottom.</summary>
    private const double VerticalInset = 7;

    /// <summary>Whether the petal is carrying a session. Pushed in from the view model.</summary>
    public static readonly StyledProperty<bool> IsLiveProperty =
        AvaloniaProperty.Register<EdgeHandleWindow, bool>(nameof(IsLive));

    private bool _suppressed;
    private bool _panelBridge;
    private bool _placed;
    private PixelRect _area;
    private double _scale = 1;

    public EdgeHandleWindow()
    {
        InitializeComponent();
        // Asked of the framework rather than tracked by hand: IsPointerOver is the one answer
        // that already accounts for the children, and the whole strip is one hover.
        Reach.PropertyChanged += (_, change) =>
        {
            if (change.Property == IsPointerOverProperty) OnPointerOverChanged(Reach.IsPointerOver);
        };
    }

    /// <summary>The left button: show or hide the main panel.</summary>
    public event EventHandler? ToggleRequested;

    /// <summary>The right button: show or hide the quick widget.</summary>
    public event EventHandler? PeekRequested;

    /// <summary>The pointer is on the petal. The widget and the control rail both use this.</summary>
    public event EventHandler? HoverStarted;

    /// <summary>The pointer left it; whatever is beside it starts its own grace period.</summary>
    public event EventHandler? HoverEnded;

    public bool IsLive
    {
        get => GetValue(IsLiveProperty);
        set => SetValue(IsLiveProperty, value);
    }

    /// <summary>Whether the pointer is anywhere on the strip. The rail beside it asks.</summary>
    public bool IsHovered => Reach.IsPointerOver;

    /// <summary>The open planner owns its gap so a click cannot leave its keyboard surface.</summary>
    public void SetPanelBridge(bool panelOpen)
    {
        if (_panelBridge == panelOpen) return;
        _panelBridge = panelOpen;
        var left = panelOpen ? WindowWidth - ScreenGap - RestCapsuleWidth : 0;
        Reach.Margin = new Thickness(left, 0, 0, 0);
        (PlatformImpl as LinuxLayerWindow)?.SetInputInsets(new Thickness(left, 0, 0, 0));
    }

    /// <summary>
    /// Whether something else of this application's own is standing on this edge. The shell and
    /// the quick widget leave room for the narrow capsule and its screen gap. The stopwatch
    /// folds away while those surfaces are open so it does not overlap them.
    /// The petal itself stays exactly where it is: it is still the way back.
    /// </summary>
    public void SetSuppressed(bool suppressed)
    {
        if (_suppressed == suppressed) return;
        _suppressed = suppressed;
        ApplyShape();
    }

    /// <summary>
    /// Where this petal belongs, in the working area it belongs to. The shell's own placement is
    /// the one thing that knows which monitor everything is on, so it calls this — and it is the
    /// only thing in the application that ever sizes or moves this window.
    /// </summary>
    public void PlaceOn(PixelRect area, double scale)
    {
        _area = area;
        _scale = scale > 0.1 ? scale : 1;
        _placed = true;

        var height = Math.Clamp(_area.Height / _scale * HeightShare, MinHeight, MaxHeight);
        if (!Width.Equals(WindowWidth)) Width = WindowWidth;
        if (!Height.Equals(height)) Height = height;
        Petal.Height = Math.Max(1, height - (VerticalInset * 2));
        Reposition();
        ApplyShape();
    }

    /// <summary>
    /// Reaches the right-hand edge and is centred on it, measured from what the window actually
    /// is. <see cref="ClientSize"/> rather than <see cref="Window.Width"/> because the platform
    /// has the last word on how narrow a window may be, and a petal placed by the number it
    /// asked for rather than the number it got is a petal hanging off the side of the monitor.
    ///
    /// A position is a setter with a compositor round trip behind it, so it refuses to assign a
    /// point the window is already at.
    /// </summary>
    private void Reposition()
    {
        if (!_placed) return;
        var width = ClientSize.Width > 1 ? ClientSize.Width : Width;
        var height = ClientSize.Height > 1 ? ClientSize.Height : Height;
        var target = new PixelPoint(
            _area.Right - (int)Math.Ceiling(width * _scale),
            _area.Y + Math.Max(0, (_area.Height - (int)Math.Ceiling(height * _scale)) / 2));
        if (Position != target) Position = target;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsLiveProperty) ApplyShape();
        // The size the window actually got, which is not always the size it was asked for, and
        // which can land a frame after the request.
        else if (change.Property == ClientSizeProperty) Reposition();
    }

    /// <summary>
    /// The two states, which differ in width and in whether the clock is in
    /// there. Nothing about the window is named in here on purpose.
    /// </summary>
    private void ApplyShape()
    {
        if (!_placed) return;
        var live = IsLive && !_suppressed;
        var width = live ? LiveCapsuleWidth : RestCapsuleWidth;
        Petal.Width = width;
        Petal.CornerRadius = new CornerRadius(live ? 12 : width / 2);
        Petal.Classes.Set("live", live);
        Readout.IsVisible = live;
        Readout.Classes.Set("on", live);
    }

    // ---- The pointer -----------------------------------------------------------------------

    private void OnPointerOverChanged(bool over)
    {
        if (over)
        {
            Petal.Classes.Set("hover", true);
            HoverStarted?.Invoke(this, EventArgs.Empty);
            return;
        }

        Petal.Classes.Set("hover", false);
        HoverEnded?.Invoke(this, EventArgs.Empty);
    }

    private void Reach_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Nothing inside the petal is pressable, but the reading is a control the framework will
        // happily make the source of an event, and a press that began on something with a button
        // in it is not a press on the petal.
        if (e.Source is Visual source && source.FindAncestorOfType<Button>() is not null) return;

        e.Handled = true;

        switch (e.GetCurrentPoint(this).Properties.PointerUpdateKind)
        {
            case PointerUpdateKind.LeftButtonPressed:
                ToggleRequested?.Invoke(this, EventArgs.Empty);
                break;
            case PointerUpdateKind.RightButtonPressed:
                PeekRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

}
