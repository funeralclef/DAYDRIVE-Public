using Avalonia.Controls;

namespace IntelligentScheduler.Desktop;

public sealed partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        Deactivated += (_, _) => (DataContext as ViewModels.EdgeShellViewModel)?.CancelShortcutCapture();
    }
}
