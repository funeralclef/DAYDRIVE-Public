using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Threading;
using IntelligentScheduler.Application;
using IntelligentScheduler.Desktop.Controls;
using IntelligentScheduler.Desktop.Localization;
using IntelligentScheduler.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace IntelligentScheduler.Desktop.Platform;

internal sealed class WindowedShell : IDisposable
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly EdgeShellViewModel _model;
    private readonly WorkspaceWindow _window;
    private readonly NotificationCenter _notifications;
    private readonly FocusModeWatcher _focus;
    private readonly DesktopGlobalHotkeyService _hotkeys;
    private readonly ReminderService _reminders;
    private readonly CrashReporter _crashes;
    private readonly LocalizationService _localization;
    private readonly IAppTrayService _tray;
    private bool _stopping;
    private bool _exitPending;

    public WindowedShell(IClassicDesktopStyleApplicationLifetime desktop, IServiceProvider services, EdgeShellViewModel model)
    {
        _desktop = desktop;
        _model = model;
        _window = new WorkspaceWindow(model) { Icon = AppIcon.Create() };
        _notifications = services.GetRequiredService<NotificationCenter>();
        _focus = services.GetRequiredService<FocusModeWatcher>();
        _hotkeys = (DesktopGlobalHotkeyService)services.GetRequiredService<IGlobalHotkeyService>();
        _reminders = services.GetRequiredService<ReminderService>();
        _crashes = services.GetRequiredService<CrashReporter>();
        _localization = services.GetRequiredService<LocalizationService>();
        _tray = OperatingSystem.IsWindows() ? new Win32TrayService(_window) : new AvaloniaTrayService();
    }

    public void Start()
    {
        _desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _desktop.MainWindow = _window;
        _crashes.Diagnostic("Windowed shell: compositor manages one resizable window; no edge surfaces or off-screen warm-up.");
        _window.Opened += (_, _) =>
        {
            _hotkeys.Attach(_window);
            ApplyShortcuts();
            _tray.Start(Reveal, BuildTrayMenu);
        };
        _window.Closing += (_, args) =>
        {
            if (_stopping) return;
            if (args.CloseReason is WindowCloseReason.OSShutdown or WindowCloseReason.ApplicationShutdown)
            { StopForSessionEnd(); return; }
            args.Cancel = true;
            _ = ExitAsync();
        };
        _desktop.ShutdownRequested += (_, _) => StopForSessionEnd();
        _model.ShellSettingsChanged += (_, _) => ApplyPreferences();
        _model.NotificationPreferencesChanged += (_, _) => ApplyPreferences();
        _model.FocusModePreferencesChanged += (_, _) => ApplyPreferences();
        _model.ShortcutsChanged += (_, _) => ApplyShortcuts();
        _model.LocalizationChanged += (_, _) => _tray.Refresh();
        _model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(EdgeShellViewModel.IsCapturingShortcut))
            {
                if (_model.IsCapturingShortcut) { _window.KeyBindings.Clear(); _hotkeys.Unregister(); }
                else ApplyShortcuts();
            }
        };
        _model.RestartRequested += async (_, _) =>
        {
            try
            {
                await _model.FlushSettingsAsync();
                if (!AppInfo.TryRestart()) { _model.ReportRestartFailed(); return; }
                _stopping = true;
                _desktop.Shutdown();
            }
            catch (Exception exception) { _model.ReportUnexpectedError(exception); }
        };
        _model.ShellRevealRequested += (_, _) => Reveal();
        _model.Notifications.Opened += (_, item) =>
        {
            try
            {
                _window.ShowPlanner();
                Reveal();
                _model.FlashEvent(item.EventId);
            }
            catch (Exception exception) { _model.ReportUnexpectedError(exception); }
        };
        _notifications.Posted += (_, item) => Dispatcher.UIThread.Post(() => Deliver(item));
        _notifications.HeldReleased += (_, items) => Dispatcher.UIThread.Post(() =>
        {
            foreach (var item in items) Deliver(item);
        });
        _model.AreFullScreenAppsDetected = _focus.IsSupported;
        _focus.Changed += (_, state) =>
        {
            _model.ReportFocusMode(state);
            _notifications.ApplyFocusMode(_model.IsFocusHoldingReminders);
            ApplyShortcuts();
            _tray.Refresh();
        };
        ApplyPreferences();
        _reminders.Start();
        LinuxDesktopControl.Current?.Attach(HandleLaunch);
    }

    private void HandleLaunch(string[] arguments)
    {
        if (arguments.Contains("--exit", StringComparer.OrdinalIgnoreCase)) { _ = ExitAsync(); return; }
        var action = LinuxDesktopControl.RequestedAction(arguments);
        if (action is { } requested)
        {
            if (!_model.IsFocusHoldingShortcuts) RunShortcut(requested);
            return;
        }
        Reveal();
        var section = arguments.FirstOrDefault(a => a.StartsWith("--section=", StringComparison.OrdinalIgnoreCase))?
            ["--section=".Length..].ToLowerInvariant();
        switch (section)
        {
            case "tasks": _window.ShowPlanner(); break;
            case "calendar": _model.ShowCalendarCommand.Execute(null); break;
            case "settings": _model.ShowSettingsCommand.Execute(null); break;
            case "analytics": if (!_model.IsAnalyticsOpen) _model.ToggleAnalyticsCommand.Execute(null); break;
            case "assistant": _model.ShowAssistantCommand.Execute(null); break;
        }
    }

    private void Reveal()
    {
        if (_stopping) return;
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void ApplyPreferences()
    {
        _notifications.ApplyPreferences(_model.NotificationsEnabled, _model.NotificationSoundEnabled,
            _model.DoNotDisturbUntilUtc, _model.DoNotDisturbUntilResumed);
        _focus.Configure(_model.FocusModeEnabled, _model.IsFocusModeHeld);
        _notifications.ApplyFocusMode(_model.IsFocusHoldingReminders);
        ApplyShortcuts();
        _tray.Refresh();
    }

    private void Deliver(AppNotification notification)
    {
        if (_stopping) return;
        _model.Notifications.Post(notification);
        _model.MarkReminder(notification);
        if (_notifications.IsSoundEnabled) NotificationSound.Play(1);
        // Keep focus on the user's current application. The banner stays inside our window.
    }

    private void ApplyShortcuts()
    {
        _window.KeyBindings.Clear();
        if (_model.IsCapturingShortcut) { _hotkeys.Unregister(); return; }
        var claims = new List<GlobalHotkey>();
        foreach (var (action, gesture) in _model.ShortcutMap)
        {
            if (ShortcutGesture.ToKeyGesture(gesture) is not { } key) continue;
            void Run() => RunShortcut(action);
            _window.KeyBindings.Add(new KeyBinding { Gesture = key, Command = new ShortcutCommand(Run) });
            claims.Add(new GlobalHotkey(action, gesture, Run));
        }
        var refused = _model.IsFocusHoldingShortcuts ? [] : _hotkeys.Apply(claims);
        if (_model.IsFocusHoldingShortcuts) _hotkeys.Unregister();
        _model.ReportShortcutRegistration(_hotkeys.IsSupported, refused);
    }

    private void RunShortcut(ShellShortcutAction action)
    {
        if (_model.IsCapturingShortcut) return;
        switch (action)
        {
            case ShellShortcutAction.ToggleShell: Reveal(); break;
            case ShellShortcutAction.ToggleWidget: _window.ShowPlanner(); Reveal(); break;
            case ShellShortcutAction.ToggleAssistant:
                if (!_model.IsChatOpen) _model.IsChatExpanded = true;
                _model.ToggleChatCommand.Execute(null);
                break;
            case ShellShortcutAction.StartOrPauseSession: _model.ToggleSessionCommand.Execute(null); break;
            case ShellShortcutAction.StopSession: _window.ShowPlanner(); _model.StopEventCommand.Execute(null); break;
            case ShellShortcutAction.ToggleDoNotDisturb: _model.ToggleDoNotDisturbCommand.Execute(null); break;
        }
    }

    private IReadOnlyList<TrayMenuEntry> BuildTrayMenu()
    {
        var quiet = EdgeShellViewModel.DoNotDisturbLengths.Select(minutes => new TrayMenuEntry(
            _model.FormatQuietLength(minutes), () => _model.StartDoNotDisturbCommand.Execute(minutes.ToString()),
            IsChecked: _model.IsDoNotDisturb && _model.DoNotDisturbMinutes == minutes)).ToList();
        if (_model.IsDoNotDisturb) quiet.Insert(0, new(_localization.Text("DoNotDisturbEnd"),
            () => _model.EndDoNotDisturbCommand.Execute(null)));
        return
        [
            new(_localization.Text("OpenApp"), Reveal),
            new(_localization.Text("DoNotDisturb"), IsChecked: _model.IsDoNotDisturb) { Submenu = quiet },
            new(_localization.Text("FocusModeHold"), () => _model.ToggleFocusModeHoldCommand.Execute(null), IsChecked: _model.IsFocusModeHeld),
            new(_localization.Text("NotificationSound"), () => _model.ToggleNotificationSoundCommand.Execute(null), IsChecked: _model.NotificationSoundEnabled),
            new(_localization.Text("Exit"), () => _ = ExitAsync())
        ];
    }

    private async Task ExitAsync()
    {
        if (_stopping || _exitPending) return;
        _exitPending = true;
        try
        {
            await _model.FlushSettingsAsync();
            await _model.SyncBeforeExitAsync();
            _stopping = true;
            _desktop.Shutdown();
        }
        catch (Exception exception) { _exitPending = false; _model.ReportUnexpectedError(exception); }
    }

    private void StopForSessionEnd()
    {
        if (_stopping) return;
        _stopping = true;
        _reminders.Dispose();
        _focus.Dispose();
        _hotkeys.Unregister();
        try
        {
            var flush = _model.FlushSettingsAsync();
            var until = DateTime.UtcNow.AddSeconds(2);
            while (!flush.IsCompleted && DateTime.UtcNow < until)
            { Dispatcher.UIThread.RunJobs(); Thread.Sleep(8); }
        }
        catch (Exception exception) { _crashes.Record(exception, "Saving windowed shell on shutdown"); }
    }

    public void Dispose()
    {
        _stopping = true;
        _reminders.Dispose();
        _focus.Dispose();
        _hotkeys.Unregister();
        _tray.Dispose();
    }
}
