using Avalonia.Controls;

namespace IntelligentScheduler.Desktop;

public sealed partial class MainWindow : Window
{
    public const double ContentWidth = 444;

    public MainWindow()
    {
        InitializeComponent();
    }

    public event EventHandler? HideRequested
    {
        add => View.HideRequested += value;
        remove => View.HideRequested -= value;
    }

    public event EventHandler? PointerReturned
    {
        add => View.PointerReturned += value;
        remove => View.PointerReturned -= value;
    }

    public event EventHandler? PointerDeparted
    {
        add => View.PointerDeparted += value;
        remove => View.PointerDeparted -= value;
    }

    public event EventHandler? UserActivity
    {
        add => View.UserActivity += value;
        remove => View.UserActivity -= value;
    }
}
