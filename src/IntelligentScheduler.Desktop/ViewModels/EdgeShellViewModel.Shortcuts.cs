using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IntelligentScheduler.Application;
using IntelligentScheduler.Domain;

namespace IntelligentScheduler.Desktop.ViewModels;

/// <summary>
/// The shortcuts page, and the one list of bindings the shell registers from.
///
/// This half owns the map: what each action is bound to, whether two rows are asking for the
/// same keys, and what the system said when the shell tried to claim them. Claiming is not done
/// here — that needs a window handle and belongs in <c>App.axaml.cs</c>, which listens to
/// <see cref="ShortcutsChanged"/> and reports back through
/// <see cref="ReportShortcutRegistration"/>.
/// </summary>
public sealed partial class EdgeShellViewModel
{
    private readonly Dictionary<ShellShortcutAction, string> _shortcuts = [];
    /// <summary>
    /// Which combinations the system would not hand over, kept on the view model rather than only
    /// on the rows. The rows are thrown away and built again whenever the language changes, and a
    /// refusal that lived only on a row was silently forgotten there — the page then said a
    /// shortcut was fine while the shell was not holding it.
    /// </summary>
    private readonly HashSet<ShellShortcutAction> _refusedShortcuts = [];
    private ShortcutBindingViewModel? _capturingShortcut;

    /// <summary>One row per action, in the order <see cref="ShellShortcuts.All"/> lists them.</summary>
    public ObservableCollection<ShortcutBindingViewModel> Shortcuts { get; } = [];

    /// <summary>The map changed. Whoever holds the keys should claim the new set.</summary>
    public event EventHandler? ShortcutsChanged;

    /// <summary>
    /// Whether this platform lets the application claim keys from the whole desktop. Windows
    /// does; macOS wants an accessibility grant this application has never asked for, and a
    /// Wayland compositor — niri and Hyprland among them — will not hand a key to a client at
    /// all, which is why both of those bind shortcuts in their own configuration. The bindings
    /// still work inside the application's own windows everywhere.
    /// </summary>
    [ObservableProperty] private bool _areGlobalShortcutsSupported;

    /// <summary>True while a row is waiting for the combination to be pressed.</summary>
    [ObservableProperty] private bool _isCapturingShortcut;

    /// <summary>
    /// What the page says above the rows. Focus mode comes first when it is holding them: a
    /// shortcut that is not working right now is the question the page is about to be asked, and
    /// "something else has the whole screen" is a better answer than a row that looks fine.
    /// </summary>
    public string ShortcutsHint => localization.Text(
        IsFocusHoldingShortcuts ? "ShortcutsFocusHeld"
            : AreGlobalShortcutsSupported ? "ShortcutsIntro"
            : "ShortcutsWindowOnlyIntro");

    /// <summary>Every binding the shell should currently be holding, empty ones left out.</summary>
    public IReadOnlyDictionary<ShellShortcutAction, string> ShortcutMap => _shortcuts;

    private void LoadShortcuts(AppSettings settings)
    {
        _shortcuts.Clear();
        foreach (var (action, gesture) in ShellShortcuts.Read(settings.ShortcutsJson, settings.QuickAddHotkey))
            _shortcuts[action] = gesture;
        BuildShortcutRows();
    }

    /// <summary>
    /// Rebuilds the rows. Called again when the language changes, because the only thing a row
    /// carries that is not the gesture is the sentence saying what it does.
    /// </summary>
    private void BuildShortcutRows()
    {
        // The row that was listening is about to be thrown away. Left listening it takes the
        // global claims with it: they are dropped for as long as a row is waiting for a
        // combination, and nothing would ever say that this one had stopped waiting.
        CancelShortcutCapture();
        Shortcuts.Clear();
        foreach (var action in ShellShortcuts.All)
            Shortcuts.Add(new ShortcutBindingViewModel(
                action,
                localization.Text($"Shortcut{action}"),
                _shortcuts.GetValueOrDefault(action, string.Empty),
                BeginShortcutCapture,
                ResetShortcut,
                DescribeGesture)
            {
                IsRefused = AreGlobalShortcutsSupported
                            && _shortcuts.GetValueOrDefault(action, string.Empty).Length > 0
                            && _refusedShortcuts.Contains(action)
            });
        MarkShortcutClashes();
    }

    /// <summary>
    /// The combination as this desktop writes it. Windows and Linux say Ctrl and Alt; macOS
    /// draws the same two keys as symbols and puts them in its own order, and a shortcut printed
    /// in the wrong dialect is one the user has to translate before they can trust it.
    /// </summary>
    private string DescribeGesture(string gesture)
    {
        if (gesture.Length == 0) return localization.Text("ShortcutOff");
        if (!OperatingSystem.IsMacOS()) return gesture.Replace("+", " + ");
        var text = string.Empty;
        foreach (var part in gesture.Split('+'))
            text += part switch
            {
                "Ctrl" => "⌃",
                "Alt" => "⌥",
                "Shift" => "⇧",
                "Win" => "⌘",
                _ => part
            };
        return text;
    }

    private void BeginShortcutCapture(ShortcutBindingViewModel row)
    {
        // A second press on the row that is already listening is the way out of listening.
        if (ReferenceEquals(_capturingShortcut, row))
        {
            CancelShortcutCapture();
            return;
        }
        CancelShortcutCapture();
        _capturingShortcut = row;
        row.IsCapturing = true;
        IsCapturingShortcut = true;
    }

    /// <summary>Stops listening without changing anything. Escape does this, and so does closing.</summary>
    public void CancelShortcutCapture()
    {
        if (_capturingShortcut is { } row) row.IsCapturing = false;
        _capturingShortcut = null;
        IsCapturingShortcut = false;
    }

    /// <summary>
    /// A combination was pressed while a row was listening. The view sends the gesture it read
    /// off the keyboard; anything that is not a usable global shortcut — a bare letter, a key
    /// with only shift on it — is ignored rather than rejected with a message, because the user
    /// is mid-gesture and the next key press is very likely the real answer.
    ///
    /// An empty gesture switches the shortcut off, which is the one way out of a collision with
    /// something this application cannot see.
    /// </summary>
    public bool ApplyCapturedShortcut(string? gesture)
    {
        if (_capturingShortcut is not { } row) return false;
        if (gesture is null)
        {
            CancelShortcutCapture();
            return true;
        }
        if (gesture.Length > 0 && !ShellShortcuts.TryNormalize(gesture, out gesture)) return false;

        SetShortcut(row, gesture);
        CancelShortcutCapture();
        return true;
    }

    private void ResetShortcut(ShortcutBindingViewModel row)
    {
        CancelShortcutCapture();
        SetShortcut(row, ShellShortcuts.Default(row.Action));
    }

    [RelayCommand]
    private void ResetShortcuts()
    {
        CancelShortcutCapture();
        foreach (var row in Shortcuts)
        {
            _shortcuts[row.Action] = ShellShortcuts.Default(row.Action);
            row.Gesture = _shortcuts[row.Action];
            row.IsRefused = false;
            _refusedShortcuts.Remove(row.Action);
        }
        CommitShortcuts();
    }

    private void SetShortcut(ShortcutBindingViewModel row, string gesture)
    {
        if (_shortcuts.GetValueOrDefault(row.Action) == gesture) return;
        _shortcuts[row.Action] = gesture;
        row.Gesture = gesture;
        // A refusal belongs to the combination that was refused, not to the row.
        row.IsRefused = false;
        _refusedShortcuts.Remove(row.Action);
        CommitShortcuts();
    }

    private void CommitShortcuts()
    {
        MarkShortcutClashes();
        if (_settings is not null) _settings.ShortcutsJson = ShellShortcuts.Write(_shortcuts);
        ScheduleSettingsSave();
        ShortcutsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void MarkShortcutClashes()
    {
        var clashing = ShellShortcuts.Clashes(_shortcuts).ToHashSet();
        foreach (var row in Shortcuts) row.HasClash = clashing.Contains(row.Action);
    }

    /// <summary>
    /// What the system made of the last attempt to claim them. A refused shortcut is not an
    /// error the user caused, so it is said on the row rather than in a dialog: something else
    /// on this machine holds those keys, and the row is where they would change them.
    /// </summary>
    public void ReportShortcutRegistration(bool supported, IReadOnlyList<ShellShortcutAction> refused)
    {
        AreGlobalShortcutsSupported = supported;
        OnPropertyChanged(nameof(ShortcutsHint));
        _refusedShortcuts.Clear();
        foreach (var action in refused) _refusedShortcuts.Add(action);
        foreach (var row in Shortcuts)
            row.IsRefused = supported && row.Gesture.Length > 0 && refused.Contains(row.Action);
    }

    // ---- What the shortcuts actually do ---------------------------------------------------
    // Two of the six are not commands anywhere else in the shell, because until now nothing but
    // the rail asked for them and the rail knows exactly which of the three it is offering.

    /// <summary>
    /// One key for the whole stopwatch: start it if it has not been started, hold it if it is
    /// counting, release it if it is held. A shortcut that only started sessions would be a
    /// shortcut the user could not use twice.
    /// </summary>
    [RelayCommand]
    private Task ToggleSessionAsync()
        => CanStartTracking ? StartEventAsync()
            : CanPauseSession ? PauseEventAsync()
            : CanResumeSession ? ResumeEventAsync()
            : Task.CompletedTask;
}
