namespace TerminalEmulator;

/// <summary>
/// Resolves terminal colors to 24-bit values: the 16 ANSI colors (Windows Terminal's "Campbell" scheme), the
/// 6×6×6 cube and gray ramp of the 256-color palette, and direct RGB.
/// </summary>
internal static class Palette
{
    public const uint DefaultForeground = 0xCCCCCC;
    public const uint DefaultBackground = 0x0C0C0C;

    private static readonly uint[] s_ansi =
    [
        0x0C0C0C, 0xC50F1F, 0x13A10E, 0xC19C00, 0x0037DA, 0x881798, 0x3A96DD, 0xCCCCCC,
        0x767676, 0xE74856, 0x16C60C, 0xF9F1A5, 0x3B78FF, 0xB4009E, 0x61D6D6, 0xF2F2F2,
    ];

    public static uint Resolve(TermColor color, bool isForeground, bool bold)
    {
        switch (color.Kind)
        {
            case ColorKind.Rgb:
                return color.Rgb;
            case ColorKind.Indexed:
                var index = color.Index;
                // Bold brightens the eight basic colors, the convention every ANSI-era program relies on.
                if (bold && isForeground && index < 8)
                    index += 8;
                return Indexed(index);
            default:
                return isForeground ? DefaultForeground : DefaultBackground;
        }
    }

    private static uint Indexed(int index)
    {
        if (index < 16)
            return s_ansi[index];

        if (index < 232)
        {
            index -= 16;
            var r = index / 36;
            var g = index / 6 % 6;
            var b = index % 6;
            return (Level(r) << 16) | (Level(g) << 8) | Level(b);
        }

        var gray = (uint)(8 + (index - 232) * 10);
        return (gray << 16) | (gray << 8) | gray;
    }

    private static uint Level(int component) => component == 0 ? 0u : (uint)(55 + component * 40);
}
