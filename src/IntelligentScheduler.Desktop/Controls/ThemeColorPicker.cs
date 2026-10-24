using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;

namespace IntelligentScheduler.Desktop.Controls;

/// <summary>
/// Lightweight saturation/value surface with a hue strip. It renders synchronously and updates
/// one color property, avoiding the bitmap generation and event churn of the full color editor.
/// </summary>
public sealed class ThemeColorPicker : Control
{
    private const double SpectrumHeight = 146;
    private const double HueHeight = 16;
    private const double Gap = 10;
    private const double Radius = 7;

    public static readonly StyledProperty<Color> SelectedColorProperty =
        AvaloniaProperty.Register<ThemeColorPicker, Color>(
            nameof(SelectedColor), Colors.Red, defaultBindingMode: BindingMode.TwoWay);

    private PickerPart _dragging;

    static ThemeColorPicker()
    {
        AffectsRender<ThemeColorPicker>(SelectedColorProperty);
    }

    public ThemeColorPicker()
    {
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    public Color SelectedColor
    {
        get => GetValue(SelectedColorProperty);
        set => SetValue(SelectedColorProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 280 : availableSize.Width;
        return new Size(Math.Max(80, width), SpectrumHeight + Gap + HueHeight);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= 0) return;

        var spectrum = SpectrumRect;
        var hueBar = HueRect;
        var hsv = SelectedColor.ToHsv();
        var hueColor = new HsvColor(1, hsv.H, 1, 1).ToRgb();

        DrawRoundedLayer(context, new SolidColorBrush(hueColor), spectrum, Radius);
        DrawRoundedLayer(context, HorizontalGradient(Colors.White, Color.FromArgb(0, 255, 255, 255)), spectrum, Radius);
        DrawRoundedLayer(context, VerticalGradient(Color.FromArgb(0, 0, 0, 0), Colors.Black), spectrum, Radius);

        var rainbow = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.Parse("#FF3B30"), 0),
                new GradientStop(Color.Parse("#FFD60A"), 1d / 6),
                new GradientStop(Color.Parse("#34C759"), 2d / 6),
                new GradientStop(Color.Parse("#32ADE6"), 3d / 6),
                new GradientStop(Color.Parse("#0A84FF"), 4d / 6),
                new GradientStop(Color.Parse("#BF5AF2"), 5d / 6),
                new GradientStop(Color.Parse("#FF3B30"), 1)
            }
        };
        DrawRoundedLayer(context, rainbow, hueBar, HueHeight / 2);

        var selection = new Point(
            spectrum.X + (hsv.S * spectrum.Width),
            spectrum.Y + ((1 - hsv.V) * spectrum.Height));
        var hueSelection = new Point(hueBar.X + ((hsv.H / 360d) * hueBar.Width), hueBar.Center.Y);
        var shadowPen = new Pen(new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)), 4);
        var lightPen = new Pen(Brushes.White, 2);
        context.DrawEllipse(null, shadowPen, selection, 5.5, 5.5);
        context.DrawEllipse(null, lightPen, selection, 5.5, 5.5);
        context.DrawEllipse(new SolidColorBrush(hueColor), shadowPen, hueSelection, 6, 6);
        context.DrawEllipse(new SolidColorBrush(hueColor), lightPen, hueSelection, 6, 6);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var point = e.GetPosition(this);
        _dragging = SpectrumRect.Contains(point) ? PickerPart.Spectrum
            : ExpandedHueRect.Contains(point) ? PickerPart.Hue
            : PickerPart.None;
        if (_dragging == PickerPart.None) return;

        Focus();
        e.Pointer.Capture(this);
        UpdateColor(point);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging == PickerPart.None || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        UpdateColor(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging == PickerPart.None) return;
        UpdateColor(e.GetPosition(this));
        _dragging = PickerPart.None;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var hsv = SelectedColor.ToHsv();
        const double step = 0.025;
        switch (e.Key)
        {
            case Key.Left: hsv = new HsvColor(hsv.A, hsv.H, Math.Clamp(hsv.S - step, 0, 1), hsv.V); break;
            case Key.Right: hsv = new HsvColor(hsv.A, hsv.H, Math.Clamp(hsv.S + step, 0, 1), hsv.V); break;
            case Key.Up: hsv = new HsvColor(hsv.A, hsv.H, hsv.S, Math.Clamp(hsv.V + step, 0, 1)); break;
            case Key.Down: hsv = new HsvColor(hsv.A, hsv.H, hsv.S, Math.Clamp(hsv.V - step, 0, 1)); break;
            case Key.PageUp: hsv = new HsvColor(hsv.A, (hsv.H + 5) % 360, hsv.S, hsv.V); break;
            case Key.PageDown: hsv = new HsvColor(hsv.A, (hsv.H + 355) % 360, hsv.S, hsv.V); break;
            default: return;
        }

        SelectedColor = hsv.ToRgb();
        e.Handled = true;
    }

    private void UpdateColor(Point point)
    {
        var hsv = SelectedColor.ToHsv();
        if (_dragging == PickerPart.Spectrum)
        {
            var rect = SpectrumRect;
            var saturation = Math.Clamp((point.X - rect.X) / rect.Width, 0, 1);
            var value = 1 - Math.Clamp((point.Y - rect.Y) / rect.Height, 0, 1);
            SelectedColor = new HsvColor(hsv.A, hsv.H, saturation, value).ToRgb();
        }
        else if (_dragging == PickerPart.Hue)
        {
            var rect = HueRect;
            var hue = Math.Clamp((point.X - rect.X) / rect.Width, 0, 1) * 359.999;
            SelectedColor = new HsvColor(hsv.A, hue, hsv.S, hsv.V).ToRgb();
        }
    }

    private Rect SpectrumRect => new(0, 0, Bounds.Width, SpectrumHeight);
    private Rect HueRect => new(0, SpectrumHeight + Gap, Bounds.Width, HueHeight);
    private Rect ExpandedHueRect => HueRect.Inflate(5);

    private static void DrawRoundedLayer(DrawingContext context, IBrush brush, Rect rect, double radius) =>
        context.DrawRectangle(brush, null, new RoundedRect(rect, radius), default);

    private static LinearGradientBrush HorizontalGradient(Color start, Color end) => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops = { new GradientStop(start, 0), new GradientStop(end, 1) }
    };

    private static LinearGradientBrush VerticalGradient(Color start, Color end) => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(start, 0), new GradientStop(end, 1) }
    };

    private enum PickerPart { None, Spectrum, Hue }
}
