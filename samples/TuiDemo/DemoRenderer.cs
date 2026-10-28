using System.Text;
using TerminalEmulator;

internal readonly record struct DemoView(
    int Width, int Height, int Selected, bool Paused, bool Dialog, int MouseColumn, int MouseRow, long Frame,
    int Page = DemoLayout.DashboardPage, FileBrowser Files = default, ChartsLayout Charts = default,
    DemoDrag Dragging = DemoDrag.None)
{
    internal bool Compact => Width < DemoLayout.MinWidth || Height < DemoLayout.MinHeight;
}

/// <summary>Which Charts border a left-button drag is moving.</summary>
internal enum DemoDrag : byte { None, Rows, Columns }

/// <summary>
/// The Charts split the user dragged: the bottom panels' height and the bar chart's width. Zero means the default;
/// <see cref="ChartsGeometry"/> clamps both to the current size.
/// </summary>
internal readonly record struct ChartsLayout(int BottomRows, int BarWidth);

/// <summary>
/// Charts positions for a size and a split. The bottom panels keep at least 7 rows (their three sparklines need them)
/// and the plot at least 3 dot rows; the bar chart keeps at least 30 columns and the sparklines at least 8 cells.
/// With the default split at 72 × 22 every value equals the original fixed layout.
/// </summary>
internal readonly struct ChartsGeometry
{
    internal const int DefaultBottomRows = 7;
    internal const int DefaultBarWidth = 35;
    private const int MinPlotRows = 3;
    private const int MinBarWidth = 30;
    private const int MinSparkLength = 8;

    internal ChartsGeometry(DemoGeometry g, ChartsLayout layout)
    {
        Width = g.Width;
        PageBottom = g.PageBottom;
        var pageRows = g.Height - 5;
        BottomRows = Math.Clamp(layout.BottomRows == 0 ? DefaultBottomRows : layout.BottomRows,
            DefaultBottomRows, pageRows - MinPlotRows - 2);
        BarWidth = Math.Clamp(layout.BarWidth == 0 ? DefaultBarWidth : layout.BarWidth,
            MinBarWidth, g.Width - 15 - MinSparkLength);
        PlotRows = pageRows - BottomRows - 2;
        MaxPlotRows = pageRows - DefaultBottomRows - 2;
    }

    internal int Width { get; }
    internal int PageBottom { get; }
    internal int BottomRows { get; }
    internal int BarWidth { get; }
    internal int PlotRows { get; }
    /// <summary>The plot's rows with the smallest bottom panels, for buffers that must not grow while dragging.</summary>
    internal int MaxPlotRows { get; }
    internal int PlotColumns => Width - 12;
    /// <summary>The plot panel's bottom border row.</summary>
    internal int PlotBottom => 4 + PlotRows;
    /// <summary>The bottom panels' top border row.</summary>
    internal int Top => PlotBottom + 1;
    internal int BarRows => BottomRows - 3;
    internal int BarSlot => (BarWidth - 5) / 5;
    /// <summary>The column between the bar chart and the Load panel.</summary>
    internal int Gap => BarWidth + 1;
    internal int LoadLeft => BarWidth + 2;
    internal int SparkLength => Width - BarWidth - 15;

    internal ChartsLayout Layout => new(BottomRows, BarWidth);
}

/// <summary>Cell positions (1-based) that do not depend on the terminal size; see <see cref="DemoGeometry"/> for the rest.</summary>
internal static class DemoLayout
{
    internal const int MinWidth = 72;
    internal const int MinHeight = 22;
    internal const int DashboardPage = 0;
    internal const int FilesPage = 1;
    internal const int ChartsPage = 2;
    internal const int PageCount = 3;
    internal const int TabRow = 1;
    internal const int FirstJobRow = 4;
    internal const int JobLeft = 25;
    internal const int ButtonRow = 8;
    internal const int PauseLeft = 26;
    internal const int PauseRight = 35;
    internal const int DetailsLeft = 38;
    internal const int DetailsRight = 48;
    internal const int LogTop = 12;
    internal const int CloseRow = 12;
    internal const int FilterRow = 4;
    internal const int FilterLeft = 5;
    internal const int FirstFileRow = 5;
    internal const int FileLeft = 2;

    internal static ReadOnlySpan<int> TabLeft => [20, 33, 42];
    internal static ReadOnlySpan<int> TabRight => [32, 41, 51];
}

/// <summary>
/// The size-dependent part of the layout for a terminal of at least 72 × 22, shared by the renderer, the TUI's hit
/// testing, and the smoke. Fixed-size parts (spinners, buttons, the Details panel, the bar chart, the dialog) keep
/// their size; progress bars, the log, the tree, the chart, and the sparklines take the rest. At 72 × 22 every value
/// equals the original fixed layout.
/// </summary>
internal readonly record struct DemoGeometry(int Width, int Height)
{
    /// <summary>The page area's last row, where its panels' bottom borders are.</summary>
    internal int PageBottom => Height - 3;
    internal int StatusRow => Height - 2;
    internal int HelpRow => Height - 1;
    internal int BottomRow => Height;
    internal int SizeColumn => Width - 17;
    internal int GradientCells => Width - 32;

    internal int JobRight => Width - 1;
    internal int BarCells => Width - 50;
    internal int LogBottom => Height - 4;
    internal int LogLines => LogBottom - DemoLayout.LogTop + 1;
    internal int DialogLeft => (Width - 42) / 2;
    internal int CloseLeft => DialogLeft + 16;
    internal int CloseRight => DialogLeft + 24;

    /// <summary>The tree panel spans columns 1 to this; the Details panel the last 31 columns.</summary>
    internal int TreeWidth => Width - 32;
    internal int FileRight => TreeWidth - 1;
    internal int FileRows => PageBottom - DemoLayout.FirstFileRow;
    internal int FilterCells => TreeWidth - 6;
    internal int DetailsColumn => Width - 30;
    internal int PreviewLines => PageBottom - 10;

    internal ChartsGeometry Charts(ChartsLayout layout) => new(this, layout);
}

/// <summary>
/// Keeps the last displayed state and overwrites only cells that changed. Each page paints every cell of rows
/// 3 to H−3, so switching pages, resizing, and closing the dialog rewrite cells in place without clearing; an
/// animation tick, a navigation step, or a keystroke rewrites only what it changed (see each page's file). The screen
/// is cleared only on first draw, on a switch between the full and compact layout, and on compact resize (the message
/// may have wrapped). A reusable character buffer batches each update into one write. This belongs to the TUI, not to
/// PTY transport.
/// </summary>
internal sealed partial class DemoRenderer
{
    private const string HeaderStyle = "\u001b[0;1;97;44m";
    private const string ActiveTabStyle = "\u001b[0;1;30;47m";
    private const string HelpStyle = "\u001b[0;30;47m";
    private const string BottomStyle = "\u001b[0;97;44m";
    private const string Bottom = " \u001b[1mQ/Ctrl+C: quit\u001b[22m  1-3/Tab: switch tabs  Shift+drag: select text";
    private const string BottomTyping = " \u001b[1mCtrl+C: quit\u001b[22m  Enter: stop typing  (Q and digits go to the filter)";
    private const string FilesTypingHelp = " Type to filter  Backspace: delete  Ctrl+U: clear  Enter: done";

    private static readonly string[] TabLabels = [" 1 Dashboard ", " 2 Files ", " 3 Charts "];
    private static readonly string[] Helps =
    [
        " Click/Wheel/Up/Down: select  Enter: details  Space: pause",
        " Up/Down/PgUp/PgDn/Home/End: move  Enter/Left/Right: fold  /: filter",
        " Space: pause  Drag a border: resize the charts",
    ];

    private readonly TextWriter _output;
    private readonly StringBuilder _frame = new(8192);
    private DemoView _previous;
    private bool _drawn;
    private bool _cursorShown;

    internal DemoRenderer(TextWriter output) => _output = output;

    internal void Draw(DemoView view)
    {
        if (_drawn && view == _previous)
            return;

        var g = new DemoGeometry(view.Width, view.Height);
        // The log follows the clock on every page, so returning to the dashboard shows a continuous history.
        KeepLogLines(g.LogLines);
        var added = CollectLog(view.Frame);
        var resized = !_drawn || view.Width != _previous.Width || view.Height != _previous.Height;
        var clear = !_drawn || view.Compact != _previous.Compact || (view.Compact && resized);
        // A dragged Charts split moves borders across the page, so it redraws the page in place like a resize.
        var chartsMoved = view.Page == DemoLayout.ChartsPage && (view.Charts != _previous.Charts || view.Dragging != _previous.Dragging);
        var redraw = clear || resized || chartsMoved || view.Page != _previous.Page || (_previous.Dialog && !view.Dialog);
        var mouseMoved = view.MouseColumn != _previous.MouseColumn || view.MouseRow != _previous.MouseRow;
        _frame.Clear();
        if (clear)
            _frame.Append("\u001b[0m\u001b[2J\u001b[H");

        if (view.Compact)
        {
            if (clear)
                _frame.Append("Resize to at least 72 x 22 (Q quits).");
            if (view.Width >= 20 && view.Height >= 2 && (clear || mouseMoved))
                DrawMouse(2, 1, view);
        }
        else if (redraw)
        {
            DrawHeader(view, g);
            DrawColorRow(g);
            switch (view.Page)
            {
                case DemoLayout.DashboardPage:
                    DrawDashboard(view, g);
                    break;
                case DemoLayout.FilesPage:
                    DrawFiles(view, g);
                    break;
                default:
                    DrawCharts(view, g);
                    break;
            }
            DrawStatus(view, g);
            DrawMouse(g.StatusRow, 33, view);
            DrawHelp(view, g);
            DrawBottomBar(view, g);
            if (view.Page == DemoLayout.DashboardPage && view.Dialog)
                DrawDialog(view, g);
        }
        else
        {
            switch (view.Page)
            {
                case DemoLayout.DashboardPage:
                    // The TUI stops the clock while the dialog is open; nothing under it changes.
                    if (!view.Dialog)
                        DrawDashboardChanges(view, g, added);
                    else if (!_previous.Dialog)
                        DrawDialog(view, g);
                    break;
                case DemoLayout.FilesPage:
                    DrawFilesChanges(view, g);
                    break;
                default:
                    DrawChartsChanges(view, g);
                    break;
            }
            if (StatusChanged(view))
                DrawStatus(view, g);
            if (mouseMoved)
                DrawMouse(g.StatusRow, 33, view);
        }
        PlaceCursor(view, g);

        if (_frame.Length > 0)
        {
            foreach (var chunk in _frame.GetChunks())
                _output.Write(chunk.Span);
            _output.Flush();
        }
        _previous = view;
        _drawn = true;
    }

    /// <summary>
    /// The terminal cursor is hidden everywhere except in the filter box while it takes text. Any write moves the
    /// cursor, so each frame that writes something while typing ends by putting it back after the text.
    /// </summary>
    private void PlaceCursor(DemoView view, DemoGeometry g)
    {
        var show = !view.Compact && view.Page == DemoLayout.FilesPage && view.Files.Editing;
        if (show && (_frame.Length > 0 || !_cursorShown))
        {
            MoveTo(DemoLayout.FilterRow, FilterCursorColumn(view, g));
            if (!_cursorShown)
                _frame.Append("\u001b[?25h");
        }
        else if (!show && _cursorShown)
        {
            _frame.Append("\u001b[?25l");
        }
        _cursorShown = show;
    }

    private void DrawHeader(DemoView view, DemoGeometry g)
    {
        MoveTo(1, 1).Append(HeaderStyle).Append(' ', g.Width);
        MoveTo(1, 2).Append("MiniPty TUI demo");
        for (var page = 0; page < DemoLayout.PageCount; page++)
            MoveTo(1, DemoLayout.TabLeft[page]).Append(page == view.Page ? ActiveTabStyle : HeaderStyle).Append(TabLabels[page]);
        MoveTo(1, g.SizeColumn).Append(HeaderStyle).Append("Size: ").Append(view.Width).Append(" x ").Append(view.Height);
        // Pad to the end of the bar so a shorter size erases the old digits.
        var written = 9 + Digits(view.Width) + Digits(view.Height);
        _frame.Append(' ', Math.Max(0, 18 - written)).Append("\u001b[0m");
    }

    /// <summary>Unicode text and a 24-bit gradient; how a frontend reduces 24-bit color is part of the comparison.</summary>
    private void DrawColorRow(DemoGeometry g)
    {
        MoveTo(2, 1).Append("\u001b[0mUnicode: 日本語 / café   24-bit ");
        var cells = g.GradientCells;
        for (var i = 0; i < cells; i++)
        {
            var (r, gr, b) = Hue(i * 299 / (cells - 1));
            _frame.Append("\u001b[48;2;").Append(r).Append(';').Append(gr).Append(';').Append(b).Append("m ");
        }
        _frame.Append("\u001b[0m");
    }

    private void DrawHelp(DemoView view, DemoGeometry g)
    {
        var help = view.Page == DemoLayout.FilesPage && view.Files.Editing ? FilesTypingHelp : Helps[view.Page];
        MoveTo(g.HelpRow, 1).Append(HelpStyle).Append(help).Append(' ', g.Width - help.Length).Append("\u001b[0m");
    }

    /// <summary>
    /// The last row. Its color is filled with EL instead of writing the last cell of the screen, which some terminals
    /// treat as a line feed and scroll the whole screen up.
    /// </summary>
    private void DrawBottomBar(DemoView view, DemoGeometry g) =>
        MoveTo(g.BottomRow, 1).Append(BottomStyle)
            .Append(view.Page == DemoLayout.FilesPage && view.Files.Editing ? BottomTyping : Bottom).Append("\u001b[K\u001b[0m");

    private bool StatusChanged(DemoView view) => view.Page switch
    {
        DemoLayout.DashboardPage => view.Selected != _previous.Selected || view.Paused != _previous.Paused,
        DemoLayout.FilesPage => view.Files != _previous.Files,
        _ => view.Frame != _previous.Frame || view.Paused != _previous.Paused,
    };

    /// <summary>Columns 1–32 of the status row; padding erases a longer status left by another page.</summary>
    private void DrawStatus(DemoView view, DemoGeometry g)
    {
        MoveTo(g.StatusRow, 1).Append("\u001b[0m");
        int written;
        switch (view.Page)
        {
            case DemoLayout.DashboardPage:
                _frame.Append("Selected: ").Append(view.Selected + 1).Append("/3   Paused: ").Append(view.Paused ? "yes" : "no ");
                written = 27;
                break;
            case DemoLayout.FilesPage:
                Span<int> visible = stackalloc int[DemoFiles.Count];
                var count = DemoFiles.Visible(view.Files, visible);
                var item = count == 0 ? 0 : view.Files.Cursor + 1;
                _frame.Append("Item: ").Append(item).Append('/').Append(count);
                written = 7 + Digits(item) + Digits(count);
                break;
            default:
                _frame.Append("Frame: ").Append(view.Frame).Append("   Paused: ").Append(view.Paused ? "yes" : "no ");
                written = 21 + Digits(view.Frame);
                break;
        }
        _frame.Append(' ', Math.Max(0, 32 - written));
    }

    private void DrawMouse(int row, int column, DemoView view) =>
        MoveTo(row, column).Append("\u001b[0mMouse: ").Append(view.MouseColumn).Append(',').Append(view.MouseRow).Append("\u001b[K");

    /// <summary>Appends <paramref name="text"/> cut to <paramref name="width"/> cells by display width, then pads it.</summary>
    private void AppendFitted(ReadOnlySpan<char> text, int width)
    {
        var used = 0;
        Span<char> chars = stackalloc char[2];
        foreach (var rune in text.EnumerateRunes())
        {
            var cells = Math.Max(0, CharWidth.Of(rune.Value));
            if (used + cells > width)
                break;
            _frame.Append(chars[..rune.EncodeToUtf16(chars)]);
            used += cells;
        }
        _frame.Append(' ', width - used);
    }

    /// <summary>Hue 0–299 at full saturation, as 8-bit RGB.</summary>
    private static (int R, int G, int B) Hue(int degrees)
    {
        const int value = 220;
        var rise = degrees % 60 * value / 60;
        return (degrees / 60) switch
        {
            0 => (value, rise, 0),
            1 => (value - rise, value, 0),
            2 => (0, value, rise),
            3 => (0, value - rise, value),
            _ => (rise, 0, value),
        };
    }

    private static int Digits(long value)
    {
        var digits = 1;
        while (value >= 10)
        {
            value /= 10;
            digits++;
        }
        return digits;
    }

    private StringBuilder AppendTwoDigits(int value) => _frame.Append((char)('0' + value / 10)).Append((char)('0' + value % 10));

    private StringBuilder MoveTo(int row, int column) => _frame.Append("\u001b[").Append(row).Append(';').Append(column).Append('H');
}
