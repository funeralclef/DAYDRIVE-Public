using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IntelligentScheduler.Application;
using IntelligentScheduler.Desktop.Localization;
using IntelligentScheduler.Domain;

namespace IntelligentScheduler.Desktop.ViewModels;

/// <summary>
/// Chart geometry is computed here rather than in XAML: the view stays declarative and the
/// numbers stay testable. Every height and arc below is already in device-independent pixels.
/// </summary>
public static class AnalyticsGeometry
{
    public const double ColumnHeight = 116;
    public const double DonutSize = 138;
    public const double DonutOuterRadius = 66;
    public const double DonutInnerRadius = 45;
    public const double HourStripHeight = 52;
    public const double WeekdayBarWidth = 424;

    /// <summary>
    /// How much room the daily strip actually has inside the analytics card. The bars used to
    /// be a fixed seven pixels each whichever range was on, so ninety-one days drew ninety-one
    /// overlapping slivers spilling out of the card; the width is derived from this instead.
    /// </summary>
    public const double ColumnStripWidth = 528;

    /// <summary>The one gridline that means something: everything is drawn against this.</summary>
    public const double GridlineCount = 4;

    /// <summary>Builds an SVG path for one slice of the donut, in a DonutSize×DonutSize box.</summary>
    public static string DonutSlice(double startFraction, double sweepFraction)
    {
        var centre = DonutSize / 2;
        // A full circle cannot be drawn as a single arc, so leave a hairline gap.
        sweepFraction = Math.Clamp(sweepFraction, 0, 0.9999);
        var startAngle = startFraction * 2 * Math.PI - Math.PI / 2;
        var endAngle = (startFraction + sweepFraction) * 2 * Math.PI - Math.PI / 2;
        var largeArc = sweepFraction > 0.5 ? 1 : 0;

        var (outerStartX, outerStartY) = Point(centre, DonutOuterRadius, startAngle);
        var (outerEndX, outerEndY) = Point(centre, DonutOuterRadius, endAngle);
        var (innerEndX, innerEndY) = Point(centre, DonutInnerRadius, endAngle);
        var (innerStartX, innerStartY) = Point(centre, DonutInnerRadius, startAngle);

        return string.Create(CultureInfo.InvariantCulture,
            $"M {outerStartX:0.##},{outerStartY:0.##} " +
            $"A {DonutOuterRadius:0.##},{DonutOuterRadius:0.##} 0 {largeArc} 1 {outerEndX:0.##},{outerEndY:0.##} " +
            $"L {innerEndX:0.##},{innerEndY:0.##} " +
            $"A {DonutInnerRadius:0.##},{DonutInnerRadius:0.##} 0 {largeArc} 0 {innerStartX:0.##},{innerStartY:0.##} Z");
    }

    private static (double X, double Y) Point(double centre, double radius, double angle)
        => (centre + radius * Math.Cos(angle), centre + radius * Math.Sin(angle));

    /// <summary>Scales a value into a bar height, keeping a visible stub for small non-zero values.</summary>
    public static double Scale(double value, double maximum, double height)
    {
        if (maximum <= 0 || value <= 0) return 0;
        return Math.Clamp(value / maximum * height, 3, height);
    }

    /// <summary>
    /// How often a column gets a label. Thirty-one dates written under thirty-one six-pixel
    /// columns is a grey smear; every third one is a scale.
    /// </summary>
    public static int LabelStep(int columnCount)
        => columnCount <= 14 ? 1 : (int)Math.Ceiling(columnCount / 11d);
}

public sealed class AnalyticsMetricViewModel
{
    public AnalyticsMetricViewModel(string label, string value, string? unit, AnalyticsMetric metric,
        LocalizationService localization, bool higherIsBetter = true, bool asPercent = false)
    {
        Label = label;
        Value = value;
        Unit = unit ?? string.Empty;
        HasSample = metric.HasEnoughData;
        NoSampleText = localization.Text("MetricNoSample");

        if (metric.Delta is { } delta && Math.Abs(delta) > (asPercent ? 0.01 : 0.05))
        {
            var improving = higherIsBetter ? delta > 0 : delta < 0;
            TrendGlyph = delta > 0 ? "▲" : "▼";
            TrendText = asPercent
                ? localization.Format("MetricPercentagePoints", Math.Abs(delta) * 100)
                : $"{Math.Abs(delta):0.#}";
            TrendBrush = improving ? EventVisuals.SuccessBrush : EventVisuals.DangerBrush;
            HasTrend = true;
        }
        ComparisonText = localization.Text("MetricVsPrevious");
    }

    public string Label { get; }
    public string Value { get; }
    public string Unit { get; }
    public bool HasUnit => Unit.Length > 0;
    public bool HasSample { get; }
    public string NoSampleText { get; }
    public bool HasTrend { get; }
    public string TrendGlyph { get; } = string.Empty;
    public string TrendText { get; } = string.Empty;
    public IBrush TrendBrush { get; } = Brushes.Transparent;
    public string ComparisonText { get; }
}

/// <summary>
/// One column of the daily-load chart. It carries its own width, because how wide a column may
/// be depends on how many there are, and its own reveal, because a bar that grows out of the
/// axis is the cheapest honest animation a chart can have: the movement is the value.
/// </summary>
public sealed partial class AnalyticsColumnViewModel : ObservableObject
{
    private readonly double _scheduledTarget;
    private readonly double _completedTarget;

    public AnalyticsColumnViewModel(
        AnalyticsBucket bucket, int maxMinutes, double barWidth, bool showLabel,
        LocalizationService localization, bool revealed)
    {
        From = bucket.From;
        To = bucket.To;
        BarWidth = barWidth;
        _isRevealed = revealed;
        _scheduledTarget = AnalyticsGeometry.Scale(bucket.ScheduledMinutes, maxMinutes, AnalyticsGeometry.ColumnHeight);
        _completedTarget = AnalyticsGeometry.Scale(bucket.CompletedMinutes, maxMinutes, AnalyticsGeometry.ColumnHeight);

        var today = DateOnly.FromDateTime(DateTime.Today);
        IsToday = bucket.Contains(today);
        IsEmpty = bucket.IsEmpty;
        HasLabel = showLabel;
        Label = bucket.IsSingleDay
            ? bucket.From.Day.ToString(localization.CurrentCulture)
            : bucket.From.ToString("d MMM", localization.CurrentCulture);
        Caption = bucket.IsSingleDay
            ? localization.CurrentCulture.DateTimeFormat.GetShortestDayName(bucket.From.DayOfWeek)
            : string.Empty;

        var span = bucket.IsSingleDay
            ? bucket.From.ToString("d MMM", localization.CurrentCulture)
            : $"{bucket.From.ToString("d MMM", localization.CurrentCulture)} — " +
              $"{bucket.To.ToString("d MMM", localization.CurrentCulture)}";
        Tooltip = $"{span} · " +
                  $"{localization.Text("ChartScheduledLegend")} {Hours(bucket.ScheduledMinutes, localization)} · " +
                  $"{localization.Text("ChartCompletedLegend")} {Hours(bucket.CompletedMinutes, localization)}";
    }

    public DateOnly From { get; }
    public DateOnly To { get; }
    public double BarWidth { get; }
    public string Label { get; }
    public string Caption { get; }
    public bool HasCaption => Caption.Length > 0;
    public bool HasLabel { get; }
    public bool IsToday { get; }
    public bool IsEmpty { get; }
    public string Tooltip { get; }

    /// <summary>Bars start flat and grow to their value once the panel has arrived.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScheduledHeight))]
    [NotifyPropertyChangedFor(nameof(CompletedHeight))]
    private bool _isRevealed;

    public double ScheduledHeight => IsRevealed ? _scheduledTarget : 0;
    public double CompletedHeight => IsRevealed ? _completedTarget : 0;
    public bool HasCompleted => _completedTarget > 0;

    private static string Hours(int minutes, LocalizationService localization)
        => localization.Format("MetricHours", minutes / 60d);
}

/// <summary>
/// One wedge of the donut. It owns where it begins on the ring and how far round it reaches,
/// because the ring is drawn rather than switched on: the whole circle is swept once, clockwise
/// from twelve, and each wedge is however much of itself that sweep has passed. Fading six
/// finished wedges up together said "here is a chart"; a single stroke going round says what a
/// donut actually means, which is that these are the parts of one whole.
/// </summary>
public sealed partial class AnalyticsSliceViewModel : ObservableObject
{
    /// <summary>How far round this wedge is currently drawn, in turns of the whole ring.</summary>
    private double _sweep;

    public AnalyticsSliceViewModel(AnalyticsSlice slice, double startFraction, IBrush brush, string label,
        LocalizationService localization, bool revealed)
    {
        Key = slice.Key;
        Label = label;
        Minutes = slice.Minutes;
        Share = slice.Share;
        StartFraction = startFraction;
        Brush = brush;
        _sweep = revealed ? Share : 0;
        _pathData = Geometry.Parse(AnalyticsGeometry.DonutSlice(StartFraction, _sweep));
        PercentText = $"{slice.Share * 100:0}%";
        HoursText = localization.Format("MetricHours", slice.Minutes / 60d);
    }

    public string Key { get; }
    public string Label { get; }
    public int Minutes { get; }
    public double Share { get; }

    /// <summary>Where on the ring this wedge starts, measured from twelve o'clock.</summary>
    public double StartFraction { get; }

    /// <summary>Where the ring ends once this wedge has been drawn in full.</summary>
    public double EndFraction => StartFraction + Share;

    public IBrush Brush { get; }
    public string PercentText { get; }
    public string HoursText { get; }

    /// <summary>The arc as it stands. Rebuilt as the sweep passes over this wedge.</summary>
    [ObservableProperty] private Geometry _pathData;

    /// <summary>A wedge the sweep has not reached yet has nothing to show.</summary>
    public double SliceOpacity => _sweep > 0 ? 1 : 0;

    /// <summary>Whether the ring has been drawn all the way past this wedge.</summary>
    public bool IsRevealed
    {
        get => _sweep >= Share;
        set => Draw(value ? Share : 0);
    }

    /// <summary>
    /// Draws this wedge up to <paramref name="sweep"/> turns, which the caller passes as the
    /// ring's own progress minus where this wedge begins: negative before the sweep arrives,
    /// past <see cref="Share"/> once it has gone by, and the wedge clamps both ends itself.
    /// </summary>
    public void Draw(double sweep)
    {
        sweep = Math.Clamp(sweep, 0, Share);
        if (sweep == _sweep) return;
        var wasVisible = _sweep > 0;
        _sweep = sweep;
        PathData = Geometry.Parse(AnalyticsGeometry.DonutSlice(StartFraction, sweep));
        if (wasVisible != sweep > 0) OnPropertyChanged(nameof(SliceOpacity));
    }
}

public sealed partial class AnalyticsHourViewModel : ObservableObject
{
    private readonly double _target;

    public AnalyticsHourViewModel(AnalyticsHourPoint point, int maxMinutes, bool showLabel, bool isBusiest,
        LocalizationService localization, bool revealed)
    {
        Hour = point.Hour;
        _isRevealed = revealed;
        _target = AnalyticsGeometry.Scale(point.Minutes, maxMinutes, AnalyticsGeometry.HourStripHeight);
        // Opacity doubles as intensity so a quiet hour still reads as present but muted.
        Intensity = maxMinutes <= 0 ? 0.12 : Math.Clamp(point.Minutes / (double)maxMinutes, 0.16, 1);
        Label = showLabel ? point.Hour.ToString("00", CultureInfo.InvariantCulture) : string.Empty;
        HasLabel = showLabel;
        IsBusiest = isBusiest && point.Minutes > 0;
        Tooltip = $"{point.Hour:00}:00 · {localization.Format("MetricHours", point.Minutes / 60d)}";
    }

    public int Hour { get; }
    public double Intensity { get; }
    public string Label { get; }
    public bool HasLabel { get; }
    public bool IsBusiest { get; }
    public string Tooltip { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Height))]
    private bool _isRevealed;

    public double Height => IsRevealed ? _target : 0;
}

public sealed partial class AnalyticsWeekdayViewModel : ObservableObject
{
    private readonly double _target;

    public AnalyticsWeekdayViewModel(AnalyticsWeekdayPoint point, int maxMinutes,
        LocalizationService localization, bool revealed)
    {
        Label = localization.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName(point.Day).TrimEnd('.');
        _isRevealed = revealed;
        _target = AnalyticsGeometry.Scale(point.Minutes, maxMinutes, AnalyticsGeometry.WeekdayBarWidth);
        ValueText = point.Minutes == 0 ? "—" : localization.Format("MetricHours", point.Minutes / 60d);
        IsBusiest = maxMinutes > 0 && point.Minutes == maxMinutes;
        Tooltip = $"{localization.CurrentCulture.DateTimeFormat.GetDayName(point.Day)} · {ValueText}";
    }

    public string Label { get; }
    public string ValueText { get; }
    public bool IsBusiest { get; }
    public string Tooltip { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Width))]
    private bool _isRevealed;

    public double Width => IsRevealed ? _target : 0;
}

/// <summary>One entry of the range list: a name a reader recognises, and the span behind it.</summary>
public sealed partial class AnalyticsRangeOptionViewModel(
    AnalyticsRange range,
    int days,
    string label,
    string detail,
    Func<AnalyticsRangeOptionViewModel, Task> select) : ObservableObject
{
    public AnalyticsRange Range { get; } = range;
    public int Days { get; } = days;
    public string Label { get; } = label;
    public string Detail { get; } = detail;

    [ObservableProperty] private bool _isSelected;

    [RelayCommand] private Task SelectAsync() => select(this);
}

public sealed partial class AnalyticsInsightViewModel : ObservableObject
{
    private readonly Func<AnalyticsInsightViewModel, Task>? _discuss;

    public AnalyticsInsightViewModel(AnalyticsInsight insight, LocalizationService localization,
        Func<AnalyticsInsightViewModel, Task>? discuss = null)
    {
        Insight = insight;
        _discuss = discuss;
        Text = Describe(insight, localization);
        Sample = localization.Format("InsightSample", insight.SampleSize, (int)Math.Round(insight.Confidence * 100));
        IsPositive = insight.IsPositive;
        Glyph = insight.IsPositive ? "✓" : "!";
    }

    public AnalyticsInsight Insight { get; }
    public string Text { get; }
    public string Sample { get; }
    public bool IsPositive { get; }
    public string Glyph { get; }

    [RelayCommand] private Task DiscussAsync() => _discuss?.Invoke(this) ?? Task.CompletedTask;

    private static string Describe(AnalyticsInsight insight, LocalizationService localization)
    {
        var arguments = insight.Arguments.Select(argument => (object)Translate(argument, localization)).ToArray();
        var key = insight.Kind switch
        {
            AnalyticsInsightKind.EstimationBias => insight.Arguments.ElementAtOrDefault(1) == "under"
                ? "InsightEstimationBiasUnder"
                : "InsightEstimationBiasOver",
            AnalyticsInsightKind.StrongTimeOfDay => "InsightStrongTimeOfDay",
            AnalyticsInsightKind.ScheduleFriction => "InsightScheduleFriction",
            AnalyticsInsightKind.StartDelay => "InsightStartDelay",
            AnalyticsInsightKind.Overload => "InsightOverload",
            AnalyticsInsightKind.Underuse => "InsightUnderuse",
            AnalyticsInsightKind.CategoryDominance => "InsightCategoryDominance",
            _ => "InsightConsistency"
        };
        return localization.Format(key, arguments);
    }

    /// <summary>Turns raw enum-ish arguments into localized words before formatting.</summary>
    private static string Translate(string argument, LocalizationService localization) => argument switch
    {
        nameof(PreferredTimeOfDay.Morning) => localization.Text("PartOfDayMorning"),
        nameof(PreferredTimeOfDay.Afternoon) => localization.Text("PartOfDayAfternoon"),
        nameof(PreferredTimeOfDay.Evening) => localization.Text("PartOfDayEvening"),
        "Focus" => localization.Text("CategoryFocus"),
        _ when Enum.TryParse<EventCategory>(argument, out var category) && Enum.IsDefined(category)
            => localization.Text(EventVisuals.CategoryKey(category)),
        _ => argument
    };
}
