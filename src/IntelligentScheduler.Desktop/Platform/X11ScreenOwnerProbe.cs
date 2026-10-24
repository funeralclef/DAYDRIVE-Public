using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using IntelligentScheduler.Application;

namespace IntelligentScheduler.Desktop.Platform;

/// <summary>
/// Linux on X11, asked in the one language every desktop there already speaks.
///
/// The window manager publishes what has focus on the root window, and a window that has gone
/// full screen says so itself by carrying <c>_NET_WM_STATE_FULLSCREEN</c>. Both are part of the
/// specification every X11 desktop implements — GNOME, KDE, Xfce, i3, whatever somebody has
/// built their session out of — so this needs no list of desktops and no list of games. A video
/// player going full screen, a game, a presentation: all three set the same property, because
/// that property is how they ask the window manager to give them the screen in the first place.
///
/// The measured fallback is kept for the rare client that takes the whole screen without asking
/// — a window as large as the display and nothing in front of it.
///
/// Wayland is a different answer and not a worse implementation of this one: a compositor does
/// not tell a client what else is on screen, by design. A session running under it says so and
/// leaves focus mode as the switch the user throws by hand.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class X11ScreenOwnerProbe : IScreenOwnerProbe
{
    /// <summary>A window may miss the display's size by this much and still be covering it.</summary>
    private const int EdgeTolerance = 2;

    /// <summary>
    /// Kept alive as a field, not as a local. It is handed to Xlib as a raw function pointer,
    /// and a delegate the collector cannot see a reference to is one it is free to move.
    /// </summary>
    private readonly XErrorHandler _swallowErrors = (_, _) => 0;

    private readonly nint _display;
    private readonly nint _root;
    private readonly nint _activeWindow;
    private readonly nint _state;
    private readonly nint _fullScreen;
    private readonly nint _windowType;
    private readonly nint _desktopType;
    private readonly nint _processId;
    private readonly nint _windowClass;
    private readonly int _screenWidth;
    private readonly int _screenHeight;

    private nint _namedWindow;
    private string _namedApp = string.Empty;
    private bool _disposed;

    public X11ScreenOwnerProbe()
    {
        UnsupportedReason = DescribeWhyNot();
        if (UnsupportedReason.Length > 0) return;
        try
        {
            _display = XOpenDisplay(0);
        }
        catch (DllNotFoundException) { _display = 0; }
        catch (EntryPointNotFoundException) { _display = 0; }
        if (_display == 0)
        {
            UnsupportedReason = "no X display";
            return;
        }

        var screen = XDefaultScreen(_display);
        _root = XRootWindow(_display, screen);
        _screenWidth = XDisplayWidth(_display, screen);
        _screenHeight = XDisplayHeight(_display, screen);
        _activeWindow = XInternAtom(_display, "_NET_ACTIVE_WINDOW", false);
        _state = XInternAtom(_display, "_NET_WM_STATE", false);
        _fullScreen = XInternAtom(_display, "_NET_WM_STATE_FULLSCREEN", false);
        _windowType = XInternAtom(_display, "_NET_WM_WINDOW_TYPE", false);
        _desktopType = XInternAtom(_display, "_NET_WM_WINDOW_TYPE_DESKTOP", false);
        _processId = XInternAtom(_display, "_NET_WM_PID", false);
        _windowClass = XInternAtom(_display, "WM_CLASS", false);
    }

    public bool IsSupported => _display != 0 && !_disposed;

    public string UnsupportedReason { get; }

    /// <summary>
    /// Why this session cannot be asked, before anything is opened. A Wayland session is the
    /// one that matters; the rest is a session with no display to talk to at all.
    /// </summary>
    private static string DescribeWhyNot()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland",
                StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
            return "wayland";
        return string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) ? "no DISPLAY" : string.Empty;
    }

    public ScreenOwnership Ask()
    {
        if (!IsSupported) return ScreenOwnership.Nothing;
        // Every call below is a round trip, so an error for one of them is reported inside the
        // call that caused it. Asking about a window that closed while the question was in
        // flight is an ordinary race on this desktop, and Xlib answers a race by calling the
        // process-wide handler — whose default ends the process. Ours is put in for the length
        // of the questions and taken out again, so nothing else in the application loses the
        // handler it installed.
        var previous = XSetErrorHandler(_swallowErrors);
        try
        {
            return Measure();
        }
        catch (DllNotFoundException) { return ScreenOwnership.Nothing; }
        catch (EntryPointNotFoundException) { return ScreenOwnership.Nothing; }
        finally
        {
            if (previous != 0) XSetErrorHandlerRaw(previous);
        }
    }

    private ScreenOwnership Measure()
    {
        var active = ReadCardinals(_root, _activeWindow);
        if (active.Length == 0 || active[0] == 0) return ScreenOwnership.Nothing;
        var window = active[0];

        // The shell's own petal and widget are always on top and a QA rig can make either of
        // them full size; neither is somebody else having the screen.
        var pid = ReadCardinals(window, _processId);
        if (pid.Length > 0 && (int)pid[0] == Environment.ProcessId) return ScreenOwnership.Nothing;

        // Clicking the wallpaper focuses the desktop itself, which covers the screen by
        // definition and is not somebody concentrating.
        foreach (var type in ReadCardinals(window, _windowType))
            if (type == _desktopType) return new ScreenOwnership(UserPresenceState.Unknown, false, window);

        var owns = false;
        foreach (var flag in ReadCardinals(window, _state))
            if (flag == _fullScreen) { owns = true; break; }
        if (!owns) owns = CoversTheScreen(window);

        if (window != _namedWindow)
        {
            _namedWindow = window;
            _namedApp = ReadClass(window);
        }
        return new ScreenOwnership(UserPresenceState.Unknown, owns, window);
    }

    public string DescribeOwner(nint window)
        => window != 0 && window == _namedWindow ? _namedApp : string.Empty;

    /// <summary>
    /// The fallback, for a client that takes the screen without telling the window manager. Only
    /// the size is compared and not the corner: a window manager that reparents its clients hangs
    /// them off a frame, so the coordinates a window reports are its own parent's, and the one
    /// number that means the same thing in every session is how big it is.
    /// </summary>
    private bool CoversTheScreen(nint window)
        => XGetGeometry(_display, window, out _, out _, out _, out var width, out var height, out _, out _) != 0
           && width + EdgeTolerance >= _screenWidth
           && height + EdgeTolerance >= _screenHeight;

    /// <summary>
    /// A window's class, which is what a desktop puts under its icon — "steam_app_1091500",
    /// "mpv", "firefox". Two NUL-separated strings, of which the second is the class.
    /// </summary>
    private string ReadClass(nint window)
    {
        if (XGetWindowProperty(_display, window, _windowClass, 0, 64, false, AnyPropertyType,
                out _, out var format, out var count, out _, out var data) != Success || data == 0)
            return string.Empty;
        try
        {
            if (format != 8 || count <= 0) return string.Empty;
            var bytes = new byte[(int)count];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            var parts = Encoding.UTF8.GetString(bytes)
                .Split('\0', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length switch { 0 => string.Empty, 1 => parts[0], _ => parts[1] };
        }
        finally { XFree(data); }
    }

    /// <summary>
    /// A window or atom list. The quirk worth naming: a property whose format is 32 is handed
    /// back as an array of C <c>long</c>, which on every 64-bit Linux is eight bytes rather than
    /// four — reading it as <c>int</c> is the classic way to get half a window id.
    /// </summary>
    private nint[] ReadCardinals(nint window, nint property)
    {
        if (property == 0) return [];
        if (XGetWindowProperty(_display, window, property, 0, 32, false, AnyPropertyType,
                out _, out var format, out var count, out _, out var data) != Success || data == 0)
            return [];
        try
        {
            if (format != 32 || count <= 0) return [];
            var items = new nint[(int)count];
            for (var index = 0; index < items.Length; index++)
                items[index] = Marshal.ReadIntPtr(data, index * IntPtr.Size);
            return items;
        }
        finally { XFree(data); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_display == 0) return;
        try { XCloseDisplay(_display); }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    // ---- Xlib -------------------------------------------------------------------------------

    private const int Success = 0;
    private const nint AnyPropertyType = 0;
    private const string X11 = "libX11.so.6";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XErrorHandler(nint display, nint error);

    [DllImport(X11)]
    private static extern nint XOpenDisplay(nint name);

    [DllImport(X11)]
    private static extern int XCloseDisplay(nint display);

    [DllImport(X11)]
    private static extern int XDefaultScreen(nint display);

    [DllImport(X11)]
    private static extern nint XRootWindow(nint display, int screen);

    [DllImport(X11)]
    private static extern int XDisplayWidth(nint display, int screen);

    [DllImport(X11)]
    private static extern int XDisplayHeight(nint display, int screen);

    [DllImport(X11, CharSet = CharSet.Ansi)]
    private static extern nint XInternAtom(nint display, string name, [MarshalAs(UnmanagedType.I1)] bool onlyIfExists);

    [DllImport(X11)]
    private static extern int XGetWindowProperty(
        nint display, nint window, nint property, nint offset, nint length,
        [MarshalAs(UnmanagedType.I1)] bool delete, nint requestedType,
        out nint actualType, out int actualFormat, out nint itemCount, out nint bytesAfter, out nint data);

    [DllImport(X11)]
    private static extern int XGetGeometry(
        nint display, nint drawable, out nint root, out int x, out int y,
        out uint width, out uint height, out uint borderWidth, out uint depth);

    [DllImport(X11)]
    private static extern int XFree(nint data);

    [DllImport(X11, EntryPoint = "XSetErrorHandler")]
    private static extern nint XSetErrorHandler(XErrorHandler handler);

    [DllImport(X11, EntryPoint = "XSetErrorHandler")]
    private static extern nint XSetErrorHandlerRaw(nint handler);
}
