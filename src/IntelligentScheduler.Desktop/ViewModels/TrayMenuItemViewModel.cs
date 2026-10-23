using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IntelligentScheduler.Application;

namespace IntelligentScheduler.Desktop.ViewModels;

/// <summary>
/// One line of the tray menu, bound to the window that draws it. Choosing a line closes the
/// menu first and acts afterwards, so the shell it opens is not opened behind a menu.
///
/// A line that carries a submenu closes nothing: it opens in place instead, and the window
/// re-places itself around the new height. That is what keeps the menu the height of five
/// lines rather than of every length a quiet spell could be set to.
/// </summary>
public sealed partial class TrayMenuItemViewModel : ObservableObject
{
    private readonly TrayMenuEntry _entry;
    private readonly Action _close;

    public TrayMenuItemViewModel(TrayMenuEntry entry, Action close)
    {
        _entry = entry;
        _close = close;
        Label = entry.Label;
        Detail = entry.Detail ?? string.Empty;
        IsChecked = entry.IsChecked;
        IsSeparator = entry.IsSeparator;
        _isExpanded = entry.IsExpanded;
        foreach (var child in entry.Submenu ?? [])
            Children.Add(new TrayMenuItemViewModel(child, close));
    }

    public string Label { get; }
    public string Detail { get; }
    public bool IsChecked { get; }
    public bool IsSeparator { get; }

    public ObservableCollection<TrayMenuItemViewModel> Children { get; } = [];

    public bool HasSubmenu => Children.Count > 0;
    public bool HasDetail => Detail.Length > 0;

    /// <summary>A line that says what is going on rather than offering to change it.</summary>
    public bool IsCaption => !IsSeparator && !HasSubmenu && !(_entry.IsEnabled && _entry.Invoke is not null);

    public bool IsAction => !IsSeparator && !HasSubmenu && !IsCaption;

    /// <summary>Every line that answers a press: one that acts, and one that opens.</summary>
    public bool IsClickable => IsAction || HasSubmenu;

    /// <summary>Set while this line's own lines are showing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChevronAngle))]
    private bool _isExpanded;

    /// <summary>
    /// The chevron points down when the lines below it are showing and right when they are not,
    /// so it always points at where the lines either are or would be. The glyph is drawn
    /// pointing down, so "right" is a quarter turn back rather than forward.
    /// </summary>
    public double ChevronAngle => IsExpanded ? 0 : -90;

    [RelayCommand]
    private void Invoke()
    {
        if (HasSubmenu)
        {
            // The window re-places itself off its own size change, so opening a submenu keeps
            // the menu's corner against the icon rather than growing off the bottom of it.
            IsExpanded = !IsExpanded;
            return;
        }
        _close();
        _entry.Invoke?.Invoke();
    }
}
