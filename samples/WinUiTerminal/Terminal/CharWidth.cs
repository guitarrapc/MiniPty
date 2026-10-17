using System.Globalization;

namespace WinUiTerminal.Terminal;

/// <summary>
/// Terminal cell width of a code point: 0 for marks that attach to the previous cell, 2 for East Asian wide and
/// fullwidth characters and emoji, 1 for everything else. A condensed <c>wcwidth</c>; ambiguous-width characters
/// count as narrow, which is what ConPTY and most terminals assume.
/// </summary>
internal static class CharWidth
{
    public static int Of(int codePoint)
    {
        if (codePoint < 0x20)
            return 0;
        if (codePoint < 0x7F)
            return 1;
        if (codePoint is 0x7F or 0x200B or 0x200C or 0x200D or 0x2060 or 0xFEFF)
            return 0;
        if (codePoint is >= 0xFE00 and <= 0xFE0F || codePoint is >= 0xE0100 and <= 0xE01EF)
            return 0; // Variation selectors.
        if (codePoint is >= 0x1160 and <= 0x11FF)
            return 0; // Hangul jungseong/jongseong combine with the leading consonant.

        var category = CharUnicodeInfo.GetUnicodeCategory(codePoint);
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format)
            return 0;

        return IsWide(codePoint) ? 2 : 1;
    }

    /// <summary>Whether <paramref name="codePoint"/> is a mark or joiner that belongs to the cell before it.</summary>
    public static bool IsCombining(int codePoint) => codePoint >= 0x300 && Of(codePoint) == 0;

    private static bool IsWide(int cp) =>
        cp is >= 0x1100 and <= 0x115F // Hangul Jamo
        || cp is >= 0x231A and <= 0x231B
        || cp is >= 0x2329 and <= 0x232A
        || cp is >= 0x23E9 and <= 0x23EC
        || cp is 0x23F0 or 0x23F3
        || cp is >= 0x25FD and <= 0x25FE
        || cp is >= 0x2614 and <= 0x2615
        || cp is >= 0x2648 and <= 0x2653
        || cp is 0x267F or 0x2693 or 0x26A1
        || cp is >= 0x26AA and <= 0x26AB
        || cp is >= 0x26BD and <= 0x26BE
        || cp is >= 0x26C4 and <= 0x26C5
        || cp is 0x26CE or 0x26D4 or 0x26EA
        || cp is >= 0x26F2 and <= 0x26F3
        || cp is 0x26F5 or 0x26FA or 0x26FD or 0x2705
        || cp is >= 0x270A and <= 0x270B
        || cp is 0x2728 or 0x274C or 0x274E
        || cp is >= 0x2753 and <= 0x2755
        || cp is 0x2757
        || cp is >= 0x2795 and <= 0x2797
        || cp is 0x27B0 or 0x27BF
        || cp is >= 0x2B1B and <= 0x2B1C
        || cp is 0x2B50 or 0x2B55
        || cp is >= 0x2E80 and <= 0x303E // CJK radicals, punctuation (U+303F is narrow)
        || cp is >= 0x3041 and <= 0x33FF // Hiragana, Katakana, Bopomofo, Hangul compatibility, enclosed CJK
        || cp is >= 0x3400 and <= 0x4DBF // CJK Extension A
        || cp is >= 0x4E00 and <= 0x9FFF // CJK Unified
        || cp is >= 0xA000 and <= 0xA4CF // Yi
        || cp is >= 0xA960 and <= 0xA97F // Hangul Jamo Extended-A
        || cp is >= 0xAC00 and <= 0xD7A3 // Hangul syllables
        || cp is >= 0xF900 and <= 0xFAFF // CJK compatibility ideographs
        || cp is >= 0xFE10 and <= 0xFE19 // Vertical forms
        || cp is >= 0xFE30 and <= 0xFE6F // CJK compatibility forms, small forms
        || cp is >= 0xFF00 and <= 0xFF60 // Fullwidth forms
        || cp is >= 0xFFE0 and <= 0xFFE6
        || cp is >= 0x16FE0 and <= 0x16FE4
        || cp is >= 0x17000 and <= 0x18AFF // Tangut
        || cp is >= 0x1B000 and <= 0x1B2FF // Kana supplement
        || cp is >= 0x1F004 and <= 0x1F004
        || cp is 0x1F0CF or 0x1F18E
        || cp is >= 0x1F191 and <= 0x1F19A
        || cp is >= 0x1F200 and <= 0x1F251 // Enclosed ideographic supplement
        || cp is >= 0x1F300 and <= 0x1F64F // Misc symbols and pictographs, emoticons
        || cp is >= 0x1F680 and <= 0x1F6FF // Transport and map
        || cp is >= 0x1F7E0 and <= 0x1F7EB
        || cp is >= 0x1F90C and <= 0x1F9FF // Supplemental symbols and pictographs
        || cp is >= 0x1FA70 and <= 0x1FAFF
        || cp is >= 0x20000 and <= 0x3FFFD; // CJK Extensions B and later
}
