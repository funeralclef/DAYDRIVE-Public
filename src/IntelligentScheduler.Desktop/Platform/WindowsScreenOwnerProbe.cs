using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using IntelligentScheduler.Application;

namespace IntelligentScheduler.Desktop.Platform;

/// <summary>
/// Windows, which answers the question itself.
///
/// <c>SHQueryUserNotificationState</c> is the same call Windows makes before it shows a
/// notification of its own. That is deliberate: a game registers as a game without this
/// application keeping a list of games, and a title that ships next year is covered on the day
/// it ships.
///
/// It is not the whole answer, though. A borderless-windowed game that leaves the taskbar alone
/// is a case Windows sometimes reports as an ordinary desktop, so a second, measured question is
/// asked after it: does the foreground window cover its entire monitor, and is it missing a
/// title bar? A maximized editor answers no to the second half, which is what keeps a maximized
/// window on a desktop with an auto-hiding taskbar from reading as a game.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsScreenOwnerProbe : IScreenOwnerProbe
{
    /// <summary>A window may miss its monitor's edge by this much and still be covering it.</summary>
    private const int EdgeTolerance = 2;

    public bool IsSupported => true;
    public string UnsupportedReason => string.Empty;

    public ScreenOwnership Ask()
    {
        var presence = QueryPresence();
        var foreground = GetForegroundWindow();
        return new ScreenOwnership(presence, OwnsItsScreen(foreground), foreground);
    }

    private static UserPresenceState QueryPresence()
    {
        try
        {
            return SHQueryUserNotificationState(out var state) == 0 ? state : UserPresenceState.Unknown;
        }
        catch (DllNotFoundException) { return UserPresenceState.Unknown; }
        catch (EntryPointNotFoundException) { return UserPresenceState.Unknown; }
    }

    /// <summary>
    /// Whether this window covers its whole monitor and has no title bar. Both halves matter: the
    /// first on its own calls every maximized window a game whenever the taskbar is set to hide
    /// itself, and the second on its own calls every borderless tool window one.
    /// </summary>
    private static bool OwnsItsScreen(nint window)
    {
        if (window == 0 || window == GetShellWindow() || window == GetDesktopWindow()) return false;
        // The shell's own windows are the one thing on screen this must never react to: the
        // widget and the petal are always on top, and a QA rig can make any of them full size.
        GetWindowThreadProcessId(window, out var processId);
        if (processId == Environment.ProcessId) return false;
        // Clicking the wallpaper puts one of the desktop's own windows in front. It covers the
        // screen by definition, and it is not somebody concentrating.
        var className = new StringBuilder(64);
        if (GetClassName(window, className, className.Capacity) > 0
            && className.ToString() is "WorkerW" or "Progman" or "Shell_TrayWnd") return false;

        if ((GetWindowLong(window, GwlStyle) & WsCaption) == WsCaption) return false;
        if (!GetWindowRect(window, out var bounds)) return false;

        var monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        if (monitor == 0) return false;
        var info = new MonitorInfo { cbSize = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;

        return bounds.Left <= info.rcMonitor.Left + EdgeTolerance
               && bounds.Top <= info.rcMonitor.Top + EdgeTolerance
               && bounds.Right >= info.rcMonitor.Right - EdgeTolerance
               && bounds.Bottom >= info.rcMonitor.Bottom - EdgeTolerance;
    }

    /// <summary>
    /// The executable's name, without the path or the extension. Asked through
    /// <c>QueryFullProcessImageName</c> rather than through a <c>Process</c> object, because the
    /// limited-information right it needs is one Windows hands out for processes — an elevated
    /// game among them — that will not open for anything more.
    /// </summary>
    public string DescribeOwner(nint window)
    {
        if (window == 0) return string.Empty;
        GetWindowThreadProcessId(window, out var processId);
        if (processId == 0) return string.Empty;
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == 0) return string.Empty;
        try
        {
            var path = new StringBuilder(260);
            var length = path.Capacity;
            return QueryFullProcessImageName(handle, 0, path, ref length)
                ? Path.GetFileNameWithoutExtension(path.ToString(0, length))
                : string.Empty;
        }
        catch (ArgumentException) { return string.Empty; }
        finally { CloseHandle(handle); }
    }

    public void Dispose() { }

    private const int GwlStyle = -16;
    private const int WsCaption = 0x00C00000;
    private const uint MonitorDefaultToNearest = 2;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out UserPresenceState state);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern nint GetShellWindow();

    [DllImport("user32.dll")]
    private static extern nint GetDesktopWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    private static extern int GetClassName(nint window, StringBuilder text, int count);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);

    private static long GetWindowLong(nint window, int index) => GetWindowLongPtr(window, index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out Rect bounds);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out int processId);

    [DllImport("kernel32.dll")]
    private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder path, ref int size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
