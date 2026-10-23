using Avalonia.Input;
using IntelligentScheduler.Application;

namespace IntelligentScheduler.Desktop.Controls;

/// <summary>
/// The two translations between a key press and the way a shortcut is written down: reading one
/// off the keyboard while the user is rebinding, and handing one back to Avalonia so the same
/// binding also works inside the application's own windows.
/// </summary>
public static class ShortcutGesture
{
    /// <summary>Keys that are a shortcut by name rather than by character.</summary>
    private static readonly Dictionary<Key, string> Named = new()
    {
        [Key.Space] = "Space",
        [Key.Enter] = "Enter",
        [Key.Tab] = "Tab",
        [Key.Escape] = "Escape",
        [Key.Back] = "Backspace",
        [Key.Delete] = "Delete",
        [Key.Insert] = "Insert",
        [Key.Home] = "Home",
        [Key.End] = "End",
        [Key.PageUp] = "PageUp",
        [Key.PageDown] = "PageDown",
        [Key.Left] = "Left",
        [Key.Right] = "Right",
        [Key.Up] = "Up",
        [Key.Down] = "Down",
        [Key.OemComma] = "Comma",
        [Key.OemPeriod] = "Period",
        [Key.OemQuestion] = "Slash",
        [Key.OemBackslash] = "Backslash",
        [Key.OemPipe] = "Backslash",
        [Key.OemSemicolon] = "Semicolon",
        [Key.OemQuotes] = "Quote",
        [Key.OemOpenBrackets] = "LeftBracket",
        [Key.OemCloseBrackets] = "RightBracket",
        [Key.OemMinus] = "Minus",
        [Key.OemPlus] = "Plus",
        [Key.OemTilde] = "Backtick"
    };

    private static readonly HashSet<Key> ModifierKeys =
    [
        Key.LeftCtrl, Key.RightCtrl, Key.LeftAlt, Key.RightAlt,
        Key.LeftShift, Key.RightShift, Key.LWin, Key.RWin, Key.System
    ];

    /// <summary>
    /// What was just pressed, written the way a shortcut is stored.
    ///
    /// Three answers, and the difference matters while a row is listening. A gesture is a
    /// finished answer. An empty string is "switch this shortcut off", which is what the delete
    /// keys mean here. Null is "that was not an answer" — a modifier held on its own, or a
    /// combination that would be a bad global shortcut — and the row goes on listening, because
    /// the user is almost certainly still in the middle of the gesture.
    /// </summary>
    public static string? Read(KeyModifiers modifiers, Key key)
    {
        if (ModifierKeys.Contains(key)) return null;
        if (key is Key.Back or Key.Delete && modifiers == KeyModifiers.None) return string.Empty;

        var text = string.Empty;
        if (modifiers.HasFlag(KeyModifiers.Control)) text += "Ctrl+";
        if (modifiers.HasFlag(KeyModifiers.Alt)) text += "Alt+";
        if (modifiers.HasFlag(KeyModifiers.Shift)) text += "Shift+";
        if (modifiers.HasFlag(KeyModifiers.Meta)) text += "Win+";

        var name = key switch
        {
            >= Key.A and <= Key.Z => key.ToString(),
            >= Key.D0 and <= Key.D9 => key.ToString()[1..],
            >= Key.F1 and <= Key.F24 => key.ToString(),
            _ => Named.GetValueOrDefault(key)
        };
        if (name is null) return null;

        return ShellShortcuts.TryNormalize(text + name, out var gesture) ? gesture : null;
    }

    /// <summary>
    /// The same gesture as something Avalonia can match a key press against, for the binding
    /// that works while one of this application's own windows has focus. That binding is the
    /// only one macOS and the Wayland compositors allow at all.
    /// </summary>
    public static KeyGesture? ToKeyGesture(string gesture)
    {
        if (!ShellShortcuts.TryNormalize(gesture, out var normalized)) return null;
        var modifiers = KeyModifiers.None;
        var key = Key.None;
        foreach (var part in normalized.Split('+'))
            switch (part)
            {
                case "Ctrl": modifiers |= KeyModifiers.Control; break;
                case "Alt": modifiers |= KeyModifiers.Alt; break;
                case "Shift": modifiers |= KeyModifiers.Shift; break;
                case "Win": modifiers |= KeyModifiers.Meta; break;
                default: key = ToKey(part); break;
            }
        return key == Key.None ? null : new KeyGesture(key, modifiers);
    }

    private static Key ToKey(string part)
    {
        foreach (var (candidate, name) in Named)
            if (name == part) return candidate;
        if (part.Length == 1 && char.IsAsciiLetter(part[0])) return Enum.Parse<Key>(part);
        if (part.Length == 1 && char.IsAsciiDigit(part[0])) return Key.D0 + (part[0] - '0');
        return part.Length > 1 && part[0] == 'F' && Enum.TryParse<Key>(part, out var function)
            ? function
            : Key.None;
    }
}
