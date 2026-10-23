using System.Text.Json;

namespace IntelligentScheduler.Application;

/// <summary>
/// The things the shell can be asked to do without being looked at. Each one is a shortcut the
/// user can rebind; the set is deliberately small, because a shortcut is only worth a key
/// combination if it saves reaching for the edge of the screen at all.
/// </summary>
public enum ShellShortcutAction
{
    /// <summary>Bring the shell forward, or fold it back into the edge.</summary>
    ToggleShell,
    /// <summary>The quick widget beside the petal.</summary>
    ToggleWidget,
    /// <summary>The assistant, in the panel it lives in.</summary>
    ToggleAssistant,
    /// <summary>Set the current event's stopwatch going, or hold it.</summary>
    StartOrPauseSession,
    /// <summary>End the session and ask how it went.</summary>
    StopSession,
    /// <summary>A quiet spell, and the end of one.</summary>
    ToggleDoNotDisturb
}

/// <summary>
/// Which keys ask for which of those, and what a gesture is allowed to look like.
///
/// The defaults are all <c>Ctrl+Alt</c>. That family is the one place four very different
/// desktops agree to leave alone: Windows keeps <c>Win</c> for itself and reserves nothing in
/// <c>Ctrl+Alt</c> but the secure-attention sequence, macOS builds its own shortcuts on
/// <c>Cmd</c>, and niri and Hyprland both hang essentially every default binding off the
/// <c>Super</c> key. Nothing here uses <c>Super</c>/<c>Win</c>, a bare modifier-less key, or a
/// combination the window manager is likely to have taken first — and every one of them can be
/// changed, which is the real answer to a collision this cannot predict.
/// </summary>
public static class ShellShortcuts
{
    /// <summary>The actions, in the order the settings page lists them.</summary>
    public static IReadOnlyList<ShellShortcutAction> All { get; } =
    [
        ShellShortcutAction.ToggleShell,
        ShellShortcutAction.ToggleWidget,
        ShellShortcutAction.ToggleAssistant,
        ShellShortcutAction.StartOrPauseSession,
        ShellShortcutAction.StopSession,
        ShellShortcutAction.ToggleDoNotDisturb
    ];

    private static readonly Dictionary<ShellShortcutAction, string> DefaultGestures = new()
    {
        [ShellShortcutAction.ToggleShell] = "Ctrl+Alt+Space",
        [ShellShortcutAction.ToggleWidget] = "Ctrl+Alt+W",
        [ShellShortcutAction.ToggleAssistant] = "Ctrl+Alt+A",
        [ShellShortcutAction.StartOrPauseSession] = "Ctrl+Alt+S",
        [ShellShortcutAction.StopSession] = "Ctrl+Alt+E",
        [ShellShortcutAction.ToggleDoNotDisturb] = "Ctrl+Alt+Q"
    };

    /// <summary>Keys a gesture may end on, written the way they are shown and stored.</summary>
    private static readonly HashSet<string> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Space", "Enter", "Tab", "Escape", "Backspace", "Delete", "Insert",
        "Home", "End", "PageUp", "PageDown", "Left", "Right", "Up", "Down",
        "Comma", "Period", "Slash", "Backslash", "Semicolon", "Quote",
        "LeftBracket", "RightBracket", "Minus", "Plus", "Backtick"
    };

    public static string Default(ShellShortcutAction action) => DefaultGestures[action];

    /// <summary>
    /// Puts a gesture into the one spelling everything else compares against: modifiers first,
    /// always in the same order, then the key. Returns false for anything that would be a bad
    /// global shortcut — no key, no modifier, or shift on its own, which would swallow ordinary
    /// typing the moment it was registered.
    /// </summary>
    public static bool TryNormalize(string? gesture, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(gesture)) return false;

        var ctrl = false;
        var alt = false;
        var shift = false;
        var win = false;
        string? key = null;

        foreach (var part in gesture.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": ctrl = true; continue;
                case "alt" or "option" or "opt": alt = true; continue;
                case "shift": shift = true; continue;
                case "win" or "super" or "meta" or "cmd" or "command": win = true; continue;
            }

            // Two keys in one gesture is not a gesture.
            if (key is not null) return false;
            if (!TryNormalizeKey(part, out key)) return false;
        }

        // Shift alone is not enough: "Shift+A" is how a capital A is typed.
        if (key is null || !(ctrl || alt || win)) return false;

        var text = string.Empty;
        if (ctrl) text += "Ctrl+";
        if (alt) text += "Alt+";
        if (shift) text += "Shift+";
        if (win) text += "Win+";
        normalized = text + key;
        return true;
    }

    private static bool TryNormalizeKey(string part, out string key)
    {
        key = string.Empty;
        if (part.Length == 1 && char.IsAsciiLetterOrDigit(part[0]))
        {
            key = char.ToUpperInvariant(part[0]).ToString();
            return true;
        }

        if (part.Length is 2 or 3 && (part[0] is 'F' or 'f') && int.TryParse(part[1..], out var number)
            && number is >= 1 and <= 24)
        {
            key = "F" + number;
            return true;
        }

        if (!NamedKeys.TryGetValue(part, out var named)) return false;
        key = named;
        return true;
    }

    /// <summary>
    /// Reads the stored map. Anything unreadable, unknown or no longer a legal gesture falls
    /// back to the default for that action, so a settings row is never blank and a shortcut is
    /// never silently lost — <paramref name="legacyShellGesture"/> is the single hotkey this
    /// application had before there was a map, and it keeps whatever the user had set.
    /// </summary>
    public static IReadOnlyDictionary<ShellShortcutAction, string> Read(
        string? json, string? legacyShellGesture = null)
    {
        var stored = Deserialize(json);
        var map = new Dictionary<ShellShortcutAction, string>();
        foreach (var action in All)
        {
            var candidate = stored.GetValueOrDefault(action.ToString());
            if (candidate is null && action == ShellShortcutAction.ToggleShell)
                candidate = legacyShellGesture;
            // An empty string that was actually written down means "switched off", which is a
            // different answer from "never said" and the one way out of a collision this
            // application cannot see. Anything else unreadable falls back to the default.
            map[action] = candidate is { Length: 0 }
                ? string.Empty
                : TryNormalize(candidate, out var gesture) ? gesture : Default(action);
        }
        return map;
    }

    private static Dictionary<string, string> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Writes the map back, in a stable order so an unchanged set is an unchanged string.</summary>
    public static string Write(IReadOnlyDictionary<ShellShortcutAction, string> map)
        => JsonSerializer.Serialize(
            All.Where(map.ContainsKey).ToDictionary(action => action.ToString(), action => map[action]));

    /// <summary>
    /// Which actions are asking for a combination somebody else in the same map already has.
    /// Both sides of a clash are named: neither is more wrong than the other, and the user is
    /// the one who decides which of the two moves.
    /// </summary>
    public static IReadOnlyList<ShellShortcutAction> Clashes(IReadOnlyDictionary<ShellShortcutAction, string> map)
        => [.. map
            .Where(pair => pair.Value.Length > 0)
            .GroupBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group.Select(pair => pair.Key))];
}
