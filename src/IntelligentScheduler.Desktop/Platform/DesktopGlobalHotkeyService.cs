using System.Runtime.InteropServices;
using Avalonia.Controls;
using IntelligentScheduler.Application;

namespace IntelligentScheduler.Desktop.Platform;

/// <summary>
/// The shortcuts, claimed from the whole desktop.
///
/// Windows only, and deliberately so: this is <c>RegisterHotKey</c> against the shell's own
/// message loop. macOS wants a Carbon event handler and an accessibility grant, and Wayland
/// compositors — niri and Hyprland among them — do not let a client claim a key at all; there
/// the binding belongs in the compositor's own config. The same gestures are also bound inside
/// the application's windows (see <c>App.axaml.cs</c>), so every platform gets the shortcuts
/// while a DAYDRIVE window has focus and Windows additionally gets them from anywhere.
/// </summary>
public sealed class DesktopGlobalHotkeyService : IGlobalHotkeyService
{
    private const uint WmHotkey = 0x0312;
    /// <summary>The base of the id range; each action takes the slot at its own offset.</summary>
    private const int HotkeyIdBase = 0x51A7;

    private Window? _window;
    private Win32Properties.CustomWndProcHookCallback? _hook;
    private readonly Dictionary<int, Action> _callbacks = [];

    public bool IsSupported => OperatingSystem.IsWindows();

    public void Attach(Window window) => _window = window;

    public IReadOnlyList<ShellShortcutAction> Apply(IReadOnlyList<GlobalHotkey> hotkeys)
    {
        Unregister();
        if (!OperatingSystem.IsWindows() || _window?.TryGetPlatformHandle()?.Handle is not { } handle)
            return [.. hotkeys.Select(item => item.Action)];

        var refused = new List<ShellShortcutAction>();
        // The hook goes on before the first registration and stays on for the whole set: a
        // WM_HOTKEY that arrives while no hook is attached is a shortcut that silently does
        // nothing, which is the one failure this is meant to report rather than produce.
        _hook = WndProc;
        Win32Properties.AddWndProcHookCallback(_window, _hook);

        foreach (var hotkey in hotkeys)
        {
            var id = HotkeyIdBase + (int)hotkey.Action;
            if (!TryParseGesture(hotkey.Gesture, out var modifiers, out var key)
                || !RegisterHotKey(handle, id, modifiers, key))
            {
                refused.Add(hotkey.Action);
                continue;
            }
            _callbacks[id] = hotkey.Invoke;
        }

        if (_callbacks.Count == 0)
        {
            Win32Properties.RemoveWndProcHookCallback(_window, _hook);
            _hook = null;
        }
        return refused;
    }

    public void Unregister()
    {
        if (_window is not null && _hook is not null)
            Win32Properties.RemoveWndProcHookCallback(_window, _hook);
        if (OperatingSystem.IsWindows() && _window?.TryGetPlatformHandle()?.Handle is { } handle)
            foreach (var id in _callbacks.Keys)
                UnregisterHotKey(handle, id);
        _callbacks.Clear();
        _hook = null;
    }

    public void Dispose() => Unregister();

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && _callbacks.TryGetValue(wParam.ToInt32(), out var callback))
        {
            handled = true;
            callback();
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// The gesture, in the two numbers <c>RegisterHotKey</c> wants. It is normalized first, so
    /// this only has to deal with the one spelling <see cref="ShellShortcuts"/> produces.
    /// </summary>
    private static bool TryParseGesture(string gesture, out uint modifiers, out uint key)
    {
        modifiers = 0x4000; // MOD_NOREPEAT: held down, a shortcut fires once.
        key = 0;
        if (!ShellShortcuts.TryNormalize(gesture, out var normalized)) return false;

        foreach (var part in normalized.Split('+'))
        {
            switch (part)
            {
                case "Alt": modifiers |= 0x0001; continue;
                case "Ctrl": modifiers |= 0x0002; continue;
                case "Shift": modifiers |= 0x0004; continue;
                case "Win": modifiers |= 0x0008; continue;
            }
            if (!TryParseKey(part, out key)) return false;
        }
        return key != 0;
    }

    private static bool TryParseKey(string part, out uint key)
    {
        key = part switch
        {
            "Space" => 0x20,
            "Enter" => 0x0D,
            "Tab" => 0x09,
            "Escape" => 0x1B,
            "Backspace" => 0x08,
            "Delete" => 0x2E,
            "Insert" => 0x2D,
            "Home" => 0x24,
            "End" => 0x23,
            "PageUp" => 0x21,
            "PageDown" => 0x22,
            "Left" => 0x25,
            "Up" => 0x26,
            "Right" => 0x27,
            "Down" => 0x28,
            "Comma" => 0xBC,
            "Period" => 0xBE,
            "Slash" => 0xBF,
            "Backslash" => 0xDC,
            "Semicolon" => 0xBA,
            "Quote" => 0xDE,
            "LeftBracket" => 0xDB,
            "RightBracket" => 0xDD,
            "Minus" => 0xBD,
            "Plus" => 0xBB,
            "Backtick" => 0xC0,
            [>= 'A' and <= 'Z'] or [>= '0' and <= '9'] => part[0],
            ['F', ..] when int.TryParse(part[1..], out var number) && number is >= 1 and <= 24
                => (uint)(0x6F + number),
            _ => 0
        };
        return key != 0;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
