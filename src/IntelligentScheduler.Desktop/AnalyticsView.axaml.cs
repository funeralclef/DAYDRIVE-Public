using System;
using Avalonia.Controls;
using Avalonia.Input;

namespace IntelligentScheduler.Desktop;

public sealed partial class AnalyticsView : UserControl
{
    public AnalyticsView() => InitializeComponent();

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
