using IntelligentScheduler.Application;

namespace IntelligentScheduler.Desktop.Platform;

/// <summary>
/// One answer to "does something else have the screen right now?".
///
/// <paramref name="Presence"/> is the desktop's own verdict where there is one to be had —
/// Windows answers this question for its own notifications, and asking it is what makes an
/// exclusive-fullscreen game register as one without this application keeping a list of games.
/// Every other desktop leaves it <see cref="UserPresenceState.Unknown"/>, which the rules read
/// as "no opinion" and fall through to the measurement.
///
/// <paramref name="OwnsTheScreen"/> is that measurement: the window in front covers its whole
/// display and is not wearing a title bar. <paramref name="Window"/> is whatever identifies that
/// window on this desktop — an HWND, a CoreGraphics window number, an X11 id — and is opaque
/// here on purpose. It exists so the watcher can notice that the window in front has not changed
/// and skip looking its name up again, which is the one part of this that costs anything.
/// </summary>
public readonly record struct ScreenOwnership(UserPresenceState Presence, bool OwnsTheScreen, nint Window)
{
    public static ScreenOwnership Nothing { get; } = new(UserPresenceState.Unknown, false, 0);
}

/// <summary>
/// The one thing that differs between desktops, kept behind one question so the deciding —
/// which is shared, and tested without a desktop — never has to know which one it is on.
/// </summary>
public interface IScreenOwnerProbe : IDisposable
{
    /// <summary>Whether this desktop can be asked at all. False means focus mode is hand-held only.</summary>
    bool IsSupported { get; }

    /// <summary>
    /// Why this desktop cannot answer, in one word, for the diagnostic log. Empty when it can.
    /// </summary>
    string UnsupportedReason { get; }

    ScreenOwnership Ask();

    /// <summary>
    /// The name of the application that window belongs to, for the sentence and the tray line.
    /// Asked only when the window in front has changed, and allowed to answer with nothing:
    /// "something has the whole screen" is at least true.
    /// </summary>
    string DescribeOwner(nint window);
}

/// <summary>Whichever of the three this desktop is, or one that answers nothing.</summary>
public static class ScreenOwnerProbes
{
    public static IScreenOwnerProbe ForThisDesktop()
    {
        if (OperatingSystem.IsWindows()) return new WindowsScreenOwnerProbe();
        if (OperatingSystem.IsMacOS()) return new MacScreenOwnerProbe();
        if (OperatingSystem.IsLinux()) return new X11ScreenOwnerProbe();
        return new UnsupportedScreenOwnerProbe("this desktop");
    }
}

/// <summary>
/// A desktop that will not say. Wayland is the one that matters: a compositor does not tell a
/// client what else is on screen, by design, and no amount of asking changes that. Focus mode is
/// then the switch the user throws by hand — which still lets go of the shortcuts, still holds
/// the reminders, and still takes the petal off the edge of the screen.
/// </summary>
public sealed class UnsupportedScreenOwnerProbe(string reason) : IScreenOwnerProbe
{
    public bool IsSupported => false;
    public string UnsupportedReason { get; } = reason;
    public ScreenOwnership Ask() => ScreenOwnership.Nothing;
    public string DescribeOwner(nint window) => string.Empty;
    public void Dispose() { }
}
