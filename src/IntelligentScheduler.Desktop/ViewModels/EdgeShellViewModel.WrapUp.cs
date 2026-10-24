using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IntelligentScheduler.Application;
using IntelligentScheduler.Domain;

namespace IntelligentScheduler.Desktop.ViewModels;

/// <summary>
/// The end of the day, asked about once.
///
/// Everything else in the application answers for one event at the moment it ends. That is the
/// right shape while the day is running and the wrong shape when it is over: three separate
/// prompts for three forgotten events is how a person learns to dismiss prompts. So whatever
/// the day left unanswered is gathered here, on one screen, with the application's own reading
/// of each line already filled in — a timer that ran means "done", a slot nobody ever started
/// means "move it" — and answering the lot is a tap each, or a single tap for all of them.
///
/// It lives inside the assistant panel and has nothing to do with the assistant: no model is
/// consulted, no message is sent, and it works identically with every AI feature switched off.
/// The panel is simply where the application's own drawers live.
///
/// The offer to open it backs off on its own. A wrap-up dismissed without a single answer is
/// counted, and the count widens the gap before the next offer — daily, then every other day,
/// then weekly. The drawer itself never goes away: the badge in the assistant's header is
/// always there to be pressed, so backing off costs the user nothing.
/// </summary>
public sealed partial class EdgeShellViewModel
{
    /// <summary>
    /// The rows of this visit, kept by event so the list survives its own rebuild. Two reasons
    /// they cannot simply be recreated: the day reloads every minute, and rows recreated under
    /// a reader would restart their entrance animation on the minute; and a row answered for is
    /// no longer "unanswered", so without somewhere to live its confirmation line would be
    /// swept off the screen by its own success, leaving a mis-tap nothing to point at.
    /// </summary>
    private readonly Dictionary<Guid, WrapUpItemViewModel> _wrapUpRows = [];

    private bool _wrapUpRefreshing;

    /// <summary>
    /// How much of <see cref="WrapUpPendingCount"/> belongs to today. The badge counts
    /// everything the drawer can settle; the offer that appears under the day card counts only
    /// the day it is offering to wrap up.
    /// </summary>
    private int _wrapUpTodayPendingCount;

    public ObservableCollection<WrapUpItemViewModel> WrapUpItems { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowChatMain))]
    [NotifyPropertyChangedFor(nameof(ShowChatIntro))]
    private bool _isWrapUpOpen;

    /// <summary>How many of today's events have finished with nothing recorded against them.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWrapUpBadge))]
    [NotifyPropertyChangedFor(nameof(WrapUpBadgeText))]
    private int _wrapUpPendingCount;

    [ObservableProperty] private string _wrapUpCountText = string.Empty;

    /// <summary>The quiet strip under the day card that offers to run the wrap-up.</summary>
    [ObservableProperty] private bool _isWrapUpOfferVisible;

    [ObservableProperty] private string _wrapUpOfferText = string.Empty;

    public bool HasWrapUpBadge => WrapUpPendingCount > 0;
    public string WrapUpBadgeText => WrapUpPendingCount.ToString(localization.CurrentCulture);
    public bool HasWrapUpRows => WrapUpItems.Count > 0;

    /// <summary>
    /// Whether anything is still waiting on one of the three answers. A line that has moved on
    /// to "what is in the way?" is unfinished but has no suggestion left to accept, so the
    /// bulk button stands down for it rather than offering to answer a different question.
    /// </summary>
    public bool HasWrapUpSuggestions => WrapUpItems.Any(row => row.ShowActions);

    // ---------------------------------------------------------------- opening and closing

    [RelayCommand]
    private void ToggleWrapUp()
    {
        IsChatHistoryOpen = false;
        IsMemoryPanelOpen = false;
        IsWrapUpOpen = !IsWrapUpOpen;
        if (!IsWrapUpOpen) return;
        // A fresh visit starts from what is actually open, not from last visit's receipts.
        _wrapUpRows.Clear();
        IsWrapUpOfferVisible = false;
        _ = RefreshWrapUpAsync();
    }

    /// <summary>The offer's own button: open the assistant panel on the wrap-up drawer.</summary>
    [RelayCommand]
    private void OpenWrapUp()
    {
        IsWrapUpOfferVisible = false;
        _wrapUpRows.Clear();
        IsChatHistoryOpen = false;
        IsMemoryPanelOpen = false;
        IsWrapUpOpen = true;
        if (!IsChatOpen) ToggleChatCommand.Execute(null);
        _ = RefreshWrapUpAsync();
    }

    /// <summary>
    /// Put away without an answer. Counted, because a prompt that is never used should ask
    /// less often rather than go on asking at the same volume until it is switched off.
    /// </summary>
    [RelayCommand]
    private void DismissWrapUpOffer()
    {
        IsWrapUpOfferVisible = false;
        if (_settings is null) return;
        _settings.WrapUpIgnoredStreak = Math.Min(99, _settings.WrapUpIgnoredStreak + 1);
        _settings.WrapUpLastOfferedUtc = DateTimeOffset.UtcNow;
        ScheduleSettingsSave();
    }

    /// <summary>Any answer at all clears the streak: the prompt is being used, so it may ask again.</summary>
    private void NoteWrapUpAnswered()
    {
        if (_settings is null || _settings.WrapUpIgnoredStreak == 0) return;
        _settings.WrapUpIgnoredStreak = 0;
        ScheduleSettingsSave();
    }

    // ---------------------------------------------------------------- building the list

    /// <summary>
    /// How far back the drawer looks for events nobody ever answered for.
    ///
    /// It used to look at today and nothing else, and the consequence was the opposite of what
    /// the drawer is for: an event missed on a Tuesday was asked about once, that evening, and
    /// then never again — by Wednesday it had fallen out of the only screen that could still
    /// settle it, and it stayed unanswered for the life of the calendar. Three weeks is long
    /// enough that a fortnight away from the application still comes back to a full account of
    /// what is open, and short enough that the answer to a question this old is still known.
    /// </summary>
    private static readonly int WrapUpBacklogDays = 21;

    /// <summary>
    /// The most rows an earlier day may contribute at once. Today is never cut — it is the day
    /// being wrapped up — but a calendar that has gone unanswered for weeks would otherwise
    /// open as a wall of a hundred rows, which is a list nobody starts. Oldest first, so what
    /// the cap defers is the most recent, and the next visit brings the rest.
    /// </summary>
    private const int WrapUpBacklogLimit = 40;

    /// <summary>
    /// Rebuilds the list: everything that has finished and carries no result, from the last
    /// three weeks up to this moment. It reads the calendar directly rather than the agenda on
    /// screen, so browsing last Tuesday does not empty the badge.
    /// </summary>
    public async Task RefreshWrapUpAsync()
    {
        if (_wrapUpRefreshing) return;
        _wrapUpRefreshing = true;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var today = DateOnly.FromDateTime(DateTime.Today);
            var (todayStart, end) = DayRange(today);
            var (start, _) = DayRange(today.AddDays(-WrapUpBacklogDays));
            var events = (await calendarRepository.GetRangeAsync(start, end))
                .OrderBy(item => item.StartUtc).ToList();

            var outcomes = new Dictionary<Guid, EventOutcome>();
            var followUps = new Dictionary<Guid, EventFollowUp>();
            if (events.Count > 0)
            {
                var ids = events.Select(item => item.Id).ToList();
                foreach (var outcome in await outcomeRepository.GetForEventsAsync(ids))
                    outcomes[outcome.EventId] = outcome;
                foreach (var followUp in await followUpRepository.GetForEventsAsync(ids))
                    followUps[followUp.EventId] = followUp;
            }

            // Finished, and nothing recorded. Merely pressing start is not an answer, which is
            // exactly the case that used to slip through the day with nothing to show for it.
            var unanswered = events
                .Where(item => item.EndUtc <= now
                               && outcomes.GetValueOrDefault(item.Id) is not { IsFinished: true })
                .ToList();

            // Everything today, and the oldest of what is left over from before it. Split rather
            // than simply truncated: the cap is there to keep a long-neglected calendar from
            // opening as a wall, and it must never do that by dropping the day the user came
            // here to wrap up.
            var todayOpen = unanswered.Where(item => item.EndUtc >= todayStart).ToList();
            var backlog = unanswered.Where(item => item.EndUtc < todayStart)
                .Take(WrapUpBacklogLimit).ToList();
            var open = backlog.Concat(todayOpen).ToList();

            var rows = new List<WrapUpItemViewModel>();
            foreach (var item in open)
            {
                var outcome = outcomes.GetValueOrDefault(item.Id);
                // A row is reused unless the reading it was built on has moved underneath it -
                // which can only happen if a session was resumed after the event's slot ended.
                if (!_wrapUpRows.TryGetValue(item.Id, out var row) ||
                    (!row.IsResolved && row.HadActivity != HasSessionActivity(outcome)))
                {
                    row = BuildWrapUpRow(item, outcome, followUps.GetValueOrDefault(item.Id), today);
                    _wrapUpRows[item.Id] = row;
                }
                rows.Add(row);
            }

            // Receipts for rows that have just left the open set keep their place in the day -
            // but only while somebody is looking at them. The list is rebuilt every minute
            // whether the drawer is open or not, so kept unconditionally they would still be
            // here tomorrow, describing a day that has ended.
            foreach (var stale in _wrapUpRows.Values.ToList())
            {
                if (open.Any(item => item.Id == stale.EventId)) continue;
                var unfinished = stale.IsAskingBlocker || stale.IsOfferingRemedy;
                if (IsWrapUpOpen && (stale.IsResolved || unfinished)) rows.Add(stale);
                else _wrapUpRows.Remove(stale.EventId);
            }

            ApplyWrapUpRows([.. rows.OrderBy(row => row.StartUtc)]);

            WrapUpPendingCount = WrapUpItems.Count(row => !row.IsResolved);
            // What the unprompted offer is allowed to count. The offer is about the day that has
            // just finished; a backlog three weeks deep is not news and must not be what makes
            // the shell speak up this evening.
            _wrapUpTodayPendingCount = WrapUpItems.Count(row => !row.IsResolved && !row.IsFromEarlierDay);
            OnPropertyChanged(nameof(HasWrapUpSuggestions));
            // With nothing outstanding the banner goes back to naming the drawer: the empty
            // card underneath already says there is nothing left, and saying it twice, once in
            // each of two different type sizes, reads as a fault rather than as good news.
            WrapUpCountText = WrapUpPendingCount == 0
                ? localization.Text("WrapUpTitle")
                : localization.Format("WrapUpCount", WrapUpPendingCount);

            RefreshWrapUpOffer(events.Where(item => item.EndUtc >= todayStart).ToList(), now);
        }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Building the day wrap-up");
        }
        finally { _wrapUpRefreshing = false; }
    }

    /// <summary>
    /// Replaces the list only when it has actually changed. Clearing and refilling an
    /// observable collection every minute would restart every row's arrival animation and
    /// throw away the reader's place in the list, for no change at all on most minutes.
    /// </summary>
    private void ApplyWrapUpRows(IReadOnlyList<WrapUpItemViewModel> rows)
    {
        if (!WrapUpItems.SequenceEqual(rows))
        {
            WrapUpItems.Clear();
            foreach (var row in rows) WrapUpItems.Add(row);
            OnPropertyChanged(nameof(HasWrapUpRows));
        }
    }

    /// <summary>Whether a stopwatch ever ran against this event for long enough to count.</summary>
    private static bool HasSessionActivity(EventOutcome? outcome)
        => outcome?.StartedAtUtc is not null
           && outcome.WorkedAt(DateTimeOffset.UtcNow) >= TimeSpan.FromMinutes(1);

    private WrapUpItemViewModel BuildWrapUpRow(
        CalendarEvent source, EventOutcome? outcome, EventFollowUp? followUp, DateOnly today)
        => new(source, outcome, followUp, today, Use24HourClock, localization,
            ResolveWrapUpAsync, AnswerWrapUpBlockerAsync, ShortenWrapUpEventAsync,
            MoveWrapUpEventEarlierAsync, DropWrapUpEventAsync, SaveWrapUpFirstStepAsync);

    /// <summary>
    /// Whether to offer the wrap-up unprompted. Three things have to be true: the day has
    /// actually finished running, something is genuinely unanswered, and enough time has passed
    /// since the last offer — where "enough" widens every time an offer goes unused.
    /// </summary>
    private void RefreshWrapUpOffer(IReadOnlyList<CalendarEvent> today, DateTimeOffset now)
    {
        if (IsWrapUpOpen || _wrapUpTodayPendingCount == 0 || today.Any(item => item.EndUtc > now))
        {
            if (IsWrapUpOfferVisible) IsWrapUpOfferVisible = false;
            return;
        }

        // An offer already on screen only keeps its wording up to date. The gate below is
        // about raising one, and the stamp it reads is set the moment an offer appears - so
        // testing it against a standing offer would take that offer away a minute after it
        // arrived, which is the one thing a prompt must never do to somebody reading it.
        WrapUpOfferText = localization.Format("WrapUpOffer", _wrapUpTodayPendingCount);
        if (IsWrapUpOfferVisible) return;

        var streak = _settings?.WrapUpIgnoredStreak ?? 0;
        var quietDays = streak switch { <= 1 => 1, <= 3 => 2, <= 5 => 4, _ => 7 };
        if (_settings?.WrapUpLastOfferedUtc is { } stamp && (now - stamp).TotalDays < quietDays) return;

        IsWrapUpOfferVisible = true;
        if (_settings is null) return;
        // Stamped on sight, so one offer a day is exactly one whatever happens to it next.
        _settings.WrapUpLastOfferedUtc = now;
        ScheduleSettingsSave();
    }

    // ---------------------------------------------------------------- the three answers

    private async Task ResolveWrapUpAsync(WrapUpItemViewModel row, WrapUpAction action)
    {
        try
        {
            switch (action)
            {
                // Settled first, then written. Recording rebuilds the list, and a row that is
                // not yet marked answered would be swept out of it and put back a moment later.
                case WrapUpAction.Done:
                    Settle(row, localization.Text("WrapUpResolvedDone"));
                    await RecordOutcomeAsync(row.Event, EventOutcomeKind.Completed);
                    break;
                case WrapUpAction.Skip:
                    Settle(row, localization.Text("WrapUpResolvedSkipped"));
                    await RecordOutcomeAsync(row.Event, EventOutcomeKind.Skipped);
                    break;
                default:
                    await MoveWrapUpEventAsync(row);
                    break;
            }

            NoteWrapUpAnswered();
            await RefreshWrapUpAsync();
        }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Answering the day wrap-up");
            Status = exception.Message;
        }
    }

    /// <summary>
    /// Onto tomorrow, at the same time of day, carrying its continuation point with it. The
    /// stopwatch is cleared with the move: a session belongs to the day it was worked in, and
    /// leaving a half-finished one attached would make tomorrow open already started.
    /// </summary>
    private async Task MoveWrapUpEventAsync(WrapUpItemViewModel row)
    {
        var source = row.Event;
        // Tomorrow from here, not from the event. Adding a day to a slot the drawer has only
        // just caught up with would land it back in the past — nine days ago becomes eight — and
        // the row would still be sitting in the list, still unanswered, after being answered.
        var length = source.EndUtc - source.StartUtc;
        var local = source.StartUtc.ToLocalTime();
        var tomorrow = DateTime.Today.AddDays(1);
        var target = new DateTimeOffset(
            tomorrow.Year, tomorrow.Month, tomorrow.Day,
            local.Hour, local.Minute, local.Second,
            TimeZoneInfo.Local.GetUtcOffset(tomorrow));
        var landing = target + length;
        await calendarRepository.MoveEventsAsync(
            [new EventMove(source.Id, source.Title, source.StartUtc, source.EndUtc, target, landing)]);
        await outcomeRepository.ClearAsync(source.Id);
        _dayOutcomes.Remove(source.Id);

        var followUp = await followUpRepository.RecordDeferralAsync(source.Id);
        _dayFollowUps[source.Id] = followUp;
        source.StartUtc = target;
        source.EndUtc = landing;

        await ReloadDayAsync();

        // The second push is the first one that could be about the task rather than the week.
        if (followUp.NeedsBlockerAnswer)
        {
            _wrapUpRows[row.EventId] = row;
            row.AskBlocker(followUp.DeferCount);
            return;
        }

        Settle(row, localization.Format("WrapUpResolvedMoved",
            target.ToLocalTime().ToString("ddd, d MMM", localization.CurrentCulture)));
    }

    private void Settle(WrapUpItemViewModel row, string resolution)
    {
        row.Settle(resolution);
        _wrapUpRows[row.EventId] = row;
    }

    // ---------------------------------------------------------------- what is in the way

    private async Task AnswerWrapUpBlockerAsync(WrapUpItemViewModel row, EventBlocker blocker)
    {
        try
        {
            var stored = await followUpRepository.SetBlockerAsync(row.EventId, blocker);
            _dayFollowUps[row.EventId] = stored;
            row.OfferRemedy(blocker);
            NoteWrapUpAnswered();
        }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Recording what blocks an event");
            Status = exception.Message;
        }
    }

    /// <summary>Halves the slot, never below a quarter of an hour, keeping the same start.</summary>
    private async Task ShortenWrapUpEventAsync(WrapUpItemViewModel row)
    {
        try
        {
            var source = row.Event;
            var minutes = Math.Max(15, (int)Math.Round((source.EndUtc - source.StartUtc).TotalMinutes) / 2);
            source.EndUtc = source.StartUtc.AddMinutes(minutes);
            source.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await calendarRepository.UpdateAsync(source);
            await ReloadDayAsync();
            Settle(row, localization.Text("BlockerNoted"));
            await RefreshWrapUpAsync();
        }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Shortening a repeatedly moved event");
            Status = exception.Message;
        }
    }

    /// <summary>
    /// Puts it first thing on the day it now sits on, keeping its length. "Not enough time" is
    /// usually a statement about the rest of the day rather than about this event, and nine in
    /// the morning is the part of a day that the rest of it has not eaten yet.
    /// </summary>
    private async Task MoveWrapUpEventEarlierAsync(WrapUpItemViewModel row)
    {
        try
        {
            var source = row.Event;
            var length = source.EndUtc - source.StartUtc;
            var local = source.StartUtc.ToLocalTime();
            var morning = new DateTimeOffset(local.Year, local.Month, local.Day, 9, 0, 0, local.Offset);
            await calendarRepository.MoveEventsAsync(
                [new EventMove(source.Id, source.Title, source.StartUtc, source.EndUtc, morning, morning + length)]);
            source.StartUtc = morning;
            source.EndUtc = morning + length;
            await ReloadDayAsync();
            Settle(row, localization.Text("BlockerNoted"));
            await RefreshWrapUpAsync();
        }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Moving a repeatedly moved event earlier");
            Status = exception.Message;
        }
    }

    /// <summary>Takes it off the calendar, and its follow-up history with it.</summary>
    private async Task DropWrapUpEventAsync(WrapUpItemViewModel row)
    {
        try
        {
            await calendarRepository.DeleteAsync(row.EventId);
            await followUpRepository.ClearAsync(row.EventId);
            await outcomeRepository.ClearAsync(row.EventId);
            _dayFollowUps.Remove(row.EventId);
            _dayOutcomes.Remove(row.EventId);
            await ReloadDayAsync();
            Settle(row, localization.Text("BlockerNoted"));
            await RefreshWrapUpAsync();
        }
        catch (Exception exception)
        {
            crashReporter.Record(exception, "Dropping a repeatedly moved event");
            Status = exception.Message;
        }
    }

    private async Task SaveWrapUpFirstStepAsync(WrapUpItemViewModel row, string firstStep)
    {
        await SaveNextStepAsync(row.Event, firstStep);
        row.ApplyNextStep(_dayFollowUps.GetValueOrDefault(row.EventId)?.NextStep);
        Settle(row, localization.Text("BlockerNoted"));
        await RefreshWrapUpAsync();
    }

    // ---------------------------------------------------------------- one tap for the lot

    /// <summary>
    /// Takes every reading the application made and writes it. This is the whole point of
    /// proposing an answer per row: a day where nothing surprising happened costs one tap.
    /// </summary>
    [RelayCommand]
    private async Task AcceptWrapUpSuggestionsAsync()
    {
        foreach (var row in WrapUpItems.Where(item => item.ShowActions).ToList())
            await row.AcceptSuggestionAsync();
        OnPropertyChanged(nameof(HasWrapUpSuggestions));
    }
}
