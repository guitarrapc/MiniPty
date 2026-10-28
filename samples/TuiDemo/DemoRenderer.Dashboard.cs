/// <summary>
/// The dashboard page: spinners, jobs, a log, and the Details dialog. A tick rewrites the spinner glyphs that changed,
/// the job rows, and the elapsed time; a new log line scrolls the log region instead of redrawing it.
/// </summary>
internal sealed partial class DemoRenderer
{
    private const int FramesPerSecond = 1000 / DemoJobs.FrameMilliseconds;
    private const int MaxCatchUpFrames = 1000;
    private const string DialogStyle = "\u001b[0;97;44m";
    private const string Eighths = "▏▎▍▌▋▊▉";

    private readonly DemoLogEntry[] _log = new DemoLogEntry[DemoLayout.LogLines];
    private int _logCount;
    private int _logNext;
    private long _loggedFrame = -1;

    private void DrawDashboard(DemoView view)
    {
        DrawPanels();
        DrawElapsed(view.Frame);
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
    }

    private void DrawDashboardChanges(DemoView view, int added)
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

    /// <summary>The elapsed time sits in the Jobs panel's top border (row 3, columns 32–38).</summary>
    private void DrawElapsed(long frame)
    {
        var seconds = frame / FramesPerSecond;
        MoveTo(3, 32).Append("\u001b[0m ");
        AppendTwoDigits((int)(seconds / 60 % 100)).Append(':');
        AppendTwoDigits((int)(seconds % 60)).Append(' ');
    }

    /// <summary>Static frames of rows 3–19. Every cell is written, so a redraw also erases a closed dialog's shadow.</summary>
    private void DrawPanels()
    {
        MoveTo(3, 1).Append("\u001b[0m┌ Spinners ").Append('─', 10).Append("┐ ┌ Jobs ─").Append(' ', 7).Append('─', 33).Append('┐');
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
        _frame.Append(color).Append(StateText(state.Status)).Append("\u001b[39m \u001b[0m│");
    }

    private void DrawButtons(DemoView view)
    {
        MoveTo(DemoLayout.ButtonRow, 24).Append("\u001b[0m│ \u001b[30;42m").Append(view.Paused ? "[ Resume ]" : "[ Pause  ]");
        _frame.Append("\u001b[0m  \u001b[30;46m[ Details ]\u001b[0m").Append(' ', 23).Append('│');
    }

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
        DialogLine(7).Append("State:    ").Append(StateText(state.Status)).Append(' ', 22).Append('│');
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

    private System.Text.StringBuilder DialogLine(int row) => MoveTo(row, 15).Append(DialogStyle).Append("│ ");

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

    private static string StateText(JobStatus status) => status switch
    {
        JobStatus.Queued => "queued ",
        JobStatus.Running => "running",
        JobStatus.Done => "done   ",
        _ => "failed ",
    };

    private static string StateColor(JobStatus status) => status switch
    {
        JobStatus.Running => "\u001b[33m",
        JobStatus.Done => "\u001b[32m",
        JobStatus.Failed => "\u001b[31m",
        _ => "\u001b[90m",
    };
}
