using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IntelligentScheduler.Application;
using IntelligentScheduler.Domain;
using DomainTaskStatus = IntelligentScheduler.Domain.TaskStatus;

namespace IntelligentScheduler.Desktop.ViewModels;

/// <summary>
/// Projects: a task or an event broken into steps.
///
/// There is no list of projects any more. A project was always the steps of something the day
/// already knew how to hold, so it is now held there. An unscheduled task broken into steps is a
/// piece of work spread over days: each step carries a length and a day, the day is a plan rather
/// than a booking, and "set a time" books an hour for it as an ordinary event, which from then on
/// is the step's slot. A calendar event broken into steps is a sitting: the steps are what the hour
/// is spent on, they are ticked in the event's own row, and those ticks and the event's verdict are
/// one answer — the last tick says "done", a tick taken back says "partly", and "done" said anywhere
/// ticks what was left. What a sitting leaves undone can be carried on: to another hour, or back to
/// the unscheduled list as a project of its own.
/// </summary>
public sealed partial class EdgeShellViewModel
{
    private List<Project> _projectList = [];
    private List<ProjectStep> _projectSteps = [];

    /// <summary>Every task, by id: the hosts of the unscheduled projects and whatever might become one.</summary>
    private Dictionary<Guid, TaskItem> _projectTasks = new();

    /// <summary>The events booked for unscheduled steps, and their verdicts.</summary>
    private Dictionary<Guid, CalendarEvent> _stepEvents = new();
    private Dictionary<Guid, EventOutcome> _stepOutcomes = new();

    /// <summary>The ticks of the sittings on screen: occurrence → step → when it was ticked.</summary>
    private readonly Dictionary<Guid, Dictionary<Guid, DateTimeOffset>> _checks = new();

    /// <summary>
    /// The project panels of tasks, kept by task for as long as the task is a project — or is being
    /// made one. A panel is updated where it stands rather than rebuilt: it holds the step being
    /// typed, and a rebuilt panel would take the caret with it after every Enter.
    /// </summary>
    private readonly Dictionary<Guid, TaskProjectViewModel> _taskProjects = new();

    /// <summary>The checklists of the events on screen, kept by occurrence for the same reason.</summary>
    private readonly Dictionary<Guid, SittingViewModel> _sittings = new();

    /// <summary>The unscheduled list's rows, by task, updated where they stand; see ReloadTasksAsync.</summary>
    private readonly Dictionary<Guid, CompactTaskViewModel> _taskRows = new();

    private ProjectActions? _projectActions;

    /// <summary>The unscheduled step the event composer is booking a time for, while it is.</summary>
    private Guid? _schedulingStepId;

    /// <summary>Whose length or day the shared pickers are answering for.</summary>
    private Guid? _pickerStepId;
    private TaskProjectViewModel? _pickerProject;
    private SittingViewModel? _pickerSitting;
    private DraftStepViewModel? _pickerDraft;
    private bool _pickerDraftIsEvent;

    /// <summary>The day the date picker opens on for a project's question.</summary>
    private DateOnly _projectPickerDate = DateOnly.FromDateTime(DateTime.Today);

    /// <summary>A step box that takes the caret once it is drawn.</summary>
    private Guid? _focusProjectComposerOf;
    private Guid? _focusSittingComposerOf;

    /// <summary>
    /// The sitting whose unfinished steps the event composer is carrying on, while it is. Its
    /// steps move with it — or, for a list kept by a whole series, are copied, because the series
    /// still needs them next time.
    /// </summary>
    private CarrySource? _carrySource;

    private sealed record CarrySource(Guid EventId, Guid ProjectId, bool IsSeries);

    private int _eventDraftMinutes = 30;
    private int _taskDraftMinutes = 60;
    private EventCategory _taskCategoryDraft = EventCategory.Work;

    /// <summary>The task being edited is a project, so its length is its steps' and its colour is asked instead.</summary>
    private bool _editedTaskIsProject;

    [ObservableProperty] private bool _hasDaySteps;
    [ObservableProperty] private string _dayStepsSummary = string.Empty;
    [ObservableProperty] private string _dayStepsTitle = string.Empty;

    // ---- The forms' own steps -------------------------------------------------------------
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEventDraftToggle))]
    [NotifyPropertyChangedFor(nameof(ShowEventDraftEditor))]
    private bool _isEventDraftStepsOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEventDraftToggle))]
    [NotifyPropertyChangedFor(nameof(ShowEventDraftEditor))]
    private bool _canDraftEventSteps = true;

    [ObservableProperty] private string _eventDraftStepText = string.Empty;
    [ObservableProperty] private string _eventDraftDurationText = string.Empty;
    [ObservableProperty] private string _eventDraftSummary = string.Empty;
    [ObservableProperty] private bool _isEventDraftOverflowing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTaskDraftToggle))]
    [NotifyPropertyChangedFor(nameof(ShowTaskDraftEditor))]
    private bool _isTaskDraftStepsOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTaskDraftToggle))]
    [NotifyPropertyChangedFor(nameof(ShowTaskDraftEditor))]
    private bool _canDraftTaskSteps = true;

    [ObservableProperty] private string _taskDraftStepText = string.Empty;
    [ObservableProperty] private string _taskDraftDurationText = string.Empty;
    [ObservableProperty] private string _taskDraftSummary = string.Empty;

    /// <summary>The task form is for a project: the length field gives way to the colour its hours take.</summary>
    [ObservableProperty] private bool _isTaskEditorProject;
    [ObservableProperty] private ChoiceOption? _selectedTaskCategoryOption;

    /// <summary>"How did it go?" is being asked of an event with steps: its ticks are the answer's figure.</summary>
    [ObservableProperty] private bool _isSessionReviewSitting;

    /// <summary>
    /// Asks the window whether a step box in the day list has the caret. The day is rebuilt every
    /// minute, and a rebuild underneath the box takes the caret and the half-typed line with it,
    /// so the minute waits. Asked rather than remembered: a flag set on focus is left standing by
    /// a box that goes away without ever losing it, and the day would then stop being rebuilt.
    /// </summary>
    public Func<bool>? TypingProbe { get; set; }

    private bool IsTypingInDay => TypingProbe?.Invoke() == true;

    public ObservableCollection<DraftStepViewModel> EventDraftSteps { get; } = [];
    public ObservableCollection<DraftStepViewModel> TaskDraftSteps { get; } = [];
    public bool HasEventDraftSteps => EventDraftSteps.Count > 0;
    public bool HasTaskDraftSteps => TaskDraftSteps.Count > 0;
    public bool ShowEventDraftToggle => CanDraftEventSteps && !IsEventDraftStepsOpen;
    public bool ShowEventDraftEditor => CanDraftEventSteps && IsEventDraftStepsOpen;
    public bool ShowTaskDraftToggle => CanDraftTaskSteps && !IsTaskDraftStepsOpen;
    public bool ShowTaskDraftEditor => CanDraftTaskSteps && IsTaskDraftStepsOpen;

    /// <summary>
    /// The steps the selected day holds that are not on its timeline yet — planned for it, but not
    /// booked at an hour. They are unscheduled work, so they head the unscheduled list.
    /// </summary>
    public ObservableCollection<ProjectStepRowViewModel> DaySteps { get; } = [];

    public bool ShowDaySteps => HasDaySteps && IsTasksExpanded;

    /// <summary>Raised once a task's step box is drawn, so the next step can be typed straight away.</summary>
    public event EventHandler<Guid>? ProjectComposerFocusRequested;

    /// <summary>The same for an event's checklist.</summary>
    public event EventHandler<Guid>? SittingComposerFocusRequested;

    /// <summary>The steps box of the event form (true) or the task form (false) has just opened.</summary>
    public event EventHandler<bool>? DraftStepFocusRequested;

    /// <summary>
    /// Raised when the event composer opens from further down the panel. The composer sits at the
    /// head of the schedule, and a step asking for a time is usually well below it.
    /// </summary>
    public event EventHandler? EventComposerRequested;

    /// <summary>Raised to bring an event's row into view once the day it is on has been opened.</summary>
    public event EventHandler<Guid>? AgendaRowRequested;

    /// <summary>Raised to bring a task's row into view: a project was opened from somewhere else.</summary>
    public event EventHandler<Guid>? TaskRowRequested;

    /// <summary>The date picker offers "no day" only to the question that allows one.</summary>
    public bool CanClearCalendarDate => _calendarSelectionPurpose is CalendarSelectionPurpose.ProjectDraftDay
        or CalendarSelectionPurpose.ProjectStepDay;

    public string CalendarClearLabel => localization.Text("StepNoDay");

    /// <summary>The time picker is asking for a length rather than a time of day.</summary>
    private bool IsDurationPicker => _timeSelectionPurpose is TimeSelectionPurpose.TaskDuration
        or TimeSelectionPurpose.ProjectDraftDuration or TimeSelectionPurpose.ProjectStepDuration
        or TimeSelectionPurpose.SittingDraftDuration or TimeSelectionPurpose.SittingStepDuration
        or TimeSelectionPurpose.FormDraftDuration;

    private ProjectActions ProjectActionSet => _projectActions ??= new ProjectActions
    {
        AddStep = AddProjectStepAsync,
        PickDraftDuration = PickDraftDurationAsync,
        PickDraftDay = PickDraftDayAsync,
        ToggleStep = ToggleProjectStepAsync,
        PickStepDuration = PickStepDurationAsync,
        PickStepDay = PickStepDayAsync,
        StepTime = StepTimeAsync,
        DeleteStep = DeleteProjectStepAsync,
        RenameStep = RenameProjectStepAsync,
        RevealProject = row => RevealTaskProject(row.TaskId),
        AddSittingStep = AddSittingStepAsync,
        PickSittingDraftDuration = PickSittingDraftDurationAsync,
        CloseSitting = CloseEmptySitting,
        CarryToTime = CarryToTimeAsync,
        CarryToUnscheduled = CarryToUnscheduledAsync,
        ToggleSittingStep = ToggleSittingStepAsync,
        PickSittingStepDuration = PickSittingStepDurationAsync,
        DeleteSittingStep = DeleteSittingStepAsync,
        RenameSittingStep = RenameSittingStepAsync
    };

    partial void OnIsTasksExpandedChanged(bool value) => OnPropertyChanged(nameof(ShowDaySteps));
    partial void OnHasDayStepsChanged(bool value) => OnPropertyChanged(nameof(ShowDaySteps));

    partial void OnSelectedTaskCategoryOptionChanged(ChoiceOption? value)
    {
        // A list being rebuilt clears its selection for a moment; the last real choice stands.
        if (value is not null) _taskCategoryDraft = Parse(value.Value, EventCategory.Work);
    }

    // ---- Finding things -----------------------------------------------------------------

    private Project? ProjectForTask(Guid taskId) => _projectList.FirstOrDefault(project => project.TaskId == taskId);

    /// <summary>
    /// The checklist an occurrence carries: its own, or its series'. A list written on one
    /// occurrence of a repeating event is the series' list; see <see cref="HostFor"/>.
    /// </summary>
    private Project? SittingProjectFor(CalendarEvent calendarEvent)
        => _projectList.FirstOrDefault(project => project.EventId == calendarEvent.Id)
           ?? (calendarEvent.SeriesId is { } series ? _projectList.FirstOrDefault(project => project.SeriesId == series) : null);

    /// <summary>A step's slot is already part of a project; it is not broken up again.</summary>
    private bool IsStepSlot(Guid eventId)
        => _projectSteps.Any(step => step.EventId == eventId && ProjectById(step.ProjectId)?.Host == ProjectHost.Task);

    private Project? ProjectById(Guid id) => _projectList.FirstOrDefault(project => project.Id == id);

    private IEnumerable<ProjectStep> StepsOf(Guid projectId) => _projectSteps.Where(step => step.ProjectId == projectId);

    private ProjectStep? FindStep(Guid id) => _projectSteps.FirstOrDefault(step => step.Id == id);

    private CalendarEvent? AgendaEvent(Guid eventId) => Agenda.FirstOrDefault(row => row.Event?.Id == eventId)?.Event;

    private HashSet<Guid> TicksOf(Guid eventId) => _checks.TryGetValue(eventId, out var ticks) ? [.. ticks.Keys] : [];

    /// <summary>
    /// Where a checklist written on an event is kept: the series for a repeating event, because a
    /// list on a weekly review is the list of every Friday; the event itself otherwise.
    /// </summary>
    private static Project HostFor(CalendarEvent calendarEvent, Project project)
    {
        if (calendarEvent.SeriesId is { } series) project.SeriesId = series;
        else project.EventId = calendarEvent.Id;
        return project;
    }

    private SittingSummary SittingOf(CalendarEvent source, Project project)
        => SittingSummary.Build(StepsOf(project.Id), TicksOf(source.Id), EventMinutes(source));

    private static int EventMinutes(CalendarEvent source) => Math.Max(0, (int)Math.Round((source.EndUtc - source.StartUtc).TotalMinutes));

    /// <summary>
    /// Hands a freshly built day row what it needs to be broken into steps and to point at the
    /// project its slot was booked for — and the checklist it already has, so the row is drawn
    /// whole rather than gaining its list a moment after it appears.
    /// </summary>
    private void AttachRowSteps(AgendaItemViewModel row)
    {
        row.AttachSteps(BreakEventIntoStepsAsync, RevealTaskProject);
        if (row.Event is not { } source) return;
        if (_sittings.TryGetValue(source.Id, out var sitting)) row.Sitting = sitting;
        row.CanBreakIntoSteps = SittingProjectFor(source) is null && !IsStepSlot(source.Id);
    }

    // ---- Reading and laying out -------------------------------------------------------

    /// <summary>
    /// Reads the projects, lets go of the ones whose task or event is gone, lines every booked step
    /// up with its slot, keeps each project's task in line with its steps, and lays the result out.
    /// It runs with the day, which is rebuilt every minute and after every verdict — so a step
    /// whose slot was just marked done is ticked by the time the day says so.
    /// </summary>
    private async Task ReloadProjectsAsync()
    {
        var tasksChanged = false;
        try
        {
            tasksChanged = await projectRepository.AdoptHostlessAsync(TimeZoneInfo.Local) > 0;
            var projects = (await projectRepository.GetProjectsAsync()).ToList();
            var steps = (await projectRepository.GetStepsAsync()).ToList();
            var tasks = (await taskRepository.GetAllAsync()).ToDictionary(task => task.Id);

            var hostEventIds = projects.Where(project => project.EventId is not null).Select(project => project.EventId!.Value).ToList();
            var liveEvents = hostEventIds.Count == 0
                ? new HashSet<Guid>()
                : (await calendarRepository.GetByIdsAsync(hostEventIds)).Select(item => item.Id).ToHashSet();
            var liveSeries = await calendarRepository.GetLiveSeriesAsync(
                [.. projects.Where(project => project.SeriesId is not null).Select(project => project.SeriesId!.Value)]);
            var orphans = ProjectHosts.Orphaned(projects, tasks.Keys.ToHashSet(), liveEvents, liveSeries);
            if (orphans.Count > 0)
            {
                foreach (var orphan in orphans) await projectRepository.DeleteProjectAsync(orphan.Id);
                var gone = orphans.Select(project => project.Id).ToHashSet();
                projects.RemoveAll(project => gone.Contains(project.Id));
                steps.RemoveAll(step => gone.Contains(step.ProjectId));
            }

            var unscheduled = projects.Where(project => project.Host == ProjectHost.Task).Select(project => project.Id).ToHashSet();
            var taskSteps = steps.Where(step => unscheduled.Contains(step.ProjectId)).ToList();

            // A task answered for after its steps last changed has the later word, and its steps
            // follow it. Asked before the slots are lined up: a slot's verdict ticks a step
            // without restamping it, and asked after, that tick would look older than the task.
            foreach (var project in projects.Where(project => project.Host == ProjectHost.Task))
            {
                if (!tasks.TryGetValue(project.TaskId!.Value, out var host)) continue;
                var followed = ProjectTaskMirror.FollowTask(host, [.. taskSteps.Where(step => step.ProjectId == project.Id)]);
                if (followed.Count > 0) await projectRepository.SaveStepsAsync(followed);
            }

            var booked = taskSteps.Where(step => step.EventId is not null).Select(step => step.EventId!.Value).Distinct().ToList();
            var events = booked.Count == 0
                ? new Dictionary<Guid, CalendarEvent>()
                : (await calendarRepository.GetByIdsAsync(booked)).ToDictionary(item => item.Id);
            var outcomes = booked.Count == 0
                ? new Dictionary<Guid, EventOutcome>()
                : (await outcomeRepository.GetForEventsAsync(booked)).ToDictionary(item => item.EventId);
            var changed = ProjectStepReconciler.Reconcile(taskSteps, events, outcomes, TimeZoneInfo.Local);
            if (changed.Count > 0) await projectRepository.SaveStepsAsync(changed);

            // The task is what the rest of the shell reads — the assistant, the counts, the order
            // of the list — so it carries its project's length, what is left of it, and whether
            // it is done.
            foreach (var project in projects.Where(project => project.Host == ProjectHost.Task))
            {
                if (!tasks.TryGetValue(project.TaskId!.Value, out var task)) continue;
                if (!ProjectTaskMirror.Apply(task, [.. taskSteps.Where(step => step.ProjectId == project.Id)])) continue;
                await taskRepository.UpdateAsync(task);
                tasksChanged = true;
            }

            _projectList = projects;
            _projectSteps = steps;
            _projectTasks = tasks;
            _stepEvents = events;
            _stepOutcomes = outcomes;
            await LoadChecksAsync();
        }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Loading projects");
            return;
        }

        if (tasksChanged) await ReloadTasksAsync();
        ApplyProjects();
    }

    /// <summary>The ticks of every sitting the day list is showing.</summary>
    private async Task LoadChecksAsync()
    {
        var shown = Agenda.Where(row => row.Event is { } item && SittingProjectFor(item) is not null)
            .Select(row => row.Event!.Id).ToList();
        _checks.Clear();
        foreach (var check in await projectRepository.GetChecksAsync(shown))
        {
            if (!_checks.TryGetValue(check.EventId, out var ticks)) _checks[check.EventId] = ticks = new();
            ticks[check.StepId] = check.CompletedAtUtc;
        }
    }

    private void ApplyProjects()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var stepsByProject = _projectSteps.ToLookup(step => step.ProjectId);

        var hosting = new HashSet<Guid>();
        foreach (var project in _projectList.Where(project => project.Host == ProjectHost.Task))
        {
            var taskId = project.TaskId!.Value;
            if (!_projectTasks.TryGetValue(taskId, out var task)) continue;
            hosting.Add(taskId);
            if (!_taskProjects.TryGetValue(taskId, out var panel))
                _taskProjects[taskId] = panel = new TaskProjectViewModel(taskId, ProjectActionSet);
            panel.ProjectId = project.Id;
            ApplyTaskProject(panel, project, task, [.. stepsByProject[project.Id]], today);
        }

        foreach (var (taskId, panel) in _taskProjects.ToList())
        {
            if (hosting.Contains(taskId)) continue;
            // A box opened on a task with no steps yet stays open until it is put away: the project
            // is written with its first step, not with the press that asked for it.
            if (panel.IsExpanded && _projectTasks.ContainsKey(taskId))
            {
                panel.ProjectId = null;
                ApplyEmptyTaskProject(panel, today);
                continue;
            }

            _taskProjects.Remove(taskId);
        }

        foreach (var row in Tasks) row.Project = _taskProjects.GetValueOrDefault(row.Task.Id);

        ApplySittings(stepsByProject);
        ApplyProjectLabelsToAgenda();
        BuildDaySteps(today);
        ApplyCardMeta();

        if (_focusProjectComposerOf is { } focusTask && _taskProjects.ContainsKey(focusTask))
        {
            _focusProjectComposerOf = null;
            ProjectComposerFocusRequested?.Invoke(this, focusTask);
        }

        if (_focusSittingComposerOf is { } focusEvent && _sittings.ContainsKey(focusEvent))
        {
            _focusSittingComposerOf = null;
            SittingComposerFocusRequested?.Invoke(this, focusEvent);
        }
    }

    private void ApplyTaskProject(TaskProjectViewModel panel, Project project, TaskItem task, IReadOnlyList<ProjectStep> steps, DateOnly today)
    {
        var deadline = task.DeadlineUtc is { } due ? DateOnly.FromDateTime(due.ToLocalTime().Date) : (DateOnly?)null;
        var summary = ProjectSummary.Build(steps, today, deadline);
        panel.Accent = EventVisuals.AccentBrush(project.Category);
        panel.AccentSoft = EventVisuals.AccentSoftBrush(project.Category);
        panel.HasSteps = summary.TotalSteps > 0;
        panel.IsComplete = summary.IsComplete;
        panel.Progress = summary.Percent;
        panel.StepsText = $"{summary.DoneSteps}/{summary.TotalSteps}";
        panel.ProgressText = localization.Format("ProjectProgress", summary.DoneSteps, summary.TotalSteps,
            StepDurationText(summary.DoneMinutes), StepDurationText(summary.TotalMinutes));
        panel.NextText = summary.Next is { } next
            ? localization.Format("ProjectNextChip",
                next.PlannedDate is { } day ? $"{next.Title} · {StepDayLabel(day, today)}" : next.Title)
            : string.Empty;

        // The box offers the day the plan has reached, so a run of steps for one day is typed
        // without touching the chip. Once the user picks a day it stays theirs.
        if (!panel.DraftDayChosen)
            panel.DraftDay = summary.LastPlannedDate is { } last && last > today ? last : today;
        panel.DraftDayText = panel.DraftDay is { } draftDay ? StepDayLabel(draftDay, today) : localization.Text("StepNoDay");
        panel.DraftDurationText = StepDurationText(panel.DraftMinutes);

        ApplyProjectDays(panel, project, task, deadline, steps, today);
    }

    private void ApplyEmptyTaskProject(TaskProjectViewModel panel, DateOnly today)
    {
        panel.Accent = EventVisuals.AccentBrush(EventCategory.Work);
        panel.AccentSoft = EventVisuals.AccentSoftBrush(EventCategory.Work);
        panel.HasSteps = false;
        panel.IsComplete = false;
        panel.Progress = 0;
        panel.StepsText = string.Empty;
        panel.NextText = string.Empty;
        if (!panel.DraftDayChosen) panel.DraftDay = today;
        panel.DraftDayText = panel.DraftDay is { } draftDay ? StepDayLabel(draftDay, today) : localization.Text("StepNoDay");
        panel.DraftDurationText = StepDurationText(panel.DraftMinutes);
        panel.Groups.Clear();
        panel.HasFinishedDays = false;
    }

    /// <summary>
    /// The project's days, each found again by its date so a group that only gained a step keeps
    /// the rows it already had.
    /// </summary>
    private void ApplyProjectDays(TaskProjectViewModel panel, Project project, TaskItem task, DateOnly? deadline,
        IReadOnlyList<ProjectStep> steps, DateOnly today)
    {
        var days = ProjectSchedule.ByDay(steps);
        var groups = new List<ProjectDayGroupViewModel>(days.Count);
        var finished = 0;
        foreach (var day in days)
        {
            var key = day.Date?.DayNumber ?? int.MaxValue;
            var group = panel.Groups.FirstOrDefault(item => item.Key == key) ?? new ProjectDayGroupViewModel(key);
            group.IsToday = day.Date == today;
            group.IsOverdue = day.Date is { } date && date < today && day.HasOpenSteps;
            group.IsAfterDeadline = deadline is { } due && day.Date is { } planned && planned > due && day.HasOpenSteps;
            group.IsFinished = day.Date is { } past && past < today && !day.HasOpenSteps;
            group.Heading = day.Date is { } heading ? StepDayHeading(heading, today) : localization.Text("StepNoDay");
            group.TotalText = StepDurationText(day.TotalMinutes);
            Refill(group.Steps, [.. day.Steps.Select(step => BuildStepRow(step, project, task, today, showProject: false))]);
            if (group.IsFinished) finished++;
            groups.Add(group);
        }

        SyncInOrder(panel.Groups, groups);
        panel.HasFinishedDays = finished > 0;
        panel.ShowFinishedLabel = localization.Format("ProjectShowFinished", finished);
        panel.HideFinishedLabel = localization.Text("ProjectHideFinished");
        panel.ApplyFinishedVisibility();
    }

    private ProjectStepRowViewModel BuildStepRow(ProjectStep step, Project project, TaskItem task, DateOnly today, bool showProject)
    {
        var culture = localization.CurrentCulture;
        var booked = step.EventId is { } eventId ? _stepEvents.GetValueOrDefault(eventId) : null;
        var outcome = booked is null ? null : _stepOutcomes.GetValueOrDefault(booked.Id);
        var scheduleText = booked is null
            ? localization.Text("StepSchedule")
            : $"{ClockText.Format(booked.StartUtc, Use24HourClock, culture)}–{ClockText.Format(booked.EndUtc, Use24HourClock, culture)}";
        // "Done" on the event already ticked the step; the other two verdicts are worth a chip.
        var outcomeText = step.IsDone ? string.Empty : outcome?.Kind switch
        {
            EventOutcomeKind.Partial => $"{Math.Clamp(outcome.ProgressPercent, 0, 100)}%",
            EventOutcomeKind.Skipped => localization.Text("TrackSkip"),
            _ => string.Empty
        };
        var overdue = !step.IsDone && step.PlannedDate is { } planned && planned < today;
        var carried = showProject && overdue && step.PlannedDate is { } from
            ? localization.Format("StepOverdueFrom", from.ToString("ddd, d MMM", culture))
            : string.Empty;
        var durationText = StepDurationText(step.DurationMinutes);

        return new ProjectStepRowViewModel(ProjectActionSet)
        {
            Id = step.Id,
            TaskId = task.Id,
            Title = step.Title,
            IsDone = step.IsDone,
            DurationText = durationText,
            ScheduleText = scheduleText,
            ScheduleTooltip = localization.Text(booked is null ? "StepScheduleTooltip" : "StepOpenEvent"),
            IsScheduled = booked is not null,
            ProjectTitle = task.Title,
            ShowProject = showProject,
            Accent = EventVisuals.AccentBrush(project.Category),
            AccentSoft = EventVisuals.AccentSoftBrush(project.Category),
            OutcomeText = outcomeText,
            IsPartial = outcome?.Kind == EventOutcomeKind.Partial,
            IsSkipped = outcome?.Kind == EventOutcomeKind.Skipped,
            CarriedFromText = carried,
            Signature = string.Join('', step.Id, step.Title, step.IsDone, durationText, scheduleText,
                booked is null, outcomeText, carried, task.Title, project.Category, showProject, culture.Name)
        };
    }

    /// <summary>
    /// Puts every event on screen together with its checklist, if it has one or one is being
    /// written into it. A card nobody asked for stays closed except on the event happening now:
    /// that is the list the hour is being spent on.
    /// </summary>
    private void ApplySittings(ILookup<Guid, ProjectStep> stepsByProject)
    {
        var now = DateTimeOffset.UtcNow;
        var shown = new HashSet<Guid>();
        foreach (var row in Agenda)
        {
            if (row.Event is not { } source) continue;
            var project = SittingProjectFor(source);
            _sittings.TryGetValue(source.Id, out var sitting);
            if (project is null)
            {
                // Opened with "break into steps" and not written in yet: it waits, open, for the first.
                if (sitting is { IsOpen: true })
                {
                    sitting.ProjectId = null;
                    ApplyEmptySitting(sitting, source);
                    shown.Add(source.Id);
                }

                row.Sitting = sitting is { IsOpen: true } ? sitting : null;
                row.CanBreakIntoSteps = !IsStepSlot(source.Id);
                continue;
            }

            if (sitting is null) _sittings[source.Id] = sitting = new SittingViewModel(source.Id, ProjectActionSet);
            sitting.ProjectId = project.Id;
            sitting.IsSeries = project.Host == ProjectHost.Series;
            ApplySitting(sitting, row, source, stepsByProject[project.Id], now);
            row.Sitting = sitting;
            row.CanBreakIntoSteps = false;
            shown.Add(source.Id);
        }

        foreach (var gone in _sittings.Keys.Where(id => !shown.Contains(id)).ToList()) _sittings.Remove(gone);
    }

    private void ApplySitting(SittingViewModel sitting, AgendaItemViewModel row, CalendarEvent source,
        IEnumerable<ProjectStep> steps, DateTimeOffset now)
    {
        var culture = localization.CurrentCulture;
        var ordered = ProjectSchedule.Order(steps).ToList();
        var ticks = TicksOf(source.Id);
        var summary = SittingSummary.Build(ordered, ticks, EventMinutes(source));
        sitting.Accent = EventVisuals.AccentBrush(source.Category);
        sitting.AccentSoft = EventVisuals.AccentSoftBrush(source.Category);
        sitting.HasSteps = summary.TotalSteps > 0;
        sitting.IsComplete = summary.IsComplete;
        sitting.Progress = summary.ProgressPercent;
        sitting.StepsText = $"{summary.DoneSteps}/{summary.TotalSteps}";
        sitting.IsOverflowing = summary.OverflowMinutes > 0;
        sitting.SummaryText = sitting.IsOverflowing
            ? localization.Format("SittingOverflow", StepDurationText(summary.TotalMinutes), StepDurationText(summary.OverflowMinutes))
            : localization.Format("SittingPlanned", StepDurationText(summary.TotalMinutes), StepDurationText(summary.SlotMinutes));
        sitting.SeriesNote = sitting.IsSeries ? localization.Text("SittingSeriesHint") : string.Empty;
        if (!sitting.OpenChosen) sitting.IsOpen = row.IsCurrent && sitting.HasSteps;

        // What is left can be carried on once the hour has gone — or once it was answered short of done.
        var outcome = _dayOutcomes.GetValueOrDefault(source.Id);
        sitting.CanCarry = summary.OpenSteps > 0
                           && (source.EndUtc <= now || outcome?.Kind is EventOutcomeKind.Partial or EventOutcomeKind.Skipped);
        sitting.CarryText = localization.Format("SittingLeft", StepDurationText(summary.OpenMinutes));

        var rows = ordered.Select(step =>
        {
            var done = ticks.Contains(step.Id);
            var duration = StepDurationText(step.DurationMinutes);
            return new SittingStepViewModel(ProjectActionSet)
            {
                Id = step.Id,
                EventId = source.Id,
                Title = step.Title,
                IsDone = done,
                DurationText = duration,
                Accent = sitting.Accent,
                Signature = string.Join('', step.Id, source.Id, step.Title, done, duration, source.Category, culture.Name)
            };
        }).ToList();
        Refill(sitting.Steps, rows);
        sitting.DraftDurationText = StepDurationText(sitting.DraftMinutes);
    }

    /// <summary>
    /// The line under the event the day card is about: its hours, and - for an event broken into
    /// steps - the next of them. A session running on a checklist is a session on one line of it,
    /// and that line is the thing worth reading at a glance, not a count.
    /// </summary>
    private void ApplyCardMeta()
    {
        if (IsDayReviewVisible || _trackedEvent is not { } source) return;
        var hours = ClockText.Range(source.StartUtc, source.EndUtc, Use24HourClock, localization.CurrentCulture);
        var next = SittingProjectFor(source) is { } project
            ? ProjectSchedule.Order(StepsOf(project.Id)).FirstOrDefault(step => !TicksOf(source.Id).Contains(step.Id))
            : null;
        NextMeta = next is null ? hours : $"{hours} · {localization.Format("ProjectNextChip", next.Title)}";
    }

    private void ApplyEmptySitting(SittingViewModel sitting, CalendarEvent source)
    {
        sitting.Accent = EventVisuals.AccentBrush(source.Category);
        sitting.AccentSoft = EventVisuals.AccentSoftBrush(source.Category);
        sitting.HasSteps = false;
        sitting.IsComplete = false;
        sitting.Progress = 0;
        sitting.StepsText = string.Empty;
        sitting.IsOverflowing = false;
        sitting.SummaryText = localization.Format("SittingPlanned", StepDurationText(0), StepDurationText(EventMinutes(source)));
        sitting.SeriesNote = source.SeriesId is not null ? localization.Text("SittingSeriesHint") : string.Empty;
        sitting.CanCarry = false;
        sitting.Steps.Clear();
        sitting.DraftDurationText = StepDurationText(sitting.DraftMinutes);
    }

    /// <summary>
    /// What the selected day holds from unscheduled projects without an hour: the steps planned
    /// for it and not booked on it. Today also gathers what earlier days left unfinished, so a step
    /// that slipped is carried forward instead of staying behind on a day nobody opens again.
    /// </summary>
    private void BuildDaySteps(DateOnly today)
    {
        var date = SelectedDate;
        var projects = _projectList.Where(project => project.Host == ProjectHost.Task).ToDictionary(project => project.Id);
        var picked = new List<(ProjectStep Step, Project Project, TaskItem Task)>();
        foreach (var step in _projectSteps)
        {
            if (!projects.TryGetValue(step.ProjectId, out var project)
                || !_projectTasks.TryGetValue(project.TaskId!.Value, out var task)) continue;
            var bookedOn = BookedDate(step);
            var plannedHere = step.PlannedDate == date && bookedOn != date;
            var carried = date == today && !step.IsDone && step.PlannedDate is { } planned && planned < today
                          && (bookedOn is null || bookedOn < today);
            if (plannedHere || carried) picked.Add((step, project, task));
        }

        var rows = picked
            .OrderBy(item => item.Step.IsDone)
            .ThenBy(item => item.Step.PlannedDate)
            .ThenBy(item => item.Task.CreatedAtUtc)
            .ThenBy(item => item.Step.SortOrder)
            .Select(item => BuildStepRow(item.Step, item.Project, item.Task, today, showProject: true))
            .ToList();
        Refill(DaySteps, rows);
        HasDaySteps = rows.Count > 0;
        DayStepsTitle = localization.Text(date == today ? "DayStepsTitleToday" : "DayStepsTitle");

        var open = picked.Where(item => !item.Step.IsDone).ToList();
        DayStepsSummary = open.Count == 0
            ? string.Empty
            : $"{CountText(open.Count, "StepsCountOne", "StepsCountFew", "StepsCountMany")} · " +
              StepDurationText(open.Sum(item => item.Step.DurationMinutes));
    }

    private DateOnly? BookedDate(ProjectStep step)
        => step.EventId is { } id && _stepEvents.TryGetValue(id, out var booked)
            ? DateOnly.FromDateTime(booked.StartUtc.ToLocalTime().Date)
            : null;

    /// <summary>A slot booked for a step says which project it is for, in place of its category.</summary>
    private void ApplyProjectLabelsToAgenda()
    {
        var labels = new Dictionary<Guid, (string Title, Guid TaskId)>();
        foreach (var step in _projectSteps)
        {
            if (step.EventId is not { } eventId || ProjectById(step.ProjectId) is not { Host: ProjectHost.Task } project
                || !_projectTasks.TryGetValue(project.TaskId!.Value, out var task)) continue;
            labels[eventId] = (task.Title, task.Id);
        }

        foreach (var row in Agenda)
        {
            if (row.Event is { } item && labels.TryGetValue(item.Id, out var label))
            {
                row.ProjectLabel = label.Title;
                row.ProjectTaskId = label.TaskId;
            }
            else
            {
                row.ProjectLabel = string.Empty;
                row.ProjectTaskId = null;
            }
        }
    }

    /// <summary>
    /// Puts <paramref name="target"/> into the order of <paramref name="desired"/> by moving what
    /// is already in it, so an item that only changed place keeps its controls.
    /// </summary>
    private static void SyncInOrder<T>(ObservableCollection<T> target, IReadOnlyList<T> desired) where T : class
    {
        for (var index = target.Count - 1; index >= 0; index--)
            if (!desired.Contains(target[index])) target.RemoveAt(index);
        for (var index = 0; index < desired.Count; index++)
        {
            var at = target.IndexOf(desired[index]);
            if (at == index) continue;
            if (at < 0) target.Insert(index, desired[index]);
            else target.Move(at, index);
        }
    }

    /// <summary>
    /// Runs a change to a project and redraws the projects — only the projects, unless the change
    /// reached the calendar as well. Steps are ticked and added far more often than anything else
    /// in the shell is changed, and the full reload behind the other lists rebuilds the week board.
    /// </summary>
    private async Task RunProjectActionAsync(Func<Task> action, bool touchesCalendar = false)
    {
        try
        {
            await action();
            if (touchesCalendar) await ReloadDayAsync();
            else await ReloadProjectsAsync();
        }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Project operation");
            Status = exception.Message;
        }
    }

    // ---- An unscheduled project -------------------------------------------------------

    /// <summary>
    /// "Break into steps" on a task: opens the box its steps are written in. The project is only
    /// written with the first of them, so opening the box and closing it again changes nothing.
    /// </summary>
    private Task BreakTaskIntoStepsAsync(CompactTaskViewModel row)
    {
        CloseShellFlyouts();
        var taskId = row.Task.Id;
        // Pressed again on a box that is still empty, it puts the box away: nothing was written.
        if (_taskProjects.TryGetValue(taskId, out var open) && open is { IsExpanded: true, HasSteps: false })
        {
            open.IsExpanded = false;
            _taskProjects.Remove(taskId);
            row.Project = null;
            return Task.CompletedTask;
        }

        if (!_taskProjects.TryGetValue(taskId, out var panel))
        {
            _taskProjects[taskId] = panel = new TaskProjectViewModel(taskId, ProjectActionSet);
            ApplyEmptyTaskProject(panel, DateOnly.FromDateTime(DateTime.Today));
        }

        panel.IsExpanded = true;
        row.Project = panel;
        IsTasksExpanded = true;
        ProjectComposerFocusRequested?.Invoke(this, taskId);
        return Task.CompletedTask;
    }

    /// <summary>Opens a task's project wherever it was asked for — a step in the day, a booked slot.</summary>
    private void RevealTaskProject(Guid taskId)
    {
        if (!_taskProjects.TryGetValue(taskId, out var panel)) return;
        CloseShellFlyouts();
        IsTasksExpanded = true;
        panel.IsExpanded = true;
        TaskRowRequested?.Invoke(this, taskId);
    }

    private async Task<Project> EnsureTaskProjectAsync(TaskProjectViewModel panel)
    {
        if (panel.ProjectId is { } id && ProjectById(id) is { } existing) return existing;
        var title = _projectTasks.TryGetValue(panel.TaskId, out var task) ? task.Title : string.Empty;
        var project = new Project { Title = title, Category = EventCategory.Work, TaskId = panel.TaskId };
        await projectRepository.AddProjectAsync(project);
        _projectList.Add(project);
        panel.ProjectId = project.Id;
        return project;
    }

    private Task AddProjectStepAsync(TaskProjectViewModel panel) => RunProjectActionAsync(async () =>
    {
        var title = panel.DraftTitle.Trim();
        if (title.Length == 0) return;
        var project = await EnsureTaskProjectAsync(panel);
        var order = StepsOf(project.Id).Select(step => step.SortOrder).DefaultIfEmpty(-1).Max() + 1;
        var step = new ProjectStep
        {
            ProjectId = project.Id,
            Title = title,
            DurationMinutes = panel.DraftMinutes,
            PlannedDate = panel.DraftDay,
            SortOrder = order
        };
        await projectRepository.AddStepAsync(step);
        // Kept in the list at once, so a second Enter before the reload lands counts from it.
        _projectSteps.Add(step);
        panel.DraftTitle = string.Empty;
        panel.IsExpanded = true;
        // The row may be rebuilt under the box when the task's figures change; the caret comes back.
        _focusProjectComposerOf = panel.TaskId;
    });

    private Task PickDraftDurationAsync(TaskProjectViewModel panel)
    {
        ClearPickerTargets();
        _pickerProject = panel;
        OpenTimePicker(TimeSelectionPurpose.ProjectDraftDuration, TimeSpan.FromMinutes(panel.DraftMinutes));
        return Task.CompletedTask;
    }

    private Task PickDraftDayAsync(TaskProjectViewModel panel)
    {
        ClearPickerTargets();
        _pickerProject = panel;
        return OpenProjectDateAsync(CalendarSelectionPurpose.ProjectDraftDay, panel.DraftDay ?? SelectedDate);
    }

    /// <summary>
    /// The task's own tick, on a task that is a project. Ticking it finishes what was left, all at
    /// once; taking it back reopens exactly what that tick — or the last step ticked — finished,
    /// so a press made by mistake undoes cleanly and a finished project is never emptied out.
    /// Returns false for a task with no steps, which is ticked the ordinary way.
    /// </summary>
    private async Task<bool> ToggleTaskProjectAsync(TaskItem task)
    {
        if (ProjectForTask(task.Id) is not { } project) return false;
        var steps = StepsOf(project.Id).ToList();
        if (steps.Count == 0) return false;

        var now = DateTimeOffset.UtcNow;
        List<ProjectStep> changed;
        if (steps.All(step => step.IsDone))
        {
            changed = [.. CompletionBatch.Latest(steps, step => step.CompletedAtUtc)];
            if (changed.Count == 0) changed = steps;
            foreach (var step in changed)
            {
                step.IsDone = false;
                step.CompletedAtUtc = null;
            }

            Status = localization.Text("TaskRestored");
        }
        else
        {
            changed = [.. steps.Where(step => !step.IsDone)];
            foreach (var step in changed)
            {
                step.IsDone = true;
                step.CompletedAtUtc = now;
            }

            Status = localization.Text("ProjectFinished");
        }

        foreach (var step in changed) step.UpdatedAtUtc = now;
        await projectRepository.SaveStepsAsync(changed);
        if (ProjectTaskMirror.Apply(task, steps)) await taskRepository.UpdateAsync(task);
        return true;
    }

    /// <summary>Removes a task's project along with the task: its steps have nowhere else to be.</summary>
    private async Task DeleteTaskProjectAsync(Guid taskId)
    {
        if (ProjectForTask(taskId) is { } project) await projectRepository.DeleteProjectAsync(project.Id);
        _taskProjects.Remove(taskId);
    }

    private Task ToggleProjectStepAsync(ProjectStepRowViewModel row) => RunProjectActionAsync(async () =>
    {
        if (FindStep(row.Id) is not { } step) return;
        step.IsDone = !step.IsDone;
        step.CompletedAtUtc = step.IsDone ? DateTimeOffset.UtcNow : null;
        await projectRepository.UpdateStepAsync(step);
    });

    private Task PickStepDurationAsync(ProjectStepRowViewModel row)
    {
        if (FindStep(row.Id) is not { } step) return Task.CompletedTask;
        // A booked step lasts as long as its event does, and the event is where that is changed.
        if (row.IsScheduled) return StepTimeAsync(row);
        ClearPickerTargets();
        _pickerStepId = row.Id;
        OpenTimePicker(TimeSelectionPurpose.ProjectStepDuration, TimeSpan.FromMinutes(step.DurationMinutes));
        return Task.CompletedTask;
    }

    private Task PickStepDayAsync(ProjectStepRowViewModel row)
    {
        if (FindStep(row.Id) is not { } step) return Task.CompletedTask;
        if (row.IsScheduled) return StepTimeAsync(row);
        ClearPickerTargets();
        _pickerStepId = row.Id;
        return OpenProjectDateAsync(CalendarSelectionPurpose.ProjectStepDay, step.PlannedDate ?? SelectedDate);
    }

    private Task MoveStepToDayAsync(Guid? stepId, DateOnly? date) => RunProjectActionAsync(async () =>
    {
        if (stepId is not { } id || FindStep(id) is not { } step || step.PlannedDate == date) return;
        step.PlannedDate = date;
        await projectRepository.UpdateStepAsync(step);
    });

    /// <summary>
    /// Deletes a step. The last one takes the project with it and leaves the task as an ordinary
    /// task again — with the length its steps last added up to.
    /// </summary>
    private Task DeleteProjectStepAsync(ProjectStepRowViewModel row) => RunProjectActionAsync(async () =>
    {
        if (FindStep(row.Id) is not { } step) return;
        await projectRepository.DeleteStepAsync(step.Id);
        _projectSteps.Remove(step);
        if (!StepsOf(step.ProjectId).Any())
        {
            await projectRepository.DeleteProjectAsync(step.ProjectId);
            if (_taskProjects.TryGetValue(row.TaskId, out var panel)) panel.IsExpanded = false;
            Status = localization.Text("StepsRemoved");
        }
    });

    private Task RenameProjectStepAsync(ProjectStepRowViewModel row, string title)
    {
        var step = FindStep(row.Id);
        var booked = step?.EventId is { } eventId ? _stepEvents.GetValueOrDefault(eventId) : null;
        // The booked event carries the step's name on the calendar, and follows a rename — unless
        // it was given a name of its own there, which is the user's to keep.
        var renameEvent = booked is not null && booked.Title == step!.Title;
        return RunProjectActionAsync(async () =>
        {
            if (step is null) return;
            step.Title = title;
            await projectRepository.UpdateStepAsync(step);
            if (renameEvent)
            {
                booked!.Title = title;
                await calendarRepository.UpdateAsync(booked);
            }
        }, touchesCalendar: renameEvent);
    }

    // ---- Booking an hour for an unscheduled step ---------------------------------------

    /// <summary>
    /// The time chip: a step without an hour opens the composer to book one, a booked step opens
    /// the day its event is on and points at it.
    /// </summary>
    private async Task StepTimeAsync(ProjectStepRowViewModel row)
    {
        if (FindStep(row.Id) is not { } step) return;
        if (step.EventId is { } eventId && _stepEvents.TryGetValue(eventId, out var booked))
        {
            var day = DateOnly.FromDateTime(booked.StartUtc.ToLocalTime().Date);
            if (day != SelectedDate) await SelectDateSafelyAsync(day);
            IsScheduleExpanded = true;
            FlashEvent(booked.Id);
            AgendaRowRequested?.Invoke(this, booked.Id);
            return;
        }

        await BeginSchedulingStepAsync(step);
    }

    /// <summary>
    /// Opens the event composer on a step, already filled in: its name, its project's category,
    /// its day — today, for a step whose day has passed — and the first gap in that day long
    /// enough for it. Saving books the slot and ties it to the step.
    /// </summary>
    private async Task BeginSchedulingStepAsync(ProjectStep step)
    {
        if (ProjectById(step.ProjectId) is not { } project) return;
        var today = DateOnly.FromDateTime(DateTime.Today);
        var date = step.PlannedDate ?? SelectedDate;
        if (date < today) date = today;
        if (date != SelectedDate) await SelectDateSafelyAsync(date);
        var start = await SuggestSlotAsync(date, step.DurationMinutes);

        OpenPrefilledComposer(step.Title, project.Category, date, start, step.DurationMinutes, localization.Text("StepScheduleHeading"));
        // A slot belongs to its step; it is not broken into steps of its own.
        CanDraftEventSteps = false;
        // Set once the composer is up: closing it is what lets go of the step.
        _schedulingStepId = step.Id;
        EventComposerRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task<TimeSpan> SuggestSlotAsync(DateOnly date, int minutes)
    {
        var (dayStart, dayEnd) = DayRange(date);
        var busy = await calendarRepository.GetRangeAsync(dayStart, dayEnd);
        return StepSlotFinder.Suggest(date, minutes, busy.Select(item => (item.StartUtc, item.EndUtc)),
            DateTimeOffset.Now, TimeZoneInfo.Local);
    }

    /// <summary>The event composer, opened as a new event already filled in.</summary>
    private void OpenPrefilledComposer(string title, EventCategory category, DateOnly date, TimeSpan start, int minutes, string heading)
    {
        CloseShellFlyouts();
        CancelTaskEdit();
        ResetEventConfirmation();
        ResetEventDrafts(allowed: true);
        _schedulingStepId = null;
        _editedEvent = null;
        OnPropertyChanged(nameof(IsEditingEvent));
        OnPropertyChanged(nameof(IsEditingRecurringEvent));
        EventTitle = title;
        EventValidationMessage = string.Empty;
        SelectEventPlanningOptions();
        SelectedEventCategoryOption = EventCategoryOptions.FirstOrDefault(option => option.Value == category.ToString())
                                      ?? SelectedEventCategoryOption;
        _eventEndDayOffset = 0;
        EventTime = start;
        EventEndTime = TimeSpan.FromMinutes((start.TotalMinutes + minutes) % (24 * 60));
        EventTimeText = FormatClockTime(EventTime);
        EventEndTimeText = FormatClockTime(EventEndTime);
        EventActionText = heading;
        EventDate = ToLocalOffset(date);
        RefreshDateLabels();
        IsScheduleExpanded = true;
        IsEventComposerVisible = true;
    }

    /// <summary>Ties the event the composer has just saved to the step it was opened for.</summary>
    private async Task LinkScheduledStepAsync(Guid eventId, DateOnly day)
    {
        if (_schedulingStepId is not { } stepId) return;
        _schedulingStepId = null;
        if (FindStep(stepId) is not { } step) return;
        step.EventId = eventId;
        step.PlannedDate = day;
        await projectRepository.UpdateStepAsync(step);
    }

    // ---- A sitting: an event broken into steps ------------------------------------------

    /// <summary>
    /// "Break into steps" on an event: opens its checklist, empty, with the caret in the box. The
    /// list is written with its first step, and a box closed empty leaves the event as it was.
    /// </summary>
    private Task BreakEventIntoStepsAsync(AgendaItemViewModel row)
    {
        if (row.Event is not { } source || IsStepSlot(source.Id)) return Task.CompletedTask;
        CloseShellFlyouts();
        if (!_sittings.TryGetValue(source.Id, out var sitting))
        {
            _sittings[source.Id] = sitting = new SittingViewModel(source.Id, ProjectActionSet);
            ApplyEmptySitting(sitting, source);
        }

        sitting.OpenChosen = true;
        sitting.IsOpen = true;
        row.Sitting = sitting;
        IsScheduleExpanded = true;
        SittingComposerFocusRequested?.Invoke(this, source.Id);
        return Task.CompletedTask;
    }

    /// <summary>A checklist opened and put away without a single step: the event is left as it was.</summary>
    private void CloseEmptySitting(SittingViewModel sitting)
    {
        if (sitting.HasSteps) return;
        _sittings.Remove(sitting.EventId);
        foreach (var row in Agenda.Where(item => item.Event?.Id == sitting.EventId)) row.Sitting = null;
    }

    private async Task<Project?> EnsureSittingProjectAsync(SittingViewModel sitting, CalendarEvent source)
    {
        if (sitting.ProjectId is { } id && ProjectById(id) is { } existing) return existing;
        if (SittingProjectFor(source) is { } found) return found;
        var project = HostFor(source, new Project { Title = source.Title, Category = source.Category });
        await projectRepository.AddProjectAsync(project);
        _projectList.Add(project);
        sitting.ProjectId = project.Id;
        sitting.IsSeries = project.Host == ProjectHost.Series;
        return project;
    }

    private Task AddSittingStepAsync(SittingViewModel sitting) => RunProjectActionAsync(async () =>
    {
        var title = sitting.DraftTitle.Trim();
        if (title.Length == 0 || AgendaEvent(sitting.EventId) is not { } source) return;
        if (await EnsureSittingProjectAsync(sitting, source) is not { } project) return;
        var order = StepsOf(project.Id).Select(step => step.SortOrder).DefaultIfEmpty(-1).Max() + 1;
        var step = new ProjectStep
        {
            ProjectId = project.Id,
            Title = title,
            DurationMinutes = sitting.DraftMinutes,
            SortOrder = order
        };
        await projectRepository.AddStepAsync(step);
        _projectSteps.Add(step);
        sitting.DraftTitle = string.Empty;
        sitting.OpenChosen = true;
        sitting.IsOpen = true;
        _focusSittingComposerOf = sitting.EventId;
    });

    private Task PickSittingDraftDurationAsync(SittingViewModel sitting)
    {
        ClearPickerTargets();
        _pickerSitting = sitting;
        OpenTimePicker(TimeSelectionPurpose.SittingDraftDuration, TimeSpan.FromMinutes(sitting.DraftMinutes));
        return Task.CompletedTask;
    }

    private Task PickSittingStepDurationAsync(SittingStepViewModel row)
    {
        if (FindStep(row.Id) is not { } step) return Task.CompletedTask;
        ClearPickerTargets();
        _pickerStepId = row.Id;
        OpenTimePicker(TimeSelectionPurpose.SittingStepDuration, TimeSpan.FromMinutes(step.DurationMinutes));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Ticks a step in this sitting, or takes the tick back — and says what that means for the
    /// event: the last tick answers "done", a tick taken back from under "done" makes it "partly",
    /// and a "partly" keeps the figure the ticks come to. While "how did it go?" is open on this
    /// very event the ticks only move its figure: the answer is being written right there.
    /// </summary>
    private async Task ToggleSittingStepAsync(SittingStepViewModel row)
    {
        if (AgendaEvent(row.EventId) is not { } source || SittingProjectFor(source) is not { } project) return;
        try
        {
            if (row.IsDone) await projectRepository.UncheckAsync(source.Id, row.Id);
            else await projectRepository.CheckAsync(source.Id, [row.Id], DateTimeOffset.UtcNow);
            await ReloadProjectsAsync();

            var sitting = SittingOf(source, project);
            if (_sessionReviewTarget?.Id == source.Id)
            {
                if (sitting.ProgressPercent > 0) SessionReviewProgress = Math.Clamp(sitting.ProgressPercent, 1, 100);
                return;
            }

            await AnswerForSittingAsync(source, sitting);
        }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Ticking a step");
            Status = exception.Message;
        }
    }

    private async Task AnswerForSittingAsync(CalendarEvent source, SittingSummary sitting)
    {
        var outcome = _dayOutcomes.GetValueOrDefault(source.Id);
        switch (SittingVerdictPolicy.AfterTick(sitting, outcome, SittingVerdictPolicy.HasBegun(source, DateTimeOffset.UtcNow)))
        {
            case SittingFollowUp.AnswerDone:
                await RecordOutcomeAsync(source, EventOutcomeKind.Completed);
                Status = localization.Text("SittingAnsweredDone");
                break;
            case SittingFollowUp.ReopenAsPartial when outcome is not null:
                await RecordOutcomeAsync(source, EventOutcomeKind.Partial, outcome.ActualMinutes,
                    Math.Clamp(sitting.ProgressPercent, 1, 99));
                Status = localization.Text("SittingReopened");
                break;
            case SittingFollowUp.FollowProgress:
                var draft = DraftOutcome(source);
                draft.ProgressPercent = Math.Clamp(sitting.ProgressPercent, 1, 99);
                await PersistOutcomeAsync(source, draft, "Following a sitting's ticks");
                break;
        }
    }

    /// <summary>
    /// "Done", said of an event with steps, is said of its steps too: whatever was left is ticked
    /// with the verdict's own stamp, which is how taking the verdict back finds those ticks again.
    /// </summary>
    private async Task TickRestOfSittingAsync(CalendarEvent source, DateTimeOffset stamp)
    {
        if (SittingProjectFor(source) is not { } project) return;
        var ticks = TicksOf(source.Id);
        var open = StepsOf(project.Id).Where(step => !ticks.Contains(step.Id)).Select(step => step.Id).ToList();
        if (open.Count == 0) return;
        await projectRepository.CheckAsync(source.Id, open, stamp);
    }

    /// <summary>Taking a "done" back takes back the ticks it wrote, and only those.</summary>
    private async Task UntickVerdictAsync(CalendarEvent source, EventOutcome? previous)
    {
        if (previous is not { Kind: EventOutcomeKind.Completed, FinishedAtUtc: { } stamp }) return;
        if (SittingProjectFor(source) is null) return;
        await projectRepository.UncheckBatchAsync(source.Id, stamp);
    }

    private Task DeleteSittingStepAsync(SittingStepViewModel row) => RunProjectActionAsync(async () =>
    {
        if (FindStep(row.Id) is not { } step) return;
        await projectRepository.DeleteStepAsync(step.Id);
        _projectSteps.Remove(step);
        if (StepsOf(step.ProjectId).Any()) return;
        // The last step goes and the list with it. The card stays open for a moment, empty, in
        // case the step was being replaced rather than removed.
        await projectRepository.DeleteProjectAsync(step.ProjectId);
        _projectList.RemoveAll(project => project.Id == step.ProjectId);
        if (_sittings.TryGetValue(row.EventId, out var sitting))
        {
            sitting.ProjectId = null;
            sitting.OpenChosen = true;
            sitting.IsOpen = true;
        }

        Status = localization.Text("StepsRemoved");
    });

    private Task RenameSittingStepAsync(SittingStepViewModel row, string title) => RunProjectActionAsync(async () =>
    {
        if (FindStep(row.Id) is not { } step) return;
        step.Title = title;
        await projectRepository.UpdateStepAsync(step);
    });

    private List<ProjectStep> OpenStepsOf(SittingViewModel sitting)
    {
        if (sitting.ProjectId is not { } projectId) return [];
        var ticks = TicksOf(sitting.EventId);
        return [.. ProjectSchedule.Order(StepsOf(projectId)).Where(step => !ticks.Contains(step.Id))];
    }

    /// <summary>
    /// "Continue…": what the sitting left undone, given an hour of its own. The composer opens on
    /// the first gap long enough for it — later today while today still has room, tomorrow
    /// otherwise — with the event's name and colour and the unfinished steps already in it.
    /// </summary>
    private async Task CarryToTimeAsync(SittingViewModel sitting)
    {
        if (AgendaEvent(sitting.EventId) is not { } source || sitting.ProjectId is not { } projectId) return;
        var open = OpenStepsOf(sitting);
        if (open.Count == 0) return;
        try
        {
            var minutes = Math.Max(15, (int)Math.Ceiling(open.Sum(step => step.DurationMinutes) / 5.0) * 5);
            var date = DateOnly.FromDateTime(DateTime.Today);
            var start = await SuggestSlotAsync(date, minutes);
            if (!StepSlotFinder.FitsWorkingDay(start, minutes))
            {
                date = date.AddDays(1);
                start = await SuggestSlotAsync(date, minutes);
            }

            var carry = new CarrySource(source.Id, projectId, sitting.IsSeries);
            if (date != SelectedDate) await SelectDateSafelyAsync(date);
            OpenPrefilledComposer(source.Title, source.Category, date, start, minutes, localization.Text("CarryHeading"));
            _carrySource = carry;
            foreach (var step in open)
                EventDraftSteps.Add(new DraftStepViewModel(RemoveEventDraftStep)
                {
                    Title = step.Title,
                    Minutes = step.DurationMinutes,
                    DurationText = StepDurationText(step.DurationMinutes),
                    SourceStepId = step.Id
                });
            IsEventDraftStepsOpen = true;
            RefreshEventDraftSummary();
            EventComposerRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Carrying a sitting on");
            Status = exception.Message;
        }
    }

    /// <summary>
    /// "To unscheduled": what the sitting left undone, as a task of the same name broken into those
    /// steps — to be planned over days like any other, rather than squeezed into the next free hour.
    /// </summary>
    private Task CarryToUnscheduledAsync(SittingViewModel sitting) => RunProjectActionAsync(async () =>
    {
        if (AgendaEvent(sitting.EventId) is not { } source || sitting.ProjectId is not { } projectId) return;
        var open = OpenStepsOf(sitting);
        if (open.Count == 0) return;

        var minutes = open.Sum(step => step.DurationMinutes);
        var task = new TaskItem
        {
            Title = source.Title,
            Status = DomainTaskStatus.Ready,
            Priority = source.Priority switch { EventPriority.Critical => 90, EventPriority.High => 70, _ => 50 },
            EstimatedDurationMin = Math.Max(1, minutes),
            RemainingDurationMin = minutes,
            MinBlockMin = Math.Max(1, Math.Min(30, minutes))
        };
        await taskRepository.AddAsync(task);
        var target = new Project { Title = task.Title, Category = source.Category, TaskId = task.Id };
        await projectRepository.AddProjectAsync(target);
        await HandOverStepsAsync(open, projectId, target.Id, sitting.IsSeries);

        _taskProjects[task.Id] = new TaskProjectViewModel(task.Id, ProjectActionSet) { IsExpanded = true };
        IsTasksExpanded = true;
        Status = localization.Text("StepsCarriedToUnscheduled");
        await ReloadTasksAsync();
        TaskRowRequested?.Invoke(this, task.Id);
    });

    /// <summary>
    /// Gives open steps to another project: moved, when they are one sitting's own — the sitting
    /// keeps what it did and nothing more — and copied, when they are a whole series' list, which
    /// the next occurrence still needs. A sitting left with nothing at all loses its list.
    /// </summary>
    private async Task HandOverStepsAsync(IReadOnlyList<ProjectStep> steps, Guid fromProject, Guid toProject, bool isSeries)
    {
        if (steps.Count == 0) return;
        if (isSeries)
        {
            var order = 0;
            await projectRepository.AddStepsAsync([.. steps.Select(step => new ProjectStep
            {
                ProjectId = toProject,
                Title = step.Title,
                DurationMinutes = step.DurationMinutes,
                SortOrder = order++
            })]);
            return;
        }

        await projectRepository.MoveStepsAsync([.. steps.Select(step => step.Id)], toProject);
        var moved = steps.Select(step => step.Id).ToHashSet();
        if (!StepsOf(fromProject).Any(step => !moved.Contains(step.Id)))
            await projectRepository.DeleteProjectAsync(fromProject);
    }

    // ---- Copies ---------------------------------------------------------------------------

    /// <summary>
    /// A copied event takes its checklist with it, unticked: copying Friday's review to next
    /// Tuesday is copying the list it is done by, not what was ticked in it last time.
    /// </summary>
    private async Task CopySittingAsync(CalendarEvent source, CalendarEvent copy)
    {
        if (SittingProjectFor(source) is not { } project) return;
        var steps = ProjectSchedule.Order(StepsOf(project.Id)).ToList();
        if (steps.Count == 0) return;
        var target = HostFor(copy, new Project { Title = copy.Title, Category = copy.Category });
        await projectRepository.AddProjectAsync(target);
        var order = 0;
        await projectRepository.AddStepsAsync([.. steps.Select(step => new ProjectStep
        {
            ProjectId = target.Id,
            Title = step.Title,
            DurationMinutes = step.DurationMinutes,
            SortOrder = order++
        })]);
    }

    /// <summary>
    /// Keeps a checklist attached when saving an event replaced it with new occurrences — which
    /// is what changing how an event repeats does. The list follows the event to its new ids;
    /// when only this and the following occurrences were changed, the earlier ones keep theirs and
    /// the new series gets a copy.
    /// </summary>
    private async Task RehostSittingAsync(CalendarEvent original, EventSeriesScope scope, IReadOnlyCollection<CalendarEvent> replacements)
    {
        if (SittingProjectFor(original) is not { } project || replacements.Count == 0) return;
        var first = replacements.OrderBy(item => item.StartUtc).First();
        var splitsSeries = project.Host == ProjectHost.Series && scope != EventSeriesScope.Series;
        if (splitsSeries)
        {
            await CopySittingAsync(original, first);
            return;
        }

        project.EventId = null;
        project.SeriesId = null;
        HostFor(first, project);
        await projectRepository.UpdateProjectAsync(project);
    }

    // ---- The forms' steps -----------------------------------------------------------------

    private void ResetEventDrafts(bool allowed)
    {
        EventDraftSteps.Clear();
        EventDraftStepText = string.Empty;
        IsEventDraftStepsOpen = false;
        CanDraftEventSteps = allowed;
        _carrySource = null;
        EventDraftDurationText = StepDurationText(_eventDraftMinutes);
        RefreshEventDraftSummary();
    }

    private void ResetTaskDrafts(bool allowed)
    {
        TaskDraftSteps.Clear();
        TaskDraftStepText = string.Empty;
        IsTaskDraftStepsOpen = false;
        CanDraftTaskSteps = allowed;
        TaskDraftDurationText = StepDurationText(_taskDraftMinutes);
        RefreshTaskDraftSummary();
    }

    [RelayCommand]
    private void OpenEventDraftSteps()
    {
        IsEventDraftStepsOpen = true;
        RefreshEventDraftSummary();
        DraftStepFocusRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void OpenTaskDraftSteps()
    {
        IsTaskDraftStepsOpen = true;
        RefreshTaskDraftSummary();
        DraftStepFocusRequested?.Invoke(this, false);
    }

    [RelayCommand]
    private void AddEventDraftStep()
    {
        var title = EventDraftStepText.Trim();
        if (title.Length == 0) return;
        EventDraftSteps.Add(new DraftStepViewModel(RemoveEventDraftStep)
        {
            Title = title,
            Minutes = _eventDraftMinutes,
            DurationText = StepDurationText(_eventDraftMinutes)
        });
        EventDraftStepText = string.Empty;
        RefreshEventDraftSummary();
    }

    [RelayCommand]
    private void AddTaskDraftStep()
    {
        var title = TaskDraftStepText.Trim();
        if (title.Length == 0) return;
        TaskDraftSteps.Add(new DraftStepViewModel(RemoveTaskDraftStep)
        {
            Title = title,
            Minutes = _taskDraftMinutes,
            DurationText = StepDurationText(_taskDraftMinutes)
        });
        TaskDraftStepText = string.Empty;
        RefreshTaskDraftSummary();
    }

    private void RemoveEventDraftStep(DraftStepViewModel step)
    {
        EventDraftSteps.Remove(step);
        RefreshEventDraftSummary();
    }

    private void RemoveTaskDraftStep(DraftStepViewModel step)
    {
        TaskDraftSteps.Remove(step);
        RefreshTaskDraftSummary();
    }

    [RelayCommand]
    private void PickEventDraftDuration()
    {
        ClearPickerTargets();
        _pickerDraftIsEvent = true;
        OpenTimePicker(TimeSelectionPurpose.FormDraftDuration, TimeSpan.FromMinutes(_eventDraftMinutes));
    }

    [RelayCommand]
    private void PickTaskDraftDuration()
    {
        ClearPickerTargets();
        _pickerDraftIsEvent = false;
        OpenTimePicker(TimeSelectionPurpose.FormDraftDuration, TimeSpan.FromMinutes(_taskDraftMinutes));
    }

    /// <summary>The length of a line already on a form's list. A line carried over keeps its step's length.</summary>
    [RelayCommand]
    private void PickDraftLineDuration(DraftStepViewModel? line)
    {
        if (line is null || line.SourceStepId is not null) return;
        ClearPickerTargets();
        _pickerDraft = line;
        _pickerDraftIsEvent = EventDraftSteps.Contains(line);
        OpenTimePicker(TimeSelectionPurpose.FormDraftDuration, TimeSpan.FromMinutes(line.Minutes));
    }

    /// <summary>How the steps written so far sit in the event being written: "1 h 30 min of 2 h".</summary>
    private void RefreshEventDraftSummary()
    {
        OnPropertyChanged(nameof(HasEventDraftSteps));
        var total = EventDraftSteps.Sum(step => step.Minutes);
        var slot = ComposerMinutes();
        IsEventDraftOverflowing = slot > 0 && total > slot;
        EventDraftSummary = EventDraftSteps.Count == 0
            ? string.Empty
            : IsEventDraftOverflowing
                ? localization.Format("SittingOverflow", StepDurationText(total), StepDurationText(total - slot))
                : localization.Format("SittingPlanned", StepDurationText(total), StepDurationText(slot));
    }

    private void RefreshTaskDraftSummary()
    {
        OnPropertyChanged(nameof(HasTaskDraftSteps));
        IsTaskEditorProject = _editedTaskIsProject || TaskDraftSteps.Count > 0;
        TaskDraftSummary = TaskDraftSteps.Count == 0
            ? string.Empty
            : $"{CountText(TaskDraftSteps.Count, "StepsCountOne", "StepsCountFew", "StepsCountMany")} · " +
              StepDurationText(TaskDraftSteps.Sum(step => step.Minutes));
    }

    /// <summary>The length of the event the composer is writing, overnight ones included.</summary>
    private int ComposerMinutes()
    {
        if (EventTime is not { } start || EventEndTime is not { } end) return 0;
        var minutes = (int)Math.Round((end - start).TotalMinutes) + _eventEndDayOffset * 24 * 60;
        if (minutes <= 0) minutes += 24 * 60;
        return minutes;
    }

    /// <summary>
    /// Writes the steps on the event form into the event just saved. A carried-over line brings
    /// its step along — moved from a one-off sitting, copied from a series' list — and a new line
    /// becomes a new step. Whatever is still in the box counts, as it does everywhere a list is
    /// written: making the user press "add" for the last line would be asking to be told twice.
    /// </summary>
    private async Task AttachEventDraftStepsAsync(CalendarEvent host)
    {
        AddEventDraftStep();
        if (EventDraftSteps.Count == 0) return;
        var project = SittingProjectFor(host);
        if (project is null)
        {
            project = HostFor(host, new Project { Title = host.Title, Category = host.Category });
            await projectRepository.AddProjectAsync(project);
            _projectList.Add(project);
        }

        var carried = EventDraftSteps.Where(line => line.SourceStepId is not null).ToList();
        if (_carrySource is { } carry && carried.Count > 0)
        {
            var sources = carried.Select(line => FindStep(line.SourceStepId!.Value)).OfType<ProjectStep>().ToList();
            await HandOverStepsAsync(sources, carry.ProjectId, project.Id, carry.IsSeries);
        }

        var order = StepsOf(project.Id).Select(step => step.SortOrder).DefaultIfEmpty(-1).Max() + carried.Count + 1;
        var fresh = EventDraftSteps.Where(line => line.SourceStepId is null).Select(line => new ProjectStep
        {
            ProjectId = project.Id,
            Title = line.Title,
            DurationMinutes = Math.Max(5, line.Minutes),
            SortOrder = order++
        }).ToList();
        await projectRepository.AddStepsAsync(fresh);

        // The list just written is shown, open, on the row it now belongs to.
        _sittings[host.Id] = new SittingViewModel(host.Id, ProjectActionSet) { IsOpen = true, OpenChosen = true };
        if (_carrySource is not null) Status = localization.Text("StepsCarried");
        ResetEventDrafts(allowed: true);
    }

    /// <summary>The same for the task form: the task becomes a project, its steps not yet given days.</summary>
    private async Task AttachTaskDraftStepsAsync(TaskItem host)
    {
        AddTaskDraftStep();
        if (TaskDraftSteps.Count == 0) return;
        var project = ProjectForTask(host.Id);
        if (project is null)
        {
            project = new Project { Title = host.Title, Category = _taskCategoryDraft, TaskId = host.Id };
            await projectRepository.AddProjectAsync(project);
            _projectList.Add(project);
        }

        var order = StepsOf(project.Id).Select(step => step.SortOrder).DefaultIfEmpty(-1).Max() + 1;
        await projectRepository.AddStepsAsync([.. TaskDraftSteps.Select(line => new ProjectStep
        {
            ProjectId = project.Id,
            Title = line.Title,
            DurationMinutes = Math.Max(5, line.Minutes),
            SortOrder = order++
        })]);

        // Opened, so its days can be planned straight away.
        if (!_taskProjects.TryGetValue(host.Id, out var panel))
            _taskProjects[host.Id] = panel = new TaskProjectViewModel(host.Id, ProjectActionSet);
        panel.IsExpanded = true;
        ResetTaskDrafts(allowed: false);
    }

    /// <summary>The task form's colour, written to the project it describes.</summary>
    private async Task SaveTaskProjectCategoryAsync(Guid taskId)
    {
        if (ProjectForTask(taskId) is not { } project || project.Category == _taskCategoryDraft) return;
        project.Category = _taskCategoryDraft;
        await projectRepository.UpdateProjectAsync(project);
    }

    /// <summary>Sets the task form up for the task it opens on: a project gets its colour asked, not its length.</summary>
    private void PrepareTaskFormForProjects(TaskItem? task)
    {
        var project = task is null ? null : ProjectForTask(task.Id);
        _editedTaskIsProject = project is not null && StepsOf(project.Id).Any();
        _taskCategoryDraft = project?.Category ?? EventCategory.Work;
        SelectedTaskCategoryOption = EventCategoryOptions.FirstOrDefault(option => option.Value == _taskCategoryDraft.ToString())
                                     ?? EventCategoryOptions.FirstOrDefault();
        // A project's steps are written on its own card, where their days are; the form only
        // breaks up a task that is not one yet.
        ResetTaskDrafts(allowed: !_editedTaskIsProject);
    }

    // ---- The shared pickers -----------------------------------------------------------

    private void ClearPickerTargets()
    {
        _pickerStepId = null;
        _pickerProject = null;
        _pickerSitting = null;
        _pickerDraft = null;
    }

    private async Task OpenProjectDateAsync(CalendarSelectionPurpose purpose, DateOnly date)
    {
        CloseTimePicker();
        _calendarSelectionPurpose = purpose;
        _projectPickerDate = date;
        _calendarMonth = new DateOnly(date.Year, date.Month, 1);
        OnPropertyChanged(nameof(CanClearCalendarDate));
        OnPropertyChanged(nameof(CalendarClearLabel));
        IsTaskDatePickerOpen = false;
        IsEventDatePickerOpen = true;
        await RefreshCalendarAsync();
    }

    /// <summary>The answer to one of the project questions the date picker was opened for; null is "no day".</summary>
    private async Task ApplyProjectDateAsync(DateOnly? date)
    {
        switch (_calendarSelectionPurpose)
        {
            case CalendarSelectionPurpose.ProjectDraftDay when _pickerProject is { } panel:
                panel.DraftDay = date;
                panel.DraftDayChosen = true;
                panel.DraftDayText = date is { } day
                    ? StepDayLabel(day, DateOnly.FromDateTime(DateTime.Today))
                    : localization.Text("StepNoDay");
                break;
            case CalendarSelectionPurpose.ProjectStepDay:
                await MoveStepToDayAsync(_pickerStepId, date);
                break;
        }
    }

    [RelayCommand]
    private async Task ClearCalendarDateAsync()
    {
        if (!CanClearCalendarDate) return;
        IsEventDatePickerOpen = false;
        await ApplyProjectDateAsync(null);
    }

    private async Task ApplyProjectDurationAsync(TimeSpan? value)
    {
        var minutes = Math.Clamp((int)Math.Round((value ?? TimeSpan.Zero).TotalMinutes), 5, 24 * 60);
        switch (_timeSelectionPurpose)
        {
            case TimeSelectionPurpose.ProjectDraftDuration when _pickerProject is { } panel:
                panel.DraftMinutes = minutes;
                panel.DraftDurationText = StepDurationText(minutes);
                return;
            case TimeSelectionPurpose.SittingDraftDuration when _pickerSitting is { } sitting:
                sitting.DraftMinutes = minutes;
                sitting.DraftDurationText = StepDurationText(minutes);
                return;
            case TimeSelectionPurpose.FormDraftDuration:
                if (_pickerDraft is { } line)
                {
                    line.Minutes = minutes;
                    line.DurationText = StepDurationText(minutes);
                }
                else if (_pickerDraftIsEvent)
                {
                    _eventDraftMinutes = minutes;
                    EventDraftDurationText = StepDurationText(minutes);
                }
                else
                {
                    _taskDraftMinutes = minutes;
                    TaskDraftDurationText = StepDurationText(minutes);
                }

                RefreshEventDraftSummary();
                RefreshTaskDraftSummary();
                return;
            case TimeSelectionPurpose.ProjectStepDuration or TimeSelectionPurpose.SittingStepDuration
                when _pickerStepId is { } id:
                await RunProjectActionAsync(async () =>
                {
                    if (FindStep(id) is not { } step || step.DurationMinutes == minutes) return;
                    step.DurationMinutes = minutes;
                    await projectRepository.UpdateStepAsync(step);
                });
                return;
        }
    }

    // ---- Wording ----------------------------------------------------------------------

    private string StepDurationText(int minutes) => ScheduleText.Duration(TimeSpan.FromMinutes(minutes), localization);

    private string CountText(int count, string one, string few, string many)
        => localization.Format(PluralKey(count, one, few, many), count);

    /// <summary>"today", "tomorrow" or "Thu, 17 Sep" — the way a chip names a day.</summary>
    private string StepDayLabel(DateOnly date, DateOnly today)
        => date == today ? localization.Text("TodayBadge")
            : date == today.AddDays(1) ? localization.Text("TomorrowBadge")
            : date.ToString("ddd, d MMM", localization.CurrentCulture);

    /// <summary>The heading over a day in a project: the date, and for the next two days their names as well.</summary>
    private string StepDayHeading(DateOnly date, DateOnly today)
    {
        var culture = localization.CurrentCulture;
        var full = Capitalize(date.ToString("ddd, d MMM", culture), culture);
        if (date != today && date != today.AddDays(1)) return full;
        var name = Capitalize(localization.Text(date == today ? "TodayBadge" : "TomorrowBadge"), culture);
        return $"{name} · {full}";
    }

    private static string Capitalize(string text, CultureInfo culture)
        => text.Length == 0 ? text : char.ToUpper(text[0], culture) + text[1..];
}
