using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IntelligentScheduler.Application;

namespace IntelligentScheduler.Desktop.ViewModels;

/// <summary>
/// One row on the shortcuts page: what it does, what it is bound to, and whether that binding is
/// actually going to work. The row does no deciding of its own — it hands presses back to the
/// shell, which is the only thing that can see the other five rows.
/// </summary>
public sealed partial class ShortcutBindingViewModel : ObservableObject
{
    private readonly Action<ShortcutBindingViewModel> _capture;
    private readonly Action<ShortcutBindingViewModel> _reset;
    private readonly Func<string, string> _describe;

    public ShortcutBindingViewModel(
        ShellShortcutAction action,
        string label,
        string gesture,
        Action<ShortcutBindingViewModel> capture,
        Action<ShortcutBindingViewModel> reset,
        Func<string, string> describe)
    {
        Action = action;
        Label = label;
        _capture = capture;
        _reset = reset;
        _describe = describe;
        _gesture = gesture;
    }

    public ShellShortcutAction Action { get; }

    /// <summary>What pressing it does, in the user's language.</summary>
    public string Label { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GestureText))]
    private string _gesture;

    /// <summary>
    /// The combination as it is shown: the platform's own names for the modifiers, and a word
    /// rather than a blank for a shortcut that has been switched off.
    /// </summary>
    public string GestureText => _describe(Gesture);

    /// <summary>This row is listening for the next key press.</summary>
    [ObservableProperty] private bool _isCapturing;

    /// <summary>Another row in the same list is asking for the same combination.</summary>
    [ObservableProperty] private bool _hasClash;

    /// <summary>The system would not hand this combination over: something else already holds it.</summary>
    [ObservableProperty] private bool _isRefused;

    public bool HasProblem => HasClash || IsRefused;

    partial void OnHasClashChanged(bool value) => OnPropertyChanged(nameof(HasProblem));
    partial void OnIsRefusedChanged(bool value) => OnPropertyChanged(nameof(HasProblem));

    [RelayCommand] private void BeginCapture() => _capture(this);
    [RelayCommand] private void Reset() => _reset(this);
}
