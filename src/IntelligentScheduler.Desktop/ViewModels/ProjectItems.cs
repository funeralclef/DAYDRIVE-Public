using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace IntelligentScheduler.Desktop.ViewModels;

/// <summary>
/// What a project's rows can ask of the shell. One set, handed to every row, so a row knows what
/// it can do without knowing anything about the shell that does it. Two kinds of project share it:
/// an unscheduled task broken into steps planned over days, and a calendar event broken into the
/// steps its hour is spent on.
/// </summary>
public sealed class ProjectActions
{
    public required Func<TaskProjectViewModel, Task> AddStep { get; init; }
    public required Func<TaskProjectViewModel, Task> PickDraftDuration { get; init; }
    public required Func<TaskProjectViewModel, Task> PickDraftDay { get; init; }
    public required Func<ProjectStepRowViewModel, Task> ToggleStep { get; init; }
    public required Func<ProjectStepRowViewModel, Task> PickStepDuration { get; init; }
    public required Func<ProjectStepRowViewModel, Task> PickStepDay { get; init; }
    public required Func<ProjectStepRowViewModel, Task> StepTime { get; init; }
    public required Func<ProjectStepRowViewModel, Task> DeleteStep { get; init; }
    public required Func<ProjectStepRowViewModel, string, Task> RenameStep { get; init; }
    public required Action<ProjectStepRowViewModel> RevealProject { get; init; }

    public required Func<SittingViewModel, Task> AddSittingStep { get; init; }
    public required Func<SittingViewModel, Task> PickSittingDraftDuration { get; init; }
    public required Action<SittingViewModel> CloseSitting { get; init; }
    public required Func<SittingViewModel, Task> CarryToTime { get; init; }
    public required Func<SittingViewModel, Task> CarryToUnscheduled { get; init; }
    public required Func<SittingStepViewModel, Task> ToggleSittingStep { get; init; }
    public required Func<SittingStepViewModel, Task> PickSittingStepDuration { get; init; }
    public required Func<SittingStepViewModel, Task> DeleteSittingStep { get; init; }
    public required Func<SittingStepViewModel, string, Task> RenameSittingStep { get; init; }
}

/// <summary>
/// A step whose name is edited where it is read. Both kinds of step row are renamed the same way,
/// and the window catches the keys and the lost caret for both, so it asks this rather than which
/// of the two it is holding.
/// </summary>
public interface IRenamableStep
{
    bool IsRenaming { get; }
    void BeginRename();
    Task CommitRenameAsync();
    void CancelRename();
}

/// <summary>
/// The rename box both kinds of step row carry: pressing the name turns it into its own box, Enter
/// and losing the caret both keep what was typed, Escape keeps the old name.
/// </summary>
public abstract partial class RenamableStepViewModel : ObservableObject, IRenamableStep
{
    public abstract string CurrentTitle { get; }

    [ObservableProperty] private bool _isRenaming;
    [ObservableProperty] private string _renameText = string.Empty;

    public void BeginRename()
    {
        RenameText = CurrentTitle;
        IsRenaming = true;
    }

    /// <summary>
    /// Writes the new name. Enter and losing the caret both end the edit, and Enter takes the
    /// caret away as it hides the box — so the second of the two finds nothing left to do.
    /// </summary>
    public Task CommitRenameAsync()
    {
        if (!IsRenaming) return Task.CompletedTask;
        IsRenaming = false;
        var text = RenameText.Trim();
        return text.Length == 0 || text == CurrentTitle ? Task.CompletedTask : Rename(text);
    }

    public void CancelRename() => IsRenaming = false;

    protected abstract Task Rename(string title);
}

/// <summary>
/// One step of an unscheduled project, as a row — inside its task's card, or in the day it is
/// planned for. Built fresh on every refresh and compared by what it shows, so a refresh that
/// changed nothing about a step leaves its row standing; see <see cref="IRowEquivalence"/>.
/// </summary>
public sealed partial class ProjectStepRowViewModel(ProjectActions actions) : RenamableStepViewModel, IRowEquivalence
{
    public required Guid Id { get; init; }
    public required Guid TaskId { get; init; }
    public required string Title { get; init; }
    public required bool IsDone { get; init; }
    public required string DurationText { get; init; }

    /// <summary>"set a time" while the step has no hour; the hours it is booked for once it has.</summary>
    public required string ScheduleText { get; init; }
    public required string ScheduleTooltip { get; init; }
    public required bool IsScheduled { get; init; }
    public required string ProjectTitle { get; init; }

    /// <summary>Rows in the day name their project. Rows inside the project's own card do not need to.</summary>
    public required bool ShowProject { get; init; }
    public required IBrush Accent { get; init; }
    public required IBrush AccentSoft { get; init; }

    /// <summary>What the booked event's verdict said, when it said something other than "done".</summary>
    public string OutcomeText { get; init; } = string.Empty;
    public bool IsPartial { get; init; }
    public bool IsSkipped { get; init; }

    /// <summary>On today's list, the day a step that slipped was planned for.</summary>
    public string CarriedFromText { get; init; } = string.Empty;

    /// <summary>Everything the row shows, in one string; two rows that agree on it look the same.</summary>
    public required string Signature { get; init; }

    public bool HasOutcome => OutcomeText.Length > 0;
    public bool HasCarriedFrom => CarriedFromText.Length > 0;

    /// <summary>A booked step's day and length are its event's, so they are changed on the event.</summary>
    public bool CanEditPlan => !IsScheduled;

    public override string CurrentTitle => Title;

    [RelayCommand] private Task ToggleAsync() => actions.ToggleStep(this);
    [RelayCommand] private Task PickDurationAsync() => actions.PickStepDuration(this);
    [RelayCommand] private Task PickDayAsync() => actions.PickStepDay(this);
    [RelayCommand] private Task TimeAsync() => actions.StepTime(this);
    [RelayCommand] private Task DeleteAsync() => actions.DeleteStep(this);

    /// <summary>The project chip on a row in the day: opens the task the step belongs to.</summary>
    [RelayCommand] private void RevealProject() => actions.RevealProject(this);

    protected override Task Rename(string title) => actions.RenameStep(this, title);

    public bool Matches(object other) => other is ProjectStepRowViewModel row && row.Signature == Signature;
}

/// <summary>One day of an unscheduled project: its heading, its total, and the steps on it.</summary>
public sealed partial class ProjectDayGroupViewModel(int key) : ObservableObject
{
    /// <summary>The day as a number, the undated group last. How a group is found again on the next refresh.</summary>
    public int Key { get; } = key;

    [ObservableProperty] private string _heading = string.Empty;
    [ObservableProperty] private string _totalText = string.Empty;
    [ObservableProperty] private bool _isToday;
    [ObservableProperty] private bool _isOverdue;
    [ObservableProperty] private bool _isAfterDeadline;
    [ObservableProperty] private bool _isShown = true;

    /// <summary>A day that is over with everything on it done. Folded away unless asked for.</summary>
    public bool IsFinished { get; set; }

    public ObservableCollection<ProjectStepRowViewModel> Steps { get; } = [];
}

/// <summary>
/// The project a task has been broken into: how far along it is and what comes next, and —
/// opened — its days, its steps and the box the next step is written in.
///
/// It lives as long as the task does and is updated where it stands. It holds the step being
/// typed, and a panel rebuilt after every Enter would take the caret with it. It exists before its
/// project does, too: "break into steps" opens the box on a task that has none yet, and the project
/// is only written once the first step is.
/// </summary>
public sealed partial class TaskProjectViewModel(Guid taskId, ProjectActions actions) : ObservableObject
{
    public Guid TaskId { get; } = taskId;

    /// <summary>The project, once there is one: null while the box is open on a task with no steps yet.</summary>
    public Guid? ProjectId { get; set; }

    [ObservableProperty] private IBrush _accent = Brushes.Transparent;
    [ObservableProperty] private IBrush _accentSoft = Brushes.Transparent;
    [ObservableProperty] private double _progress;

    /// <summary>"3/9": the count that says what the bar is a bar of.</summary>
    [ObservableProperty] private string _stepsText = string.Empty;

    /// <summary>"3 of 9 done · 4 h of 13 h", said when the pointer rests on the chip.</summary>
    [ObservableProperty] private string _progressText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNext))]
    private string _nextText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _hasSteps;

    [ObservableProperty] private bool _isComplete;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private string _draftTitle = string.Empty;
    [ObservableProperty] private string _draftDurationText = string.Empty;
    [ObservableProperty] private string _draftDayText = string.Empty;

    /// <summary>No steps yet: the task still reads as an ordinary task, and can still be broken up.</summary>
    public bool IsEmpty => !HasSteps;
    [ObservableProperty] private bool _hasFinishedDays;
    [ObservableProperty] private bool _showFinished;
    [ObservableProperty] private string _finishedToggleText = string.Empty;

    public bool HasNext => NextText.Length > 0;

    public ObservableCollection<ProjectDayGroupViewModel> Groups { get; } = [];

    /// <summary>The length the next step is written with. It carries over from one step to the next.</summary>
    internal int DraftMinutes { get; set; } = 60;

    internal DateOnly? DraftDay { get; set; }

    /// <summary>Once the user has picked the day, it stays theirs; until then it follows the plan.</summary>
    internal bool DraftDayChosen { get; set; }

    internal string ShowFinishedLabel { get; set; } = string.Empty;
    internal string HideFinishedLabel { get; set; } = string.Empty;

    partial void OnShowFinishedChanged(bool value) => ApplyFinishedVisibility();

    internal void ApplyFinishedVisibility()
    {
        foreach (var group in Groups) group.IsShown = !group.IsFinished || ShowFinished;
        FinishedToggleText = ShowFinished ? HideFinishedLabel : ShowFinishedLabel;
    }

    [RelayCommand] private void ToggleExpanded() => IsExpanded = !IsExpanded;
    [RelayCommand] private void ToggleFinished() => ShowFinished = !ShowFinished;
    [RelayCommand] private Task AddStepAsync() => actions.AddStep(this);
    [RelayCommand] private Task PickDraftDurationAsync() => actions.PickDraftDuration(this);
    [RelayCommand] private Task PickDraftDayAsync() => actions.PickDraftDay(this);
}

/// <summary>
/// One line of an event's checklist: a tick, the name, and how long it takes. Built fresh on every
/// refresh and left standing when nothing about it changed, like every other row of the day.
/// </summary>
public sealed partial class SittingStepViewModel(ProjectActions actions) : RenamableStepViewModel, IRowEquivalence
{
    public required Guid Id { get; init; }
    public required Guid EventId { get; init; }
    public required string Title { get; init; }
    public required bool IsDone { get; init; }
    public required string DurationText { get; init; }
    public required IBrush Accent { get; init; }
    public required string Signature { get; init; }

    public override string CurrentTitle => Title;

    [RelayCommand] private Task ToggleAsync() => actions.ToggleSittingStep(this);
    [RelayCommand] private Task PickDurationAsync() => actions.PickSittingStepDuration(this);
    [RelayCommand] private Task DeleteAsync() => actions.DeleteSittingStep(this);

    protected override Task Rename(string title) => actions.RenameSittingStep(this, title);

    public bool Matches(object other) => other is SittingStepViewModel row && row.Signature == Signature;
}

/// <summary>
/// The checklist of one occurrence of an event: the steps the hour is spent on, ticked for this
/// sitting, and the box the next one is written in.
///
/// Kept by the shell for as long as the event is on screen and handed to each row the day list
/// builds for it — the list is rebuilt every minute, and a checklist rebuilt with it would fold
/// itself shut and drop what was being typed.
/// </summary>
public sealed partial class SittingViewModel(Guid eventId, ProjectActions actions) : ObservableObject
{
    public Guid EventId { get; } = eventId;

    /// <summary>The project, once there is one: null while the box is open on an event with no steps yet.</summary>
    public Guid? ProjectId { get; set; }

    /// <summary>The list belongs to a whole repeating series; this sitting only has its own ticks.</summary>
    public bool IsSeries { get; set; }

    [ObservableProperty] private IBrush _accent = Brushes.Transparent;
    [ObservableProperty] private IBrush _accentSoft = Brushes.Transparent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCardVisible))]
    private bool _isOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCardVisible))]
    private bool _hasSteps;

    [ObservableProperty] private bool _isComplete;
    [ObservableProperty] private double _progress;

    /// <summary>"2/4" on the row's own chip.</summary>
    [ObservableProperty] private string _stepsText = string.Empty;

    /// <summary>"1 h 5 min of 2 h" — how the ticked part and the whole sit in the event's time.</summary>
    [ObservableProperty] private string _summaryText = string.Empty;

    /// <summary>The steps ask for more time than the event has.</summary>
    [ObservableProperty] private bool _isOverflowing;

    [ObservableProperty] private string _draftTitle = string.Empty;
    [ObservableProperty] private string _draftDurationText = string.Empty;

    /// <summary>What is left can be carried on elsewhere: the sitting is over, or was answered short of done.</summary>
    [ObservableProperty] private bool _canCarry;
    [ObservableProperty] private string _carryText = string.Empty;

    /// <summary>The repeating list says so, so ticking it here is not mistaken for ticking it everywhere.</summary>
    [ObservableProperty] private string _seriesNote = string.Empty;

    public bool HasSeriesNote => SeriesNote.Length > 0;
    partial void OnSeriesNoteChanged(string value) => OnPropertyChanged(nameof(HasSeriesNote));

    /// <summary>The card is drawn while it is open — with steps, or empty and waiting for the first.</summary>
    public bool IsCardVisible => IsOpen;

    public ObservableCollection<SittingStepViewModel> Steps { get; } = [];

    internal int DraftMinutes { get; set; } = 30;

    /// <summary>Whether the user has opened or closed this card by hand; until then it follows the event.</summary>
    internal bool OpenChosen { get; set; }

    [RelayCommand]
    private void ToggleOpen()
    {
        OpenChosen = true;
        IsOpen = !IsOpen;
        if (!IsOpen && !HasSteps) actions.CloseSitting(this);
    }

    [RelayCommand] private Task AddStepAsync() => actions.AddSittingStep(this);
    [RelayCommand] private Task PickDraftDurationAsync() => actions.PickSittingDraftDuration(this);
    [RelayCommand] private Task CarryToTimeAsync() => actions.CarryToTime(this);
    [RelayCommand] private Task CarryToUnscheduledAsync() => actions.CarryToUnscheduled(this);
}

/// <summary>
/// A step written into the event or task form before the thing it belongs to exists. On save they
/// become the new item's steps. A step carried over from a sitting that ran out of time is one of
/// these as well, pointing at the step it is — taking it off this list leaves that step where it was.
/// </summary>
public sealed partial class DraftStepViewModel(Action<DraftStepViewModel> remove) : ObservableObject
{
    public required string Title { get; init; }

    [ObservableProperty] private int _minutes;
    [ObservableProperty] private string _durationText = string.Empty;

    /// <summary>The existing step this line carries over, when it is one.</summary>
    public Guid? SourceStepId { get; init; }

    [RelayCommand] private void Remove() => remove(this);
}
