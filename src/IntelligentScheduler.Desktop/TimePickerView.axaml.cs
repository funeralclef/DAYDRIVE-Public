using Avalonia.Controls;
using Avalonia.Input;
using IntelligentScheduler.Desktop.ViewModels;

namespace IntelligentScheduler.Desktop;

public sealed partial class TimePickerView : UserControl
{
    public TimePickerView() => InitializeComponent();

    private void Hours_OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
        => AdjustFromWheel(e, hours: true);

    private void Minutes_OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
        => AdjustFromWheel(e, hours: false);

    private void AdjustFromWheel(PointerWheelEventArgs e, bool hours)
    {
        if (DataContext is not EdgeShellViewModel vm || Math.Abs(e.Delta.Y) < 0.01) return;
        var command = hours
            ? e.Delta.Y > 0 ? vm.IncrementTimePickerHourCommand : vm.DecrementTimePickerHourCommand
            : e.Delta.Y > 0 ? vm.IncrementTimePickerMinuteCommand : vm.DecrementTimePickerMinuteCommand;
        if (command.CanExecute(null)) command.Execute(null);
        e.Handled = true;
    }
}
