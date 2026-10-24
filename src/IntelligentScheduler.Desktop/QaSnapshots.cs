using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using IntelligentScheduler.Application;
using IntelligentScheduler.Desktop.Controls;
using IntelligentScheduler.Desktop.Platform;
using IntelligentScheduler.Desktop.ViewModels;
using IntelligentScheduler.Domain;
using IntelligentScheduler.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IntelligentScheduler.Desktop;

/// <summary>
/// Offscreen renderer used by <c>--render-qa-snapshots</c>. It seeds a throwaway database with
/// a realistic week and photographs every surface, so UI work can be reviewed without clicking
/// through the app by hand. Point it at a temporary data directory: it writes real rows.
/// </summary>
internal static class QaSnapshots
{
    public static async Task Run(IServiceProvider services, EdgeShellViewModel viewModel, string outputDirectory)
    {
        await SeedAsync(services);
        // The shell already initialized against an empty database, so the caches are rebuilt here.
        await viewModel.InitializeAsync();
        await viewModel.RefreshConversationsAsync();
        await viewModel.RefreshMemoriesAsync();
        await viewModel.RefreshAnalyticsAsync();
        await viewModel.RefreshSuggestionsAsync();
        SeedConversation(viewModel);
        Shot(outputDirectory, "01-main.png", 444, 748, viewModel, new MainWindow { DataContext = viewModel });

        // Projects are tasks and events broken into steps. First the unscheduled kind: the list
        // with the schedule folded away, the day's planned steps at its head and the projects as
        // tasks among the rest - then the first of them opened onto its days.
        var scheduleExpanded = viewModel.IsScheduleExpanded;
        var tasksExpanded = viewModel.IsTasksExpanded;
        viewModel.IsScheduleExpanded = false;
        viewModel.IsTasksExpanded = true;
        Shot(outputDirectory, "01n-unscheduled-projects.png", 444, 748, viewModel, new MainWindow { DataContext = viewModel });
        if (viewModel.Tasks.FirstOrDefault(row => row.Project is { HasSteps: true })?.Project is { } project)
        {
            project.IsExpanded = true;
            Shot(outputDirectory, "01n2-project-open.png", 444, 748, viewModel, new MainWindow { DataContext = viewModel }, scrollTo: 560);

            // A step asking for a time is handed to the ordinary composer, already filled in from
            // the step: its name, its project's category and the first gap that day long enough.
            if (project.Groups.SelectMany(group => group.Steps).FirstOrDefault(step => !step.IsDone && !step.IsScheduled)
                is { } waiting)
            {
                await waiting.TimeCommand.ExecuteAsync(null);
                Shot(outputDirectory, "01n3-step-time.png", 444, 748, viewModel, new MainWindow { DataContext = viewModel });
                viewModel.ToggleEventComposerCommand.Execute(null);

                // Its day is asked with the ordinary date picker, which for a step also offers "no day".
                await waiting.PickDayCommand.ExecuteAsync(null);
                Shot(outputDirectory, "01n4-step-day-picker.png", 360, 402, viewModel,
                    new DatePickerWindow { DataContext = viewModel });
                viewModel.CloseDatePickerCommand.Execute(null);
            }
            project.IsExpanded = false;
        }

        // The task form breaking a new task into steps as it is written.
        viewModel.IsScheduleExpanded = false;
        viewModel.ToggleTaskComposerCommand.Execute(null);
        viewModel.EditTitle = "Move to the new flat";
        viewModel.OpenTaskDraftStepsCommand.Execute(null);
        foreach (var line in new[] { "Book the van", "Pack the kitchen", "Hand back the keys" })
        {
            viewModel.TaskDraftStepText = line;
            viewModel.AddTaskDraftStepCommand.Execute(null);
        }
        viewModel.TaskDraftStepText = "Change the address";
        Shot(outputDirectory, "01n5-task-form-steps.png", 444, 748, viewModel, new MainWindow { DataContext = viewModel });
        viewModel.CancelTaskEditCommand.Execute(null);

        // Then the scheduled kind: an event broken into what its hour is spent on, the checklist
        // open under its row with half of it ticked.
        viewModel.IsScheduleExpanded = true;
        viewModel.IsTasksExpanded = false;
        if (viewModel.Agenda.FirstOrDefault(row => row.Sitting is { HasSteps: true })?.Sitting is { } sitting)
        {
            var wasOpen = sitting.IsOpen;
            sitting.IsOpen = true;
            Shot(outputDirectory, "01o-event-steps.png", 444, 748, viewModel, new MainWindow { DataContext = viewModel }, scrollTo: 120);

            // What the sitting left undone, carried on: the composer with the open steps in it.
            await sitting.CarryToTimeCommand.ExecuteAsync(null);
            Shot(outputDirectory, "01o2-carry-on.png", 444, 748, viewModel, new MainWindow { DataContext = viewModel });
            viewModel.ToggleEventComposerCommand.Execute(null);
            sitting.IsOpen = wasOpen;
        }

        // The event form breaking a new event into steps, one of them longer than the hour has room for.
        viewModel.ToggleEventComposerCommand.Execute(null);
        viewModel.EventTitle = "Quarterly planning";
        viewModel.OpenEventDraftStepsCommand.Execute(null);
        foreach (var line in new[] { "Review last quarter", "Draft three goals" })
        {
            viewModel.EventDraftStepText = line;
            viewModel.AddEventDraftStepCommand.Execute(null);
        }
        Shot(outputDirectory, "01o3-event-form-steps.png", 444, 748, viewModel, new MainWindow { DataContext = viewModel });
        viewModel.ToggleEventComposerCommand.Execute(null);

        viewModel.IsScheduleExpanded = scheduleExpanded;
        viewModel.IsTasksExpanded = tasksExpanded;

        var canTrackEvent = viewModel.CanTrackEvent;
        var isEventStarted = viewModel.IsEventStarted;
        var isEventFinished = viewModel.IsEventFinished;
        viewModel.CanTrackEvent = true;
        viewModel.IsEventStarted = false;
        viewModel.IsEventFinished = false;
        // The monitoring dock across the foot of the shell, with nothing running in it yet.
        Shot(outputDirectory, "01a-current-event-controls.png", 444, 748, viewModel,
            new MainWindow { DataContext = viewModel });
        if (viewModel.StartEventCommand.CanExecute(null))
        {
            await viewModel.StartEventCommand.ExecuteAsync(null);
            Shot(outputDirectory, "01a2-current-event-timer.png", 444, 748, viewModel,
                new MainWindow { DataContext = viewModel });

            // A held session. The rail swaps pause for resume, the stopwatch loses its accent,
            // and nothing has been asked - which is the whole point of the state.
            await viewModel.PauseEventCommand.ExecuteAsync(null);
            Shot(outputDirectory, "01a2b-session-paused.png", 444, 748, viewModel,
                new MainWindow { DataContext = viewModel });

            // The one thing a pause is allowed to ask, once it has stood still long enough.
            viewModel.IsLongPause = true;
            viewModel.LongPauseText = "Paused for 27 min.";
            Shot(outputDirectory, "01a2c-long-pause.png", 444, 748, viewModel,
                new MainWindow { DataContext = viewModel });
            viewModel.IsLongPause = false;

            // Stopping asks how it went. Never how long: the session measured that - and it
            // asks in the tray of the row the event is on, which the stop opens by itself.
            await viewModel.StopEventCommand.ExecuteAsync(null);
            Shot(outputDirectory, "01a2d-session-result.png", 444, 748, viewModel,
                new MainWindow { DataContext = viewModel });

            // "Partly" is the one answer with a second step: how far it got, and what is left.
            // The figure is now landed on rather than picked from four presets, so 73 is as
            // reachable an answer as 75 and the sweep photographs one of the odd ones.
            viewModel.AnswerSessionPartialCommand.Execute(null);
            viewModel.SetSessionProgressCommand.Execute("73");
            // What is left is a list now, so the sweep photographs one: two lines already on
            // it and a third still in the box, which is the state saving has to fold in.
            viewModel.SessionReviewNextStep = "Recheck the last three screens";
            viewModel.AddSessionStepCommand.Execute(null);
            viewModel.SessionReviewNextStep = "Ask Dana about the export";
            viewModel.AddSessionStepCommand.Execute(null);
            viewModel.SessionReviewNextStep = "Write the migration note";
            Shot(outputDirectory, "01a2e-session-partial.png", 444, 748, viewModel,
                new MainWindow { DataContext = viewModel });

            viewModel.DismissSessionReviewCommand.Execute(null);
            await viewModel.UndoTrackingCommand.ExecuteAsync(null);
        }
        viewModel.CanTrackEvent = canTrackEvent;
        viewModel.IsEventStarted = isEventStarted;
        viewModel.IsEventFinished = isEventFinished;

        // The day's own offer to be answered for, all at once, at the end of it.
        var wrapUpOffer = viewModel.IsWrapUpOfferVisible;
        viewModel.IsWrapUpOfferVisible = true;
        viewModel.WrapUpOfferText = "3 of today's events have no result yet.";
        Shot(outputDirectory, "01a5-wrap-up-offer.png", 444, 748, viewModel,
            new MainWindow { DataContext = viewModel });
        viewModel.IsWrapUpOfferVisible = wrapUpOffer;

        // A reminder that lands while the shell is open does not open anything — it marks the
        // event it is about, which is the state photographed here.
        if (viewModel.Agenda.FirstOrDefault(item => item.IsEvent)?.Event is { } reminded)
        {
            viewModel.FlashEvent(reminded.Id);
            Shot(outputDirectory, "01a4-reminder-mark.png", 444, 748, viewModel,
                new MainWindow { DataContext = viewModel });
            viewModel.FlashEvent(null);
            foreach (var row in viewModel.Agenda) row.IsFlashing = false;
        }

        // Fixation on a row: the path that makes a past event markable at all.
        //
        // The tray is opened by hand rather than through the toggle, and the row is the day's
        // first event rather than the first one that may be marked. Those two conditions are
        // the same thing at four in the afternoon and different things at ten past midnight -
        // a sweep run after the day rolls over has no event behind it, and this photograph
        // used to go quietly missing for the rest of the night.
        if (viewModel.Agenda.FirstOrDefault(item => item.IsEvent) is { } trackable)
        {
            trackable.IsTrackingOpen = true;
            Shot(outputDirectory, "01a3-row-fixation.png", 444, 748, viewModel,
                new MainWindow { DataContext = viewModel });

            // The same tray with a stopwatch running in it. The session's own controls are not
            // here - they belong to the rail at the foot of the shell and to the widget - so
            // what this shows is the tray asking its one question about a session that is
            // still going, with the undo beside the heading and the three answers below it.
            await trackable.TrackStartCommand.ExecuteAsync(null);
            trackable.IsTrackingOpen = true;
            Shot(outputDirectory, "01a3b-row-session.png", 444, 748, viewModel,
                new MainWindow { DataContext = viewModel });
            await trackable.TrackUndoCommand.ExecuteAsync(null);
            trackable.CloseTrackingCommand.Execute(null);
            viewModel.DismissSessionReviewCommand.Execute(null);

            // What is left, as its own card under the row - then the same card rolled up to its
            // heading, and then the row with the card put away entirely, which leaves one chip
            // among the row's own and nothing else. The two are photographed apart because they
            // are two different states and the card has to be legible in both: rolled up it is
            // one line with a count on it, closed it is not there at all.
            var hadNextStep = trackable.NextStepText;
            trackable.NextStepText = "Recheck the last three screens; ask Dana about the export "
                                     + "before the numbers go out on Friday";
            Shot(outputDirectory, "01a3c-row-leftover.png", 444, 748, viewModel,
                new MainWindow { DataContext = viewModel });
            trackable.ToggleNextStepCollapsedCommand.Execute(null);
            Shot(outputDirectory, "01a3c2-row-leftover-rolled.png", 444, 748, viewModel,
                new MainWindow { DataContext = viewModel });
            trackable.ToggleNextStepCollapsedCommand.Execute(null);
            trackable.ToggleNextStepCommand.Execute(null);
            Shot(outputDirectory, "01a3d-row-leftover-hidden.png", 444, 748, viewModel,
                new MainWindow { DataContext = viewModel });
            trackable.ToggleNextStepCommand.Execute(null);
            trackable.NextStepText = hadNextStep;
        }

        // Through the command rather than the flag: opening the sheet is what sets up the day
        // the shift would land on.
        viewModel.ToggleDayActionsCommand.Execute(null);
        Shot(outputDirectory, "01b-day-actions.png", 444, 748, viewModel,
            new MainWindow { DataContext = viewModel });
        viewModel.ToggleDayShiftMoreCommand.Execute(null);
        viewModel.NudgeDayShiftDaysCommand.Execute("1");
        Shot(outputDirectory, "01b2-day-shift-options.png", 444, 748, viewModel,
            new MainWindow { DataContext = viewModel });
        viewModel.NudgeDayShiftDaysCommand.Execute("-1");
        viewModel.IsDayShiftMoreOpen = false;
        viewModel.IsDayActionsOpen = false;

        // The event form, and the question it asks before it writes anything.
        if (viewModel.Agenda.FirstOrDefault(item => item.Event is not null) is { } editable)
        {
            await editable.EditCommand.ExecuteAsync(null);
            Shot(outputDirectory, "01l-event-composer.png", 444, 748, viewModel,
                new MainWindow { DataContext = viewModel });
            await viewModel.RequestEventSaveCommand.ExecuteAsync(null);
            Shot(outputDirectory, "01m-event-save-confirmation.png", 444, 748, viewModel,
                new MainWindow { DataContext = viewModel });
            viewModel.CancelEventConfirmationCommand.Execute(null);
            viewModel.ToggleEventComposerCommand.Execute(null);
        }

        // Picking events out of the day, and the sheet that moves them.
        viewModel.ToggleSelectionModeCommand.Execute(null);
        viewModel.SelectAllEventsCommand.Execute(null);
        Shot(outputDirectory, "01h-selection.png", 444, 748, viewModel,
            new MainWindow { DataContext = viewModel });
        viewModel.OpenMoveForSelectionCommand.Execute(null);
        await viewModel.PreviewMoveCommand.ExecuteAsync(null);
        Shot(outputDirectory, "01i-move-sheet.png", 444, 748, viewModel,
            new MainWindow { DataContext = viewModel });
        viewModel.CloseMovePanelCommand.Execute(null);
        viewModel.ToggleSelectionModeCommand.Execute(null);

        // A day that is over says how it went. It used to announce that day's first event as
        // though it were about to start.
        var shownDate = viewModel.SelectedDate;
        await ShowDayAsync(viewModel, shownDate.AddDays(-2));
        Shot(outputDirectory, "01j-past-day-review.png", 444, 748, viewModel,
            new MainWindow { DataContext = viewModel });
        await ShowDayAsync(viewModel, shownDate.AddDays(2));
        Shot(outputDirectory, "01k-future-day.png", 444, 748, viewModel,
            new MainWindow { DataContext = viewModel });
        await ShowDayAsync(viewModel, shownDate);

        viewModel.Use24HourClock = false;
        await WaitUntilAsync(() => viewModel.Agenda.Count == 0 || viewModel.Agenda.Any(item => item.StartText.Contains(' ')));
        Shot(outputDirectory, "01c-main-12-hour.png", 444, 748, viewModel,
            new MainWindow { DataContext = viewModel });
        viewModel.OpenEventStartTimePickerCommand.Execute(null);
        Shot(outputDirectory, "01d-time-picker-12-hour.png", 236, 176, viewModel,
            new TimePickerWindow { DataContext = viewModel });
        viewModel.CloseTimePickerCommand.Execute(null);
        viewModel.Use24HourClock = true;

        viewModel.OpenEventDateCalendarCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.IsEventDatePickerOpen);
        Shot(outputDirectory, "01e-date-picker.png", 360, 402, viewModel,
            new DatePickerWindow { DataContext = viewModel });
        viewModel.CloseDatePickerCommand.Execute(null);

        viewModel.ToggleTaskComposerCommand.Execute(null);
        Shot(outputDirectory, "01f-task-editor.png", 444, 748, viewModel,
            new MainWindow { DataContext = viewModel }, scrollTo: 500);
        viewModel.OpenTaskDurationTimePickerCommand.Execute(null);
        Shot(outputDirectory, "01g-task-duration-picker.png", 236, 176, viewModel,
            new TimePickerWindow { DataContext = viewModel });
        viewModel.CloseTimePickerCommand.Execute(null);
        viewModel.CancelTaskEditCommand.Execute(null);

        viewModel.IsChatOpen = true;
        Shot(outputDirectory, "02-chat.png", 400, 590, viewModel, new ChatFlyoutView());

        // The sheet over the planner it covers. What is behind it is left in focus: blurring
        // the whole card every time the assistant opened read as the window going soft, and
        // cost a full-surface effect pass on every repaint underneath.
        Shot(outputDirectory, "02b-chat-over-planner.png", 444, 748, viewModel,
            new MainWindow { DataContext = viewModel });

        // The same conversation widened out of the planner: a panel of its own, standing where
        // the calendar and the report stand and held to one reading column rather than stretched
        // across the whole of it.
        viewModel.IsChatExpanded = true;
        Shot(outputDirectory, "02c-chat-widened.png", 740, 748, viewModel,
            new ChatWindow { DataContext = viewModel });
        viewModel.IsChatExpanded = false;

        viewModel.IsChatHistoryOpen = true;
        Shot(outputDirectory, "03-chat-history.png", 400, 590, viewModel, new ChatFlyoutView());

        viewModel.IsChatHistoryOpen = false;
        viewModel.IsMemoryPanelOpen = true;
        Shot(outputDirectory, "04-chat-memory.png", 400, 590, viewModel, new ChatFlyoutView());

        // The wrap-up drawer: everything today left unanswered, each line already carrying the
        // application's own reading of it. It consults no model and sends no message; it lives
        // in this panel only because this is where the application's own drawers are.
        viewModel.IsMemoryPanelOpen = false;
        viewModel.IsWrapUpOpen = true;
        await viewModel.RefreshWrapUpAsync();
        Shot(outputDirectory, "04d-chat-wrap-up.png", 400, 590, viewModel, new ChatFlyoutView());
        viewModel.IsWrapUpOpen = false;

        // What the running model costs the machine. Nothing is actually running in a sweep, so
        // the four figures are put in by hand — the point of the photograph is the shape of the
        // readout, which is what was wrong with it: a card half again as tall as anything it
        // reported, and video memory hung underneath the rest of it on a line of its own.
        var wasRunning = viewModel.IsLocalModelRunning;
        var wasProvider = viewModel.AssistantProvider;
        viewModel.AssistantProvider = nameof(Domain.AssistantProvider.Local);
        viewModel.IsLocalModelRunning = true;
        viewModel.LocalModelMonitorTag = "Qwen 3 4B";
        viewModel.LocalModelCpuUsageText = "36.4%";
        viewModel.LocalModelCpuFraction = 0.364;
        viewModel.IsLocalModelOnGpu = true;
        viewModel.LocalModelAcceleratorText = "GPU";
        viewModel.LocalModelGpuUsageText = "72.1%";
        viewModel.LocalModelGpuFraction = 0.721;
        viewModel.LocalModelMemoryUsageText = "3.1 GB";
        viewModel.LocalModelMemoryFraction = 0.19;
        viewModel.LocalModelVideoMemoryText = "5.7 GB";
        viewModel.LocalModelVideoMemoryFraction = 0.71;
        viewModel.LocalModelUptimeText = "12:07";
        Shot(outputDirectory, "04b-chat-resource-monitor.png", 400, 590, viewModel, new ChatFlyoutView());
        // The same readout with the model on the processor, which is the state that used to run
        // out of room first.
        viewModel.IsLocalModelOnGpu = false;
        viewModel.LocalModelAcceleratorText = "CPU";
        viewModel.LocalModelGpuUsageText = "not in use";
        viewModel.LocalModelGpuFraction = 0;
        viewModel.LocalModelVideoMemoryText = "not in use";
        viewModel.LocalModelVideoMemoryFraction = 0;
        Shot(outputDirectory, "04c-chat-resource-monitor-cpu.png", 400, 590, viewModel, new ChatFlyoutView());
        viewModel.IsLocalModelRunning = wasRunning;
        viewModel.AssistantProvider = wasProvider;

        // The charts grow into place when a real panel opens; an offscreen photograph has no
        // frames to play them on, so they are put straight into their finished shape.
        viewModel.SettleAnalyticsCharts();
        Shot(outputDirectory, "05-analytics.png", 642, 748, viewModel, new AnalyticsWindow { DataContext = viewModel });
        Shot(outputDirectory, "05b-analytics-lower.png", 642, 748, viewModel,
            new AnalyticsWindow { DataContext = viewModel }, scrollTo: 620);

        // The range that used to draw ninety-one bars on top of one another.
        await viewModel.SetAnalyticsRangeCommand.ExecuteAsync("91");
        viewModel.SettleAnalyticsCharts();
        Shot(outputDirectory, "05c-analytics-quarter.png", 642, 748, viewModel,
            new AnalyticsWindow { DataContext = viewModel });
        await viewModel.SetAnalyticsRangeCommand.ExecuteAsync("7");
        viewModel.SettleAnalyticsCharts();
        Shot(outputDirectory, "05d-analytics-week.png", 642, 748, viewModel,
            new AnalyticsWindow { DataContext = viewModel });
        await viewModel.PreviousAnalyticsWeekCommand.ExecuteAsync(null);
        viewModel.SettleAnalyticsCharts();
        Shot(outputDirectory, "05d2-analytics-previous-week.png", 642, 748, viewModel,
            new AnalyticsWindow { DataContext = viewModel });
        await viewModel.CurrentAnalyticsWeekCommand.ExecuteAsync(null);
        viewModel.SettleAnalyticsCharts();
        viewModel.IsAnalyticsRangeListOpen = true;
        Shot(outputDirectory, "05e-analytics-range-list.png", 642, 748, viewModel,
            new AnalyticsWindow { DataContext = viewModel });
        viewModel.IsAnalyticsRangeListOpen = false;
        await viewModel.SetAnalyticsRangeCommand.ExecuteAsync("30");
        viewModel.SettleAnalyticsCharts();

        viewModel.IsCalendarPopupOpen = true;
        // The board is built for an open panel and lands on it over several dispatcher turns,
        // so that opening the calendar never blocks. A photograph wants the finished board.
        await viewModel.SettleSchedulerBoardAsync();
        Shot(outputDirectory, "06-calendar.png", 872, 748, viewModel, new SidePanelWindow { DataContext = viewModel });
        Shot(outputDirectory, "06b-calendar-scroll-active.png", 872, 748, viewModel,
            new SidePanelWindow { DataContext = viewModel }, previewScrollBar: true);
        viewModel.Use24HourClock = false;
        await WaitUntilAsync(() => viewModel.SchedulerBlocks.Count == 0 || viewModel.SchedulerBlocks.Any(item => item.StartText.Contains(' ')));
        await viewModel.SettleSchedulerBoardAsync();
        Shot(outputDirectory, "06c-calendar-12-hour.png", 872, 748, viewModel,
            new SidePanelWindow { DataContext = viewModel });
        viewModel.Use24HourClock = true;
        viewModel.IsCalendarPopupOpen = false;

        viewModel.SelectSettingsSectionCommand.Execute("general");
        Shot(outputDirectory, "07-settings-general.png", 444, 748, viewModel,
            new SettingsWindow { DataContext = viewModel }, verifyComboBox: true);
        // Reminders, including the quiet hour, sit below the fold of the general page.
        Shot(outputDirectory, "07a-settings-reminders.png", 444, 748, viewModel,
            new SettingsWindow { DataContext = viewModel }, scrollTo: 620);
        viewModel.StartDoNotDisturbCommand.Execute("60");
        Shot(outputDirectory, "07a2-settings-quiet-hour.png", 444, 748, viewModel,
            new SettingsWindow { DataContext = viewModel }, scrollTo: 620);
        // The quiet spell that has no end. It is worth its own photograph because it is the one
        // the sentence underneath has to word differently: there is no clock time to name.
        viewModel.StartDoNotDisturbCommand.Execute(EdgeShellViewModel.UntilResumed.ToString());
        Shot(outputDirectory, "07a3-settings-quiet-until-resumed.png", 444, 748, viewModel,
            new SettingsWindow { DataContext = viewModel }, scrollTo: 620);
        viewModel.EndDoNotDisturbCommand.Execute(null);
        viewModel.DoNotDisturbMinutes = 60;
        // Focus mode, in the two shapes its sentence has: nothing on the screen, and a game with
        // a name on it. The second is worth a photograph of its own because it is the only place
        // in settings where the sentence under a switch describes the world rather than a
        // preference — and the only one that can be wrong in a way a photograph would show.
        // The rig builds the view model without the shell around it, so nothing has told it what
        // this desktop can see. Said here, because the card words itself differently when
        // full-screen applications cannot be noticed at all and the ordinary case is the one
        // worth photographing.
        viewModel.AreFullScreenAppsDetected = true;
        Shot(outputDirectory, "07a4-settings-focus-mode.png", 444, 748, viewModel,
            new SettingsWindow { DataContext = viewModel }, scrollTo: 1240);
        viewModel.ReportFocusMode(new FocusModeState(FocusModeTrigger.FullScreenApp, "Cyberpunk2077"));
        Shot(outputDirectory, "07a5-settings-focus-mode-on.png", 444, 748, viewModel,
            new SettingsWindow { DataContext = viewModel }, scrollTo: 1240);
        viewModel.ReportFocusMode(FocusModeState.Off);
        viewModel.SelectSettingsSectionCommand.Execute("appearance");
        Shot(outputDirectory, "07b-settings-appearance.png", 444, 748, viewModel,
            new SettingsWindow { DataContext = viewModel });
        viewModel.ThemeColorSource = nameof(ThemeColorSource.Custom);
        viewModel.SelectedColorSourceOption = viewModel.ColorSourceOptions.First(option => option.Value == viewModel.ThemeColorSource);
        Shot(outputDirectory, "07b2-settings-custom-colors.png", 444, 748, viewModel,
            new SettingsWindow { DataContext = viewModel });
        viewModel.ThemeColors.Single(editor => editor.Role == ThemeColorRole.Accent).IsExpanded = true;
        Shot(outputDirectory, "07b2a-settings-color-picker.png", 444, 748, viewModel,
            new SettingsWindow { DataContext = viewModel }, scrollTo: 230);
        viewModel.ThemeColors.Single(editor => editor.Role == ThemeColorRole.Accent).IsExpanded = false;
        Shot(outputDirectory, "07b3-settings-theme-actions.png", 444, 748, viewModel,
            new SettingsWindow { DataContext = viewModel }, scrollTo: 430);
        viewModel.UseThemeMascotColor = false;
        viewModel.IsMascotColorPickerOpen = true;
        Shot(outputDirectory, "07b2b-settings-mascot-color.png", 444, 748, viewModel,
            new SettingsWindow { DataContext = viewModel }, scrollTo: 430);
        viewModel.IsMascotColorPickerOpen = false;
        viewModel.UseThemeMascotColor = true;
        viewModel.ShowMascot = false;
        Shot(outputDirectory, "07b2c-settings-mascot-off.png", 444, 748, viewModel,
            new SettingsWindow { DataContext = viewModel }, scrollTo: 430);
        viewModel.ShowMascot = true;
        foreach (var fontId in new[] { "builtin:Literata", "builtin:ShantellSans" })
        {
            viewModel.SelectedFontOption = viewModel.FontOptions.First(option => option.Value == fontId);
            Shot(outputDirectory, $"07-font-{fontId[8..].ToLowerInvariant()}.png", 444, 748, viewModel,
                new SettingsWindow { DataContext = viewModel }, scrollTo: 420);
        }
        viewModel.SelectedFontOption = viewModel.FontOptions.First(option => option.Value == Platform.FontCatalogService.InterId);
        viewModel.SelectThemeModeCommand.Execute(nameof(ShellThemeMode.Light));
        viewModel.ThemeColorSource = nameof(ThemeColorSource.Preset);
        viewModel.SelectedColorSourceOption = viewModel.ColorSourceOptions.First(option => option.Value == viewModel.ThemeColorSource);
        Shot(outputDirectory, "07b3-settings-marelian-light.png", 444, 748, viewModel,
            new SettingsWindow { DataContext = viewModel });
        viewModel.SelectedThemeOption = viewModel.ThemeOptions.First(option => option.Value == nameof(ShellTheme.Marelian));
        viewModel.SelectThemeModeCommand.Execute(nameof(ShellThemeMode.Dark));
        viewModel.SelectSettingsSectionCommand.Execute("ai");
        viewModel.AssistantProvider = nameof(IntelligentScheduler.Domain.AssistantProvider.Local);
        viewModel.SelectedAssistantProviderOption = viewModel.AssistantProviderOptions.First(option => option.Value == viewModel.AssistantProvider);
        Shot(outputDirectory, "07c-settings-ai-local.png", 444, 748, viewModel,
            new SettingsWindow { DataContext = viewModel });
        viewModel.AssistantProvider = nameof(IntelligentScheduler.Domain.AssistantProvider.OpenAiCompatible);
        viewModel.SelectedAssistantProviderOption = viewModel.AssistantProviderOptions.First(option => option.Value == viewModel.AssistantProvider);
        Shot(outputDirectory, "07d-settings-ai-api.png", 444, 748, viewModel,
            new SettingsWindow { DataContext = viewModel });
        viewModel.AssistantProvider = nameof(IntelligentScheduler.Domain.AssistantProvider.Mcp);
        viewModel.SelectedAssistantProviderOption = viewModel.AssistantProviderOptions.First(option => option.Value == viewModel.AssistantProvider);
        Shot(outputDirectory, "07e-settings-ai-mcp.png", 444, 748, viewModel,
            new SettingsWindow { DataContext = viewModel });
        viewModel.IsAssistantAdvancedOpen = true;
        viewModel.AreToolsVisible = true;
        // Under MCP the request parameters are replaced by the line saying why they are absent.
        Shot(outputDirectory, "07f-settings-ai-advanced.png", 444, 748, viewModel,
            new SettingsWindow { DataContext = viewModel }, scrollTo: 620);
        viewModel.AssistantProvider = nameof(IntelligentScheduler.Domain.AssistantProvider.OpenAiCompatible);
        viewModel.SelectedAssistantProviderOption = viewModel.AssistantProviderOptions.First(option => option.Value == viewModel.AssistantProvider);
        Shot(outputDirectory, "07f2-settings-ai-advanced-api.png", 444, 748, viewModel,
            new SettingsWindow { DataContext = viewModel }, scrollTo: 620);
        viewModel.IsAssistantAdvancedOpen = false;
        viewModel.AreToolsVisible = false;
        viewModel.AssistantProvider = nameof(IntelligentScheduler.Domain.AssistantProvider.Local);
        viewModel.SelectedAssistantProviderOption = viewModel.AssistantProviderOptions.First(option => option.Value == viewModel.AssistantProvider);
        // The local engine now lives inside the AI section rather than in a tab of its own.
        Shot(outputDirectory, "07g-settings-local-ai.png", 444, 748, viewModel,
            new SettingsWindow { DataContext = viewModel }, scrollTo: 320);

        // Image input on: the projector is a second file with a purpose of its own, and it is
        // only asked for once something actually needs it.
        var vision = viewModel.LocalModelVisionEnabled;
        viewModel.LocalModelVisionEnabled = true;
        Shot(outputDirectory, "07g2-settings-local-ai-vision.png", 444, 748, viewModel,
            new SettingsWindow { DataContext = viewModel }, scrollTo: 420);
        viewModel.LocalModelVisionEnabled = vision;

        // Where the model runs, and the knobs the engine is launched with.
        Shot(outputDirectory, "07g3-settings-local-ai-gpu.png", 444, 748, viewModel,
            new SettingsWindow { DataContext = viewModel }, scrollTo: 700);

        viewModel.SelectSettingsSectionCommand.Execute("data");
        Shot(outputDirectory, "07h-settings-data.png", 444, 748, viewModel,
            new SettingsWindow { DataContext = viewModel });
        viewModel.SelectedCloudProviderOption = viewModel.CloudProviderOptions
            .First(option => option.Value == nameof(CloudSyncProvider.Folder));
        Shot(outputDirectory, "07h2-settings-data-folder.png", 444, 748, viewModel,
            new SettingsWindow { DataContext = viewModel });
        viewModel.SelectedCloudProviderOption = viewModel.CloudProviderOptions
            .First(option => option.Value == nameof(CloudSyncProvider.GoogleDrive));
        Shot(outputDirectory, "07h3-settings-data-google.png", 444, 748, viewModel,
            new SettingsWindow { DataContext = viewModel });
        viewModel.SelectedCloudProviderOption = viewModel.CloudProviderOptions
            .First(option => option.Value == nameof(CloudSyncProvider.None));

        viewModel.SelectSettingsSectionCommand.Execute("about");
        Shot(outputDirectory, "07i-settings-about.png", 444, 748, viewModel,
            new SettingsWindow { DataContext = viewModel });
        // The release banner never appears against a live GitHub here, so it is staged.
        viewModel.AvailableUpdateVersion = "1.3.0";
        viewModel.AvailableUpdateName = "DAYDRIVE 1.3.0";
        viewModel.AvailableUpdateNotes = "Fixation on every row of the day\nOne AI settings section\nCloud sync";
        viewModel.UpdateStatusText = "Version 1.3.0 is available.";
        Shot(outputDirectory, "07i2-settings-update.png", 444, 748, viewModel,
            new SettingsWindow { DataContext = viewModel }, scrollTo: 420);
        viewModel.AvailableUpdateVersion = string.Empty;
        viewModel.UpdateStatusText = string.Empty;

        // The quick widget. It has one state now that it is opened deliberately.
        var quietPeek = new QuickPeekWindow { DataContext = viewModel };
        quietPeek.SettleNow();
        Shot(outputDirectory, "08-peek.png", 316, 320, viewModel, quietPeek);

        // The same widget with the creature turned off: the clock block keeps its shape and the
        // text simply has the whole of it.
        viewModel.ShowMascot = false;
        var peekWithoutMascot = new QuickPeekWindow { DataContext = viewModel };
        peekWithoutMascot.SettleNow();
        Shot(outputDirectory, "08a-peek-no-mascot.png", 316, 320, viewModel, peekWithoutMascot);
        viewModel.ShowMascot = true;

        // Its other shape. Whether an event is actually running when the sweep happens to run is
        // not something the seeded day can promise, so the three-block state is staged.
        var hadPeekEvent = viewModel.HasPeekEvent;
        var hadPeekTimer = viewModel.HasPeekTimer;
        var hadTrackingState = viewModel.TrackingStateText;
        viewModel.HasPeekEvent = true;
        viewModel.HasPeekTimer = true;
        viewModel.IsPeekEventRunning = true;
        viewModel.PeekTimerText = "00:01:27";
        viewModel.PeekTimerCaption = "60 min left";
        viewModel.PeekProgressPercent = 42;
        viewModel.TrackingStateText = "started at 13:43";
        viewModel.PeekNextTime = "21:30";
        viewModel.PeekNextTitle = "Evening walk";
        var eventPeek = new QuickPeekWindow { DataContext = viewModel };
        eventPeek.SettleNow();
        Shot(outputDirectory, "08b-peek-event.png", 316, 440, viewModel, eventPeek);

        // A reminder is the event card wearing it, so the widget a notification puts on the
        // screen is that card alone — no countdown, clock, rail or what-comes-next.
        // Held, because a card that lapses mid-sweep would be photographed
        // letting go of the reminder.
        var hadPeekEventId = viewModel.PeekEventId;
        var remindedEventId = Guid.NewGuid();
        viewModel.PeekEventId = remindedEventId;
        viewModel.IsPeekCompact = true;
        viewModel.Notifications.IsHeld = true;
        viewModel.Notifications.Post(new AppNotification("Starting soon", "«Standup» starts in 5 min",
            DateTimeOffset.Now, remindedEventId));
        var reminderPeek = new QuickPeekWindow { DataContext = viewModel };
        reminderPeek.SettleNow();
        Shot(outputDirectory, "08b1-peek-reminder.png", 316, 300, viewModel, reminderPeek);

        // The same reminder in a widget the user has opened. Every block is back and none of
        // them has moved; the card is still the one wearing the news.
        viewModel.IsPeekCompact = false;
        var openedReminderPeek = new QuickPeekWindow { DataContext = viewModel };
        openedReminderPeek.SettleNow();
        Shot(outputDirectory, "08b1b-peek-reminder-open.png", 316, 440, viewModel, openedReminderPeek);
        if (viewModel.Notifications.Current is { } reminder) reminder.DismissCommand.Execute(null);

        // And a reminder about something the card is not describing: a different day is open, or
        // it is about the event after the one that is running. Nothing on the card belongs to
        // it, so it says itself — and the countdown, which would be counting a different event
        // down, goes with the rest.
        viewModel.Notifications.EndPresentation();
        viewModel.Notifications.ReleasePresentation();
        viewModel.IsPeekCompact = true;
        viewModel.Notifications.Post(new AppNotification("Ending soon", "«Deep work» ends in 8 min",
            DateTimeOffset.Now, Guid.NewGuid()));
        var soloPeek = new QuickPeekWindow { DataContext = viewModel };
        soloPeek.SettleNow();
        Shot(outputDirectory, "08b1a-peek-reminder-alone.png", 316, 220, viewModel, soloPeek);
        if (viewModel.Notifications.Current is { } solo) solo.DismissCommand.Execute(null);
        viewModel.IsPeekCompact = false;
        viewModel.PeekEventId = hadPeekEventId;
        // Ending and releasing are two moments apart on screen — the card lets go of the accent
        // and only then stops drawing what it was holding. Nothing is fading in a photograph,
        // so both happen at once.
        viewModel.Notifications.EndPresentation();
        viewModel.Notifications.ReleasePresentation();
        viewModel.Notifications.IsHeld = false;

        // "Partly" from a row opens the session sheet at its second step - the one that asks
        // how far the work got and where to pick it up. Nothing anywhere asks for minutes any
        // more, so there is no duration picker left to photograph.
        if (viewModel.Agenda.FirstOrDefault(item => item.IsEvent) is { } partialRow)
        {
            await partialRow.TrackPartialCommand.ExecuteAsync(null);
            viewModel.DismissSessionReviewCommand.Execute(null);
        }

        // The widget asking the same two questions the day card asks. Both are driven through
        // the real commands rather than set by hand, because what is being checked is that the
        // widget and the shell agree about which event is being asked about: the strip only
        // appears when the question is about the event the widget is describing.
        viewModel.CanTrackEvent = true;
        viewModel.IsEventStarted = false;
        viewModel.IsEventFinished = false;
        if (viewModel.StartEventCommand.CanExecute(null))
        {
            await viewModel.StartEventCommand.ExecuteAsync(null);
            await viewModel.PauseEventCommand.ExecuteAsync(null);
            viewModel.HasPeekEvent = true;
            viewModel.HasPeekTimer = true;
            viewModel.IsLongPause = true;
            viewModel.LongPauseText = "Paused for 27 min.";
            viewModel.PeekTimerText = "00:24:10";
            viewModel.PeekTimerCaption = "12 min paused";
            var pausedPeek = new QuickPeekWindow { DataContext = viewModel };
            pausedPeek.SettleNow();
            Shot(outputDirectory, "08b2-peek-long-pause.png", 316, 500, viewModel, pausedPeek);
            viewModel.IsLongPause = false;

            await viewModel.StopEventCommand.ExecuteAsync(null);
            viewModel.HasPeekEvent = true;
            viewModel.HasPeekTimer = true;
            viewModel.PeekTimerText = "00:24:10";
            viewModel.PeekTimerCaption = "plan 1 h - actual 24 min";
            var askingPeek = new QuickPeekWindow { DataContext = viewModel };
            askingPeek.SettleNow();
            Shot(outputDirectory, "08b3-peek-session-result.png", 316, 500, viewModel, askingPeek);

            viewModel.DismissSessionReviewCommand.Execute(null);
            await viewModel.UndoTrackingCommand.ExecuteAsync(null);
        }
        viewModel.CanTrackEvent = canTrackEvent;
        viewModel.IsEventStarted = isEventStarted;
        viewModel.IsEventFinished = isEventFinished;
        viewModel.HasPeekEvent = true;
        viewModel.HasPeekTimer = true;
        viewModel.IsPeekEventRunning = true;
        viewModel.PeekTimerText = "00:01:27";
        viewModel.PeekTimerCaption = "plan 1 h - actual 1 min";

        // And the state the countdown block used to have no way of showing: the event is due,
        // and nobody has pressed start.
        viewModel.IsPeekAwaitingStart = true;
        viewModel.IsPeekEventRunning = false;
        viewModel.PeekTimerText = "00:00:00";
        viewModel.PeekTimerCaption = "not started yet";
        viewModel.PeekProgressPercent = 0;
        var waitingPeek = new QuickPeekWindow { DataContext = viewModel };
        waitingPeek.SettleNow();
        Shot(outputDirectory, "08c-peek-waiting.png", 316, 440, viewModel, waitingPeek);
        viewModel.IsPeekAwaitingStart = false;
        viewModel.HasPeekEvent = hadPeekEvent;
        viewModel.HasPeekTimer = hadPeekTimer;
        viewModel.TrackingStateText = hadTrackingState;

        // The other reading of the status chip, and the one pose of the mascot that means
        // something is wrong. The seeded day has nothing overdue, so it is staged too.
        var hadOverdue = viewModel.OverdueTaskCount;
        var hadStatus = viewModel.PeekStatusText;
        var hadMood = viewModel.PeekMascotMood;
        viewModel.OverdueTaskCount = 2;
        viewModel.PeekStatusText = "2 overdue";
        viewModel.PeekMascotMood = MascotMood.Alert;
        var overduePeek = new QuickPeekWindow { DataContext = viewModel };
        overduePeek.SettleNow();
        Shot(outputDirectory, "08d-peek-overdue.png", 316, 320, viewModel, overduePeek);
        viewModel.OverdueTaskCount = hadOverdue;
        viewModel.PeekStatusText = hadStatus;
        viewModel.PeekMascotMood = hadMood;

        // The clock block under the pointer: it is the way into the panel, so it has to look
        // like something that can be pressed.
        var hoverPeek = new QuickPeekWindow { DataContext = viewModel };
        hoverPeek.SettleNow();
        Shot(outputDirectory, "08e-peek-hover.png", 316, 320, viewModel, hoverPeek, hoverClockBlock: true);

        // A twelve-hour clock is half again as wide as a twenty-four-hour one, and the block is
        // a fixed width shared with the creature, so it gets its own picture.
        viewModel.Use24HourClock = false;
        var twelveHourPeek = new QuickPeekWindow { DataContext = viewModel };
        twelveHourPeek.SettleNow();
        Shot(outputDirectory, "08g-peek-12-hour.png", 316, 320, viewModel, twelveHourPeek);
        viewModel.Use24HourClock = true;

        // Every mood the creature has, at both the sizes it is drawn at, on one sheet.
        Shot(outputDirectory, "08f-peek-mascot-moods.png", 900, 296, viewModel, MascotSheet());
    }

    /// <summary>
    /// Every pose the creature can take, laid out at both sizes it is used at, so a change to
    /// the drawing can be reviewed in one picture rather than by opening the widget repeatedly.
    /// </summary>
    private static Control MascotSheet()
    {
        var sheet = new StackPanel
        {
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        foreach (var (plate, creature) in new[] { (160, 112), (76, 54) })
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            foreach (var mood in Enum.GetValues<MascotMood>())
                row.Children.Add(new Border
                {
                    Width = plate,
                    Height = plate,
                    CornerRadius = new CornerRadius(22),
                    Background = new SolidColorBrush(Color.Parse("#131720")),
                    Child = new MascotView { IsAnimated = false, Mood = mood, Width = creature, Height = creature }
                });
            sheet.Children.Add(row);
        }
        return sheet;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 50 && !condition(); attempt++)
            await Task.Delay(20);
    }

    /// <summary>Selects a day the way the week strip does, which is the only public path in.</summary>
    private static async Task ShowDayAsync(EdgeShellViewModel viewModel, DateOnly date)
    {
        if (viewModel.WeekDays.FirstOrDefault(chip => chip.Date == date) is { } chip)
            await chip.SelectCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// <c>--qa-only=01n,01o</c> photographs only the shots whose names start with one of these.
    /// The whole sweep takes minutes, and a change to one surface is checked on that surface.
    /// </summary>
    private static readonly string[] OnlyShots = Environment.GetCommandLineArgs()
        .FirstOrDefault(argument => argument.StartsWith("--qa-only=", StringComparison.OrdinalIgnoreCase))
        ?["--qa-only=".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

    private static void Shot(string directory, string fileName, int width, int height,
        EdgeShellViewModel viewModel, Control content, double scrollTo = 0, bool previewScrollBar = false,
        bool verifyComboBox = false, bool hoverClockBlock = false)
    {
        if (OnlyShots.Length > 0 && !OnlyShots.Any(prefix => fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) return;
        try { ShotCore(directory, fileName, width, height, viewModel, content, scrollTo, previewScrollBar, verifyComboBox, hoverClockBlock); }
        // One unrenderable surface must not cost the whole sweep.
        catch (Exception exception) { Console.Error.WriteLine($"{fileName}: {exception}"); }
    }

    private static void ShotCore(string directory, string fileName, int width, int height,
        EdgeShellViewModel viewModel, Control content, double scrollTo, bool previewScrollBar,
        bool verifyComboBox, bool hoverClockBlock = false)
    {
        // Re-hosting a window's content drops the inherited data context, so it is set again here.
        var window = new Window
        {
            Width = width,
            Height = height,
            WindowDecorations = WindowDecorations.None,
            DataContext = viewModel,
            Content = content is Window inner ? inner.Content : content,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent]
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.Measure(new Size(width, height));
        window.Arrange(new Rect(0, 0, width, height));
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        if (verifyComboBox && window.GetVisualDescendants().OfType<ComboBox>()
                .FirstOrDefault(item => item.IsVisible && item.IsEnabled && item.Items.Count > 0) is { } comboBox)
        {
            comboBox.IsDropDownOpen = true;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var popup = comboBox.GetVisualDescendants().OfType<Popup>()
                .FirstOrDefault(item => item.Name == "PART_Popup");
            if (!comboBox.IsDropDownOpen || popup is null || !popup.IsOpen)
                throw new InvalidOperationException("ComboBox popup did not open through the native template.");
            comboBox.IsDropDownOpen = false;
            Dispatcher.UIThread.RunJobs();
        }

        if (hoverClockBlock && window.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(item => item.Classes.Contains("peekBlockFill")) is { } clockButton)
        {
            clockButton.Transitions = null;
            ((Avalonia.Controls.IPseudoClasses)clockButton.Classes).Set(":pointerover", true);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        if (window.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } scroll)
        {
            scroll.Offset = new Vector(0, scrollTo);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        if (fileName == "01b-day-actions.png")
        {
            VerifyEqualWidths(VisibleButtons(window, "dayActionTile"), "copy actions", 3);
            VerifyEqualWidths(VisibleButtons(window, "shiftDirection", "dayShiftTime"), "shift controls", 3);
        }

        // With the extra answers unfolded, the days row stands in the same three columns as the
        // hours row above it, so the two read as one control rather than two.
        if (fileName == "01b2-day-shift-options.png")
            VerifyEqualWidths(VisibleButtons(window, "shiftDirection", "dayShiftTime"), "shift controls", 5);

        if (previewScrollBar && window.GetVisualDescendants().OfType<ScrollBar>()
                .FirstOrDefault(item => item.Orientation == Avalonia.Layout.Orientation.Vertical && item.IsVisible) is { } scrollBar)
        {
            scrollBar.Transitions = null;
            scrollBar.Opacity = 1;
            if (scrollBar.GetVisualDescendants().OfType<Border>()
                    .FirstOrDefault(item => item.Classes.Contains("capsuleScrollRail")) is { } rail)
            {
                rail.Transitions = null;
                rail.Width = 4;
                rail.Opacity = 0.20;
            }
            if (scrollBar.GetVisualDescendants().OfType<Thumb>().FirstOrDefault() is { } thumb)
            {
                thumb.Transitions = null;
                thumb.Width = 5.8;
                thumb.Opacity = 1;
                thumb.RenderTransform = new ScaleTransform(1, 1.04);
            }
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            if (scrollBar.GetVisualDescendants().OfType<Track>().FirstOrDefault() is { } track)
            {
                var thumbRatio = track.Bounds.Height <= 0 || track.Thumb is null
                    ? 0
                    : track.Thumb.Bounds.Height / track.Bounds.Height;
                if (thumbRatio is < 0.055 or > 0.26 || track.ValueFromDistance(0, 24) <= 0)
                    throw new InvalidOperationException($"Capsule scrollbar geometry is invalid: ratio={thumbRatio:0.###}.");
            }
        }

        var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        bitmap.Render(window);
        bitmap.Save(Path.Combine(directory, fileName), new PngBitmapEncoderOptions());
        window.Close();
    }

    /// <summary>
    /// The controls of one group that are actually on the screen. A folded-away section is still
    /// in the visual tree, and counting what it holds would fail a check about the row on show.
    /// </summary>
    private static IReadOnlyCollection<Button> VisibleButtons(Visual root, params string[] classes)
        => [.. root.GetVisualDescendants().OfType<Button>()
            .Where(button => button.IsEffectivelyVisible && classes.Any(button.Classes.Contains))];

    private static void VerifyEqualWidths(IReadOnlyCollection<Button> buttons, string group, int expected)
    {
        if (buttons.Count != expected)
            throw new InvalidOperationException($"Expected {expected} {group}, found {buttons.Count}.");
        var widths = buttons.Select(button => button.Bounds.Width).ToArray();
        if (widths.Max() - widths.Min() > 0.5)
            throw new InvalidOperationException($"{group} are not equal: {string.Join(", ", widths.Select(width => width.ToString("0.##")))}.");
    }

    private static async Task SeedAsync(IServiceProvider services)
    {
        var events = services.GetRequiredService<ICalendarRepository>();
        if ((await events.GetAllAsync()).Count > 0) return;

        await DemoDataSeeder.SeedAsync(
            events,
            services.GetRequiredService<ITaskRepository>(),
            services.GetRequiredService<IBlockRepository>(),
            services.GetRequiredService<ITaskExecutionRepository>(),
            new DemoDataOptions(Days: 60),
            outcomes: services.GetRequiredService<IEventOutcomeRepository>(),
            projects: services.GetRequiredService<IProjectRepository>());

        var memory = services.GetRequiredService<IUserMemoryRepository>();
        await memory.UpsertAsync(new UserMemoryEntry
        {
            Key = "deep-work-window",
            Value = "Works best between 09:00 and 12:00, dislikes meetings before 10:00.",
            Category = UserMemoryCategory.Routine
        });
        await memory.UpsertAsync(new UserMemoryEntry
        {
            Key = "training-days",
            Value = "Trains on Tuesday and Thursday evenings and wants those protected.",
            Category = UserMemoryCategory.Constraint
        });
        await memory.UpsertAsync(new UserMemoryEntry
        {
            Key = "goal-q3",
            Value = "Wants to finish the scheduler rewrite before the end of the quarter.",
            Category = UserMemoryCategory.Goal,
            IsFromAssistant = false
        });

        var chat = services.GetRequiredService<IChatRepository>();
        foreach (var (title, preview) in new[]
                 {
                     ("Plan for the week", "Let us protect two deep-work blocks."),
                     ("Free time on Friday", "You have 14:00 to 17:00 open."),
                     ("Move the training session", "Proposed a move to 19:30.")
                 })
        {
            var conversation = await chat.CreateConversationAsync(title);
            await chat.AppendMessageAsync(new ChatMessage
            {
                ConversationId = conversation.Id, Role = ChatRole.User, Content = title
            });
            await chat.AppendMessageAsync(new ChatMessage
            {
                ConversationId = conversation.Id, Role = ChatRole.Assistant, Content = preview, Meta = "API \u00b7 1.4 s"
            });
        }
    }

    /// <summary>A scripted exchange so the bubbles, markdown and tool trace are all visible.</summary>
    private static void SeedConversation(EdgeShellViewModel viewModel)
    {
        viewModel.Messages.Clear();
        viewModel.Messages.Add(new("Plan tomorrow around what is already booked", true));
        viewModel.Messages.Add(new(
            """
            Tomorrow already has **three fixed items**, so here is what is left:

            - `09:00–09:30` Daily sync
            - `11:00–12:30` Deep work
            - `19:00–20:00` Training

            That leaves two usable windows:

            1. **13:00–15:00** — long enough for the quarterly report.
            2. 16:00–17:30 — better for the shorter review reply.

            I would put the report in the first window while the morning focus is still fresh.
            Say the word and I will create the event.
            """,
            false, "API · 2.1 s",
            trace:
            [
                new(new AssistantToolTraceEntry("get_schedule", "3 events on 2026-08-24", false, false), Localization(viewModel)),
                new(new AssistantToolTraceEntry("find_free_slots", "2 windows of 90+ minutes", false, false), Localization(viewModel)),
                new(new AssistantToolTraceEntry("get_tasks", "3 unscheduled tasks", false, false), Localization(viewModel))
            ])
        {
            IsTraceExpanded = true
        });
    }

    private static Localization.LocalizationService Localization(EdgeShellViewModel viewModel)
        => LocalizationAccessor ??= new Localization.LocalizationService();

    private static Localization.LocalizationService? LocalizationAccessor;
}
