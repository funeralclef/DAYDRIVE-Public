using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IntelligentScheduler.Desktop.Localization;
using IntelligentScheduler.Domain;

namespace IntelligentScheduler.Desktop.ViewModels;

/// <summary>The three answers the wrap-up accepts for anything the day left unanswered.</summary>
public enum WrapUpAction { Done, Move, Skip }

/// <summary>
/// One unanswered event on the wrap-up screen.
///
/// The row exists because asking about events one at a time, as each one ends, is the fastest
/// way to make a person stop answering at all. Everything the day left open is collected and
/// put on one screen, each line carrying what the application already worked out for itself:
/// whether a timer ran against it, how long for, and which of the three answers that implies.
/// A line where the stopwatch ran is proposed as done; one nobody ever started is proposed for
/// another day. The user is confirming a reading, not filling in a form.
///
/// A line can also grow a question, but only once. The second time an event is pushed onto a
/// later day, moving it stops being an answer and starts being a symptom, so the row asks what
/// is in the way — and then offers the one thing that would actually help with the answer
/// given, rather than a menu.
/// </summary>
public sealed partial class WrapUpItemViewModel : ObservableObject
{
    private readonly LocalizationService _localization;
    private readonly bool _use24HourClock;

    /// <summary>Where "first thing" is, for an event that keeps losing to the rest of the day.</summary>
    private static readonly TimeSpan Morning = TimeSpan.FromHours(9);
    private readonly Func<WrapUpItemViewModel, WrapUpAction, Task> _resolve;
    private readonly Func<WrapUpItemViewModel, EventBlocker, Task> _answerBlocker;
    private readonly Func<WrapUpItemViewModel, Task> _shorten;
    private readonly Func<WrapUpItemViewModel, Task> _moveEarlier;
    private readonly Func<WrapUpItemViewModel, Task> _drop;
    private readonly Func<WrapUpItemViewModel, string, Task> _saveFirstStep;

    public WrapUpItemViewModel(
        CalendarEvent source,
        EventOutcome? outcome,
        EventFollowUp? followUp,
        DateOnly today,
        bool use24HourClock,
        LocalizationService localization,
        Func<WrapUpItemViewModel, WrapUpAction, Task> resolve,
        Func<WrapUpItemViewModel, EventBlocker, Task> answerBlocker,
        Func<WrapUpItemViewModel, Task> shorten,
        Func<WrapUpItemViewModel, Task> moveEarlier,
        Func<WrapUpItemViewModel, Task> drop,
        Func<WrapUpItemViewModel, string, Task> saveFirstStep)
    {
        Event = source;
        _localization = localization;
        _resolve = resolve;
        _answerBlocker = answerBlocker;
        _shorten = shorten;
        _moveEarlier = moveEarlier;
        _drop = drop;
        _saveFirstStep = saveFirstStep;

        Title = source.Title;
        _use24HourClock = use24HourClock;
        TimeText = ClockText.Range(source.StartUtc, source.EndUtc, use24HourClock, localization.CurrentCulture);

        // Which day this is from, and only when that is not today. A list that reaches back
        // three weeks has to say where each line came from, or a row is a time with no date
        // attached to it and the reader has no way to tell last Tuesday from this morning; and
        // a date printed against every row of a day the drawer is called after would be three
        // weeks of noise for the sake of a fortnight of history.
        var day = DateOnly.FromDateTime(source.StartUtc.ToLocalTime().DateTime);
        IsFromEarlierDay = day < today;
        DayText = IsFromEarlierDay
            ? source.StartUtc.ToLocalTime().ToString("ddd, d MMM", localization.CurrentCulture)
            : string.Empty;
        DaysWaiting = IsFromEarlierDay ? today.DayNumber - day.DayNumber : 0;
        Accent = EventVisuals.AccentBrush(source.Category);
        PlannedMinutes = Math.Max(1, (int)Math.Round((source.EndUtc - source.StartUtc).TotalMinutes));
        DeferCount = followUp?.DeferCount ?? 0;
        NextStepText = followUp?.NextStep ?? string.Empty;

        // The only thing that separates "you worked on this and forgot to say so" from "this
        // never happened" is whether a stopwatch ever ran against it. That is the whole of the
        // guesswork, and it is the reason the row can propose an answer at all.
        var worked = outcome?.WorkedAt(DateTimeOffset.UtcNow) ?? TimeSpan.Zero;
        HadActivity = outcome?.StartedAtUtc is not null && worked >= TimeSpan.FromMinutes(1);
        ActivityText = HadActivity
            ? localization.Format("WrapUpHadActivity", ScheduleText.Duration(worked, localization))
            : localization.Text("WrapUpNeverStarted");
        Suggested = HadActivity ? WrapUpAction.Done : WrapUpAction.Move;
        SuggestionText = localization.Format("WrapUpSuggestion", ActionLabel(Suggested, localization));
    }

    public CalendarEvent Event { get; }
    public Guid EventId => Event.Id;
    public DateTimeOffset StartUtc => Event.StartUtc;
    public string Title { get; }
    public string TimeText { get; }

    /// <summary>The day this event was on, written out — empty for anything from today.</summary>
    public string DayText { get; }

    /// <summary>
    /// True for a line the drawer has caught up with rather than one today produced. It carries
    /// the date, it is drawn a shade quieter, and it is the half of the list the offer under the
    /// day card deliberately does not count.
    /// </summary>
    public bool IsFromEarlierDay { get; }

    /// <summary>How many days this has gone unanswered. Zero for today.</summary>
    public int DaysWaiting { get; }
    public Avalonia.Media.IBrush Accent { get; }
    public int PlannedMinutes { get; }

    /// <summary>A stopwatch ran against this event, so something was worked on.</summary>
    public bool HadActivity { get; }

    public string ActivityText { get; }
    public string SuggestionText { get; }
    public WrapUpAction Suggested { get; }

    /// <summary>How many times this event has already been pushed onto a later day.</summary>
    public int DeferCount { get; private set; }

    public string NextStepText { get; private set; }
    public bool HasNextStep => !string.IsNullOrWhiteSpace(NextStepText);

    /// <summary>Suggested action highlighted, so the confirming tap is the obvious one.</summary>
    public bool IsDoneSuggested => Suggested == WrapUpAction.Done;
    public bool IsMoveSuggested => Suggested == WrapUpAction.Move;

    [ObservableProperty] private bool _isBusy;

    /// <summary>The row has been answered for; what is left is the line saying so.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowActions))]
    private bool _isResolved;

    [ObservableProperty] private string _resolutionText = string.Empty;

    /// <summary>Pushed often enough that the row asks what keeps getting in the way.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowActions))]
    private bool _isAskingBlocker;

    [ObservableProperty] private string _blockerTitle = string.Empty;

    /// <summary>The single tailored offer that follows the answer, if the answer has one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowActions))]
    private bool _isOfferingRemedy;

    [ObservableProperty] private string _remedyText = string.Empty;
    [ObservableProperty] private string _remedyHint = string.Empty;
    [ObservableProperty] private bool _isRemedyShorten;
    [ObservableProperty] private bool _isRemedyEarlier;
    [ObservableProperty] private bool _isRemedyDrop;
    [ObservableProperty] private bool _isRemedyFirstStep;
    [ObservableProperty] private string _firstStepDraft = string.Empty;

    /// <summary>The three buttons, shown only while the row is still an open question.</summary>
    public bool ShowActions => !IsResolved && !IsAskingBlocker && !IsOfferingRemedy;

    // ---------------------------------------------------------------- the three answers

    [RelayCommand] private Task MarkDoneAsync() => ResolveAsync(WrapUpAction.Done);
    [RelayCommand] private Task MoveOnAsync() => ResolveAsync(WrapUpAction.Move);
    [RelayCommand] private Task SkipAsync() => ResolveAsync(WrapUpAction.Skip);

    /// <summary>Used by "accept all": the row answers with whatever it proposed.</summary>
    public Task AcceptSuggestionAsync() => ResolveAsync(Suggested);

    private async Task ResolveAsync(WrapUpAction action)
    {
        if (IsBusy || IsResolved) return;
        IsBusy = true;
        try { await _resolve(this, action); }
        finally { IsBusy = false; }
    }

    /// <summary>Called back by the shell once the answer is written.</summary>
    public void Settle(string resolution)
    {
        ResolutionText = resolution;
        IsAskingBlocker = false;
        IsOfferingRemedy = false;
        IsResolved = true;
    }

    // ---------------------------------------------------------------- what is in the way

    /// <summary>Turns the row into the question. Only a repeat postponement gets here.</summary>
    public void AskBlocker(int deferCount)
    {
        DeferCount = deferCount;
        OnPropertyChanged(nameof(DeferCount));
        BlockerTitle = _localization.Format("BlockerTitle", deferCount);
        IsResolved = false;
        IsOfferingRemedy = false;
        IsAskingBlocker = true;
    }

    [RelayCommand] private Task ChooseNotEnoughTimeAsync() => ChooseAsync(EventBlocker.NotEnoughTime);
    [RelayCommand] private Task ChooseTooBigAsync() => ChooseAsync(EventBlocker.TooBig);
    [RelayCommand] private Task ChooseUnclearStartAsync() => ChooseAsync(EventBlocker.UnclearStart);
    [RelayCommand] private Task ChooseNotRelevantAsync() => ChooseAsync(EventBlocker.NotRelevant);

    private async Task ChooseAsync(EventBlocker blocker)
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await _answerBlocker(this, blocker); }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// One offer, chosen by the answer, and never a menu of four - a menu would hand the work
    /// of choosing straight back to the person who has just said they do not know where to
    /// start. "Too big" is answered by making the slot smaller; "not enough time" by moving it
    /// to the part of the day nothing else has eaten yet; "unclear where to start" by writing
    /// the first step down while it is still in mind; "no longer relevant" by taking it off
    /// the calendar.
    ///
    /// An event that already runs first thing cannot be moved to first thing, so for that one
    /// "not enough time" falls back to making it shorter - the other honest answer to it.
    /// </summary>
    public void OfferRemedy(EventBlocker blocker)
    {
        var canMoveEarlier = Event.StartUtc.ToLocalTime().TimeOfDay > Morning.Add(TimeSpan.FromHours(1));
        IsRemedyEarlier = blocker == EventBlocker.NotEnoughTime && canMoveEarlier;
        IsRemedyShorten = blocker == EventBlocker.TooBig
                          || (blocker == EventBlocker.NotEnoughTime && !canMoveEarlier);
        IsRemedyFirstStep = blocker == EventBlocker.UnclearStart;
        IsRemedyDrop = blocker == EventBlocker.NotRelevant;
        RemedyText =
            IsRemedyEarlier ? _localization.Text("BlockerEarlier")
            : IsRemedyShorten ? _localization.Text("BlockerShorten")
            : IsRemedyFirstStep ? _localization.Text("BlockerFirstStep")
            : IsRemedyDrop ? _localization.Text("BlockerDrop")
            : string.Empty;
        RemedyHint =
            IsRemedyEarlier ? _localization.Format("BlockerEarlierHint",
                ClockText.Format(Morning, _use24HourClock, _localization.CurrentCulture))
            : IsRemedyShorten ? _localization.Format("BlockerShortenHint",
                ScheduleText.Duration(TimeSpan.FromMinutes(Math.Max(15, PlannedMinutes / 2)), _localization))
            : IsRemedyDrop ? _localization.Text("BlockerDropHint")
            : string.Empty;
        FirstStepDraft = NextStepText;
        IsAskingBlocker = false;
        IsOfferingRemedy = true;
    }

    [RelayCommand]
    private async Task TakeRemedyAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            if (IsRemedyShorten) await _shorten(this);
            else if (IsRemedyEarlier) await _moveEarlier(this);
            else if (IsRemedyDrop) await _drop(this);
            else if (IsRemedyFirstStep) await _saveFirstStep(this, FirstStepDraft);
        }
        finally { IsBusy = false; }
    }

    /// <summary>Declining the offer is an answer too; the blocker itself is already recorded.</summary>
    [RelayCommand]
    private void DeclineRemedy() => Settle(_localization.Text("BlockerNoted"));

    public void ApplyNextStep(string? nextStep)
    {
        NextStepText = nextStep ?? string.Empty;
        OnPropertyChanged(nameof(NextStepText));
        OnPropertyChanged(nameof(HasNextStep));
    }

    private static string ActionLabel(WrapUpAction action, LocalizationService localization) => action switch
    {
        WrapUpAction.Done => localization.Text("WrapUpDone"),
        WrapUpAction.Move => localization.Text("WrapUpMove"),
        _ => localization.Text("WrapUpSkip")
    };
}
