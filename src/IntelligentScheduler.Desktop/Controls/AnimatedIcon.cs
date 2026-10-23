using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace IntelligentScheduler.Desktop.Controls;

public enum MotionIconKind
{
    Auto,
    Settings,
    Sliders,
    Palette,
    Bot,
    Brain,
    Calendar,
    Chart,
    Sparkles,
    ChevronRight,
    ChevronLeft,
    ChevronDown,
    ChevronUp,
    Plus,
    Check,
    Edit,
    Menu,
    Copy,
    Delete,
    Close,
    Send,
    Attach,
    ArrowUp,
    Clock,
    Play,
    Pause,
    Stop,
    Power,
    History,
    Search,
    Pin,
    Refresh,
    Tool,
    Flame,
    Timer,
    Skip,
    Partial,
    ArrowLeft,
    ArrowRight,
    Shift,
    Moon,
    Sun,
    Help,
    Expand,
    Collapse,
    Steps
}

/// <summary>
/// Native Avalonia port of the MIT-licensed lucide-animated navigation icons.
/// Supported icons animate their own vector parts when the host button is hovered;
/// unsupported glyphs keep using the bundled Phosphor fallback.
/// </summary>
public sealed class AnimatedIcon : Control
{
    private const double StrokeWidth = 2.25;
    private static readonly Dictionary<string, Geometry> GeometryCache = new(StringComparer.Ordinal);
    private static readonly List<WeakReference<AnimatedIcon>> LiveIcons = [];
    private readonly List<Visual> _visibilityHosts = [];

    public static readonly StyledProperty<string?> DataProperty =
        AvaloniaProperty.Register<AnimatedIcon, string?>(nameof(Data));

    public static readonly StyledProperty<MotionIconKind> MotionProperty =
        AvaloniaProperty.Register<AnimatedIcon, MotionIconKind>(nameof(Motion));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextBlock.ForegroundProperty.AddOwner<AnimatedIcon>();

    public static readonly StyledProperty<double> FontSizeProperty =
        TextBlock.FontSizeProperty.AddOwner<AnimatedIcon>();

    private readonly DispatcherTimer _animationTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private Control? _hoverHost;
    private DateTimeOffset _animationStarted;
    private double _progress;
    private bool _isHovering;

    public string? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public MotionIconKind Motion
    {
        get => GetValue(MotionProperty);
        set => SetValue(MotionProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    static AnimatedIcon()
    {
        AffectsRender<AnimatedIcon>(ForegroundProperty, DataProperty, MotionProperty);
        DataProperty.Changed.AddClassHandler<AnimatedIcon>((icon, _) => icon.RefreshVisual());
        MotionProperty.Changed.AddClassHandler<AnimatedIcon>((icon, _) => icon.RefreshVisual());
        ForegroundProperty.Changed.AddClassHandler<AnimatedIcon>((icon, _) => icon.InvalidateVisual());
    }

    public AnimatedIcon()
    {
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        FontSize = 14;
        _animationTimer.Tick += OnAnimationTick;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _hoverHost = this.FindAncestorOfType<Button>();
        _hoverHost ??= this;
        _hoverHost.PointerEntered += OnHostPointerEntered;
        _hoverHost.PointerExited += OnHostPointerExited;
        _visibilityHosts.Clear();
        _visibilityHosts.Add(this);
        _visibilityHosts.AddRange(this.GetVisualAncestors());
        foreach (var host in _visibilityHosts)
            host.PropertyChanged += OnVisibilityHostPropertyChanged;
        lock (LiveIcons) LiveIcons.Add(new WeakReference<AnimatedIcon>(this));
        RefreshVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_hoverHost is not null)
        {
            _hoverHost.PointerEntered -= OnHostPointerEntered;
            _hoverHost.PointerExited -= OnHostPointerExited;
            _hoverHost = null;
        }
        foreach (var host in _visibilityHosts)
            host.PropertyChanged -= OnVisibilityHostPropertyChanged;
        _visibilityHosts.Clear();
        ResetAnimation();
        lock (LiveIcons)
        {
            for (var index = LiveIcons.Count - 1; index >= 0; index--)
                if (!LiveIcons[index].TryGetTarget(out var icon) || ReferenceEquals(icon, this))
                    LiveIcons.RemoveAt(index);
        }
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Immediately drops stale animation frames after a live theme or data-palette change.</summary>
    public static void RefreshAll()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RefreshAll, DispatcherPriority.Render);
            return;
        }

        lock (LiveIcons)
        {
            for (var index = LiveIcons.Count - 1; index >= 0; index--)
            {
                if (!LiveIcons[index].TryGetTarget(out var icon))
                {
                    LiveIcons.RemoveAt(index);
                    continue;
                }
                icon.ResetAnimation();
            }
        }

        // Dynamic resources and brush transitions settle on the next render pass.
        Dispatcher.UIThread.Post(() =>
        {
            lock (LiveIcons)
                foreach (var reference in LiveIcons)
                    if (reference.TryGetTarget(out var icon)) icon.InvalidateVisual();
        }, DispatcherPriority.Render);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WidthProperty && change.NewValue is double width && double.IsFinite(width) && width > 0)
            FontSize = width;
        else if (change.Property == HeightProperty && double.IsNaN(Width) &&
                 change.NewValue is double height && double.IsFinite(height) && height > 0)
            FontSize = height;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(Width) && Width > 0 ? Width : double.NaN;
        var height = double.IsFinite(Height) && Height > 0 ? Height : double.NaN;
        var side = double.IsFinite(width) ? width : double.IsFinite(height) ? height : FontSize;
        return new Size(side, side);
    }

    public override void Render(DrawingContext context)
    {
        var kind = ResolveKind();
        if (kind == MotionIconKind.Auto)
        {
            DrawFallbackGlyph(context);
            return;
        }

        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0 || Foreground is null) return;

        var scale = size / 24d;
        var offsetX = (Bounds.Width - size) / 2d;
        var offsetY = (Bounds.Height - size) / 2d;
        var matrix = Matrix.CreateScale(scale, scale).Append(Matrix.CreateTranslation(offsetX, offsetY));
        using var pushed = context.PushTransform(matrix);
        var pen = new Pen(Foreground, StrokeWidth, null, PenLineCap.Round, PenLineJoin.Round, 10);

        switch (kind)
        {
            case MotionIconKind.Settings: DrawSettings(context, pen); break;
            case MotionIconKind.Sliders: DrawSliders(context, pen); break;
            case MotionIconKind.Palette: DrawPalette(context, pen); break;
            case MotionIconKind.Bot: DrawBot(context, pen); break;
            case MotionIconKind.Brain: DrawBrain(context); break;
            case MotionIconKind.Calendar: DrawCalendar(context, pen); break;
            case MotionIconKind.Chart: DrawChart(context, pen); break;
            case MotionIconKind.Sparkles: DrawSparkles(context, pen); break;
            case MotionIconKind.ChevronRight: DrawChevron(context, pen, 1, 0); break;
            case MotionIconKind.ChevronLeft: DrawChevron(context, pen, -1, 0); break;
            case MotionIconKind.ChevronDown: DrawChevron(context, pen, 0, 1); break;
            case MotionIconKind.ChevronUp: DrawChevron(context, pen, 0, -1); break;
            case MotionIconKind.Plus: DrawPlus(context, pen); break;
            case MotionIconKind.Check: DrawCheck(context, pen); break;
            case MotionIconKind.Edit: DrawEdit(context, pen); break;
            case MotionIconKind.Menu: DrawMenu(context, pen); break;
            case MotionIconKind.Copy: DrawCopy(context, pen); break;
            case MotionIconKind.Delete: DrawDelete(context, pen); break;
            case MotionIconKind.Close: DrawClose(context, pen); break;
            case MotionIconKind.Send: DrawSend(context, pen); break;
            case MotionIconKind.Attach: DrawAttach(context, pen); break;
            case MotionIconKind.ArrowUp: DrawArrowUp(context, pen); break;
            case MotionIconKind.Clock: DrawClock(context, pen); break;
            case MotionIconKind.Play: DrawPlay(context, pen); break;
            case MotionIconKind.Pause: DrawPause(context, pen); break;
            case MotionIconKind.Stop: DrawStop(context, pen); break;
            case MotionIconKind.Power: DrawPower(context, pen); break;
            case MotionIconKind.History: DrawHistory(context, pen); break;
            case MotionIconKind.Search: DrawSearch(context, pen); break;
            case MotionIconKind.Pin: DrawPin(context, pen); break;
            case MotionIconKind.Refresh: DrawRefresh(context, pen); break;
            case MotionIconKind.Tool: DrawTool(context, pen); break;
            case MotionIconKind.Flame: DrawFlame(context, pen); break;
            case MotionIconKind.Timer: DrawTimer(context, pen); break;
            case MotionIconKind.Skip: DrawSkip(context, pen); break;
            case MotionIconKind.Partial: DrawPartial(context, pen); break;
            case MotionIconKind.ArrowLeft: DrawHorizontalArrow(context, pen, -1); break;
            case MotionIconKind.ArrowRight: DrawHorizontalArrow(context, pen, 1); break;
            case MotionIconKind.Shift: DrawShift(context, pen); break;
            case MotionIconKind.Moon: DrawMoon(context, pen); break;
            case MotionIconKind.Sun: DrawSun(context, pen); break;
            case MotionIconKind.Help: DrawHelp(context, pen); break;
            case MotionIconKind.Expand: DrawCornerArrows(context, pen, outward: true); break;
            case MotionIconKind.Collapse: DrawCornerArrows(context, pen, outward: false); break;
            case MotionIconKind.Steps: DrawSteps(context, pen); break;
        }
    }

    private void RefreshVisual()
    {
        _progress = 0;
        InvalidateVisual();
    }

    private void DrawFallbackGlyph(DrawingContext context)
    {
        if (string.IsNullOrEmpty(Data) || Foreground is null) return;
        var family = FontFamily.Parse("avares://DAYDRIVE/Assets/Icons/Phosphor#Phosphor");
        var text = new FormattedText(Data, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(family), FontSize, Foreground);
        context.DrawText(text, new Point((Bounds.Width - text.Width) / 2, (Bounds.Height - text.Height) / 2));
    }

    private MotionIconKind ResolveKind()
    {
        if (Motion != MotionIconKind.Auto) return Motion;
        return Data switch
        {
            "\uE272" => MotionIconKind.Settings,
            "\uE434" => MotionIconKind.Sliders,
            "\uE168" => MotionIconKind.Bot,
            "\uE74E" => MotionIconKind.Brain,
            "\uE7B4" => MotionIconKind.Calendar,
            "\uE150" => MotionIconKind.Chart,
            "\uE6A2" => MotionIconKind.Sparkles,
            "\uE13A" => MotionIconKind.ChevronRight,
            "\uE138" => MotionIconKind.ChevronLeft,
            "\uE136" => MotionIconKind.ChevronDown,
            "\uE13C" => MotionIconKind.ChevronUp,
            "\uE3D4" => MotionIconKind.Plus,
            "\uE182" => MotionIconKind.Check,
            "\uE3B4" => MotionIconKind.Edit,
            "\uE34C" => MotionIconKind.Edit,
            "\uE1FE" => MotionIconKind.Menu,
            "\uE1CA" => MotionIconKind.Copy,
            "\uE4A6" => MotionIconKind.Delete,
            "\uE4F6" => MotionIconKind.Close,
            "\uE398" => MotionIconKind.Send,
            "\uE39A" => MotionIconKind.Attach,
            "\uE08E" => MotionIconKind.ArrowUp,
            "\uE19A" => MotionIconKind.Clock,
            "\uE3D0" => MotionIconKind.Play,
            "\uE46C" => MotionIconKind.Stop,
            "\uE3DA" => MotionIconKind.Power,
            "\uE1A0" => MotionIconKind.History,
            "\uE30C" => MotionIconKind.Search,
            "\uE3E2" => MotionIconKind.Pin,
            "\uE094" => MotionIconKind.Refresh,
            "\uE5D4" => MotionIconKind.Tool,
            "\uE242" => MotionIconKind.Flame,
            "\uE492" => MotionIconKind.Timer,
            "\uE5A6" => MotionIconKind.Skip,
            _ => MotionIconKind.Auto
        };
    }

    private bool MotionAllowed()
        => ResolveKind() != MotionIconKind.Auto &&
           this.FindAncestorOfType<Window>()?.Classes.Contains("reduceMotion") != true;

    private void OnHostPointerEntered(object? sender, PointerEventArgs e)
    {
        if (!MotionAllowed()) return;
        _isHovering = true;
        _progress = 0;
        _animationStarted = DateTimeOffset.UtcNow;
        _animationTimer.Start();
        InvalidateVisual();
    }

    private void OnHostPointerExited(object? sender, PointerEventArgs e)
        => ResetAnimation();

    private void OnVisibilityHostPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty && e.NewValue is false) ResetAnimation();
    }

    private void ResetAnimation()
    {
        _isHovering = false;
        _animationTimer.Stop();
        _progress = 0;
        InvalidateVisual();
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        if (!_isHovering || !MotionAllowed())
        {
            _animationTimer.Stop();
            _progress = 0;
            InvalidateVisual();
            return;
        }

        var isLooping = ResolveKind() == MotionIconKind.Brain;
        var duration = isLooping ? 1400d : 760d;
        var elapsed = (DateTimeOffset.UtcNow - _animationStarted).TotalMilliseconds;
        if (isLooping)
            _progress = elapsed % duration / duration;
        else
        {
            _progress = Math.Min(1, elapsed / duration);
            if (_progress >= 1) _animationTimer.Stop();
        }
        InvalidateVisual();
    }

    private static double Ease(double value) => 1 - Math.Pow(1 - Math.Clamp(value, 0, 1), 3);
    private static double Lerp(double from, double to, double value) => from + (to - from) * value;
    private static double Pulse(double value) => Math.Sin(Math.Clamp(value, 0, 1) * Math.PI);

    private void DrawSettings(DrawingContext context, Pen pen)
    {
        var angle = Ease(_progress) * Math.PI;
        using var rotated = context.PushTransform(Matrix.CreateRotation(angle, new Point(12, 12)));
        Path(context, pen, "M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2z");
        context.DrawEllipse(null, pen, new Point(12, 12), 3, 3);
    }

    private void DrawSliders(DrawingContext context, Pen pen)
    {
        var p = Ease(_progress);
        DrawSlider(context, pen, 4, Lerp(14, 9, p));
        DrawSlider(context, pen, 12, Lerp(8, 14, p));
        DrawSlider(context, pen, 20, Lerp(16, 8, p));
    }

    private static void DrawSlider(DrawingContext context, Pen pen, double y, double knobX)
    {
        context.DrawLine(pen, new Point(3, y), new Point(knobX - 2, y));
        context.DrawLine(pen, new Point(knobX + 2, y), new Point(21, y));
        context.DrawLine(pen, new Point(knobX, y - 2), new Point(knobX, y + 2));
    }

    private void DrawPalette(DrawingContext context, Pen pen)
    {
        var lift = Pulse(_progress) * -0.7;
        using var moved = context.PushTransform(Matrix.CreateTranslation(0, lift));
        Path(context, pen, "M12 2a1 1 0 0 0 0 20h.25a1.75 1.75 0 0 0 1.4-2.8l-.3-.4a1.75 1.75 0 0 1 1.4-2.8H17a5 5 0 0 0 5-5 10 9 0 0 0-10-9z");
        var dots = new[] { new Point(6.5, 12.5), new Point(8.5, 7.5), new Point(13.5, 6.5), new Point(17.5, 10.5) };
        for (var i = 0; i < dots.Length; i++)
        {
            var local = Math.Clamp((_progress - i * .1) / .55, 0, 1);
            var radius = .65 * (.7 + .45 * Pulse(local));
            context.DrawEllipse(Foreground, null, dots[i], radius, radius);
        }
    }

    private void DrawBot(DrawingContext context, Pen pen)
    {
        var wobble = Math.Sin(_progress * Math.PI * 4) * Pulse(_progress) * .045;
        using var rotated = context.PushTransform(Matrix.CreateRotation(wobble, new Point(12, 12)));
        Path(context, pen, "M12 6V2H8 M2 12h2 M20 12h2");
        Path(context, pen, "M20 16a2 2 0 0 1-2 2H8.828a2 2 0 0 0-1.414.586l-2.202 2.202A.71.71 0 0 1 4 20.286V8a2 2 0 0 1 2-2h12a2 2 0 0 1 2 2z");
        var blinkLeft = 1 - Pulse(Math.Clamp((_progress - .08) / .35, 0, 1)) * .9;
        var blinkRight = 1 - Pulse(Math.Clamp((_progress - .18) / .35, 0, 1)) * .9;
        context.DrawLine(pen, new Point(9, 12 - blinkLeft), new Point(9, 12 + blinkLeft));
        context.DrawLine(pen, new Point(15, 12 - blinkRight), new Point(15, 12 + blinkRight));
    }

    private void DrawBrain(DrawingContext context)
    {
        var pulse = (Math.Sin(_progress * Math.PI * 2) + 1) / 2;
        var scale = 1 + pulse * .045;
        var scaleAroundCenter = Matrix.CreateTranslation(-12, -12)
            .Append(Matrix.CreateScale(scale, scale))
            .Append(Matrix.CreateTranslation(12, 12));
        using var scaled = context.PushTransform(scaleAroundCenter);
        var livePen = new Pen(Foreground, StrokeWidth + pulse * .22, null, PenLineCap.Round, PenLineJoin.Round, 10);
        Path(context, livePen, "M12 18V5 M15 13a4.17 4.17 0 0 1-3-4 4.17 4.17 0 0 1-3 4");
        Path(context, livePen, "M12 5A3 3 0 1 1 17.598 6.5 M12 5A3 3 0 1 0 6.402 6.5");
        Path(context, livePen, "M17.997 5.125a4 4 0 0 1 2.526 5.77 M18 18a4 4 0 0 0 2-7.464");
        Path(context, livePen, "M19.967 17.483A4 4 0 1 1 12 18a4 4 0 1 1-7.967-.517");
        Path(context, livePen, "M6 18a4 4 0 0 1-2-7.464 M6.003 5.125a4 4 0 0 0-2.526 5.77");
    }

    private void DrawCalendar(DrawingContext context, Pen pen)
    {
        Path(context, pen, "M8 2v4 M16 2v4 M3 10h18");
        context.DrawRectangle(null, pen, new RoundedRect(new Rect(3, 4, 18, 18), 2), default);
        var dots = new[]
        {
            new Point(8, 14), new Point(12, 14), new Point(16, 14),
            new Point(8, 18), new Point(12, 18), new Point(16, 18)
        };
        for (var i = 0; i < dots.Length; i++)
        {
            var phase = Math.Clamp((_progress - i * .07) / .38, 0, 1);
            var opacity = 1 - Pulse(phase) * .72;
            using var faded = context.PushOpacity(opacity);
            context.DrawEllipse(Foreground, null, dots[i], .9, .9);
        }
    }

    private void DrawChart(DrawingContext context, Pen pen)
    {
        var heights = new[] { 4d, 10d, 16d };
        var xs = new[] { 6d, 12d, 18d };
        for (var i = 0; i < xs.Length; i++)
        {
            var local = Math.Clamp((_progress - i * .1) / .7, 0, 1);
            var amount = _progress <= 0 ? 1 : Ease(local);
            context.DrawLine(pen, new Point(xs[i], 20), new Point(xs[i], 20 - heights[i] * amount));
        }
    }

    private void DrawSparkles(DrawingContext context, Pen pen)
    {
        var lift = -Pulse(_progress);
        using (context.PushTransform(Matrix.CreateTranslation(0, lift)))
        {
            var geometry = GetGeometry("M9.937 15.5A2 2 0 0 0 8.5 14.063l-6.135-1.582a.5.5 0 0 1 0-.962L8.5 9.936A2 2 0 0 0 9.937 8.5l1.582-6.135a.5.5 0 0 1 .963 0L14.063 8.5A2 2 0 0 0 15.5 9.937l6.135 1.581a.5.5 0 0 1 0 .964L15.5 14.063a2 2 0 0 0-1.437 1.437l-1.582 6.135a.5.5 0 0 1-.963 0z");
            context.DrawGeometry(null, pen, geometry);
            var fillOpacity = .22 * Pulse(_progress);
            using var fill = context.PushOpacity(fillOpacity);
            context.DrawGeometry(Foreground, null, geometry);
        }
        var opacity = .45 + .55 * Math.Abs(Math.Cos(_progress * Math.PI * 2));
        using var blink = context.PushOpacity(opacity);
        Path(context, pen, "M20 3v4 M22 5h-4 M4 17v2 M5 18H3");
    }

    private void DrawChevron(DrawingContext context, Pen pen, double directionX, double directionY)
    {
        var distance = Pulse(_progress) * 1.8;
        using var moved = context.PushTransform(Matrix.CreateTranslation(directionX * distance, directionY * distance));
        if (directionX > 0) Path(context, pen, "m9 18 6-6-6-6");
        else if (directionX < 0) Path(context, pen, "m15 18-6-6 6-6");
        else if (directionY > 0) Path(context, pen, "m6 9 6 6 6-6");
        else Path(context, pen, "m6 15 6-6 6 6");
    }

    private void DrawPlus(DrawingContext context, Pen pen)
    {
        using var rotated = context.PushTransform(Matrix.CreateRotation(Ease(_progress) * Math.PI * 2, new Point(12, 12)));
        Path(context, pen, "M5 12h14 M12 5v14");
    }

    private void DrawCheck(DrawingContext context, Pen pen)
    {
        using var scaled = context.PushTransform(ScaleAround(1 + Pulse(_progress) * .13));
        Path(context, pen, "m4 12 5 5L20 6");
    }

    private void DrawEdit(DrawingContext context, Pen pen)
    {
        var shift = Pulse(_progress) * .9;
        using var moved = context.PushTransform(Matrix.CreateTranslation(shift, -shift));
        Path(context, pen, "M12 20h9 M16.5 3.5a2.12 2.12 0 0 1 3 3L8 18l-5 1 1-5z");
    }

    private void DrawMenu(DrawingContext context, Pen pen)
    {
        var spread = Pulse(_progress) * 2;
        context.DrawLine(pen, new Point(4 + spread, 6), new Point(20 - spread, 6));
        context.DrawLine(pen, new Point(4, 12), new Point(20, 12));
        context.DrawLine(pen, new Point(4 + spread, 18), new Point(20 - spread, 18));
    }

    private void DrawCopy(DrawingContext context, Pen pen)
    {
        var shift = Pulse(_progress) * 1.1;
        using (context.PushTransform(Matrix.CreateTranslation(-shift, -shift)))
            context.DrawRectangle(null, pen, new RoundedRect(new Rect(3, 3, 13, 13), 2), default);
        using (context.PushTransform(Matrix.CreateTranslation(shift, shift)))
            context.DrawRectangle(null, pen, new RoundedRect(new Rect(8, 8, 13, 13), 2), default);
    }

    private void DrawDelete(DrawingContext context, Pen pen)
    {
        Path(context, pen, "M6 7l1 14h10l1-14 M10 11v6 M14 11v6");
        var lift = Pulse(_progress) * -1.6;
        using var moved = context.PushTransform(Matrix.CreateTranslation(0, lift));
        Path(context, pen, "M3 6h18 M9 6V3h6v3");
    }

    private void DrawClose(DrawingContext context, Pen pen)
    {
        using var rotated = context.PushTransform(Matrix.CreateRotation(Ease(_progress) * Math.PI / 2, new Point(12, 12)));
        Path(context, pen, "M6 6l12 12 M18 6 6 18");
    }

    private void DrawSend(DrawingContext context, Pen pen)
    {
        var fly = Pulse(_progress) * 1.4;
        using var moved = context.PushTransform(Matrix.CreateTranslation(fly, -fly));
        Path(context, pen, "M22 2 11 13 M22 2l-7 20-4-9-9-4z");
    }

    private void DrawAttach(DrawingContext context, Pen pen)
    {
        var swing = Math.Sin(_progress * Math.PI * 2) * Pulse(_progress) * .09;
        using var rotated = context.PushTransform(Matrix.CreateRotation(swing, new Point(12, 12)));
        Path(context, pen, "m21.44 11.05-9.19 9.19a6 6 0 0 1-8.49-8.49l9.19-9.19a4 4 0 0 1 5.66 5.66l-9.2 9.19a2 2 0 0 1-2.83-2.83l8.49-8.48");
    }

    private void DrawArrowUp(DrawingContext context, Pen pen)
    {
        using var moved = context.PushTransform(Matrix.CreateTranslation(0, -Pulse(_progress) * 1.7));
        Path(context, pen, "M12 19V5 M5 12l7-7 7 7");
    }

    private void DrawClock(DrawingContext context, Pen pen)
    {
        context.DrawEllipse(null, pen, new Point(12, 12), 9, 9);
        using var rotated = context.PushTransform(Matrix.CreateRotation(Ease(_progress) * Math.PI * 2, new Point(12, 12)));
        Path(context, pen, "M12 7v5l3 2");
    }

    private void DrawPlay(DrawingContext context, Pen pen)
    {
        using var scaled = context.PushTransform(ScaleAround(1 + Pulse(_progress) * .12));
        var geometry = GetGeometry("M7 4v16l13-8z");
        context.DrawGeometry(null, pen, geometry);
        using var tint = context.PushOpacity(.15 * Pulse(_progress));
        context.DrawGeometry(Foreground, null, geometry);
    }

    /// <summary>
    /// Two held bars. They breathe apart rather than pulsing in size, so a paused session reads
    /// as something waiting to move rather than something being pressed.
    /// </summary>
    private void DrawPause(DrawingContext context, Pen pen)
    {
        // Drawn rather than parsed: the bars move every frame, and a geometry cache keyed by
        // the path string would collect a fresh entry for each position they pass through.
        var spread = Pulse(_progress) * .7;
        context.DrawLine(pen, new Point(9 - spread, 5), new Point(9 - spread, 19));
        context.DrawLine(pen, new Point(15 + spread, 5), new Point(15 + spread, 19));
    }

    private void DrawStop(DrawingContext context, Pen pen)
    {
        using var scaled = context.PushTransform(ScaleAround(1 - Pulse(_progress) * .12));
        context.DrawRectangle(null, pen, new RoundedRect(new Rect(5, 5, 14, 14), 2), default);
    }

    private void DrawPower(DrawingContext context, Pen pen)
    {
        using var scaled = context.PushTransform(ScaleAround(1 + Pulse(_progress) * .08));
        Path(context, pen, "M12 2v10 M18.36 5.64a9 9 0 1 1-12.72 0");
    }

    private void DrawHistory(DrawingContext context, Pen pen)
    {
        using var rotated = context.PushTransform(Matrix.CreateRotation(-Pulse(_progress) * .16, new Point(12, 12)));
        Path(context, pen, "M3 12a9 9 0 1 0 3-6.7L3 8 M3 3v5h5 M12 7v5l4 2");
    }

    private void DrawSearch(DrawingContext context, Pen pen)
    {
        using var scaled = context.PushTransform(ScaleAround(1 + Pulse(_progress) * .08));
        context.DrawEllipse(null, pen, new Point(10.5, 10.5), 6.5, 6.5);
        context.DrawLine(pen, new Point(15.2, 15.2), new Point(21, 21));
    }

    private void DrawPin(DrawingContext context, Pen pen)
    {
        var wobble = Math.Sin(_progress * Math.PI * 4) * Pulse(_progress) * .07;
        using var rotated = context.PushTransform(Matrix.CreateRotation(wobble, new Point(12, 12)));
        Path(context, pen, "M12 17v5 M5 17h14 M6 3h12l-2 7 3 3H5l3-3z");
    }

    private void DrawRefresh(DrawingContext context, Pen pen)
    {
        using var rotated = context.PushTransform(Matrix.CreateRotation(Ease(_progress) * Math.PI * 2, new Point(12, 12)));
        Path(context, pen, "M20 6v5h-5 M4 18v-5h5 M19 11a7 7 0 0 0-12-4L4 11 M5 13a7 7 0 0 0 12 4l3-4");
    }

    private void DrawTool(DrawingContext context, Pen pen)
    {
        using var rotated = context.PushTransform(Matrix.CreateRotation(-Pulse(_progress) * .18, new Point(12, 12)));
        Path(context, pen, "M14.7 6.3a4 4 0 0 0-5-5L7 4l3 3-3 3-3-3-2.7 2.7a4 4 0 0 0 5 5l6.6 6.6a2.1 2.1 0 0 0 3-3z");
    }

    private void DrawFlame(DrawingContext context, Pen pen)
    {
        var scaleY = 1 + Pulse(_progress) * .1;
        var transform = Matrix.CreateTranslation(0, -20)
            .Append(Matrix.CreateScale(1, scaleY))
            .Append(Matrix.CreateTranslation(0, 20));
        using var scaled = context.PushTransform(transform);
        var geometry = GetGeometry("M12 22c4.4 0 8-3.6 8-8 0-3-1.5-5.5-4-8-1 3-3 4-5 5-1-2-1-4 0-7-4 3-6 7-6 11 0 4.4 3.6 8 8 8z M9 18c0-2 1-3 3-5 1 2 3 3 3 5a3 3 0 0 1-6 0z");
        context.DrawGeometry(null, pen, geometry);
        using var tint = context.PushOpacity(.12 * Pulse(_progress));
        context.DrawGeometry(Foreground, null, geometry);
    }

    private void DrawTimer(DrawingContext context, Pen pen)
    {
        Path(context, pen, "M9 2h6 M12 14l3-3");
        context.DrawEllipse(null, pen, new Point(12, 13.5), 8.5, 8.5);
        using var rotated = context.PushTransform(Matrix.CreateRotation(Ease(_progress) * Math.PI * 2, new Point(12, 13.5)));
        context.DrawLine(pen, new Point(12, 13.5), new Point(12, 8));
    }

    private void DrawSkip(DrawingContext context, Pen pen)
    {
        using var moved = context.PushTransform(Matrix.CreateTranslation(Pulse(_progress) * 1.5, 0));
        Path(context, pen, "M5 4v16l11-8z M19 5v14");
    }

    private void DrawPartial(DrawingContext context, Pen pen)
    {
        using var scaled = context.PushTransform(ScaleAround(1 + Pulse(_progress) * .07));
        context.DrawEllipse(null, pen, new Point(12, 12), 9, 9);
        var half = GetGeometry("M12 3a9 9 0 0 0 0 18z");
        using var fill = context.PushOpacity(.22 + Pulse(_progress) * .16);
        context.DrawGeometry(Foreground, null, half);
    }

    private void DrawHorizontalArrow(DrawingContext context, Pen pen, double direction)
    {
        using var moved = context.PushTransform(Matrix.CreateTranslation(direction * Pulse(_progress) * 1.5, 0));
        if (direction < 0) Path(context, pen, "M20 12H5 M10 6l-6 6 6 6");
        else Path(context, pen, "M4 12h15 M14 6l6 6-6 6");
    }

    /// <summary>
    /// A checklist: the first line ticked, the two under it still waiting. Under the hand the tick
    /// lands again and the waiting lines reach a little further, the next thing to be done.
    /// </summary>
    private void DrawSteps(DrawingContext context, Pen pen)
    {
        var pulse = Pulse(_progress);
        using (context.PushTransform(Matrix.CreateTranslation(0, -pulse * 0.8)))
            Path(context, pen, "M3.5 6.5l1.8 1.8 3.4-3.6");
        context.DrawLine(pen, new Point(12, 6.5), new Point(20.5, 6.5));
        var reach = pulse * 1.5;
        context.DrawEllipse(null, pen, new Point(6, 12.5), 1.6, 1.6);
        context.DrawLine(pen, new Point(12, 12.5), new Point(19 + reach, 12.5));
        context.DrawEllipse(null, pen, new Point(6, 18.5), 1.6, 1.6);
        context.DrawLine(pen, new Point(12, 18.5), new Point(17 + reach, 18.5));
    }

    private void DrawShift(DrawingContext context, Pen pen)
    {
        var spread = Pulse(_progress) * 1.3;
        using (context.PushTransform(Matrix.CreateTranslation(spread, 0)))
            Path(context, pen, "M4 7h15 M15 3l4 4-4 4");
        using (context.PushTransform(Matrix.CreateTranslation(-spread, 0)))
            Path(context, pen, "M20 17H5 M9 13l-4 4 4 4");
    }

    /// <summary>
    /// The two diagonal corners of a window being pushed apart or pulled together. One method
    /// for both because they are the same picture read in opposite directions, and the motion is
    /// the direction: the corners drift the way the press is about to take the surface.
    /// </summary>
    private void DrawCornerArrows(DrawingContext context, Pen pen, bool outward)
    {
        var drift = Pulse(_progress) * 1.35 * (outward ? 1 : -1);
        using (context.PushTransform(Matrix.CreateTranslation(drift, -drift)))
            Path(context, pen, outward ? "M15 3h6v6 M21 3l-7 7" : "M20 10h-6V4 M14 10l7-7");
        using (context.PushTransform(Matrix.CreateTranslation(-drift, drift)))
            Path(context, pen, outward ? "M9 21H3v-6 M3 21l7-7" : "M4 14h6v6 M3 21l7-7");
    }

    private void DrawMoon(DrawingContext context, Pen pen)
    {
        var drift = Pulse(_progress) * .8;
        using var moved = context.PushTransform(Matrix.CreateTranslation(drift * .35, -drift));
        Path(context, pen, "M20.4 14.7A8.5 8.5 0 0 1 9.3 3.6 9 9 0 1 0 20.4 14.7z");
    }

    private void DrawSun(DrawingContext context, Pen pen)
    {
        context.DrawEllipse(null, pen, new Point(12, 12), 3.6, 3.6);
        using var rotated = context.PushTransform(Matrix.CreateRotation(Ease(_progress) * .36, new Point(12, 12)));
        for (var index = 0; index < 8; index++)
        {
            var angle = index * Math.PI / 4;
            var extension = Pulse(_progress) * .7;
            var inner = 7.1;
            var outer = 9 + extension;
            context.DrawLine(pen,
                new Point(12 + Math.Cos(angle) * inner, 12 + Math.Sin(angle) * inner),
                new Point(12 + Math.Cos(angle) * outer, 12 + Math.Sin(angle) * outer));
        }
    }

    private void DrawHelp(DrawingContext context, Pen pen)
    {
        using var scaled = context.PushTransform(ScaleAround(1 + Pulse(_progress) * .045));
        context.DrawEllipse(null, pen, new Point(12, 12), 9, 9);
        using (context.PushTransform(Matrix.CreateTranslation(0, -Pulse(_progress) * .7)))
            Path(context, pen, "M9.7 9a2.5 2.5 0 0 1 4.8 1c0 1.8-2.5 2.1-2.5 4");
        context.DrawEllipse(Foreground, null, new Point(12, 17.3), 1.05, 1.05);
    }

    private static Matrix ScaleAround(double scale)
        => Matrix.CreateTranslation(-12, -12)
            .Append(Matrix.CreateScale(scale, scale))
            .Append(Matrix.CreateTranslation(12, 12));

    private static void Path(DrawingContext context, Pen pen, string data)
        => context.DrawGeometry(null, pen, GetGeometry(data));

    private static Geometry GetGeometry(string data)
    {
        lock (GeometryCache)
        {
            if (!GeometryCache.TryGetValue(data, out var geometry))
            {
                geometry = Geometry.Parse(data);
                GeometryCache[data] = geometry;
            }
            return geometry;
        }
    }
}
