namespace TerminalEmulator;

/// <summary>Encodes mouse events for a program that enabled mouse tracking.</summary>
internal static class MouseReport
{
    public const int LeftButton = 0;
    public const int MiddleButton = 1;
    public const int RightButton = 2;
    public const int NoButton = 3;
    public const int WheelUp = 64;
    public const int WheelDown = 65;

    /// <summary>
    /// The X10/normal report (<c>CSI M</c> with offset bytes) or, when the program asked for it, the SGR report
    /// (<c>CSI &lt; b;x;y M</c> for press and motion, <c>m</c> for release). Returns null when the current tracking
    /// mode does not report this event.
    /// </summary>
    public static string? Encode(
        MouseTracking mode,
        bool sgr,
        int button,
        int row,
        int column,
        bool release,
        bool motion,
        bool shift,
        bool alt,
        bool ctrl)
    {
        if (mode == MouseTracking.None)
            return null;
        if (mode == MouseTracking.X10 && (release || motion || button > RightButton))
            return null;
        if (motion && mode == MouseTracking.Normal)
            return null;

        var code = button + (shift ? 4 : 0) + (alt ? 8 : 0) + (ctrl ? 16 : 0) + (motion ? 32 : 0);
        if (sgr)
            return $"\u001b[<{code};{column + 1};{row + 1}{(release ? 'm' : 'M')}";

        if (release)
            code = NoButton + (code & ~3);
        if (column > 222 || row > 222)
            return null;

        return $"\u001b[M{(char)(32 + code)}{(char)(33 + column)}{(char)(33 + row)}";
    }
}
