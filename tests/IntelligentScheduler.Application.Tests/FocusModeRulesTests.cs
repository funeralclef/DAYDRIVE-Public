using Xunit;

namespace IntelligentScheduler.Application.Tests;

public sealed class FocusModeRulesTests
{
    [Theory]
    [InlineData(UserPresenceState.RunningFullScreenDirect3D)]
    [InlineData(UserPresenceState.Busy)]
    public void FullScreenApplications_PutTheShellOnItsBestBehaviour(UserPresenceState presence)
        => Assert.Equal(FocusModeTrigger.FullScreenApp,
            FocusModeRules.Decide(automatic: true, held: false, presence, foregroundOwnsTheScreen: false));

    [Fact]
    public void PresentationMode_IsItsOwnReason()
        => Assert.Equal(FocusModeTrigger.Presentation,
            FocusModeRules.Decide(true, false, UserPresenceState.PresentationMode, false));

    [Fact]
    public void AnOrdinaryDesktop_LeavesEverythingAsItWas()
        => Assert.Equal(FocusModeTrigger.None,
            FocusModeRules.Decide(true, false, UserPresenceState.AcceptsNotifications, false));

    /// <summary>
    /// The measured fallback. A borderless game that leaves the taskbar alone is a case Windows
    /// itself sometimes reports as an ordinary desktop, and missing those would leave focus mode
    /// covering only the half of games nobody worries about.
    /// </summary>
    [Fact]
    public void AWindowCoveringItsMonitor_CountsEvenWhenWindowsSaysNothing()
        => Assert.Equal(FocusModeTrigger.FullScreenApp,
            FocusModeRules.Decide(true, false, UserPresenceState.AcceptsNotifications, foregroundOwnsTheScreen: true));

    /// <summary>
    /// Windows' own quiet hours are a setting the user made about Windows' notifications. They
    /// switch themselves on at night and during Windows' own focus sessions, and a meeting
    /// swallowed by a setting made somewhere else is exactly what moving reminders in-house was
    /// meant to stop.
    /// </summary>
    [Fact]
    public void WindowsQuietHours_AreNotThisApplicationsBusiness()
        => Assert.Equal(FocusModeTrigger.None,
            FocusModeRules.Decide(true, false, UserPresenceState.QuietTime, false));

    /// <summary>A locked screen is nobody playing anything, whatever is measured on it.</summary>
    [Fact]
    public void ALockedSession_IsNotSomebodyConcentrating()
        => Assert.Equal(FocusModeTrigger.None,
            FocusModeRules.Decide(true, false, UserPresenceState.NotPresent, foregroundOwnsTheScreen: true));

    [Fact]
    public void WithNoticingSwitchedOff_NothingOnScreenMatters()
        => Assert.Equal(FocusModeTrigger.None,
            FocusModeRules.Decide(automatic: false, held: false,
                UserPresenceState.RunningFullScreenDirect3D, foregroundOwnsTheScreen: true));

    /// <summary>Asking for quiet by hand is not something an unrelated preference gets to refuse.</summary>
    [Fact]
    public void AHandHeldSwitch_OutranksTheAutomaticOneBeingOff()
        => Assert.Equal(FocusModeTrigger.Held,
            FocusModeRules.Decide(automatic: false, held: true, UserPresenceState.AcceptsNotifications, false));

    [Fact]
    public void AHandHeldSwitch_AlsoOutranksWhatIsOnScreen()
        => Assert.Equal(FocusModeTrigger.Held,
            FocusModeRules.Decide(true, true, UserPresenceState.RunningFullScreenDirect3D, true));

    /// <summary>
    /// The numbers are Windows' own, and they reach the rules straight out of the marshaller —
    /// so a member sliding off its value would quietly read a game as an ordinary desktop.
    /// </summary>
    [Theory]
    [InlineData(UserPresenceState.NotPresent, 1)]
    [InlineData(UserPresenceState.Busy, 2)]
    [InlineData(UserPresenceState.RunningFullScreenDirect3D, 3)]
    [InlineData(UserPresenceState.PresentationMode, 4)]
    [InlineData(UserPresenceState.AcceptsNotifications, 5)]
    [InlineData(UserPresenceState.QuietTime, 6)]
    [InlineData(UserPresenceState.RunningStoreApp, 7)]
    public void PresenceStates_KeepTheNumbersWindowsAnswersWith(UserPresenceState state, int expected)
        => Assert.Equal(expected, (int)state);

    [Fact]
    public void AStateIsOnlyActiveWhenSomethingPutItThere()
    {
        Assert.False(FocusModeState.Off.IsActive);
        Assert.True(new FocusModeState(FocusModeTrigger.FullScreenApp, "Cyberpunk2077").IsActive);
    }
}
