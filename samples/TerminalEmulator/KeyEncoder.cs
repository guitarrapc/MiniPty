namespace TerminalEmulator;

/// <summary>
/// The keys a terminal cares about, independent of any UI framework's key enumeration. Each host maps its own
/// key codes onto this before calling <see cref="KeyEncoder"/>; printable text never comes through here.
/// </summary>
internal enum TermKey
{
    None,
    Up,
    Down,
    Right,
    Left,
    Home,
    End,
    Insert,
    Delete,
    PageUp,
    PageDown,
    F1,
    F2,
    F3,
    F4,
    F5,
    F6,
    F7,
    F8,
    F9,
    F10,
    F11,
    F12,
    Enter,
    Tab,
    Backspace,
    Escape,
    Space,
    A,
    B,
    C,
    D,
    E,
    F,
    G,
    H,
    I,
    J,
    K,
    L,
    M,
    N,
    O,
    P,
    Q,
    R,
    S,
    T,
    U,
    V,
    W,
    X,
    Y,
    Z,
    D0,
    D1,
    D2,
    D3,
    D4,
    D5,
    D6,
    D7,
    D8,
    D9,
    BracketLeft,
    Backslash,
    BracketRight,
    Minus,
}

/// <summary>
/// Turns a key press into the bytes a VT terminal sends: cursor and editing keys with xterm modifier encoding,
/// function keys, and control-character chords. Printable text does not come through here; hosts take it from
/// their text-input event, which already reflects the keyboard layout and IME.
/// </summary>
internal static class KeyEncoder
{
    private const string Esc = "\u001b";

    /// <returns>The sequence to send, or null when the key is not a terminal key (ordinary text, modifiers).</returns>
    public static string? Encode(TermKey key, bool shift, bool ctrl, bool alt, bool applicationCursorKeys)
    {
        var modifier = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);
        var hasModifier = modifier > 1;

        switch (key)
        {
            case TermKey.Up:
                return Cursor('A', hasModifier, modifier, applicationCursorKeys);
            case TermKey.Down:
                return Cursor('B', hasModifier, modifier, applicationCursorKeys);
            case TermKey.Right:
                return Cursor('C', hasModifier, modifier, applicationCursorKeys);
            case TermKey.Left:
                return Cursor('D', hasModifier, modifier, applicationCursorKeys);
            case TermKey.Home:
                return Cursor('H', hasModifier, modifier, applicationCursorKeys);
            case TermKey.End:
                return Cursor('F', hasModifier, modifier, applicationCursorKeys);
            case TermKey.Insert:
                return Tilde(2, hasModifier, modifier);
            case TermKey.Delete:
                return Tilde(3, hasModifier, modifier);
            case TermKey.PageUp:
                return Tilde(5, hasModifier, modifier);
            case TermKey.PageDown:
                return Tilde(6, hasModifier, modifier);
            case TermKey.F1:
                return FunctionLow('P', hasModifier, modifier);
            case TermKey.F2:
                return FunctionLow('Q', hasModifier, modifier);
            case TermKey.F3:
                return FunctionLow('R', hasModifier, modifier);
            case TermKey.F4:
                return FunctionLow('S', hasModifier, modifier);
            case TermKey.F5:
                return Tilde(15, hasModifier, modifier);
            case TermKey.F6:
                return Tilde(17, hasModifier, modifier);
            case TermKey.F7:
                return Tilde(18, hasModifier, modifier);
            case TermKey.F8:
                return Tilde(19, hasModifier, modifier);
            case TermKey.F9:
                return Tilde(20, hasModifier, modifier);
            case TermKey.F10:
                return Tilde(21, hasModifier, modifier);
            case TermKey.F11:
                return Tilde(23, hasModifier, modifier);
            case TermKey.F12:
                return Tilde(24, hasModifier, modifier);
            case TermKey.Enter:
                return alt ? Esc + "\r" : "\r";
            case TermKey.Tab:
                return shift ? Esc + "[Z" : "\t";
            case TermKey.Backspace:
                // DEL is what modern terminals send for Backspace; Ctrl+Backspace sends BS, Alt prefixes ESC.
                var backspace = ctrl ? "\b" : "\u007f";
                return alt ? Esc + backspace : backspace;
            case TermKey.Escape:
                return Esc;
            case TermKey.Space when ctrl:
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
            if (key is >= TermKey.A and <= TermKey.Z)
                return Esc + (char)((shift ? 'A' : 'a') + (key - TermKey.A));
            if (key is >= TermKey.D0 and <= TermKey.D9)
                return Esc + (char)('0' + (key - TermKey.D0));
        }

        return null;
    }

    /// <summary>The arrow key a wheel notch stands for in a full-screen program, which has no scrollback.</summary>
    public static string ScrollArrow(bool up, bool applicationCursorKeys) =>
        applicationCursorKeys ? (up ? Esc + "OA" : Esc + "OB") : (up ? Esc + "[A" : Esc + "[B");

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

    private static string? ControlCharacter(TermKey key)
    {
        if (key is >= TermKey.A and <= TermKey.Z)
            return ((char)(1 + (key - TermKey.A))).ToString();

        return key switch
        {
            TermKey.BracketLeft => "\u001b",
            TermKey.Backslash => "\u001c",
            TermKey.BracketRight => "\u001d",
            TermKey.D2 => "\0",
            TermKey.D3 => "\u001b",
            TermKey.D4 => "\u001c",
            TermKey.D5 => "\u001d",
            TermKey.D6 => "\u001e",
            TermKey.D7 => "\u001f",
            TermKey.D8 => "\u007f",
            TermKey.Minus => "\u001f",
            _ => null,
        };
    }
}
