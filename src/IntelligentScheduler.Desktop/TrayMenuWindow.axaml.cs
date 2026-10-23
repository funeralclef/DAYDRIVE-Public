using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using IntelligentScheduler.Application;
using IntelligentScheduler.Desktop.ViewModels;

namespace IntelligentScheduler.Desktop;

/// <summary>
/// The tray icon's menu, drawn by the shell.
///
/// It is a window of the shell's own rather than a platform menu for one reason above the
/// theme: every surface in this application lives in the always-on-top band, and a menu that is
/// not in that band opens underneath the shell that owns it. This one is put there deliberately
/// and activated, which places it at the front of the band, and it leaves the moment it loses
/// focus — which is what a menu is expected to do anyway.
/// </summary>
public sealed partial class TrayMenuWindow : Window
{
    /// <summary>Roughly what a line costs, used to place the window before its first layout.</summary>
    private const double EstimatedItemHeight = 25;
    private const double EstimatedSeparatorHeight = 7;
    private const double EstimatedWidth = 210;

    /// <summary>The slack around the card, which is window but not menu. Matches the XAML.</summary>
    private const double CardInset = 10;

    private readonly IReadOnlyList<TrayMenuEntry> _entries;
    private PixelPoint _anchor;
    private bool _closing;

    public TrayMenuWindow() : this([]) { }

    public TrayMenuWindow(IReadOnlyList<TrayMenuEntry> entries)
    {
        InitializeComponent();
        _entries = entries;
        MenuItems.ItemsSource = entries.Select(entry => new TrayMenuItemViewModel(entry, CloseOnce)).ToList();
        Deactivated += (_, _) => CloseOnce();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) CloseOnce();
        };
        // A submenu opening changes the menu's height, and the corner it is pinned to is the one
        // against the icon — so it has to grow away from that corner rather than off the screen.
        SizeChanged += (_, _) => Reposition();
    }

    /// <summary>
    /// Opens the menu beside the pointer, folded back onto the screen when the corner it was
    /// asked for has no room. The position is estimated before the window is shown and corrected
    /// from its real size afterwards, so it never appears in the wrong place first.
    /// </summary>
    public void ShowAt(PixelPoint cursor)
    {
        _anchor = cursor;
        Position = Place(EstimatedWidth, EstimatedHeight());
        Show();
        Reposition();
        Activate();
    }

    /// <summary>
    /// Puts the menu back against its corner using whatever it currently measures.
    ///
    /// The size to ask for is <see cref="TopLevel.ClientSize"/>, never <see cref="Visual.Bounds"/>.
    /// A window sized to its content is created at a default size far larger than the menu and
    /// only shrinks to fit once the content has been measured; <c>Bounds</c> still holds that
    /// first size for one more layout pass, including all the way through
    /// <see cref="Window.Opened"/>. Placing from it made the menu believe it was wider and taller
    /// than the screen, so the fold-back below found no room in either direction and pinned the
    /// menu to the clamp — several hundred pixels up and left of the icon that opened it.
    /// </summary>
    private void Reposition()
    {
        var size = ClientSize;
        Position = Place(
            size.Width > 1 ? size.Width : EstimatedWidth,
            size.Height > 1 ? size.Height : EstimatedHeight());
    }

    private double EstimatedHeight()
        => CardInset * 2 + 8 + _entries.Sum(entry => entry.IsSeparator ? EstimatedSeparatorHeight : EstimatedItemHeight);

    private PixelPoint Place(double width, double height)
    {
        var screen = Screens.ScreenFromPoint(_anchor) ?? Screens.Primary;
        var scale = screen?.Scaling ?? RenderScaling;
        var area = screen?.WorkingArea ?? new PixelRect(_anchor.X - 400, _anchor.Y - 400, 800, 800);
        var pixelWidth = (int)Math.Ceiling(width * scale);
        var pixelHeight = (int)Math.Ceiling(height * scale);
        // The card sits inside its window, so lining the window up against the pointer would
        // leave the menu itself standing off it by the inset. The window overhangs by that much.
        var inset = (int)Math.Round(CardInset * scale);
        // The pointer is normally at the bottom right of the screen when this opens, so the menu
        // grows up and to the left out of it, and only folds the other way at an edge.
        var wantsLeft = _anchor.X - pixelWidth + inset >= area.X;
        var wantsUp = _anchor.Y - pixelHeight + inset >= area.Y;
        var x = wantsLeft ? _anchor.X - pixelWidth + inset : _anchor.X - inset;
        var y = wantsUp ? _anchor.Y - pixelHeight + inset : _anchor.Y - inset;
        return new PixelPoint(
            Math.Clamp(x, area.X - inset, Math.Max(area.X - inset, area.Right - pixelWidth + inset)),
            Math.Clamp(y, area.Y - inset, Math.Max(area.Y - inset, area.Bottom - pixelHeight + inset)));
    }

    /// <summary>Losing focus and choosing a line both close the menu; only the first one counts.</summary>
    private void CloseOnce()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }
}
