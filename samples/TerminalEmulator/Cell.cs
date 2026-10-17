namespace TerminalEmulator;

[Flags]
internal enum CellFlags : ushort
{
    None = 0,
    Bold = 1 << 0,
    Dim = 1 << 1,
    Italic = 1 << 2,
    Underline = 1 << 3,
    Blink = 1 << 4,
    Inverse = 1 << 5,
    Hidden = 1 << 6,
    Strikethrough = 1 << 7,
}

internal enum ColorKind : byte
{
    Default = 0,
    Indexed = 1,
    Rgb = 2,
}

/// <summary>A terminal color: the default, one of the 256 palette entries, or a 24-bit value.</summary>
internal readonly record struct TermColor(uint Packed)
{
    public static readonly TermColor Default = new(0);

    public ColorKind Kind => (ColorKind)(Packed >> 24);

    public int Index => (int)(Packed & 0xFF);

    public uint Rgb => Packed & 0xFFFFFF;

    public static TermColor Indexed(int index) => new(((uint)ColorKind.Indexed << 24) | (uint)(index & 0xFF));

    public static TermColor FromRgb(int r, int g, int b) =>
        new(((uint)ColorKind.Rgb << 24) | ((uint)(r & 0xFF) << 16) | ((uint)(g & 0xFF) << 8) | (uint)(b & 0xFF));
}

/// <summary>Rendering attributes shared by a run of cells.</summary>
internal record struct CellAttributes(TermColor Foreground, TermColor Background, CellFlags Flags)
{
    public static readonly CellAttributes Plain = new(TermColor.Default, TermColor.Default, CellFlags.None);

    public bool Has(CellFlags flag) => (Flags & flag) != 0;
}

/// <summary>
/// One screen cell. <see cref="Text"/> is the grapheme shown (null for a blank), and <see cref="Width"/> is 1,
/// 2 for the left half of a wide character, or 0 for the right half that the wide character also covers.
/// </summary>
internal struct Cell
{
    public string? Text;
    public CellAttributes Attributes;
    public byte Width;

    public static Cell Blank(CellAttributes attributes) => new() { Text = null, Attributes = attributes, Width = 1 };

    public readonly bool IsBlank => Text is null && Width == 1;

    public readonly bool IsContinuation => Width == 0;
}

/// <summary>A row of cells, plus whether the row continues onto the next one because output wrapped.</summary>
internal sealed class TermLine
{
    public Cell[] Cells;
    public bool Wrapped;

    public TermLine(int columns, CellAttributes attributes)
    {
        Cells = new Cell[columns];
        Fill(attributes);
    }

    public void Fill(CellAttributes attributes)
    {
        Array.Fill(Cells, Cell.Blank(attributes));
        Wrapped = false;
    }

    public void Resize(int columns, CellAttributes attributes)
    {
        if (columns == Cells.Length)
            return;

        var cells = new Cell[columns];
        var keep = Math.Min(columns, Cells.Length);
        Array.Copy(Cells, cells, keep);
        if (keep < columns)
            Array.Fill(cells, Cell.Blank(attributes), keep, columns - keep);
        else if (keep > 0 && cells[keep - 1].Width == 2)
            cells[keep - 1] = Cell.Blank(attributes); // A wide character cannot be cut in half.

        Cells = cells;
    }

    /// <summary>The row's text with trailing blanks removed, for copying.</summary>
    public string ToText()
    {
        var end = Cells.Length;
        while (end > 0 && Cells[end - 1].IsBlank)
            end--;

        var text = new System.Text.StringBuilder(end);
        for (var i = 0; i < end; i++)
        {
            var cell = Cells[i];
            if (cell.IsContinuation)
                continue;

            text.Append(cell.Text ?? " ");
        }

        return text.ToString();
    }
}
