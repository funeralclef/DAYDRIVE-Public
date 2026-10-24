using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace IntelligentScheduler.Desktop.Controls;

/// <summary>
/// How long the card has left, drawn as a ring rather than said as a number.
///
/// A reminder leaves on its own, and the widget owes the user one thing about that: how soon.
/// That used to be a two-pixel hairline laid across the top of the event block — a progress bar
/// wearing a reminder's clock, which is a shape the eye reads as "loading" rather than as
/// "going". It also lived where nothing else on the card lived, over the block's own padding,
/// so it was a line floating above the words rather than part of the card.
///
/// A ring says the same thing in the same place a state light already stands, and it says it in
/// the one language a countdown actually has: a hand going round. It winds <em>down</em> — the
/// stroke starts as the whole circle and gives the circle back — so the reminder's own leaving
/// is what the drawing is of.
///
/// Nothing here animates itself. The value is stepped four times a second by the reminder queue
/// and carried between those steps by a transition on <see cref="ValueProperty"/>, which is why
/// this is a plain control with no timer in it: a widget that is off the screen most of the day
/// must not keep one warm.
/// </summary>
public sealed class RingCountdown : Control
{
    /// <summary>How much of the ring is still standing, 0..100.</summary>
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<RingCountdown, double>(nameof(Value), 100d);

    /// <summary>The stroke, in device-independent pixels.</summary>
    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<RingCountdown, double>(nameof(StrokeThickness), 2d);

    /// <summary>The part still to run.</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<RingCountdown, IBrush?>(nameof(Foreground));

    /// <summary>The circle it is running round, drawn faintly so the arc has a path to be on.</summary>
    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<RingCountdown, IBrush?>(nameof(TrackBrush));

    static RingCountdown()
        => AffectsRender<RingCountdown>(ValueProperty, StrokeThicknessProperty,
            ForegroundProperty, TrackBrushProperty);

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var side = Math.Min(Bounds.Width, Bounds.Height);
        var stroke = Math.Max(0.5, StrokeThickness);
        var radius = (side - stroke) / 2d;
        if (radius <= 0) return;

        var centre = new Point(Bounds.Width / 2d, Bounds.Height / 2d);

        if (TrackBrush is { } track)
            context.DrawEllipse(null, new Pen(track, stroke), centre, radius, radius);

        if (Foreground is not { } arc) return;
        var share = Math.Clamp(Value, 0d, 100d) / 100d;
        if (share <= 0) return;

        // Round caps, so the last sliver of a reminder is a dot on the circle rather than a
        // chipped rectangle - and so the head of the arc reads as the end of a hand.
        var pen = new Pen(arc, stroke, null, PenLineCap.Round);
        if (share >= 0.999)
        {
            context.DrawEllipse(null, pen, centre, radius, radius);
            return;
        }

        context.DrawGeometry(null, pen, BuildArc(centre, radius, share * 360d));
    }

    /// <summary>
    /// The arc itself: from twelve o'clock, clockwise, for as many degrees as are left. Drawn in
    /// two halves past the semicircle rather than with the large-arc flag, because a single
    /// figure whose start and end meet is ambiguous at exactly the moment a fresh reminder
    /// arrives - which is the one frame that must not flash.
    /// </summary>
    private static Geometry BuildArc(Point centre, double radius, double sweepDegrees)
    {
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        context.BeginFigure(PointOn(centre, radius, 0), false);
        if (sweepDegrees > 180d)
        {
            context.ArcTo(PointOn(centre, radius, 180d), new Size(radius, radius), 0,
                false, SweepDirection.Clockwise);
            context.ArcTo(PointOn(centre, radius, sweepDegrees), new Size(radius, radius), 0,
                false, SweepDirection.Clockwise);
        }
        else
        {
            context.ArcTo(PointOn(centre, radius, sweepDegrees), new Size(radius, radius), 0,
                false, SweepDirection.Clockwise);
        }
        context.EndFigure(false);
        return geometry;
    }

    /// <summary>A point on the circle, measured clockwise from twelve o'clock.</summary>
    private static Point PointOn(Point centre, double radius, double degrees)
    {
        var radians = (degrees - 90d) * Math.PI / 180d;
        return new Point(centre.X + (radius * Math.Cos(radians)), centre.Y + (radius * Math.Sin(radians)));
    }
}
