using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IntelligentScheduler.Application;
using IntelligentScheduler.Desktop.Localization;
using IntelligentScheduler.Domain;

namespace IntelligentScheduler.Desktop.ViewModels;

/// <summary>
/// Picking events out of a day and doing one thing to all of them.
///
/// Until now a single event could only be edited through the full form, one field at a time,
/// and the only way to move anything was to shift the entire day. This is the missing middle:
/// tick what should move, say by how much or onto which day, and see — before anything is
/// written — what the move would land on. Deleting a handful at once goes through the same
/// selection, because "these five" is the same thought whichever verb follows it.
/// </summary>
public sealed partial class EdgeShellViewModel
{
    /// <summary>Events queued for a move that is waiting on the user's answer.</summary>
    private readonly List<CalendarEvent> _moveSubjects = [];
    private IReadOnlyList<EventMove> _pendingMoves = [];
    /// <summary>Set while the day list is being rebuilt so the ticks can be put back.</summary>
    private readonly HashSet<Guid> _selectedEventIds = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelectionIdle))]
    private bool _isSelectionMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedEvents))]
    [NotifyPropertyChangedFor(nameof(CanSelectAllEvents))]
    private int _selectedEventCount;

    [ObservableProperty] private string _selectionSummaryText = string.Empty;

    /// <summary>The mover opened from a single event or a selection in the agenda.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelectionIdle))]
    private bool _isMovePanelOpen;

    [ObservableProperty] private string _moveSubjectText = string.Empty;

    /// <summary>Which of the two ways to say where: by an amount of time, or onto a date.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMoveByDate))]
    private bool _isMoveByOffset = true;

    [ObservableProperty] private bool _isMoveEarlier;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MoveAmountLabel))]
    private decimal _moveShiftHours;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MoveAmountLabel))]
    private decimal _moveShiftMinutes = 30;

    [ObservableProperty] private DateOnly _moveTargetDate = DateOnly.FromDateTime(DateTime.Today);
    [ObservableProperty] private string _moveTargetDateLabel = string.Empty;

    /// <summary>What the move would do, in words, before it is written.</summary>
    [ObservableProperty] private string _movePreviewText = string.Empty;
    [ObservableProperty] private string _moveValidationMessage = string.Empty;

    [ObservableProperty] private bool _isMoveConfirmationVisible;
    [ObservableProperty] private string _moveConfirmationText = string.Empty;

    [ObservableProperty] private bool _isSelectionDeleteConfirmationVisible;
    [ObservableProperty] private string _selectionDeleteConfirmationText = string.Empty;

    /// <summary>Everything the pending move would end up sharing the clock with.</summary>
    public ObservableCollection<ScheduleCollisionViewModel> MoveCollisions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMoveCollisions))]
    private string _moveCollisionSummary = string.Empty;

    public bool HasMoveCollisions => MoveCollisions.Count > 0;
    public bool HasSelectedEvents => SelectedEventCount > 0;
    public bool IsMoveByDate => !IsMoveByOffset;
    public bool HasMoveValidationError => MoveValidationMessage.Length > 0;
    /// <summary>The selection bar's own state: ticking rows, with no sheet over the top of it.</summary>
    public bool IsSelectionIdle => IsSelectionMode && !IsMovePanelOpen;
    public bool CanSelectAllEvents => SelectedEventCount < Agenda.Count(row => row.IsEvent);
    public string MoveAmountLabel => $"{(int)MoveShiftHours:00}:{(int)MoveShiftMinutes:00}";

    partial void OnMoveValidationMessageChanged(string value) => OnPropertyChanged(nameof(HasMoveValidationError));
    partial void OnIsMovePanelOpenChanged(bool value) => OnPropertyChanged(nameof(HasBlockingSurface));

    // ---------------------------------------------------------------- entering and leaving

    [RelayCommand]
    private void ToggleSelectionMode()
    {
        if (IsSelectionMode) { ExitSelectionMode(); return; }
        IsScheduleExpanded = true;
        IsSelectionMode = true;
        foreach (var row in Agenda) row.IsSelectionMode = true;
        RefreshSelectionSummary();
    }

    /// <summary>Leaves selection mode and forgets everything it was holding.</summary>
    public void ExitSelectionMode()
    {
        IsSelectionMode = false;
        CloseMovePanel();
        IsSelectionDeleteConfirmationVisible = false;
        _selectedEventIds.Clear();
        foreach (var row in Agenda)
        {
            row.IsSelectionMode = false;
            row.RestoreSelection(false);
        }
        SelectedEventCount = 0;
        SelectionSummaryText = string.Empty;
    }

    [RelayCommand]
    private void SelectAllEvents()
    {
        foreach (var row in Agenda.Where(item => item.IsEvent)) row.RestoreSelection(true);
        RefreshSelectionSummary();
    }

    [RelayCommand]
    private void ClearEventSelection()
    {
        foreach (var row in Agenda) row.RestoreSelection(false);
        RefreshSelectionSummary();
    }

    /// <summary>
    /// Wires a freshly built row into the selection, restoring its tick. The day list is
    /// rebuilt on a timer, and a selection that vanished every minute would be unusable.
    /// </summary>
    private void AttachRowSelection(AgendaItemViewModel row)
    {
        row.AttachSelection(RefreshSelectionSummary, MoveSingleEventAsync, BeginSelectionFor);
        row.IsSelectionMode = IsSelectionMode;
        if (row.Event is { } source && _selectedEventIds.Contains(source.Id)) row.RestoreSelection(true);
    }

    /// <summary>
    /// Enters selection mode from one event's own menu, with that event already ticked. This is
    /// where ticking begins now: the heading used to carry a button that turned the ticks on and
    /// left the user to find the first event afterwards, which is the same two steps in the less
    /// useful order — nobody decides to select before they have decided what.
    /// </summary>
    private void BeginSelectionFor(AgendaItemViewModel row)
    {
        if (!row.IsEvent) return;
        if (!IsSelectionMode)
        {
            IsScheduleExpanded = true;
            IsSelectionMode = true;
            foreach (var item in Agenda) item.IsSelectionMode = true;
        }

        row.IsSelected = true;
        RefreshSelectionSummary();
    }

    /// <summary>Recounts the ticked rows and updates everything that reads from the count.</summary>
    private void RefreshSelectionSummary()
    {
        var chosen = SelectedEvents();
        _selectedEventIds.Clear();
        foreach (var item in chosen) _selectedEventIds.Add(item.Id);
        SelectedEventCount = chosen.Count;
        SelectionSummaryText = chosen.Count == 0
            ? localization.Text("SelectionNone")
            : localization.Format("SelectionCount", chosen.Count,
                ScheduleText.Duration(TimeSpan.FromMinutes(chosen.Sum(item =>
                    Math.Max(0, (item.EndUtc - item.StartUtc).TotalMinutes))), localization));
        OnPropertyChanged(nameof(CanSelectAllEvents));
    }

    private List<CalendarEvent> SelectedEvents()
        => [.. Agenda.Where(row => row.IsSelected).Select(row => row.Event).OfType<CalendarEvent>()
            .OrderBy(item => item.StartUtc)];

    // ---------------------------------------------------------------- the mover

    /// <summary>"Move…" on one row: the same sheet, with that event as its only subject.</summary>
    private Task MoveSingleEventAsync(AgendaItemViewModel row)
    {
        if (row.Event is not { } source) return Task.CompletedTask;
        OpenMoveFor([source]);
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void OpenMoveForSelection()
    {
        var chosen = SelectedEvents();
        if (chosen.Count == 0)
        {
            Status = localization.Text("SelectionEmptyHint");
            return;
        }
        OpenMoveFor(chosen);
    }

    /// <summary>Puts the mover in front of the user with a fixed set of subjects.</summary>
    private void OpenMoveFor(IReadOnlyList<CalendarEvent> subjects)
    {
        _moveSubjects.Clear();
        _moveSubjects.AddRange(subjects.OrderBy(item => item.StartUtc));
        _pendingMoves = [];
        MoveCollisions.Clear();
        MoveCollisionSummary = string.Empty;
        OnPropertyChanged(nameof(HasMoveCollisions));
        MoveValidationMessage = string.Empty;
        IsMoveConfirmationVisible = false;
        IsSelectionDeleteConfirmationVisible = false;
        IsMoveByOffset = true;
        MoveTargetDate = DateOnly.FromDateTime(_moveSubjects[0].StartUtc.ToLocalTime().Date).AddDays(1);
        MoveTargetDateLabel = MoveTargetDate.ToString("ddd, d MMM", localization.CurrentCulture);
        MoveSubjectText = _moveSubjects.Count == 1
            ? _moveSubjects[0].Title
            : FormatEventsCount(_moveSubjects.Count);
        IsMovePanelOpen = true;
        RefreshMovePreview();
    }

    [RelayCommand]
    private void CloseMovePanel()
    {
        IsMovePanelOpen = false;
        IsMoveConfirmationVisible = false;
        _moveSubjects.Clear();
        _pendingMoves = [];
        MoveCollisions.Clear();
        MoveCollisionSummary = string.Empty;
        OnPropertyChanged(nameof(HasMoveCollisions));
        MoveValidationMessage = string.Empty;
    }

    [RelayCommand] private void SetMoveEarlier() { IsMoveEarlier = true; RefreshMovePreview(); }
    [RelayCommand] private void SetMoveLater() { IsMoveEarlier = false; RefreshMovePreview(); }
    [RelayCommand] private void SetMoveByOffset() { IsMoveByOffset = true; RefreshMovePreview(); }
    [RelayCommand] private void SetMoveByDate() { IsMoveByOffset = false; RefreshMovePreview(); }

    [RelayCommand]
    private void OpenMoveAmountPicker()
        => OpenTimePicker(TimeSelectionPurpose.MoveAmount,
            TimeSpan.FromMinutes((double)(MoveShiftHours * 60 + MoveShiftMinutes)));

    [RelayCommand]
    private Task OpenMoveDateCalendarAsync()
        => OpenDateSelectionAsync(CalendarSelectionPurpose.MoveTarget, MoveTargetDate);

    /// <summary>
    /// Adjusts the currently entered amount without changing its direction. The old behaviour
    /// replaced the amount and silently flipped earlier/later, so +15 after 30 minutes still
    /// showed 15 instead of the expected 45.
    /// </summary>
    [RelayCommand]
    private void NudgeMove(string? minutes)
    {
        if (!int.TryParse(minutes, out var value) || value == 0) return;
        IsMoveByOffset = true;
        const int maximumMinutes = 7 * 24 * 60;
        var current = (int)Math.Round(MoveShiftHours * 60 + MoveShiftMinutes);
        var adjusted = Math.Clamp(current + value, 0, maximumMinutes);
        MoveShiftHours = adjusted / 60;
        MoveShiftMinutes = adjusted % 60;
        RefreshMovePreview();
    }

    /// <summary>The offset the mover currently describes, positive for later.</summary>
    private TimeSpan MoveOffset()
    {
        var total = (int)(Math.Clamp(MoveShiftHours, 0, 168) * 60 + Math.Clamp(MoveShiftMinutes, 0, 59));
        return TimeSpan.FromMinutes(IsMoveEarlier ? -total : total);
    }

    /// <summary>Builds the plan and describes it, without touching the database.</summary>
    private void RefreshMovePreview()
    {
        OnPropertyChanged(nameof(MoveAmountLabel));
        if (_moveSubjects.Count == 0)
        {
            MovePreviewText = string.Empty;
            _pendingMoves = [];
            return;
        }

        _pendingMoves = IsMoveByOffset
            ? ScheduleShift.By(_moveSubjects, MoveOffset())
            : ScheduleShift.ToDay(_moveSubjects, MoveTargetDate);

        var first = _pendingMoves[0];
        var lastEnd = _pendingMoves.Max(move => move.ToEndUtc);
        MovePreviewText = _pendingMoves.All(move => move.IsNoop)
            ? localization.Text("MoveNothingToDo")
            : localization.Format("MovePreview",
                ClockText.Format(first.ToStartUtc, Use24HourClock, localization.CurrentCulture),
                ClockText.Format(lastEnd, Use24HourClock, localization.CurrentCulture),
                first.ToStartUtc.ToLocalTime().ToString("ddd, d MMM", localization.CurrentCulture));
        MoveValidationMessage = string.Empty;
    }

    /// <summary>
    /// Works out what the move would run into and asks. This is the notification the user was
    /// missing: the answer arrives before the schedule changes, not after.
    /// </summary>
    [RelayCommand]
    private async Task PreviewMoveAsync()
    {
        RefreshMovePreview();
        if (_pendingMoves.Count == 0) return;
        if (_pendingMoves.All(move => move.IsNoop))
        {
            MoveValidationMessage = localization.Text("MoveNothingToDo");
            return;
        }

        try
        {
            var window = MoveWindow(_pendingMoves);
            var neighbours = await calendarRepository.GetRangeAsync(window.Start, window.End);
            var collisions = ScheduleShift.Collisions(_pendingMoves, neighbours);

            MoveCollisions.Clear();
            foreach (var collision in collisions.Take(8))
                MoveCollisions.Add(new ScheduleCollisionViewModel(collision, localization, Use24HourClock));
            OnPropertyChanged(nameof(HasMoveCollisions));
            MoveCollisionSummary = collisions.Count == 0
                ? string.Empty
                : localization.Format("MoveCollisionSummary", collisions.Count);

            MoveConfirmationText = collisions.Count == 0
                ? localization.Format("MoveConfirmClear", FormatEventsCount(_pendingMoves.Count), MovePreviewText)
                : localization.Format("MoveConfirmWithCollisions", FormatEventsCount(_pendingMoves.Count), collisions.Count);
            IsMoveConfirmationVisible = true;
        }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Checking a move for conflicts");
            MoveValidationMessage = exception.Message;
        }
    }

    [RelayCommand] private void CancelMoveConfirmation() => IsMoveConfirmationVisible = false;

    [RelayCommand] private Task ConfirmMoveAsync() => RunBusyAsync(ApplyMoveCoreAsync);

    private async Task ApplyMoveCoreAsync()
    {
        var moves = _pendingMoves.Where(move => !move.IsNoop).ToList();
        if (moves.Count == 0)
        {
            IsMoveConfirmationVisible = false;
            return;
        }

        try
        {
            var written = await calendarRepository.MoveEventsAsync(moves);
            var collisions = MoveCollisions.Count;
            IsMoveConfirmationVisible = false;
            CloseMovePanel();
            ExitSelectionMode();
            Status = collisions == 0
                ? localization.Format("MoveCompleted", written)
                : localization.Format("MoveCompletedWithCollisions", written, collisions);
            await ReloadAsync(preserveStatus: true);
            await RefreshCalendarAsync();
            await RefreshSchedulerAsync();
        }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Moving events");
            MoveValidationMessage = exception.Message;
            IsMoveConfirmationVisible = false;
        }
    }

    /// <summary>The span worth reading neighbours from: everything the plan touches, plus a day.</summary>
    private static (DateTimeOffset Start, DateTimeOffset End) MoveWindow(IReadOnlyList<EventMove> moves)
        => (moves.Min(move => move.ToStartUtc).AddDays(-1), moves.Max(move => move.ToEndUtc).AddDays(1));

    // ---------------------------------------------------------------- removing a selection

    [RelayCommand]
    private void RequestDeleteSelection()
    {
        var chosen = SelectedEvents();
        if (chosen.Count == 0)
        {
            Status = localization.Text("SelectionEmptyHint");
            return;
        }
        SelectionDeleteConfirmationText = localization.Format("SelectionDeleteConfirm", FormatEventsCount(chosen.Count));
        IsSelectionDeleteConfirmationVisible = true;
    }

    [RelayCommand] private void CancelDeleteSelection() => IsSelectionDeleteConfirmationVisible = false;

    [RelayCommand] private Task ConfirmDeleteSelectionAsync() => RunBusyAsync(DeleteSelectionCoreAsync);

    private async Task DeleteSelectionCoreAsync()
    {
        var chosen = SelectedEvents();
        IsSelectionDeleteConfirmationVisible = false;
        if (chosen.Count == 0) return;
        try
        {
            var removed = await calendarRepository.DeleteManyAsync([.. chosen.Select(item => item.Id)]);
            ExitSelectionMode();
            Status = localization.Format("SelectionDeleted", removed);
            await ReloadAsync(preserveStatus: true);
            await RefreshCalendarAsync();
            await RefreshSchedulerAsync();
        }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Deleting selected events");
            Status = exception.Message;
        }
    }

}

/// <summary>One line of "this would land on that", in the user's own clock format.</summary>
public sealed class ScheduleCollisionViewModel
{
    public ScheduleCollisionViewModel(ScheduleCollision collision, LocalizationService localization, bool use24HourClock)
    {
        MovedTitle = collision.MovedTitle;
        BlockingTitle = collision.BlockingTitle;
        IsWithinSelection = collision.BlockingIsMovingToo;
        Window = ClockText.Range(collision.StartUtc, collision.EndUtc, use24HourClock, localization.CurrentCulture);
        Overlap = ScheduleText.Duration(TimeSpan.FromMinutes(collision.Minutes), localization);
        Text = localization.Format(
            collision.BlockingIsMovingToo ? "CollisionWithinSelection" : "CollisionWithExisting",
            collision.MovedTitle, collision.BlockingTitle);
    }

    public string MovedTitle { get; }
    public string BlockingTitle { get; }
    public string Window { get; }
    public string Overlap { get; }
    public string Text { get; }
    public bool IsWithinSelection { get; }
}
