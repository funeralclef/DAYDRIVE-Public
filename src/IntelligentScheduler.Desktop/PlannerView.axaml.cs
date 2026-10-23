using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using IntelligentScheduler.Desktop.ViewModels;

namespace IntelligentScheduler.Desktop;

public sealed partial class PlannerView : UserControl
{
    private EdgeShellViewModel? _bound;

    public PlannerView()
    {
        InitializeComponent();
        DataContextChanged += OnShellBound;
        // A step's row is built from a template kept among the window's resources, and a handler
        // written on an element of such a template is never wired to this class - the press on a
        // step's name simply did nothing. The events renaming and the step boxes need are caught
        // here instead, as they pass through the window, and told apart by the elements' classes.
        AddHandler(Button.ClickEvent, OnStepTitleClick);
        AddHandler(KeyDownEvent, OnStepRenameKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnStepBoxKeyDown, RoutingStrategies.Tunnel);
        AddHandler(LostFocusEvent, OnStepRenameLostFocus);
    }

    public event EventHandler? HideRequested;
    public event EventHandler? PointerReturned;
    public event EventHandler? PointerDeparted;
    public event EventHandler? UserActivity;

    private void OnShellBound(object? sender, EventArgs e)
    {
        if (_bound is not null)
        {
            _bound.ReviewRowRequested -= OnAgendaRowRequested;
            _bound.AgendaRowRequested -= OnAgendaRowRequested;
            _bound.EventComposerRequested -= OnEventComposerRequested;
            _bound.ProjectComposerFocusRequested -= OnProjectComposerFocusRequested;
            _bound.SittingComposerFocusRequested -= OnSittingComposerFocusRequested;
            _bound.DraftStepFocusRequested -= OnDraftStepFocusRequested;
            _bound.TaskRowRequested -= OnTaskRowRequested;
            _bound.TypingProbe = null;
        }

        _bound = DataContext as EdgeShellViewModel;
        if (_bound is not null)
        {
            _bound.ReviewRowRequested += OnAgendaRowRequested;
            _bound.AgendaRowRequested += OnAgendaRowRequested;
            _bound.EventComposerRequested += OnEventComposerRequested;
            _bound.ProjectComposerFocusRequested += OnProjectComposerFocusRequested;
            _bound.SittingComposerFocusRequested += OnSittingComposerFocusRequested;
            _bound.DraftStepFocusRequested += OnDraftStepFocusRequested;
            _bound.TaskRowRequested += OnTaskRowRequested;
            _bound.TypingProbe = IsTypingStep;
        }
    }

    /// <summary>
    /// Scrolls the row that has just been asked about into view. "How did it go?" is drawn in
    /// the row's own tray, and the control that usually raises it - the stop button in the dock
    /// across the foot of the window - is nowhere near that row. A project step's time chip
    /// asks for the same thing: it opens the day its event is on and points at the event. The
    /// scroll is posted rather than done here because the row is only laid out afterwards.
    /// </summary>
    private void OnAgendaRowRequested(object? sender, Guid eventId)
    {
        if (DataContext is not EdgeShellViewModel shell) return;
        Dispatcher.UIThread.Post(() =>
        {
            var index = -1;
            for (var position = 0; position < shell.Agenda.Count; position++)
                if (shell.Agenda[position].Event?.Id == eventId) { index = position; break; }
            if (index >= 0 && AgendaList.ContainerFromIndex(index) is { } container) container.BringIntoView();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Brings the event composer into view when it was opened from further down the panel: a
    /// step asking for a time, or a sitting carried on, sits well below the head of the schedule.
    /// </summary>
    private void OnEventComposerRequested(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() => EventComposerCard.BringIntoView(), DispatcherPriority.Loaded);

    /// <summary>Brings a task's row into view: its project was opened from a step or a slot elsewhere.</summary>
    private void OnTaskRowRequested(object? sender, Guid taskId)
    {
        if (DataContext is not EdgeShellViewModel shell) return;
        Dispatcher.UIThread.Post(() =>
        {
            var index = -1;
            for (var position = 0; position < shell.Tasks.Count; position++)
                if (shell.Tasks[position].Task.Id == taskId) { index = position; break; }
            if (index >= 0 && TasksList.ContainerFromIndex(index) is { } container) container.BringIntoView();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Puts the caret into a task's step box, so its steps are typed straight after the press that
    /// broke it up - and back into it after each Enter, whatever was rebuilt in between.
    /// </summary>
    private void OnProjectComposerFocusRequested(object? sender, Guid taskId)
        => FocusStepBox(TasksList, "stepComposerInput",
            context => context is TaskProjectViewModel project && project.TaskId == taskId);

    /// <summary>The same for an event's checklist.</summary>
    private void OnSittingComposerFocusRequested(object? sender, Guid eventId)
        => FocusStepBox(AgendaList, "sittingComposerInput",
            context => context is SittingViewModel sitting && sitting.EventId == eventId);

    private static void FocusStepBox(Control list, string boxClass, Func<object?, bool> owns)
        => Dispatcher.UIThread.Post(() =>
        {
            var box = list.GetVisualDescendants().OfType<TextBox>()
                .FirstOrDefault(item => item.Classes.Contains(boxClass) && owns(item.DataContext));
            if (box is null) return;
            box.BringIntoView();
            box.Focus();
        }, DispatcherPriority.Loaded);

    /// <summary>The steps box of a form has just opened: the first step is typed without reaching for it.</summary>
    private void OnDraftStepFocusRequested(object? sender, bool isEvent)
        => Dispatcher.UIThread.Post(() =>
        {
            var box = isEvent ? EventDraftStepBox : TaskDraftStepBox;
            box.BringIntoView();
            box.Focus();
        }, DispatcherPriority.Loaded);

    /// <summary>
    /// Enter adds the line being typed to "what is left". The box does not accept newlines, so
    /// nothing has handled the key by the time it reaches here - a box that took them would eat
    /// it in a class handler long before this.
    /// </summary>
    private void SessionStep_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not EdgeShellViewModel shell) return;
        e.Handled = true;
        shell.AddSessionStepCommand.Execute(null);
    }

    /// <summary>
    /// Enter writes the step and leaves the box empty for the next one - in a task's project, in
    /// an event's checklist and in either form. One line, Enter, the next line.
    /// </summary>
    private void OnStepBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || StepBox(e.Source) is not { } box) return;
        if (box.Classes.Contains("draftStepInput"))
        {
            if (DataContext is not EdgeShellViewModel shell) return;
            e.Handled = true;
            if (Equals(box.Tag, "event")) shell.AddEventDraftStepCommand.Execute(null);
            else shell.AddTaskDraftStepCommand.Execute(null);
            return;
        }

        switch (box.DataContext)
        {
            case TaskProjectViewModel project:
                e.Handled = true;
                project.AddStepCommand.Execute(null);
                break;
            case SittingViewModel sitting:
                e.Handled = true;
                sitting.AddStepCommand.Execute(null);
                break;
        }
    }

    /// <summary>
    /// Whether a step is being typed into a row of the day. The day rebuilds itself every minute,
    /// and doing that underneath the box takes the caret and the half-typed line with it.
    /// </summary>
    private bool IsTypingStep()
        => TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox box && TopLevel.GetTopLevel(box) is not null
           && (box.Classes.Contains("stepComposerInput") || box.Classes.Contains("stepRenameInput"));

    /// <summary>The step box an input event belongs to. Its source can be a part inside the box's own template.</summary>
    private static TextBox? StepBox(object? source)
    {
        for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
            if (visual is TextBox box)
                return box.Classes.Contains("stepComposerInput") || box.Classes.Contains("draftStepInput") ? box : null;
        return null;
    }

    /// <summary>
    /// A step's name is edited where it is read: pressing it turns it into its own box. The name
    /// is a button rather than text with a tap handler, so it can be reached from the keyboard too.
    /// </summary>
    private void OnStepTitleClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Button { DataContext: IRenamableStep row, Parent: Panel panel } button
            || !button.Classes.Contains("stepTitle")) return;
        e.Handled = true;
        row.BeginRename();
        if (panel.Children.OfType<TextBox>().FirstOrDefault() is not { } box) return;
        Dispatcher.UIThread.Post(() =>
        {
            box.Focus();
            box.SelectAll();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Enter keeps the new name, Escape keeps the old one. Caught on the way down, before the box.</summary>
    private void OnStepRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape)
            || RenameBox(e.Source) is not { DataContext: IRenamableStep row }) return;
        e.Handled = true;
        if (e.Key == Key.Enter) _ = row.CommitRenameAsync();
        else row.CancelRename();
    }

    /// <summary>Clicking away keeps what was typed, the way leaving any field does.</summary>
    private void OnStepRenameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (RenameBox(e.Source) is { DataContext: IRenamableStep row }) _ = row.CommitRenameAsync();
    }

    /// <summary>The rename box an input event belongs to. Its source can be a part inside the box's own template.</summary>
    private static TextBox? RenameBox(object? source)
    {
        for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
            if (visual is TextBox box) return box.Classes.Contains("stepRenameInput") ? box : null;
        return null;
    }

    private void Header_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && TopLevel.GetTopLevel(this) is Window host)
            host.BeginMoveDrag(e);
    }

    private void Hide_OnClick(object? sender, RoutedEventArgs e) => HideRequested?.Invoke(this, EventArgs.Empty);

    private async void WeekStrip_OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (DataContext is not EdgeShellViewModel vm || Math.Abs(e.Delta.Y) < 0.01) return;
        e.Handled = true;
        await ChangeWeekAnimatedAsync(e.Delta.Y > 0 ? -1 : 1);
    }

    private async void PreviousWeek_OnClick(object? sender, RoutedEventArgs e) => await ChangeWeekAnimatedAsync(-1);
    private async void NextWeek_OnClick(object? sender, RoutedEventArgs e) => await ChangeWeekAnimatedAsync(1);

    private async Task ChangeWeekAnimatedAsync(int direction)
    {
        if (DataContext is not EdgeShellViewModel vm) return;
        var command = direction < 0 ? vm.PreviousWeekCommand : vm.NextWeekCommand;
        if (!command.CanExecute(null)) return;
        await command.ExecuteAsync(null);
    }

    protected override void OnPointerEntered(PointerEventArgs e) { base.OnPointerEntered(e); PointerReturned?.Invoke(this, EventArgs.Empty); }
    protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); PointerDeparted?.Invoke(this, EventArgs.Empty); }
    protected override void OnPointerMoved(PointerEventArgs e) { base.OnPointerMoved(e); UserActivity?.Invoke(this, EventArgs.Empty); }
    protected override void OnPointerPressed(PointerPressedEventArgs e) { base.OnPointerPressed(e); UserActivity?.Invoke(this, EventArgs.Empty); }
    protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); UserActivity?.Invoke(this, EventArgs.Empty); }
}
