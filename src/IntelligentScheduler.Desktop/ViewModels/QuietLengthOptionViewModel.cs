using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace IntelligentScheduler.Desktop.ViewModels;

/// <summary>
/// One length a quiet spell can be set to. The lengths are a row of choices rather than a
/// number to type: nobody wants to be undisturbed for forty-three minutes, and the four on
/// offer are the ones a meeting, an afternoon or a night actually take.
/// </summary>
public sealed partial class QuietLengthOptionViewModel(int minutes, string label, Action<int> select) : ObservableObject
{
    public int Minutes { get; } = minutes;
    public string Label { get; } = label;

    [ObservableProperty] private bool _isSelected;

    [RelayCommand]
    private void Select() => select(Minutes);
}
