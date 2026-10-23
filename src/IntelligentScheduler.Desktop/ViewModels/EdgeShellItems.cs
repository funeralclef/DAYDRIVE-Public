using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia;
using Avalonia.Media;
using IntelligentScheduler.Application;
using IntelligentScheduler.Desktop.Localization;
using IntelligentScheduler.Domain;
using System.Globalization;

namespace IntelligentScheduler.Desktop.ViewModels;

/// <summary>
/// One palette, ready to be handed to <see cref="EventVisuals"/>: a colour for every event
/// category, indexed by the enum, and the handful of colours that are about state rather than
/// about kind.
/// </summary>
internal readonly record struct VisualAccentPalette(
    IReadOnlyList<Color> Categories,
    Color Focus, Color Success, Color Warning, Color Danger, Color Muted);

/// <summary>
/// The brushes every event on the screen is coloured with.
///
/// They are live brushes rather than values: the whole set is repainted in place when the theme
/// or the palette changes, so a rail already on screen follows without every row being rebuilt.
/// The colours themselves are not chosen here — <see cref="CategoryPalette"/> builds them as an
/// evenly spaced ladder, which is what keeps two categories from arriving at the same colour.
/// </summary>
internal static class EventVisuals
{
    /// <summary>The two tints every accent is also drawn as: a chip behind text, and a filled body.</summary>
    private const byte SoftAlpha = 0x38;
    private const byte BodyAlpha = 0x59;

    private static readonly SolidColorBrush[] Accents = Build(0xFF);
    private static readonly SolidColorBrush[] Softs = Build(SoftAlpha);
    private static readonly SolidColorBrush[] Bodies = Build(BodyAlpha);
    private static readonly SolidColorBrush Focus = new(Color.Parse("#FFB45E"));
    private static readonly SolidColorBrush Success = new(Color.Parse("#55D6A1"));
    private static readonly SolidColorBrush Warning = new(Color.Parse("#F4B860"));
    private static readonly SolidColorBrush Danger = new(Color.Parse("#FF8295"));
    private static readonly SolidColorBrush Muted = new(Color.Parse("#929BAD"));
    private static readonly SolidColorBrush FocusSoft = Tint(Focus.Color, SoftAlpha);
    private static readonly SolidColorBrush SuccessSoft = Tint(Success.Color, SoftAlpha);
    private static readonly SolidColorBrush FocusBody = Tint(Focus.Color, BodyAlpha);
    private static readonly SolidColorBrush SuccessBody = Tint(Success.Color, BodyAlpha);

    /// <summary>
    /// A set of brushes at one weight, grey until a palette is applied over them. The weight is
    /// carried from the start rather than only from the first palette: a surface drawn before
    /// then is a placeholder either way, but a placeholder at the wrong alpha is an opaque slab
    /// where a tint belongs.
    /// </summary>
    private static SolidColorBrush[] Build(byte alpha)
    {
        var brushes = new SolidColorBrush[CategoryPalette.Count];
        for (var index = 0; index < brushes.Length; index++) brushes[index] = Tint(Colors.Gray, alpha);
        return brushes;
    }

    /// <summary>Anything stored before this category existed reads back as the catch-all.</summary>
    private static int Slot(EventCategory category)
    {
        var index = (int)category;
        return index >= 0 && index < Accents.Length ? index : (int)EventCategory.General;
    }

    public static IBrush AccentBrush(EventCategory category) => Accents[Slot(category)];

    public static IBrush AccentSoftBrush(EventCategory category) => Softs[Slot(category)];

    /// <summary>
    /// The filled body a block on the week board is painted with: its accent, at the weight that
    /// keeps the text over it readable.
    ///
    /// Taken from the accent brush rather than from a category, because the board colours a task
    /// block by its state and an event by its kind, and both arrive here as an accent. And it is
    /// a live brush like every other one on this page: a tint worked out from the accent's colour
    /// when the block was built stopped being that accent's colour the moment the palette changed
    /// — and nothing rebuilds the board for a colour, because nothing about a block has changed
    /// except how it is painted. That is why switching palettes used to leave the calendar in the
    /// old one until the application was started again.
    /// </summary>
    public static IBrush AccentBodyBrush(IBrush accent)
    {
        for (var index = 0; index < Accents.Length; index++)
            if (ReferenceEquals(Accents[index], accent)) return Bodies[index];
        if (ReferenceEquals(accent, Focus)) return FocusBody;
        if (ReferenceEquals(accent, Success)) return SuccessBody;
        // Something that is not one of this page's brushes cannot be followed, so it is copied.
        return accent is ISolidColorBrush solid ? Tint(solid.Color, BodyAlpha) : accent;
    }

    public static IBrush FocusBrush => Focus;
    public static IBrush FocusSoftBrush => FocusSoft;
    public static IBrush SuccessBrush => Success;
    public static IBrush SuccessSoftBrush => SuccessSoft;
    public static IBrush WarningBrush => Warning;
    public static IBrush DangerBrush => Danger;
    public static IBrush MutedBrush => Muted;

    // Kept for view-model compatibility; animated glow layers are intentionally gone.
    public static IBrush AccentGlowBrush(EventCategory category) => Brushes.Transparent;

    public static void ApplyPalette(VisualAccentPalette palette)
    {
        for (var index = 0; index < Accents.Length; index++)
            Set(Accents[index], Softs[index], Bodies[index],
                index < palette.Categories.Count ? palette.Categories[index] : Colors.Gray);
        Set(Focus, FocusSoft, FocusBody, palette.Focus);
        Set(Success, SuccessSoft, SuccessBody, palette.Success);
        Warning.Color = palette.Warning;
        Danger.Color = palette.Danger;
        Muted.Color = palette.Muted;
    }

    private static SolidColorBrush Tint(Color color, byte alpha)
        => new(Color.FromArgb(alpha, color.R, color.G, color.B));

    private static void Set(SolidColorBrush brush, SolidColorBrush soft, SolidColorBrush body, Color color)
    {
        brush.Color = color;
        soft.Color = Color.FromArgb(SoftAlpha, color.R, color.G, color.B);
        body.Color = Color.FromArgb(BodyAlpha, color.R, color.G, color.B);
    }

    /// <summary>
    /// The resource key for a category's name. Every category is spelled the same way in the
    /// string table, so this is a formula rather than a list somebody has to remember to extend.
    /// </summary>
    public static string CategoryKey(EventCategory category) =>
        Enum.IsDefined(category) ? $"EventCategory{category}" : "EventCategoryGeneral";

    public static string PriorityKey(EventPriority priority) => priority switch
    {
        EventPriority.High => "EventPriorityHigh",
        EventPriority.Critical => "EventPriorityCritical",
        _ => "EventPriorityNormal"
    };

    public static string RecurrenceKey(EventRecurrence recurrence) => recurrence switch
    {
        EventRecurrence.Daily => "RepeatDaily",
        EventRecurrence.Weekdays => "RepeatWeekdays",
        EventRecurrence.Weekly => "RepeatWeekly",
        EventRecurrence.Monthly => "RepeatMonthly",
        _ => "RepeatNever"
    };
}
/// <summary>
/// A row that can say whether a freshly built copy of itself would look and behave exactly the
/// same. Rebuilding a list from the database is cheap; putting the new objects into the bound
/// collection is not - a replaced item takes its controls down and builds another, and every
/// control built is matched against the whole of the application's styles. Rows that answer
/// "yes" here are left standing instead, which is what makes reopening a panel cost nothing.
/// </summary>
internal interface IRowEquivalence
{
    bool Matches(object other);
}

internal static class ScheduleText
{
    /// <summary>"1 h 30 min" or "45 min", using the localized unit templates.</summary>
    public static string Duration(TimeSpan value, LocalizationService localization)
    {
        var minutes = Math.Max(0, (int)Math.Round(value.TotalMinutes));
        return minutes < 60
            ? localization.Format("DurationMinutes", minutes)
            : minutes % 60 == 0
                ? localization.Format("MetricHours", minutes / 60)
                : localization.Format("DurationHoursMinutes", minutes / 60, minutes % 60);
    }

}

internal static class ClockText
{
    public static string Format(DateTimeOffset value, bool use24HourClock, CultureInfo culture)
        => Format(value.ToLocalTime().TimeOfDay, use24HourClock, culture);

    public static string Format(TimeSpan value, bool use24HourClock, CultureInfo culture)
    {
        if (use24HourClock)
            return $"{value.Hours:00}:{value.Minutes:00}";

        var hour = value.Hours % 12 is 0 ? 12 : value.Hours % 12;
        var designator = value.Hours < 12 ? culture.DateTimeFormat.AMDesignator : culture.DateTimeFormat.PMDesignator;
        if (string.IsNullOrWhiteSpace(designator)) designator = value.Hours < 12 ? "AM" : "PM";
        return $"{hour.ToString(culture)}:{value.Minutes.ToString("00", culture)} {designator}";
    }

    public static string Range(DateTimeOffset start, DateTimeOffset end, bool use24HourClock, CultureInfo culture)
        => $"{Format(start, use24HourClock, culture)} — {Format(end, use24HourClock, culture)}";
}

public sealed record ChoiceOption(string Value, string Label)
{
    public IBrush Accent { get; init; } = Brushes.Transparent;
    public IBrush AccentGlow { get; init; } = Brushes.Transparent;
    public string Glyph { get; init; } = "–";
    public override string ToString() => Label;
}

public sealed partial class LocalModelOptionViewModel(
    LocalModelDescriptor descriptor,
    LocalizationService localization,
    Action<LocalModelOptionViewModel> select) : ObservableObject
{
    public LocalModelDescriptor Descriptor { get; } = descriptor;
    public string Id => Descriptor.Id;
    public string DisplayName => Descriptor.DisplayName;
    public string Tier => Descriptor.ParameterLabel;
    public string Role => localization.Text(Descriptor.RoleKey);
    public bool IsRecommended => Descriptor.IsRecommended;
    public bool IsExperimental => Descriptor.IsExperimental;
    public bool SupportsVision => Descriptor.SupportsVision;

    public string Size => Descriptor.Quant.ApproximateSizeLabel;
    public string QuantTag => Descriptor.Quant.Tag;
    /// <summary>Only says something the parameter label does not already say.</summary>
    public string Badge => IsRecommended
        ? localization.Text("Recommended")
        : IsExperimental ? localization.Text("ExperimentalBadge") : string.Empty;
    public bool HasBadge => Badge.Length > 0;

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isInstalled;
    [ObservableProperty] private bool _isProjectorInstalled;
    [RelayCommand] private void Select() => select(this);
}

public sealed partial class ChatModelQuickOptionViewModel(
    string id,
    string provider,
    string displayName,
    string subtitle,
    string? badge,
    Func<ChatModelQuickOptionViewModel, Task> select) : ObservableObject
{
    public string Id { get; } = id;
    public string Provider { get; } = provider;
    public string DisplayName { get; } = displayName;
    public string Subtitle { get; } = subtitle;
    public string? Badge { get; } = badge;
    public bool HasBadge => !string.IsNullOrWhiteSpace(Badge);
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isInstalled;
    [RelayCommand] private Task SelectAsync() => select(this);
}

public sealed partial class CalendarDayViewModel(
    DateOnly date,
    DateOnly visibleMonth,
    DateOnly selectedDate,
    bool hasItems,
    Func<DateOnly, Task> select) : ObservableObject, IRowEquivalence
{
    public DateOnly Date { get; } = date;
    public string Number => Date.Day.ToString();
    public bool IsInCurrentMonth => Date.Month == visibleMonth.Month && Date.Year == visibleMonth.Year;
    public bool IsOutsideMonth => !IsInCurrentMonth;
    public bool IsSelected => Date == selectedDate;
    public bool IsToday => Date == DateOnly.FromDateTime(DateTime.Today);
    public bool HasItems { get; } = hasItems;
    [RelayCommand] private Task SelectAsync() => select(Date);

    public bool Matches(object other)
        => other is CalendarDayViewModel day
           && day.Date == Date
           && day.IsInCurrentMonth == IsInCurrentMonth
           && day.IsSelected == IsSelected
           && day.HasItems == HasItems;
}

public sealed record SchedulerHourViewModel(string Label, double Top);

public sealed partial class SchedulerDayViewModel(
    DateOnly date,
    DateOnly selectedDate,
    Func<DateOnly, Task> select,
    LocalizationService localization) : ObservableObject
{
    public DateOnly Date { get; } = date;
    public string Day => localization.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName(Date.DayOfWeek).TrimEnd('.');
    public string Number => Date.Day.ToString();
    public bool IsSelected => Date == selectedDate;
    public bool IsToday => Date == DateOnly.FromDateTime(DateTime.Today);
    [RelayCommand] private Task SelectAsync() => select(Date);
}

public sealed partial class SchedulerBlockViewModel(
    string title,
    DateTimeOffset startUtc,
    DateTimeOffset endUtc,
    IBrush accent,
    IBrush accentGlow,
    double left,
    double top,
    double width,
    double height,
    CalendarEvent? calendarEvent,
    TimeBlock? timeBlock,
    /// <summary>
    /// False for one day's slice of an event that runs over midnight. Such a slice does not
    /// start where the event starts, so dragging it would compute the new start from the wrong
    /// anchor; those slices stay put and are edited through the form instead.
    /// </summary>
    bool isWholeItem,
    Func<CalendarEvent, Task>? edit,
    Func<TimeBlock, Task>? manageTaskBlock,
    bool use24HourClock,
    bool animateAccent,
    LocalizationService localization) : ObservableObject, IRowEquivalence
{
    public string Title { get; } = title;
    public string Time => ClockText.Range(startUtc, endUtc, use24HourClock, localization.CurrentCulture);
    public string StartText => ClockText.Format(startUtc, use24HourClock, localization.CurrentCulture);
    public IBrush Accent { get; } = accent;
    /// <summary>Filled body tint; the solid accent stays on the left rail for contrast.</summary>
    public IBrush Surface { get; } = EventVisuals.AccentBodyBrush(accent);
    public IBrush AccentGlow { get; } = accentGlow;
    public bool AnimateAccent { get; } = animateAccent;
    /// <summary>Short blocks cannot fit two lines of text, so the time is dropped instead of clipped.</summary>
    public bool ShowTime => Height >= 40;
    public bool IsPast { get; } = endUtc <= DateTimeOffset.UtcNow;
    public double BodyOpacity => IsPast ? 0.6 : 1;

    public double Left { get; } = left;
    public double Top { get; } = top;
    public double Width { get; } = width;
    public double Height { get; } = height;
    public TimeBlock? TimeBlock { get; } = timeBlock;

    /// <summary>The event this block draws, when it is one. Dragging needs it by reference.</summary>
    public CalendarEvent? Event { get; } = calendarEvent;

    /// <summary>Minutes the block covers, used to keep its length while it is dragged.</summary>
    public int DurationMinutes { get; } = Math.Max(1, (int)Math.Round((endUtc - startUtc).TotalMinutes));

    /// <summary>Only actionable task blocks can be moved or resized on the week board.</summary>
    public bool CanDrag => isWholeItem
                           && TimeBlock is { State: BlockState.Planned or BlockState.InProgress, TaskId: not null };

    public bool IsEditable => Event is not null || TimeBlock is not null;

    /// <summary>
    /// Whether a freshly built block would draw exactly this one. The week is rebuilt from the
    /// database on every reload and on every switch onto the panel, and almost always comes back
    /// identical; without this, "identical" cost a full teardown and rebuild of every button on
    /// the board. Only what the block shows or does is compared - the closures behind it come
    /// from the same shell either way.
    /// </summary>
    public bool Matches(object other)
        => other is SchedulerBlockViewModel block
           && block.Title == Title
           && block.Left == Left && block.Top == Top
           && block.Width == Width && block.Height == Height
           && block.StartText == StartText
           && block.IsPast == IsPast
           && block.CanDrag == CanDrag
           && block.Event?.Id == Event?.Id
           && block.TimeBlock?.Id == TimeBlock?.Id
           && ReferenceEquals(block.Accent, Accent);

    [RelayCommand]
    private Task EditAsync()
        => Event is not null && edit is not null
            ? edit(Event)
            : TimeBlock is not null && manageTaskBlock is not null
                ? manageTaskBlock(TimeBlock)
                : Task.CompletedTask;
}

public sealed partial class DayChipViewModel(DateOnly date, DateOnly selected, Func<DateOnly, Task> select, LocalizationService localization) : ObservableObject
{
    public DateOnly Date { get; } = date;
    public string Day => localization.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName(Date.DayOfWeek).TrimEnd('.');
    public string Number => Date.Day.ToString();
    public bool IsToday => Date == DateOnly.FromDateTime(DateTime.Today);
    public bool IsSelected => Date == selected;
    [RelayCommand] private Task SelectAsync() => select(Date);
}

/// <summary>
/// One row of the unscheduled list. It is kept for as long as its task exists and brought up to
/// date in place, because a task broken into steps carries the box its next step is typed in.
/// </summary>
public sealed partial class CompactTaskViewModel(
    TaskItem task,
    Func<CompactTaskViewModel, Task> toggle,
    Func<CompactTaskViewModel, Task> edit,
    Func<CompactTaskViewModel, Task> delete,
    Func<CompactTaskViewModel, Task> breakIntoSteps,
    LocalizationService localization,
    bool use24HourClock) : ObservableObject
{
    private static readonly string[] Displayed =
    [
        nameof(Title), nameof(PriorityLabel), nameof(PriorityBrush), nameof(DurationText), nameof(HasDeadline),
        nameof(IsOverdue), nameof(DeadlineText), nameof(Meta), nameof(IsDone), nameof(DimOpacity)
    ];

    [ObservableProperty] private bool _isDeletePending;

    /// <summary>
    /// The project the task has been broken into — or is being broken into, while the box for its
    /// first step is open. Null for an ordinary task.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeleteConfirmationText))]
    private TaskProjectViewModel? _project;

    public TaskItem Task { get; private set; } = task;
    public bool Use24HourClock { get; } = use24HourClock;
    public string Title => Task.Title;

    /// <summary>A task whose steps are gone with it says so before it goes.</summary>
    public string DeleteConfirmationText => localization.Text(Project is { HasSteps: true } ? "ConfirmDeleteProjectTask" : "ConfirmDeleteTask");

    /// <summary>Takes the task as it is now, and tells whatever shows it.</summary>
    public void Update(TaskItem fresh)
    {
        Task = fresh;
        foreach (var name in Displayed) OnPropertyChanged(name);
        OnPropertyChanged(nameof(DeleteConfirmationText));
    }

    public string PriorityLabel => localization.Text(Task.Priority switch
    {
        >= 90 => "PriorityUrgent",
        >= 65 => "PriorityHigh",
        < 38 => "PriorityLow",
        _ => "PriorityNormal"
    });

    /// <summary>Priority reads at a glance from colour rather than from a word buried in metadata.</summary>
    public IBrush PriorityBrush => Task.Priority switch
    {
        >= 90 => EventVisuals.DangerBrush,
        >= 65 => EventVisuals.WarningBrush,
        < 38 => EventVisuals.MutedBrush,
        _ => EventVisuals.FocusBrush
    };

    public string DurationText => ScheduleText.Duration(TimeSpan.FromMinutes(Task.RemainingDurationMin), localization);

    public bool HasDeadline => Task.DeadlineUtc is not null;
    public bool IsOverdue => !IsDone && Task.DeadlineUtc is { } deadline && deadline < DateTimeOffset.UtcNow;

    public string DeadlineText
    {
        get
        {
            if (Task.DeadlineUtc is not { } deadline) return string.Empty;
            var local = deadline.ToLocalTime();
            var date = DateOnly.FromDateTime(local.Date);
            var today = DateOnly.FromDateTime(DateTime.Today);
            var day = date == today
                ? localization.Text("TodayBadge")
                : date == today.AddDays(1)
                    ? localization.Text("TomorrowBadge")
                    : local.ToString("d MMM", localization.CurrentCulture);
            // A deadline picked from the calendar is the end of that day, and "23:59" beside it
            // is a time nobody chose. Only a deadline with an hour of its own says the hour.
            if (local.Hour == 23 && local.Minute == 59) return day;
            return $"{day}, {ClockText.Format(local, Use24HourClock, localization.CurrentCulture)}";
        }
    }

    public string Meta
    {
        get
        {
            var parts = new List<string> { DurationText, PriorityLabel };
            if (HasDeadline) parts.Add(DeadlineText);
            return string.Join(" · ", parts);
        }
    }

    public bool IsDone => Task.Status == Domain.TaskStatus.Done;
    public double DimOpacity => IsDone ? 0.48 : 1;
    [RelayCommand] private Task ToggleAsync() => toggle(this);
    [RelayCommand] private Task EditAsync() => edit(this);
    [RelayCommand] private Task BreakIntoStepsAsync() => breakIntoSteps(this);

    [RelayCommand]
    private void RequestDelete()
    {
        OnPropertyChanged(nameof(DeleteConfirmationText));
        IsDeletePending = true;
    }

    [RelayCommand] private void CancelDelete() => IsDeletePending = false;
    [RelayCommand] private async Task ConfirmDeleteAsync()
    {
        IsDeletePending = false;
        await delete(this);
    }
}

/// <summary>What a row can do to the stopwatch it owns, short of recording a verdict.</summary>
public enum RowSessionAction { Pause, Resume, Stop }

public sealed partial class AgendaItemViewModel : ObservableObject
{
    private readonly Func<AgendaItemViewModel, Task>? _complete;
    private readonly Func<AgendaItemViewModel, Task>? _manage;
    private readonly Func<AgendaItemViewModel, Task>? _edit;
    private readonly Func<AgendaItemViewModel, Task>? _copy;
    private readonly Func<AgendaItemViewModel, EventSeriesScope, Task>? _delete;
    [ObservableProperty] private bool _isDeletePending;
    [ObservableProperty] private ChoiceOption? _selectedDeleteScopeOption;
    private readonly List<ChoiceOption> _deleteScopeOptions = [];
    /// <summary>Kept so verdict tooltips written later still use the user's clock.</summary>
    private readonly bool _use24HourClock;

    public AgendaItemViewModel(CalendarEvent item, Func<AgendaItemViewModel, Task> edit, Func<AgendaItemViewModel, Task> copy, Func<AgendaItemViewModel, EventSeriesScope, Task> delete, LocalizationService localization, bool use24HourClock, bool animateAccent)
    {
        Event = item;
        _use24HourClock = use24HourClock;
        _edit = edit;
        _copy = copy;
        _delete = delete;
        StartUtc = item.StartUtc;
        EndUtc = item.EndUtc;
        Title = item.Title;
        StartText = ClockText.Format(item.StartUtc, use24HourClock, localization.CurrentCulture);
        EndText = ClockText.Format(item.EndUtc, use24HourClock, localization.CurrentCulture);
        Time = ClockText.Range(item.StartUtc, item.EndUtc, use24HourClock, localization.CurrentCulture);
        DurationText = ScheduleText.Duration(item.EndUtc - item.StartUtc, localization);
        CategoryLabel = localization.Text(EventVisuals.CategoryKey(item.Category));

        var details = new List<string>();
        if (item.Priority != EventPriority.Normal)
            details.Add(localization.Text(EventVisuals.PriorityKey(item.Priority)));
        if (item.Recurrence != EventRecurrence.None)
            details.Add(localization.Text(EventVisuals.RecurrenceKey(item.Recurrence)));
        Kind = details.Count == 0 ? CategoryLabel : string.Join(" · ", details);
        Accent = EventVisuals.AccentBrush(item.Category);
        AccentSoft = EventVisuals.AccentSoftBrush(item.Category);
        AccentGlow = EventVisuals.AccentGlowBrush(item.Category);
        AnimateAccent = animateAccent;
        PriorityMark = item.Priority switch { EventPriority.High => "!", EventPriority.Critical => "‼", _ => string.Empty };

        var now = DateTimeOffset.UtcNow;
        IsCurrent = item.StartUtc <= now && item.EndUtc > now;
        IsPast = item.EndUtc <= now;
        NowLabel = localization.Text("NowLabel");
        if (IsCurrent)
            RelativeText = localization.Format("EndsIn", ScheduleText.Duration(item.EndUtc - now, localization));
        else if (item.StartUtc > now && (item.StartUtc - now).TotalHours <= 12)
            RelativeText = localization.Format("StartsIn", ScheduleText.Duration(item.StartUtc - now, localization));
        _deleteScopeOptions.Add(new(nameof(EventSeriesScope.Occurrence), localization.Text("ScopeOccurrence")));
        if (item.SeriesId.HasValue)
        {
            _deleteScopeOptions.Add(new(nameof(EventSeriesScope.ThisAndFollowing), localization.Text("ScopeFollowing")));
            _deleteScopeOptions.Add(new(nameof(EventSeriesScope.Series), localization.Text("ScopeSeries")));
        }
        SelectedDeleteScopeOption = _deleteScopeOptions[0];
    }

    public AgendaItemViewModel(TimeBlock block, Func<AgendaItemViewModel, Task> complete,
        Func<AgendaItemViewModel, Task> manage, LocalizationService localization, bool use24HourClock, bool animateAccent)
    {
        Block = block;
        _use24HourClock = use24HourClock;
        _complete = complete;
        _manage = manage;
        StartUtc = block.StartUtc;
        EndUtc = block.EndUtc;
        Title = block.Task?.Title ?? block.TitleOverride ?? localization.Text("Focus");
        StartText = ClockText.Format(block.StartUtc, use24HourClock, localization.CurrentCulture);
        EndText = ClockText.Format(block.EndUtc, use24HourClock, localization.CurrentCulture);
        Time = ClockText.Range(block.StartUtc, block.EndUtc, use24HourClock, localization.CurrentCulture);
        DurationText = ScheduleText.Duration(block.EndUtc - block.StartUtc, localization);
        Kind = localization.Text(block.State == BlockState.Completed ? "DoneKind" : "TaskKind");
        CategoryLabel = Kind;
        Accent = block.State == BlockState.Completed ? EventVisuals.SuccessBrush : EventVisuals.FocusBrush;
        AccentSoft = block.State == BlockState.Completed ? EventVisuals.SuccessSoftBrush : EventVisuals.FocusSoftBrush;
        AccentGlow = Accent;
        AnimateAccent = animateAccent;
        NowLabel = localization.Text("NowLabel");

        var now = DateTimeOffset.UtcNow;
        IsCurrent = block.StartUtc <= now && block.EndUtc > now;
        IsPast = block.EndUtc <= now;
    }

    public CalendarEvent? Event { get; }
    public TimeBlock? Block { get; }
    public DateTimeOffset StartUtc { get; }
    public DateTimeOffset EndUtc { get; }
    public string Title { get; }
    public string Time { get; }
    public string StartText { get; } = string.Empty;
    public string EndText { get; } = string.Empty;
    public string DurationText { get; } = string.Empty;
    public string CategoryLabel { get; } = string.Empty;
    public string Kind { get; }
    public IBrush Accent { get; }
    public IBrush AccentSoft { get; } = Brushes.Transparent;
    public IBrush AccentGlow { get; }
    public bool AnimateAccent { get; }
    /// <summary>True while the clock is inside this item, which the card highlights.</summary>
    public bool IsCurrent { get; }
    public bool IsPast { get; }
    public string NowLabel { get; } = string.Empty;
    public string RelativeText { get; } = string.Empty;
    public bool HasRelative => RelativeText.Length > 0;
    public string PriorityMark { get; } = string.Empty;
    public bool HasPriorityMark => PriorityMark.Length > 0;

    /// <summary>
    /// The project this slot was booked for, when it was booked for a project's step. It stands
    /// in the category's place: "Launch the website" says more about the hour than "Work" does.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProjectLabel))]
    [NotifyPropertyChangedFor(nameof(ShowCategoryChip))]
    private string _projectLabel = string.Empty;

    public bool HasProjectLabel => ProjectLabel.Length > 0;
    public bool ShowCategoryChip => IsEvent && !HasProjectLabel;

    /// <summary>The task whose project this slot was booked for, which its label opens.</summary>
    public Guid? ProjectTaskId { get; set; }

    // ---- The steps the hour is spent on ----------------------------------------------------
    // An event broken into steps carries its checklist under the row. The list itself is the
    // shell's, kept by occurrence, because this row is rebuilt every minute and the list is not.

    private Func<AgendaItemViewModel, Task>? _breakIntoSteps;
    private Action<Guid>? _revealProject;

    /// <summary>This occurrence's checklist, or null for an event that has none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeleteConfirmationText))]
    private SittingViewModel? _sitting;

    /// <summary>The row menu offers to break the event into steps: it has none, and is not a step's slot itself.</summary>
    [ObservableProperty] private bool _canBreakIntoSteps;

    /// <summary>A one-off event with steps says they go with it; a series keeps its list for its other days.</summary>
    public string DeleteConfirmationText => _localization?.Text(Sitting is { HasSteps: true, IsSeries: false }
        ? "ConfirmDeleteEventSteps" : "ConfirmDeleteEvent") ?? string.Empty;

    public void AttachSteps(Func<AgendaItemViewModel, Task> breakIntoSteps, Action<Guid> revealProject)
    {
        _breakIntoSteps = breakIntoSteps;
        _revealProject = revealProject;
    }

    [RelayCommand] private Task BreakIntoStepsAsync() => _breakIntoSteps?.Invoke(this) ?? Task.CompletedTask;

    /// <summary>The project label on a step's slot: opens the task the step belongs to.</summary>
    [RelayCommand]
    private void OpenProject()
    {
        if (ProjectTaskId is { } taskId) _revealProject?.Invoke(taskId);
    }

    // ---- Picking rows out of the day ---------------------------------------------------
    // Moving one event, or ten, is the same gesture: tick what should move, say where. Only
    // events take part — a focus block already has its own editor and its own drag.

    private Action? _selectionChanged;
    private Func<AgendaItemViewModel, Task>? _moveAlone;
    private Action<AgendaItemViewModel>? _beginSelection;

    /// <summary>True while the day list is showing tick boxes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelectable))]
    private bool _isSelectionMode;

    [ObservableProperty] private bool _isSelected;

    /// <summary>
    /// True for a few seconds after a reminder about this event arrived while the shell was
    /// open. The card lights up softly instead of a notification standing over the shell that
    /// is already naming the same event.
    /// </summary>
    [ObservableProperty] private bool _isFlashing;

    /// <summary>Focus blocks cannot be part of a selection, so they never grow a tick box.</summary>
    public bool IsSelectable => IsSelectionMode && IsEvent;

    partial void OnIsSelectedChanged(bool value) => _selectionChanged?.Invoke();

    /// <summary>Lets the shell recount without subscribing to every row's property changes.</summary>
    public void AttachSelection(Action selectionChanged, Func<AgendaItemViewModel, Task> moveAlone,
        Action<AgendaItemViewModel> beginSelection)
    {
        _selectionChanged = selectionChanged;
        _moveAlone = moveAlone;
        _beginSelection = beginSelection;
    }

    /// <summary>"Move…" from the row menu: the shortest path from one event to the mover.</summary>
    [RelayCommand] private Task MoveAloneAsync() => _moveAlone?.Invoke(this) ?? Task.CompletedTask;

    /// <summary>
    /// "Select" from the row's own menu. Ticking used to begin with a button in the day's
    /// heading, which asked the user to turn on a mode and only then say what it was for; every
    /// selection in fact starts at one particular event. Asked for here, the ticks appear and
    /// this row is already one of them.
    /// </summary>
    [RelayCommand] private void BeginSelection() => _beginSelection?.Invoke(this);

    /// <summary>Sets the tick without telling the shell, for a rebuild that already knows.</summary>
    public void RestoreSelection(bool selected)
    {
        var callback = _selectionChanged;
        _selectionChanged = null;
        IsSelected = selected;
        _selectionChanged = callback;
    }

    [RelayCommand]
    private void ToggleSelected()
    {
        if (!IsEvent) return;
        IsSelected = !IsSelected;
    }

    // ---- Recording what happened -----------------------------------------------------
    // Every row owns its own controls. Restricting this to the "now / next" card is what made
    // a finished event from three hours ago, or anything on a past day, impossible to mark.

    private Func<AgendaItemViewModel, EventOutcomeKind, Task>? _record;
    private Func<AgendaItemViewModel, Task>? _recordPartial;
    private Func<AgendaItemViewModel, Task>? _clearOutcome;
    private Func<AgendaItemViewModel, RowSessionAction, Task>? _session;
    private Action<AgendaItemViewModel>? _dismissReview;
    private Action<AgendaItemViewModel>? _toggleNextStep;
    private Action<AgendaItemViewModel>? _collapseNextStep;
    private LocalizationService? _localization;

    [ObservableProperty] private string _outcomeText = string.Empty;
    [ObservableProperty] private bool _isOutcomeSkipped;
    [ObservableProperty] private bool _isOutcomeDone;
    [ObservableProperty] private bool _isOutcomePartial;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartTracking))]
    [NotifyPropertyChangedFor(nameof(IsRowSessionOpen))]
    [NotifyPropertyChangedFor(nameof(CanPauseRowSession))]
    [NotifyPropertyChangedFor(nameof(CanResumeRowSession))]
    private bool _isOutcomeStarted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartTracking))]
    [NotifyPropertyChangedFor(nameof(IsRowSessionOpen))]
    [NotifyPropertyChangedFor(nameof(CanPauseRowSession))]
    [NotifyPropertyChangedFor(nameof(CanResumeRowSession))]
    private bool _isOutcomeFinished;

    /// <summary>The event is over and carries no verdict at all.</summary>
    [ObservableProperty] private bool _isOutcomeMissing;

    /// <summary>A session is open on this row and standing still.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPauseRowSession))]
    [NotifyPropertyChangedFor(nameof(CanResumeRowSession))]
    private bool _isOutcomePaused;

    /// <summary>
    /// Where to pick this event up, written the last time it was left part-done. It is shown on
    /// the row rather than kept for the card, because the row is what a person reads when they
    /// come back to a day - and "recheck the last three screens" is the whole reason returning
    /// to half-finished work is cheap rather than expensive.
    /// </summary>
    [ObservableProperty] private string _nextStepText = string.Empty;

    public bool HasNextStep => NextStepText.Length > 0;

    private static readonly char[] NextStepSeparators = ['\n', '\r', ';'];

    /// <summary>What is left, as the list it actually is.</summary>
    public IReadOnlyList<string> NextStepItems { get; private set; } = [];

    public int NextStepCount => NextStepItems.Count;

    /// <summary>
    /// Whether the card's heading carries a count. A single line needs none - the line itself
    /// is right there - so the badge appears when there is more than one, or when the list has
    /// been rolled up and the count is the only thing left saying how much is under the heading.
    /// </summary>
    public bool ShowNextStepCount => NextStepItems.Count > 1 || IsNextStepCollapsed;

    /// <summary>The card is on screen: there is something to show, and it has not been put away.</summary>
    public bool ShowNextStepCard => HasNextStep && !IsNextStepFolded;

    /// <summary>
    /// The lines themselves are on screen. Rolling them up is the smaller of the two ways to
    /// quieten this card: the heading stays, and with it the count and the way back - which is
    /// the difference between putting a note aside for a minute and taking it off the day.
    /// </summary>
    public bool ShowNextStepList => !IsNextStepCollapsed;

    /// <summary>
    /// The card has been put away and something has to say so. A whole card under every
    /// half-finished event is what makes a day list heavy, so hiding it hides all of it - and
    /// what is left behind is one chip among the row's own chips, which costs the list nothing
    /// and is still the way back.
    /// </summary>
    public bool IsNextStepHidden => HasNextStep && IsNextStepFolded;

    /// <summary>
    /// What the button beside the pin offers, with the count the card would have shown. The
    /// button is the size of the pin next to it, which leaves no room for a number on its face.
    /// </summary>
    public string NextStepPeekHint => _localization is not { } text
        ? string.Empty
        : $"{text.Text("NextStepUnfold")} · {NextStepCount}";

    /// <summary>
    /// The card folded away to its own heading. It is a note about work still to do, which is
    /// worth keeping and not worth reading twenty times, so it can be put away without being
    /// thrown away - and it stays away, because the shell remembers which rows are folded.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNextStepCard))]
    [NotifyPropertyChangedFor(nameof(IsNextStepHidden))]
    [NotifyPropertyChangedFor(nameof(NextStepPeekHint))]
    private bool _isNextStepFolded;

    /// <summary>
    /// The card rolled up to its own heading. This is the lighter of the two: closing the card
    /// takes it off the day and leaves a chip on the row as the way back, and rolling it up
    /// leaves the heading standing where it is with the count on it. Both are kept, because
    /// they answer different questions - "not now" and "not any more" - and the card is a note
    /// about work still to do either way. Like the fold, it is remembered by the shell: the day
    /// list is rebuilt from scratch every minute.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNextStepList))]
    [NotifyPropertyChangedFor(nameof(ShowNextStepCount))]
    [NotifyPropertyChangedFor(nameof(NextStepCollapseHint))]
    private bool _isNextStepCollapsed;

    /// <summary>What the chevron on the card's heading offers, which way round it is pointing.</summary>
    public string NextStepCollapseHint => _localization is not { } text
        ? string.Empty
        : text.Text(IsNextStepCollapsed ? "NextStepExpand" : "NextStepCollapse");

    /// <summary>
    /// A stored note read back as the list it was written as. One line is still the usual
    /// answer; anything written as several is kept as several rather than run together into a
    /// sentence nobody meant to write.
    /// </summary>
    public static IReadOnlyList<string> SplitNextStep(string? stored)
        => stored is null or "" ? [] : stored.Split(NextStepSeparators,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    partial void OnNextStepTextChanged(string value)
    {
        // A list, not an array: reflection bindings read a public Count, and an array's is an
        // explicit interface implementation they cannot see.
        NextStepItems = SplitNextStep(value);
        OnPropertyChanged(nameof(NextStepItems));
        OnPropertyChanged(nameof(NextStepCount));
        OnPropertyChanged(nameof(ShowNextStepCount));
        OnPropertyChanged(nameof(ShowNextStepCard));
        OnPropertyChanged(nameof(IsNextStepHidden));
        OnPropertyChanged(nameof(NextStepPeekHint));
        OnPropertyChanged(nameof(HasNextStep));
        OnPropertyChanged(nameof(ShowNextStepList));
    }

    /// <summary>Whether the inline verdict buttons are showing for this row.</summary>
    [ObservableProperty] private bool _isTrackingOpen;

    /// <summary>
    /// Whether this row's tray is showing the second step of the question - how far the work
    /// got, and what is left of it. The shell owns the answer being written, because the same
    /// question can be raised from the widget and from the dock; the row owns where it is drawn.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVerdictStep))]
    private bool _isReviewDetailOpen;

    /// <summary>The tray's ordinary face: three answers and the session controls.</summary>
    public bool IsVerdictStep => !IsReviewDetailOpen;

    [ObservableProperty] private bool _isTrackingBusy;

    public bool HasOutcome => OutcomeText.Length > 0;

    /// <summary>
    /// Anything already under way can be recorded. Fifteen minutes of leeway covers pressing
    /// "start" just before the hour; a purely future event has nothing to report yet.
    /// </summary>
    public bool CanTrack => IsEvent
                           && (StartUtc <= DateTimeOffset.UtcNow.AddMinutes(15) || IsRowSessionOpen);

    public bool CanStartTracking => !IsOutcomeStarted && !IsOutcomeFinished;

    // A session started from a row used to be strandable: the pause and stop controls lived on
    // the day card, which points at the current or next event, so a stopwatch set going against
    // something earlier in the day could only be ended by marking it. The row now carries the
    // whole cycle, the way it already carried the whole verdict.
    public bool IsRowSessionOpen => IsOutcomeStarted && !IsOutcomeFinished;
    public bool CanPauseRowSession => IsRowSessionOpen && !IsOutcomePaused;
    public bool CanResumeRowSession => IsRowSessionOpen && IsOutcomePaused;

    public string OutcomeTooltip { get; private set; } = string.Empty;
    public bool HasOutcomeTooltip => OutcomeTooltip.Length > 0;

    partial void OnOutcomeTextChanged(string value) => OnPropertyChanged(nameof(HasOutcome));

    /// <summary>Wires the row to the shell so its buttons write through the same path as the card.</summary>
    public void AttachTracking(
        Func<AgendaItemViewModel, EventOutcomeKind, Task> record,
        Func<AgendaItemViewModel, Task> recordPartial,
        Func<AgendaItemViewModel, Task> clearOutcome,
        Func<AgendaItemViewModel, RowSessionAction, Task> session,
        Action<AgendaItemViewModel> dismissReview,
        Action<AgendaItemViewModel> toggleNextStep,
        Action<AgendaItemViewModel> collapseNextStep,
        LocalizationService localization)
    {
        _record = record;
        _recordPartial = recordPartial;
        _clearOutcome = clearOutcome;
        _session = session;
        _dismissReview = dismissReview;
        _toggleNextStep = toggleNextStep;
        _collapseNextStep = collapseNextStep;
        _localization = localization;
        OnPropertyChanged(nameof(CanTrack));
        OnPropertyChanged(nameof(NextStepCollapseHint));
    }

    /// <summary>Shows what was recorded for this event, or clears the chip when nothing was.</summary>
    public void ApplyOutcome(EventOutcome? outcome, EventFollowUp? followUp, LocalizationService localization)
    {
        _localization ??= localization;
        IsOutcomeSkipped = outcome?.Kind == EventOutcomeKind.Skipped;
        IsOutcomeDone = outcome?.Kind == EventOutcomeKind.Completed;
        IsOutcomePartial = outcome?.Kind == EventOutcomeKind.Partial;
        IsOutcomeStarted = outcome is { StartedAtUtc: not null };
        IsOutcomeFinished = outcome is { } item && item.IsFinished;
        IsOutcomePaused = outcome is { IsPaused: true };
        NextStepText = followUp?.NextStep ?? string.Empty;
        // A running session is one of the two things that make this row markable.
        OnPropertyChanged(nameof(CanTrack));
        // A row that has only been started is not a row that has been answered for, so the chip
        // says so rather than repeating the name of the button that was pressed.
        OutcomeText = outcome switch
        {
            null => string.Empty,
            { Kind: EventOutcomeKind.Completed } => localization.Text("TrackDone"),
            { Kind: EventOutcomeKind.Partial } => localization.Text("TrackPartial"),
            { Kind: EventOutcomeKind.Skipped } => localization.Text("TrackSkip"),
            { IsPaused: true } => localization.Text("TrackPaused"),
            _ => localization.Text("TrackRunning")
        };
        OutcomeTooltip = outcome switch
        {
            null => string.Empty,
            { Kind: EventOutcomeKind.Partial, ProgressPercent: > 0 } scored =>
                localization.Format("TrackedPartialProgress", scored.ProgressPercent,
                    ScheduleText.Duration(TimeSpan.FromMinutes(scored.ActualMinutes), localization)),
            { Kind: EventOutcomeKind.Completed or EventOutcomeKind.Partial } finished =>
                localization.Format(finished.Kind == EventOutcomeKind.Completed ? "TrackedDone" : "TrackedPartial",
                    ScheduleText.Duration(TimeSpan.FromMinutes(finished.ActualMinutes), localization)),
            { Kind: EventOutcomeKind.Skipped } => localization.Text("TrackedSkipped"),
            // A held session says so, with the work it has banked so far. It used to read
            // "started at 14:05" whether or not the clock had moved since.
            { IsPaused: true } held => localization.Format("TrackedPausedAt",
                ScheduleText.Duration(held.WorkedAt(DateTimeOffset.UtcNow), localization)),
            { StartedAtUtc: { } started } => localization.Format("TrackedStarted",
                ClockText.Format(started, _use24HourClock, localization.CurrentCulture)),
            _ => localization.Text("TrackRunning")
        };
        // Finished, and nobody said how it went. The row itself points that out now; before,
        // an event could slide past with nothing recorded and nothing to show for it.
        IsOutcomeMissing = IsEvent && !IsOutcomeFinished && EndUtc <= DateTimeOffset.UtcNow;
        OnPropertyChanged(nameof(OutcomeTooltip));
        OnPropertyChanged(nameof(HasOutcomeTooltip));
        OnPropertyChanged(nameof(NextStepPeekHint));
        OnPropertyChanged(nameof(NextStepCollapseHint));
    }

    [RelayCommand]
    private void ToggleTracking()
    {
        if (!CanTrack) return;
        IsTrackingOpen = !IsTrackingOpen;
    }

    /// <summary>
    /// Puts the tray away, and with it any question that was being asked inside it. The two go
    /// together: a sheet left open against a closed tray is a question nobody can see and the
    /// widget still thinks is standing.
    /// </summary>
    [RelayCommand]
    private void CloseTracking()
    {
        IsTrackingOpen = false;
        IsReviewDetailOpen = false;
        _dismissReview?.Invoke(this);
    }

    [RelayCommand] private void ToggleNextStep() => _toggleNextStep?.Invoke(this);
    [RelayCommand] private void ToggleNextStepCollapsed() => _collapseNextStep?.Invoke(this);

    [RelayCommand] private Task TrackPauseAsync() => RunSessionAsync(RowSessionAction.Pause);
    [RelayCommand] private Task TrackResumeAsync() => RunSessionAsync(RowSessionAction.Resume);

    /// <summary>
    /// Ends the session and raises the one question - here, in this tray, which is where the
    /// question was always about to be answered. It used to close the tray and put the sheet on
    /// the day card instead, which meant stopping a stopwatch moved the conversation to the
    /// other end of the panel and left the same three answers drawn twice.
    /// </summary>
    [RelayCommand]
    private Task TrackStopAsync() => RunSessionAsync(RowSessionAction.Stop);

    private async Task RunSessionAsync(RowSessionAction action)
    {
        if (_session is null || IsTrackingBusy) return;
        IsTrackingBusy = true;
        try { await _session(this, action); }
        finally { IsTrackingBusy = false; }
    }

    [RelayCommand] private Task TrackStartAsync() => RecordAsync(EventOutcomeKind.Started);
    [RelayCommand] private Task TrackDoneAsync() => RecordAsync(EventOutcomeKind.Completed);
    [RelayCommand] private Task TrackSkipAsync() => RecordAsync(EventOutcomeKind.Skipped);

    /// <summary>"Partly" - the one answer with a second step, asked in this same tray.</summary>
    [RelayCommand]
    private Task TrackPartialAsync() => _recordPartial?.Invoke(this) ?? Task.CompletedTask;

    [RelayCommand]
    private async Task TrackUndoAsync()
    {
        if (_clearOutcome is null || IsTrackingBusy) return;
        IsTrackingBusy = true;
        try { await _clearOutcome(this); }
        finally { IsTrackingBusy = false; }
    }

    private async Task RecordAsync(EventOutcomeKind kind)
    {
        if (_record is null || IsTrackingBusy) return;
        IsTrackingBusy = true;
        try { await _record(this, kind); }
        finally { IsTrackingBusy = false; }
        // Starting is the one verdict a user follows up on, so the tray stays open for it.
        if (kind != EventOutcomeKind.Started) IsTrackingOpen = false;
    }
    public bool CanComplete => Block is { State: BlockState.Planned or BlockState.InProgress };
    public bool CanManage => Block is not null;
    public bool IsEvent => Event is not null;
    public bool IsRecurring => Event?.SeriesId is not null;
    public IReadOnlyList<ChoiceOption> DeleteScopeOptions => _deleteScopeOptions;
    [RelayCommand] private Task CompleteAsync() => _complete?.Invoke(this) ?? Task.CompletedTask;
    [RelayCommand] private Task ManageAsync() => _manage?.Invoke(this) ?? Task.CompletedTask;
    [RelayCommand] private Task EditAsync() => _edit?.Invoke(this) ?? Task.CompletedTask;
    [RelayCommand] private Task CopyAsync() => _copy?.Invoke(this) ?? Task.CompletedTask;
    [RelayCommand]
    private void RequestDelete()
    {
        OnPropertyChanged(nameof(DeleteConfirmationText));
        IsDeletePending = true;
    }

    [RelayCommand] private void CancelDelete() => IsDeletePending = false;
    [RelayCommand] private async Task ConfirmDeleteAsync()
    {
        IsDeletePending = false;
        if (_delete is not null)
        {
            var scope = Enum.TryParse<EventSeriesScope>(SelectedDeleteScopeOption?.Value, out var parsed) ? parsed : EventSeriesScope.Occurrence;
            await _delete(this, scope);
        }
    }
}


/// <summary>
/// One line of "what is left", carrying the button that takes it off the list. The line owns
/// its own removal so the template that draws it needs nothing from above it: a list item that
/// has to reach past two ItemsControls for a command is a list item that quietly stops working
/// the first time the markup around it moves.
/// </summary>
public sealed partial class SessionStepViewModel(string text, Action<SessionStepViewModel> remove)
    : ObservableObject
{
    public string Text { get; } = text;

    [RelayCommand] private void Remove() => remove(this);
}

public sealed partial class AssistantActionViewModel(
    AssistantScheduleAction action,
    Func<AssistantActionViewModel, Task> apply,
    LocalizationService localization) : ObservableObject
{
    public AssistantScheduleAction Action { get; } = action;
    public string Title => localization.Text($"AiAction{Action.Kind}");
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Action.Title)) parts.Add(Action.Title!);
            if (Action.StartLocal is { } start)
            {
                var end = Action.EndLocal;
                if (end is null)
                {
                    parts.Add(start.ToString("g", localization.CurrentCulture));
                }
                else if (DateOnly.FromDateTime(start.LocalDateTime) == DateOnly.FromDateTime(end.Value.LocalDateTime))
                {
                    parts.Add($"{start.ToString("d", localization.CurrentCulture)} · {start.ToString("t", localization.CurrentCulture)}–{end.Value.ToString("t", localization.CurrentCulture)}");
                }
                else
                {
                    parts.Add($"{start.ToString("g", localization.CurrentCulture)} → {end.Value.ToString("g", localization.CurrentCulture)}");
                }
            }
            if (Action.SourceDate is { } source)
                parts.Add(Action.TargetDate is { } target
                    ? $"{source.ToString("d", localization.CurrentCulture)} → {target.ToString("d", localization.CurrentCulture)}"
                    : source.ToString("d", localization.CurrentCulture));
            else if (Action.TargetDate is { } target)
                parts.Add(target.ToString("d", localization.CurrentCulture));
            if (Action.OffsetMinutes is { } offset)
                parts.Add($"{(offset >= 0 ? "+" : "−")}{TimeSpan.FromMinutes(Math.Abs(offset)):hh\\:mm}");
            if (Action.DurationMinutes is { } duration) parts.Add($"{duration} {localization.Text("MinutesShort")}");
            // A status change carries no title and no times, so without this the card said
            // nothing at all about what it was going to do.
            if (Action.TaskStatus is { } status) parts.Add(localization.Text($"TaskStatus{status}"));
            return parts.Count == 0 ? Action.Reason ?? localization.Text("AiActionReady") : string.Join(" · ", parts);
        }
    }
    public string Reason => Action.Reason ?? string.Empty;
    public bool HasReason => !string.IsNullOrWhiteSpace(Reason);
    public bool IsCritical => Action.IsCritical;
    [ObservableProperty] private bool _isApplied;
    [ObservableProperty] private bool _isDismissed;
    [ObservableProperty] private bool _isFailed;
    [ObservableProperty] private bool _isRunning;
    public bool IsPending => !IsApplied && !IsDismissed && !IsFailed;
    public string StateText => localization.Text(IsApplied ? "AiActionApplied" : IsFailed ? "AiActionFailed" : "AiActionDismissed");

    partial void OnIsAppliedChanged(bool value) { OnPropertyChanged(nameof(IsPending)); OnPropertyChanged(nameof(StateText)); ConfirmCommand.NotifyCanExecuteChanged(); }
    partial void OnIsDismissedChanged(bool value) { OnPropertyChanged(nameof(IsPending)); OnPropertyChanged(nameof(StateText)); ConfirmCommand.NotifyCanExecuteChanged(); }
    partial void OnIsFailedChanged(bool value) { OnPropertyChanged(nameof(IsPending)); OnPropertyChanged(nameof(StateText)); ConfirmCommand.NotifyCanExecuteChanged(); }
    partial void OnIsRunningChanged(bool value) => ConfirmCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private Task ConfirmAsync() => apply(this);

    [RelayCommand]
    private void Dismiss() => IsDismissed = true;

    private bool CanConfirm() => IsPending && !IsRunning;
    public void MarkApplied() => IsApplied = true;
    public void MarkFailed() => IsFailed = true;
}
public sealed partial class AssistantSuggestionViewModel(
    AssistantTaskSuggestion suggestion,
    Func<AssistantSuggestionViewModel, Task> add,
    LocalizationService localization) : ObservableObject
{
    public AssistantTaskSuggestion Suggestion { get; } = suggestion;
    public string Title => Suggestion.Title;
    public string Meta => $"{Suggestion.DurationMinutes} {localization.Text("MinutesShort")} · {localization.Text("Priority")} {Suggestion.Priority}";
    public string Reason => Suggestion.Reason ?? localization.Text("SuggestedByAssistant");
    [ObservableProperty] private bool _isAdded;
    public string ActionText => localization.Text(IsAdded ? "Added" : "Add");
    partial void OnIsAddedChanged(bool value) => OnPropertyChanged(nameof(ActionText));
    [RelayCommand(CanExecute = nameof(CanAdd))] private Task AddAsync() => add(this);
    private bool CanAdd() => !IsAdded;
    public void MarkAdded() { IsAdded = true; AddCommand.NotifyCanExecuteChanged(); }
}
