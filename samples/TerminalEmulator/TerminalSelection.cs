using System.Text;

namespace TerminalEmulator;

/// <summary>
/// A mouse selection over the screen, kept in absolute line coordinates so it stays put while the view scrolls.
/// The host feeds it pointer events in cell coordinates and asks it for the highlighted range per row and for
/// the text to copy.
/// </summary>
internal sealed class TerminalSelection
{
    private (int Row, int Column) _anchor;
    private (int Row, int Column) _focus;

    /// <summary>Whether a range is selected. A press without a drag selects nothing.</summary>
    public bool HasSelection { get; private set; }

    /// <summary>Whether the pointer is down and extending the selection.</summary>
    public bool IsDragging { get; private set; }

    public void Begin(int absoluteRow, int column)
    {
        _anchor = (absoluteRow, column);
        _focus = _anchor;
        HasSelection = false;
        IsDragging = true;
    }

    /// <returns><see langword="true"/> when the focus cell changed and the view should repaint.</returns>
    public bool Extend(int absoluteRow, int column)
    {
        var focus = (absoluteRow, column);
        if (focus == _focus)
            return false;

        _focus = focus;
        HasSelection = true;
        return true;
    }

    public void EndDrag() => IsDragging = false;

    public void Clear()
    {
        HasSelection = false;
        IsDragging = false;
    }

    /// <summary>Selects the run of non-blank cells around <paramref name="column"/>, for a double-click.</summary>
    public void SelectWord(ScreenBuffer screen, int absoluteRow, int column)
    {
        var line = screen.AbsoluteLine(absoluteRow);
        if (line is null || (uint)column >= (uint)line.Cells.Length || line.Cells[column].IsBlank)
            return;

        var cells = line.Cells;
        var start = column;
        while (start > 0 && !cells[start - 1].IsBlank)
            start--;
        var end = column;
        while (end + 1 < cells.Length && !cells[end + 1].IsBlank)
            end++;

        _anchor = (absoluteRow, start);
        _focus = (absoluteRow, end);
        HasSelection = true;
        IsDragging = false;
    }

    /// <summary>The highlighted column range on <paramref name="absoluteRow"/>, as [from, to).</summary>
    public bool TryGetRowRange(int absoluteRow, int columns, out int from, out int to)
    {
        from = 0;
        to = 0;
        if (!HasSelection)
            return false;

        var (start, end) = Normalized();
        if (absoluteRow < start.Row || absoluteRow > end.Row)
            return false;

        from = absoluteRow == start.Row ? start.Column : 0;
        to = absoluteRow == end.Row ? end.Column + 1 : columns;
        return to > from;
    }

    /// <summary>
    /// The selected text. Rows end with a Windows newline unless the row wrapped, in which case it continues on the
    /// next row the way it was written.
    /// </summary>
    public string GetText(ScreenBuffer screen)
    {
        if (!HasSelection)
            return string.Empty;

        var (start, end) = Normalized();
        var text = new StringBuilder();
        for (var row = start.Row; row <= end.Row; row++)
        {
            var line = screen.AbsoluteLine(row);
            if (line is null)
                continue;

            var rowText = line.ToText();
            var from = Math.Min(row == start.Row ? start.Column : 0, rowText.Length);
            var to = Math.Min(row == end.Row ? end.Column + 1 : int.MaxValue, rowText.Length);
            text.Append(rowText, from, Math.Max(0, to - from));
            if (row < end.Row && !line.Wrapped)
                text.Append("\r\n");
        }

        return text.ToString();
    }

    private ((int Row, int Column) Start, (int Row, int Column) End) Normalized()
    {
        var a = _anchor;
        var f = _focus;
        return a.Row < f.Row || (a.Row == f.Row && a.Column <= f.Column) ? (a, f) : (f, a);
    }
}
