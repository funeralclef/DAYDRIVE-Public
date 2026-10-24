using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using IntelligentScheduler.Desktop.ViewModels;

namespace IntelligentScheduler.Desktop;

/// <summary>One compositor-managed window; pages and pickers never become desktop windows.</summary>
public sealed partial class WorkspaceWindow : Window
{
    private readonly EdgeShellViewModel _shell;
    private readonly PlannerView _planner;
    private readonly CalendarView _calendar;
    private readonly SettingsView _settings;
    private readonly AnalyticsView _analytics;
    private readonly AssistantView _assistant;
    private readonly DatePickerView _datePicker;
    private readonly TimePickerView _timePicker;
    private bool _updatePending;

    public WorkspaceWindow(EdgeShellViewModel shell)
    {
        InitializeComponent();
        DataContext = _shell = shell;
        shell.IsWindowedShell = true;
        _planner = new PlannerView { DataContext = shell };
        _calendar = new CalendarView { DataContext = shell, MinWidth = 872 };
        _settings = new SettingsView { DataContext = shell, MinWidth = 444 };
        _analytics = new AnalyticsView { DataContext = shell, MinWidth = 642 };
        _assistant = new AssistantView { DataContext = shell, MinWidth = 580 };
        _datePicker = new DatePickerView { DataContext = shell, Width = 360, Height = 402 };
        _timePicker = new TimePickerView { DataContext = shell, Width = 236, Height = 176 };
        PlannerContent.Content = _planner;
        _planner.HideRequested += (_, _) => Close();
        shell.PropertyChanged += ShellChanged;
        shell.EventComposerRequested += ComposerRequested;
        shell.TaskRowRequested += TaskRequested;
        shell.ReviewRowRequested += ReviewRequested;
        SizeChanged += (_, _) => UpdatePages();
        Deactivated += (_, _) => shell.CancelShortcutCapture();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        Closed += (_, _) =>
        {
            shell.PropertyChanged -= ShellChanged;
            shell.EventComposerRequested -= ComposerRequested;
            shell.TaskRowRequested -= TaskRequested;
            shell.ReviewRowRequested -= ReviewRequested;
        };
        UpdatePages();
    }

    private void ShellChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(EdgeShellViewModel.IsCalendarPopupOpen) or nameof(EdgeShellViewModel.IsSettings)
            or nameof(EdgeShellViewModel.IsAnalyticsOpen) or nameof(EdgeShellViewModel.IsChatOpen)
            or nameof(EdgeShellViewModel.IsChatExpanded) or nameof(EdgeShellViewModel.IsAnyDatePickerOpen)
            or nameof(EdgeShellViewModel.IsTimePickerOpen))) return;
        if (_updatePending) return;
        _updatePending = true;
        Dispatcher.UIThread.Post(() => { _updatePending = false; UpdatePages(); }, DispatcherPriority.Send);
    }

    private void UpdatePages()
    {
        Control? section = _shell.IsCalendarPopupOpen ? _calendar
            : _shell.IsSettings ? _settings
            : _shell.IsAnalyticsOpen ? _analytics
            : _shell.IsChatDocked ? _assistant : null;
        if (!ReferenceEquals(SectionContent.Content, section)) SectionContent.Content = section;
        var sideBySide = section is not null && Bounds.Width >= 444 + section.MinWidth + 32;
        PlannerContent.IsVisible = section is null || sideBySide;
        SectionScroll.IsVisible = section is not null;
        WorkspaceBody.ColumnDefinitions = new ColumnDefinitions(section is not null && sideBySide ? "444,*" : "*");
        Grid.SetColumn(SectionScroll, section is not null && sideBySide ? 1 : 0);
        SectionScroll.Margin = new Thickness(section is not null && sideBySide ? 8 : 0, 0, 0, 0);
        Control? picker = _shell.IsTimePickerOpen ? _timePicker : _shell.IsAnyDatePickerOpen ? _datePicker : null;
        if (!ReferenceEquals(PickerContent.Content, picker)) PickerContent.Content = picker;
        PickerBackdrop.IsVisible = picker is not null;
        WorkspaceBody.IsEnabled = picker is null;
        WorkspaceToolbar.IsEnabled = picker is null;
        ReminderBanner.IsEnabled = picker is null;
    }

    public void ShowPlanner()
    {
        _shell.CloseShellPanels();
        UpdatePages();
    }

    private void ComposerRequested(object? sender, EventArgs args) { if (Bounds.Width < 1100) ShowPlanner(); }
    private void TaskRequested(object? sender, Guid id) { if (Bounds.Width < 1100) ShowPlanner(); }
    private void ReviewRequested(object? sender, Guid id) { if (Bounds.Width < 1100) ShowPlanner(); }
    private void Planner_OnClick(object? sender, RoutedEventArgs args) => ShowPlanner();
    private void Assistant_OnClick(object? sender, RoutedEventArgs args)
    {
        if (!_shell.IsChatOpen) _shell.IsChatExpanded = true;
        _shell.ToggleChatCommand.Execute(null);
    }
    private void Reminder_OnClick(object? sender, RoutedEventArgs args)
    {
        if (_shell.Notifications.Current is { } item) _shell.Notifications.Open(item);
    }
    private void DismissReminder_OnClick(object? sender, RoutedEventArgs args) => _shell.Notifications.DismissAll();
    private void ClosePicker()
    {
        if (_shell.IsTimePickerOpen) _shell.CloseTimePickerCommand.Execute(null);
        else _shell.CloseDatePickerCommand.Execute(null);
    }
    private void Backdrop_OnPointerPressed(object? sender, PointerPressedEventArgs args)
    {
        if (ReferenceEquals(args.Source, PickerBackdrop)) ClosePicker();
    }
    private void OnPreviewKeyDown(object? sender, KeyEventArgs args)
    {
        if (_shell.IsCapturingShortcut) return; // SettingsView owns the capture.
        if (args.Key == Key.Escape && PickerBackdrop.IsVisible) { ClosePicker(); args.Handled = true; }
    }
}
