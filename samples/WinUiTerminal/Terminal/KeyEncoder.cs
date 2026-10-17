using Windows.System;

namespace WinUiTerminal.Terminal;

/// <summary>
/// Turns a key press into the bytes a VT terminal sends: cursor and editing keys with xterm modifier encoding,
/// function keys, and control-character chords. Printable text does not come through here; the control takes
/// it from <c>CharacterReceived</c>, which already reflects the keyboard layout and IME.
/// </summary>
internal static class KeyEncoder
{
    private const string Esc = "\u001b";

    /// <returns>The sequence to send, or null when the key is not a terminal key (ordinary text, modifiers).</returns>
    public static string? Encode(VirtualKey key, bool shift, bool ctrl, bool alt, bool applicationCursorKeys)
    {
        var modifier = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);
        var hasModifier = modifier > 1;

        switch (key)
        {
            case VirtualKey.Up:
                return Cursor('A', hasModifier, modifier, applicationCursorKeys);
            case VirtualKey.Down:
                return Cursor('B', hasModifier, modifier, applicationCursorKeys);
            case VirtualKey.Right:
                return Cursor('C', hasModifier, modifier, applicationCursorKeys);
            case VirtualKey.Left:
                return Cursor('D', hasModifier, modifier, applicationCursorKeys);
            case VirtualKey.Home:
                return Cursor('H', hasModifier, modifier, applicationCursorKeys);
            case VirtualKey.End:
                return Cursor('F', hasModifier, modifier, applicationCursorKeys);
            case VirtualKey.Insert:
                return Tilde(2, hasModifier, modifier);
            case VirtualKey.Delete:
                return Tilde(3, hasModifier, modifier);
            case VirtualKey.PageUp:
                return Tilde(5, hasModifier, modifier);
            case VirtualKey.PageDown:
                return Tilde(6, hasModifier, modifier);
            case VirtualKey.F1:
                return FunctionLow('P', hasModifier, modifier);
            case VirtualKey.F2:
                return FunctionLow('Q', hasModifier, modifier);
            case VirtualKey.F3:
                return FunctionLow('R', hasModifier, modifier);
            case VirtualKey.F4:
                return FunctionLow('S', hasModifier, modifier);
            case VirtualKey.F5:
                return Tilde(15, hasModifier, modifier);
            case VirtualKey.F6:
                return Tilde(17, hasModifier, modifier);
            case VirtualKey.F7:
                return Tilde(18, hasModifier, modifier);
            case VirtualKey.F8:
                return Tilde(19, hasModifier, modifier);
            case VirtualKey.F9:
                return Tilde(20, hasModifier, modifier);
            case VirtualKey.F10:
                return Tilde(21, hasModifier, modifier);
            case VirtualKey.F11:
                return Tilde(23, hasModifier, modifier);
            case VirtualKey.F12:
                return Tilde(24, hasModifier, modifier);
            case VirtualKey.Enter:
                return alt ? Esc + "\r" : "\r";
            case VirtualKey.Tab:
                return shift ? Esc + "[Z" : "\t";
            case VirtualKey.Back:
                // DEL is what modern terminals send for Backspace; Ctrl+Backspace sends BS, Alt prefixes ESC.
                var backspace = ctrl ? "\b" : "\u007f";
                return alt ? Esc + backspace : backspace;
            case VirtualKey.Escape:
                return Esc;
            case VirtualKey.Space when ctrl:
                return "\0";
        }

        if (ctrl && !alt)
        {
            var control = ControlCharacter(key);
            if (control is not null)
                return control;
        }

        if (alt && !ctrl)
        {
            // Alt+letter sends ESC and the letter; the shell's readline treats that as Meta.
            if (key is >= VirtualKey.A and <= VirtualKey.Z)
                return Esc + (char)((shift ? 'A' : 'a') + (key - VirtualKey.A));
            if (key is >= VirtualKey.Number0 and <= VirtualKey.Number9)
                return Esc + (char)('0' + (key - VirtualKey.Number0));
        }

        return null;
    }

    private static string Cursor(char final, bool hasModifier, int modifier, bool applicationMode)
    {
        if (hasModifier)
            return $"{Esc}[1;{modifier}{final}";
        return applicationMode ? $"{Esc}O{final}" : $"{Esc}[{final}";
    }

    private static string Tilde(int code, bool hasModifier, int modifier) =>
        hasModifier ? $"{Esc}[{code};{modifier}~" : $"{Esc}[{code}~";

    private static string FunctionLow(char final, bool hasModifier, int modifier) =>
        hasModifier ? $"{Esc}[1;{modifier}{final}" : $"{Esc}O{final}";

    private static string? ControlCharacter(VirtualKey key)
    {
        if (key is >= VirtualKey.A and <= VirtualKey.Z)
            return ((char)(1 + (key - VirtualKey.A))).ToString();

        return (int)key switch
        {
            0xDB => "\u001b", // Ctrl+[
            0xDC => "\u001c", // Ctrl+\
            0xDD => "\u001d", // Ctrl+]
            (int)VirtualKey.Number2 => "\0",
            (int)VirtualKey.Number3 => "\u001b",
            (int)VirtualKey.Number4 => "\u001c",
            (int)VirtualKey.Number5 => "\u001d",
            (int)VirtualKey.Number6 => "\u001e",
            (int)VirtualKey.Number7 => "\u001f",
            (int)VirtualKey.Number8 => "\u007f",
            0xBD => "\u001f", // Ctrl+-
            _ => null,
        };
    }
}
