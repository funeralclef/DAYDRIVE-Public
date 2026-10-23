using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IntelligentScheduler.Application;
using IntelligentScheduler.Domain;

namespace IntelligentScheduler.Desktop.ViewModels;

/// <summary>
/// The analytics surface. Everything shown here is derived from local history by
/// <see cref="ScheduleAnalytics"/>; this part only turns those numbers into shapes.
/// </summary>
public sealed partial class EdgeShellViewModel
{
    /// <summary>Cancels a chart reveal that a newer refresh has made obsolete.</summary>
    private CancellationTokenSource? _analyticsReveal;

    /// <summary>
    /// Whether the opening about to happen is this panel taking another one's spot.
    ///
    /// A swap waits longer before it lays the report out, because it waits for two moves: the
    /// panel in front of it folding back into the edge, and then its own unfolding out of it.
    /// Held to the shorter clock the report landed in the middle of that and the switch
    /// stuttered on it.
    /// </summary>
    internal bool AnalyticsTakesAnotherPanelsPlace { get; set; }

    /// <summary>Whether there is a drawn chart on this panel that a reveal would take down.</summary>
    private bool AnalyticsChartsAreStanding => AnalyticsColumns.Any(column => column.IsRevealed);

    [ObservableProperty] private bool _isAnalyticsOpen;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviousAnalyticsWeekCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextAnalyticsWeekCommand))]
    [NotifyCanExecuteChangedFor(nameof(CurrentAnalyticsWeekCommand))]
    private bool _isAnalyticsLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRangeWeek))]
    [NotifyPropertyChangedFor(nameof(IsRangeMonth))]
    [NotifyPropertyChangedFor(nameof(IsRangeQuarter))]
    [NotifyPropertyChangedFor(nameof(IsViewingHistoricalAnalyticsWeek))]
    [NotifyCanExecuteChangedFor(nameof(PreviousAnalyticsWeekCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextAnalyticsWeekCommand))]
    [NotifyCanExecuteChangedFor(nameof(CurrentAnalyticsWeekCommand))]
    private int _analyticsRangeDays = RangeMonthDays;

    /// <summary>Zero is the current calendar week; negative values select earlier calendar weeks.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsViewingHistoricalAnalyticsWeek))]
    [NotifyCanExecuteChangedFor(nameof(NextAnalyticsWeekCommand))]
    [NotifyCanExecuteChangedFor(nameof(CurrentAnalyticsWeekCommand))]
    private int _analyticsWeekOffset;

    private DateOnly? _displayedAnalyticsFrom;
    private DateOnly? _displayedAnalyticsTo;

    /// <summary>
    /// Stable selector values retained for commands and bindings. The actual report length is
    /// derived from calendar boundaries and therefore varies by month.
    /// </summary>
    public const int RangeWeekDays = 7;
    public const int RangeMonthDays = 30;
    public const int RangeQuarterDays = 91;

    [ObservableProperty] private bool _hasAnalyticsData;
    [ObservableProperty] private string _analyticsRangeLabel = string.Empty;
    [ObservableProperty] private string _analyticsBusiestHourText = "—";
    [ObservableProperty] private string _analyticsStreakText = "—";
    [ObservableProperty] private string _analyticsOverdueText = "0";

    /// <summary>Name of the window currently shown, for the closed range list.</summary>
    [ObservableProperty] private string _analyticsRangeName = string.Empty;

    /// <summary>Whether the range list is dropped open.</summary>
    [ObservableProperty] private bool _isAnalyticsRangeListOpen;

    /// <summary>The largest column in the daily chart, written above the gridlines.</summary>
    [ObservableProperty] private string _analyticsPeakDayText = "—";

    /// <summary>What one column of the daily chart covers: a day, or a week.</summary>
    [ObservableProperty] private string _analyticsColumnUnitText = string.Empty;

    /// <summary>
    /// The load chart's heading. A card titled "day by day" over thirteen weekly columns is
    /// telling the reader something that is not true, so the title follows the columns.
    /// </summary>
    [ObservableProperty] private string _analyticsLoadTitle = string.Empty;

    /// <summary>Hours behind the donut, printed in the hole in the middle of it.</summary>
    [ObservableProperty] private string _analyticsCategoryTotalText = "—";

    public ObservableCollection<AnalyticsMetricViewModel> AnalyticsMetrics { get; } = [];
    public ObservableCollection<AnalyticsColumnViewModel> AnalyticsColumns { get; } = [];
    public ObservableCollection<AnalyticsSliceViewModel> AnalyticsSlices { get; } = [];
    public ObservableCollection<AnalyticsSliceViewModel> AnalyticsSlicesLeft { get; } = [];
    public ObservableCollection<AnalyticsSliceViewModel> AnalyticsSlicesRight { get; } = [];
    public ObservableCollection<AnalyticsHourViewModel> AnalyticsHours { get; } = [];
    public ObservableCollection<AnalyticsWeekdayViewModel> AnalyticsWeekdays { get; } = [];
    public ObservableCollection<AnalyticsInsightViewModel> AnalyticsInsights { get; } = [];
    public ObservableCollection<AnalyticsRangeOptionViewModel> AnalyticsRangeOptions { get; } = [];

    /// <summary>The horizontal rules the columns are read against, top value first.</summary>
    public ObservableCollection<string> AnalyticsGridlines { get; } = [];

    public bool IsRangeWeek => AnalyticsRangeDays == RangeWeekDays;
    public bool IsRangeMonth => AnalyticsRangeDays == RangeMonthDays;
    public bool IsRangeQuarter => AnalyticsRangeDays == RangeQuarterDays;
    public bool IsViewingHistoricalAnalyticsWeek => IsRangeWeek && AnalyticsWeekOffset < 0;
    public bool HasAnalyticsInsights => AnalyticsInsights.Count > 0;
    public bool HasAnalyticsSlices => AnalyticsSlices.Count > 0;
    public double DonutSize => AnalyticsGeometry.DonutSize;
    public double ColumnChartHeight => AnalyticsGeometry.ColumnHeight;
    public double ColumnStripWidth => AnalyticsGeometry.ColumnStripWidth;
    public double HourStripHeight => AnalyticsGeometry.HourStripHeight;

#pragma warning disable MVVMTK0034
    [RelayCommand]
    private void ToggleAnalytics()
    {
        if (IsAnalyticsOpen) { IsAnalyticsOpen = false; return; }
        // Noted before the panel it replaces is switched off, because afterwards there is no
        // way left to tell a swap from an opening onto nothing.
        AnalyticsTakesAnotherPanelsPlace = IsCalendarPopupOpen || IsSettings || IsChatDocked;
        SetProperty(ref _isCalendarPopupOpen, false, nameof(IsCalendarPopupOpen));
        SetProperty(ref _isSettings, false, nameof(IsSettings));
        CloseChatQuietly();
        IsAnalyticsOpen = true;
    }
#pragma warning restore MVVMTK0034

    [RelayCommand] private void CloseAnalytics() { IsAnalyticsRangeListOpen = false; IsAnalyticsOpen = false; }

    [RelayCommand] private void ToggleAnalyticsRangeList() => IsAnalyticsRangeListOpen = !IsAnalyticsRangeListOpen;
    [RelayCommand] private void CloseAnalyticsRangeList() => IsAnalyticsRangeListOpen = false;

    [RelayCommand]
    private async Task SetAnalyticsRangeAsync(string? days)
    {
        if (IsAnalyticsLoading || !int.TryParse(days, out var value)) return;
        IsAnalyticsRangeListOpen = false;
        AnalyticsRangeDays = Math.Clamp(value, 3, 366);
        AnalyticsWeekOffset = 0;
        RefreshRangeOptionState();
        // A different window is a different answer and is drawn rather than swapped in: the
        // charts go back to the axis so the new report has somewhere to climb out of. Every
        // other refresh lands on charts that are up and changes them where they stand.
        CollapseAnalyticsCharts();
        await RefreshAnalyticsAsync();
    }

    private Task SelectAnalyticsRangeAsync(AnalyticsRangeOptionViewModel option)
        => SetAnalyticsRangeAsync(option.Days.ToString(localization.CurrentCulture));

    private bool CanNavigateAnalyticsWeek() => IsRangeWeek && !IsAnalyticsLoading;
    private bool CanNavigateAnalyticsWeekForward()
        => IsRangeWeek && !IsAnalyticsLoading && AnalyticsWeekOffset < 0;
    private bool CanReturnToCurrentAnalyticsWeek()
        => IsRangeWeek && !IsAnalyticsLoading && AnalyticsWeekOffset < 0;

    [RelayCommand(CanExecute = nameof(CanNavigateAnalyticsWeek))]
    private Task PreviousAnalyticsWeekAsync() => ShiftAnalyticsWeekAsync(-1);

    [RelayCommand(CanExecute = nameof(CanNavigateAnalyticsWeekForward))]
    private Task NextAnalyticsWeekAsync() => ShiftAnalyticsWeekAsync(1);

    [RelayCommand(CanExecute = nameof(CanReturnToCurrentAnalyticsWeek))]
    private Task CurrentAnalyticsWeekAsync() => ShiftAnalyticsWeekAsync(-AnalyticsWeekOffset);

    private async Task ShiftAnalyticsWeekAsync(int offset)
    {
        if (!IsRangeWeek || IsAnalyticsLoading || offset == 0) return;
        AnalyticsWeekOffset = Math.Min(0, AnalyticsWeekOffset + offset);
        CollapseAnalyticsCharts();
        await RefreshAnalyticsAsync();
    }

    /// <summary>
    /// Builds the range list. It replaced three buttons labelled "7 days / 28 days / 90 days" —
    /// true, and unreadable: a list that says "Week", "Month", "3 months" and shows the span
    /// each one covers is the same choice made in the reader's own words.
    /// </summary>
    private void BuildAnalyticsRangeOptions()
    {
        AnalyticsRangeOptions.Clear();
        AnalyticsRangeOptions.Add(new(AnalyticsRange.Week, RangeWeekDays,
            localization.Text("RangeWeek"), localization.Text("RangeDetailCalendarWeek"), SelectAnalyticsRangeAsync));
        AnalyticsRangeOptions.Add(new(AnalyticsRange.Month, RangeMonthDays,
            localization.Text("RangeMonth"), localization.Text("RangeDetailCalendarMonth"), SelectAnalyticsRangeAsync));
        AnalyticsRangeOptions.Add(new(AnalyticsRange.Quarter, RangeQuarterDays,
            localization.Text("RangeQuarter"), localization.Text("RangeDetailCalendarQuarter"), SelectAnalyticsRangeAsync));
        RefreshRangeOptionState();
    }

    private void RefreshRangeOptionState()
    {
        foreach (var option in AnalyticsRangeOptions) option.IsSelected = option.Days == AnalyticsRangeDays;
        AnalyticsRangeName = AnalyticsRangeOptions.FirstOrDefault(option => option.IsSelected)?.Label
                             ?? localization.Format("RangeDetailDays", AnalyticsRangeDays);
    }

    /// <summary>Hands the current window to the assistant so the user can talk about it.</summary>
    [RelayCommand]
    private async Task DiscussAnalyticsAsync()
    {
        IsAnalyticsOpen = false;
        IsChatOpen = true;
        var prompt = _displayedAnalyticsFrom is { } from && _displayedAnalyticsTo is { } to
            ? localization.Format("AnalyticsAskAiPeriodPrompt", from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"))
            : localization.Format("AnalyticsAskAiPrompt", AnalyticsRangeDays);
        await SendQuickPromptAsync(prompt);
    }

    /// <summary>
    /// Builds the report and turns it into shapes. <paramref name="settled"/> is the window's own
    /// arrival: the reading and the arithmetic run at once on a worker, and the six collections —
    /// which are UI-thread work, and the part of this the eye actually notices — are rebuilt only
    /// once the window has finished coming in.
    /// </summary>
    public async Task RefreshAnalyticsAsync(Task? settled = null)
    {
        if (IsAnalyticsLoading) return;
        IsAnalyticsLoading = true;
        try
        {
            if (AnalyticsRangeOptions.Count == 0) BuildAnalyticsRangeOptions();
            var now = DateTimeOffset.Now;
            var days = AnalyticsRangeDays;
            var today = DateOnly.FromDateTime(now.LocalDateTime);
            var period = days switch
            {
                RangeWeekDays => AnalyticsPeriod.ForCalendarWeek(
                    CalendarWeek.Start(today).AddDays(AnalyticsWeekOffset * RangeWeekDays)),
                RangeMonthDays => AnalyticsPeriod.ForCalendarMonths(today, 1),
                RangeQuarterDays => AnalyticsPeriod.ForCalendarMonths(today, 3),
                _ => AnalyticsPeriod.Rolling(today, days)
            };

            // Both the visible and comparison windows are needed for the trend arrows. Seven
            // extra days retain any tracked interval that begins before a boundary and crosses
            // into it; event outcomes are capped at one week when they are recorded.
            var from = LocalDateRange.StartUtc(period.PreviousFrom.AddDays(-7));
            var to = LocalDateRange.StartUtc(period.To.AddDays(1));

            // Five reads over three months of history, and then the arithmetic on top of them.
            // SQLite answers from a local file, which means it answers synchronously: awaited on
            // the UI thread none of these is a wait at all, it is the interface standing still —
            // and that stall was what the analytics window used to open through.
            //
            // The work leaves the thread only when there is an arrival to protect, which is the
            // same rule the calendar and the week board follow, and for the same reason: that is
            // also the only time there is certainly a message loop to come back to. The snapshot
            // sweep runs before one exists, and a hop to the pool there posts its continuation to
            // a dispatcher nobody is running — which is exactly where changing the range hung.
            async Task<AnalyticsReport> ReadHistoryAsync() => ScheduleAnalytics.Build(new AnalyticsInput(
                now, days,
                await calendarRepository.GetRangeAsync(from, to),
                await blockRepository.GetHistoryAsync(from, to),
                await executionRepository.GetRecentAsync(from),
                await taskRepository.GetAllAsync(),
                Outcomes: await outcomeRepository.GetRangeAsync(from, to),
                Period: period));

            AnalyticsReport report;
            if (settled is null)
            {
                report = await ReadHistoryAsync();
            }
            else
            {
                report = await Task.Run(ReadHistoryAsync);
                await settled;
                // Switched away from while the panel was arriving. Laying six collections out
                // for a window nobody is looking at costs a full layout on the same thread the
                // panel that replaced it is animating in on, and a run of quick switches used
                // to queue one of those up behind every press.
                if (!IsAnalyticsOpen) return;
            }

            Apply(report, now);
        }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Building analytics");
            Status = exception.Message;
        }
        finally { IsAnalyticsLoading = false; }
    }

    /// <summary>
    /// Puts a freshly built list into a bound collection without ever emptying it.
    ///
    /// Clearing an <see cref="ObservableCollection{T}"/> takes every container in the control
    /// bound to it with it, and the layout closes over the gap before the replacements are
    /// added — so a report arriving while the panel was on screen showed the whole surface fall
    /// in on itself and build itself back up. That is what the reader saw on every switch onto
    /// this panel: not the charts drawing, the charts being taken away first.
    ///
    /// Replacing position by position keeps the layout standing the whole way through. Each row
    /// changes where it already is, and only a report that is a different length at all grows or
    /// shrinks, at its end, by the difference.
    /// </summary>
    private static void Refill<T>(ObservableCollection<T> target, IReadOnlyList<T> fresh)
    {
        for (var index = 0; index < fresh.Count; index++)
        {
            if (index >= target.Count) { target.Add(fresh[index]); continue; }
            // A row that would come back looking exactly the same is left where it is. The
            // rebuilt object is the cheap half of a refresh; handing it to the collection is
            // the expensive one, because every replaced row takes its controls down and builds
            // another set. See IRowEquivalence.
            if (target[index] is IRowEquivalence standing && standing.Matches(fresh[index]!)) continue;
            target[index] = fresh[index];
        }

        while (target.Count > fresh.Count) target.RemoveAt(target.Count - 1);
    }

    private void Apply(AnalyticsReport report, DateTimeOffset now)
    {
        // Charts grow into place only when the panel the user is looking at asked for them.
        // Off-screen renders and a reduced-motion setting both want the finished shape at once —
        // and so does a report landing on charts that are already up, whichever way the panel
        // was reached. An arrival played over standing charts is not an arrival: it drops every
        // bar to the axis first so it has somewhere to climb back from, and what the reader
        // sees is the answer being taken away and given back. Standing charts are changed where
        // they stand; see CollapseAnalyticsChartsIfUnreachable for what takes them down.
        var animate = IsAnalyticsOpen && !ReduceMotion && !AnalyticsChartsAreStanding;
        CancelAnalyticsReveal();

        HasAnalyticsData = report.HasData;
        _displayedAnalyticsFrom = report.From;
        _displayedAnalyticsTo = report.To;
        AnalyticsRangeLabel = $"{report.From.ToString("d MMM", localization.CurrentCulture)} — " +
                              $"{report.To.ToString("d MMM yyyy", localization.CurrentCulture)}";
        RefreshRangeOptionState();

        var hoursUnit = localization.Format("MetricHours", string.Empty).Trim();
        Refill(AnalyticsMetrics,
        [
            new(localization.Text("MetricScheduled"),
                $"{report.ScheduledHours.Value:0.#}", hoursUnit, report.ScheduledHours, localization),
            new(localization.Text("MetricFocus"),
                $"{report.FocusHours.Value:0.#}", hoursUnit, report.FocusHours, localization),
            new(localization.Text("MetricCompletion"),
                $"{report.CompletionRate.Value * 100:0}", "%", report.CompletionRate, localization, asPercent: true),
            new(localization.Text("MetricAccuracy"),
                $"{report.PlanAccuracy.Value * 100:0}", "%", report.PlanAccuracy, localization, asPercent: true),
            new(localization.Text("MetricOnTime"),
                $"{report.OnTimeStartRate.Value * 100:0}", "%", report.OnTimeStartRate, localization, asPercent: true),
            // A rising load is not automatically good, so the arrow is coloured the other way round.
            new(localization.Text("MetricLoad"),
                $"{report.Load.Value * 100:0}", "%", report.Load, localization, higherIsBetter: false, asPercent: true)
        ]);

        AnalyticsStreakText = localization.Format("MetricDays", report.CurrentStreakDays);
        AnalyticsBusiestHourText = report.BusiestHour < 0
            ? "—"
            : ClockText.Format(TimeSpan.FromHours(report.BusiestHour), Use24HourClock, localization.CurrentCulture);
        // Task rows keep their current status, not a status-change history. Showing today's
        // overdue count inside an older weekly report would mix two different dates and look
        // historical when it is not, so that one non-reconstructable fact is left unspecified.
        AnalyticsOverdueText = IsViewingHistoricalAnalyticsWeek
            ? "—"
            : ScheduleAnalytics
                .OverdueTaskCount([.. Tasks.Select(item => item.Task)], now)
                .ToString(localization.CurrentCulture);

        BuildDailyChart(report, animate);
        BuildCategoryDonut(report, animate);
        BuildHourStrip(report, animate);
        BuildWeekdayBars(report, animate);

        Refill(AnalyticsInsights,
            [.. report.Insights.Select(insight => new AnalyticsInsightViewModel(insight, localization, DiscussInsightAsync))]);
        OnPropertyChanged(nameof(HasAnalyticsInsights));

        if (animate) StartAnalyticsReveal();
    }

    /// <summary>
    /// The day-by-day chart. It used to draw one fixed-width bar pair per day and then quietly
    /// throw away everything before the last thirty-one — so the ninety-day range was labelled
    /// ninety days and showed a month, drawn in bars too wide to fit the card. The series is
    /// folded into as many columns as the strip can hold, and the columns size themselves.
    /// </summary>
    private void BuildDailyChart(AnalyticsReport report, bool animate)
    {
        var buckets = AnalyticsSeries.Bucket(report.Daily, 32);
        var maxMinutes = buckets.Count == 0
            ? 0
            : buckets.Max(bucket => Math.Max(bucket.ScheduledMinutes, bucket.CompletedMinutes));
        var barWidth = AnalyticsSeries.ColumnWidth(buckets.Count, AnalyticsGeometry.ColumnStripWidth);
        var step = AnalyticsGeometry.LabelStep(buckets.Count);

        var columns = new List<AnalyticsColumnViewModel>(buckets.Count);
        for (var index = 0; index < buckets.Count; index++)
        {
            // Counted back from the newest column so the last one always carries a label.
            var showLabel = (buckets.Count - 1 - index) % step == 0;
            columns.Add(new(buckets[index], maxMinutes, barWidth, showLabel, localization, !animate));
        }

        Refill(AnalyticsColumns, columns);

        var gridlines = new List<string>();
        if (maxMinutes > 0)
            for (var line = (int)AnalyticsGeometry.GridlineCount; line > 0; line--)
                // A scale is read, not consulted: one decimal place, and never "8.183333 h".
                gridlines.Add(localization.Format("MetricHours",
                    (maxMinutes / 60d * line / AnalyticsGeometry.GridlineCount)
                        .ToString("0.#", localization.CurrentCulture)));
        Refill(AnalyticsGridlines, gridlines);

        AnalyticsPeakDayText = maxMinutes <= 0 ? "—" : localization.Format("MetricHours", maxMinutes / 60d);
        var perDay = buckets.Count == 0 || buckets[0].IsSingleDay;
        AnalyticsColumnUnitText = localization.Text(perDay ? "ChartPerDay" : "ChartPerWeek");
        AnalyticsLoadTitle = localization.Text(perDay ? "ChartDailyLoad" : "ChartWeeklyLoad");
    }

    private void BuildCategoryDonut(AnalyticsReport report, bool animate)
    {
        var slices = new List<AnalyticsSliceViewModel>(6);
        var offset = 0d;
        foreach (var slice in report.Categories.Take(6))
        {
            slices.Add(new(slice, offset, SliceBrush(slice.Key), SliceLabel(slice.Key), localization, !animate));
            offset += slice.Share;
        }

        var leftCount = (slices.Count + 1) / 2;
        Refill(AnalyticsSlices, slices);
        Refill(AnalyticsSlicesLeft, slices.Take(leftCount).ToList());
        Refill(AnalyticsSlicesRight, slices.Skip(leftCount).ToList());
        AnalyticsCategoryTotalText = report.Categories.Count == 0
            ? "—"
            : localization.Format("MetricHours", report.Categories.Sum(slice => slice.Minutes) / 60d);
        OnPropertyChanged(nameof(HasAnalyticsSlices));
    }

    private void BuildHourStrip(AnalyticsReport report, bool animate)
    {
        var maxHour = report.Hours.Count == 0 ? 0 : report.Hours.Max(hour => hour.Minutes);
        Refill(AnalyticsHours,
        [
            .. report.Hours.Select(hour => new AnalyticsHourViewModel(hour, maxHour, hour.Hour % 6 == 0,
                maxHour > 0 && hour.Minutes == maxHour, localization, !animate))
        ]);
    }

    private void BuildWeekdayBars(AnalyticsReport report, bool animate)
    {
        var maxWeekday = report.Weekdays.Count == 0 ? 0 : report.Weekdays.Max(day => day.Minutes);
        Refill(AnalyticsWeekdays,
            [.. report.Weekdays.Select(day => new AnalyticsWeekdayViewModel(day, maxWeekday, localization, !animate))]);
    }

    // ---------------------------------------------------------------- the reveal

    /// <summary>
    /// Lets the charts draw themselves. Every bar carries a style transition on the property
    /// this flips, so all this has to do is walk the columns from left to right and hand each
    /// one its value a few milliseconds after the last. Cheap, and it says something true: the
    /// chart is being read out in the order the days happened.
    /// </summary>
    private void StartAnalyticsReveal()
    {
        _analyticsReveal = new CancellationTokenSource();
        var token = _analyticsReveal.Token;
        // Walked over copies, not over the bound collections themselves. This spends most of its
        // life waiting between one bar and the next, and a report landing during one of those
        // waits rewrites the collections underneath it — which threw where the loop asked for the
        // bar after the one it had just lit, before it ever got as far as noticing it had been
        // cancelled. The reveal died there, and every bar it had not reached yet stayed flat: the
        // charts the reader watched go missing were the ones this loop never came back for.
        var columns = AnalyticsColumns.ToArray();
        var hours = AnalyticsHours.ToArray();
        var weekdays = AnalyticsWeekdays.ToArray();
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                // The ring draws itself alongside the bars rather than after them: both are the
                // same report arriving, and one waiting politely for the other is two events.
                _ = SweepDonutAsync(1, DonutDrawDuration, token);
                foreach (var column in columns)
                {
                    if (token.IsCancellationRequested) return;
                    column.IsRevealed = true;
                    await Task.Delay(columns.Length > 20 ? 8 : 26, token);
                }
                foreach (var hour in hours)
                {
                    if (token.IsCancellationRequested) return;
                    hour.IsRevealed = true;
                    await Task.Delay(8, token);
                }
                foreach (var day in weekdays)
                {
                    if (token.IsCancellationRequested) return;
                    day.IsRevealed = true;
                    await Task.Delay(30, token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) { crashReporter.Record(exception, "Revealing the analytics charts"); }
        }, DispatcherPriority.Background);
    }

    private void CancelAnalyticsReveal()
    {
        _analyticsReveal?.Cancel();
        _analyticsReveal?.Dispose();
        _analyticsReveal = null;
    }

    /// <summary>How long the ring takes to come round once, and how long it takes to go back.</summary>
    private static readonly TimeSpan DonutDrawDuration = TimeSpan.FromMilliseconds(640);
    private static readonly TimeSpan DonutEraseDuration = TimeSpan.FromMilliseconds(180);

    /// <summary>
    /// Walks the ring's own progress from one end to the other and hands every wedge whatever
    /// of it has reached them. <paramref name="target"/> is 1 for drawing and 0 for erasing;
    /// the wedges clamp their own share of it, so a single number carries the whole circle.
    ///
    /// The wedges together may not add up to a full turn — only the six largest categories are
    /// shown — so the sweep runs to wherever the last one ends rather than to twelve o'clock,
    /// and does not spend its last frames drawing nothing.
    /// </summary>
    private async Task SweepDonutAsync(double target, TimeSpan duration, CancellationToken token)
    {
        if (AnalyticsSlices.Count == 0) return;
        var slices = AnalyticsSlices.ToArray();
        var circle = slices[^1].EndFraction;
        if (circle <= 0) return;
        var from = target > 0 ? 0d : circle;
        var to = target > 0 ? circle : 0d;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while (clock.Elapsed < duration)
            {
                token.ThrowIfCancellationRequested();
                var progress = Smooth(clock.Elapsed / duration);
                var reached = from + (to - from) * progress;
                foreach (var slice in slices) slice.Draw(reached - slice.StartFraction);
                await Task.Delay(16, token);
            }

            token.ThrowIfCancellationRequested();
            foreach (var slice in slices) slice.Draw(to - slice.StartFraction);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { crashReporter.Record(exception, "Drawing the category ring"); }
    }

    /// <summary>Eased at both ends: the stroke sets off, travels, and arrives rather than stops.</summary>
    private static double Smooth(double value)
    {
        value = Math.Clamp(value, 0, 1);
        return value * value * (3 - 2 * value);
    }

    /// <summary>
    /// Takes every chart back to the axis. The panel closing used to leave the charts standing
    /// at their full height behind it, so the next opening showed a finished chart for as long
    /// as the new report took to build, then dropped it to zero and drew it again — the reader
    /// watching the answer be taken away and given back. They now leave with the panel, which is
    /// also what makes the next arrival an arrival.
    /// </summary>
    public void CollapseAnalyticsCharts()
    {
        CancelAnalyticsReveal();
        foreach (var column in AnalyticsColumns.ToArray()) column.IsRevealed = false;
        foreach (var hour in AnalyticsHours.ToArray()) hour.IsRevealed = false;
        foreach (var day in AnalyticsWeekdays.ToArray()) day.IsRevealed = false;
        if (AnalyticsSlices.Count == 0) return;
        if (ReduceMotion)
        {
            foreach (var slice in AnalyticsSlices.ToArray()) slice.IsRevealed = false;
            return;
        }

        _analyticsReveal = new CancellationTokenSource();
        _ = SweepDonutAsync(0, DonutEraseDuration, _analyticsReveal.Token);
    }

    /// <summary>
    /// Takes the charts down once nothing on screen could still be showing them.
    ///
    /// Leaving this panel for the calendar or the settings deliberately leaves the charts
    /// standing: the two panels take each other's place, and a switch back should find the
    /// answer where it was left rather than watch it be drawn again. Nothing was watching the
    /// panel the charts were handed to, though — close the calendar and the shell is back to
    /// showing no panel at all, with a set of finished charts still held behind it. Opening the
    /// analytics from there is a fresh arrival by every measure the panel has, so it played one:
    /// the standing charts dropped to the axis and climbed back up in front of the reader.
    ///
    /// So the charts leave with whatever was left holding their place. Called wherever a
    /// surface goes away, it does nothing at all until the last one has.
    /// </summary>
    public void CollapseAnalyticsChartsIfUnreachable()
    {
        if (IsShellFlyoutOpen || !AnalyticsChartsAreStanding) return;
        CollapseAnalyticsCharts();
    }

    /// <summary>
    /// Puts every chart straight into its finished shape. Used by the offscreen snapshot sweep,
    /// which photographs the window in the same turn it builds it and would otherwise catch the
    /// bars halfway up.
    /// </summary>
    public void SettleAnalyticsCharts()
    {
        CancelAnalyticsReveal();
        foreach (var column in AnalyticsColumns.ToArray()) column.IsRevealed = true;
        foreach (var slice in AnalyticsSlices.ToArray()) slice.IsRevealed = true;
        foreach (var hour in AnalyticsHours.ToArray()) hour.IsRevealed = true;
        foreach (var day in AnalyticsWeekdays.ToArray()) day.IsRevealed = true;
    }

    private async Task DiscussInsightAsync(AnalyticsInsightViewModel insight)
    {
        IsAnalyticsOpen = false;
        IsChatOpen = true;
        await SendQuickPromptAsync(insight.Text);
    }

    private static IBrush SliceBrush(string key) => key == "Focus"
        ? EventVisuals.FocusBrush
        : EventVisuals.AccentBrush(Enum.TryParse<EventCategory>(key, out var category) ? category : EventCategory.General);

    private string SliceLabel(string key) => key == "Focus"
        ? localization.Text("CategoryFocus")
        : localization.Text(EventVisuals.CategoryKey(
            Enum.TryParse<EventCategory>(key, out var category) ? category : EventCategory.General));
}
