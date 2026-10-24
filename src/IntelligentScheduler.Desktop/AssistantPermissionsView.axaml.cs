using Avalonia.Controls;

namespace IntelligentScheduler.Desktop;

public sealed partial class AssistantPermissionsView : UserControl
{
    public AssistantPermissionsView()
    {
        InitializeComponent();
        AddHandler(Button.ClickEvent, (_, _) => SelectionInvoked?.Invoke(this, EventArgs.Empty));
    }

    public event EventHandler? SelectionInvoked;
}
