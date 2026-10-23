using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Avalonia.Threading;

namespace IntelligentScheduler.Desktop.Controls;

/// <summary>What the mascot is reacting to. Nothing else about the widget changes with it.</summary>
public enum MascotMood
{
    /// <summary>Default: nothing wrong, nothing running.</summary>
    Calm,
    /// <summary>Every deadline is met. The one state the widget used to have no way to show.</summary>
    Happy,
    /// <summary>An event is on and the user started it.</summary>
    Focused,
    /// <summary>Nothing scheduled at all.</summary>
    Sleepy,
    /// <summary>Something is overdue.</summary>
    Alert
}

/// <summary>
/// The small creature that lives beside the clock: one flat, rounded body with a scalloped hem,
/// two big eyes and a sprout on the crown. It is drawn rather than bundled, so it costs nothing
/// to ship, scales to any size without a second asset, and takes the accent colour of whatever
/// theme is on.
///
/// Everything here is flat on purpose. The first version of the creature was shaded — gradients
/// down the body, a sheen, a rim light and a contact shadow — and at the size it actually appears
/// that reads as a small rendered object rather than as a character. A sticker does not survive
/// being lit; it survives being simple. So the body is one colour, the face is two shapes and a
/// glint, and the only thing carrying depth is the silhouette.
///
/// The body is the accent now, not a near-black hull with a coloured band on it. That is what
/// makes the mascot colour in settings worth having: picking a colour paints the creature rather
/// than a ribbon most of the way up it. The one colour is fitted into a luminance band chosen
/// against the current theme variant, so neither a near-black accent on a dark widget nor a
/// near-white one on a light widget can make the creature disappear.
///
/// One 25 fps timer drives everything, and it only runs while the control is really on screen —
/// the widget is hidden most of the time, and a hidden window must not keep a timer warm. With
/// "reduce motion" on it draws a single settled pose and never starts the timer at all.
/// </summary>
public sealed class MascotView : Control
{
    /// <summary>Design box the drawing is authored in, shared with the icon set.</summary>
    private const double Design = 24d;

    /// <summary>
    /// The pose held when animation is off. Zero is a poor still: half the mood marks are at the
    /// very start of their loop and would be invisible, which is exactly the frame the QA sheet
    /// and the settings preview would freeze on.
    /// </summary>
    private const double StillTime = 0.8;

    /// <summary>The point every squash and every tilt turns about: the middle of the hem.</summary>
    private static readonly Point Ground = new(12, 20.75);

    /// <summary>
    /// Head and hem as one closed shape: a single dome over three round scallops. The scallops
    /// are half circles rather than the shallow ripples they used to be, which is the whole
    /// difference between "a bean" and "a small thing under a cloth" at 40 px.
    ///
    /// It is authored nearly edge to edge in the box on purpose. The creature reads at the size
    /// its body occupies, not the size its layout slot does, so filling the box is what let the
    /// widget give it more presence without taking a proportional bite out of the clock.
    /// </summary>
    private const string BodyPath =
        "M3 12.6 C3 7 7.05 3.5 12 3.5 C16.95 3.5 21 7 21 12.6 " +
        "L21 18.35 C21 21.55 15 21.55 15 18.35 " +
        "C15 21.55 9 21.55 9 18.35 " +
        "C9 21.55 3 21.55 3 18.35 Z";

    /// <summary>
    /// The sprout on the crown. It replaces the two swinging streamers of the first version: at
    /// this size a band of cloth is four moving edges nobody can resolve, where one leaf is a
    /// shape — and one shape can be swung on its own stem, which is all the life it needs.
    /// </summary>
    private const string LeafPath =
        "M11.95 3.8 C12.2 1.5 14.1 0.2 16.15 0.8 C15.9 3.1 14.05 4.35 11.95 3.8 Z";

    /// <summary>Where the sprout is pinned, and the centre its sway turns about.</summary>
    private static readonly Point Stem = new(11.95, 3.8);

    private static readonly Point LeftEye = new(9.05, 12.85);
    private static readonly Point RightEye = new(14.95, 12.85);

    /// <summary>Where the mouth hangs from. Every mood draws its own shape at this one point.</summary>
    private static readonly Point Mouth = new(12, 16.05);

    private static readonly Dictionary<string, Geometry> GeometryCache = new(StringComparer.Ordinal);

    public static readonly StyledProperty<MascotMood> MoodProperty =
        AvaloniaProperty.Register<MascotView, MascotMood>(nameof(Mood));

    public static readonly StyledProperty<IBrush?> AccentProperty =
        AvaloniaProperty.Register<MascotView, IBrush?>(nameof(Accent));

    public static readonly StyledProperty<IBrush?> AccentOverrideProperty =
        AvaloniaProperty.Register<MascotView, IBrush?>(nameof(AccentOverride));

    public static readonly StyledProperty<bool> IsAnimatedProperty =
        AvaloniaProperty.Register<MascotView, bool>(nameof(IsAnimated), true);

    public MascotMood Mood
    {
        get => GetValue(MoodProperty);
        set => SetValue(MoodProperty, value);
    }

    /// <summary>The theme's own accent, which is what the creature follows unless it is told not to.</summary>
    public IBrush? Accent
    {
        get => GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    /// <summary>
    /// A colour the user picked for the mascot alone. Null means "keep following the theme",
    /// which is what keeps the default a live resource rather than a copy of one.
    /// </summary>
    public IBrush? AccentOverride
    {
        get => GetValue(AccentOverrideProperty);
        set => SetValue(AccentOverrideProperty, value);
    }

    /// <summary>Cleared by the shell's "reduce motion" preference.</summary>
    public bool IsAnimated
    {
        get => GetValue(IsAnimatedProperty);
        set => SetValue(IsAnimatedProperty, value);
    }

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private bool _isAttached;
    private DateTimeOffset _started = DateTimeOffset.UtcNow;
    /// <summary>Seconds since the control woke up, which is all the drawing needs to know.</summary>
    private double _time;
    /// <summary>Runs 0 to 1 when the mood changes, so a new pose arrives rather than snaps in.</summary>
    private double _moodBlend = 1;
    /// <summary>Kept so the face it is leaving can fade out under the one it is arriving at.</summary>
    private MascotMood _previousMood;

    static MascotView()
    {
        AffectsRender<MascotView>(MoodProperty, AccentProperty, AccentOverrideProperty, IsAnimatedProperty);
        MoodProperty.Changed.AddClassHandler<MascotView>((mascot, args) => mascot.OnMoodChanged(args));
        IsAnimatedProperty.Changed.AddClassHandler<MascotView>((mascot, _) => mascot.SyncTimer());
    }

    public MascotView()
    {
        _timer.Tick += OnTick;
        // The one colour the creature has is fitted against the surface it sits on, so moving
        // between a light and a dark theme is a repaint rather than only a resource swap.
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>
    /// Every visual between this control and the window, watched for one thing only: whether it
    /// is still visible.
    ///
    /// The creature stops itself the moment it is off screen, and nothing about the creature
    /// changes when an *ancestor* is what took it away - not its own IsVisible, not IsAnimated,
    /// not being attached - so on its own it has no way of hearing that it is back. That is
    /// exactly what froze the mascot in the shell's monitoring dock: the shell is hidden and
    /// shown all day, the first tick after a hide stopped the timer, and the creature was still
    /// holding that frame when the shell came back. Avalonia's own effective-visibility notice
    /// is internal to the framework, so the chain is watched directly.
    /// </summary>
    private readonly List<Visual> _watchedAncestors = [];

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        _started = DateTimeOffset.UtcNow;
        WatchAncestors();
        SyncTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        _timer.Stop();
        ReleaseAncestors();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>See <see cref="_watchedAncestors"/>. The chain is fixed for as long as this
    /// control is attached: anything that changes it detaches this control first.</summary>
    private void WatchAncestors()
    {
        ReleaseAncestors();
        for (var node = this.GetVisualParent(); node is not null; node = node.GetVisualParent())
        {
            node.PropertyChanged += AncestorPropertyChanged;
            _watchedAncestors.Add(node);
        }
    }

    private void ReleaseAncestors()
    {
        foreach (var node in _watchedAncestors) node.PropertyChanged -= AncestorPropertyChanged;
        _watchedAncestors.Clear();
    }

    private void AncestorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property == IsVisibleProperty) SyncTimer();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty) SyncTimer();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // A second way of hearing the same news, kept because it costs a comparison: a hidden
        // subtree is not measured, so being measured at all means the creature is somewhere
        // that is being laid out. The chain above is what actually catches a panel or a window
        // coming back - see the ancestors this control watches - and this only covers the case
        // where the creature arrives into a tree that was already visible.
        if (IsAnimated && !_timer.IsEnabled) SyncTimer();

        var width = double.IsFinite(Width) && Width > 0 ? Width : double.NaN;
        var height = double.IsFinite(Height) && Height > 0 ? Height : double.NaN;
        var side = double.IsFinite(width) ? width : double.IsFinite(height) ? height : Design;
        return new Size(double.IsFinite(width) ? width : side, double.IsFinite(height) ? height : side);
    }

    /// <summary>
    /// Restarts the clock the drawing runs on. The widget is hidden far more often than it is
    /// shown, so every reveal begins the idle loop from the same settled frame.
    /// </summary>
    public void Restart()
    {
        _started = DateTimeOffset.UtcNow;
        _time = 0;
        SyncTimer();
    }

    private void OnMoodChanged(AvaloniaPropertyChangedEventArgs args)
    {
        if (args.OldValue is MascotMood old) _previousMood = old;
        _moodBlend = IsAnimated ? 0 : 1;
        InvalidateVisual();
    }

    private void SyncTimer()
    {
        var shouldRun = IsAnimated && IsEffectivelyVisible && _isAttached;
        if (shouldRun && !_timer.IsEnabled)
        {
            _started = DateTimeOffset.UtcNow;
            _timer.Start();
        }
        else if (!shouldRun && _timer.IsEnabled)
        {
            _timer.Stop();
        }
        InvalidateVisual();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        // A hidden window keeps its controls attached, so visibility is re-checked every frame
        // rather than only when something raises an event about it.
        if (!IsEffectivelyVisible)
        {
            _timer.Stop();
            return;
        }

        _time = (DateTimeOffset.UtcNow - _started).TotalSeconds;
        if (_moodBlend < 1) _moodBlend = Math.Min(1, _moodBlend + 0.06);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 1) return;

        var palette = Palette.For(ResolveAccent(), ActualThemeVariant != ThemeVariant.Light);
        var scale = size / Design;
        var matrix = Matrix.CreateScale(scale, scale)
            .Append(Matrix.CreateTranslation((Bounds.Width - size) / 2d, (Bounds.Height - size) / 2d));
        using var pushed = context.PushTransform(matrix);

        var mood = Mood;
        var time = IsAnimated ? _time : StillTime;
        var blend = IsAnimated ? Ease(_moodBlend) : 1;
        var pose = MascotPose.For(mood, time);

        // A mood change is a reaction, so the body swells once on the way into it rather than
        // dissolving: the creature is small, and a cross-fade at this size reads as a glitch.
        // It is half of what it used to be — the old pop was large enough to be its own event.
        var pop = 1 + Math.Sin(blend * Math.PI) * 0.045;

        // Volume is conserved: whatever the body loses in height it gains in width, which is the
        // difference between a thing that breathes and a thing that is being scaled.
        var body = Matrix.CreateTranslation(-Ground.X, -Ground.Y)
            .Append(Matrix.CreateScale(pop / pose.Squash, pop * pose.Squash))
            .Append(Matrix.CreateRotation(pose.Tilt * Math.PI / 180))
            .Append(Matrix.CreateTranslation(Ground.X, Ground.Y + pose.Bob));

        using (context.PushTransform(body))
        {
            context.DrawGeometry(palette.Body, null, GetGeometry(BodyPath));
            DrawSprout(context, palette, pose, time);
            DrawFace(context, palette, mood, time, blend);
        }

        // The mark is beside the creature, not on it, so it stays put while the body moves. It
        // still arrives with the mood rather than before it.
        DrawMoodMark(context, palette, mood, time, blend);
    }

    // ---------------------------------------------------------------- pose

    /// <summary>
    /// Everything the mood does to the body, in one place. Each mood is the same creature at a
    /// different tempo: nothing here changes what is drawn, only how it is carried.
    ///
    /// Every curve in here is a sine or a half-sine. The first version had a buzz on one mood and
    /// a decaying rattle on another, and at 40 px neither reads as an emotion — they read as the
    /// drawing failing to hold still. Anything that cannot be followed by eye was cut.
    /// </summary>
    private readonly record struct MascotPose(
        double Bob, double Squash, double Tilt, double Sway, double SwaySpeed)
    {
        public static MascotPose For(MascotMood mood, double time)
        {
            switch (mood)
            {
                case MascotMood.Happy:
                {
                    // A hop, not a float. Two thirds of the cycle is air, the rest is the landing,
                    // and the squash on the way down is what gives the hop somewhere to land.
                    const double period = 1.15;
                    var cycle = time % period / period;
                    var air = cycle < 0.64 ? Math.Sin(cycle / 0.64 * Math.PI) : 0;
                    var land = cycle >= 0.64 ? Math.Sin((cycle - 0.64) / 0.36 * Math.PI) : 0;
                    return new MascotPose(
                        Bob: -air * 2.1,
                        Squash: 1 + air * 0.05 - land * 0.1,
                        Tilt: Math.Sin(cycle * Math.PI * 2) * 2.4,
                        Sway: 13,
                        SwaySpeed: 2.3);
                }
                case MascotMood.Focused:
                {
                    // Working, and happy to be. The old pose was a fixed lean with a slightly
                    // quicker breath — accurate, and almost perfectly still, which at forty
                    // pixels reads as the drawing having stopped rather than as concentration.
                    //
                    // It rocks foot to foot instead, on a slow two-beat: rising as it leans
                    // each way and settling through the middle, where the body takes the weight
                    // and squashes a little. Two beats to the cycle means the sprout — which
                    // runs a beat behind whatever the body does — is always somewhere else,
                    // and the whole creature reads as busy rather than as posed.
                    const double period = 1.45;
                    var cycle = time % period / period;
                    var rock = Math.Sin(cycle * Math.PI * 2);
                    var lift = Math.Abs(rock);
                    return new MascotPose(
                        Bob: -lift * 0.95,
                        Squash: 0.965 + lift * 0.055,
                        Tilt: rock * 5.4,
                        Sway: 15,
                        SwaySpeed: 2.2);
                }
                case MascotMood.Sleepy:
                {
                    // Settled and squat, with a breath slow enough to count and a head tilted
                    // far enough over that the closed eyes are not the only thing saying so.
                    var breath = Math.Sin(time * 0.62);
                    return new MascotPose(
                        Bob: 0.6 + breath * 0.34,
                        Squash: 0.955 + breath * 0.05,
                        Tilt: 4 + Math.Sin(time * 0.31) * 1.1,
                        Sway: 3.5,
                        SwaySpeed: 0.55);
                }
                case MascotMood.Alert:
                {
                    // Two quick hops and then a wait, rather than a permanent shiver: a creature
                    // that never stops moving stops reading as alarmed and starts reading as broken.
                    const double period = 1.7;
                    var cycle = time % period;
                    var hop = cycle < 0.64 ? Math.Abs(Math.Sin(cycle / 0.32 * Math.PI)) : 0;
                    return new MascotPose(
                        Bob: -hop * 1.55 + Math.Sin(time * 2.2) * 0.18,
                        Squash: 1 + hop * 0.05,
                        Tilt: Math.Sin(time * 2.4) * 1.7,
                        Sway: 12,
                        SwaySpeed: 2.7);
                }
                default:
                {
                    var breath = Math.Sin(time * 1.15);
                    return new MascotPose(
                        Bob: breath * 0.55,
                        Squash: 1 + Math.Sin(time * 1.15 + Math.PI / 2) * 0.022,
                        Tilt: Math.Sin(time * 0.58) * 1.7,
                        Sway: 7,
                        SwaySpeed: 1.1);
                }
            }
        }
    }

    // ---------------------------------------------------------------- parts

    /// <summary>
    /// The sprout, swung about its own stem. It runs a beat behind the body it is attached to,
    /// which is the entire reason it is a separate shape: a leaf that turns exactly when the
    /// creature does is painted on, and one that catches up afterwards is growing out of it.
    /// </summary>
    private static void DrawSprout(DrawingContext context, Palette palette, MascotPose pose, double time)
    {
        var beat = time * pose.SwaySpeed;
        var angle = Math.Sin(beat) * pose.Sway + Math.Sin(beat * 0.63 + 0.8) * pose.Sway * 0.28;
        using (context.PushTransform(Matrix.CreateRotation(angle * Math.PI / 180, Stem)))
            context.DrawGeometry(palette.Sprout, null, GetGeometry(LeafPath));
    }

    private void DrawFace(DrawingContext context, Palette palette, MascotMood mood, double time, double blend)
    {
        if (blend < 1 && _previousMood != mood)
        {
            using (context.PushOpacity(1 - blend)) DrawExpression(context, palette, _previousMood, time);
            using (context.PushOpacity(blend)) DrawExpression(context, palette, mood, time);
            return;
        }

        DrawExpression(context, palette, mood, time);
    }

    private static void DrawExpression(DrawingContext context, Palette palette, MascotMood mood, double time)
    {
        DrawBlush(context, palette, mood switch
        {
            MascotMood.Happy => 0.55,
            MascotMood.Focused => 0.5,
            _ => 0.34
        });

        var openness = Blink(time);
        switch (mood)
        {
            case MascotMood.Happy:
                // Eyes shut and turned up. Nothing else says pleased this cheaply.
                DrawCurvedEye(context, palette.InkPen, LeftEye, 1.05);
                DrawCurvedEye(context, palette.InkPen, RightEye, 1.05);
                DrawOpenSmile(context, palette);
                break;
            case MascotMood.Focused:
                // Wide open and tipped up, with a small closed smile under them. It used to be
                // two narrowed slits over a flat line — which is what concentration looks like
                // drawn large, and what displeasure looks like at forty pixels: the eyes were
                // too flat to keep their glints, and a creature with no light in its eyes and
                // no mouth to speak of is not somebody enjoying their work. A timer running is
                // the best thing that happens in this application; the face says so.
                DrawEye(context, palette, new Point(LeftEye.X, LeftEye.Y - 0.15), 1.58, 1.66 * openness, 5);
                DrawEye(context, palette, new Point(RightEye.X, RightEye.Y - 0.15), 1.58, 1.66 * openness, -5);
                DrawMouthLine(context, palette, 1.05, 1);
                break;
            case MascotMood.Alert:
                DrawEye(context, palette, LeftEye, 1.72, 1.8 * openness);
                DrawEye(context, palette, RightEye, 1.72, 1.8 * openness);
                // Caught mid-word: a small round mouth, breathing with the hops.
                context.DrawEllipse(palette.Ink, null,
                    Mouth, 0.62, 0.7 + Math.Abs(Math.Sin(time * 3.7)) * 0.16);
                break;
            case MascotMood.Sleepy:
                // Lids, not eyes, and curved the other way from the happy ones.
                DrawCurvedEye(context, palette.InkPen, new Point(LeftEye.X, LeftEye.Y + 0.5), -0.85);
                DrawCurvedEye(context, palette.InkPen, new Point(RightEye.X, RightEye.Y + 0.5), -0.85);
                break;
            default:
                DrawEye(context, palette, LeftEye, 1.6, 1.62 * openness);
                DrawEye(context, palette, RightEye, 1.6, 1.62 * openness);
                DrawMouthLine(context, palette, 1.2, 0.8);
                break;
        }
    }

    /// <summary>
    /// One eye: a filled oval with a glint high on the left of it. The glint is the only place
    /// the drawing spends a second colour on the face, and it is what keeps the eyes from
    /// reading as two holes punched in the body.
    /// </summary>
    private static void DrawEye(DrawingContext context, Palette palette, Point centre,
        double radiusX, double radiusY, double tilt = 0)
    {
        if (radiusY < 0.14)
        {
            // Mid-blink. A shut eye is a line, not a very flat oval, which at this size turns
            // into a grey smudge a frame before it disappears.
            DrawCurvedEye(context, palette.InkPen, centre, -0.25);
            return;
        }

        using var turned = context.PushTransform(Matrix.CreateRotation(tilt * Math.PI / 180, centre));
        context.DrawEllipse(palette.Ink, null, centre, radiusX, radiusY);
        // A glint on an eye this flat is a speck rather than a highlight, and a speck at 40 px
        // is dirt on the screen. Narrowed eyes and half-shut ones simply do without.
        if (radiusY < 0.9) return;
        context.DrawEllipse(palette.Glint, null,
            new Point(centre.X - radiusX * 0.3, centre.Y - radiusY * 0.34),
            radiusX * 0.32, radiusY * 0.3);
    }

    /// <summary>
    /// A stroked lid. A positive rise arches up for pleased, a negative one droops for asleep,
    /// and a rise of nearly nothing is the flat line a blink passes through.
    /// </summary>
    private static void DrawCurvedEye(DrawingContext context, IPen pen, Point centre, double rise)
    {
        var arc = new StreamGeometry();
        using (var builder = arc.Open())
        {
            builder.BeginFigure(new Point(centre.X - 1.5, centre.Y + rise * 0.3), false);
            builder.QuadraticBezierTo(
                new Point(centre.X, centre.Y - rise * 1.35),
                new Point(centre.X + 1.5, centre.Y + rise * 0.3));
            builder.EndFigure(false);
        }
        context.DrawGeometry(null, pen, arc);
    }

    /// <summary>The resting mouth: one small stroked curve, nearly flat when the mood is not smiling.</summary>
    private static void DrawMouthLine(DrawingContext context, Palette palette, double width, double drop)
    {
        var mouth = new StreamGeometry();
        using (var builder = mouth.Open())
        {
            builder.BeginFigure(new Point(Mouth.X - width, Mouth.Y), false);
            builder.QuadraticBezierTo(new Point(Mouth.X, Mouth.Y + drop * 1.9), new Point(Mouth.X + width, Mouth.Y));
            builder.EndFigure(false);
        }
        context.DrawGeometry(null, palette.InkPen, mouth);
    }

    /// <summary>The one mood that opens its mouth. A filled half-disc, which is all it takes.</summary>
    private static void DrawOpenSmile(DrawingContext context, Palette palette)
    {
        var smile = new StreamGeometry();
        using (var builder = smile.Open())
        {
            builder.BeginFigure(new Point(Mouth.X - 1.2, Mouth.Y - 0.15), true);
            builder.CubicBezierTo(
                new Point(Mouth.X - 1.05, Mouth.Y + 1.7),
                new Point(Mouth.X + 1.05, Mouth.Y + 1.7),
                new Point(Mouth.X + 1.2, Mouth.Y - 0.15));
            builder.EndFigure(true);
        }
        context.DrawGeometry(palette.Ink, null, smile);
    }

    private static void DrawBlush(DrawingContext context, Palette palette, double opacity)
    {
        using var faded = context.PushOpacity(opacity);
        context.DrawEllipse(palette.Blush, null, new Point(5.85, 14.85), 1.3, 0.82);
        context.DrawEllipse(palette.Blush, null, new Point(18.15, 14.85), 1.3, 0.82);
    }

    // ---------------------------------------------------------------- mood mark

    /// <summary>
    /// The one thing beside the creature: what it is reacting to, when that is worth saying.
    /// Two of the five moods now say it with the face alone, which is two fewer small shapes
    /// flickering away in the corner of a widget nobody is looking straight at.
    /// </summary>
    private static void DrawMoodMark(DrawingContext context, Palette palette, MascotMood mood, double time, double blend)
    {
        using var arriving = context.PushOpacity(blend);
        switch (mood)
        {
            case MascotMood.Alert:
            {
                // Struck rather than pulsing: it jumps on the beat and settles between them.
                var beat = time % 1.7 / 1.7;
                var strike = beat < 0.34 ? Math.Sin(beat / 0.34 * Math.PI) : 0;
                using var lifted = context.PushTransform(Matrix.CreateTranslation(0, -strike * 0.7));
                context.DrawLine(palette.DangerPen, new Point(21.4, 1.5), new Point(21.4, 3.7));
                context.DrawEllipse(palette.Danger, null, new Point(21.4, 5.05), 0.62, 0.62);
                break;
            }
            case MascotMood.Focused:
            {
                // The one mood that is literally a clock running, so it carries one: a small
                // stopwatch beside the head with its hand going round. Nothing else in the
                // widget says "this is being timed" without being read first, and it gives the
                // mood a mark of its own — it was the only one that had none.
                var centre = new Point(20.4, 3.35);
                const double radius = 2.15;
                context.DrawLine(palette.MarkPen,
                    new Point(centre.X, centre.Y - radius - 0.85), new Point(centre.X, centre.Y - radius - 0.05));
                using (context.PushOpacity(0.3))
                    context.DrawEllipse(null, palette.MarkPen, centre, radius, radius);
                DrawTimerHand(context, palette.MarkPen, centre, radius, time % 2.4 / 2.4);
                break;
            }
            case MascotMood.Happy:
            {
                // Two sparkles out of phase, so there is always one on the way in and one on the
                // way out and never a frame where the corner is empty.
                DrawSparkle(context, palette.Mark, new Point(20.6, 3.5),
                    1.5 * (0.55 + Math.Sin(time * 2.6) * 0.45));
                DrawSparkle(context, palette.Mark, new Point(18.5, 1.3),
                    1.0 * (0.55 + Math.Sin(time * 2.6 + 2.1) * 0.45));
                break;
            }
            case MascotMood.Sleepy:
            {
                // Two marks on one loop, each further along the same rise.
                for (var index = 0; index < 2; index++)
                {
                    var phase = (time * 0.34 + index / 2d) % 1d;
                    var opacity = Math.Sin(phase * Math.PI);
                    if (opacity <= 0.02) continue;
                    using var faded = context.PushOpacity(opacity);
                    DrawSleepMark(context, palette.MarkPen,
                        new Point(18.4 + phase * 2.9, 8.6 - phase * 7.4), 0.95 + phase * 1.15);
                }
                break;
            }
        }
    }

    /// <summary>
    /// The stopwatch's hand, as the arc it has covered rather than as a needle: a needle two
    /// pixels long is a speck, and the arc says how far round it has got as well as where it is.
    /// </summary>
    private static void DrawTimerHand(DrawingContext context, IPen pen, Point centre, double radius, double turn)
    {
        var sweep = Math.Clamp(turn, 0, 0.999);
        if (sweep < 0.02) return;
        var end = sweep * 2 * Math.PI - Math.PI / 2;
        var arc = new StreamGeometry();
        using (var builder = arc.Open())
        {
            builder.BeginFigure(new Point(centre.X, centre.Y - radius), false);
            builder.ArcTo(
                new Point(centre.X + radius * Math.Cos(end), centre.Y + radius * Math.Sin(end)),
                new Size(radius, radius), 0, sweep > 0.5, SweepDirection.Clockwise);
            builder.EndFigure(false);
        }
        context.DrawGeometry(null, pen, arc);
    }

    private static void DrawSleepMark(DrawingContext context, IPen pen, Point origin, double scale)
    {
        var mark = new StreamGeometry();
        using (var builder = mark.Open())
        {
            builder.BeginFigure(origin, false);
            builder.LineTo(new Point(origin.X + scale, origin.Y));
            builder.LineTo(new Point(origin.X, origin.Y + scale));
            builder.LineTo(new Point(origin.X + scale, origin.Y + scale));
            builder.EndFigure(false);
        }
        context.DrawGeometry(null, pen, mark);
    }

    /// <summary>A four-point star with a pinched waist, which is what stops it reading as a plus.</summary>
    private static void DrawSparkle(DrawingContext context, IBrush brush, Point centre, double radius)
    {
        if (radius <= 0.05) return;
        var waist = radius * 0.18;
        var spark = new StreamGeometry();
        using (var builder = spark.Open())
        {
            builder.BeginFigure(new Point(centre.X, centre.Y - radius), true);
            builder.QuadraticBezierTo(new Point(centre.X + waist, centre.Y - waist), new Point(centre.X + radius, centre.Y));
            builder.QuadraticBezierTo(new Point(centre.X + waist, centre.Y + waist), new Point(centre.X, centre.Y + radius));
            builder.QuadraticBezierTo(new Point(centre.X - waist, centre.Y + waist), new Point(centre.X - radius, centre.Y));
            builder.QuadraticBezierTo(new Point(centre.X - waist, centre.Y - waist), new Point(centre.X, centre.Y - radius));
            builder.EndFigure(true);
        }
        context.DrawGeometry(brush, null, spark);
    }

    // ---------------------------------------------------------------- timing

    /// <summary>
    /// Eye openness, 1 open and 0 shut. Blinks are spaced by a hash of the cycle index rather than
    /// a fixed interval: on a metronome the creature reads as a loop, which is the one thing a
    /// drawn character must never look like.
    /// </summary>
    private static double Blink(double time)
    {
        const double cycle = 3.9;
        const double span = 0.15;
        var index = Math.Floor(time / cycle);
        var jitter = Math.Abs(Math.Sin(index * 12.9898) * 43758.5453 % 1) * (cycle - span * 3);
        var local = time - index * cycle - jitter;
        if (local < 0 || local > span) return 1;
        return Math.Abs(Math.Cos(local / span * Math.PI));
    }

    private static double Ease(double value)
    {
        value = Math.Clamp(value, 0, 1);
        return 1 - Math.Pow(1 - value, 3);
    }

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

    // ---------------------------------------------------------------- colour

    /// <summary>The user's own colour when there is one, otherwise whatever the theme is using.</summary>
    private Color ResolveAccent() => (AccentOverride ?? Accent) switch
    {
        ISolidColorBrush solid when solid.Color.A > 0 => solid.Color,
        IGradientBrush { GradientStops.Count: > 0 } gradient => gradient.GradientStops[0].Color,
        _ => Color.FromRgb(0x9B, 0x8C, 0xFF)
    };

    /// <summary>
    /// Every brush and pen the drawing needs, built once per accent and theme variant. The
    /// control redraws 25 times a second and neither of those changes more than a few times a
    /// session, so nothing here belongs in Render.
    /// </summary>
    private sealed class Palette
    {
        /// <summary>
        /// Cheeks stay rose whatever colour the creature is. A blush in the body's own hue is a
        /// shadow, and a shadow on a flat drawing is exactly the thing being avoided.
        /// </summary>
        private static readonly Color BlushColor = Color.FromRgb(0xFF, 0x7E, 0x9B);

        private static Palette? _last;

        public Color Accent { get; }
        public bool IsDark { get; }
        public IBrush Body { get; }
        public IBrush Sprout { get; }
        public IBrush Ink { get; }
        public IPen InkPen { get; }
        public IBrush Glint { get; }
        public IBrush Blush { get; }
        public IBrush Mark { get; }
        public IPen MarkPen { get; }
        public IBrush Danger { get; }
        public IPen DangerPen { get; }

        public static Palette For(Color accent, bool isDark)
        {
            var last = _last;
            if (last is not null && last.Accent == accent && last.IsDark == isDark) return last;
            return _last = new Palette(accent, isDark);
        }

        private Palette(Color accent, bool isDark)
        {
            Accent = accent;
            IsDark = isDark;

            // The whole creature is this one colour, so it is the one colour that is not allowed
            // to be wrong: pulled into a band that keeps it off the surface behind it whichever
            // way the theme is pointing, and left exactly as picked whenever it already is.
            var body = isDark ? Fit(accent, 0.26, 0.78) : Fit(accent, 0.1, 0.52);
            Body = new SolidColorBrush(body).ToImmutable();

            // Which way the face has to go is a property of the body, not of the theme: a dark
            // accent on a dark widget is still a dark creature, and dark eyes would vanish into it.
            var onLightBody = Luminance(body) > 0.2;
            var ink = onLightBody ? Mix(body, Colors.Black, 0.86) : Mix(body, Colors.White, 0.9);
            Ink = new SolidColorBrush(ink).ToImmutable();
            InkPen = new Pen(Ink, 1.15, null, PenLineCap.Round, PenLineJoin.Round, 10);
            Glint = new SolidColorBrush(onLightBody
                ? Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)
                : Mix(body, Colors.Black, 0.45)).ToImmutable();

            // The sprout is the body one step further from the surface, so it separates from the
            // head without introducing a colour the drawing does not already have.
            Sprout = new SolidColorBrush(onLightBody
                ? Mix(body, Colors.Black, 0.26)
                : Mix(body, Colors.White, 0.3)).ToImmutable();

            Blush = new SolidColorBrush(BlushColor).ToImmutable();

            // Marks sit on the widget rather than on the creature, so they are fitted against
            // the surface instead of against the body.
            var mark = isDark ? Mix(accent, Colors.White, 0.2) : Mix(accent, Colors.Black, 0.28);
            Mark = new SolidColorBrush(mark).ToImmutable();
            MarkPen = new Pen(Mark, 0.85, null, PenLineCap.Round, PenLineJoin.Round, 10);

            var danger = isDark ? Color.FromRgb(0xFF, 0x82, 0x95) : Color.FromRgb(0xD9, 0x3F, 0x57);
            Danger = new SolidColorBrush(danger).ToImmutable();
            DangerPen = new Pen(Danger, 1.35, null, PenLineCap.Round, PenLineJoin.Round, 10);
        }

        /// <summary>
        /// Walks a colour toward white or black until it lands inside a luminance band, and
        /// leaves it alone when it already is. Every accent the app ships with passes through
        /// untouched; it is the ones the colour picker allows that need catching.
        /// </summary>
        private static Color Fit(Color colour, double min, double max)
        {
            for (var step = 0; step < 32 && Luminance(colour) < min; step++)
                colour = Mix(colour, Colors.White, 0.06);
            for (var step = 0; step < 32 && Luminance(colour) > max; step++)
                colour = Mix(colour, Colors.Black, 0.06);
            return colour;
        }

        private static double Luminance(Color colour)
        {
            static double Channel(byte value)
            {
                var scaled = value / 255d;
                return scaled <= 0.03928 ? scaled / 12.92 : Math.Pow((scaled + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Channel(colour.R) + 0.7152 * Channel(colour.G) + 0.0722 * Channel(colour.B);
        }

        private static Color Mix(Color from, Color to, double amount) => Color.FromArgb(
            from.A,
            (byte)Math.Round(from.R + (to.R - from.R) * amount),
            (byte)Math.Round(from.G + (to.G - from.G) * amount),
            (byte)Math.Round(from.B + (to.B - from.B) * amount));
    }
}
