using System.Text;

namespace TerminalEmulator;

internal enum MouseTracking
{
    None,
    X10, // 9: press only
    Normal, // 1000: press and release
    Button, // 1002: plus drag
    Any, // 1003: plus motion
}

/// <summary>
/// The terminal's state: a main screen with scrollback, an alternate screen, the cursor, the scroll region,
/// tab stops, character sets, and the modes a VT520-class terminal exposes to programs. <see cref="VtParser"/>
/// drives it; the control reads it to render and to copy text.
/// </summary>
/// <remarks>
/// Lines are <see cref="TermLine"/> objects. The main buffer is one list where the last <see cref="Rows"/> lines
/// are the screen and everything before them is scrollback, so scrolling the whole screen up is an append.
/// Erased cells take the current background (background color erase), as xterm and Windows Terminal do.
/// </remarks>
internal sealed class ScreenBuffer
{
    public const int MaxScrollback = 5000;

    private readonly List<TermLine> _main = new();
    private TermLine[] _alt;
    private bool _altActive;
    private bool[] _tabStops;
    private CellAttributes _attributes = CellAttributes.Plain;
    private int _scrollTop;
    private int _scrollBottom;
    private bool _wrapPending;
    private bool _originMode;
    private bool _autoWrap = true;
    private bool _insertMode;
    private SavedCursor _savedMain;
    private SavedCursor _savedAlt;
    private bool _g0Graphics;
    private bool _g1Graphics;
    private bool _shiftOut;
    private string? _lastPrinted;
    private int _lastPrintedWidth;

    public ScreenBuffer(int columns, int rows)
    {
        Columns = Math.Max(1, columns);
        Rows = Math.Max(1, rows);
        for (var i = 0; i < Rows; i++)
            _main.Add(new TermLine(Columns, CellAttributes.Plain));

        _alt = NewScreen();
        _tabStops = DefaultTabStops(Columns);
        _scrollBottom = Rows - 1;
    }

    public int Columns { get; private set; }

    public int Rows { get; private set; }

    public int CursorRow { get; private set; }

    public int CursorColumn { get; private set; }

    public bool CursorVisible { get; private set; } = true;

    public bool ApplicationCursorKeys { get; private set; }

    public bool BracketedPaste { get; private set; }

    public bool FocusEvents { get; private set; }

    public bool SynchronizedOutput { get; private set; }

    public MouseTracking Mouse { get; private set; }

    public bool MouseSgr { get; private set; }

    public bool IsAlternateScreen => _altActive;

    /// <summary>Lines scrolled back from the bottom of the main buffer; 0 shows the live screen.</summary>
    public int ScrollOffset { get; private set; }

    public int ScrollbackCount => _altActive ? 0 : _main.Count - Rows;

    /// <summary>Set whenever the visible content changed; the renderer clears it.</summary>
    public bool Dirty { get; set; } = true;

    public string Title { get; private set; } = string.Empty;

    /// <summary>Bytes the terminal answers to the program (cursor position reports, device attributes).</summary>
    public event Action<string>? Response;

    public event Action<string>? TitleChanged;

    public event Action? Bell;

    // ---------------------------------------------------------------- Rendering access

    /// <summary>The line shown at <paramref name="viewRow"/>, honoring <see cref="ScrollOffset"/>.</summary>
    public TermLine ViewLine(int viewRow)
    {
        if (_altActive)
            return _alt[viewRow];

        var index = _main.Count - Rows - ScrollOffset + viewRow;
        return _main[Math.Clamp(index, 0, _main.Count - 1)];
    }

    /// <summary>Absolute index of a view row inside the line list, for selections that survive scrolling.</summary>
    public int ViewRowToAbsolute(int viewRow) => _altActive ? viewRow : _main.Count - Rows - ScrollOffset + viewRow;

    public TermLine? AbsoluteLine(int absoluteRow)
    {
        if (_altActive)
            return (uint)absoluteRow < (uint)_alt.Length ? _alt[absoluteRow] : null;

        return (uint)absoluteRow < (uint)_main.Count ? _main[absoluteRow] : null;
    }

    public void ScrollView(int lines)
    {
        if (_altActive)
            return;

        var offset = Math.Clamp(ScrollOffset + lines, 0, ScrollbackCount);
        if (offset == ScrollOffset)
            return;

        ScrollOffset = offset;
        Dirty = true;
    }

    public void ScrollViewToBottom()
    {
        if (ScrollOffset == 0)
            return;

        ScrollOffset = 0;
        Dirty = true;
    }

    // ---------------------------------------------------------------- Printing

    /// <summary>Writes one grapheme of <paramref name="width"/> cells (1 or 2) at the cursor.</summary>
    public void Print(string text, int width)
    {
        if (_shiftOut ? _g1Graphics : _g0Graphics)
            text = DecGraphics.Map(text);

        _lastPrinted = text;
        _lastPrintedWidth = width;
        PrintCore(text, width);
    }

    /// <summary>Repeats the last printed character, for REP.</summary>
    public void RepeatLast(int count)
    {
        if (_lastPrinted is null)
            return;

        for (var i = 0; i < count; i++)
            PrintCore(_lastPrinted, _lastPrintedWidth);
    }

    /// <summary>Attaches a combining mark to the cell before the cursor.</summary>
    public void Combine(string mark)
    {
        var line = ScreenLine(CursorRow);
        var column = _wrapPending ? CursorColumn : CursorColumn - 1;
        if (column < 0)
            return;

        if (line.Cells[column].IsContinuation && column > 0)
            column--;

        ref var cell = ref line.Cells[column];
        cell.Text = (cell.Text ?? " ") + mark;
        Dirty = true;
    }

    private void PrintCore(string text, int width)
    {
        if (_wrapPending)
        {
            if (_autoWrap)
            {
                ScreenLine(CursorRow).Wrapped = true;
                CursorColumn = 0;
                LineFeed();
            }

            _wrapPending = false;
        }

        if (width == 2 && CursorColumn == Columns - 1)
        {
            // A wide character does not fit in the last column: blank it and wrap first.
            var tail = ScreenLine(CursorRow);
            ClearWideAt(tail, CursorColumn);
            tail.Cells[CursorColumn] = Cell.Blank(_attributes);
            if (!_autoWrap)
                return;

            tail.Wrapped = true;
            CursorColumn = 0;
            LineFeed();
        }

        var line = ScreenLine(CursorRow);
        if (_insertMode)
            ShiftRight(line, CursorColumn, width);

        ClearWideAt(line, CursorColumn);
        if (width == 2)
            ClearWideAt(line, CursorColumn + 1);

        line.Cells[CursorColumn] = new Cell { Text = text, Attributes = _attributes, Width = (byte)width };
        if (width == 2)
            line.Cells[CursorColumn + 1] = new Cell { Text = null, Attributes = _attributes, Width = 0 };

        CursorColumn += width;
        if (CursorColumn >= Columns)
        {
            CursorColumn = Columns - 1;
            _wrapPending = _autoWrap;
        }

        ScrollOffset = 0;
        Dirty = true;
    }

    /// <summary>Overwriting either half of a wide character blanks the other half too.</summary>
    private void ClearWideAt(TermLine line, int column)
    {
        if ((uint)column >= (uint)Columns)
            return;

        ref var cell = ref line.Cells[column];
        if (cell.Width == 2 && column + 1 < Columns)
            line.Cells[column + 1] = Cell.Blank(_attributes);
        else if (cell.IsContinuation && column > 0)
            line.Cells[column - 1] = Cell.Blank(_attributes);
    }

    private void ShiftRight(TermLine line, int column, int count)
    {
        var cells = line.Cells;
        for (var i = Columns - 1; i >= column + count; i--)
            cells[i] = cells[i - count];
        for (var i = column; i < Math.Min(column + count, Columns); i++)
            cells[i] = Cell.Blank(_attributes);
    }

    // ---------------------------------------------------------------- C0 controls

    public void RingBell() => Bell?.Invoke();

    public void Backspace()
    {
        _wrapPending = false;
        if (CursorColumn > 0)
        {
            CursorColumn--;
            Dirty = true;
        }
    }

    public void Tab()
    {
        _wrapPending = false;
        var column = CursorColumn + 1;
        while (column < Columns - 1 && !_tabStops[column])
            column++;

        CursorColumn = Math.Min(column, Columns - 1);
        Dirty = true;
    }

    public void BackTab(int count)
    {
        _wrapPending = false;
        for (var n = 0; n < count; n++)
        {
            var column = CursorColumn - 1;
            while (column > 0 && !_tabStops[column])
                column--;

            CursorColumn = Math.Max(column, 0);
        }

        Dirty = true;
    }

    public void CarriageReturn()
    {
        _wrapPending = false;
        CursorColumn = 0;
        Dirty = true;
    }

    /// <summary>Line feed, also used for IND and the vertical part of NEL: scrolls at the bottom of the region.</summary>
    public void LineFeed()
    {
        _wrapPending = false;
        if (CursorRow == _scrollBottom)
            ScrollUp(1);
        else if (CursorRow < Rows - 1)
            CursorRow++;

        Dirty = true;
    }

    public void NextLine()
    {
        CarriageReturn();
        LineFeed();
    }

    /// <summary>Reverse index: moves up, scrolling the region down at its top.</summary>
    public void ReverseIndex()
    {
        _wrapPending = false;
        if (CursorRow == _scrollTop)
            ScrollDown(1);
        else if (CursorRow > 0)
            CursorRow--;

        Dirty = true;
    }

    public void ShiftOut() => _shiftOut = true;

    public void ShiftIn() => _shiftOut = false;

    public void DesignateCharset(bool g1, bool graphics)
    {
        if (g1)
            _g1Graphics = graphics;
        else
            _g0Graphics = graphics;
    }

    // ---------------------------------------------------------------- Cursor movement

    public void MoveCursor(int rowDelta, int columnDelta)
    {
        _wrapPending = false;
        var top = _originMode ? _scrollTop : 0;
        var bottom = _originMode ? _scrollBottom : Rows - 1;
        // Movement stops at the region when the cursor starts inside it.
        if (CursorRow >= _scrollTop && CursorRow <= _scrollBottom)
        {
            top = _scrollTop;
            bottom = _scrollBottom;
        }

        CursorRow = Math.Clamp(CursorRow + rowDelta, top, bottom);
        CursorColumn = Math.Clamp(CursorColumn + columnDelta, 0, Columns - 1);
        Dirty = true;
    }

    /// <summary>CUP/HVP with 1-based coordinates, relative to the scroll region in origin mode.</summary>
    public void SetCursor(int row, int column)
    {
        _wrapPending = false;
        var top = _originMode ? _scrollTop : 0;
        var bottom = _originMode ? _scrollBottom : Rows - 1;
        CursorRow = Math.Clamp(top + row - 1, top, bottom);
        CursorColumn = Math.Clamp(column - 1, 0, Columns - 1);
        Dirty = true;
    }

    public void SetColumn(int column)
    {
        _wrapPending = false;
        CursorColumn = Math.Clamp(column - 1, 0, Columns - 1);
        Dirty = true;
    }

    public void SetRow(int row)
    {
        _wrapPending = false;
        var top = _originMode ? _scrollTop : 0;
        var bottom = _originMode ? _scrollBottom : Rows - 1;
        CursorRow = Math.Clamp(top + row - 1, top, bottom);
        Dirty = true;
    }

    public void SaveCursor()
    {
        ref var saved = ref (_altActive ? ref _savedAlt : ref _savedMain);
        saved = new SavedCursor(CursorRow, CursorColumn, _attributes, _originMode, _autoWrap, _g0Graphics, _g1Graphics, _shiftOut, true);
    }

    public void RestoreCursor()
    {
        ref var saved = ref (_altActive ? ref _savedAlt : ref _savedMain);
        _wrapPending = false;
        if (!saved.Valid)
        {
            CursorRow = 0;
            CursorColumn = 0;
            _attributes = CellAttributes.Plain;
            Dirty = true;
            return;
        }

        CursorRow = Math.Clamp(saved.Row, 0, Rows - 1);
        CursorColumn = Math.Clamp(saved.Column, 0, Columns - 1);
        _attributes = saved.Attributes;
        _originMode = saved.OriginMode;
        _autoWrap = saved.AutoWrap;
        _g0Graphics = saved.G0Graphics;
        _g1Graphics = saved.G1Graphics;
        _shiftOut = saved.ShiftOut;
        Dirty = true;
    }

    // ---------------------------------------------------------------- Erasing and editing

    /// <summary>ED: 0 below, 1 above, 2 all, 3 scrollback.</summary>
    public void EraseInDisplay(int mode)
    {
        _wrapPending = false;
        switch (mode)
        {
            case 0:
                EraseInLine(0);
                for (var row = CursorRow + 1; row < Rows; row++)
                    ScreenLine(row).Fill(EraseAttributes);
                break;
            case 1:
                EraseInLine(1);
                for (var row = 0; row < CursorRow; row++)
                    ScreenLine(row).Fill(EraseAttributes);
                break;
            case 2:
                for (var row = 0; row < Rows; row++)
                    ScreenLine(row).Fill(EraseAttributes);
                break;
            case 3:
                if (!_altActive && _main.Count > Rows)
                {
                    _main.RemoveRange(0, _main.Count - Rows);
                    ScrollOffset = 0;
                }

                break;
        }

        Dirty = true;
    }

    /// <summary>EL: 0 right of the cursor, 1 left of it, 2 the whole line.</summary>
    public void EraseInLine(int mode)
    {
        _wrapPending = false;
        var line = ScreenLine(CursorRow);
        var (from, to) = mode switch
        {
            1 => (0, CursorColumn + 1),
            2 => (0, Columns),
            _ => (CursorColumn, Columns),
        };
        EraseCells(line, from, to);
        if (mode != 1)
            line.Wrapped = false;

        Dirty = true;
    }

    public void EraseCharacters(int count)
    {
        _wrapPending = false;
        var line = ScreenLine(CursorRow);
        EraseCells(line, CursorColumn, Math.Min(Columns, CursorColumn + Math.Max(1, count)));
        Dirty = true;
    }

    private void EraseCells(TermLine line, int from, int to)
    {
        if (from < to)
        {
            ClearWideAt(line, from);
            ClearWideAt(line, to - 1);
            Array.Fill(line.Cells, Cell.Blank(EraseAttributes), from, to - from);
        }
    }

    public void InsertCharacters(int count)
    {
        _wrapPending = false;
        var line = ScreenLine(CursorRow);
        ClearWideAt(line, CursorColumn);
        ShiftRight(line, CursorColumn, Math.Clamp(count, 1, Columns - CursorColumn));
        Dirty = true;
    }

    public void DeleteCharacters(int count)
    {
        _wrapPending = false;
        var line = ScreenLine(CursorRow);
        count = Math.Clamp(count, 1, Columns - CursorColumn);
        ClearWideAt(line, CursorColumn);
        var cells = line.Cells;
        for (var i = CursorColumn; i < Columns; i++)
            cells[i] = i + count < Columns ? cells[i + count] : Cell.Blank(EraseAttributes);

        Dirty = true;
    }

    public void InsertLines(int count)
    {
        _wrapPending = false;
        if (CursorRow < _scrollTop || CursorRow > _scrollBottom)
            return;

        CursorColumn = 0;
        ScrollDownWithin(CursorRow, _scrollBottom, Math.Max(1, count));
        Dirty = true;
    }

    public void DeleteLines(int count)
    {
        _wrapPending = false;
        if (CursorRow < _scrollTop || CursorRow > _scrollBottom)
            return;

        CursorColumn = 0;
        ScrollUpWithin(CursorRow, _scrollBottom, Math.Max(1, count));
        Dirty = true;
    }

    public void ScrollUp(int count)
    {
        count = Math.Max(1, count);
        if (!_altActive && _scrollTop == 0 && _scrollBottom == Rows - 1)
        {
            // Whole-screen scroll on the main buffer: the top lines become scrollback.
            for (var i = 0; i < count; i++)
                _main.Add(new TermLine(Columns, EraseAttributes));

            var excess = _main.Count - Rows - MaxScrollback;
            if (excess > 0)
                _main.RemoveRange(0, excess);

            // A reader who scrolled back keeps looking at the same lines.
            if (ScrollOffset > 0)
                ScrollOffset = Math.Min(ScrollOffset + count, ScrollbackCount);
        }
        else
        {
            ScrollUpWithin(_scrollTop, _scrollBottom, count);
        }

        Dirty = true;
    }

    public void ScrollDown(int count)
    {
        ScrollDownWithin(_scrollTop, _scrollBottom, Math.Max(1, count));
        Dirty = true;
    }

    private void ScrollUpWithin(int top, int bottom, int count)
    {
        count = Math.Min(count, bottom - top + 1);
        for (var row = top; row <= bottom; row++)
        {
            var source = row + count;
            SetScreenLine(row, source <= bottom ? ScreenLine(source) : new TermLine(Columns, EraseAttributes));
        }
    }

    private void ScrollDownWithin(int top, int bottom, int count)
    {
        count = Math.Min(count, bottom - top + 1);
        for (var row = bottom; row >= top; row--)
        {
            var source = row - count;
            SetScreenLine(row, source >= top ? ScreenLine(source) : new TermLine(Columns, EraseAttributes));
        }
    }

    public void SetScrollRegion(int top, int bottom)
    {
        _wrapPending = false;
        top = top < 1 ? 1 : top;
        bottom = bottom < 1 || bottom > Rows ? Rows : bottom;
        if (top >= bottom)
            return;

        _scrollTop = top - 1;
        _scrollBottom = bottom - 1;
        SetCursor(1, 1);
    }

    /// <summary>DECALN: fills the screen with E, the classic alignment test.</summary>
    public void FillWithE()
    {
        for (var row = 0; row < Rows; row++)
        {
            var cells = ScreenLine(row).Cells;
            for (var i = 0; i < Columns; i++)
                cells[i] = new Cell { Text = "E", Attributes = CellAttributes.Plain, Width = 1 };
        }

        Dirty = true;
    }

    // ---------------------------------------------------------------- Attributes

    public CellAttributes Attributes => _attributes;

    public void SetAttributes(CellAttributes attributes) => _attributes = attributes;

    private CellAttributes EraseAttributes => new(TermColor.Default, _attributes.Background, CellFlags.None);

    // ---------------------------------------------------------------- Modes

    public void SetTabStop() => _tabStops[CursorColumn] = true;

    public void ClearTabStop(int mode)
    {
        if (mode == 3)
            Array.Clear(_tabStops);
        else if (mode == 0)
            _tabStops[CursorColumn] = false;
    }

    public void SetInsertMode(bool enabled) => _insertMode = enabled;

    public void SetDecMode(int mode, bool enabled)
    {
        switch (mode)
        {
            case 1:
                ApplicationCursorKeys = enabled;
                break;
            case 6:
                _originMode = enabled;
                SetCursor(1, 1);
                break;
            case 7:
                _autoWrap = enabled;
                _wrapPending = false;
                break;
            case 9:
                Mouse = enabled ? MouseTracking.X10 : MouseTracking.None;
                break;
            case 25:
                CursorVisible = enabled;
                Dirty = true;
                break;
            case 47:
            case 1047:
                SwitchScreen(enabled, saveCursor: false, clearOnLeave: mode == 1047);
                break;
            case 1049:
                SwitchScreen(enabled, saveCursor: true, clearOnLeave: false);
                break;
            case 1000:
                Mouse = enabled ? MouseTracking.Normal : MouseTracking.None;
                break;
            case 1002:
                Mouse = enabled ? MouseTracking.Button : MouseTracking.None;
                break;
            case 1003:
                Mouse = enabled ? MouseTracking.Any : MouseTracking.None;
                break;
            case 1004:
                FocusEvents = enabled;
                break;
            case 1006:
                MouseSgr = enabled;
                break;
            case 2004:
                BracketedPaste = enabled;
                break;
            case 2026:
                SynchronizedOutput = enabled;
                if (!enabled)
                    Dirty = true;
                break;
        }
    }

    private void SwitchScreen(bool alternate, bool saveCursor, bool clearOnLeave)
    {
        if (alternate == _altActive)
            return;

        if (alternate)
        {
            if (saveCursor)
                SaveCursor();

            _altActive = true;
            _alt = NewScreen();
            ScrollOffset = 0;
            CursorRow = 0;
            CursorColumn = 0;
        }
        else
        {
            if (clearOnLeave)
                _alt = NewScreen();

            _altActive = false;
            if (saveCursor)
                RestoreCursor();
        }

        _wrapPending = false;
        _scrollTop = 0;
        _scrollBottom = Rows - 1;
        Dirty = true;
    }

    /// <summary>RIS: back to power-on state, keeping the size.</summary>
    public void Reset()
    {
        _altActive = false;
        _alt = NewScreen();
        _main.Clear();
        for (var i = 0; i < Rows; i++)
            _main.Add(new TermLine(Columns, CellAttributes.Plain));

        SoftReset();
        CursorRow = 0;
        CursorColumn = 0;
        ScrollOffset = 0;
        Mouse = MouseTracking.None;
        MouseSgr = false;
        BracketedPaste = false;
        FocusEvents = false;
        SynchronizedOutput = false;
        _tabStops = DefaultTabStops(Columns);
        Dirty = true;
    }

    /// <summary>DECSTR: resets modes and attributes, leaving the screen content alone.</summary>
    public void SoftReset()
    {
        _attributes = CellAttributes.Plain;
        _scrollTop = 0;
        _scrollBottom = Rows - 1;
        _originMode = false;
        _autoWrap = true;
        _insertMode = false;
        _wrapPending = false;
        _g0Graphics = false;
        _g1Graphics = false;
        _shiftOut = false;
        CursorVisible = true;
        ApplicationCursorKeys = false;
        _savedMain = default;
        _savedAlt = default;
        Dirty = true;
    }

    // ---------------------------------------------------------------- Reports

    public void ReportCursorPosition()
    {
        var row = CursorRow - (_originMode ? _scrollTop : 0) + 1;
        Response?.Invoke($"\u001b[{row};{CursorColumn + 1}R");
    }

    public void ReportStatusOk() => Response?.Invoke("\u001b[0n");

    public void ReportPrimaryAttributes() => Response?.Invoke("\u001b[?62;22c");

    public void ReportSecondaryAttributes() => Response?.Invoke("\u001b[>0;10;1c");

    public void SetTitle(string title)
    {
        if (title == Title)
            return;

        Title = title;
        TitleChanged?.Invoke(title);
    }

    // ---------------------------------------------------------------- Size

    public void Resize(int columns, int rows)
    {
        columns = Math.Max(1, columns);
        rows = Math.Max(1, rows);
        if (columns == Columns && rows == Rows)
            return;

        foreach (var line in _main)
            line.Resize(columns, CellAttributes.Plain);

        var alt = new TermLine[rows];
        for (var row = 0; row < rows; row++)
        {
            alt[row] = row < _alt.Length ? _alt[row] : new TermLine(columns, CellAttributes.Plain);
            alt[row].Resize(columns, CellAttributes.Plain);
        }

        _alt = alt;

        if (!_altActive)
        {
            // The cursor keeps its line. Shrinking removes blank lines below the cursor first, so a prompt near
            // the top is not pushed into scrollback; growing pulls scrollback back onto the screen.
            var absolute = _main.Count - Rows + CursorRow;
            while (_main.Count > rows && _main.Count - 1 > absolute && _main[^1].ToText().Length == 0)
                _main.RemoveAt(_main.Count - 1);

            while (_main.Count < rows)
                _main.Add(new TermLine(columns, CellAttributes.Plain));

            CursorRow = Math.Clamp(absolute - (_main.Count - rows), 0, rows - 1);
        }
        else
        {
            CursorRow = Math.Min(CursorRow, rows - 1);
        }

        var tabStops = DefaultTabStops(columns);
        Array.Copy(_tabStops, tabStops, Math.Min(_tabStops.Length, columns));
        _tabStops = tabStops;

        Columns = columns;
        Rows = rows;
        CursorColumn = Math.Min(CursorColumn, columns - 1);
        _scrollTop = 0;
        _scrollBottom = rows - 1;
        _wrapPending = false;
        ScrollOffset = Math.Min(ScrollOffset, ScrollbackCount);
        Dirty = true;
    }

    // ---------------------------------------------------------------- Helpers

    private TermLine ScreenLine(int row) => _altActive ? _alt[row] : _main[_main.Count - Rows + row];

    private void SetScreenLine(int row, TermLine line)
    {
        if (_altActive)
            _alt[row] = line;
        else
            _main[_main.Count - Rows + row] = line;
    }

    private TermLine[] NewScreen()
    {
        var screen = new TermLine[Rows];
        for (var row = 0; row < Rows; row++)
            screen[row] = new TermLine(Columns, CellAttributes.Plain);
        return screen;
    }

    private static bool[] DefaultTabStops(int columns)
    {
        var stops = new bool[columns];
        for (var i = 8; i < columns; i += 8)
            stops[i] = true;
        return stops;
    }

    private record struct SavedCursor(
        int Row,
        int Column,
        CellAttributes Attributes,
        bool OriginMode,
        bool AutoWrap,
        bool G0Graphics,
        bool G1Graphics,
        bool ShiftOut,
        bool Valid);
}

/// <summary>The DEC Special Graphics set (ESC ( 0): line-drawing glyphs on the ASCII lowercase range.</summary>
internal static class DecGraphics
{
    public static string Map(string text)
    {
        if (text.Length != 1)
            return text;

        return text[0] switch
        {
            '`' => "◆", 'a' => "▒", 'b' => "␉", 'c' => "␌", 'd' => "␍", 'e' => "␊",
            'f' => "°", 'g' => "±", 'h' => "␤", 'i' => "␋", 'j' => "┘", 'k' => "┐",
            'l' => "┌", 'm' => "└", 'n' => "┼", 'o' => "⎺", 'p' => "⎻", 'q' => "─",
            'r' => "⎼", 's' => "⎽", 't' => "├", 'u' => "┤", 'v' => "┴", 'w' => "┬",
            'x' => "│", 'y' => "≤", 'z' => "≥", '{' => "π", '|' => "≠", '}' => "£",
            '~' => "·",
            _ => text,
        };
    }
}
