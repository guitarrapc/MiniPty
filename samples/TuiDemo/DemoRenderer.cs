using System.Text;

internal readonly record struct DemoView(
    int Width, int Height, int Selected, bool Paused, bool Dialog, int MouseColumn, int MouseRow, long Frame)
{
    internal bool Compact => Width < DemoLayout.MinWidth || Height < DemoLayout.MinHeight;
}

/// <summary>Cell positions (1-based) of the fixed 72 × 22 layout, shared by the renderer and the TUI's hit testing.</summary>
internal static class DemoLayout
{
    internal const int MinWidth = 72;
    internal const int MinHeight = 22;
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
}

/// <summary>
/// Keeps the last displayed state and overwrites only cells that changed. The layout has fixed cell positions, so
/// an animation tick rewrites the spinner glyphs, the job rows, and the elapsed time; a new log line scrolls the log
/// through a scroll region instead of redrawing it; and normal resizing within the minimum size only changes the
/// size label. The screen is cleared only on first draw, on a layout transition, and on compact resize (the message
/// may have wrapped). Closing the dialog rewrites every cell in place, which avoids the blank frame a clear shows.
/// A reusable character buffer batches each update into one write. This belongs to the TUI, not to PTY transport.
/// </summary>
internal sealed class DemoRenderer(TextWriter output)
{
    private const int FramesPerSecond = 1000 / DemoJobs.FrameMilliseconds;
    private const int MaxCatchUpFrames = 1000;
    private const string HeaderStyle = "\u001b[0;1;97;44m";
    private const string DialogStyle = "\u001b[0;97;44m";
    private const string Help = " Click/Wheel/Up/Down: select  Enter: details  Space: pause  Q: quit";
    private const string Eighths = "▏▎▍▌▋▊▉";

    private readonly StringBuilder _frame = new(8192);
    private readonly DemoLogEntry[] _log = new DemoLogEntry[DemoLayout.LogLines];
    private int _logCount;
    private int _logNext;
    private long _loggedFrame = -1;
    private DemoView _previous;
    private bool _drawn;

    internal void Draw(DemoView view)
    {
        if (_drawn && view == _previous)
            return;

        // The log follows the clock even while it is not visible, so a later redraw shows a continuous history.
        var added = CollectLog(view.Frame);
        var resized = !_drawn || view.Width != _previous.Width || view.Height != _previous.Height;
        var clear = !_drawn || view.Compact != _previous.Compact || (view.Compact && resized);
        var redraw = clear || (_previous.Dialog && !view.Dialog);
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
            DrawAll(view);
        }
        else
        {
            if (resized)
                DrawSize(view);
            if (view.Dialog)
            {
                // The TUI stops the clock while the dialog is open; nothing under it changes.
                if (!_previous.Dialog)
                    DrawDialog(view);
            }
            else
            {
                DrawChanges(view, added);
            }
            if (view.Selected != _previous.Selected || view.Paused != _previous.Paused)
                DrawStatus(view);
            if (mouseMoved)
                DrawMouse(DemoLayout.StatusRow, 33, view);
        }

        if (_frame.Length > 0)
        {
            foreach (var chunk in _frame.GetChunks())
                output.Write(chunk.Span);
            output.Flush();
        }
        _previous = view;
        _drawn = true;
    }

    private void DrawAll(DemoView view)
    {
        DrawHeader(view);
        DrawColorRow();
        DrawPanels();
        for (var i = 0; i < DemoSpinners.Count; i++)
            DrawSpinner(i, view.Frame);
        for (var i = 0; i < DemoJobs.Count; i++)
            DrawJob(i, view);
        DrawButtons(view);
        for (var k = 0; k < DemoLayout.LogLines; k++)
        {
            var blanks = DemoLayout.LogLines - _logCount;
            DrawLogLine(DemoLayout.LogTop + k, k < blanks ? default : LogEntryAt(k - blanks), blank: k < blanks);
        }
        DrawStatus(view);
        DrawMouse(DemoLayout.StatusRow, 33, view);
        MoveTo(21, 1).Append("\u001b[0;30;47m").Append(Help).Append(' ', DemoLayout.MinWidth - Help.Length).Append("\u001b[0m");
        MoveTo(22, 1).Append("Text selection: Shift+drag (depends on terminal)\u001b[K");
        if (view.Dialog)
            DrawDialog(view);
    }

    private void DrawChanges(DemoView view, int added)
    {
        var frame = view.Frame;
        var before = _previous.Frame;
        if (frame / FramesPerSecond != before / FramesPerSecond)
            DrawElapsed(frame);
        for (var i = 0; i < DemoSpinners.Count; i++)
        {
            if (DemoSpinners.Glyph(i, frame) != DemoSpinners.Glyph(i, before))
                DrawSpinner(i, frame);
        }
        for (var i = 0; i < DemoJobs.Count; i++)
        {
            var selectionMoved = view.Selected != _previous.Selected && (i == view.Selected || i == _previous.Selected);
            if (selectionMoved || DemoJobs.Get(i, frame) != DemoJobs.Get(i, before) || JobGlyph(i, frame) != JobGlyph(i, before))
                DrawJob(i, view);
        }
        if (view.Paused != _previous.Paused)
            DrawButtons(view);
        if (added > 0)
            ScrollLog(added);
    }

    private void DrawHeader(DemoView view)
    {
        MoveTo(1, 1).Append(HeaderStyle).Append(' ', DemoLayout.MinWidth);
        MoveTo(1, 2).Append("MiniPty TUI demo");
        DrawElapsed(view.Frame);
        DrawSize(view);
    }

    private void DrawElapsed(long frame)
    {
        var seconds = frame / FramesPerSecond;
        MoveTo(1, 38).Append(HeaderStyle).Append("Elapsed ");
        AppendTwoDigits((int)(seconds / 60 % 100)).Append(':');
        AppendTwoDigits((int)(seconds % 60)).Append("\u001b[0m");
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

    /// <summary>Static frames of rows 3–19. Every cell is written, so a redraw also erases a closed dialog's shadow.</summary>
    private void DrawPanels()
    {
        MoveTo(3, 1).Append("\u001b[0m┌ Spinners ").Append('─', 10).Append("┐ ┌ Jobs ").Append('─', 41).Append('┐');
        for (var i = 0; i < DemoSpinners.Count; i++)
        {
            var label = DemoSpinners.Label(i);
            MoveTo(4 + i, 1).Append("│     ").Append(label).Append(' ', 14 - label.Length).Append(" │ ");
        }
        MoveTo(9, 1).Append('│').Append(' ', 20).Append("│ ");
        MoveTo(10, 1).Append('└').Append('─', 20).Append("┘ ");
        foreach (var row in (ReadOnlySpan<int>)[7, 9])
            MoveTo(row, 24).Append('│').Append(' ', 47).Append('│');
        MoveTo(10, 24).Append('└').Append('─', 47).Append('┘');
        MoveTo(11, 1).Append("┌ Log ").Append('─', 65).Append('┐');
        MoveTo(19, 1).Append('└').Append('─', 70).Append('┘');
    }

    private void DrawSpinner(int spinner, long frame)
    {
        var glyph = DemoSpinners.Glyph(spinner, frame);
        MoveTo(4 + spinner, 3).Append(DemoSpinners.Color(spinner)).Append(glyph).Append(' ', 3 - glyph.Length).Append("\u001b[0m");
    }

    /// <summary>Columns 24–72: marker, glyph, name, 22-cell bar in eighths, percent, state.</summary>
    private void DrawJob(int job, DemoView view)
    {
        var state = DemoJobs.Get(job, view.Frame);
        var color = StateColor(state.Status);
        var name = DemoJobs.Name(job);
        MoveTo(DemoLayout.FirstJobRow + job, 24).Append("\u001b[0m│").Append(job == view.Selected ? "\u001b[100m>" : " ");
        _frame.Append(color).Append(JobGlyph(job, view.Frame)).Append("\u001b[39m ").Append(name).Append(' ', 8 - name.Length);

        var eighths = state.Progress * 22 * 8 / 100;
        var full = eighths / 8;
        var partial = eighths % 8;
        _frame.Append(color).Append('█', full);
        if (partial > 0)
            _frame.Append(Eighths[partial - 1]);
        _frame.Append("\u001b[90m").Append('.', 22 - full - (partial > 0 ? 1 : 0)).Append("\u001b[39m ");

        _frame.Append(' ', 3 - Digits(state.Progress)).Append(state.Progress).Append("% ");
        _frame.Append(color).Append(state.Status switch
        {
            JobStatus.Queued => "queued ",
            JobStatus.Running => "running",
            JobStatus.Done => "done   ",
            _ => "failed ",
        }).Append("\u001b[39m \u001b[0m│");
    }

    private void DrawButtons(DemoView view)
    {
        MoveTo(DemoLayout.ButtonRow, 24).Append("\u001b[0m│ \u001b[30;42m").Append(view.Paused ? "[ Resume ]" : "[ Pause  ]");
        _frame.Append("\u001b[0m  \u001b[30;46m[ Details ]\u001b[0m").Append(' ', 23).Append('│');
    }

    private void DrawStatus(DemoView view) =>
        MoveTo(DemoLayout.StatusRow, 1).Append("\u001b[0mSelected: ").Append(view.Selected + 1).Append("/3   Paused: ")
            .Append(view.Paused ? "yes" : "no ");

    private void DrawMouse(int row, int column, DemoView view) =>
        MoveTo(row, column).Append("\u001b[0mMouse: ").Append(view.MouseColumn).Append(',').Append(view.MouseRow).Append("\u001b[K");

    /// <summary>Scrolls the log region up and writes only the new lines at its bottom.</summary>
    private void ScrollLog(int added)
    {
        // DECSTBM homes the cursor; SU moves the region's lines, borders included, without touching other rows.
        _frame.Append("\u001b[0m\u001b[").Append(DemoLayout.LogTop).Append(';').Append(DemoLayout.LogBottom).Append('r');
        _frame.Append("\u001b[").Append(added).Append('S');
        for (var j = 0; j < added; j++)
            DrawLogLine(DemoLayout.LogBottom - added + 1 + j, LogEntryAt(_logCount - added + j), blank: false);
        _frame.Append("\u001b[r");
    }

    private void DrawLogLine(int row, in DemoLogEntry entry, bool blank)
    {
        MoveTo(row, 1).Append("\u001b[0m│ ");
        var width = 0;
        if (!blank)
        {
            var seconds = entry.Frame / FramesPerSecond;
            _frame.Append("\u001b[90m");
            AppendTwoDigits((int)(seconds / 60 % 100)).Append(':');
            AppendTwoDigits((int)(seconds % 60)).Append("\u001b[39m  ");
            var name = DemoJobs.Name(entry.Job);
            _frame.Append("\u001b[36m").Append(name).Append("\u001b[39m").Append(' ', 8 - name.Length);
            width = 15;
            switch (entry.Kind)
            {
                case DemoLogKind.Started:
                    _frame.Append("started (run #").Append(entry.Value).Append(')');
                    width += 15 + Digits(entry.Value);
                    break;
                case DemoLogKind.Half:
                    _frame.Append("50%");
                    width += 3;
                    break;
                case DemoLogKind.Done:
                    _frame.Append("\u001b[32mdone\u001b[39m");
                    width += 4;
                    break;
                default:
                    _frame.Append("\u001b[31mfailed at ").Append(entry.Value).Append("%, retrying\u001b[39m");
                    width += 21 + Digits(entry.Value);
                    break;
            }
        }
        _frame.Append(' ', 68 - width).Append(" │");
    }

    /// <summary>A modal box with a shadow over rows 6–14, for the selected job. The clock is stopped while it shows.</summary>
    private void DrawDialog(DemoView view)
    {
        var state = DemoJobs.Get(view.Selected, view.Frame);
        var name = DemoJobs.Name(view.Selected);
        MoveTo(6, 15).Append(DialogStyle).Append("┌ ").Append(name).Append(' ').Append('─', 38 - name.Length).Append('┐');
        DialogLine(7).Append("State:    ").Append(state.Status switch
        {
            JobStatus.Queued => "queued ",
            JobStatus.Running => "running",
            JobStatus.Done => "done   ",
            _ => "failed ",
        }).Append(' ', 22).Append('│');
        DialogLine(8).Append("Progress: ").Append(state.Progress).Append('%').Append(' ', 28 - Digits(state.Progress)).Append('│');
        DialogLine(9).Append("Run:      #").Append(state.Run).Append(' ', 28 - Digits(state.Run)).Append('│');
        DialogLine(10).Append("The clock stops while this is open.").Append(' ', 4).Append('│');
        DialogLine(11).Append(' ', 39).Append('│');
        MoveTo(DemoLayout.CloseRow, 15).Append(DialogStyle).Append('│').Append(' ', 15)
            .Append("\u001b[30;47m[ Close ]").Append(DialogStyle).Append(' ', 16).Append('│');
        MoveTo(13, 15).Append(DialogStyle).Append('└').Append('─', 40).Append('┘');
        _frame.Append("\u001b[0;100m");
        for (var row = 7; row <= 13; row++)
            MoveTo(row, 57).Append(' ');
        MoveTo(14, 16).Append(' ', 42).Append("\u001b[0m");
    }

    private StringBuilder DialogLine(int row) => MoveTo(row, 15).Append(DialogStyle).Append("│ ");

    private int CollectLog(long frame)
    {
        if (frame <= _loggedFrame)
            return 0;

        var added = 0;
        for (var f = Math.Max(_loggedFrame + 1, frame - MaxCatchUpFrames); f <= frame; f++)
        {
            for (var job = 0; job < DemoJobs.Count; job++)
            {
                if (!DemoJobs.TryGetEvent(job, f, out var entry))
                    continue;
                _log[_logNext] = entry;
                _logNext = (_logNext + 1) % DemoLayout.LogLines;
                _logCount = Math.Min(_logCount + 1, DemoLayout.LogLines);
                added++;
            }
        }
        _loggedFrame = frame;
        return Math.Min(added, DemoLayout.LogLines);
    }

    /// <summary>The <paramref name="index"/>-th kept entry, oldest first.</summary>
    private DemoLogEntry LogEntryAt(int index) =>
        _log[(_logNext - _logCount + index + 2 * DemoLayout.LogLines) % DemoLayout.LogLines];

    private static string JobGlyph(int job, long frame)
    {
        var state = DemoJobs.Get(job, frame);
        return state.Status switch
        {
            JobStatus.Running => DemoSpinners.Glyph(0, frame + job * 3),
            JobStatus.Done => "✓",
            JobStatus.Failed => "✗",
            _ => ".",
        };
    }

    private static string StateColor(JobStatus status) => status switch
    {
        JobStatus.Running => "\u001b[33m",
        JobStatus.Done => "\u001b[32m",
        JobStatus.Failed => "\u001b[31m",
        _ => "\u001b[90m",
    };

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

    private static int Digits(int value) => value < 10 ? 1 : value < 100 ? 2 : value < 1000 ? 3 : value < 10000 ? 4 : 5;

    private StringBuilder AppendTwoDigits(int value) => _frame.Append((char)('0' + value / 10)).Append((char)('0' + value % 10));

    private StringBuilder MoveTo(int row, int column) => _frame.Append("\u001b[").Append(row).Append(';').Append(column).Append('H');
}
