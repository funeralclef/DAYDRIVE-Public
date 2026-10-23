using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using IntelligentScheduler.Application;
using IntelligentScheduler.Desktop.Controls;
using IntelligentScheduler.Desktop.Platform;
using IntelligentScheduler.Desktop.ViewModels;
using IntelligentScheduler.Desktop.Localization;
using IntelligentScheduler.Domain;
using IntelligentScheduler.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IntelligentScheduler.Desktop;

public sealed partial class App : Avalonia.Application
{
    private ServiceProvider? _services;
    private bool _exiting;
    /// <summary>Set while the tray menu is on screen, so nothing climbs over it.</summary>
    private bool _trayMenuOpen;

    private CancellationTokenSource? _hideDelay;
    private CancellationTokenSource? _peekDelay;
    private CancellationTokenSource? _railDelay;
    private CancellationTokenSource? _edgeDelay;

    /// <summary>
    /// How long this edge has to stand empty, with nobody asking for anything on it, before the
    /// petal believes it and unfolds its clock again. See ReleaseEdge in the startup routine.
    /// </summary>
    private static readonly TimeSpan EdgeSettleDelay = TimeSpan.FromMilliseconds(300);

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        ThemeVisualTokens.Apply(this, new IntelligentScheduler.Domain.MarelianThemeProfile());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
            _services = BuildServices();
            var crashReporter = _services.GetRequiredService<CrashReporter>();
            LinuxLayerShell.ReportError = exception => crashReporter.Record(exception, "Wayland layer surface");
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                if (args.ExceptionObject is Exception exception) crashReporter.Record(exception, "Unhandled application exception");
            };
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                crashReporter.Record(args.Exception, "Unobserved task exception");
                args.SetObserved();
            };
            _services.GetRequiredService<DatabaseInitializer>().InitializeAsync().GetAwaiter().GetResult();

            if (Environment.GetCommandLineArgs().FirstOrDefault(argument =>
                    argument.StartsWith("--seed-demo", StringComparison.OrdinalIgnoreCase)) is { } seedArgument)
            {
                DemoDataCommand.Run(_services, seedArgument,
                    Environment.GetCommandLineArgs().Any(a => a.Equals("--seed-force", StringComparison.OrdinalIgnoreCase)))
                    .GetAwaiter().GetResult();
                Environment.Exit(0);
                return;
            }

            var viewModel = _services.GetRequiredService<EdgeShellViewModel>();
            viewModel.DataDirectoryPath = _services.GetRequiredService<AppPaths>().DataDirectory;
            Dispatcher.UIThread.UnhandledException += (_, args) =>
            {
                crashReporter.Record(args.Exception, "UI dispatcher exception");
                viewModel.ReportUnexpectedError(args.Exception);
                args.Handled = true;
            };
            viewModel.InitializeAsync().GetAwaiter().GetResult();
            var requestedSection = Environment.GetCommandLineArgs()
                .FirstOrDefault(argument => argument.StartsWith("--section=", StringComparison.OrdinalIgnoreCase))?
                ["--section=".Length..].ToLowerInvariant();
            switch (requestedSection)
            {
                case "tasks": viewModel.ShowTasksCommand.Execute(null); break;
                case "calendar": viewModel.ShowCalendarCommand.Execute(null); break;
                case "assistant": viewModel.ShowAssistantCommand.Execute(null); break;
                case "settings": viewModel.ShowSettingsCommand.Execute(null); break;
                case "analytics": viewModel.ToggleAnalyticsCommand.Execute(null); break;
                case "chat": viewModel.ToggleChatCommand.Execute(null); break;
            }
            if (Environment.GetCommandLineArgs().Any(a => a.Equals("--chat", StringComparison.OrdinalIgnoreCase)))
            {
                viewModel.ToggleChatCommand.Execute(null);
            }
            if (Environment.GetCommandLineArgs().Any(a => a.Equals("--model-menu", StringComparison.OrdinalIgnoreCase)))
            {
                viewModel.IsChatOpen = true;
                viewModel.ToggleModelMenuCommand.Execute(null);
            }
            if (Environment.GetCommandLineArgs().Any(a => a.Equals("--local-models-menu", StringComparison.OrdinalIgnoreCase)))
            {
                viewModel.IsChatOpen = true;
                viewModel.ToggleModelMenuCommand.Execute(null);
                viewModel.OpenLocalModelsSubMenuCommand.Execute(null);
            }
            if (Environment.GetCommandLineArgs().Any(a => a.Equals("--render-qa-snapshots", StringComparison.OrdinalIgnoreCase)))
            {
                var outDir = Environment.GetCommandLineArgs()
                    .FirstOrDefault(argument => argument.StartsWith("--qa-out=", StringComparison.OrdinalIgnoreCase))
                    ?["--qa-out=".Length..] ?? Path.Combine(AppContext.BaseDirectory, "qa");
                Directory.CreateDirectory(outDir);
                // The message loop has not started yet, so anything the sweep awaits that really
                // yields would post its continuation to a dispatcher nobody is running and wait
                // there forever. The dispatcher is driven by hand until the sweep is done.
                RunBeforeTheLoopStarts(QaSnapshots.Run(_services, viewModel, outDir));
                Environment.Exit(0);
                return;
            }
            var previewTheme = Environment.GetCommandLineArgs()
                .FirstOrDefault(argument => argument.StartsWith("--preview-theme=", StringComparison.OrdinalIgnoreCase))?
                ["--preview-theme=".Length..];
            if (Enum.TryParse<ShellTheme>(previewTheme, true, out var parsedTheme)) viewModel.PreviewTheme(parsedTheme);
            var previewLanguage = Environment.GetCommandLineArgs()
                .FirstOrDefault(argument => argument.StartsWith("--language=", StringComparison.OrdinalIgnoreCase))?
                ["--language=".Length..];
            if (!string.IsNullOrWhiteSpace(previewLanguage)) viewModel.PreviewLanguage(previewLanguage);

            var shellMode = ShellEnvironment.Mode;
            if (shellMode == DesktopShellMode.Windowed)
            {
                var shell = new WindowedShell(desktop, _services, viewModel);
                shell.Start();
                desktop.Exit += (_, _) =>
                {
                    shell.Dispose();
                    _services?.Dispose();
                    _services = null;
                };
                base.OnFrameworkInitializationCompleted();
                return;
            }

            var main = new MainWindow { DataContext = viewModel, Icon = AppIcon.Create() };
            var handle = new EdgeHandleWindow { DataContext = viewModel, Icon = AppIcon.Create() };
            var sidePanel = new SidePanelWindow { DataContext = viewModel, Icon = AppIcon.Create() };
            var settingsWindow = new SettingsWindow { DataContext = viewModel, Icon = AppIcon.Create() };
            var analyticsWindow = new AnalyticsWindow { DataContext = viewModel, Icon = AppIcon.Create() };
            // The assistant widened out of the planner. It is the same view the sheet holds,
            // hosted beside the shell instead of over it, which makes it the fourth of the
            // panels that take each other's place on that edge.
            var chatWindow = new ChatWindow { DataContext = viewModel, Icon = AppIcon.Create() };
            var peek = new QuickPeekWindow { DataContext = viewModel, Icon = AppIcon.Create() };
            // The petal's controls, beside the petal. They are the quick widget's rail, moved
            // next to whichever of the two surfaces the pointer is currently on: one set of
            // start, hold and stop exists in this application, and a sliver on the edge of the
            // screen is not somewhere to put a second.
            var petalRail = new PetalRailWindow { DataContext = viewModel, Icon = AppIcon.Create() };
            var datePicker = new DatePickerWindow { DataContext = viewModel, Icon = AppIcon.Create() };
            var timePicker = new TimePickerWindow { DataContext = viewModel, Icon = AppIcon.Create() };
            if (LinuxLayerShell.IsSupported)
            {
                foreach (var passive in new Window[] { handle, peek, petalRail })
                    (passive.PlatformImpl as LinuxLayerWindow)?.SetAcceptFocus(false);
                (peek.PlatformImpl as LinuxLayerWindow)?.SetInputInset(QuickPeekWindow.CardInset);
                (petalRail.PlatformImpl as LinuxLayerWindow)?.SetInputInsets(new Thickness(
                    PetalRailWindow.CardInset, PetalRailWindow.CardInset,
                    PetalRailWindow.CardInset - PetalRailWindow.Gap, PetalRailWindow.CardInset));
            }
            MacOSWindowTransparency.Prepare(main, handle, sidePanel, settingsWindow, analyticsWindow,
                chatWindow, peek, petalRail, datePicker, timePicker);
            // Reminders are the shell's own, and they are part of the quick widget rather than a
            // window of their own: the widget already stands where a reminder belongs, already
            // never takes the caret, and is already the thing the right button on the petal
            // opens. One surface on that edge instead of two stacked translucent ones.
            var notificationCenter = _services.GetRequiredService<NotificationCenter>();
            // And the one thing that can tell a reminder to wait. The shell claims keys from the
            // whole desktop and draws on top of everything; both of those are the wrong answer
            // while a game has the screen, so it watches for that and stands back on its own.
            var focusWatcher = _services.GetRequiredService<FocusModeWatcher>();
            var toasts = viewModel.Notifications;
            // Every surface arrives the way the quick widget does. The widget was the only thing
            // in the application that came in rather than appeared, which made every other window
            // read as a jump cut beside it. The shell comes from the edge it lines up against and
            // the two choosers grow out of the middle of what they cover; the three panels, which
            // share that edge and differ from one another only in width, fold into it and unfold
            // out of it, so switching between them is one continuous change of shape.
            var mainReveal = new WindowRevealAnimator(main);
            var sidePanelReveal = new WindowRevealAnimator(sidePanel, RevealOrigin.Panel);
            var settingsReveal = new WindowRevealAnimator(settingsWindow, RevealOrigin.Panel);
            var analyticsReveal = new WindowRevealAnimator(analyticsWindow, RevealOrigin.Panel);
            var chatReveal = new WindowRevealAnimator(chatWindow, RevealOrigin.Panel);
            var datePickerReveal = new WindowRevealAnimator(datePicker, RevealOrigin.Center);
            var timePickerReveal = new WindowRevealAnimator(timePicker, RevealOrigin.Center);
            // Only one of the four is ever up, so each of them needs to be able to find the
            // others in order to put them away.
            var sideSurfaces = new (Window Window, WindowRevealAnimator Reveal)[]
            {
                (sidePanel, sidePanelReveal), (settingsWindow, settingsReveal),
                (analyticsWindow, analyticsReveal), (chatWindow, chatReveal)
            };

            var forceShowOnStartup = Environment.GetCommandLineArgs().Any(argument => argument.Equals("--show", StringComparison.OrdinalIgnoreCase));
            var startsInBackground = !forceShowOnStartup &&
                                     (viewModel.StartHidden || Environment.GetCommandLineArgs().Any(argument => argument.Equals("--background", StringComparison.OrdinalIgnoreCase)));
            var initialHidePending = startsInBackground;
            // True until that startup-only hide has actually taken the shell off the screen. A
            // background start puts the main window up and hides it a fraction of a second later,
            // and the reminder service raises everything inside its ten-minute window the moment
            // it is started — so on a cold start the first reminders land while the shell is
            // still technically visible. Delivered then they were marked on a day nobody could
            // see and never said again, which is the one case where a reminder simply vanished.
            var settlingStartup = startsInBackground;
            // Reminders raised in that gap. They are not dropped and not delivered early; they
            // wait for the screen to settle, which is a fifth of a second away.
            var reminderBacklog = new List<AppNotification>();
            // Set while the side windows are being built off-screen, so none of the events that
            // showing a window raises is mistaken for the user doing something.
            var warmingUp = false;
            // Setting a transparency hint is not an ordinary value assignment on macOS: it
            // rebuilds the native backing layer. Keep the last mode so unrelated settings do
            // not briefly put that rectangular layer back behind every rounded window.
            bool? appliedTransparencyEnabled = null;
            // Counts requests to show or hide the quick widget so a stale one cannot undo a newer.
            var peekRequest = 0;
            // The shell and the widget stand against the same edge, so only one of them may be
            // arriving at any moment. Every request for either takes the next number, and the
            // second half of a hand-over that has since been overtaken — the petal pressed again
            // while the shell was folding away for the widget — is dropped rather than putting
            // the surface nobody asked for back on the screen.
            var edgeRequest = 0;
            // True once the petal has been put on the screen for the first time. Focus mode can
            // ask for it to be hidden and shown again, and the first of those requests can
            // arrive while the settings are still being applied — which is before the petal has
            // ever been shown, and therefore before there is anything to hide.
            var handleStarted = false;
            // Whether the petal has been told that something else has this edge, and whether the
            // rail beside it is currently wanted. Both are read by the placement, which runs long
            // before either of the two blocks that maintain them.
            var edgeSuppressed = false;
            var railWanted = false;
            // True from the moment the shell is asked for until it is actually on the screen.
            // Only the side-panel placement reads it, and only to tell a panel that is arriving
            // *with* the shell — the assistant shortcut opens both in one breath — from one that
            // outlived it. Failing to clear it can only switch that check off; it can never turn
            // it into something that closes a panel somebody asked for.
            var shellIsArriving = false;
            desktop.MainWindow = main;

            // A window's position is a setter with nothing behind it: assigning the point a window
            // is already at is still a move, and on a transparent window a move is a compositor
            // round trip. Opening a panel runs the placement three times over seven windows, and
            // almost every one of those was a window being told to stay exactly where it is —
            // paid on the click, in the same frames the panel was trying to slide in on.
            static void MoveTo(Window window, PixelPoint target)
            {
                if (window.Position != target) window.Position = target;
            }

            void PositionWindows()
            {
                var screen = main.Screens.ScreenFromWindow(main) ?? main.Screens.Primary;
                if (screen is null) return;
                var area = screen.WorkingArea;
                var scale = screen.Scaling;
                var mainWidth = (int)Math.Ceiling(MainWindow.ContentWidth * scale);
                var mainHeight = (int)Math.Ceiling(main.Height * scale);
                MoveTo(main, new PixelPoint(ShellRightEdge(area, scale) - mainWidth, area.Y + Math.Max(0, (area.Height - mainHeight) / 2)));
                // The petal is the one surface here that is not a fixed size. How tall it stands
                // is a share of the monitor rather than a number, and how wide it stands depends
                // on whether a session is running and whether the pointer is on it — so it is
                // handed the screen and places itself, the way the widget does.
                ApplyEdgeSuppression();
                handle.PlaceOn(area, scale);
                // Placed whether or not it is up: it is a fixed size and a fixed place, so there
                // is nothing here that can move under a hand already on one of its keys.
                petalRail.PlaceBeside(area, scale, EdgeHandleWindow.LiveCapsuleWidth + EdgeHandleWindow.ScreenGap);
                // Every reveal calls this twice, and the widget's own placement asks the platform
                // for the screen again. There is nothing to place while it is away.
                if (peek.IsVisible) PositionPeek();
                // Side windows own the empty nine-pixel bridge; their content keeps its width.
                sidePanel.Width = 881;
                sidePanel.Height = main.Height;
                var sideWidth = (int)Math.Ceiling(sidePanel.Width * scale);
                MoveTo(sidePanel, new PixelPoint(main.Position.X - sideWidth, main.Position.Y));
                settingsWindow.Width = MainWindow.ContentWidth + 9;
                settingsWindow.Height = main.Height;
                var settingsWidth = (int)Math.Ceiling(settingsWindow.Width * scale);
                MoveTo(settingsWindow, new PixelPoint(main.Position.X - settingsWidth, main.Position.Y));
                analyticsWindow.Width = 651;
                analyticsWindow.Height = main.Height;
                var analyticsWidth = (int)Math.Ceiling(analyticsWindow.Width * scale);
                MoveTo(analyticsWindow, new PixelPoint(main.Position.X - analyticsWidth, main.Position.Y));
                // Between the two: wider than the report, narrower than the calendar. A
                // conversation wants more room than a page of figures and less than a month.
                chatWindow.Width = 749;
                chatWindow.Height = main.Height;
                var chatWidth = (int)Math.Ceiling(chatWindow.Width * scale);
                MoveTo(chatWindow, new PixelPoint(main.Position.X - chatWidth, main.Position.Y));
                var usesSidePanelHost = viewModel.IsTaskBlockEditorVisible && viewModel.IsCalendarPopupOpen;
                var timePickerHostX = usesSidePanelHost
                    ? sidePanel.Position.X
                    : main.Position.X;
                var timePickerHostWidth = usesSidePanelHost
                    ? (int)Math.Ceiling(872 * scale)
                    : mainWidth;
                // The date chooser is a global surface and stays centered over the visible
                // main card. Time pickers remain contextual and may belong to the side editor.
                var mainPetalWidth = (int)Math.Ceiling(20 * scale);
                var datePickerHostX = main.Position.X + mainPetalWidth;
                var datePickerHostWidth = mainWidth - mainPetalWidth;
                var datePickerWidth = (int)Math.Ceiling(datePicker.Width * scale);
                var datePickerHeight = (int)Math.Ceiling(datePicker.Height * scale);
                MoveTo(datePicker, new PixelPoint(
                    datePickerHostX + Math.Max(0, (datePickerHostWidth - datePickerWidth) / 2),
                    main.Position.Y + Math.Max(0, (mainHeight - datePickerHeight) / 2)));
                timePicker.Width = 236;
                timePicker.Height = 176;
                var timePickerWidth = (int)Math.Ceiling(timePicker.Width * scale);
                var timePickerHeight = (int)Math.Ceiling(timePicker.Height * scale);
                if (main.IsVisible)
                {
                    MoveTo(timePicker, new PixelPoint(
                        timePickerHostX + Math.Max(0, (timePickerHostWidth - timePickerWidth) / 2),
                        main.Position.Y + Math.Max(0, (mainHeight - timePickerHeight) / 2)));
                }
                else
                {
                    MoveTo(timePicker, new PixelPoint(
                        area.X + Math.Max(0, (area.Width - timePickerWidth) / 2),
                        area.Y + Math.Max(0, (area.Height - timePickerHeight) / 2)));
                }
            }

            // Which blocks the widget was made of the last time it was placed. A widget that has
            // just become a different object — a verdict folding the countdown away, the day's
            // last event being answered for and taking the event block and the rail with it —
            // is re-centred whatever the pointer happens to be doing; see PositionPeek.
            var peekShape = -1;
            // True while the widget is somewhere other than the middle of its edge because a
            // re-centring was held back, for the pointer or for an unfold. It owes one, and the
            // pointer leaving is what pays it.
            var peekOffCentre = false;
            if (LinuxLayerShell.IsSupported) main.Screens.Changed += (_, _) => PositionWindows();
            crashReporter.Diagnostic(LinuxLayerShell.IsSupported
                ? "Edge shell: native GTK Wayland layer surfaces; on-demand keyboard focus."
                : $"Edge shell: native platform windows; layer-shell unavailable: {LinuxLayerShell.UnavailableReason}.");

            // The widget sizes itself to whatever it is currently showing, so it is placed on
            // its own rather than with the fixed windows. It stands off the petal by exactly what
            // the shell stands off it: both are placed against the same edge now, instead of the
            // widget being derived from a gap measured off the shell.
            void PositionPeek(bool recentre = false, Size? measuredSize = null)
            {
                var screen = main.Screens.ScreenFromWindow(main) ?? main.Screens.Primary;
                if (screen is null) return;
                var area = screen.WorkingArea;
                var scale = screen.Scaling;
                // Measured, never declared: SizeToContent means there is no declared size, and
                // the widget is a different size with an event on than without one. The size the
                // layout has just settled on is the best answer of all, which is why the resize
                // hands its own measurement in; ClientSize is the next best, because Bounds on a
                // window that sizes to its content still reports the previous pass for one more
                // frame. Before the first layout there is no measurement at all, and what the
                // widget wants is a better guess than a number written down here.
                var measured = measuredSize is { Width: > 1 } handed ? handed
                    : peek.ClientSize.Width > 1 ? peek.ClientSize
                    : peek.Bounds.Width > 1 ? peek.Bounds.Size
                    : peek.DesiredSize;
                var peekWidth = (int)Math.Ceiling((measured.Width > 1 ? measured.Width : 256) * scale);
                var peekHeight = (int)Math.Ceiling((measured.Height > 1 ? measured.Height : 240) * scale);
                // The card is inset inside its window so its shadows have room to fall. That
                // inset is not part of what the eye lines up against the petal.
                var inset = (int)Math.Round(QuickPeekWindow.CardInset * scale);

                var shape = peek.ShapeKey;
                var reshaped = shape != peekShape;
                peekShape = shape;

                // Normally the widget is centred on the edge, and it re-centres whenever what it
                // is showing changes size. Two things hold that back, and both are debts now
                // rather than states: whatever defers a re-centring writes down that one is owed,
                // and the pointer leaving pays it.
                //
                // Unfolding out of the compact shape a reminder put it in is the first: the card
                // is already on the screen and being read, so it is pinned where it is and the
                // widget grows around it — re-centred instead, the whole thing would crawl up
                // the monitor by half a clock block while the user was halfway through the
                // sentence on it.
                //
                // The pointer resting inside the widget is the second, because re-centring moves
                // the button being reached for out from under the press. That used to hold for
                // any size change at all, which is how the widget ended up stranded: pressing
                // "done" on the rail is a press inside the widget, and answering for the day's
                // last event takes the rail, the event block and the countdown away in one go,
                // so the clock was left hanging where the top edge of a widget three times its
                // height had been and nothing ever moved it back. A change of shape is exactly
                // the case where there is no longer a button under the pointer to protect, so it
                // re-centres. Only the widget staying the same object while changing size — a
                // caption growing a line, a chip getting longer — keeps the top edge it has.
                int top;
                if (peek.IsVisible && peek.TryGetAnchoredTop(scale, out var anchoredTop))
                {
                    top = anchoredTop;
                    peekOffCentre = true;
                }
                else if (!recentre && !reshaped && peek.IsVisible && peek.IsPointerOver)
                {
                    top = peek.Position.Y;
                    peekOffCentre = true;
                }
                else
                {
                    top = area.Y + (area.Height - peekHeight) / 2;
                    peekOffCentre = false;
                }

                MoveTo(peek, new PixelPoint(
                    Math.Max(area.X, ShellRightEdge(area, scale) + inset - peekWidth),
                    Math.Clamp(top, area.Y, Math.Max(area.Y, area.Bottom - peekHeight))));
            }

            // Where the shell's card ends, and with it every surface that lines up beside it.
            static int ShellRightEdge(PixelRect area, double scale)
                => area.Right - (int)Math.Round((EdgeHandleWindow.ScreenGap + EdgeHandleWindow.RestCapsuleWidth + 6) * scale);

            // ---- Keeping the petal reachable ---------------------------------------------
            // The handle is seven pixels of the screen and everything else in the shell is
            // hundreds, so it loses every argument about who is in front by simply being the
            // window that was shown first. "Always on top" is a band, not a place in one: the
            // last window to join the band joins it at the front, which is how the quick widget
            // — whose card is inset inside its window, and whose window therefore reaches past
            // the shell's edge and over the petal — ended up owning the petal's own pixels. A
            // left click there hit the widget's transparent margin, which does nothing with it,
            // and the petal read as broken exactly while the widget was open.
            //
            // Assigning the same value to Topmost is not a change and does nothing, so the flag
            // is dropped and put back: that is a real request to the window manager, and it
            // lands the handle at the front of the band without taking focus from anything.
            void LiftHandle()
            {
                // Dropping Topmost and putting it back is a real request to the window manager,
                // and one made while somebody is using the petal is a flicker under their hand.
                // Nothing can get in front of it during the second they are pointing at it.
                if (_exiting || _trayMenuOpen || !handle.IsVisible || handle.IsHovered
                    || petalRail.IsVisible) return;
                try
                {
                    handle.Topmost = false;
                    handle.Topmost = true;
                }
                catch (Exception exception) { crashReporter.Record(exception, "Raising the edge handle"); }
            }

            void ApplyShellSettings()
            {
                if (appliedTransparencyEnabled != viewModel.EnableTransparency)
                {
                    // Transparent must stay first even when blur is disabled: it provides the
                    // alpha channel used by the rounded shell and the space around the side tabs.
                    var shellTransparency = viewModel.EnableTransparency
                        ? new[] { WindowTransparencyLevel.Transparent, WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Mica, WindowTransparencyLevel.Blur }
                        : [WindowTransparencyLevel.Transparent];
                    main.TransparencyLevelHint = shellTransparency;
                    sidePanel.TransparencyLevelHint = shellTransparency;
                    settingsWindow.TransparencyLevelHint = shellTransparency;
                    analyticsWindow.TransparencyLevelHint = shellTransparency;
                    chatWindow.TransparencyLevelHint = shellTransparency;
                    // Never blurred and never opaque: the card is rounded and its shadows fall
                    // into the slack around it, both of which need a real alpha channel behind them.
                    peek.TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
                    appliedTransparencyEnabled = viewModel.EnableTransparency;
                    MacOSWindowTransparency.Refresh(main, sidePanel, settingsWindow, analyticsWindow, chatWindow, peek);
                }
                main.Topmost = viewModel.KeepOnTop;
                // The edge handle is the recovery path for a hidden shell and must never
                // disappear behind another maximized window.
                handle.Topmost = true;
                peek.Topmost = true;
                petalRail.Topmost = true;
                LiftHandle();
                sidePanel.Topmost = viewModel.KeepOnTop;
                settingsWindow.Topmost = viewModel.KeepOnTop;
                analyticsWindow.Topmost = viewModel.KeepOnTop;
                chatWindow.Topmost = viewModel.KeepOnTop;
                // Pickers are transient application surfaces. They must stay above every
                // shell window even when the persistent "keep on top" option is disabled.
                datePicker.Topmost = true;
                timePicker.Topmost = true;
                notificationCenter.ApplyPreferences(
                    viewModel.NotificationsEnabled, viewModel.NotificationSoundEnabled,
                    viewModel.DoNotDisturbUntilUtc, viewModel.DoNotDisturbUntilResumed);
                // The first of these also starts the watching, and only if it was asked for.
                focusWatcher.Configure(viewModel.FocusModeEnabled, viewModel.IsFocusModeHeld);
                notificationCenter.ApplyFocusMode(viewModel.IsFocusHoldingReminders);
                PositionWindows();
                // A settings save on the way out re-applies the shell, and a pinned shell would
                // put itself back on screen in the middle of the session ending.
                // Whatever is beside it comes back with it: this is the shell putting itself
                // back, not somebody asking for it, and a pinned shell only ever leaves the
                // screen by being told to — which closes the panels on the way out anyway.
                if (viewModel.PinOpen && !main.IsVisible && !_exiting) ShowMain(activate: true, withPanel: true);
            }

            // <paramref name="withPanel"/> says a panel is being asked for in the same breath as
            // the shell, which only the assistant shortcut does. Everything else — the petal, the
            // shell's own shortcut, the tray, a reminder — is asking for the shell alone.
            //
            // Called on the UI thread: every summoning of the shell comes from an input event, a
            // tray callback on this window's own message loop, or the view model.
            void ShowMain(bool activate, bool withPanel = false)
            {
                // Both of these are settled here rather than inside the work posted below, and
                // for the same reason: the side-panel placement is posted at Send priority and
                // therefore runs ahead of that work, so by the time it asks these questions they
                // have to already have been answered.
                var wasAway = !main.IsVisible;
                // The shell asked for on its own account arrives on its own. A panel flag that
                // outlived the last time the shell was up is not a panel anybody is asking for
                // now, and leaving it standing is what handed the calendar, the assistant, the
                // analytics or the settings back with a shell that had only been asked for by
                // itself. It is closed here, before the flag below tells the placement to stop
                // closing anything.
                if (wasAway && !withPanel) viewModel.CloseShellPanels();
                // And the other half. Set inside the posted work this was still false when a
                // panel asked for *with* the shell reached the placement, which then read it as
                // exactly the leftover above and closed it — which is why the assistant shortcut
                // used to open the shell and nothing else.
                shellIsArriving = true;
                Dispatcher.UIThread.Post(() =>
                {
                    _hideDelay?.Cancel();
                    var request = ++edgeRequest;
                    try
                    {
                        void Arrive()
                        {
                            if (request != edgeRequest) { shellIsArriving = false; return; }
                            mainReveal.Reveal(() =>
                            {
                                PositionWindows();
                                if (!main.IsVisible) main.Show();
                                main.Opacity = 1;
                                PositionWindows();
                                if (activate) main.Activate();
                            });
                            shellIsArriving = false;
                            // A panel that came in with the shell was let through by the
                            // placement rather than closed, on the strength of the flag above —
                            // but it had nothing to stand beside at the time and so was never
                            // put on the screen. It has now.
                            if (wasAway && viewModel.IsShellFlyoutOpen) UpdateSidePanel();
                            LiftHandle();
                        }

                        // The widget is off the screen before the shell begins to arrive, the
                        // same way the three side panels hand over to each other: they stand in
                        // the same place and both are translucent, so one over the other is not
                        // a switch but two pictures stacked. Dropped in a single frame instead,
                        // the widget also left its finished picture as the last thing it
                        // painted — which is what made its own next arrival skip its entrance
                        // and simply appear.
                        if (peek.IsVisible) VacatePeek(Arrive);
                        else Arrive();
                    }
                    catch (Exception exception)
                    {
                        shellIsArriving = false;
                        crashReporter.Record(exception, "Showing shell");
                    }
                });
            }
            void ShowMainActivated() => ShowMain(activate: true);

            // Showing the widget and putting it away both go through a request number, and only
            // the newest request is allowed to finish. They used to post their work independently,
            // so a hide queued while the panel was opening could land after a later reveal and cut
            // the entrance in half — which is what closing the panel and immediately opening the
            // widget looked like.
            void RunOnUiThread(Action work)
            {
                if (Dispatcher.UIThread.CheckAccess()) work();
                else Dispatcher.UIThread.Post(work);
            }

            // True while a grace period is counting down towards putting the widget away. A
            // reminder landing in an open widget restarts that countdown so the card is not taken
            // away half-read — but only when one was already running: the widget the user opened
            // by hand is theirs, and a reminder must not be what starts it closing.
            var peekHideArmed = false;

            // True only while the widget is on screen because a reminder put it there. A widget
            // the user opened follows the ordinary pointer grace period; a notification popup
            // follows the notification's own lifetime instead.
            var peekOpenedByReminder = false;

            void HidePeek()
            {
                _peekDelay?.Cancel();
                peekHideArmed = false;
                var request = ++peekRequest;
                RunOnUiThread(() =>
                {
                    if (request != peekRequest) return;
                    peek.ResetState();
                });
            }

            // The widget's half of a hand-over to the shell: it folds away on real frames and
            // says when its place is free. Anything queued for the widget is invalidated first,
            // so a show that was still on its way cannot land behind the shell.
            void VacatePeek(Action landed)
            {
                _peekDelay?.Cancel();
                ++peekRequest;
                peek.Vacate(landed);
            }

            // The shell's half of the same hand-over; see VacatePeek.
            void VacateMain(Action landed)
            {
                _hideDelay?.Cancel();
                viewModel.CloseShellFlyouts();
                mainReveal.Vacate(() =>
                {
                    main.Hide();
                    main.Opacity = 1;
                    PositionWindows();
                }, landed);
            }

            void ShowPeek()
            {
                _peekDelay?.Cancel();
                var request = ++peekRequest;
                RunOnUiThread(() =>
                {
                    if (request != peekRequest) return;
                    try
                    {
                        // The showing is handed to the widget rather than done before it, so the
                        // card is already back at its starting point on the frame the window goes
                        // up. Shown first and animated afterwards, the first painted frame is the
                        // finished one and the arrival is over before it is seen.
                        peek.Reveal(() =>
                        {
                            if (!peek.IsVisible)
                            {
                                peek.Show();
                                // SizeToContent means the window has no size until something lays
                                // it out. Placed before that, the first reveal was positioned
                                // against a guess and then jumped sideways once the real
                                // measurement arrived.
                                peek.UpdateLayout();
                            }
                            // An arrival has no previous position worth keeping, and the shape
                            // it arrives in is not the one it went away in.
                            PositionPeek(recentre: true);
                        });
                        // The widget has just joined the always-on-top band, which puts it in
                        // front of the petal it opens beside. The petal goes back in front.
                        LiftHandle();
                    }
                    catch (Exception exception) { crashReporter.Record(exception, "Showing the quick widget"); }
                });
            }

            // The handle answers two buttons now. The left one opens the panel, and the right one
            // opens the widget — asked for rather than offered, which is what let the widget stop
            // arriving in a dim, untouchable first stage designed to survive being unwanted. The
            // two stand in the same place on the screen, so asking for one puts the other away;
            // the widget used to simply refuse to open while the panel was up.
            void TogglePeek() => RunOnUiThread(() =>
            {
                // The widget is up, but the user never asked for it — a reminder put it there
                // in its compact shape. The button that opens the widget must then open it, not
                // close something the user did not open: the card stays exactly where it is on
                // the screen, the rest of the widget unfolds around it, and it stops being the
                // reminder's, so it no longer leaves when the reminder lapses. The reminder
                // itself is left alone on its own clock.
                if (peek.IsVisible && !peek.IsDismissing && peekOpenedByReminder)
                {
                    AdoptPeek();
                    return;
                }

                // A press while it is fading means "no, stay" — the only case where the same
                // button twice does not close what it opened.
                if (peek.IsVisible && !peek.IsDismissing)
                {
                    _peekDelay?.Cancel();
                    peek.BeginDismiss(final: true);
                    return;
                }

                // The shell folds back into the edge and is off the screen before the widget
                // unfolds out of it. Shown over a shell that was still on its way out, the
                // widget arrived onto a second translucent surface it then had to share the
                // edge with — which is what a run of quick presses on the panel followed by a
                // press for the widget used to look like.
                // It is away, or halfway out. Either way this press asks for the widget itself,
                // so one that is still fading out of its compact shape comes back whole.
                AdoptPeek();
                var request = ++edgeRequest;
                void Arrive() { if (request == edgeRequest) ShowPeek(); }
                if (main.IsVisible) VacateMain(Arrive);
                else Arrive();
            });

            // A widget that put itself on screen to carry a reminder becomes the user's the
            // moment they act on it — pressing the petal's widget button, or pressing anything
            // inside the widget itself. Two things follow from that. It stops leaving with the
            // reminder and takes the ordinary pointer grace period instead, so reading a
            // reminder and then reaching for an outcome button does not race its expiry. And it
            // stops being a notification: everything a reminder had no claim on unfolds around
            // the card already standing there, which is what touching it asked for.
            void AdoptPeek()
            {
                if (!peekOpenedByReminder) return;
                peekOpenedByReminder = false;
                _peekDelay?.Cancel();
                peekHideArmed = false;
                peek.Unfold();
                AnswerReminders();
            }

            // The reminder has been seen, so it goes — on the same press, rather than sitting
            // out the rest of its own countdown. The news used to be left alone on its clock
            // while the user got on with the thing it was about: a session started from the
            // rail, the day opened from the card, and the eyebrow still saying "starts in five
            // minutes" over a stopwatch that had been running for ten seconds.
            //
            // Only the queue is dropped. The card lets go of what it is drawing over real
            // frames, which is the fade in QuickPeekWindow, and the widget itself stays: it is
            // the user's now, and Emptied reads that and leaves it standing.
            void AnswerReminders() => toasts.DismissAll();

            // The pointer is back on the handle or on the widget. Anything already under way to
            // put the widget away is called off — including a fade that has started, which the
            // reveal picks up from wherever it got to rather than restarting. Only a widget that
            // timed out comes back this way: one the user closed with the button stays closed,
            // because the pointer crossing something on its way out is not a change of mind.
            void KeepPeek()
            {
                _peekDelay?.Cancel();
                peekHideArmed = false;
                if (peek.IsVisible && peek.IsDismissing && !peek.IsDismissalFinal) peek.Reveal();
            }

            // Hovering does not open the widget any more, but it still decides whether an open
            // one is still wanted: leaving both the handle and the widget starts the grace
            // period, whose length is now the user's to set.
            async void SchedulePeekHide()
            {
                if (!peek.IsVisible) return;
                _peekDelay?.Cancel();
                // The notification owns this appearance and its expiry owns the exit. Applying
                // the much shorter widget hover delay here made the popup disappear early and,
                // depending on the pointer position, left its ownership state inconsistent.
                if (peekOpenedByReminder && toasts.HasAny)
                {
                    peekHideArmed = false;
                    return;
                }
                // The user has said the widget stays. The grace period is the application asking
                // whether a widget that appeared beside the pointer was still wanted; a widget
                // somebody keeps open beside their work has already answered that, and only the
                // two buttons that close it — the petal's, and the widget's own — may take it
                // away. A widget that is on screen because a reminder put it there is not theirs
                // yet and still leaves with the reminder; touching it is what makes it theirs.
                if (viewModel.WidgetKeepOpen && !peekOpenedByReminder)
                {
                    peekHideArmed = false;
                    return;
                }
                _peekDelay?.Dispose();
                _peekDelay = new CancellationTokenSource();
                var token = _peekDelay.Token;
                peekHideArmed = true;
                try
                {
                    await Task.Delay(viewModel.WidgetHideDelay, token);
                    if (token.IsCancellationRequested) return;
                    peekHideArmed = false;
                    // A question this application asked is not something it may then take back
                    // on a timer. Stopping a session raises "how did it go?", and the stop that
                    // raised it can have been pressed on the rail beside the petal — a surface
                    // the pointer then leaves, because the widget it opened is somewhere else.
                    if (viewModel.IsSessionReviewOpen) return;
                    // Answering the widget's "done in part" opens the duration picker, and
                    // reaching for it means leaving the widget. Fading the widget out from
                    // under the question it just asked is not a timeout, it is a bug.
                    if (viewModel.IsTimePickerOpen) return;
                    // A fade already under way is left alone: restarting it would drop the note
                    // that this one was asked for, and the pointer could then bring it back.
                    if (!peek.IsPointerOver && !handle.IsPointerOver && !peek.IsDismissing) peek.BeginDismiss();
                }
                catch (OperationCanceledException) { }
                catch (Exception exception) { crashReporter.Record(exception, "Hiding the quick widget"); }
            }

            // A switch between two panels is played in two halves, never as a cross-fade. These
            // surfaces are translucent, so a panel underneath another one is not hidden by it —
            // it shows through, and the two backgrounds stack into a visibly darker rectangle.
            // So the one that is leaving folds back into the edge it stands against and is off
            // the screen before its replacement unfolds out of the same edge. Both ends of that
            // handover are invisible, so the seam is not a hole.
            //
            // A request made while one is playing is not queued behind it: it is answered by
            // asking the view model again the moment the place is free, so what is finally shown
            // is what was last asked for rather than everything that was clicked on the way.
            var swapInFlight = false;

            void UpdateSidePanelCore()
            {
                try
                {
                    if (swapInFlight) return;
                    PositionWindows();
                    if (!main.IsVisible)
                    {
                        // The shell itself is gone; a panel fading out beside nothing is not a
                        // gesture, it is a leftover.
                        foreach (var (surface, surfaceReveal) in sideSurfaces)
                            if (surface.IsVisible) surfaceReveal.DismissNow(surface.Hide);
                        // And a leftover is closed rather than only taken off the screen. What a
                        // panel is standing on is a flag in the view model, and a flag that
                        // outlives the shell is a panel handed back the next time the user asks
                        // for the shell alone — which is what the shortcut looked like it was
                        // doing. Not while the shell is on its way in: a panel opened in the same
                        // breath as the shell is arriving with it, not left over from before.
                        if (!shellIsArriving) viewModel.CloseShellPanels();
                        return;
                    }

                    Window? wanted = viewModel.IsCalendarPopupOpen ? sidePanel
                        : viewModel.IsAnalyticsOpen ? analyticsWindow
                        : viewModel.IsSettings ? settingsWindow
                        : viewModel.IsChatDocked ? chatWindow
                        : null;

                    // Only one side surface is ever open, so anything up that is not the one
                    // being asked for has to leave. A second one on top of that is a leftover
                    // from a state that should not exist and is simply dropped.
                    Window? leaving = null;
                    WindowRevealAnimator? leavingReveal = null;
                    foreach (var (surface, surfaceReveal) in sideSurfaces)
                    {
                        if (ReferenceEquals(surface, wanted) || !surface.IsVisible) continue;
                        if (leaving is null) { leaving = surface; leavingReveal = surfaceReveal; }
                        else surfaceReveal.DismissNow(surface.Hide);
                    }

                    if (leaving is not null && leavingReveal is not null)
                    {
                        // Nothing is taking its place: this is the panel being shut, and being
                        // shut has its own longer farewell.
                        if (wanted is null) { leavingReveal.Dismiss(leaving.Hide); return; }
                        swapInFlight = true;
                        leavingReveal.Vacate(leaving.Hide, () =>
                        {
                            swapInFlight = false;
                            UpdateSidePanelCore();
                        });
                        return;
                    }

                    if (wanted is not { } arriving) return;

                    var animator = Array.Find(sideSurfaces, entry => ReferenceEquals(entry.Window, arriving)).Reveal;

                    void Place()
                    {
                        PositionWindows();
                        if (!arriving.IsVisible) arriving.Show();
                        PositionWindows();
                        arriving.Opacity = 1;
                        arriving.Activate();
                    }

                    if (animator is null) Place();
                    else animator.Reveal(Place);
                }
                catch (Exception exception) { crashReporter.Record(exception, "Updating side panel"); }
            }

            // Asking for one side surface is three separate answers: the two that are not wanted
            // are switched off one at a time and the one that is arrives last. Run as each of
            // those lands, the placement saw a moment with nothing open at all — so a swap was
            // read as a close followed by an unrelated opening, and the panel being replaced left
            // through the full farewell it would get if the user had simply shut it. The whole
            // change is one thought and is answered once, after it has finished being made.
            var sidePanelUpdatePending = false;
            void UpdateSidePanel()
            {
                if (sidePanelUpdatePending) return;
                sidePanelUpdatePending = true;
                Dispatcher.UIThread.Post(() =>
                {
                    sidePanelUpdatePending = false;
                    UpdateSidePanelCore();
                }, DispatcherPriority.Send);
            }

            // A popup needs something to sit over. For most of them that is the shell; the
            // duration picker also serves the quick widget, which only exists while the shell
            // is away, so it accepts either.
            void UpdatePopup(Window popup, WindowRevealAnimator reveal, Func<bool> shouldShow, string context,
                Func<bool>? hasHost = null)
                => Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    // Re-read the state on the UI thread. Capturing the old bool allowed a
                    // queued close to hide a picker after a newer open request.
                    if (!shouldShow() || !(hasHost?.Invoke() ?? main.IsVisible))
                    {
                        if (popup.IsVisible) reveal.Dismiss(popup.Hide);
                        return;
                    }
                    reveal.Reveal(() =>
                    {
                        PositionWindows();
                        if (!popup.IsVisible) popup.Show();
                        PositionWindows();
                        popup.Topmost = true;
                        popup.Opacity = 1;
                        popup.Activate();
                    });
                }
                catch (Exception exception) { crashReporter.Record(exception, context); }
            });

            void UpdateDatePicker()
                => UpdatePopup(datePicker, datePickerReveal, () => viewModel.IsAnyDatePickerOpen, "Updating date picker");

            void UpdateTimePicker()
                => UpdatePopup(timePicker, timePickerReveal, () => viewModel.IsTimePickerOpen, "Updating time picker",
                    () => main.IsVisible || peek.IsVisible);

            void HideMain(bool force = false) => Dispatcher.UIThread.Post(() =>
            {
                if (!force && (viewModel.PinOpen || viewModel.HasBlockingSurface || sidePanel.IsVisible || settingsWindow.IsVisible
                    || analyticsWindow.IsVisible || chatWindow.IsVisible || datePicker.IsVisible || timePicker.IsVisible)) return;
                try
                {
                    viewModel.CloseShellFlyouts();
                    if (!main.IsVisible) return;
                    ++edgeRequest;
                    mainReveal.Dismiss(() =>
                    {
                        main.Hide();
                        main.Opacity = 1;
                        PositionWindows();
                    });
                }
                catch (Exception exception) { crashReporter.Record(exception, "Hiding shell"); }
            });

            async void HideAfterDelay()
            {
                // The panel is opened by a click now, so it only closes on a click or on exit.
                if (viewModel.PinOpen || warmingUp) return;
                _hideDelay?.Cancel();
                _hideDelay?.Dispose();
                _hideDelay = new CancellationTokenSource();
                try
                {
                    await Task.Delay(1600, _hideDelay.Token);
                    if (!main.IsPointerOver && !sidePanel.IsPointerOver && !settingsWindow.IsPointerOver && !analyticsWindow.IsPointerOver
                        && !chatWindow.IsPointerOver && !datePicker.IsPointerOver && !timePicker.IsPointerOver
                        && !main.IsActive && !sidePanel.IsActive && !settingsWindow.IsActive && !analyticsWindow.IsActive
                        && !chatWindow.IsActive && !datePicker.IsActive && !timePicker.IsActive
                        && !viewModel.HasBlockingSurface)
                    {
                        HideMain();
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception exception) { crashReporter.Record(exception, "Auto-hide delay"); }
            }

            // Whether the shell has ever been on the screen in this session.
            var shellHasOpened = false;
            main.Opened += (_, _) =>
            {
                PositionWindows();
                // A side surface has nothing to stand beside until the shell is up, so one asked
                // for before that — by --section= on the command line, say — is asked for again
                // here rather than being quietly dropped. Once only. This fires on every Show,
                // and from the second one onwards it is the view model that says which panel
                // belongs on the screen; asked again here, every summoning of the shell re-opened
                // whatever panel the view model still had a flag for, so the shortcut for the
                // shell handed back a panel with it.
                if (!shellHasOpened)
                {
                    shellHasOpened = true;
                    if (viewModel.IsShellFlyoutOpen) UpdateSidePanel();
                }
                if (!initialHidePending) return;
                initialHidePending = false;
                Dispatcher.UIThread.Post(() =>
                {
                    if (main.IsVisible) main.Hide();
                    // Whatever the command line asked to open goes away with it. Left standing,
                    // it is exactly the stale flag the summoning path above used to act on.
                    viewModel.CloseShellPanels();
                    FinishStartupSettling();
                });
            };
            main.HideRequested += (_, _) => HideMain(force: true);
            main.PointerReturned += (_, _) => _hideDelay?.Cancel();
            main.UserActivity += (_, _) => _hideDelay?.Cancel();
            main.PointerDeparted += (_, _) => HideAfterDelay();
            main.Activated += (_, _) => _hideDelay?.Cancel();
            main.Deactivated += (_, _) => HideAfterDelay();

            sidePanel.PointerEntered += (_, _) => _hideDelay?.Cancel();
            sidePanel.PointerMoved += (_, _) => _hideDelay?.Cancel();
            sidePanel.PointerExited += (_, _) => HideAfterDelay();
            sidePanel.Activated += (_, _) => _hideDelay?.Cancel();
            sidePanel.Deactivated += (_, _) => HideAfterDelay();

            settingsWindow.PointerEntered += (_, _) => _hideDelay?.Cancel();
            settingsWindow.PointerMoved += (_, _) => _hideDelay?.Cancel();
            settingsWindow.PointerExited += (_, _) => HideAfterDelay();
            settingsWindow.Activated += (_, _) => _hideDelay?.Cancel();
            settingsWindow.Deactivated += (_, _) => HideAfterDelay();

            analyticsWindow.PointerEntered += (_, _) => _hideDelay?.Cancel();
            analyticsWindow.PointerMoved += (_, _) => _hideDelay?.Cancel();
            analyticsWindow.PointerExited += (_, _) => HideAfterDelay();
            analyticsWindow.Activated += (_, _) => _hideDelay?.Cancel();
            analyticsWindow.Deactivated += (_, _) => HideAfterDelay();

            chatWindow.PointerEntered += (_, _) => _hideDelay?.Cancel();
            chatWindow.PointerMoved += (_, _) => _hideDelay?.Cancel();
            chatWindow.PointerExited += (_, _) => HideAfterDelay();
            chatWindow.Activated += (_, _) => _hideDelay?.Cancel();
            chatWindow.Deactivated += (_, _) => HideAfterDelay();

            datePicker.PointerEntered += (_, _) => _hideDelay?.Cancel();
            datePicker.PointerExited += (_, _) => HideAfterDelay();
            datePicker.Activated += (_, _) => _hideDelay?.Cancel();
            datePicker.Deactivated += (_, _) => HideAfterDelay();

            timePicker.PointerEntered += (_, _) => { _hideDelay?.Cancel(); _peekDelay?.Cancel(); };
            timePicker.PointerExited += (_, _) => { HideAfterDelay(); SchedulePeekHide(); };
            timePicker.Activated += (_, _) => { _hideDelay?.Cancel(); _peekDelay?.Cancel(); };
            timePicker.Deactivated += (_, _) => HideAfterDelay();
            main.Closing += (_, args) =>
            {
                if (_exiting || IsSessionEnding(args)) return;
                args.Cancel = true;
                viewModel.CloseShellFlyouts();
                foreach (var (surface, surfaceReveal) in sideSurfaces) surfaceReveal.DismissNow(surface.Hide);
                datePickerReveal.DismissNow(datePicker.Hide);
                timePickerReveal.DismissNow(timePicker.Hide);
                mainReveal.DismissNow(main.Hide);
            };
            // ---- Who has this edge -------------------------------------------------------
            // The petal folds its stopwatch away while the shell or the widget is up, because
            // both leave room for the narrow capsule and its screen gap, rather than the clock.
            //
            // Occupying is immediate and letting go is not, and that asymmetry is the whole
            // point of this. The two surfaces hand over to each other rather than overlapping:
            // the widget is off the screen before the shell begins to arrive, so for two or
            // three frames in the middle of every switch neither of them is visible. Read
            // straight, that gap says "the edge is free" — and the petal would start unfolding
            // its clock and be told to fold it away again before it had finished, twice, every
            // time anything was opened from it. That flinch is what this waits out.
            void ApplyEdgeSuppression()
            {
                // The startup hide and the warm-up both put windows on the screen and take them
                // off again before anybody has asked for anything, and neither is a surface
                // arriving on this edge. Read as one, they make the petal fold its clock away
                // and unfold it again while the application is still starting.
                if (warmingUp || initialHidePending) return;
                handle.SetPanelBridge(main.IsVisible);
                var occupied = main.IsVisible || peek.IsVisible;
                // Cancelled before the early return, not after it: the second half of a
                // hand-over finds the edge already marked as taken, and a release still counting
                // down from the first half is exactly the thing that must not be left running.
                if (occupied) _edgeDelay?.Cancel();
                if (occupied == edgeSuppressed) return;
                _edgeDelay?.Cancel();
                if (occupied)
                {
                    edgeSuppressed = true;
                    handle.SetSuppressed(true);
                    UpdatePetalRail(immediate: true);
                    return;
                }
                ReleaseEdge();
            }

            async void ReleaseEdge()
            {
                _edgeDelay?.Dispose();
                _edgeDelay = new CancellationTokenSource();
                var token = _edgeDelay.Token;
                // Free is not the same as free for good, and no length of wait settles the
                // difference: how long a hand-over leaves this edge empty depends on what is
                // arriving and how much of it there is to lay out, and a fixed delay tuned to
                // one of them is wrong for the next.
                //
                // What does settle it is that both surfaces here are asked for through a
                // counter. If either has moved while we were waiting, the edge is not free —
                // it is between two surfaces — and the wait starts again. On a genuine close
                // nothing asks for anything, the counters stand still, and the clock unfolds
                // once. The cap is there because this is a loop driven by what the user does.
                for (var attempt = 0; attempt < 8; attempt++)
                {
                    var asked = (edgeRequest, peekRequest);
                    try { await Task.Delay(EdgeSettleDelay, token); }
                    catch (TaskCanceledException) { return; }
                    if (token.IsCancellationRequested || _exiting) return;
                    if (main.IsVisible || peek.IsVisible) return;
                    if (asked == (edgeRequest, peekRequest)) break;
                }
                if (main.IsVisible || peek.IsVisible) return;
                edgeSuppressed = false;
                handle.SetSuppressed(false);
                UpdatePetalRail();
            }

            // ---- The petal's rail --------------------------------------------------------
            // Up whenever there is a session to control and the pointer is on either the petal
            // or the keys themselves. Leaving both starts a short grace period rather than
            // taking them away at once: the journey between the two surfaces crosses a gap, and
            // a rail that vanished halfway across it could never be reached.
            void UpdatePetalRail(bool immediate = false)
            {
                if (_exiting) return;
                var wanted = viewModel.IsPetalLive && handle.IsVisible && !edgeSuppressed
                             && (handle.IsHovered || petalRail.IsHovered);
                if (wanted == railWanted && petalRail.IsVisible == wanted && !petalRail.IsDismissing) return;
                railWanted = wanted;
                _railDelay?.Cancel();
                if (wanted)
                {
                    petalRail.Reveal(() =>
                    {
                        // Placed twice, as every surface here is: a window that has not been
                        // shown yet has no size to be placed by, and its first real measurement
                        // arrives with the showing.
                        PositionWindows();
                        if (!petalRail.IsVisible) petalRail.Show();
                        PositionWindows();
                    });
                    return;
                }
                if (!petalRail.IsVisible) return;
                if (immediate)
                {
                    petalRail.Dismiss();
                    return;
                }
                ScheduleRailHide();
            }

            async void ScheduleRailHide()
            {
                _railDelay?.Dispose();
                _railDelay = new CancellationTokenSource();
                var token = _railDelay.Token;
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(260), token);
                    if (token.IsCancellationRequested || _exiting) return;
                    // Asked again rather than trusted: the pointer may have arrived on the other
                    // half of this pair while the grace period was running, and that is the case
                    // the grace period exists for.
                    if (handle.IsHovered || petalRail.IsHovered) return;
                    railWanted = false;
                    petalRail.Dismiss();
                }
                catch (TaskCanceledException) { }
            }

            handle.HoverStarted += (_, _) => UpdatePetalRail();
            handle.HoverEnded += (_, _) => UpdatePetalRail();
            petalRail.PointerReturned += (_, _) => UpdatePetalRail();
            petalRail.PointerDeparted += (_, _) => UpdatePetalRail();
            // Pressing a key changes which keys there are, and the answer to "is the rail still
            // wanted" changes with it — a session that has just been stopped has nothing left to
            // control, and the keys should not sit there waiting to be pressed again.
            viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(viewModel.IsPetalLive)) UpdatePetalRail(immediate: true);
            };
            // Stopping a session does not record anything: it holds the clock and raises the one
            // question this application asks. The rail has nowhere to ask it — it is four round
            // keys — so the widget comes out to carry it, the same widget the petal's other
            // button opens and the same strip it already asks the question in.
            //
            // Hung off the raising rather than off "the question is now open", because the
            // question can be raised again while it already is: closing the widget without
            // answering leaves it open, and the next press of stop would then change no flag,
            // announce nothing, and open nothing at all.
            viewModel.SessionReviewRaised += (_, _) =>
            {
                if (_exiting || main.IsVisible || peek.IsVisible) return;
                ShowPeek();
            };
            petalRail.Closing += (_, args) => { if (!_exiting && !IsSessionEnding(args)) args.Cancel = true; };

            // The petal stands down while either of the two surfaces that share its edge is up.
            // Both of them already carry the session's controls, and an expanded petal would be
            // underneath them — three surfaces where the user asked for one. Watched rather than
            // announced because a window is shown and hidden from a dozen places in this file,
            // and every one of them would have had to remember to say so.
            void WatchEdgeSurface(Window surface) => surface.PropertyChanged += (_, change) =>
            {
                if (change.Property != Visual.IsVisibleProperty) return;
                ApplyEdgeSuppression();
            };
            WatchEdgeSurface(main);
            WatchEdgeSurface(peek);

            handle.PeekRequested += (_, _) => TogglePeek();
            handle.HoverStarted += (_, _) => KeepPeek();
            handle.HoverEnded += (_, _) => SchedulePeekHide();
            handle.ToggleRequested += (_, _) => ToggleShellSurface();

            // The petal's left button, and the shortcut bound to the same thing.
            void ToggleShellSurface()
            {
                // A shell that is on its way out is not a shell that is up: pressing the petal
                // while it fades means "no, come back", not "go away twice".
                if (main.IsVisible && !mainReveal.IsDismissing)
                {
                    HidePeek();
                    HideMain(force: true);
                }
                // Showing the shell takes the widget off the screen itself, and waits for it.
                else ShowMainActivated();
            }

            peek.PointerReturned += (_, _) =>
            {
                KeepPeek();
                // Nothing in the widget lapses under the pointer, reminders included: a card
                // being read must not go out from under the eye that is reading it.
                toasts.IsHeld = true;
            };
            peek.PointerDeparted += (_, _) =>
            {
                toasts.IsHeld = false;
                // Anything that moved the widget while the pointer was inside it left it off the
                // centre of its edge deliberately, to keep what was under the pointer where it
                // was. There is nothing under the pointer now.
                if (peekOffCentre && peek.IsVisible && !peek.IsDismissing) PositionPeek(recentre: true);
                SchedulePeekHide();
            };
            // Pressed anywhere inside it: whatever put the widget on screen, it is being used now.
            // Any press inside the widget. AdoptPeek answers for a widget a reminder put on the
            // screen; this second call is for the other case — the widget was already the
            // user's and the reminder landed on it — where there is nothing to adopt but the
            // press still means the news has been seen.
            peek.Engaged += (_, _) =>
            {
                AdoptPeek();
                AnswerReminders();
            };
            peek.DismissRequested += (_, _) =>
            {
                _peekDelay?.Cancel();
                toasts.IsHeld = false;
                peek.BeginDismiss(final: true);
            };
            // A reminder was pressed. It is about one event, and the answer to "what about it?"
            // is the day it is in — so the shell opens on it and the row lights up, which is the
            // same mark a reminder leaves when the shell was open all along.
            peek.ReminderOpened += (_, item) =>
            {
                toasts.Open(item);
                ShowMainActivated();
                Dispatcher.UIThread.Post(() => viewModel.FlashEvent(item.EventId), DispatcherPriority.Background);
            };

            // ---- Reminders ---------------------------------------------------------------
            // A reminder is not a surface of its own, nor a card inside one, nor a strip grown
            // underneath one: it is the widget's event card wearing what has just been said
            // about the event it is already describing. Which leaves one question — who shows
            // it — and that depends entirely on where the user already is:
            //
            //   the shell is open   the widget stays away — the two stand on the same edge, and
            //                       two translucent surfaces there make one dark plate rather
            //                       than two cards. The event lights up in the day instead, and
            //                       the sound plays at full volume.
            //   the widget is open  the card it is already showing puts the reminder on. No
            //                       window is raised, no entrance is replayed, nothing changes
            //                       size and every block stays exactly where it was. The sound
            //                       is played softly, because the user is looking straight at
            //                       the surface the news is arriving on.
            //   neither             the widget unfolds out of the petal in its compact shape —
            //                       the card and the countdown under it, which between them are
            //                       the whole of "this starts in five minutes" — and leaves with
            //                       the reminder rather than staying on as a dashboard nobody
            //                       asked for.
            //
            // Whichever of the three answers, the user can take the widget over at any point in
            // it — the petal's widget button, or a press anywhere inside — and from then on it
            // is theirs, it stays, and the rest of it unfolds around the card.
            //
            void PlayReminderSound(bool soft)
            {
                if (!notificationCenter.IsSoundEnabled) return;
                NotificationSound.Play(soft ? NotificationSound.SoftGain : 1.0);
            }

            // The screen is what it is going to be: a background start has finished putting the
            // shell away. Anything raised while that was still happening is delivered now, and
            // reaches whichever surface is actually on screen rather than the one that was about
            // to leave it.
            void FinishStartupSettling()
            {
                if (!settlingStartup) return;
                settlingStartup = false;
                if (reminderBacklog.Count == 0) return;
                var queued = reminderBacklog.ToArray();
                reminderBacklog.Clear();
                foreach (var notification in queued) DeliverReminder(notification);
            }

            void DeliverReminder(AppNotification notification)
            {
                if (_exiting) return;
                if (settlingStartup)
                {
                    reminderBacklog.Add(notification);
                    return;
                }
                // Three surfaces can answer for one reminder and only one of them ever does, so
                // which one it was is the first thing worth knowing when it answers wrongly.
                crashReporter.Diagnostic(
                    $"Reminder \"{notification.Title}\" shell={main.IsVisible} widget={peek.IsVisible} " +
                    $"dismissing={peek.IsDismissing} event={notification.EventId}");
                // The card goes into the widget's list either way. Even when the shell is what
                // answers for it, a reminder raised a moment ago should still be there if the
                // user reaches for the petal straight afterwards.
                toasts.Post(notification);

                if (main.IsVisible && !mainReveal.IsDismissing)
                {
                    viewModel.MarkReminder(notification);
                    PlayReminderSound(soft: false);
                    return;
                }

                if (peek.IsVisible && !peek.IsDismissing)
                {
                    // Already open. The event block grows around the card where it stands and
                    // nothing else happens — no window is raised and no entrance is replayed.
                    // The sound is played softly only when the widget is the user's own: they
                    // are looking straight at the surface the news arrives on. A widget that put
                    // itself there for an earlier reminder has no such claim on their attention,
                    // so that one is announced at full volume.
                    PlayReminderSound(soft: !peekOpenedByReminder);
                    // A grace period that was already counting down is restarted from here so it
                    // cannot take the reminder away half-read; one that was never running is not
                    // started, because this widget is the user's.
                    if (peekHideArmed) SchedulePeekHide();
                    return;
                }

                // Nothing is on the screen, or the widget is halfway out of it. Either way this
                // is the widget's own arrival, in the shape a reminder is entitled to: the event
                // card and the countdown under it, and none of the blocks that describe the day.
                // Showing it reverses a fade that had already started rather than beginning a
                // second one on top of it — which is also why the shape is only set for a widget
                // that is actually away: taking blocks off a card that is already half faded
                // reads as the thing breaking rather than as a notification arriving.
                peekOpenedByReminder = true;
                if (!peek.IsVisible) viewModel.IsPeekCompact = true;
                ShowPeek();
                PlayReminderSound(soft: false);
            }

            // Off the screen, it stops being the reminder's; the next one starts this over. The
            // hold goes with it: a widget put away under the pointer never sees it leave, and a
            // held card never lapses — so the reminders would sit in a list nobody can see.
            peek.Retired += (_, _) =>
            {
                peekOpenedByReminder = false;
                peekShape = -1;
                peekOffCentre = false;
                toasts.IsHeld = false;
                // If the last item expired or was dismissed, its rendered copy was kept during
                // the exit so the dashboard could not replace it and resize the window mid-air.
                toasts.ReleasePresentation();
            };
            // The last card has gone. A widget that opened itself to carry it has nothing left
            // to say and goes with it; one the user has taken over — see AdoptPeek — is theirs
            // and stays, keeping every block it had once the reminder folds out of it.
            toasts.Emptied += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                // Emptied is intentionally handled one dispatcher turn later. A newer reminder
                // may have arrived in that gap; the stale empty event must not close its card.
                if (toasts.HasAny) return;
                if (!peekOpenedByReminder || !peek.IsVisible || peek.IsDismissing)
                {
                    // The widget is the user's, or it is not on screen to leave. Either way the
                    // reminder goes and the widget it was on does not: the card lets go of the
                    // accent and the hairline and goes back to its own eyebrow. Ending the
                    // presentation is what starts that; the card says when it has finished,
                    // because until then the reminder is still drawn on it.
                    toasts.EndPresentation();
                    return;
                }
                if (_exiting) return;
                // The widget is here only because of this reminder, so the two leave together
                // rather than the card falling back to a dashboard nobody asked for and the
                // window then going as well. It must finish even if the pointer happens to
                // rest over the petal; the hover hold has already prevented a timed expiry while
                // the pointer was over the widget itself.
                _peekDelay?.Cancel();
                peek.BeginDismiss();
            });
            notificationCenter.Posted += (_, notification) =>
                Dispatcher.UIThread.Post(() => DeliverReminder(notification));
            // The widget's "done in part" question is a window of its own, and while the shell
            // is away the widget is the only thing it has to sit over. Whether it still does was
            // only ever asked when the question itself changed, so a widget that went away left
            // the question standing on the bare desktop with nothing to answer it about. Read a
            // turn later than the widget's own leaving, so the petal handing the screen from the
            // widget to the shell is not mistaken for the widget leaving nothing behind.
            peek.Retired += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                if (_exiting || peek.IsVisible || main.IsVisible || !viewModel.IsTimePickerOpen) return;
                viewModel.CloseTimePickerCommand.Execute(null);
            }, DispatcherPriority.Background);
            // The widget is a different size with an event on than without one, so it re-aligns
            // itself with the petal whenever what it is showing changes shape. Not while it is
            // being prepared, though: the preparation lays it out off every screen, and that
            // layout would otherwise pull it back onto one.
            peek.SizeChanged += (_, args) => { if (!warmingUp) PositionPeek(measuredSize: args.NewSize); };
            viewModel.ShellRevealRequested += (_, _) => ShowMainActivated();
            handle.Closing += (_, args) => { if (!_exiting && !IsSessionEnding(args)) args.Cancel = true; };
            sidePanel.Closing += (_, args) =>
            {
                if (_exiting || IsSessionEnding(args)) return;
                args.Cancel = true;
                viewModel.CloseCalendarCommand.Execute(null);
                sidePanel.Hide();
            };
            settingsWindow.Closing += (_, args) =>
            {
                if (_exiting || IsSessionEnding(args)) return;
                args.Cancel = true;
                viewModel.CloseSettingsCommand.Execute(null);
                settingsWindow.Hide();
            };
            analyticsWindow.Closing += (_, args) =>
            {
                if (_exiting || IsSessionEnding(args)) return;
                args.Cancel = true;
                viewModel.CloseAnalyticsCommand.Execute(null);
                analyticsWindow.Hide();
            };
            chatWindow.Closing += (_, args) =>
            {
                if (_exiting || IsSessionEnding(args)) return;
                args.Cancel = true;
                viewModel.CloseChatCommand.Execute(null);
                chatWindow.Hide();
            };
            datePicker.Closing += (_, args) =>
            {
                if (_exiting || IsSessionEnding(args)) return;
                args.Cancel = true;
                viewModel.CloseDatePickerCommand.Execute(null);
                datePicker.Hide();
            };
            timePicker.Closing += (_, args) =>
            {
                if (_exiting || IsSessionEnding(args)) return;
                args.Cancel = true;
                viewModel.CloseTimePickerCommand.Execute(null);
                timePicker.Hide();
            };
            viewModel.PropertyChanged += (_, args) =>
            {
                // The assistant's two flags are watched rather than the shape they add up to.
                // A panel asked for only through a derived property is a panel that goes
                // missing the moment one of the callers sets the flag underneath it without
                // announcing the derivation.
                if (args.PropertyName is nameof(EdgeShellViewModel.IsSettings) or nameof(EdgeShellViewModel.IsCalendarPopupOpen)
                    or nameof(EdgeShellViewModel.IsAnalyticsOpen) or nameof(EdgeShellViewModel.IsChatOpen)
                    or nameof(EdgeShellViewModel.IsChatExpanded))
                    UpdateSidePanel();
                if (args.PropertyName == nameof(EdgeShellViewModel.IsAnyDatePickerOpen)) UpdateDatePicker();
                if (args.PropertyName is nameof(EdgeShellViewModel.IsTimePickerOpen)
                    or nameof(EdgeShellViewModel.Use24HourClock))
                    UpdateTimePicker();
                // An event starting while the widget is on screen adds two blocks and the rail
                // to it. They should arrive the way they do on any other reveal, not appear.
                if (args.PropertyName == nameof(EdgeShellViewModel.HasPeekEvent) && peek.IsVisible)
                    Dispatcher.UIThread.Post(() => { if (peek.IsVisible && !peek.IsDismissing) peek.ReplayReveal(); });
            };
            viewModel.ShellSettingsChanged += (_, _) => ApplyShellSettings();

            // ---- Shortcuts ---------------------------------------------------------------
            // One map, claimed twice. The Windows claim takes the keys from the whole desktop;
            // the other is an ordinary key binding on this application's own windows, which is
            // all macOS and the Wayland compositors will let a client have — there a shortcut
            // works while a DAYDRIVE window has focus, and a desktop-wide binding is something
            // the user writes in the compositor's own configuration. Nothing fires twice on
            // Windows: a registered hotkey is taken before the key ever reaches the window.
            var hotkey = (DesktopGlobalHotkeyService)_services.GetRequiredService<IGlobalHotkeyService>();
            var hotkeyInitialized = false;
            Window[] shortcutHosts = [main, sidePanel, settingsWindow, analyticsWindow, chatWindow, peek];
            main.Opened += (_, _) =>
            {
                if (hotkeyInitialized) return;
                hotkeyInitialized = true;
                hotkey.Attach(main);
                ApplyShortcuts();
            };
            viewModel.ShortcutsChanged += (_, _) => ApplyShortcuts();
            // A row that is listening for a combination has to be able to hear it. A registered
            // hotkey is taken before the key reaches any window, so rebinding "toggle the widget"
            // to something starting with the combination it already has would open the widget
            // instead of being read — the claim is dropped for as long as the page is listening.
            viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != nameof(EdgeShellViewModel.IsCapturingShortcut)) return;
                if (viewModel.IsCapturingShortcut) hotkey.Unregister();
                else if (hotkeyInitialized) ApplyShortcuts();
            };

            void RunShortcut(ShellShortcutAction action) => RunOnUiThread(() =>
            {
                // Mid-rebind every key press belongs to the row that is listening.
                if (viewModel.IsCapturingShortcut) return;
                switch (action)
                {
                    case ShellShortcutAction.ToggleShell: ToggleShellSurface(); break;
                    case ShellShortcutAction.ToggleWidget: TogglePeek(); break;
                    case ShellShortcutAction.ToggleAssistant:
                        // The assistant lives inside the shell, so asking for it asks for the
                        // shell too — but only on the way in. Closing it must not summon the
                        // window the user is closing something inside. The shell is told a panel
                        // is coming with it, which is the difference between this and every other
                        // way the shell is summoned: without that the assistant is read as a
                        // leftover from the last time the shell was up, and closed again before
                        // the shell it asked for has even arrived.
                        if (!viewModel.IsChatOpen) ShowMain(activate: true, withPanel: true);
                        viewModel.ToggleChatCommand.Execute(null);
                        break;
                    case ShellShortcutAction.StartOrPauseSession:
                        viewModel.ToggleSessionCommand.Execute(null);
                        break;
                    case ShellShortcutAction.StopSession:
                        // Stopping a session asks how it went, and that question is a sheet
                        // inside the shell. Asked while the shell is away it is asked of nobody:
                        // the sheet stands there unseen, and is what the shell hands back the
                        // next time somebody summons it.
                        if (viewModel.IsSessionOpen) ShowMainActivated();
                        viewModel.StopEventCommand.Execute(null);
                        break;
                    case ShellShortcutAction.ToggleDoNotDisturb:
                        viewModel.ToggleDoNotDisturbCommand.Execute(null);
                        break;
                }
            });

            void ApplyShortcuts()
            {
                var claims = new List<GlobalHotkey>();
                foreach (var window in shortcutHosts) window.KeyBindings.Clear();
                foreach (var (action, gesture) in viewModel.ShortcutMap)
                {
                    if (gesture.Length == 0) continue;
                    void Run() => RunShortcut(action);
                    claims.Add(new GlobalHotkey(action, gesture, Run));
                    if (ShortcutGesture.ToKeyGesture(gesture) is not { } keyGesture) continue;
                    foreach (var window in shortcutHosts)
                        window.KeyBindings.Add(new KeyBinding { Gesture = keyGesture, Command = new ShortcutCommand(Run) });
                }
                // Focus mode takes the desktop-wide claims away and leaves the bindings above
                // standing. That is the whole of it: a key this application holds is a key the
                // game never receives, and the bindings inside the shell's own windows can only
                // fire while one of those windows has focus — which is to say while the game
                // does not. Nothing is reported to the settings page from here either; what it
                // shows is what the system made of the real claims, and a set that was never
                // offered has nothing to say about that.
                if (viewModel.IsFocusHoldingShortcuts)
                {
                    hotkey.Unregister();
                    return;
                }
                var refused = hotkey.Apply(claims);
                viewModel.ReportShortcutRegistration(hotkey.IsSupported, refused);
                if (refused.Count > 0)
                    crashReporter.Diagnostic($"Shortcuts refused by the system: {string.Join(", ", refused)}");
            }

            // The icon is attached to the main window's message loop, so it is created once that
            // window has a handle. Quick add is gone from the menu: the same click opens the shell,
            // which is where anything is added anyway.
            var localization = _services.GetRequiredService<LocalizationService>();
            IAppTrayService tray = OperatingSystem.IsWindows()
                ? new Win32TrayService(main)
                : new AvaloniaTrayService();
            if (tray is Win32TrayService windowsTray)
                windowsTray.MenuVisibilityChanged += (_, open) =>
                {
                    // The handle asks for the front of the always-on-top band every few seconds.
                    // While the menu is up, it must not: that is the whole reason the tray menu
                    // used to open behind the shell.
                    _trayMenuOpen = open;
                    if (!open) LiftHandle();
                };

            async void ExitFromTray()
            {
                await viewModel.FlushSettingsAsync();
                // Last chance to push this session's work; a failure must not block the exit.
                await viewModel.SyncBeforeExitAsync();
                _exiting = true;
                tray.Dispose();
                desktop.Shutdown();
            }

            // A downloaded cloud copy only takes effect on a fresh start. The next process is
            // told to wait for this one, so the database is free by the time it looks for it.
            viewModel.RestartRequested += async (_, _) =>
            {
                await viewModel.FlushSettingsAsync();
                if (!AppInfo.TryRestart())
                {
                    viewModel.ReportRestartFailed();
                    return;
                }
                _exiting = true;
                tray.Dispose();
                desktop.Shutdown();
            };

            // Five lines, whatever state the shell is in, and a sixth while cloud sync is on.
            // Every length a quiet spell can be set to used to be a line of its own, which made
            // the menu twice the height of a menu for a choice nobody makes most times they
            // open it; they live behind one line that opens now, with what it is currently set
            // to said on that line.
            IReadOnlyList<TrayMenuEntry> BuildTrayMenu()
            {
                viewModel.ExpireDoNotDisturb();
                var quiet = new List<TrayMenuEntry>();
                if (viewModel.IsDoNotDisturb)
                    quiet.Add(new(localization.Text("DoNotDisturbEnd"),
                        () => viewModel.EndDoNotDisturbCommand.Execute(null)));
                foreach (var minutes in EdgeShellViewModel.DoNotDisturbLengths)
                    quiet.Add(new(viewModel.FormatQuietLength(minutes),
                        () => viewModel.StartDoNotDisturbCommand.Execute(minutes.ToString()),
                        IsChecked: viewModel.IsDoNotDisturb && viewModel.DoNotDisturbMinutes == minutes));

                // Focus mode gets a line of the same shape, and for the same reason: switching it
                // on by hand and switching off the noticing are two different answers, and
                // neither is worth a line of the top menu on its own.
                var focus = new List<TrayMenuEntry>
                {
                    new(localization.Text("FocusModeHold"),
                        () => viewModel.ToggleFocusModeHoldCommand.Execute(null),
                        IsChecked: viewModel.IsFocusModeHeld)
                };
                if (focusWatcher.IsSupported)
                    focus.Add(new(localization.Text("FocusModeAuto"),
                        () => viewModel.FocusModeEnabled = !viewModel.FocusModeEnabled,
                        IsChecked: viewModel.FocusModeEnabled));

                var entries = new List<TrayMenuEntry>
                {
                    new(localization.Text("OpenApp"), ShowMainActivated),
                    TrayMenuEntry.Separator,
                    new(localization.Text("DoNotDisturb"), IsChecked: viewModel.IsDoNotDisturb)
                    {
                        Detail = viewModel.IsDoNotDisturb
                            ? viewModel.FormatQuietLength(viewModel.DoNotDisturbMinutes)
                            : localization.Text("DoNotDisturbOffShort"),
                        Submenu = quiet
                    },
                    new(localization.Text("FocusMode"), IsChecked: viewModel.IsFocusMode)
                    {
                        Detail = viewModel.FocusModeTrayDetail,
                        Submenu = focus
                    },
                    new(localization.Text("NotificationSound"),
                        () => viewModel.ToggleNotificationSoundCommand.Execute(null),
                        IsChecked: viewModel.NotificationSoundEnabled)
                };
                // Where the data stands, one glance from anywhere: the line says how the last
                // sync went, and choosing it syncs now.
                if (!viewModel.IsCloudSyncOff)
                    entries.Add(new(localization.Text("CloudTrayLabel"), () => viewModel.SyncNowCommand.Execute(null),
                        IsEnabled: !viewModel.IsCloudSyncBusy) { Detail = viewModel.CloudTrayDetail });
                entries.Add(TrayMenuEntry.Separator);
                entries.Add(new(localization.Text("Exit"), ExitFromTray));
                return entries;
            }

            // The icon needs the window's handle, and Opened is raised again every time the
            // hidden shell is shown; the icon is only asked for once.
            var trayStarted = false;
            main.Opened += (_, _) =>
            {
                if (trayStarted) return;
                trayStarted = true;
                tray.Start(ShowMainActivated, BuildTrayMenu);
                if (tray is Win32TrayService started)
                    crashReporter.Diagnostic($"Tray icon registered: {started.IsActive}");
            };
            viewModel.NotificationPreferencesChanged += (_, _) =>
            {
                notificationCenter.ApplyPreferences(
                    viewModel.NotificationsEnabled, viewModel.NotificationSoundEnabled,
                    viewModel.DoNotDisturbUntilUtc, viewModel.DoNotDisturbUntilResumed);
                tray.Refresh();
            };

            // ---- Standing back while something else has the screen -----------------------
            // Two halves, and the shell only ever performs them from here: the shortcuts hang
            // off a window handle, and whether a reminder waits is the notification centre's to
            // be told. The view model owns the switches and the sentence that describes them.
            viewModel.AreFullScreenAppsDetected = focusWatcher.IsSupported;
            crashReporter.Diagnostic(focusWatcher.IsSupported
                ? "Focus mode can see what else is on screen"
                : $"Focus mode is hand-held only: {focusWatcher.UnsupportedReason}");
            focusWatcher.Changed += (_, state) =>
            {
                viewModel.ReportFocusMode(state);
                ApplyFocusMode();
                crashReporter.Diagnostic(
                    $"Focus mode {state.Trigger}: app={state.AppName} shortcuts={viewModel.IsFocusHoldingShortcuts} " +
                    $"reminders={viewModel.IsFocusHoldingReminders}");
            };
            viewModel.FocusModePreferencesChanged += (_, _) =>
            {
                // Configure asks the question again at once rather than waiting for the next
                // tick, so a switch thrown while a game is already running is answered now.
                // Anything it decides comes back through Changed; what is settled here is the
                // half that depends on the switches alone rather than on what is on screen.
                focusWatcher.Configure(viewModel.FocusModeEnabled, viewModel.IsFocusModeHeld);
                ApplyFocusMode();
            };

            void ApplyFocusMode()
            {
                notificationCenter.ApplyFocusMode(viewModel.IsFocusHoldingReminders);
                if (hotkeyInitialized) ApplyShortcuts();
                ApplyHandleVisibility();
                tray.Refresh();
            }

            // The third part of standing back, and the one the other two cannot do anything
            // about. Letting go of a shortcut stops the shell answering a key it was never
            // sent; the petal is not a message, it is a strip of the right-hand edge of the
            // screen that goes on swallowing a click — and the right-hand edge is exactly where
            // a mouse flung at something in a hurry ends up. So it leaves, and comes back the
            // moment focus mode lifts.
            //
            // Hidden rather than made inert, deliberately: a petal that is still drawn but no
            // longer answers is a control that has silently stopped working, which is worse
            // than one that is visibly not there. And the way back in is not lost with it — the
            // tray icon opens the shell, and un-holding focus mode from the tray puts the petal
            // straight back.
            void ApplyHandleVisibility()
            {
                if (_exiting || !handleStarted) return;
                try
                {
                    if (viewModel.IsFocusHidingHandle)
                    {
                        // The widget goes with it. It is the petal's own surface — opened from
                        // it, standing beside it, always on top whatever the user has said
                        // about the shell — so a widget left up would be the one thing still
                        // drawn over the game after everything that puts things there has been
                        // switched off.
                        if (peek.IsVisible) HidePeek();
                        if (petalRail.IsVisible) petalRail.Dismiss();
                        if (handle.IsVisible) handle.Hide();
                        return;
                    }
                    if (handle.IsVisible) return;
                    handle.Show();
                    PositionWindows();
                    LiftHandle();
                }
                catch (Exception exception) { crashReporter.Record(exception, "Taking the petal off the screen"); }
            }

            // The screen came back, and these were raised while it belonged to somebody else.
            // They arrive as one card rather than as a burst: four reminders that each once had
            // a moment are, by the time the game closes, one piece of news about a stretch of
            // time that has already passed. A single held reminder is simply itself — there is
            // nothing to summarize, and the card it would have been is the card it becomes.
            notificationCenter.HeldReleased += (_, held) => RunOnUiThread(() =>
            {
                if (_exiting || held.Count == 0) return;
                if (held.Count == 1)
                {
                    DeliverReminder(held[0]);
                    return;
                }
                // The last one's event is the one the card points at: of the several that went
                // by, it is the only one whose moment might still be ahead.
                DeliverReminder(new AppNotification(
                    localization.Text("FocusModeHeldTitle"),
                    string.Join(" · ", held.Select(item => item.Body)),
                    DateTimeOffset.Now,
                    held[^1].EventId));
            });

            // ---- Leaving because the system is ------------------------------------------
            // Every window in this shell answers a close request with "no": hiding rather than
            // closing is what keeps the application alive in the tray with nothing on screen.
            // Windows asks each top-level window for permission before it ends the session, and
            // one window refusing stops the shutdown for the whole machine — which is why the
            // computer could only be turned off by holding the button down. The session request
            // is turned into a real exit here, before any of those handlers get to answer it.
            desktop.ShutdownRequested += (_, _) => BeginSessionExit();

            void BeginSessionExit()
            {
                if (_exiting) return;
                _exiting = true;
                _hideDelay?.Cancel();
                _peekDelay?.Cancel();
                _railDelay?.Cancel();
                _edgeDelay?.Cancel();
                try { hotkey.Dispose(); } catch (Exception exception) { crashReporter.Record(exception, "Releasing the global hotkey"); }
                try { focusWatcher.Dispose(); } catch (Exception exception) { crashReporter.Record(exception, "Stopping the focus mode watcher"); }
                try { tray.Dispose(); } catch (Exception exception) { crashReporter.Record(exception, "Removing the tray icon"); }
                // A few seconds is all the operating system allows, so only the local write is
                // waited for. The cloud push a deliberate exit performs would spend that entire
                // budget on the network and still be cut off halfway.
                FlushSettingsWithin(TimeSpan.FromSeconds(2));
            }

            // The save is asynchronous and its continuations belong to the UI thread — the very
            // thread that is waiting here — so the dispatcher is pumped rather than blocked on.
            void FlushSettingsWithin(TimeSpan budget)
            {
                try
                {
                    var flush = viewModel.FlushSettingsAsync();
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    while (!flush.IsCompleted && clock.Elapsed < budget)
                    {
                        Dispatcher.UIThread.RunJobs();
                        Thread.Sleep(8);
                    }
                }
                catch (Exception exception) { crashReporter.Record(exception, "Saving settings as the session ends"); }
            }

            _services.GetRequiredService<ReminderService>().Start();
            ApplyShellSettings();
            handleStarted = true;
            // Focus mode may already have been on by the time the settings were applied — held
            // by hand is not remembered, but a game that was already running is noticed on the
            // very first ask. Asking here rather than showing unconditionally is what stops the
            // petal appearing over it for the one frame before the next tick takes it away.
            if (!viewModel.IsFocusHidingHandle) handle.Show();
            // Other applications join the always-on-top band too — a player going full screen, an
            // installer, a game — and each one that does lands in front of the petal. Nothing
            // tells us when that happens, so the petal simply asks for its place back now and
            // then. Two SetWindowPos calls every few seconds is nothing, and the alternative is
            // a shell with no way back onto the screen.
            var handleGuard = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            handleGuard.Tick += (_, _) => LiftHandle();
            // The rail leaves when the pointer leaves, and the pointer leaving is something the
            // desktop has to say out loud. It does not always: a pointer that jumps, a window
            // that takes the capture, a session that ends under the hand — any of those can
            // leave the keys standing beside a petal nobody is pointing at. Nothing here is a
            // substitute for the events, and this never fires in the ordinary case; it is only
            // the answer to "and if the message never came".
            handleGuard.Tick += (_, _) =>
            {
                if (petalRail.IsVisible && !petalRail.IsDismissing
                    && !handle.IsHovered && !petalRail.IsHovered) UpdatePetalRail(immediate: true);
            };
            handleGuard.Start();
            LinuxDesktopControl.Current?.Attach(arguments =>
            {
                if (arguments.Contains("--exit", StringComparer.OrdinalIgnoreCase)) { ExitFromTray(); return; }
                if (LinuxDesktopControl.RequestedAction(arguments) is { } action)
                {
                    if (!viewModel.IsFocusHoldingShortcuts) RunShortcut(action);
                    return;
                }
                var section = arguments.FirstOrDefault(a => a.StartsWith("--section=", StringComparison.OrdinalIgnoreCase))?
                    ["--section=".Length..].ToLowerInvariant();
                ShowMain(activate: true, withPanel: section is not null);
                switch (section)
                {
                    case "tasks": viewModel.ShowTasksCommand.Execute(null); break;
                    case "calendar": viewModel.ShowCalendarCommand.Execute(null); break;
                    case "settings": viewModel.ShowSettingsCommand.Execute(null); break;
                    case "analytics": if (!viewModel.IsAnalyticsOpen) viewModel.ToggleAnalyticsCommand.Execute(null); break;
                    case "assistant": if (!viewModel.IsChatOpen) viewModel.ToggleChatCommand.Execute(null); break;
                }
            });
            if (viewModel.IsShellFlyoutOpen) UpdateSidePanel();
            if (startsInBackground)
            {
                foreach (var (surface, surfaceReveal) in sideSurfaces) surfaceReveal.DismissNow(surface.Hide);
                // Depending on platform timing the main window may not receive Opened until
                // the first reveal attempt. Finish the startup-only hide independently so
                // that the first real user interaction is never consumed by initialization.
                Dispatcher.UIThread.Post(async () =>
                {
                    try
                    {
                        await Task.Delay(350);
                        if (main.IsVisible) main.Hide();
                        viewModel.CloseShellPanels();
                        initialHidePending = false;
                        FinishStartupSettling();
                    }
                    catch (Exception exception) { crashReporter.Record(exception, "Completing hidden startup"); }
                });
            }

            // ---- Paying for the windows before they are wanted ---------------------------
            // The first time one of these is shown it has to build its template tree, resolve
            // every style against it and lay the whole thing out. For the calendar and the
            // analytics panel that is most of what opening them costs, and until now it was paid
            // on the click — which is exactly the pause between pressing a petal and seeing the
            // window. It is done here instead, while nothing is waiting: each window is shown
            // off every screen, with nothing drawn and no focus taken, and put away again in the
            // same breath. One window per idle turn, so the warm-up itself never reads as a stall.
            //
            // The widget was left out of this and paid the same bill on its first opening — and
            // it pays more of it than anything else, because SizeToContent means the window
            // cannot even be placed until it has been measured.
            foreach (var window in new Window[] { sidePanel, analyticsWindow, settingsWindow, peek, datePicker, timePicker })
            {
                var target = window;
                Dispatcher.UIThread.Post(() => WarmUp(target), DispatcherPriority.ApplicationIdle);
            }

            void WarmUp(Window window)
            {
                if (_exiting || window.IsVisible) return;
                var activated = window.ShowActivated;
                var opacity = window.Opacity;
                var position = window.Position;
                warmingUp = true;
                try
                {
                    window.ShowActivated = false;
                    window.Opacity = 0;
                    // Far outside any monitor, so nothing of this can reach the screen even if a
                    // compositor decides to draw a frame before the window is put away.
                    window.Position = new PixelPoint(-32000, -32000);
                    window.Show();
                    window.UpdateLayout();
                }
                catch (Exception exception) { crashReporter.Record(exception, "Preparing a side window"); }
                finally
                {
                    try { window.Hide(); }
                    catch (Exception exception) { crashReporter.Record(exception, "Putting a prepared window away"); }
                    window.Opacity = opacity;
                    window.ShowActivated = activated;
                    window.Position = position;
                    warmingUp = false;
                    // Whatever the edge looked like was ignored for the length of that, so it is
                    // asked for again now that the window is back where it belongs.
                    ApplyEdgeSuppression();
                }
            }

            // ---- The last thing that runs ------------------------------------------------
            // Raised from inside the window procedure that is handling Windows' own end-of-session
            // message, and nothing above it catches: an exception thrown here does not fail a
            // step, it kills the process — an "unknown software exception" box on a machine that
            // is trying to turn itself off, which is the last place a user can do anything about
            // it. So every step is taken on its own, and none of them may take the others down:
            // whatever fails, the rest of the teardown still happens and the exit stays quiet.
            desktop.Exit += (_, _) =>
            {
                _exiting = true;
                Step(handleGuard.Stop, "Stopping the petal guard");
                Step(() =>
                {
                    _hideDelay?.Cancel();
                    _hideDelay?.Dispose();
                    _hideDelay = null;
                    _peekDelay?.Cancel();
                    _peekDelay?.Dispose();
                    _peekDelay = null;
                }, "Cancelling the pending fades");
                foreach (var window in new Window[] { peek, sidePanel, analyticsWindow, datePicker, timePicker })
                {
                    var target = window;
                    Step(target.Close, "Closing a window on exit");
                }

                Step(() => { _services?.Dispose(); _services = null; }, "Disposing the application services");

                void Step(Action work, string context)
                {
                    try { work(); }
                    catch (Exception exception) { crashReporter.Record(exception, context); }
                }
            };
            }
            catch (Exception exception)
            {
                try
                {
                    var reporter = _services?.GetService<CrashReporter>() ?? new CrashReporter(
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IntelligentScheduler"));
                    reporter.Record(exception, "Application startup");
                }
                catch { }
                var localization = _services?.GetService<LocalizationService>();
                desktop.MainWindow = new Window
                {
                    Title = "DAYDRIVE",
                    Width = 460,
                    Height = 230,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    Content = new StackPanel
                    {
                        Margin = new Thickness(24),
                        Spacing = 12,
                        Children =
                        {
                            new TextBlock { Text = localization?.Text("StartupFailed") ?? "DAYDRIVE could not start", FontSize = 20, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                            new TextBlock { Text = exception.Message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                            new TextBlock { Text = localization?.Text("CrashLogSaved") ?? "Details were saved to daydrive-crash.log in the application data folder.", Opacity = 0.65, TextWrapping = Avalonia.Media.TextWrapping.Wrap }
                        }
                    }
                };
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Waits out a task on the UI thread before Avalonia's own loop exists, by pumping the
    /// dispatcher rather than blocking on it. Used only by the offscreen snapshot sweep, which
    /// runs to completion and then exits the process.
    /// </summary>
    private static void RunBeforeTheLoopStarts(Task work)
    {
        while (!work.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }

        work.GetAwaiter().GetResult();
    }

    /// <summary>
    /// True when this close is the operating system or the lifetime taking the application
    /// down rather than the user dismissing a window. Cancelling one of these is what blocks a
    /// Windows shutdown, so every handler that normally hides instead of closing steps aside.
    /// </summary>
    private static bool IsSessionEnding(WindowClosingEventArgs args)
        => args.CloseReason is WindowCloseReason.OSShutdown or WindowCloseReason.ApplicationShutdown;

    internal static ServiceProvider BuildServices()
    {
        var dataDirectory = DataDirectoryResolver.Resolve(Environment.GetCommandLineArgs(),
            Environment.GetEnvironmentVariable("INTELLIGENT_SCHEDULER_DATA_DIR"),
            AppContext.BaseDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        Directory.CreateDirectory(dataDirectory);
        var dbPath = Path.Combine(dataDirectory, "intelligent-scheduler.db");

        // A cloud download staged during the last session is swapped in here, before the first
        // connection opens. Replacing the file under a live connection is how databases break.
        CloudSyncService.ApplyPendingRestore(dbPath);

        var services = new ServiceCollection();
        services.AddSingleton(new AppPaths(dataDirectory, dbPath));
        services.AddPooledDbContextFactory<AppDbContext>(options => options.UseSqlite($"Data Source={dbPath}"));
        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<SqliteStore>();
        services.AddSingleton<ITaskRepository>(provider => provider.GetRequiredService<SqliteStore>());
        services.AddSingleton<ICalendarRepository>(provider => provider.GetRequiredService<SqliteStore>());
        services.AddSingleton<IBlockRepository>(provider => provider.GetRequiredService<SqliteStore>());
        services.AddSingleton<IAvailabilityRepository>(provider => provider.GetRequiredService<SqliteStore>());
        services.AddSingleton<ISettingsRepository>(provider => provider.GetRequiredService<SqliteStore>());
        services.AddSingleton<ISchedulerDiagnosticsRepository>(provider => provider.GetRequiredService<SqliteStore>());
        services.AddSingleton<ITaskExecutionRepository>(provider => provider.GetRequiredService<SqliteStore>());
        services.AddSingleton<ChatStore>();
        services.AddSingleton<IChatRepository>(provider => provider.GetRequiredService<ChatStore>());
        services.AddSingleton<EventOutcomeStore>();
        services.AddSingleton<IEventOutcomeRepository>(provider => provider.GetRequiredService<EventOutcomeStore>());
        services.AddSingleton<EventFollowUpStore>();
        services.AddSingleton<IEventFollowUpRepository>(provider => provider.GetRequiredService<EventFollowUpStore>());
        services.AddSingleton<ProjectStore>();
        services.AddSingleton<IProjectRepository>(provider => provider.GetRequiredService<ProjectStore>());
        services.AddSingleton<IUserMemoryRepository>(provider => provider.GetRequiredService<ChatStore>());
        services.AddSingleton<IAIService, NaturalLanguageTaskParser>();
        services.AddSingleton<ISecretProtector>(_ => new LocalSecretProtector(dataDirectory));
        services.AddSingleton<ILocalModelRuntime>(_ => new LocalModelRuntime(dataDirectory));
        services.AddSingleton<IScheduleActionExecutor, ScheduleActionExecutor>();
        services.AddSingleton<IAssistantToolbox, ScheduleToolbox>();
        services.AddSingleton<McpToolGateway>();
        services.AddSingleton<AssistantService>();
        services.AddSingleton<IAssistantService>(provider => provider.GetRequiredService<AssistantService>());
        services.AddSingleton(_ => new FontCatalogService(dataDirectory));
        services.AddSingleton<ShellThemeService>();
        services.AddSingleton<LocalizationService>();
        services.AddSingleton(_ => new CrashReporter(dataDirectory));
        services.AddSingleton<IBackupService>(provider => new SqliteBackupService(
            provider.GetRequiredService<IDbContextFactory<AppDbContext>>(), provider.GetRequiredService<ISettingsRepository>(), dbPath));
        services.AddSingleton<NotificationCenter>();
        services.AddSingleton<INotificationService>(provider => provider.GetRequiredService<NotificationCenter>());
        services.AddSingleton<FocusModeWatcher>();
        services.AddSingleton<IAutostartService>(_ => new DesktopAutostartService(Environment.ProcessPath ?? "IntelligentScheduler.Desktop"));
        services.AddSingleton<IGlobalHotkeyService, DesktopGlobalHotkeyService>();
        services.AddSingleton<ReminderService>();
        services.AddSingleton<IUpdateService>(provider => new GitHubUpdateService(
            provider.GetRequiredService<ISettingsRepository>(), AppInfo.Version));
        services.AddSingleton<CloudSyncService>(provider => new CloudSyncService(
            provider.GetRequiredService<IDbContextFactory<AppDbContext>>(),
            provider.GetRequiredService<ISettingsRepository>(),
            provider.GetRequiredService<ISecretProtector>(),
            dbPath, AppInfo.Version));
        services.AddSingleton<ICloudSyncService>(provider => provider.GetRequiredService<CloudSyncService>());
        services.AddSingleton<EdgeShellViewModel>();
        return services.BuildServiceProvider();
    }
}

/// <summary>
/// A shortcut, as something a <see cref="KeyBinding"/> can hold. It is always available: a
/// shortcut the user has bound and cannot press is worse than one that does nothing, and every
/// question about whether the action makes sense right now is asked inside the action itself.
/// </summary>
internal sealed class ShortcutCommand(Action run) : System.Windows.Input.ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => run();
}
