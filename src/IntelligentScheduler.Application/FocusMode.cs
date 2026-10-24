namespace IntelligentScheduler.Application;

/// <summary>
/// Why the shell is currently standing back.
///
/// The three are kept apart rather than collapsed into a flag because the shell says out loud
/// what it is doing and to whom: "you asked me to" and "something else has the whole screen"
/// are different sentences, and only the second one has an application name to put in it.
/// </summary>
public enum FocusModeTrigger
{
    /// <summary>Nothing is in the way. Shortcuts are claimed and reminders arrive.</summary>
    None,
    /// <summary>The user asked for it by hand, from the tray or from settings.</summary>
    Held,
    /// <summary>Something owns the whole screen: a game, a video, a full-screen editor.</summary>
    FullScreenApp,
    /// <summary>Windows is in presentation mode — a projector, or a talk being given.</summary>
    Presentation
}

/// <summary>
/// What Windows says about whether its user can be interrupted right now, in the numbers
/// <c>SHQueryUserNotificationState</c> answers with. Windows decides this for its own
/// notifications; asking it rather than guessing is what makes an exclusive-fullscreen game
/// register as one without this application having to know anything about games.
/// </summary>
public enum UserPresenceState
{
    /// <summary>Nothing was asked, or the answer could not be had. Not a signal either way.</summary>
    Unknown = 0,
    /// <summary>The session is locked, or nobody is logged in at this desk.</summary>
    NotPresent = 1,
    /// <summary>A full-screen application is running — the borderless case lands here.</summary>
    Busy = 2,
    /// <summary>A Direct3D application has the screen exclusively: the ordinary game.</summary>
    RunningFullScreenDirect3D = 3,
    PresentationMode = 4,
    AcceptsNotifications = 5,
    /// <summary>Windows' own quiet hours. Deliberately not a trigger here; see the rules.</summary>
    QuietTime = 6,
    /// <summary>A Store application is in the foreground. Says nothing about the screen.</summary>
    RunningStoreApp = 7
}

/// <summary>
/// When the shell should get out of the way, decided from signals alone so it can be reasoned
/// about — and tested — without a desktop.
///
/// The interesting judgement is what is deliberately *not* here. Windows' own quiet hours
/// (<see cref="UserPresenceState.QuietTime"/>) are not a trigger: they are a setting the user
/// made about Windows' notifications, they switch themselves on at night and during Windows'
/// own focus sessions, and a reminder about a meeting silently swallowed by a setting made
/// somewhere else is exactly the failure this application moved reminders in-house to avoid.
/// A locked screen is not one either — nobody is playing anything, and a reminder waiting on
/// the widget is what the user comes back to.
/// </summary>
public static class FocusModeRules
{
    /// <summary>
    /// <paramref name="held"/> is the user's own hand and outranks everything, including the
    /// automatic switch being off: asking for quiet explicitly is not something an unrelated
    /// preference gets to refuse.
    ///
    /// <paramref name="foregroundOwnsTheScreen"/> is the measured fallback — a window whose
    /// rectangle covers its whole monitor. It is asked second because a borderless game that
    /// leaves the taskbar alone is a case Windows itself sometimes reports as ordinary, and a
    /// focus mode that misses those is a focus mode for the half of games nobody worries about.
    /// </summary>
    public static FocusModeTrigger Decide(
        bool automatic, bool held, UserPresenceState presence, bool foregroundOwnsTheScreen)
    {
        if (held) return FocusModeTrigger.Held;
        if (!automatic) return FocusModeTrigger.None;
        return presence switch
        {
            UserPresenceState.RunningFullScreenDirect3D or UserPresenceState.Busy => FocusModeTrigger.FullScreenApp,
            UserPresenceState.PresentationMode => FocusModeTrigger.Presentation,
            // A locked session is not somebody concentrating, and nothing measured on a screen
            // the user is not at means anything.
            UserPresenceState.NotPresent => FocusModeTrigger.None,
            _ => foregroundOwnsTheScreen ? FocusModeTrigger.FullScreenApp : FocusModeTrigger.None
        };
    }
}

/// <summary>
/// Focus mode as the rest of the shell sees it: whether it is on, why, and — when the why is
/// another application — which one, so the tray line and the settings page can name it rather
/// than saying "something".
/// </summary>
public readonly record struct FocusModeState(FocusModeTrigger Trigger, string AppName)
{
    public static FocusModeState Off { get; } = new(FocusModeTrigger.None, string.Empty);

    public bool IsActive => Trigger != FocusModeTrigger.None;
}
