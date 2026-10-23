using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using IntelligentScheduler.Desktop.ViewModels;
using IntelligentScheduler.Application;

namespace IntelligentScheduler.Desktop;

public sealed partial class CalendarView : UserControl
{
    private const double SchedulerDayWidth = 78;
    private const double SchedulerHourHeight = 52;
    private const double OneCycleHeight = 24 * SchedulerHourHeight;
    private const double TotalTimelineHeight = 3 * OneCycleHeight;

    /// <summary>The finer grid Shift asks for, in minutes; see <see cref="CalendarSnapGrid"/>.</summary>
    private int _freeStepMinutes = CalendarSnapGrid.FreeSteps[0];
    private bool _fineMode;

    private Point? _selectionStart;
    private double _selectionAnchorY;
    private int _selectionDay;
    private int _selectionStartCycle;
    private SchedulerBlockViewModel? _dragBlock;
    private Point _dragPointerStart;
    private double _dragInitialTop;
    private int _dragDay;
    private int _dragDurationMinutes;
    private bool _dragIsResize;
    private bool _dragMoved;
    private bool _scrollInteractionStarted;
    private bool _scrollThumbDragging;

    private readonly DispatcherTimer _autoScrollTimer;
    private double _autoScrollVelocity;
    private Point _lastSelectionSurfacePos;
    private Point _lastTaskBlockSurfacePos;
    private Vector _savedScrollOffset = new(0, OneCycleHeight + 8 * SchedulerHourHeight);
    private bool _hasInitialFocusBeenSet;
    private readonly DispatcherTimer _nowTimer;
    private EdgeShellViewModel? _boundShell;



    public CalendarView()
    {
        InitializeComponent();
        _autoScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _autoScrollTimer.Tick += AutoScrollTimer_OnTick;

        // The "now" marker only has to be accurate to the minute.
        _nowTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _nowTimer.Tick += (_, _) => UpdateNowIndicator();
        AttachedToVisualTree += (_, _) => { _nowTimer.Start(); UpdateNowIndicator(); };
        DetachedFromVisualTree += (_, _) => { _nowTimer.Stop(); _autoScrollTimer.Stop(); };
        DataContextChanged += (_, _) => UpdateNowIndicator();
        SchedulerScroll.AddHandler(PointerPressedEvent, (_, _) => _scrollInteractionStarted = true,
            Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        SchedulerScroll.AddHandler(PointerWheelChangedEvent, (_, _) => _scrollInteractionStarted = true,
            Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        // Shift+wheel belongs to the drag, not to the timeline: caught on the way down so the
        // scroll viewer never sees it and the board stays where the drag left it.
        SchedulerScroll.AddHandler(PointerWheelChangedEvent, SchedulerScroll_OnStepWheel,
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
        SchedulerScroll.AddHandler(Thumb.DragStartedEvent, (_, _) =>
        {
            _scrollInteractionStarted = true;
            _scrollThumbDragging = true;
        }, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        SchedulerScroll.AddHandler(Thumb.DragCompletedEvent, (_, _) =>
        {
            _scrollThumbDragging = false;
            if (SchedulerScroll is { } scroll) NormalizeScrollOffset(scroll);
        }, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);

        AttachedToVisualTree += (_, _) => RestoreScrollWhenLaidOut();

        PropertyChanged += (_, args) =>
        {
            if (args.Property == IsVisibleProperty && IsVisible) RestoreScrollWhenLaidOut();
        };

        DataContextChanged += (_, _) =>
        {
            if (_boundShell is not null) _boundShell.SchedulerWeekReady -= Shell_OnSchedulerWeekReady;
            _boundShell = DataContext as EdgeShellViewModel;
            if (_boundShell is not null) _boundShell.SchedulerWeekReady += Shell_OnSchedulerWeekReady;
        };
    }

    private void RestoreScrollWhenLaidOut()
    {
        if (SchedulerScroll is not { } sv) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (sv.Extent.Height <= 0 || _scrollInteractionStarted) return;
            // The board is only built once the panel has been asked for, and it arrives over
            // several turns, so there is usually nothing on it yet to aim at. What can be done
            // now is done now; the rest waits for the week to land - see below.
            if (!_hasInitialFocusBeenSet)
            {
                _savedScrollOffset = new Vector(sv.Offset.X, CalculateInitialFocusOffset());
                _hasInitialFocusBeenSet = HasFocusTarget;
            }
            sv.Offset = _savedScrollOffset;
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// The week is known. It is only built once the panel has been asked for, so at the moment
    /// the panel opens there is nothing yet to aim at; this is that moment, and it comes before
    /// the blocks themselves so the board never has to jump once they land.
    /// </summary>
    private void Shell_OnSchedulerWeekReady(object? sender, EventArgs e)
    {
        if (_hasInitialFocusBeenSet || _scrollInteractionStarted || !HasFocusTarget) return;
        if (SchedulerScroll is not { } sv || sv.Extent.Height <= 0) return;
        _savedScrollOffset = new Vector(sv.Offset.X, CalculateInitialFocusOffset());
        _hasInitialFocusBeenSet = true;
        sv.Offset = _savedScrollOffset;
    }

    private bool HasFocusTarget
        => DataContext is EdgeShellViewModel shell && shell.SchedulerFocusTop is not null;

    private double CalculateInitialFocusOffset()
    {
        // The shell works this out as it builds the week; the board itself is still filling.
        if (DataContext is EdgeShellViewModel vm && vm.SchedulerFocusTop is { } earliest)
            return Math.Clamp(earliest - SchedulerHourHeight, OneCycleHeight, 2 * OneCycleHeight);

        var nowHours = DateTime.Now.TimeOfDay.TotalHours;
        var focusHour = Math.Clamp(nowHours - 1.5, 0, 23);
        return OneCycleHeight + focusHour * SchedulerHourHeight;
    }



    private void AutoScrollTimer_OnTick(object? sender, EventArgs e)
    {
        if (Math.Abs(_autoScrollVelocity) < 0.01 || SchedulerScroll is null)
        {
            _autoScrollTimer.Stop();
            return;
        }

        var maxScroll = Math.Max(0, TotalTimelineHeight - SchedulerScroll.Bounds.Height);
        var newY = Math.Clamp(SchedulerScroll.Offset.Y + _autoScrollVelocity, 0, maxScroll);
        SchedulerScroll.Offset = new Vector(SchedulerScroll.Offset.X, newY);
        _savedScrollOffset = SchedulerScroll.Offset;

        if (_selectionStart is not null)
        {
            _lastSelectionSurfacePos = new Point(
                _lastSelectionSurfacePos.X,
                Math.Clamp(_lastSelectionSurfacePos.Y + _autoScrollVelocity, 0, TotalTimelineHeight));
            var currentDay = Math.Clamp((int)(_lastSelectionSurfacePos.X / SchedulerDayWidth), 0, 6);
            UpdateSelectionPreview(_selectionDay, _selectionStart.Value.Y, currentDay, SnapY(_lastSelectionSurfacePos.Y));
        }
        else if (_dragBlock is not null)
        {
            _lastTaskBlockSurfacePos = new Point(
                _lastTaskBlockSurfacePos.X,
                Math.Clamp(_lastTaskBlockSurfacePos.Y + _autoScrollVelocity, 0, TotalTimelineHeight));
            ProcessTaskBlockDragAt(_lastTaskBlockSurfacePos);
        }
    }

    private void CheckAutoScroll(PointerEventArgs e)
    {
        if (SchedulerScroll is null) return;
        var viewY = e.GetPosition(SchedulerScroll).Y;
        var viewHeight = SchedulerScroll.Bounds.Height;
        if (viewHeight <= 0) return;

        const double edgeThreshold = 48;
        const double maxSpeed = 14;

        if (viewY < edgeThreshold && viewY >= -100)
        {
            var factor = Math.Clamp((edgeThreshold - viewY) / edgeThreshold, 0.1, 1.0);
            _autoScrollVelocity = -factor * maxSpeed;
            if (!_autoScrollTimer.IsEnabled) _autoScrollTimer.Start();
        }
        else if (viewY > viewHeight - edgeThreshold && viewY <= viewHeight + 100)
        {
            var factor = Math.Clamp((viewY - (viewHeight - edgeThreshold)) / edgeThreshold, 0.1, 1.0);
            _autoScrollVelocity = factor * maxSpeed;
            if (!_autoScrollTimer.IsEnabled) _autoScrollTimer.Start();
        }
        else
        {
            _autoScrollVelocity = 0;
            _autoScrollTimer.Stop();
        }
    }


    /// <summary>
    /// Draws the current-time marker in every repeated day of the seamless timeline.
    /// </summary>
    private void UpdateNowIndicator()
    {
        if (SchedulerNowLayer is null) return;
        SchedulerNowLayer.Children.Clear();
        if (DataContext is not EdgeShellViewModel vm) return;

        var today = DateOnly.FromDateTime(DateTime.Today);
        var index = vm.SchedulerWeekDays.Select((day, position) => (day, position))
            .FirstOrDefault(item => item.day.Date == today, (null!, -1)).position;
        if (index < 0) return;

        var minutes = DateTime.Now.TimeOfDay.TotalMinutes;
        var left = index * SchedulerDayWidth;
        for (var cycle = 0; cycle < 3; cycle++)
        {
            var top = minutes / 60d * SchedulerHourHeight + cycle * OneCycleHeight;
            var line = new Border { Width = SchedulerDayWidth, IsHitTestVisible = false };
            line.Classes.Add("nowLine");
            Canvas.SetLeft(line, left);
            Canvas.SetTop(line, top);
            SchedulerNowLayer.Children.Add(line);

            var dot = new Border { IsHitTestVisible = false };
            dot.Classes.Add("nowDot");
            Canvas.SetLeft(dot, left - 3);
            Canvas.SetTop(dot, top - 3);
            SchedulerNowLayer.Children.Add(dot);
        }
    }

    private void StopAutoScroll()
    {
        _autoScrollVelocity = 0;
        _autoScrollTimer.Stop();
    }

    private void SchedulerScrollViewer_OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        UpdateNowIndicator();
        if (double.IsFinite(sv.Offset.Y)) _savedScrollOffset = sv.Offset;
        if (_selectionStart is not null || _dragBlock is not null || _scrollThumbDragging) return;
        NormalizeScrollOffset(sv);
    }

    private void NormalizeScrollOffset(ScrollViewer sv)
    {
        if (sv.Bounds.Height <= 0 || sv.Extent.Height <= 0 || _scrollThumbDragging) return;
        if (sv.Offset.Y < OneCycleHeight * 0.5)
        {
            sv.Offset = new Vector(sv.Offset.X, sv.Offset.Y + OneCycleHeight);
            _savedScrollOffset = sv.Offset;
        }
        else if (sv.Offset.Y > OneCycleHeight * 1.5)
        {
            sv.Offset = new Vector(sv.Offset.X, sv.Offset.Y - OneCycleHeight);
            _savedScrollOffset = sv.Offset;
        }
    }

    private async void Scheduler_OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (DataContext is not EdgeShellViewModel vm || Math.Abs(e.Delta.Y) < 0.01) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            AdjustFreeStep(e.Delta.Y > 0 ? 1 : -1);
            return;
        }
        e.Handled = true;
        if (e.Delta.Y > 0 && vm.PreviousWeekCommand.CanExecute(null)) await vm.PreviousWeekCommand.ExecuteAsync(null);
        else if (e.Delta.Y < 0 && vm.NextWeekCommand.CanExecute(null)) await vm.NextWeekCommand.ExecuteAsync(null);
    }

    private void SchedulerSelection_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Canvas surface || !e.GetCurrentPoint(surface).Properties.IsLeftButtonPressed) return;
        var position = e.GetPosition(surface);
        _fineMode = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        _selectionDay = Math.Clamp((int)(position.X / SchedulerDayWidth), 0, 6);
        // The unrounded press is kept: Shift taken up or put down mid-drag re-rounds both ends
        // against the step now in force, instead of leaving the start on whichever grid the
        // drag happened to begin on.
        _selectionAnchorY = position.Y;
        var snappedY = SnapY(position.Y);
        _selectionStart = new Point(_selectionDay * SchedulerDayWidth, snappedY);
        _selectionStartCycle = CycleAt(snappedY);
        _lastSelectionSurfacePos = position;
        e.Pointer.Capture(surface);
        UpdateSelectionPreview(_selectionDay, snappedY, _selectionDay, snappedY + SchedulerHourHeight / 2);
        e.Handled = true;
    }

    private void SchedulerSelection_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_selectionStart is null || sender is not Canvas surface) return;
        var position = e.GetPosition(surface);
        _lastSelectionSurfacePos = position;
        _fineMode = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        RefreshSelectionAnchor();
        var currentDay = Math.Clamp((int)(position.X / SchedulerDayWidth), 0, 6);
        UpdateSelectionPreview(_selectionDay, _selectionStart.Value.Y, currentDay, SnapY(position.Y));
        CheckAutoScroll(e);
        e.Handled = true;
    }

    private void SchedulerSelection_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        StopAutoScroll();
        if (_selectionStart is null || sender is not Canvas surface || DataContext is not EdgeShellViewModel vm) return;

        var position = _lastSelectionSurfacePos;
        _fineMode = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        RefreshSelectionAnchor();
        var currentDay = Math.Clamp((int)Math.Floor(position.X / SchedulerDayWidth), 0, 6);
        var currentY = SnapY(position.Y);
        var range = GetSelectionRange(currentDay, currentY);

        _selectionStart = null;
        ClearSelectionPreview();
        e.Pointer.Capture(null);
        if (SchedulerScroll is { } sv) NormalizeScrollOffset(sv);
        vm.BeginSchedulerEventCreate(
            range.StartDayIndex,
            range.StartMinuteOfDay,
            range.EndDayIndex,
            range.EndMinuteOfDay);
        e.Handled = true;
    }

    /// <summary>Re-rounds the pressed point against whichever step is in force right now.</summary>
    private void RefreshSelectionAnchor()
    {
        if (_selectionStart is null) return;
        var snapped = SnapY(_selectionAnchorY);
        _selectionStart = new Point(_selectionStart.Value.X, snapped);
        _selectionStartCycle = CycleAt(snapped);
    }

    /// <summary>
    /// Walks the free step up or down the ladder while Shift is held, and redraws whatever is
    /// under the pointer so the new grid is visible before the button comes up.
    /// </summary>
    private void AdjustFreeStep(int direction)
    {
        _fineMode = true;
        _freeStepMinutes = CalendarSnapGrid.StepAfter(_freeStepMinutes, direction);
        if (_selectionStart is not null)
        {
            RefreshSelectionAnchor();
            var currentDay = Math.Clamp((int)(_lastSelectionSurfacePos.X / SchedulerDayWidth), 0, 6);
            UpdateSelectionPreview(_selectionDay, _selectionStart.Value.Y, currentDay, SnapY(_lastSelectionSurfacePos.Y));
        }
        else if (_dragBlock is not null) ProcessTaskBlockDragAt(_lastTaskBlockSurfacePos);
    }

    private void SchedulerScroll_OnStepWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Shift) || Math.Abs(e.Delta.Y) < 0.01) return;
        e.Handled = true;
        AdjustFreeStep(e.Delta.Y > 0 ? 1 : -1);
    }

    private void SchedulerSelection_OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        StopAutoScroll();
        _selectionStart = null;
        ClearSelectionPreview();
        if (SchedulerScroll is { } sv) NormalizeScrollOffset(sv);
    }

    private void TaskBlock_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not SchedulerBlockViewModel { CanDrag: true } block ||
            !e.GetCurrentPoint(button).Properties.IsLeftButtonPressed) return;
        _dragBlock = block;
        _dragPointerStart = e.GetPosition(SchedulerSelectionSurface);
        _lastTaskBlockSurfacePos = _dragPointerStart;
        _dragInitialTop = block.Top;
        _dragDay = Math.Clamp((int)Math.Floor(block.Left / SchedulerDayWidth), 0, 6);
        _dragDurationMinutes = block.DurationMinutes;
        _fineMode = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        _dragIsResize = e.GetPosition(button).Y >= Math.Max(0, button.Bounds.Height - 11);
        _dragMoved = false;
        e.Pointer.Capture(button);
        UpdateTaskBlockDragPreview(_dragDay, _dragInitialTop, _dragDurationMinutes);
        e.Handled = true;
    }

    private void TaskBlock_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragBlock is null || sender is not Button) return;
        var current = e.GetPosition(SchedulerSelectionSurface);
        _lastTaskBlockSurfacePos = current;
        var delta = current - _dragPointerStart;
        if (Math.Abs(delta.X) > 3 || Math.Abs(delta.Y) > 3) _dragMoved = true;
        if (!_dragMoved) return;

        ProcessTaskBlockDragAt(current);
        CheckAutoScroll(e);
        e.Handled = true;
    }

    private void ProcessTaskBlockDragAt(Point current)
    {
        if (_dragBlock is null) return;
        var delta = current - _dragPointerStart;
        if (_dragIsResize)
        {
            var bottom = SnapY(_dragInitialTop + _dragDurationMinutes / 60d * SchedulerHourHeight + delta.Y);
            var duration = Math.Max(1, (int)Math.Round((bottom - _dragInitialTop) / SchedulerHourHeight * 60));
            UpdateTaskBlockDragPreview(_dragDay, _dragInitialTop, duration);
        }
        else
        {
            var centerX = _dragBlock.Left + _dragBlock.Width / 2 + delta.X;
            var day = Math.Clamp((int)Math.Floor(centerX / SchedulerDayWidth), 0, 6);
            var height = _dragDurationMinutes / 60d * SchedulerHourHeight;
            var top = Math.Clamp(SnapY(_dragInitialTop + delta.Y), 0, TotalTimelineHeight - height);
            UpdateTaskBlockDragPreview(day, top, _dragDurationMinutes);
        }
    }

    private void TaskBlock_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        StopAutoScroll();
        if (_dragBlock is null || sender is not Button button || DataContext is not EdgeShellViewModel vm) return;
        var block = _dragBlock;
        var moved = _dragMoved;
        var day = _dragDay;
        var top = Canvas.GetTop(TaskBlockDragPreview);
        var duration = _dragDurationMinutes;
        ClearTaskBlockDrag(button, e.Pointer);
        if (SchedulerScroll is { } sv) NormalizeScrollOffset(sv);
        if (!moved)
        {
            if (block.TimeBlock is { } clicked) vm.ShowTaskBlock(clicked);
        }
        else if (block.TimeBlock is { } changed)
        {
            var startMinutes = YToMinutes(top);
            vm.BeginTaskBlockGridChange(changed, day, startMinutes, startMinutes + duration);
        }
        e.Handled = true;
    }

    private void TaskBlock_OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        StopAutoScroll();
        _dragBlock = null;
        _dragMoved = false;
        TaskBlockDragPreview.IsVisible = false;
        TaskBlockDragNextDayPreview.IsVisible = false;
        if (SchedulerScroll is { } sv) NormalizeScrollOffset(sv);
    }

    private void UpdateTaskBlockDragPreview(int day, double top, int durationMinutes)
    {
        _dragDay = Math.Clamp(day, 0, 6);
        _dragDurationMinutes = Math.Max(1, durationMinutes);

        var startMinute = YToMinutes(top);
        var endMinute = startMinute + _dragDurationMinutes;

        if (endMinute > 1440 && _dragDay < 6)
        {
            var cycleBase = Math.Floor(top / OneCycleHeight) * OneCycleHeight;
            var day1Bottom = cycleBase + OneCycleHeight;
            var day1Height = Math.Max(22, day1Bottom - top - 2);

            Canvas.SetLeft(TaskBlockDragPreview, _dragDay * SchedulerDayWidth + 2);
            Canvas.SetTop(TaskBlockDragPreview, top);
            TaskBlockDragPreview.Height = day1Height;
            var start1 = TimeSpan.FromMinutes(startMinute);
            TaskBlockDragLabel.Text = DataContext is EdgeShellViewModel vm1
                ? $"{vm1.FormatClockTime(start1)}–24:00"
                : $"{start1:hh\\:mm}–24:00";
            TaskBlockDragPreview.IsVisible = true;

            var nextDay = _dragDay + 1;
            var nextDayMinutes = endMinute - 1440;
            var day2Height = Math.Max(22, nextDayMinutes / 60d * SchedulerHourHeight - 2);
            var day2Top = cycleBase + OneCycleHeight;

            Canvas.SetLeft(TaskBlockDragNextDayPreview, nextDay * SchedulerDayWidth + 2);
            Canvas.SetTop(TaskBlockDragNextDayPreview, day2Top);
            TaskBlockDragNextDayPreview.Height = day2Height;
            var end2 = TimeSpan.FromMinutes(nextDayMinutes);
            TaskBlockDragNextDayLabel.Text = DataContext is EdgeShellViewModel vm2
                ? $"00:00–{vm2.FormatClockTime(end2)}"
                : $"00:00–{end2:hh\\:mm}";
            TaskBlockDragNextDayPreview.IsVisible = true;
        }
        else
        {
            var height = Math.Max(22, _dragDurationMinutes / 60d * SchedulerHourHeight - 3);
            top = Math.Clamp(top, 0, TotalTimelineHeight - height);
            Canvas.SetLeft(TaskBlockDragPreview, _dragDay * SchedulerDayWidth + 2);
            Canvas.SetTop(TaskBlockDragPreview, top);
            TaskBlockDragPreview.Height = height;
            var start = TimeSpan.FromMinutes(YToMinutes(top));
            var end = start.Add(TimeSpan.FromMinutes(_dragDurationMinutes));
            TaskBlockDragLabel.Text = DataContext is EdgeShellViewModel vm
                ? $"{vm.FormatClockTime(start)}–{vm.FormatClockTime(end)}"
                : $"{start:hh\\:mm}–{end:hh\\:mm}";
            TaskBlockDragPreview.IsVisible = true;
            TaskBlockDragNextDayPreview.IsVisible = false;
        }
    }

    private void ClearTaskBlockDrag(Button button, IPointer pointer)
    {
        StopAutoScroll();
        _dragBlock = null;
        _dragMoved = false;
        TaskBlockDragPreview.IsVisible = false;
        TaskBlockDragNextDayPreview.IsVisible = false;
        pointer.Capture(null);
    }

    private void UpdateSelectionPreview(int startDay, double startY, int currentDay, double currentY)
    {
        var range = GetSelectionRange(currentDay, currentY);
        ClearSelectionPreview();

        var isMultiDay = range.EndDayIndex > range.StartDayIndex;
        // A day the drag reached by crossing midnight is drawn one cycle further down than the
        // day before it, so the selection carries on under the pointer instead of reappearing
        // at the top of the cycle the drag began in — which, by the time the board has scrolled
        // far enough to cross the line, is a screenful above what the user is looking at. That
        // is what made a drag past 24:00 look like it simply stopped there. Days reached
        // sideways all share the cycle they were dragged across.
        var cycleDelta = CycleAt(currentY) - _selectionStartCycle;
        var lastDayInStartCycle = range.EndDayIndex - Math.Abs(cycleDelta);
        var baseCycle = _selectionStartCycle + Math.Min(0, cycleDelta);
        for (var day = range.StartDayIndex; day <= range.EndDayIndex; day++)
        {
            var startMinute = day == range.StartDayIndex ? range.StartMinuteOfDay : 0;
            var endMinute = day == range.EndDayIndex ? range.EndMinuteOfDay : 1440;
            if (endMinute <= startMinute) continue;

            var isStartDay = day == range.StartDayIndex;
            var isEndDay = day == range.EndDayIndex;
            var labelText = !isMultiDay
                ? FormatSelectionRange(startMinute, endMinute)
                : isStartDay
                    ? $"{FormatMinute(startMinute)}–24:00"
                    : isEndDay
                        ? $"→ {FormatMinute(endMinute)}"
                        : string.Empty;

            var cycle = Math.Clamp(baseCycle + Math.Max(0, day - lastDayInStartCycle), 0, 2);
            // The morning after the last night on the board has no column of its own; it is
            // drawn under Sunday, where the pointer that asked for it already is.
            var column = Math.Min(day, 6);
            var top = cycle * OneCycleHeight + startMinute / 60d * SchedulerHourHeight + 1;
            var naturalHeight = (endMinute - startMinute) / 60d * SchedulerHourHeight - 2;
            var height = Math.Max(string.IsNullOrEmpty(labelText) ? 2 : 22, naturalHeight);
            var label = new TextBlock
            {
                FontSize = 10 * (DataContext is EdgeShellViewModel appearance ? appearance.ThemeTextScale / 100 : 1),
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(5, 4),
                Text = labelText,
                VerticalAlignment = isMultiDay && isEndDay ? Avalonia.Layout.VerticalAlignment.Bottom : Avalonia.Layout.VerticalAlignment.Top
            };
            var preview = new Border
            {
                Width = SchedulerDayWidth - 4,
                Height = height,
                Child = label,
                IsHitTestVisible = false,
                ClipToBounds = true
            };
            preview.Classes.Add("schedulerSelection");
            Canvas.SetLeft(preview, column * SchedulerDayWidth + 2);
            Canvas.SetTop(preview, top);
            SchedulerSelectionPreviewLayer.Children.Add(preview);
        }

        ShowStepChip(currentDay, currentY);
    }

    /// <summary>
    /// The free step is invisible until it is in use, and then it has to be visible: the wheel
    /// is only worth reaching for if the drag says what it did with it. It follows the pointer
    /// rather than sitting in the block, which is seventy-four pixels wide and already full.
    /// </summary>
    private void ShowStepChip(int currentDay, double currentY)
    {
        if (!_fineMode || DataContext is not EdgeShellViewModel viewModel) return;
        var chip = new Border
        {
            Child = new TextBlock
            {
                Text = viewModel.FormatSnapStep(_freeStepMinutes),
                FontSize = 9.5 * viewModel.ThemeTextScale / 100,
                FontWeight = FontWeight.SemiBold
            },
            IsHitTestVisible = false
        };
        chip.Classes.Add("schedulerStepChip");
        Canvas.SetLeft(chip, Math.Clamp(currentDay, 0, 6) * SchedulerDayWidth + 3);
        Canvas.SetTop(chip, Math.Clamp(currentY + 5, 0, TotalTimelineHeight - 18));
        SchedulerSelectionPreviewLayer.Children.Add(chip);
    }

    private CalendarDragRange GetSelectionRange(int currentDay, double currentY)
    {
        var startY = _selectionStart?.Y ?? currentY;
        return CalendarDragRange.Create(
            _selectionDay,
            YToMinutes(startY),
            currentDay,
            YToMinutes(currentY),
            CycleAt(currentY) - _selectionStartCycle);
    }

    /// <summary>
    /// Which repeat of the day the board is showing a point falls in. The timeline draws the
    /// same twenty-four hours three times over so it can be scrolled through midnight without a
    /// seam, and a drag that ends one repeat lower than it began has crossed into the next day.
    /// </summary>
    private static int CycleAt(double y) => (int)Math.Floor(y / OneCycleHeight);

    private string FormatSelectionRange(int startMinute, int endMinute)
        => $"{FormatMinute(startMinute)}–{FormatMinute(endMinute)}";

    private string FormatMinute(int minute)
    {
        if (minute >= 1440) return "24:00";
        var value = TimeSpan.FromMinutes(minute);
        return DataContext is EdgeShellViewModel vm
            ? vm.FormatClockTime(value)
            : value.ToString(@"hh\:mm");
    }

    private void ClearSelectionPreview() => SchedulerSelectionPreviewLayer.Children.Clear();

    /// <summary>Minutes the timeline is rounding to right now.</summary>
    private int StepMinutes => _fineMode ? _freeStepMinutes : CalendarSnapGrid.CoarseStepMinutes;

    private double SnapY(double value)
        => CalendarSnapGrid.Snap(value, SchedulerHourHeight, StepMinutes, TotalTimelineHeight);

    private static int YToMinutes(double y)
    {
        var normalizedY = ((y % OneCycleHeight) + OneCycleHeight) % OneCycleHeight;
        return (int)Math.Round(normalizedY / SchedulerHourHeight * 60);
    }
}
