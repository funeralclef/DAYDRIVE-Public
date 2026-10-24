using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IntelligentScheduler.Domain;

namespace IntelligentScheduler.Desktop.ViewModels;

public sealed partial class ThemeColorEditorViewModel(
    ThemeColorRole role, Action<ThemeColorRole, string?> changed, Action<ThemeColorEditorViewModel> expand) : ObservableObject
{
    private bool _updating;
    public ThemeColorRole Role { get; } = role;
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string _value = "";
    [ObservableProperty] private string _automaticValue = "";
    [ObservableProperty] private Color _selectedColor;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _hasOverride;
    [ObservableProperty] private bool _isInvalid;
    public IBrush Brush => new SolidColorBrush(SelectedColor);

    public void Update(string? value, Color effective)
    {
        _updating = true;
        try
        {
            HasOverride = !string.IsNullOrWhiteSpace(value);
            // Leave an incomplete edit visible until the user finishes it.
            if (!IsInvalid) Value = value ?? "";
            AutomaticValue = Hex(effective);
            SelectedColor = Color.TryParse(value, out var parsed) ? parsed : effective;
        }
        finally { _updating = false; }
    }

    public void Load(string? value, Color effective)
    {
        IsInvalid = false;
        Update(value, effective);
    }

    partial void OnValueChanged(string value)
    {
        if (_updating) return;
        var hex = string.IsNullOrWhiteSpace(value) ? null : MarelianAppearance.Hex(value);
        IsInvalid = !string.IsNullOrWhiteSpace(value) && hex is null;
        if (!IsInvalid) changed(Role, hex);
    }

    partial void OnSelectedColorChanged(Color value)
    {
        OnPropertyChanged(nameof(Brush));
        if (!_updating) Value = Hex(value);
    }

    [RelayCommand] private void Toggle() { IsExpanded = !IsExpanded; if (IsExpanded) expand(this); }
    [RelayCommand] private void Reset() { IsInvalid = false; Value = ""; changed(Role, null); }
    private static string Hex(Color value) => value.A == 255
        ? $"#{value.R:X2}{value.G:X2}{value.B:X2}" : $"#{value.A:X2}{value.R:X2}{value.G:X2}{value.B:X2}";
}
