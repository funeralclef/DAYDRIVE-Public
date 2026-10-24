using Avalonia.Controls;
using IntelligentScheduler.Application;

namespace IntelligentScheduler.Desktop.Platform;

/// <summary>
/// The tray icon everywhere except Windows, using the toolkit's own icon and a platform menu.
/// The menu cannot be built at the moment it opens here, so it is rebuilt whenever the state
/// behind it changes instead.
/// </summary>
public sealed class AvaloniaTrayService : IAppTrayService
{
    private TrayIcon? _tray;
    private Func<IReadOnlyList<TrayMenuEntry>>? _buildMenu;

    public bool IsSupported => true;

    public void Start(Action activate, Func<IReadOnlyList<TrayMenuEntry>> buildMenu)
    {
        if (_tray is not null) return;
        _buildMenu = buildMenu;
        _tray = new TrayIcon
        {
            ToolTipText = "DAYDRIVE",
            Menu = BuildNativeMenu(),
            Icon = AppIcon.Create(),
            IsVisible = true
        };
        _tray.Clicked += (_, _) => activate();
    }

    public void Refresh()
    {
        if (_tray is not null) _tray.Menu = BuildNativeMenu();
    }

    private NativeMenu BuildNativeMenu() => Build(_buildMenu?.Invoke() ?? []);

    /// <summary>
    /// The same list the shell draws itself on Windows, as a menu the platform draws. A line
    /// that opens onto others becomes a real submenu here rather than the row that expands in
    /// place: the platform already knows how to draw one, and a line with children but no action
    /// of its own would otherwise arrive as a dead entry with its children nowhere.
    /// </summary>
    private static NativeMenu Build(IReadOnlyList<TrayMenuEntry> entries)
    {
        var menu = new NativeMenu();
        foreach (var entry in entries)
        {
            if (entry.IsSeparator)
            {
                menu.Items.Add(new NativeMenuItemSeparator());
                continue;
            }
            var hasSubmenu = entry.Submenu is { Count: > 0 };
            var item = new NativeMenuItem(entry.Label)
            {
                IsEnabled = hasSubmenu || (entry.IsEnabled && entry.Invoke is not null),
                IsChecked = entry.IsChecked,
                ToggleType = entry.IsChecked ? MenuItemToggleType.CheckBox : MenuItemToggleType.None
            };
            if (hasSubmenu) item.Menu = Build(entry.Submenu!);
            var invoke = entry.Invoke;
            if (invoke is not null) item.Click += (_, _) => invoke();
            menu.Items.Add(item);
        }
        return menu;
    }

    public void Dispose()
    {
        if (_tray is null) return;
        _tray.IsVisible = false;
        _tray.Dispose();
        _tray = null;
    }
}

internal static class AppIcon
{
    private const string PngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAYAAABzenr0AAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAAEE0AABBNAWeMAeAAAAI9SURBVFhH1ZexS+RAFMYtr7zS8v4ES0v/BMvjdkLEStwExUIEG4tDDiwECxesBbniTlDwwMbSUmy0sBBB8LjmvCruzOrIN8lkk/cmbiYbi/vghzB5O++9L8kbMzHxvyruyOluR80UoTGtajHUk91ALkSB/BUHSlcRBeo8CuTaUph8ons00lKoP8aB2oiFTGiykQjVQ+F0z9qCrZGQf9nGPgiZdEX/M917pCLRn2vUdTUbNEelTHK+wdhEQm3TXEywveXOS+BBpjlz4YEZ+56PQshk8Ut/iuY2gkXsBw1ZmedrQwaHNLfpvi3rv60P9L9Hrff3ntk1C3MBw4MGNQGd39+9aKvT44oihOqRAtQ5C2rAyc/nPLmUWn9dHbAYg5APefJs2vEgT2A9klodfa/oPiO/DThY6EVfqPW3Ny8shpJPyEg8zdKLvtS2voRcNgVgOPCLHFhM1+y6j/UF0vEMKxwXS+CVgtBpcb2J9Ra8eWkBGL+OAMvu1mDYHimimfVZAaI/ZwowQ8gRYEGXV5fDLm0RY1if0pHT2YtoxvAtCyjgKgLTzsrHeoOQSRjqD8UCRp4DriIgX+tTyHmAocCDOMUi/vxO/3pbb+7/02ypAAhV0UAXKOLHfpp0Z9O3c8MFzW1U14VxcXZv1dapWAk9BV2KA3XAftgCOHFLT36VENR2EUiOU5fmelPmg8SxmTdC9Wp17lI6puUZ27QOQl2/+cD5KDuyD+r834jvx3zOv4fMJ1v6tuAW5WDd1+pXsrg6rMx7FdIAAAAASUVORK5CYII=";

    public static byte[] PngBytes() => Convert.FromBase64String(PngBase64);

    public static WindowIcon Create()
    {
        using var stream = new MemoryStream(PngBytes());
        return new WindowIcon(stream);
    }
}
