using Avalonia.Input;

namespace IntelligentScheduler.Desktop.Platform;

internal static class LinuxLayerKeys
{
    // GDK keyvals are XKB keysyms. The hardware fallback keeps Ctrl+letter shortcuts usable
    // with Cyrillic and other non-Latin layouts, while text goes through GTK's input method.
    public static Key Translate(uint symbol, ushort hardwareCode)
    {
        if (symbol is >= 'a' and <= 'z') return Key.A + (int)(symbol - 'a');
        if (symbol is >= 'A' and <= 'Z') return Key.A + (int)(symbol - 'A');
        if (symbol is >= '0' and <= '9') return Key.D0 + (int)(symbol - '0');
        if (symbol is >= 0xffbe and <= 0xffd5) return Key.F1 + (int)(symbol - 0xffbe);
        return symbol switch
        {
            0xff08 => Key.Back,
            0xff09 or 0xfe20 => Key.Tab,
            0xff0d or 0xff8d => Key.Enter,
            0xff1b => Key.Escape,
            0xffff => Key.Delete,
            0xff63 => Key.Insert,
            0xff50 => Key.Home,
            0xff57 => Key.End,
            0xff55 => Key.PageUp,
            0xff56 => Key.PageDown,
            0xff51 => Key.Left,
            0xff52 => Key.Up,
            0xff53 => Key.Right,
            0xff54 => Key.Down,
            0xffe1 => Key.LeftShift,
            0xffe2 => Key.RightShift,
            0xffe3 => Key.LeftCtrl,
            0xffe4 => Key.RightCtrl,
            0xffe9 => Key.LeftAlt,
            0xffea or 0xfe03 => Key.RightAlt,
            0xffeb => Key.LWin,
            0xffec => Key.RWin,
            0xffe5 => Key.CapsLock,
            0xff7f => Key.NumLock,
            0xff14 => Key.Scroll,
            0x20 => Key.Space,
            0x2d or 0x5f => Key.OemMinus,
            0x3d or 0x2b => Key.OemPlus,
            0x5b or 0x7b => Key.OemOpenBrackets,
            0x5d or 0x7d => Key.OemCloseBrackets,
            0x5c or 0x7c => Key.OemPipe,
            0x3b or 0x3a => Key.OemSemicolon,
            0x27 or 0x22 => Key.OemQuotes,
            0x60 or 0x7e => Key.OemTilde,
            0x2c or 0x3c => Key.OemComma,
            0x2e or 0x3e => Key.OemPeriod,
            0x2f or 0x3f => Key.OemQuestion,
            >= 0xffb0 and <= 0xffb9 => Key.NumPad0 + (int)(symbol - 0xffb0),
            0xffaa => Key.Multiply,
            0xffab => Key.Add,
            0xffad => Key.Subtract,
            0xffae => Key.Decimal,
            0xffaf => Key.Divide,
            _ => hardwareCode switch
            {
                >= 24 and <= 33 => Letter("QWERTYUIOP"[hardwareCode - 24]),
                >= 38 and <= 46 => Letter("ASDFGHJKL"[hardwareCode - 38]),
                >= 52 and <= 58 => Letter("ZXCVBNM"[hardwareCode - 52]),
                _ => Key.None
            }
        };
    }

    private static Key Letter(char letter) => Key.A + (letter - 'A');
}
