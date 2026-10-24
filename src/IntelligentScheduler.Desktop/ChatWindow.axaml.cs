using Avalonia.Controls;

namespace IntelligentScheduler.Desktop;

public sealed partial class ChatWindow : Window
{
    public ChatWindow()
    {
        InitializeComponent();
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
}
