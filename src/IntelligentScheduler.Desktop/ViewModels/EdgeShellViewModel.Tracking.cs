using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IntelligentScheduler.Desktop.Controls;
using IntelligentScheduler.Domain;

namespace IntelligentScheduler.Desktop.ViewModels;

/// <summary>
/// Recording what actually happened to an event, and saying so afterwards.
///
/// Every verdict — from the day card, from a row in the list, from the hover widget — goes
/// through <see cref="RecordOutcomeAsync"/>. The controls used to exist only on the card, and
/// the card only ever pointed at the current or next event, which left anything that finished
/// earlier, and every event on a past day, with no way to be marked at all.
///
/// The working cycle is deliberately short, and asks a question in exactly one place:
///
///     start → work → pause if you have to → stop → say how it went → leave a next step
///
/// Nothing before "stop" asks anything. Pausing is silent, because the timer keeps the work
/// and the pauses as two separate numbers and therefore has nothing to ask about; running past
/// the planned end is silent, because overrunning is a measurement rather than a mistake, and
/// the dock simply says "plan 45m · actual 1h 12m". The one question comes once the session is
/// over, and even then it never asks for a duration — the session already measured that. What
/// it asks for is the part only the user knows: how far the work got, and where to pick it up.
///
/// Three surfaces, and one job each. The day card <i>describes</i> the event: its name, its
/// hours, and — while a session is on — when the stopwatch was set going and what it reads.
/// The dock across the foot of the shell is the <i>controls</i>: start, hold, stop, the plan
/// beside the fact, and the one thing a long pause is allowed to ask. The row's own tray is
/// where the event is <i>answered for</i>: "how did it go?", the three verdicts, and for
/// "partly" the second step that asks how far it got and what is left. All three used to be
/// the same card, which is why it grew a control panel and a question sheet on top of the
/// event it was supposed to be describing, and why the three verdicts existed twice on one
/// screen. Whatever raises the question — a stop from the dock, "partly" from a row, the
/// widget — goes through <see cref="OpenSessionReview"/>, which opens the tray of the row the
/// question is about; the answer being written lives here, because the widget can raise the
/// same question, and only the row's placement is the row's.
///
/// The other half of monitoring is reading it back. A day with two skipped events and three
/// nobody marked is not a day that went well, and the widget used to say "all on track" for
/// it, because the only thing it looked at was overdue tasks. What the day actually came to is
/// now computed once, in <see cref="BuildDayReview"/>, and everything that has an opinion about
/// the day — the card, the chip on the widget, the creature's mood — reads from that. Whatever
/// is still unanswered at the end of the day is collected and asked about once, together, by
/// the wrap-up in <c>EdgeShellViewModel.WrapUp.cs</c> — never event by event.
/// </summary>
public sealed partial class EdgeShellViewModel
{
    private CalendarEvent? _trackedEvent;
    private EventOutcome? _trackedOutcome;
    private EventFollowUp? _trackedFollowUp;
    /// <summary>The event the "how did it go?" sheet is currently asking about.</summary>
    private CalendarEvent? _sessionReviewTarget;
    private readonly Dictionary<Guid, EventOutcome> _dayOutcomes = [];
    private readonly Dictionary<Guid, EventFollowUp> _dayFollowUps = [];

    /// <summary>
    /// Rows whose "what is left" card the user has folded away. Held here rather than on the
    /// row because the day list is rebuilt from scratch every minute, and a card that unfolded
    /// itself on the next tick would be a card that cannot be put away at all.
    /// </summary>
    private readonly HashSet<Guid> _foldedNextSteps = [];

    /// <summary>
    /// Rows whose "what is left" card is rolled up to its heading. Kept apart from the folded
    /// set because the two mean different things and can be true at once: a card can be rolled
    /// up, then closed, then brought back - and it comes back rolled up, which is what it was.
    /// </summary>
    private readonly HashSet<Guid> _collapsedNextSteps = [];
    private DayReview _dayReview;

    /// <summary>
    /// How long a held session may stand still before the application says anything. Short
    /// enough that a session abandoned after lunch does not sit open all afternoon, long enough
    /// that stepping out for coffee is answered by nothing at all.
    /// </summary>
    private static readonly TimeSpan LongPause = TimeSpan.FromMinutes(25);

    /// <summary>
    /// How long after an event has ended it can still be the one the card and the widget are
    /// about. Long enough that finishing something and going to lunch still leaves it there to
    /// be marked; short enough that this morning is never what the widget is showing tonight.
    /// </summary>
    private static readonly TimeSpan StaleAfterEnd = TimeSpan.FromHours(2);

    /// <summary>
    /// The one line about how the user is doing. It used to appear only when something had gone
    /// wrong, which meant the widget could say "two overdue" but never "nothing is overdue" —
    /// the absence of bad news is not the same as being told the news is good. It also used to
    /// say the news was good while the day held skipped and unmarked events, which is worse.
    /// </summary>
    [ObservableProperty] private string _peekStatusText = string.Empty;

    /// <summary>Which of the three readings the line above is: bad, worth a look, or fine.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPeekStatusDanger))]
    [NotifyPropertyChangedFor(nameof(IsPeekStatusWarning))]
    [NotifyPropertyChangedFor(nameof(IsPeekStatusOk))]
    private string _peekStatusTone = "ok";

    public bool IsPeekStatusDanger => PeekStatusTone == "danger";
    public bool IsPeekStatusWarning => PeekStatusTone == "warning";
    public bool IsPeekStatusOk => PeekStatusTone == "ok";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOverdueTasks))]
    private int _overdueTaskCount;

    public bool HasOverdueTasks => OverdueTaskCount > 0;

    /// <summary>Events of the shown day that have started and still carry no verdict.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnmarkedEvents))]
    private int _unmarkedEventCount;

    [ObservableProperty] private string _unmarkedEventsText = string.Empty;

    public bool HasUnmarkedEvents => UnmarkedEventCount > 0;

    // ---- What the shown day came to ------------------------------------------------------

    /// <summary>
    /// The tally of a day: how many of its events were done, half done, skipped, or never
    /// answered for. Counted once per reload and read by everything that describes the day.
    /// </summary>
    private readonly record struct DayReview(
        int Total,
        int Done,
        int Partial,
        int Skipped,
        int Unmarked,
        int Upcoming,
        int PlannedMinutes,
        int RecordedMinutes,
        DateTimeOffset? FirstStartUtc,
        DateTimeOffset? LastEndUtc)
    {
        public int Answered => Done + Partial + Skipped;
        public bool HasEvents => Total > 0;
        /// <summary>Minutes actually spent against minutes planned, as a share.</summary>
        public double Completion => PlannedMinutes <= 0
            ? 0
            : Math.Clamp(RecordedMinutes / (double)PlannedMinutes, 0, 1);
    }

    /// <summary>Chips under the day card: "3 done", "1 partly", "2 skipped", "1 not marked".</summary>
    public ObservableCollection<DayReviewChipViewModel> DayReviewChips { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDayReview))]
    private bool _isDayReviewVisible;

    public bool HasDayReview => IsDayReviewVisible && DayReviewChips.Count > 0;

    // ---- Hover widget --------------------------------------------------------------------
    // The widget is modular. With nothing running it is one small block: the clock, the date
    // and a single line about what is next. Once an event is on, two more blocks and a control
    // rail appear around it. Everything shown there is derived here rather than in the window,
    // so the same numbers appear whether the panel is open or the user only brushed the edge
    // of the screen.

    /// <summary>An event is running, about to start, or finished and still unmarked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPeekEventBlock))]
    [NotifyPropertyChangedFor(nameof(ShowPeekTrackingRail))]
    [NotifyPropertyChangedFor(nameof(ShowPeekSessionPrompt))]
    [NotifyPropertyChangedFor(nameof(ShowPeekPausePrompt))]
    [NotifyPropertyChangedFor(nameof(IsPeekReminderMine))]
    [NotifyPropertyChangedFor(nameof(IsPeekReminderSolo))]
    [NotifyPropertyChangedFor(nameof(ShowPeekEventLines))]
    private bool _hasPeekEvent;

    /// <summary>The inline event timer exists while there is still time left to count.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPeekEventTimer))]
    private bool _hasPeekTimer;

    /// <summary>
    /// Which event the card is describing. A reminder carries the id of the event it is about,
    /// and whether the two are the same is the difference between the card wearing the reminder
    /// and the card standing aside for it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPeekReminderMine))]
    [NotifyPropertyChangedFor(nameof(IsPeekReminderSolo))]
    [NotifyPropertyChangedFor(nameof(ShowPeekEventLines))]
    [NotifyPropertyChangedFor(nameof(ShowPeekEventTimer))]
    [NotifyPropertyChangedFor(nameof(ShowPeekSessionPrompt))]
    private Guid? _peekEventId;

    /// <summary>The compact timer: stopwatch, time left, or time to start.</summary>
    [ObservableProperty] private string _peekTimerText = string.Empty;

    /// <summary>The event's planned start and end, without session or project metadata.</summary>
    [ObservableProperty] private string _peekEventTimeRange = string.Empty;

    /// <summary>The line under it that says which of those three the number is.</summary>
    [ObservableProperty] private string _peekTimerCaption = string.Empty;

    /// <summary>How far through the event the clock is, 0..100, for the hairline progress.</summary>
    [ObservableProperty] private double _peekProgressPercent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPeekNext))]
    [NotifyPropertyChangedFor(nameof(ShowPeekNextBlock))]
    private string _peekNextTitle = string.Empty;

    [ObservableProperty] private string _peekNextTime = string.Empty;
    [ObservableProperty] private bool _isPeekEventRunning;
    [ObservableProperty] private string _peekDateText = string.Empty;

    /// <summary>
    /// The event's time has come and nobody has pressed start. The countdown block stays, but
    /// its number does not move: the stopwatch measures the user's work, not the wall clock.
    /// </summary>
    [ObservableProperty] private bool _isPeekAwaitingStart;

    /// <summary>What the creature beside the clock is reacting to.</summary>
    [ObservableProperty] private MascotMood _peekMascotMood = MascotMood.Calm;

    public bool HasPeekNext => PeekNextTitle.Length > 0;

    // ---- The petal ------------------------------------------------------------------------
    // The one surface of this application that is on the screen all day. It is derived from the
    // same numbers as the widget and deliberately says less than any of them: it is read at a
    // glance, out of the corner of an eye, by somebody who is working in another window.

    /// <summary>
    /// Whether the petal is carrying a session rather than simply standing on the edge.
    ///
    /// Not the same window as the widget's, and narrower on purpose. The widget opens a quarter
    /// of an hour before an event because it was asked for and the next thing is what it is
    /// about; the petal was not asked for, it grows on its own, and something that grows on its
    /// own fifteen minutes early is something that grew for no reason. It waits for the event to
    /// actually begin — or for somebody to press start, which can happen earlier.
    /// </summary>
    [ObservableProperty] private bool _isPetalLive;

    /// <summary>
    /// The reading, stood on end: hours over minutes over seconds. A capsule this narrow has no
    /// room for "01:23:45" laid out flat, but it has room for the same clock turned through
    /// ninety degrees — and turning it is the one way of narrowing it that loses nothing.
    ///
    /// Three properties rather than one string because the three lines are not worth the same:
    /// the minutes are what anybody glancing at a stopwatch is reading, and the hours and the
    /// seconds are context around them. That difference is a colour, and a colour needs its own
    /// run of text.
    /// </summary>
    [ObservableProperty] private string _petalHours = string.Empty;

    [ObservableProperty] private string _petalMinutes = string.Empty;

    [ObservableProperty] private string _petalSeconds = string.Empty;

    /// <summary>The whole sentence, for anyone who stops long enough to want it.</summary>
    [ObservableProperty] private string _petalTooltip = string.Empty;

    /// <summary>
    /// Whether the reading is standing still. A stopwatch that has stopped and a stopwatch that
    /// is running look identical for the fifty-nine seconds between two changes of the minutes,
    /// and on a surface this small the seconds are the only thing that would have said so.
    /// </summary>
    [ObservableProperty] private bool _isPetalPaused;

    /// <summary>The figures the hover widget shows without the user opening anything.</summary>
    private void RefreshPeekSummary()
    {
        OverdueTaskCount = Application.ScheduleAnalytics.OverdueTaskCount(
            [.. Tasks.Select(item => item.Task)], DateTimeOffset.UtcNow);
        RefreshPeekStatus();
        PeekDateText = localization.CurrentCulture.TextInfo.ToTitleCase(
            SelectedDate.ToString("ddd, d MMM", localization.CurrentCulture));
        RefreshPeekEvent();
    }

    /// <summary>
    /// The one honest line. Read in order of how much it matters: something already late, then
    /// work nobody has answered for, then work that was dropped, then work only half done. Only
    /// once none of those is true does the widget get to say the day is going well.
    /// </summary>
    private void RefreshPeekStatus()
    {
        if (OverdueTaskCount > 0)
        {
            PeekStatusText = localization.Format("PeekOverdue", OverdueTaskCount);
            PeekStatusTone = "danger";
        }
        else if (_dayReview.Unmarked > 0)
        {
            PeekStatusText = localization.Format("PeekUnmarked", _dayReview.Unmarked);
            PeekStatusTone = "warning";
        }
        else if (_dayReview.Skipped > 0)
        {
            PeekStatusText = localization.Format("PeekSkipped", _dayReview.Skipped);
            PeekStatusTone = "warning";
        }
        else if (_dayReview.Partial > 0)
        {
            PeekStatusText = localization.Format("PeekPartial", _dayReview.Partial);
            PeekStatusTone = "warning";
        }
        else if (_dayReview is { Total: > 0, Upcoming: 0 })
        {
            PeekStatusText = localization.Format("PeekDayDone", _dayReview.Done);
            PeekStatusTone = "ok";
        }
        else
        {
            PeekStatusText = localization.Text("PeekAllGood");
            PeekStatusTone = "ok";
        }
    }

    /// <summary>Countdowns are shown at second resolution, like a stopwatch.</summary>
    private static string ClockSpan(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        return $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}";
    }

    /// <summary>
    /// Which blocks exist, and what the numbers in them say. Recomputed on the one-second clock
    /// so the widget is never stale by the time the pointer reaches it.
    /// </summary>
    private void RefreshPeekEvent()
    {
        var now = DateTimeOffset.UtcNow;
        var source = _trackedEvent;
        var outcome = _trackedOutcome;
        PeekEventTimeRange = source is null ? string.Empty
            : ClockText.Range(source.StartUtc, source.EndUtc, Use24HourClock, localization.CurrentCulture);
        var isReported = outcome is { IsFinished: true };
        // The one state that means "the user is working on this": they pressed start themselves.
        var isRunningStopwatch = outcome is { IsSessionOpen: true };

        // The same window the fixation controls use: an event counts as "on" from a quarter of
        // an hour before it starts until someone says how it went.
        HasPeekEvent = source is not null && source.StartUtc <= now.AddMinutes(15);
        // Which event that is, so a reminder can tell whether it is about the card it landed on.
        PeekEventId = source?.Id;
        // Once a verdict is in, the countdown has nothing left to say and the block folds away.
        // A stopwatch someone is still running keeps its block past the planned end, because a
        // task that overruns is exactly when the number matters most.
        HasPeekTimer = HasPeekEvent && !isReported && (source!.EndUtc > now || isRunningStopwatch);
        IsPeekEventRunning = source is not null && !isReported && source.StartUtc <= now && source.EndUtc > now;
        IsPeekAwaitingStart = HasPeekTimer && !isRunningStopwatch && source!.StartUtc <= now;

        RefreshPeekNext(now, source);

        if (source is null)
        {
            PeekTimerText = string.Empty;
            PeekTimerCaption = string.Empty;
            PeekProgressPercent = 0;
            RefreshPetal(null, false, TimeSpan.Zero, false);
            RefreshMascotMood();
            return;
        }

        var planned = Math.Max(1d, (source.EndUtc - source.StartUtc).TotalMinutes);
        // The bar tracks the work, not the wall clock and not the session's own span. It used
        // to fill on its own from the moment the event was due, which said "you are half way
        // through this" to someone who had not started it at all; it now also stops filling
        // while the session is held, because a pause is not progress.
        PeekProgressPercent = isReported
            ? 100d
            : isRunningStopwatch
                ? Math.Clamp(outcome!.WorkedAt(now).TotalMinutes / planned, 0, 1) * 100d
                : 0d;

        if (!HasPeekTimer)
        {
            PeekTimerText = string.Empty;
            PeekTimerCaption = string.Empty;
        }
        else if (now < source.StartUtc)
        {
            PeekTimerText = ClockSpan(source.StartUtc - now);
            PeekTimerCaption = localization.Text("PeekUntilStart");
        }
        else if (isRunningStopwatch)
        {
            // Someone pressed start, so the number they care about is their own stopwatch: work
            // done, pauses left out. The caption is the plan set beside the fact - which is all
            // the planned length is for now that it no longer stops anything.
            PeekTimerText = TrackingElapsedText;
            PeekTimerCaption = outcome!.IsPaused
                ? localization.Format("TrackPausedFor",
                    ScheduleText.Duration(outcome.PausedFor(now), localization))
                : TrackingPlanActualText;
        }
        else
        {
            // Due, and untouched. The stopwatch belongs to the user; until they press start there
            // is nothing to count, so it sits at zero and says what it is waiting for.
            PeekTimerText = ClockSpan(TimeSpan.Zero);
            PeekTimerCaption = localization.Text("PeekNotStarted");
        }

        RefreshPetal(source, isReported,
            isRunningStopwatch ? outcome!.WorkedAt(now) : TimeSpan.Zero, isRunningStopwatch);
        RefreshMascotMood();
    }

    /// <summary>
    /// What the petal is showing, which is a strict subset of what the widget is: the same
    /// stopwatch, the same bar, and no opinion of its own about any of it.
    /// </summary>
    private void RefreshPetal(CalendarEvent? source, bool isReported, TimeSpan worked, bool isRunningStopwatch)
    {
        IsPetalLive = source is not null && HasPeekEvent && !isReported
                      && (IsPeekEventRunning || isRunningStopwatch);
        if (!IsPetalLive)
        {
            // Left empty rather than frozen at its last reading. The petal folds away over about
            // a third of a second, and a stale number counting nothing on the way out is the one
            // thing the eye would follow.
            PetalHours = string.Empty;
            PetalMinutes = string.Empty;
            PetalSeconds = string.Empty;
            IsPetalPaused = false;
            PetalTooltip = string.Empty;
            return;
        }

        (PetalHours, PetalMinutes, PetalSeconds) = SplitClock(worked);
        IsPetalPaused = IsSessionPaused;
        PetalTooltip = string.Join(" · ", new[] { source!.Title, PeekTimerText, PeekTimerCaption }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    /// <summary>
    /// A span as three pairs of digits, which is the ordinary clock and nothing cleverer. The
    /// hours are capped at ninety-nine rather than allowed to grow a third digit: the capsule is
    /// one column wide, and a reading that changes width is a reading that moves.
    /// </summary>
    private static (string Hours, string Minutes, string Seconds) SplitClock(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        return ($"{Math.Min((int)value.TotalHours, 99):00}", $"{value.Minutes:00}", $"{value.Seconds:00}");
    }

    /// <summary>
    /// What comes after whatever the widget is currently describing — and, when it is describing
    /// nothing, simply what is next. It lives in its own block rather than inside the countdown,
    /// so finishing something early, which folds the countdown away, no longer takes "and then?"
    /// off the screen with it.
    ///
    /// The quiet state used to be said in the clock block instead, as a sentence under the date.
    /// That put one fact in two places depending on the hour, and it put the day's schedule
    /// inside the block that carries the time and the creature. One block says this now,
    /// whatever the widget is otherwise doing.
    ///
    /// Only today has a "next". On another day the whole widget is a report, and the first event
    /// on a page about last Tuesday is not something that is about to happen.
    /// </summary>
    private void RefreshPeekNext(DateTimeOffset now, CalendarEvent? source)
    {
        var following = SelectedDate != DateOnly.FromDateTime(DateTime.Today)
            ? null
            : Agenda
                .Where(item => item.Event is { } candidate
                               // The event the card is already describing is not also the next
                               // one; with no card, the soonest event is exactly what is next.
                               && (!HasPeekEvent || candidate.Id != source?.Id)
                               && candidate.StartUtc > now
                               && _dayOutcomes.GetValueOrDefault(candidate.Id) is not { IsFinished: true })
                .OrderBy(item => item.StartUtc)
                .FirstOrDefault();
        PeekNextTitle = following?.Title ?? string.Empty;
        PeekNextTime = following is null ? string.Empty : following.StartText;
    }

    /// <summary>
    /// The creature beside the clock. It carries one reading of the day and nothing else, so the
    /// order here is the order the states matter in: something is wrong, something is running,
    /// there is nothing to do, everything is in hand.
    /// </summary>
    private void RefreshMascotMood()
        => PeekMascotMood = OverdueTaskCount > 0 || _dayReview.Skipped > 0
            ? MascotMood.Alert
            : _trackedOutcome is { StartedAtUtc: not null, IsFinished: false }
                ? MascotMood.Focused
                : Agenda.Count == 0
                    ? MascotMood.Sleepy
                    : UnmarkedEventCount == 0
                        ? MascotMood.Happy
                        : MascotMood.Calm;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMonitorPanel))]
    private bool _canTrackEvent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartTracking))]
    [NotifyPropertyChangedFor(nameof(IsTrackingTimerVisible))]
    private bool _isEventStarted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTrackingVerdict))]
    [NotifyPropertyChangedFor(nameof(CanStartTracking))]
    [NotifyPropertyChangedFor(nameof(IsTrackingTimerVisible))]
    [NotifyPropertyChangedFor(nameof(ShowMonitorPanel))]
    private bool _isEventFinished;

    /// <summary>The session is on and counting.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPauseSession))]
    private bool _isSessionRunning;

    /// <summary>The session is on and standing still. Nothing was asked to put it here.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanResumeSession))]
    private bool _isSessionPaused;

    /// <summary>A stopwatch exists for this event, moving or held.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPauseSession))]
    [NotifyPropertyChangedFor(nameof(CanResumeSession))]
    private bool _isSessionOpen;

    [ObservableProperty] private string _trackingStateText = string.Empty;
    [ObservableProperty] private string _trackingElapsedText = "00:00:00";

    /// <summary>
    /// The moment the stopwatch was set going, on its own. The day card keeps this line and the
    /// running clock beside it and nothing else: every control, every comparison against the
    /// plan and every verdict now belongs either to the dock at the foot of the shell or to the
    /// row's own tray, so the card says what is happening and never asks about it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTrackingStartedAt))]
    private string _trackingStartedAtText = string.Empty;

    /// <summary>One word for what the rail is looking at: running, held, or nothing yet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MonitorReadingText))]
    private string _monitorStateText = string.Empty;

    /// <summary>Time this session has stood still, shown only once there is some.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTrackingPause))]
    private string _trackingPauseText = string.Empty;

    /// <summary>
    /// "plan 45 min - actual 1 h 12 min". The planned length stops being a limit and becomes a
    /// reading: the timer is never cut off at it, and running past it is simply shown.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTrackingPlanActual))]
    [NotifyPropertyChangedFor(nameof(MonitorReadingText))]
    private string _trackingPlanActualText = string.Empty;

    /// <summary>Whether the session has passed the length it was booked for.</summary>
    [ObservableProperty] private bool _isTrackingOverPlan;

    // ---- The one thing a long pause is allowed to ask ------------------------------------

    /// <summary>
    /// A session held for longer than <see cref="LongPause"/>. Not an error and not a prompt in
    /// the middle of the screen: a quiet strip on the surfaces already showing the timer, with
    /// the only two answers that exist - keep going, or end the session and say how it went.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPeekPausePrompt))]
    private bool _isLongPause;

    [ObservableProperty] private string _longPauseText = string.Empty;

    public bool HasTrackingVerdict => IsEventFinished;
    public bool CanStartTracking => !IsEventStarted && !IsEventFinished;
    public bool CanPauseSession => IsSessionOpen && IsSessionRunning;
    public bool CanResumeSession => IsSessionOpen && IsSessionPaused;
    /// <summary>
    /// Whether the monitoring dock is drawn at all. It is a panel of tools for a session that
    /// can still be run, so an event that has already been answered for takes it away rather
    /// than leaving a strip of controls with nothing left to do.
    /// </summary>
    public bool ShowMonitorPanel => CanTrackEvent && !IsEventFinished;
    public bool HasTrackingStartedAt => TrackingStartedAtText.Length > 0;
    public bool HasTrackingState => !string.IsNullOrWhiteSpace(TrackingStateText);
    public bool HasTrackingPause => !string.IsNullOrWhiteSpace(TrackingPauseText);
    public bool HasTrackingPlanActual => !string.IsNullOrWhiteSpace(TrackingPlanActualText);
    public bool IsTrackingTimerVisible => IsEventStarted && !IsEventFinished;

    partial void OnTrackingStateTextChanged(string value) => OnPropertyChanged(nameof(HasTrackingState));
    partial void OnTrackingPauseTextChanged(string value) => OnPropertyChanged(nameof(HasTrackingPause));
    partial void OnTrackingPlanActualTextChanged(string value) => OnPropertyChanged(nameof(HasTrackingPlanActual));

    // ---- "How did it go?" ----------------------------------------------------------------
    // One sheet, two steps, and the second one only for the answer that needs it. Stopping asks
    // the question; every other way of marking an event answers it directly.

    /// <summary>Whether the sheet is up. The shell draws it in the day card, the widget as a strip.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPeekSessionPrompt))]
    [NotifyPropertyChangedFor(nameof(ShowPeekPausePrompt))]
    private bool _isSessionReviewOpen;

    /// <summary>
    /// The second step, reached only by answering "partly": how far it got, and what is left.
    /// Nothing here asks for minutes - the session measured those and the sheet states them.
    /// </summary>
    [ObservableProperty] private bool _isSessionReviewDetailStep;

    /// <summary>"Worked 1 h 12 min of 45 min planned", stated rather than asked.</summary>
    [ObservableProperty] private string _sessionReviewMeasuredText = string.Empty;

    [ObservableProperty] private string _sessionReviewTitle = string.Empty;

    /// <summary>
    /// How far the work got, in whole percent. It used to be four preset shares, which is the
    /// application deciding what an honest answer looks like; the figure is the user's, so the
    /// sheet hands over the whole scale and lets them land on the number they mean.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SessionReviewProgressText))]
    private double _sessionReviewProgress = 50;

    /// <summary>
    /// The line being typed. It is the draft rather than the answer: pressing enter or the
    /// button beside it moves it into <see cref="SessionReviewSteps"/>, and saving folds in
    /// whatever is still sitting here, so a single line never has to be "added" to be kept.
    /// </summary>
    [ObservableProperty] private string _sessionReviewNextStep = string.Empty;

    /// <summary>
    /// What is left, one line per thing. It used to be a single string with no way to add a
    /// second line, which made "what is left" a note rather than a list - and a list is what
    /// half-finished work actually leaves behind.
    /// </summary>
    public ObservableCollection<SessionStepViewModel> SessionReviewSteps { get; } = [];

    public bool HasSessionReviewSteps => SessionReviewSteps.Count > 0;

    /// <summary>Everything the rail's clock has to say, kept for its tooltip.</summary>
    public string MonitorReadingText => TrackingPlanActualText.Length == 0
        ? MonitorStateText
        : $"{MonitorStateText} · {TrackingPlanActualText}";

    /// <summary>
    /// Which event the sheet is asking about. The widget describes one event at a time, and the
    /// question can have been raised about another - a row's "partly", say - so the strip only
    /// appears when the two are the same. The shell's own sheet names its event and needs no
    /// such guard.
    /// </summary>
    public Guid? SessionReviewEventId => _sessionReviewTarget?.Id;
    public string SessionReviewProgressText => $"{SessionReviewProgress:0}%";

    /// <summary>
    /// The latest finished event of the day that still has no verdict recorded - and only while
    /// it is recent enough to be worth asking about. It used to reach back over the whole day,
    /// so a nine o'clock meeting nobody had marked was still the event the widget was about at
    /// eight in the evening: a card announcing something that had been over for eleven hours.
    /// Work older than <see cref="StaleAfterEnd"/> is not forgotten, only no longer the card's
    /// subject - the status line still counts it, and the day's wrap-up still asks about it.
    /// </summary>
    private CalendarEvent? FindUnreported(IReadOnlyList<CalendarEvent> events, DateTimeOffset now)
        => events
            .Where(item => item.EndUtc <= now && item.EndUtc > now - StaleAfterEnd)
            .OrderByDescending(item => item.EndUtc)
            .FirstOrDefault(item => !_dayOutcomes.TryGetValue(item.Id, out var outcome) || !outcome.IsFinished);

    /// <summary>Which event the card, the widget and the rail are all pointing at.</summary>
    private readonly record struct TrackingTarget(CalendarEvent? Event, bool IsCurrent, bool IsUnreported);

    /// <summary>
    /// Picks that event. An event that has been reported on keeps the target until its planned
    /// window runs out: the verdict chip and the undo button belong to it, and taking them away
    /// the instant something is marked done leaves no way back from a mis-tap. What comes next
    /// is answered by its own block instead, which is what finishing early actually needs.
    ///
    /// Another day never has a target. The card used to fall back to the day's first event,
    /// which is why opening last Tuesday announced a nine o'clock meeting as though it were
    /// about to happen; what that day needs said about it is how it went, not what was in it.
    /// </summary>
    private TrackingTarget ChooseTrackingTarget(IReadOnlyList<CalendarEvent> events, bool isToday, DateTimeOffset now)
    {
        if (!isToday) return new TrackingTarget(null, false, false);
        var current = events.FirstOrDefault(item => item.StartUtc <= now && item.EndUtc > now);
        var upcoming = current ?? events.FirstOrDefault(item => item.StartUtc > now);
        // Nothing ahead today still leaves work to report on. Falling back to the most recent
        // finished event keeps the fixation buttons reachable, which is the point of the card.
        var unreported = upcoming is null ? FindUnreported(events, now) : null;
        return new TrackingTarget(upcoming ?? unreported, current is not null, upcoming is null && unreported is not null);
    }

    /// <summary>Applies that choice to the day card's headline and to everything derived from it.</summary>
    private void ApplyTrackingTarget(IReadOnlyList<CalendarEvent> events, bool isToday, DateTimeOffset now)
    {
        _dayReview = BuildDayReview(events, now);
        var target = ChooseTrackingTarget(events, isToday, now);

        if (target.Event is null)
        {
            ApplyDayReviewCard(isToday, now);
            RefreshTracking(null);
            RefreshPeekStatus();
            return;
        }

        IsDayReviewVisible = false;
        DayReviewChips.Clear();
        OnPropertyChanged(nameof(HasDayReview));
        NextLabel = localization.Text(target.IsCurrent
            ? "CurrentEvent"
            : target.IsUnreported ? "FinishedEvent" : "NextEvent");
        NextTitle = target.Event.Title;
        RefreshTracking(target.Event);
        ApplyCardMeta();
        RefreshPeekStatus();
    }

    /// <summary>
    /// Turns the day's tally into the headline of the card. Three readings share one shape: a
    /// day that is over says what came of it, a day still ahead says what is in it, and today
    /// with nothing left to run says how far it got.
    /// </summary>
    private void ApplyDayReviewCard(bool isToday, DateTimeOffset now)
    {
        var review = _dayReview;
        var isFuture = SelectedDate > DateOnly.FromDateTime(DateTime.Today);

        NextLabel = localization.Text(isFuture ? "DayAheadLabel" : isToday ? "DaySoFarLabel" : "DayResultLabel");

        if (!review.HasEvents)
        {
            NextTitle = localization.Text(isFuture ? "NoEventsTitle" : "DayReviewEmpty");
            NextMeta = localization.Text(isFuture ? "NoEventsHint" : "DayReviewEmptyHint");
            IsDayReviewVisible = false;
            DayReviewChips.Clear();
            OnPropertyChanged(nameof(HasDayReview));
            return;
        }

        var span = review.FirstStartUtc is { } first && review.LastEndUtc is { } last
            ? ClockText.Range(first, last, Use24HourClock, localization.CurrentCulture)
            : string.Empty;
        var planned = ScheduleText.Duration(TimeSpan.FromMinutes(review.PlannedMinutes), localization);

        if (isFuture)
        {
            NextTitle = localization.Format("DayAheadTitle", FormatEventsCount(review.Total), planned);
            NextMeta = span;
            IsDayReviewVisible = false;
            DayReviewChips.Clear();
            OnPropertyChanged(nameof(HasDayReview));
            return;
        }

        NextTitle = localization.Format("DayReviewTitle", review.Done, review.Total);
        NextMeta = localization.Format("DayReviewMeta",
            ScheduleText.Duration(TimeSpan.FromMinutes(review.RecordedMinutes), localization),
            planned,
            (int)Math.Round(review.Completion * 100));

        BuildDayReviewChips(review);
    }

    /// <summary>
    /// The coloured counts under the headline. Only what happened gets a chip: a day with
    /// nothing skipped should not carry a zero next to the word "skipped".
    /// </summary>
    private void BuildDayReviewChips(DayReview review)
    {
        DayReviewChips.Clear();
        if (review.Done > 0)
            DayReviewChips.Add(new(localization.Format("DayChipDone", review.Done), EventVisuals.SuccessBrush, true));
        if (review.Partial > 0)
            DayReviewChips.Add(new(localization.Format("DayChipPartial", review.Partial), EventVisuals.WarningBrush, true));
        if (review.Skipped > 0)
            DayReviewChips.Add(new(localization.Format("DayChipSkipped", review.Skipped), EventVisuals.DangerBrush, true));
        if (review.Unmarked > 0)
            DayReviewChips.Add(new(localization.Format("DayChipUnmarked", review.Unmarked), EventVisuals.MutedBrush, false));
        if (review.Upcoming > 0)
            DayReviewChips.Add(new(localization.Format("DayChipUpcoming", review.Upcoming), EventVisuals.FocusBrush, false));
        IsDayReviewVisible = DayReviewChips.Count > 0;
        OnPropertyChanged(nameof(HasDayReview));
    }

    /// <summary>Counts the day once, from the events on screen and the verdicts held for them.</summary>
    private DayReview BuildDayReview(IReadOnlyList<CalendarEvent> events, DateTimeOffset now)
    {
        if (events.Count == 0) return default;
        var (dayStart, dayEnd) = DayRange(SelectedDate);
        int done = 0, partial = 0, skipped = 0, unmarked = 0, upcoming = 0, planned = 0;

        foreach (var item in events)
        {
            var clippedStart = item.StartUtc > dayStart ? item.StartUtc : dayStart;
            var clippedEnd = item.EndUtc < dayEnd ? item.EndUtc : dayEnd;
            if (clippedEnd > clippedStart) planned += (int)Math.Ceiling((clippedEnd - clippedStart).TotalMinutes);

            switch (_dayOutcomes.GetValueOrDefault(item.Id))
            {
                case { Kind: EventOutcomeKind.Completed }: done++; break;
                case { Kind: EventOutcomeKind.Partial }: partial++; break;
                case { Kind: EventOutcomeKind.Skipped }: skipped++; break;
                // Pressing start is not a verdict. An event that finished with only a start on
                // it is exactly the case the application used to count as handled.
                default:
                    if (item.EndUtc <= now) unmarked++;
                    else upcoming++;
                    break;
            }
        }

        return new DayReview(events.Count, done, partial, skipped, unmarked, upcoming,
            planned, Math.Min(RecordedMinutes(events), planned),
            events.Min(item => item.StartUtc), events.Max(item => item.EndUtc));
    }

    /// <summary>
    /// Re-runs the choice against the day already in memory. Recording a verdict can change the
    /// answer — marking an older event closes the fallback the card was pointing at — and the
    /// day is only reloaded once a minute.
    /// </summary>
    private void RetargetTracking()
        => ApplyTrackingTarget(
            [.. Agenda.Select(item => item.Event).OfType<CalendarEvent>().OrderBy(item => item.StartUtc)],
            SelectedDate == DateOnly.FromDateTime(DateTime.Today),
            DateTimeOffset.UtcNow);

    /// <summary>
    /// Points the day card's controls at whatever it is showing. The list rows keep their own
    /// state, so this no longer decides what can be recorded — only what the card describes.
    /// </summary>
    private void RefreshTracking(CalendarEvent? candidate)
    {
        _trackedEvent = candidate;
        _trackedOutcome = candidate is null ? null : _dayOutcomes.GetValueOrDefault(candidate.Id);
        _trackedFollowUp = candidate is null ? null : _dayFollowUps.GetValueOrDefault(candidate.Id);

        if (candidate is null)
        {
            CanTrackEvent = false;
            IsEventStarted = false;
            IsEventFinished = false;
            IsSessionOpen = false;
            IsSessionRunning = false;
            IsSessionPaused = false;
            TrackingStateText = string.Empty;
            TrackingStartedAtText = string.Empty;
            MonitorStateText = string.Empty;
            RefreshTrackingTimer();
            RefreshPeekEvent();
            return;
        }

        // Nothing in the future can be reported on yet - unless a stopwatch is already running
        // against it, in which case hiding the controls would strand the session with no way
        // to pause or stop it. The rule is about when marking becomes meaningful, not about
        // taking away controls somebody is using.
        CanTrackEvent = candidate.StartUtc <= DateTimeOffset.UtcNow.AddMinutes(15)
                        || _trackedOutcome is { IsSessionOpen: true };
        ApplyOutcomeState();
    }

    private void ApplyOutcomeState()
    {
        var outcome = _trackedOutcome;
        IsEventStarted = outcome is { StartedAtUtc: not null };
        IsEventFinished = outcome is { } item && item.IsFinished;
        IsSessionOpen = outcome is { } session && session.IsSessionOpen;
        IsSessionRunning = outcome is { } running && running.IsRunning;
        IsSessionPaused = outcome is { } held && held.IsPaused;

        // The card's half of this: when the stopwatch was set going, and nothing else.
        TrackingStartedAtText = outcome is { StartedAtUtc: { } startedAt, IsFinished: false }
            ? localization.Format("TrackedStarted",
                ClockText.Format(startedAt, Use24HourClock, localization.CurrentCulture))
            : string.Empty;
        MonitorStateText = outcome switch
        {
            { IsPaused: true } => localization.Text("TrackPaused"),
            { IsRunning: true } => localization.Text("TrackRunning"),
            _ => localization.Text("MonitorIdle")
        };
        TrackingStateText = outcome switch
        {
            null => string.Empty,
            { Kind: EventOutcomeKind.Completed } done =>
                localization.Format("TrackedDone", ScheduleText.Duration(TimeSpan.FromMinutes(done.ActualMinutes), localization)),
            // A partial verdict now carries the one figure the session could not measure, so
            // the chip says how far it got rather than only how long it ran.
            { Kind: EventOutcomeKind.Partial, ProgressPercent: > 0 } scored =>
                localization.Format("TrackedPartialProgress", scored.ProgressPercent,
                    ScheduleText.Duration(TimeSpan.FromMinutes(scored.ActualMinutes), localization)),
            { Kind: EventOutcomeKind.Partial } partial =>
                localization.Format("TrackedPartial", ScheduleText.Duration(TimeSpan.FromMinutes(partial.ActualMinutes), localization)),
            { Kind: EventOutcomeKind.Skipped } => localization.Text("TrackedSkipped"),
            // A held session says so. It used to read "started at 14:05" whether or not the
            // stopwatch had been moving for the last half hour.
            { IsPaused: true } => localization.Text("TrackPaused"),
            { StartedAtUtc: { } started } => localization.Format("TrackedStarted",
                ClockText.Format(started, Use24HourClock, localization.CurrentCulture)),
            _ => string.Empty
        };
        RefreshTrackingTimer();
        // Pressing start swaps the countdown for a stopwatch; that should not wait for the tick.
        RefreshPeekEvent();
    }

    /// <summary>
    /// The live session, driven by the shell's existing one-second clock. Three readings come
    /// out of it: the work done, the time stood still, and the plan set beside the fact. The
    /// last of those is why the timer is never stopped at the planned end - a session that runs
    /// long is a measurement worth keeping, not a rule being broken.
    /// </summary>
    private void RefreshTrackingTimer()
    {
        var outcome = _trackedOutcome;
        if (outcome is not { StartedAtUtc: not null } || outcome.IsFinished)
        {
            if (TrackingElapsedText != "00:00:00") TrackingElapsedText = "00:00:00";
            if (TrackingPauseText.Length > 0) TrackingPauseText = string.Empty;
            if (TrackingPlanActualText.Length > 0) TrackingPlanActualText = string.Empty;
            if (IsTrackingOverPlan) IsTrackingOverPlan = false;
            if (IsLongPause) IsLongPause = false;
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var worked = outcome.WorkedAt(now);
        TrackingElapsedText = ClockSpan(worked);

        var paused = outcome.PausedFor(now);
        TrackingPauseText = paused < TimeSpan.FromSeconds(30)
            ? string.Empty
            : localization.Format("TrackPausedFor", ScheduleText.Duration(paused, localization));

        var planned = _trackedEvent is { } source
            ? source.EndUtc - source.StartUtc
            : TimeSpan.FromMinutes(outcome.PlannedMinutes);
        TrackingPlanActualText = planned <= TimeSpan.Zero
            ? string.Empty
            : localization.Format("TrackPlanActual",
                ScheduleText.Duration(planned, localization),
                ScheduleText.Duration(worked, localization));
        IsTrackingOverPlan = planned > TimeSpan.Zero && worked > planned;

        // The only question a pause is ever allowed to ask, and only once it has gone on long
        // enough that the answer is genuinely unknown to the application.
        var currentPause = outcome.CurrentPauseFor(now);
        var isLong = currentPause >= LongPause;
        if (isLong) LongPauseText = localization.Format("PausePromptBody",
            ScheduleText.Duration(currentPause, localization));
        if (IsLongPause != isLong) IsLongPause = isLong;
    }

    /// <summary>Command behind the widget's own button; the shell window listens for this.</summary>
    [RelayCommand]
    private void OpenShellFromPeek() => ShellRevealRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Asks the application to bring the main panel forward.</summary>
    public event EventHandler? ShellRevealRequested;

    /// <summary>
    /// The row as it stands, ready to be changed. Every write goes through this so that the
    /// parts nobody is touching - the moment start was pressed, the seconds already paused -
    /// survive a write that is only about the verdict, and vice versa.
    /// </summary>
    private EventOutcome DraftOutcome(CalendarEvent source)
    {
        var existing = _dayOutcomes.GetValueOrDefault(source.Id);
        return new EventOutcome
        {
            Id = existing?.Id ?? Guid.NewGuid(),
            EventId = source.Id,
            Kind = existing?.Kind ?? EventOutcomeKind.Started,
            // Copied in rather than joined, so the history survives the event being edited.
            PlannedStartUtc = source.StartUtc,
            PlannedMinutes = Math.Max(0, (int)Math.Round((source.EndUtc - source.StartUtc).TotalMinutes)),
            StartedAtUtc = existing?.StartedAtUtc,
            FinishedAtUtc = existing?.FinishedAtUtc,
            ActualMinutes = existing?.ActualMinutes ?? 0,
            Note = existing?.Note,
            PausedSeconds = existing?.PausedSeconds ?? 0,
            PausedAtUtc = existing?.PausedAtUtc,
            ProgressPercent = existing?.ProgressPercent ?? 0
        };
    }

    /// <summary>
    /// Minutes actually worked. The session's own measurement when there was a session, with
    /// its pauses already taken out; otherwise the planned length, which is the honest default
    /// for marking something long after the fact.
    /// </summary>
    private int WorkedMinutes(CalendarEvent source)
    {
        var planned = Math.Max(1, (int)Math.Round((source.EndUtc - source.StartUtc).TotalMinutes));
        if (_dayOutcomes.GetValueOrDefault(source.Id) is not { StartedAtUtc: not null } outcome) return planned;
        var worked = (int)Math.Round(outcome.WorkedAt(DateTimeOffset.UtcNow).TotalMinutes);
        return Math.Clamp(worked, 1, Math.Max(planned * 6, 1));
    }

    // ---------------------------------------------------------------- the one write path

    /// <summary>
    /// Writes a verdict for any event, past or present, and refreshes everything that shows it.
    /// A verdict also closes whatever session was open: a finished event has no stopwatch.
    /// </summary>
    private async Task RecordOutcomeAsync(CalendarEvent source, EventOutcomeKind kind,
        int? actualMinutes = null, int? progressPercent = null)
    {
        var minutes = kind switch
        {
            EventOutcomeKind.Started or EventOutcomeKind.Skipped => 0,
            EventOutcomeKind.Partial => Math.Max(1, actualMinutes ?? WorkedMinutes(source)),
            _ => actualMinutes ?? WorkedMinutes(source)
        };

        var now = DateTimeOffset.UtcNow;
        var draft = DraftOutcome(source);
        draft.Kind = kind;
        draft.ActualMinutes = minutes;

        if (kind == EventOutcomeKind.Started)
        {
            // A fresh session, not a resumed one: pressing start again is starting again.
            draft.StartedAtUtc = now;
            draft.FinishedAtUtc = null;
            draft.PausedSeconds = 0;
            draft.PausedAtUtc = null;
            draft.ProgressPercent = 0;
        }
        else
        {
            draft.FinishedAtUtc = now;
            // Whatever pause was open ends here, so it is banked rather than left to grow.
            if (draft.PausedAtUtc is { } pausedAt)
            {
                draft.PausedSeconds += Math.Max(0, (int)Math.Round((now - pausedAt).TotalSeconds));
                draft.PausedAtUtc = null;
            }
            draft.ProgressPercent = progressPercent ?? kind switch
            {
                EventOutcomeKind.Completed => 100,
                EventOutcomeKind.Skipped => 0,
                _ => draft.ProgressPercent
            };
        }

        await PersistOutcomeAsync(source, draft, "Recording an event outcome");

        // "Done" said of an event with steps is said of its steps: what was left is ticked, with
        // the verdict's own stamp so that taking the verdict back takes exactly these back too.
        if (kind == EventOutcomeKind.Completed && _dayOutcomes.GetValueOrDefault(source.Id) is { Kind: EventOutcomeKind.Completed })
        {
            try
            {
                await TickRestOfSittingAsync(source, now);
                await ReloadProjectsAsync();
            }
            catch (Exception exception)
            {
                crashReporter.Record(exception, "Ticking a finished event's steps");
            }
        }
    }

    /// <summary>Writes a session change - start, hold, release - without touching the verdict.</summary>
    private async Task WriteSessionAsync(CalendarEvent source, Action<EventOutcome> change)
    {
        var draft = DraftOutcome(source);
        change(draft);
        await PersistOutcomeAsync(source, draft, "Updating a work session");
    }

    private async Task PersistOutcomeAsync(CalendarEvent source, EventOutcome draft, string context)
    {
        try
        {
            var stored = await outcomeRepository.RecordAsync(draft);
            _dayOutcomes[source.Id] = stored;
            if (_trackedEvent?.Id == source.Id) _trackedOutcome = stored;
            // A verdict is an answer, whichever button wrote it - the row's own "done" as much
            // as the sheet's. The question about this event is over, and the tray it was being
            // asked in goes with it; leaving it standing would keep the widget's strip up and
            // hold off the day's own reload.
            //
            // So is setting the clock going again. "How did it go?" is raised by a session
            // ending, and a session that has been released - or started over - has not ended:
            // the question was about a session that no longer exists in that state, and it used
            // to stay on the screen underneath a stopwatch that was counting again, asking the
            // user how something they were still doing had gone.
            if (_sessionReviewTarget?.Id == source.Id && (stored.IsFinished || stored.IsRunning))
                CloseSessionReview();
            ApplyDayOutcomes();
            RetargetTracking();
            RefreshDayProgress();
            await RefreshWrapUpAsync();
            // The row answers at once, so the project it was booked for does too: "done" on a
            // step's slot ticks the step, rather than a minute later when the day next reloads.
            // An event's own checklist follows every change of answer: whether what it left can
            // be carried on depends on how it went.
            await ReloadProjectsAsync();
            if (IsAnalyticsOpen) await RefreshAnalyticsAsync();
        }
        catch (Exception exception)
        {
            crashReporter.Record(exception, context);
            Status = exception.Message;
        }
    }

    /// <summary>Removes a verdict entirely, returning the event to "not reported".</summary>
    private async Task ClearOutcomeAsync(CalendarEvent source)
    {
        try
        {
            var previous = _dayOutcomes.GetValueOrDefault(source.Id);
            await outcomeRepository.ClearAsync(source.Id);
            await UntickVerdictAsync(source, previous);
            _dayOutcomes.Remove(source.Id);
            if (_trackedEvent?.Id == source.Id) _trackedOutcome = null;
            if (_sessionReviewTarget?.Id == source.Id) CloseSessionReview();
            ApplyDayOutcomes();
            RetargetTracking();
            RefreshDayProgress();
            await RefreshWrapUpAsync();
            await ReloadProjectsAsync();
            if (IsAnalyticsOpen) await RefreshAnalyticsAsync();
        }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Clearing an event outcome");
            Status = exception.Message;
        }
    }

    // ---------------------------------------------------------------- the session

    /// <summary>
    /// Holds the stopwatch. Deliberately silent: the work and the pauses are two separate
    /// numbers, so stepping away costs nothing and there is nothing the application needs to
    /// be told. Only a pause that goes on past <see cref="LongPause"/> says anything at all.
    /// </summary>
    private Task PauseSessionAsync(CalendarEvent source)
        => _dayOutcomes.GetValueOrDefault(source.Id) is { IsRunning: true }
            ? WriteSessionAsync(source, draft => draft.PausedAtUtc = DateTimeOffset.UtcNow)
            : Task.CompletedTask;

    /// <summary>Releases it, banking the pause that just ended.</summary>
    private Task ResumeSessionAsync(CalendarEvent source)
    {
        if (_dayOutcomes.GetValueOrDefault(source.Id) is not { IsPaused: true }) return Task.CompletedTask;
        var now = DateTimeOffset.UtcNow;
        return WriteSessionAsync(source, draft =>
        {
            if (draft.PausedAtUtc is { } pausedAt)
                draft.PausedSeconds += Math.Max(0, (int)Math.Round((now - pausedAt).TotalSeconds));
            draft.PausedAtUtc = null;
        });
    }

    /// <summary>
    /// Ends the session and asks the one question. The clock is held rather than discarded, so
    /// dismissing the question loses nothing: the session is still there, still measured, and
    /// the rail offers to resume it.
    /// </summary>
    private async Task StopSessionAsync(CalendarEvent source)
    {
        if (_dayOutcomes.GetValueOrDefault(source.Id) is not { IsSessionOpen: true }) return;
        if (_dayOutcomes.GetValueOrDefault(source.Id) is { IsRunning: true })
            await WriteSessionAsync(source, draft => draft.PausedAtUtc = DateTimeOffset.UtcNow);
        OpenSessionReview(source);
    }

    // ---------------------------------------------------------------- "How did it go?"

    /// <summary>
    /// The question has been raised, and whoever can put a surface under it should. Announced as
    /// an event rather than left to the flag below, because the flag only says whether the
    /// question is open and the question can be raised again while it already is: a session
    /// stopped from the rail beside the petal opens the widget to ask, and closing that widget
    /// without answering leaves the flag standing. The second press would then change nothing
    /// and open nothing — which is exactly what it looked like.
    /// </summary>
    public event EventHandler? SessionReviewRaised;

    /// <summary>Puts the question on screen for one event, at whichever step it needs.</summary>
    private void OpenSessionReview(CalendarEvent source, bool detailStep = false)
    {
        _sessionReviewTarget = source;
        OnPropertyChanged(nameof(SessionReviewEventId));
        SessionReviewTitle = source.Title;
        SessionReviewProgress = _dayOutcomes.GetValueOrDefault(source.Id) is { ProgressPercent: > 0 } scored
            ? scored.ProgressPercent
            : 50;
        SessionReviewNextStep = string.Empty;
        LoadSessionSteps(_dayFollowUps.GetValueOrDefault(source.Id)?.NextStep);

        // An event with steps already knows how far it got, and what is left of it is its open
        // steps: the figure is offered from the ticks, and the list is the checklist under the row,
        // which is opened for it rather than written out a second time.
        IsSessionReviewSitting = false;
        if (SittingProjectFor(source) is { } project && SittingOf(source, project) is { TotalSteps: > 0 } sitting)
        {
            IsSessionReviewSitting = true;
            if (sitting.ProgressPercent > 0) SessionReviewProgress = Math.Clamp(sitting.ProgressPercent, 1, 100);
            if (_sittings.TryGetValue(source.Id, out var card))
            {
                card.OpenChosen = true;
                card.IsOpen = true;
            }
        }

        var planned = source.EndUtc - source.StartUtc;
        // The session's own figure where there was a session, so the sheet and the "plan vs
        // actual" line above it never disagree by the minute that recording rounds up to.
        var worked = _dayOutcomes.GetValueOrDefault(source.Id) is { StartedAtUtc: not null } measured
            ? measured.WorkedAt(DateTimeOffset.UtcNow)
            : TimeSpan.FromMinutes(WorkedMinutes(source));
        SessionReviewMeasuredText = planned > TimeSpan.Zero
            ? localization.Format("SessionMeasured",
                ScheduleText.Duration(worked, localization), ScheduleText.Duration(planned, localization))
            : localization.Format("SessionMeasuredNoPlan", ScheduleText.Duration(worked, localization));

        IsSessionReviewDetailStep = detailStep;
        IsSessionReviewOpen = true;
        ApplyReviewToRows();
        ReviewRowRequested?.Invoke(this, source.Id);
        // Raised every time, whether or not the flag above actually changed. See the event.
        SessionReviewRaised?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Asks the shell to bring the row now being asked about into view. The question is drawn
    /// in that row's tray, and the button that most often raises it is at the foot of the
    /// window - so without this, stopping a session halfway down a long day opens a question
    /// below the fold, which is a question nobody was asked.
    /// </summary>
    public event EventHandler<Guid>? ReviewRowRequested;

    /// <summary>
    /// Puts the question away, and the tray it was being asked in with it. Answering is the
    /// usual way here - the tray has done its job the moment a verdict is written - and so is
    /// "not now" from the widget, which means the same thing about the tray.
    /// </summary>
    private void CloseSessionReview()
    {
        if (_sessionReviewTarget is { } asked)
            foreach (var row in Agenda.Where(item => item.Event?.Id == asked.Id))
            {
                row.IsReviewDetailOpen = false;
                row.IsTrackingOpen = false;
            }
        _sessionReviewTarget = null;
        OnPropertyChanged(nameof(SessionReviewEventId));
        IsSessionReviewOpen = false;
        IsSessionReviewDetailStep = false;
        IsSessionReviewSitting = false;
        SessionReviewNextStep = string.Empty;
        SessionReviewSteps.Clear();
        OnPropertyChanged(nameof(HasSessionReviewSteps));
        ApplyReviewToRows();
    }

    /// <summary>
    /// Puts the question in the one place it belongs: the tray of the row it is about, which is
    /// already the place a person goes to say what happened to an event. It used to be drawn on
    /// the day card as well, so the same three answers existed twice on one screen and the card
    /// grew a sheet every time a stopwatch was stopped. The tray opens itself for the row the
    /// question is about, and only that row shows the second step.
    /// </summary>
    private void ApplyReviewToRows()
    {
        var target = _sessionReviewTarget?.Id;
        foreach (var row in Agenda)
        {
            if (row.Event is not { } source) continue;
            var isTarget = target == source.Id;
            row.IsReviewDetailOpen = isTarget && IsSessionReviewDetailStep;
            if (!isTarget || !IsSessionReviewOpen) continue;
            row.IsTrackingOpen = true;
            // A question asked inside a folded-up day list is a question nobody can see. The
            // widget and the dock can both raise it, and neither of them knows what the shell
            // happens to be showing.
            IsScheduleExpanded = true;
        }
    }

    /// <summary>Writes the continuation point beside the verdict, and drops it when it is blank.</summary>
    private async Task SaveNextStepAsync(CalendarEvent source, string? nextStep)
    {
        try
        {
            var stored = await followUpRepository.SetNextStepAsync(source.Id, nextStep);
            _dayFollowUps[source.Id] = stored;
            if (_trackedEvent?.Id == source.Id) _trackedFollowUp = stored;
            ApplyDayOutcomes();
        }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Saving a next step");
            Status = exception.Message;
        }
    }

    // ---------------------------------------------------------------- day card commands

    [RelayCommand]
    private Task StartEventAsync()
        => _trackedEvent is { } source ? RecordOutcomeAsync(source, EventOutcomeKind.Started) : Task.CompletedTask;

    [RelayCommand]
    private Task PauseEventAsync()
        => _trackedEvent is { } source ? PauseSessionAsync(source) : Task.CompletedTask;

    [RelayCommand]
    private Task ResumeEventAsync()
        => _trackedEvent is { } source ? ResumeSessionAsync(source) : Task.CompletedTask;

    [RelayCommand]
    private Task StopEventAsync()
        => _trackedEvent is { } source ? StopSessionAsync(source) : Task.CompletedTask;

    /// <summary>
    /// The same from the widget. The widget has room for three buttons and a verdict, not for
    /// a progress question and a line of prose, so this brings the shell forward and asks there.
    /// </summary>
    [RelayCommand]
    private void BeginPartialEntryForPeek()
    {
        if (_trackedEvent is not { } source) return;
        OpenSessionReview(source, detailStep: true);
        ShellRevealRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private Task UndoTrackingAsync()
        => _trackedEvent is { } source ? ClearOutcomeAsync(source) : Task.CompletedTask;

    // ---------------------------------------------------------------- the sheet's own buttons

    [RelayCommand]
    private async Task AnswerSessionDoneAsync()
    {
        if (_sessionReviewTarget is not { } source) return;
        CloseSessionReview();
        await RecordOutcomeAsync(source, EventOutcomeKind.Completed);
    }

    [RelayCommand]
    private async Task AnswerSessionMissedAsync()
    {
        if (_sessionReviewTarget is not { } source) return;
        CloseSessionReview();
        await RecordOutcomeAsync(source, EventOutcomeKind.Skipped);
    }

    /// <summary>"Partly" - the only answer that needs a second step.</summary>
    [RelayCommand]
    private void AnswerSessionPartial()
    {
        IsSessionReviewDetailStep = true;
        ApplyReviewToRows();
    }

    [RelayCommand]
    private void SetSessionProgress(string? percent)
    {
        if (int.TryParse(percent, out var parsed)) SessionReviewProgress = Math.Clamp(parsed, 1, 100);
    }

    /// <summary>
    /// A step either way for the last few percent, where dragging is clumsier than pressing.
    /// The slider beside it covers the coarse move; between the two, any whole number is
    /// reachable, which is the point of having dropped the presets.
    /// </summary>
    [RelayCommand]
    private void NudgeSessionProgress(string? step)
    {
        if (int.TryParse(step, out var parsed))
            SessionReviewProgress = Math.Clamp(SessionReviewProgress + parsed, 1, 100);
    }

    /// <summary>Reads a stored note back into the list it was written as.</summary>
    private void LoadSessionSteps(string? stored)
    {
        SessionReviewSteps.Clear();
        foreach (var line in AgendaItemViewModel.SplitNextStep(stored))
            SessionReviewSteps.Add(new SessionStepViewModel(line, RemoveSessionStep));
        OnPropertyChanged(nameof(HasSessionReviewSteps));
    }

    /// <summary>Moves the line being typed onto the list. Enter does this, and so does the button.</summary>
    [RelayCommand]
    private void AddSessionStep()
    {
        var line = SessionReviewNextStep.Trim();
        if (line.Length == 0) return;
        SessionReviewSteps.Add(new SessionStepViewModel(line, RemoveSessionStep));
        SessionReviewNextStep = string.Empty;
        OnPropertyChanged(nameof(HasSessionReviewSteps));
    }

    private void RemoveSessionStep(SessionStepViewModel step)
    {
        SessionReviewSteps.Remove(step);
        OnPropertyChanged(nameof(HasSessionReviewSteps));
    }

    [RelayCommand]
    private async Task SaveSessionPartialAsync()
    {
        if (_sessionReviewTarget is not { } source) return;
        if (IsSessionReviewSitting && SittingProjectFor(source) is { } project)
        {
            // The checklist is the list of what is left, so nothing is written beside it — and a
            // checklist ticked to the end while the question was open was finished, not partly.
            var sitting = SittingOf(source, project);
            var percent = (int)Math.Round(SessionReviewProgress);
            CloseSessionReview();
            if (sitting.IsComplete) await RecordOutcomeAsync(source, EventOutcomeKind.Completed);
            else await RecordOutcomeAsync(source, EventOutcomeKind.Partial, progressPercent: Math.Clamp(percent, 1, 99));
            return;
        }

        // Whatever is still in the box counts. Making the user press "add" for the one line
        // they typed would be the application asking to be told twice.
        AddSessionStep();
        var progress = (int)Math.Round(SessionReviewProgress);
        var nextStep = string.Join('\n', SessionReviewSteps.Select(step => step.Text));
        CloseSessionReview();
        await RecordOutcomeAsync(source, EventOutcomeKind.Partial, progressPercent: progress);
        await SaveNextStepAsync(source, nextStep);
    }

    /// <summary>Puts the question away without answering it. The session is held, not lost.</summary>
    [RelayCommand]
    private void DismissSessionReview() => CloseSessionReview();

    // ---------------------------------------------------------------- the long pause

    [RelayCommand]
    private Task KeepGoingAsync()
        => _trackedEvent is { } source ? ResumeSessionAsync(source) : Task.CompletedTask;

    [RelayCommand]
    private void EndPausedSession()
    {
        if (_trackedEvent is { } source) OpenSessionReview(source);
    }

    // ---------------------------------------------------------------- list rows

    private Task RecordRowOutcomeAsync(AgendaItemViewModel row, EventOutcomeKind kind)
        => row.Event is { } source ? RecordOutcomeAsync(source, kind) : Task.CompletedTask;

    private Task ClearRowOutcomeAsync(AgendaItemViewModel row)
        => row.Event is { } source ? ClearOutcomeAsync(source) : Task.CompletedTask;

    private Task BeginRowPartialEntryAsync(AgendaItemViewModel row)
    {
        if (row.Event is { } source) OpenSessionReview(source, detailStep: true);
        return Task.CompletedTask;
    }

    /// <summary>Closing a row's tray puts away the question that was being asked inside it.</summary>
    private void DismissRowReview(AgendaItemViewModel row)
    {
        if (row.Event is { } source && _sessionReviewTarget?.Id == source.Id) CloseSessionReview();
    }

    /// <summary>Folds the row's "what is left" card away, or brings it back.</summary>
    private void ToggleRowNextStep(AgendaItemViewModel row)
    {
        if (row.Event is not { } source) return;
        if (!_foldedNextSteps.Add(source.Id)) _foldedNextSteps.Remove(source.Id);
        row.IsNextStepFolded = _foldedNextSteps.Contains(source.Id);
    }

    /// <summary>Rolls the card up to its own heading, or lets the lines back out.</summary>
    private void ToggleRowNextStepCollapsed(AgendaItemViewModel row)
    {
        if (row.Event is not { } source) return;
        if (!_collapsedNextSteps.Add(source.Id)) _collapsedNextSteps.Remove(source.Id);
        row.IsNextStepCollapsed = _collapsedNextSteps.Contains(source.Id);
    }

    private Task RunRowSessionAsync(AgendaItemViewModel row, RowSessionAction action)
        => row.Event is not { } source ? Task.CompletedTask : action switch
        {
            RowSessionAction.Pause => PauseSessionAsync(source),
            RowSessionAction.Resume => ResumeSessionAsync(source),
            _ => StopSessionAsync(source)
        };

    /// <summary>Hands a freshly built row the callbacks that let it run and record its own session.</summary>
    private void AttachRowTracking(AgendaItemViewModel row)
        => row.AttachTracking(RecordRowOutcomeAsync, BeginRowPartialEntryAsync, ClearRowOutcomeAsync,
            RunRowSessionAsync, DismissRowReview, ToggleRowNextStep, ToggleRowNextStepCollapsed,
            localization);

    // ---------------------------------------------------------------- day state

    /// <summary>
    /// Loads every verdict for the events currently listed, keyed by event id, and beside them
    /// the follow-ups: where each one is to be picked up, and how often it has been pushed.
    /// </summary>
    private async Task LoadDayOutcomesAsync(IReadOnlyList<CalendarEvent> events)
    {
        _dayOutcomes.Clear();
        _dayFollowUps.Clear();
        if (events.Count == 0) return;
        var ids = events.Select(item => item.Id).ToList();
        try
        {
            foreach (var outcome in await outcomeRepository.GetForEventsAsync(ids))
                _dayOutcomes[outcome.EventId] = outcome;
            foreach (var followUp in await followUpRepository.GetForEventsAsync(ids))
                _dayFollowUps[followUp.EventId] = followUp;
        }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Loading day outcomes");
        }
    }

    /// <summary>Re-labels the day list so every row shows its recorded verdict.</summary>
    private void ApplyDayOutcomes()
    {
        var now = DateTimeOffset.UtcNow;
        var unmarked = 0;
        foreach (var item in Agenda)
        {
            if (item.Event is not { } source) continue;
            var outcome = _dayOutcomes.GetValueOrDefault(source.Id);
            item.ApplyOutcome(outcome, _dayFollowUps.GetValueOrDefault(source.Id), localization);
            item.IsNextStepFolded = _foldedNextSteps.Contains(source.Id);
            item.IsNextStepCollapsed = _collapsedNextSteps.Contains(source.Id);
            // Merely pressing "start" is not a verdict, so an event left running still counts.
            if (source.EndUtc <= now && outcome is not { IsFinished: true }) unmarked++;
        }

        UnmarkedEventCount = unmarked;
        UnmarkedEventsText = unmarked == 0 ? string.Empty : localization.Format("UnmarkedEvents", unmarked);
        // The list was just rebuilt, so the row that is being asked about has to be told again.
        ApplyReviewToRows();
        RefreshMascotMood();
    }

    /// <summary>
    /// Re-derives the day bar from the verdicts now in memory. Marking something used to leave
    /// the bar showing the figure from the last reload, which is up to a minute stale — long
    /// enough for pressing "done" to look like it did nothing.
    /// </summary>
    private void RefreshDayProgress()
    {
        var review = _dayReview;
        HasDayRecord = review.Answered > 0;
        Progress = review.PlannedMinutes == 0 ? 0 : review.RecordedMinutes * 100d / review.PlannedMinutes;
        ProgressText = review.PlannedMinutes == 0 ? "—" : $"{Progress:0}%";
    }

    /// <summary>Minutes of the day that ended up recorded as done, used for the day progress.</summary>
    private int RecordedMinutes(IReadOnlyList<CalendarEvent> events)
    {
        var total = 0;
        foreach (var item in events)
        {
            if (!_dayOutcomes.TryGetValue(item.Id, out var outcome) || !outcome.IsFinished) continue;
            total += outcome.Kind switch
            {
                EventOutcomeKind.Completed => Math.Max(outcome.ActualMinutes,
                    (int)Math.Round((item.EndUtc - item.StartUtc).TotalMinutes)),
                EventOutcomeKind.Partial => Math.Max(0, outcome.ActualMinutes),
                _ => 0
            };
        }
        return total;
    }
}

/// <summary>One coloured count in the day card's review strip.</summary>
public sealed class DayReviewChipViewModel(string text, IBrush accent, bool isStrong)
{
    public string Text { get; } = text;
    public IBrush Accent { get; } = accent;
    /// <summary>Recorded verdicts read louder than the "nobody said" counts beside them.</summary>
    public bool IsStrong { get; } = isStrong;
}
