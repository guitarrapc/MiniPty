using System.Text;
using TerminalEmulator;

internal readonly record struct DemoView(
    int Width, int Height, int Selected, bool Paused, bool Dialog, int MouseColumn, int MouseRow, long Frame,
    int Page = DemoLayout.DashboardPage, FileBrowser Files = default)
{
    internal bool Compact => Width < DemoLayout.MinWidth || Height < DemoLayout.MinHeight;
}

/// <summary>Cell positions (1-based) of the fixed 72 × 22 layout, shared by the renderer and the TUI's hit testing.</summary>
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
    internal const int JobRight = 71;
    internal const int ButtonRow = 8;
    internal const int PauseLeft = 26;
    internal const int PauseRight = 35;
    internal const int DetailsLeft = 38;
    internal const int DetailsRight = 48;
    internal const int LogTop = 12;
    internal const int LogBottom = 18;
    internal const int LogLines = LogBottom - LogTop + 1;
    internal const int StatusRow = 20;
    internal const int CloseRow = 12;
    internal const int CloseLeft = 31;
    internal const int CloseRight = 39;
    internal const int FirstFileRow = 4;
    internal const int FileLeft = 2;
    internal const int FileRight = 39;

    internal static ReadOnlySpan<int> TabLeft => [20, 33, 42];
    internal static ReadOnlySpan<int> TabRight => [32, 41, 51];
}

/// <summary>
/// Keeps the last displayed state and overwrites only cells that changed. The layout has fixed cell positions: each
/// page paints every cell of rows 3–19, so switching pages, like closing the dialog, rewrites cells in place without
/// clearing; an animation tick or a navigation step rewrites only what it changed (see each page's file); and normal
/// resizing within the minimum size only changes the size label. The screen is cleared only on first draw, on a
/// switch between the full and compact layout, and on compact resize (the message may have wrapped). A reusable
/// character buffer batches each update into one write. This belongs to the TUI, not to PTY transport.
/// </summary>
internal sealed partial class DemoRenderer
{
    private const string HeaderStyle = "\u001b[0;1;97;44m";
    private const string ActiveTabStyle = "\u001b[0;1;30;47m";
    private const string Hint = "1-3/Tab: switch tabs | Text selection: Shift+drag (terminal-dependent)";

    private static readonly string[] TabLabels = [" 1 Dashboard ", " 2 Files ", " 3 Charts "];
    private static readonly string[] Helps =
    [
        " Click/Wheel/Up/Down: select  Enter: details  Space: pause  Q: quit",
        " Up/Down/PgUp/PgDn/Home/End: move  Enter/Left/Right: open/close  Q: quit",
        " Space: pause  Q: quit",
    ];

    private readonly TextWriter _output;
    private readonly StringBuilder _frame = new(8192);
    private DemoView _previous;
    private bool _drawn;

    internal DemoRenderer(TextWriter output) => _output = output;

    internal void Draw(DemoView view)
    {
        if (_drawn && view == _previous)
            return;

        // The log follows the clock on every page, so returning to the dashboard shows a continuous history.
        var added = CollectLog(view.Frame);
        var resized = !_drawn || view.Width != _previous.Width || view.Height != _previous.Height;
        var clear = !_drawn || view.Compact != _previous.Compact || (view.Compact && resized);
        var redraw = clear || view.Page != _previous.Page || (_previous.Dialog && !view.Dialog);
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
            DrawHeader(view);
            DrawColorRow();
            switch (view.Page)
            {
                case DemoLayout.DashboardPage:
                    DrawDashboard(view);
                    break;
                case DemoLayout.FilesPage:
                    DrawFiles(view);
                    break;
                default:
                    DrawCharts(view);
                    break;
            }
            DrawStatus(view);
            DrawMouse(DemoLayout.StatusRow, 33, view);
            var help = Helps[view.Page];
            MoveTo(21, 1).Append("\u001b[0;30;47m").Append(help).Append(' ', DemoLayout.MinWidth - help.Length).Append("\u001b[0m");
            MoveTo(22, 1).Append(Hint).Append("\u001b[K");
            if (view.Page == DemoLayout.DashboardPage && view.Dialog)
                DrawDialog(view);
        }
        else
        {
            if (resized)
                DrawSize(view);
            switch (view.Page)
            {
                case DemoLayout.DashboardPage:
                    // The TUI stops the clock while the dialog is open; nothing under it changes.
                    if (!view.Dialog)
                        DrawDashboardChanges(view, added);
                    else if (!_previous.Dialog)
                        DrawDialog(view);
                    break;
                case DemoLayout.FilesPage:
                    DrawFilesChanges(view);
                    break;
                default:
                    DrawChartsChanges(view);
                    break;
            }
            if (StatusChanged(view))
                DrawStatus(view);
            if (mouseMoved)
                DrawMouse(DemoLayout.StatusRow, 33, view);
        }

        if (_frame.Length > 0)
        {
            foreach (var chunk in _frame.GetChunks())
                _output.Write(chunk.Span);
            _output.Flush();
        }
        _previous = view;
        _drawn = true;
    }

    private void DrawHeader(DemoView view)
    {
        MoveTo(1, 1).Append(HeaderStyle).Append(' ', DemoLayout.MinWidth);
        MoveTo(1, 2).Append("MiniPty TUI demo");
        for (var page = 0; page < DemoLayout.PageCount; page++)
            MoveTo(1, DemoLayout.TabLeft[page]).Append(page == view.Page ? ActiveTabStyle : HeaderStyle).Append(TabLabels[page]);
        DrawSize(view);
    }

    private void DrawSize(DemoView view)
    {
        MoveTo(1, 55).Append(HeaderStyle).Append("Size: ").Append(view.Width).Append(" x ").Append(view.Height);
        // Pad to the end of the bar so a shorter size erases the old digits.
        var written = 9 + Digits(view.Width) + Digits(view.Height);
        _frame.Append(' ', Math.Max(0, 18 - written)).Append("\u001b[0m");
    }

    /// <summary>Unicode text and a 24-bit gradient; how a frontend reduces 24-bit color is part of the comparison.</summary>
    private void DrawColorRow()
    {
        MoveTo(2, 1).Append("\u001b[0mUnicode: 日本語 / café   24-bit ");
        for (var i = 0; i < 40; i++)
        {
            var (r, g, b) = Hue(i * 299 / 39);
            _frame.Append("\u001b[48;2;").Append(r).Append(';').Append(g).Append(';').Append(b).Append("m ");
        }
        _frame.Append("\u001b[0m");
    }

    private bool StatusChanged(DemoView view) => view.Page switch
    {
        DemoLayout.DashboardPage => view.Selected != _previous.Selected || view.Paused != _previous.Paused,
        DemoLayout.FilesPage => view.Files != _previous.Files,
        _ => view.Frame != _previous.Frame || view.Paused != _previous.Paused,
    };

    /// <summary>Columns 1–32 of the status row; padding erases a longer status left by another page.</summary>
    private void DrawStatus(DemoView view)
    {
        MoveTo(DemoLayout.StatusRow, 1).Append("\u001b[0m");
        int written;
        switch (view.Page)
        {
            case DemoLayout.DashboardPage:
                _frame.Append("Selected: ").Append(view.Selected + 1).Append("/3   Paused: ").Append(view.Paused ? "yes" : "no ");
                written = 27;
                break;
            case DemoLayout.FilesPage:
                Span<int> visible = stackalloc int[DemoFiles.Count];
                var count = DemoFiles.Visible(view.Files.Expanded, visible);
                _frame.Append("Item: ").Append(view.Files.Cursor + 1).Append('/').Append(count);
                written = 7 + Digits(view.Files.Cursor + 1) + Digits(count);
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
