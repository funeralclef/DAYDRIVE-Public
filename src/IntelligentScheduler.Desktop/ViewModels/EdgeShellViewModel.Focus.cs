using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IntelligentScheduler.Application;
using IntelligentScheduler.Domain;

namespace IntelligentScheduler.Desktop.ViewModels;

/// <summary>
/// Focus mode: the shell getting out of the way while the screen belongs to something else.
///
/// The shell is, by design, three things that are wrong in the middle of a game. It claims key
/// combinations from the whole desktop, which means a game bound to one of them never receives
/// it — <c>Ctrl+Alt+S</c> is somebody's crouch. It draws cards on top of everything, which means
/// a reminder about a meeting lands across a cutscene. And it keeps a strip of the right-hand
/// edge of the screen that answers a click, which is where a mouse thrown at something in a
/// hurry ends up. None of the three is a bug in its own half; they are all simply the wrong
/// answer for the twenty minutes somebody is playing.
///
/// So the shell watches for that, and stands back on its own. The three parts of standing back
/// are separate switches because they are separate wants: a streamer who starts and stops a
/// session between rounds needs the shortcuts and would rather not see the cards, somebody
/// watching a film wants the opposite of neither, and somebody with no tray icon on their
/// desktop wants to keep the petal they get back in through.
///
/// This half owns the switches and the sentence that says what is currently happening. What is
/// actually watched lives in <see cref="Platform.FocusModeWatcher"/>, and the acting on it — the
/// shortcuts released, the reminders held — is in <c>App.axaml.cs</c>, which is the only place
/// that holds both the window handle the claims hang off and the notification centre.
/// </summary>
public sealed partial class EdgeShellViewModel
{
    private FocusModeState _focusState = FocusModeState.Off;

    /// <summary>
    /// Whether the shell notices a full-screen application on its own. Off leaves focus mode as
    /// something only the switch below turns on.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FocusModeSummary))]
    [NotifyPropertyChangedFor(nameof(CanFocusModeEverHappen))]
    private bool _focusModeEnabled = true;

    /// <summary>Whether standing back includes letting go of the desktop-wide shortcuts.</summary>
    [ObservableProperty] private bool _focusModeReleasesShortcuts = true;

    /// <summary>Whether reminders wait for the screen instead of arriving on top of it.</summary>
    [ObservableProperty] private bool _focusModeHoldsReminders = true;

    /// <summary>
    /// Whether the petal leaves the edge of the screen while the shell is standing back.
    ///
    /// The other two switches are about what the shell <i>sends</i>; this one is about what it
    /// still <i>occupies</i>. A shortcut that has been let go of is a key the shell no longer
    /// answers, but the petal goes on being a strip of the right-hand edge that eats a click —
    /// and the edge of the screen is where a mouse ends up when it is thrown at something in a
    /// hurry. Standing back while keeping a piece of the screen is not standing back.
    /// </summary>
    [ObservableProperty] private bool _focusModeHidesHandle = true;

    /// <summary>
    /// Focus mode switched on by hand, which outranks everything — including the automatic
    /// switch being off.
    ///
    /// Deliberately not written down. A quiet spell is stored because it has an end the user
    /// named and can be trusted to lift itself; a hold has neither, and one that outlived a
    /// restart would be a silent application with nothing on screen saying why. It lasts as long
    /// as the session it was asked for in.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FocusModeSummary))]
    [NotifyPropertyChangedFor(nameof(CanFocusModeEverHappen))]
    private bool _isFocusModeHeld;

    /// <summary>
    /// Whether this desktop can see what else is on screen. Windows, macOS and an X11 session
    /// all can, each in their own words; a Wayland compositor will not tell a client what else
    /// is running by design, and that is the one desktop where this is false. The switch held by
    /// hand works anywhere.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FocusModeHint))]
    private bool _areFullScreenAppsDetected;

    /// <summary>The preferences changed; whatever is acting on them should read them again.</summary>
    public event EventHandler? FocusModePreferencesChanged;

    /// <summary>Whether the shell is standing back right now, for any reason at all.</summary>
    public bool IsFocusMode => _focusState.IsActive;

    /// <summary>
    /// Whether focus mode has any way of coming on. With both switches off, what standing back
    /// consists of is a question about something that will never happen — so the two rows that
    /// answer it stand down rather than offering a choice with no occasion.
    /// </summary>
    public bool CanFocusModeEverHappen => FocusModeEnabled || IsFocusModeHeld;

    /// <summary>Whether the desktop-wide shortcuts should be let go of at this moment.</summary>
    public bool IsFocusHoldingShortcuts => IsFocusMode && FocusModeReleasesShortcuts;

    /// <summary>Whether a reminder raised at this moment should wait rather than be shown.</summary>
    public bool IsFocusHoldingReminders => IsFocusMode && FocusModeHoldsReminders;

    /// <summary>Whether the petal should be off the edge of the screen at this moment.</summary>
    public bool IsFocusHidingHandle => IsFocusMode && FocusModeHidesHandle;

    /// <summary>What put it on, and what has the screen — for the sentence and the tray line.</summary>
    public FocusModeState FocusState => _focusState;

    public string FocusModeHint => localization.Text(
        AreFullScreenAppsDetected ? "FocusModeIntro" : "FocusModeManualOnlyHint");

    /// <summary>What is happening right now, in one sentence, naming the application when it can.</summary>
    public string FocusModeSummary => _focusState.Trigger switch
    {
        FocusModeTrigger.Held => localization.Text("FocusModeHeldHint"),
        FocusModeTrigger.Presentation => localization.Text("FocusModePresentingHint"),
        FocusModeTrigger.FullScreenApp => _focusState.AppName.Length > 0
            ? localization.Format("FocusModeAppHint", _focusState.AppName)
            : localization.Text("FocusModeAnonHint"),
        _ => localization.Text(FocusModeEnabled ? "FocusModeIdleHint" : "FocusModeOffHint")
    };

    /// <summary>
    /// What the tray line carries at its end. The application's name when there is one, because
    /// "Focus mode · Cyberpunk2077" answers at a glance the question somebody opening the tray
    /// during a game is actually asking — which is not whether it is on, but why.
    /// </summary>
    public string FocusModeTrayDetail
        => _focusState.AppName.Length > 0
            ? _focusState.AppName
            : localization.Text(IsFocusMode ? "FocusModeOnShort" : "DoNotDisturbOffShort");

    [RelayCommand]
    private void ToggleFocusModeHold() => IsFocusModeHeld = !IsFocusModeHeld;

    partial void OnFocusModeEnabledChanged(bool value)
    {
        PublishFocusModePreferences();
        ScheduleSettingsSave();
    }

    partial void OnFocusModeReleasesShortcutsChanged(bool value)
    {
        PublishFocusModePreferences();
        ScheduleSettingsSave();
    }

    partial void OnFocusModeHoldsRemindersChanged(bool value)
    {
        PublishFocusModePreferences();
        ScheduleSettingsSave();
    }

    partial void OnFocusModeHidesHandleChanged(bool value)
    {
        PublishFocusModePreferences();
        ScheduleSettingsSave();
    }

    // A hold is about now and is never written down, so it publishes without scheduling a save.
    partial void OnIsFocusModeHeldChanged(bool value) => PublishFocusModePreferences();

    /// <summary>
    /// The watcher has a new answer. Everything derived from it is announced whether or not the
    /// answer differs in the part this cares about: the trigger can stay
    /// <see cref="FocusModeTrigger.FullScreenApp"/> while the application that has the screen
    /// changes underneath it, and the sentence names that application.
    /// </summary>
    public void ReportFocusMode(FocusModeState state)
    {
        if (state == _focusState) return;
        _focusState = state;
        OnPropertyChanged(nameof(FocusState));
        OnPropertyChanged(nameof(IsFocusMode));
        OnPropertyChanged(nameof(IsFocusHoldingShortcuts));
        OnPropertyChanged(nameof(IsFocusHoldingReminders));
        OnPropertyChanged(nameof(IsFocusHidingHandle));
        OnPropertyChanged(nameof(FocusModeSummary));
        OnPropertyChanged(nameof(FocusModeTrayDetail));
        OnPropertyChanged(nameof(ShortcutsHint));
    }

    /// <summary>
    /// Hands the switches to whoever is watching the screen and acting on the answer. Pushed
    /// rather than polled, for the same reason the reminder preferences are.
    /// </summary>
    public void PublishFocusModePreferences()
    {
        FocusModePreferencesChanged?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(nameof(IsFocusHoldingShortcuts));
        OnPropertyChanged(nameof(IsFocusHoldingReminders));
        OnPropertyChanged(nameof(IsFocusHidingHandle));
        OnPropertyChanged(nameof(FocusModeSummary));
        OnPropertyChanged(nameof(FocusModeTrayDetail));
        OnPropertyChanged(nameof(ShortcutsHint));
    }

    /// <summary>
    /// Everything focus mode says, said again in a new language. All of it is worded and none of
    /// it is a list, so there is nothing here to rebuild — only to re-announce.
    /// </summary>
    private void RefreshFocusModeText()
    {
        OnPropertyChanged(nameof(FocusModeHint));
        OnPropertyChanged(nameof(FocusModeSummary));
        OnPropertyChanged(nameof(FocusModeTrayDetail));
    }

    private void LoadFocusSettings(AppSettings settings)
    {
        FocusModeEnabled = settings.FocusModeEnabled;
        FocusModeReleasesShortcuts = settings.FocusModeReleasesShortcuts;
        FocusModeHoldsReminders = settings.FocusModeHoldsReminders;
        FocusModeHidesHandle = settings.FocusModeHidesHandle;
    }

    private void CopyFocusSettings()
    {
        if (_settings is null) return;
        _settings.FocusModeEnabled = FocusModeEnabled;
        _settings.FocusModeReleasesShortcuts = FocusModeReleasesShortcuts;
        _settings.FocusModeHoldsReminders = FocusModeHoldsReminders;
        _settings.FocusModeHidesHandle = FocusModeHidesHandle;
    }
}
