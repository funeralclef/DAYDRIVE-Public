using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using IntelligentScheduler.Application;

namespace IntelligentScheduler.Desktop.Platform;

/// <summary>
/// The notification-area icon on Windows, owned rather than borrowed.
///
/// The toolkit's own tray icon can only report a left click and can only be given a menu built
/// ahead of time, and the menu it draws is a window like any other — which is exactly the
/// problem this shell had with it. Every surface here lives in the always-on-top band, and the
/// last window to join that band joins it at the front; the tray menu opened underneath the
/// shell it belongs to and read as a click that did nothing. Owning the icon means the right
/// button is ours to answer, the menu is a window we place and raise ourselves, and the shell's
/// own handle can be told to stop reaching for the front while it is open.
///
/// The icon is attached to the shell's main window, which is hidden most of the time. A hidden
/// window still receives messages, so nothing has to stay on screen for the tray to work.
/// </summary>
public sealed class Win32TrayService(Window owner) : IAppTrayService
{
    private const int WmApp = 0x8000;
    private const int TrayCallback = WmApp + 0x41;
    private const int TrayIconId = 1;

    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2;
    private const uint NifMessage = 0x01, NifIcon = 0x02, NifTip = 0x04, NifShowTip = 0x80;
    private const int WmLeftButtonUp = 0x0202, WmRightButtonUp = 0x0205, WmMiddleButtonUp = 0x0208;

    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private Win32Properties.CustomWndProcHookCallback? _hook;
    private Func<IReadOnlyList<TrayMenuEntry>>? _buildMenu;
    private Action? _activate;
    private TrayMenuWindow? _menu;
    private IntPtr _icon;
    private IntPtr _handle;
    private bool _added;

    public bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>
    /// Whether the icon is actually in the notification area. A shell that starts hidden and
    /// fails to register has only the edge handle left as a way back, which is worth knowing
    /// about from the log rather than from the user.
    /// </summary>
    public bool IsActive => _added;

    /// <summary>Raised while the menu is on screen, so the shell can stop lifting its handle over it.</summary>
    public event EventHandler<bool>? MenuVisibilityChanged;

    public void Start(Action activate, Func<IReadOnlyList<TrayMenuEntry>> buildMenu)
    {
        if (!IsSupported || _added) return;
        _activate = activate;
        _buildMenu = buildMenu;
        if (owner.TryGetPlatformHandle()?.Handle is not { } handle || handle == IntPtr.Zero) return;
        _handle = handle;
        _hook = WndProc;
        Win32Properties.AddWndProcHookCallback(owner, _hook);
        _icon = TrayIconImage.Load();
        _added = Notify(NimAdd);
    }

    /// <summary>The menu is built when it is opened, so there is nothing here to refresh.</summary>
    public void Refresh() { }

    private bool Notify(uint message)
    {
        var data = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _handle,
            uID = TrayIconId,
            uFlags = NifMessage | NifIcon | NifTip | NifShowTip,
            uCallbackMessage = TrayCallback,
            hIcon = _icon,
            szTip = "DAYDRIVE"
        };
        return Shell_NotifyIcon(message, ref data);
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Explorer restarting takes every tray icon with it and asks for them back.
        if (msg == _taskbarCreated && _added)
        {
            Notify(NimAdd);
            return IntPtr.Zero;
        }
        if (msg != TrayCallback) return IntPtr.Zero;

        switch ((int)(lParam.ToInt64() & 0xFFFF))
        {
            case WmLeftButtonUp:
            case WmMiddleButtonUp:
                handled = true;
                CloseMenu();
                _activate?.Invoke();
                break;
            case WmRightButtonUp:
                handled = true;
                ShowMenu();
                break;
        }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        if (_buildMenu is null) return;
        try
        {
            CloseMenu();
            var entries = _buildMenu();
            if (entries.Count == 0) return;

            var menu = new TrayMenuWindow(entries);
            _menu = menu;
            menu.Closed += (_, _) =>
            {
                if (ReferenceEquals(_menu, menu)) _menu = null;
                MenuVisibilityChanged?.Invoke(this, false);
            };
            MenuVisibilityChanged?.Invoke(this, true);
            menu.ShowAt(CursorPosition());
        }
        catch (Exception)
        {
            // This runs inside the shell's own window procedure: a menu that cannot be drawn
            // must not take the message loop with it. The icon still opens the shell.
            _menu = null;
            MenuVisibilityChanged?.Invoke(this, false);
        }
    }

    private void CloseMenu()
    {
        var menu = _menu;
        _menu = null;
        menu?.Close();
    }

    private static PixelPoint CursorPosition()
        => GetCursorPos(out var point) ? new PixelPoint(point.X, point.Y) : new PixelPoint(0, 0);

    public void Dispose()
    {
        CloseMenu();
        if (_added) Notify(NimDelete);
        _added = false;
        if (_hook is not null) Win32Properties.RemoveWndProcHookCallback(owner, _hook);
        _hook = null;
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
        _icon = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}

/// <summary>
/// The application mark, as something Windows can put in the notification area. The shell keeps
/// its icon as a PNG; an icon handle wants an ICO, which is a six-byte header, one directory
/// entry and the very same PNG after it.
/// </summary>
internal static class TrayIconImage
{
    private const int IconSize = 32;
    private const uint ImageIcon = 1;
    private const uint LoadFromFile = 0x0010;
    private const uint DefaultSize = 0x0040;
    private const uint SharedIcon = 0x8000;
    private const int IdiApplication = 32512;

    public static IntPtr Load()
    {
        try
        {
            var png = AppIcon.PngBytes();
            var path = Path.Combine(Path.GetTempPath(), "daydrive-tray.ico");
            File.WriteAllBytes(path, Wrap(png));
            var icon = LoadImage(IntPtr.Zero, path, ImageIcon, 0, 0, LoadFromFile | DefaultSize);
            if (icon != IntPtr.Zero) return icon;
        }
        catch
        {
            // Falls through to the stock icon: an icon nobody recognises is still better than
            // no way back to a hidden shell.
        }
        return LoadImage(IntPtr.Zero, new IntPtr(IdiApplication), ImageIcon, 0, 0, SharedIcon | DefaultSize);
    }

    private static byte[] Wrap(byte[] png)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((short)0);           // reserved
        writer.Write((short)1);           // an icon, not a cursor
        writer.Write((short)1);           // one image
        writer.Write((byte)IconSize);
        writer.Write((byte)IconSize);
        writer.Write((byte)0);            // colours: 0 means "more than 256"
        writer.Write((byte)0);            // reserved
        writer.Write((short)1);           // planes
        writer.Write((short)32);          // bits per pixel
        writer.Write(png.Length);
        writer.Write(6 + 16);             // the image starts straight after the directory
        writer.Write(png);
        writer.Flush();
        return stream.ToArray();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadImageW", SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int cx, int cy, uint load);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadImageW", SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr instance, IntPtr name, uint type, int cx, int cy, uint load);
}
