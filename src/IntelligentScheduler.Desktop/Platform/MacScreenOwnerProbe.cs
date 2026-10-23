using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using IntelligentScheduler.Application;

namespace IntelligentScheduler.Desktop.Platform;

/// <summary>
/// macOS, measured rather than asked.
///
/// There is no <c>SHQueryUserNotificationState</c> here — nothing on this desktop will say
/// whether its user can be interrupted — so the whole answer is the measurement: the window
/// list, front to back, and whether the first ordinary window in it covers a whole display.
///
/// That test is cleaner here than it is on Windows. A window that is merely zoomed stops below
/// the menu bar, and one in a full-screen space does not: an exact cover of the display's own
/// rectangle <em>is</em> full screen, and there is no title-bar question to ask on top of it.
///
/// Two deliberate choices about permissions. The window list is read through
/// <c>CGWindowListCopyWindowInfo</c>, which hands out bounds, layer and owning application
/// without any grant at all; only window <em>titles</em> are behind the screen-recording
/// permission, and nothing here reads one. And the accessibility permission — the usual way to
/// ask another application about its windows — is never requested, because a scheduler that
/// opens a system dialogue asking to observe the whole desktop on first run has asked for far
/// more than it is doing with it.
///
/// A display captured outright — the old exclusive-fullscreen path some games still take — is
/// asked for separately, because a captured display's content may not appear in the window list
/// at all.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacScreenOwnerProbe : IScreenOwnerProbe
{
    private const uint OnScreenOnly = 1;
    private const uint ExcludeDesktopElements = 16;
    private const uint NullWindowId = 0;
    private const uint Utf8 = 0x08000100;
    private const int NumberIntType = 9;
    private const int NumberDoubleType = 13;

    /// <summary>A window may miss a display's edge by this much and still be covering it.</summary>
    private const double EdgeTolerance = 2d;

    /// <summary>
    /// Smaller than this and it is furniture — a palette, a tooltip, an off-screen scratch
    /// window — not something anybody is looking at.
    /// </summary>
    private const double MinimumInterestingSide = 32d;

    private readonly nint _keyLayer = CreateKey("kCGWindowLayer");
    private readonly nint _keyBounds = CreateKey("kCGWindowBounds");
    private readonly nint _keyOwnerPid = CreateKey("kCGWindowOwnerPID");
    private readonly nint _keyOwnerName = CreateKey("kCGWindowOwnerName");
    private readonly nint _keyNumber = CreateKey("kCGWindowNumber");
    private readonly nint _keyAlpha = CreateKey("kCGWindowAlpha");

    /// <summary>
    /// The name that came back with the window in front on the last ask. The list already
    /// carries it, so unlike Windows — where a name costs a process handle — there is nothing
    /// to save by looking it up separately, and nothing to gain by asking twice.
    /// </summary>
    private nint _namedWindow;
    private string _namedApp = string.Empty;
    private bool _disposed;

    public bool IsSupported => true;
    public string UnsupportedReason => string.Empty;

    public ScreenOwnership Ask()
    {
        if (_disposed) return ScreenOwnership.Nothing;
        try
        {
            // A display taken outright is the one case the window list cannot describe: its
            // content belongs to whoever captured it and need not be a window at all.
            if (AnyDisplayIsCaptured()) return new ScreenOwnership(UserPresenceState.Unknown, true, 0);
            return ReadFrontWindow();
        }
        catch (DllNotFoundException) { return ScreenOwnership.Nothing; }
        catch (EntryPointNotFoundException) { return ScreenOwnership.Nothing; }
    }

    public string DescribeOwner(nint window)
        => window != 0 && window == _namedWindow ? _namedApp : string.Empty;

    private ScreenOwnership ReadFrontWindow()
    {
        var list = CGWindowListCopyWindowInfo(OnScreenOnly | ExcludeDesktopElements, NullWindowId);
        if (list == 0) return ScreenOwnership.Nothing;
        try
        {
            var count = (long)CFArrayGetCount(list);
            for (var index = 0L; index < count; index++)
            {
                var entry = CFArrayGetValueAtIndex(list, index);
                if (entry == 0) continue;
                // Anything above the ordinary window level is furniture the user is looking
                // past: the menu bar, the Dock, this application's own always-on-top petal.
                if (!TryReadInt(entry, _keyLayer, out var layer) || layer != 0) continue;
                if (!TryReadInt(entry, _keyOwnerPid, out var pid) || pid == Environment.ProcessId) continue;
                if (TryReadDouble(entry, _keyAlpha, out var alpha) && alpha <= 0.01) continue;

                var bounds = CFDictionaryGetValue(entry, _keyBounds);
                if (bounds == 0 || !CGRectMakeWithDictionaryRepresentation(bounds, out var rect)) continue;
                if (rect.Size.Width < MinimumInterestingSide || rect.Size.Height < MinimumInterestingSide) continue;

                // The first window that passes all of that is the one in front. Whether it has
                // the screen is a separate question, and the answer to it is no more often than
                // yes — but either way the search ends here, exactly as the foreground window
                // ends it on Windows.
                TryReadInt(entry, _keyNumber, out var number);
                _namedWindow = number;
                _namedApp = ReadString(entry, _keyOwnerName);
                return new ScreenOwnership(UserPresenceState.Unknown, CoversADisplay(rect), number);
            }
            return ScreenOwnership.Nothing;
        }
        finally { CFRelease(list); }
    }

    private static bool CoversADisplay(CGRect window)
    {
        foreach (var display in ActiveDisplays())
        {
            var bounds = CGDisplayBounds(display);
            if (bounds.Size.Width <= 0 || bounds.Size.Height <= 0) continue;
            if (window.Origin.X <= bounds.Origin.X + EdgeTolerance
                && window.Origin.Y <= bounds.Origin.Y + EdgeTolerance
                && window.Origin.X + window.Size.Width >= bounds.Origin.X + bounds.Size.Width - EdgeTolerance
                && window.Origin.Y + window.Size.Height >= bounds.Origin.Y + bounds.Size.Height - EdgeTolerance)
                return true;
        }
        return false;
    }

    private static bool AnyDisplayIsCaptured()
    {
        foreach (var display in ActiveDisplays())
            if (CGDisplayIsCaptured(display) != 0) return true;
        return false;
    }

    private static uint[] ActiveDisplays()
    {
        var displays = new uint[8];
        return CGGetActiveDisplayList((uint)displays.Length, displays, out var found) != 0 || found == 0
            ? []
            : displays[..(int)Math.Min(found, (uint)displays.Length)];
    }

    // ---- Reading one value out of one dictionary -------------------------------------------

    private static bool TryReadInt(nint dictionary, nint key, out int value)
    {
        value = 0;
        var number = CFDictionaryGetValue(dictionary, key);
        return number != 0 && CFNumberGetValue(number, NumberIntType, out value);
    }

    private static bool TryReadDouble(nint dictionary, nint key, out double value)
    {
        value = 0;
        var number = CFDictionaryGetValue(dictionary, key);
        return number != 0 && CFNumberGetValue(number, NumberDoubleType, out value);
    }

    private static string ReadString(nint dictionary, nint key)
    {
        var text = CFDictionaryGetValue(dictionary, key);
        if (text == 0) return string.Empty;
        // The fast path hands back the string's own storage when it already happens to be in
        // the encoding asked for, which for an application name it usually is.
        var direct = CFStringGetCStringPtr(text, Utf8);
        if (direct != 0) return Marshal.PtrToStringUTF8(direct) ?? string.Empty;
        var length = (long)CFStringGetLength(text);
        if (length <= 0) return string.Empty;
        var buffer = new byte[(length * 4) + 1];
        if (!CFStringGetCString(text, buffer, buffer.Length, Utf8)) return string.Empty;
        var end = Array.IndexOf(buffer, (byte)0);
        return System.Text.Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end);
    }

    private static nint CreateKey(string name) => CFStringCreateWithCString(0, name, Utf8);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var key in new[] { _keyLayer, _keyBounds, _keyOwnerPid, _keyOwnerName, _keyNumber, _keyAlpha })
            if (key != 0) CFRelease(key);
    }

    // ---- CoreGraphics and CoreFoundation ---------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct CGPoint { public double X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGSize { public double Width, Height; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGRect { public CGPoint Origin; public CGSize Size; }

    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [DllImport(CoreGraphics)]
    private static extern nint CGWindowListCopyWindowInfo(uint option, uint relativeToWindow);

    [DllImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CGRectMakeWithDictionaryRepresentation(nint dictionary, out CGRect rect);

    [DllImport(CoreGraphics)]
    private static extern int CGGetActiveDisplayList(uint maxDisplays, uint[] displays, out uint count);

    [DllImport(CoreGraphics)]
    private static extern CGRect CGDisplayBounds(uint display);

    [DllImport(CoreGraphics)]
    private static extern int CGDisplayIsCaptured(uint display);

    [DllImport(CoreFoundation)]
    private static extern nint CFArrayGetCount(nint array);

    [DllImport(CoreFoundation)]
    private static extern nint CFArrayGetValueAtIndex(nint array, long index);

    [DllImport(CoreFoundation)]
    private static extern nint CFDictionaryGetValue(nint dictionary, nint key);

    [DllImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFNumberGetValue(nint number, int type, out int value);

    [DllImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFNumberGetValue(nint number, int type, out double value);

    [DllImport(CoreFoundation)]
    private static extern nint CFStringGetLength(nint text);

    [DllImport(CoreFoundation)]
    private static extern nint CFStringGetCStringPtr(nint text, uint encoding);

    [DllImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFStringGetCString(nint text, byte[] buffer, long size, uint encoding);

    [DllImport(CoreFoundation, CharSet = CharSet.Ansi)]
    private static extern nint CFStringCreateWithCString(nint allocator, string value, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(nint reference);
}
