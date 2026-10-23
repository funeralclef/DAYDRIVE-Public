using System;
using Avalonia.Controls;
using Avalonia.Input;

namespace IntelligentScheduler.Desktop;

/// <summary>
/// The assistant once it has been widened out of the planner. It carries the same view the
/// sheet does and differs only in where that view is standing, which is the whole point: the
/// conversation is not rebuilt by being moved, it is re-hosted.
/// </summary>
public sealed partial class AssistantView : UserControl
{
    public AssistantView() => InitializeComponent();

    public event EventHandler? PointerReturned;
    public event EventHandler? PointerDeparted;

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        PointerReturned?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        PointerDeparted?.Invoke(this, EventArgs.Empty);
    }
}
