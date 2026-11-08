using MiniPty;

/// <summary>One scripted input for the TUI, then the pause after it in milliseconds at normal pace.</summary>
internal readonly record struct AutoplayStep(string Input, int PauseMs);

/// <summary>
/// A fixed tour of the TUI, written into the PTY as a terminal would send a user's keys and clicks: keys as their
/// bytes, and the mouse as SGR reports (the TUI turns on SGR mouse mode, DECSET 1006). The host plays it with
/// <c>--autoplay</c> for unattended demos and recordings; the smoke plays it fast to check that its drags land and that
/// it ends with the TUI quitting.
/// </summary>
/// <remarks>
/// Positions come from the geometry the TUI uses for hit testing, so the clicks land at any size of at least 72 × 22.
/// A smaller terminal gets the compact layout, which ignores clicks; the keys still end the tour, because the filter
/// box stops taking text before the final Q.
/// </remarks>
internal static class DemoAutoplay
{
    private const string Down = "\u001b[B";
    private const string Right = "\u001b[C";

    internal static AutoplayStep[] Script(int width, int height)
    {
        var g = new DemoGeometry(width, height);
        var charts = g.Charts(default);
        var taller = g.Charts(charts.Layout with { BottomRows = charts.BottomRows + 3 });
        var pause = DemoLayout.PauseLeft + 2;
        return
        [
            // Dashboard: spinners, job bars, and the log run on their own.
            new("", 2200),
            .. Click(40, DemoLayout.FirstJobRow + 1, 700),      // select the Test job with the mouse
            new(Down, 600),                                      // and the Deploy job with a key
            new("\r", 1400),                                     // its dialog stops the clock
            .. Click(g.CloseLeft + 3, DemoLayout.CloseRow, 700), // [ Close ]
            .. Click(pause, DemoLayout.ButtonRow, 1200),         // [ Pause ]
            .. Click(pause, DemoLayout.ButtonRow, 600),          // [ Resume ]

            // Files: unfold a folder, preview a file, then filter, in Japanese too.
            new("2", 900),
            new(Down, 200), new(Down, 200), new(Down, 300),      // samples/TerminalEmulator/
            new(Right, 800),
            new(Down, 250), new(Down, 1000),                     // VtParser.cs and its preview
            new("/", 400),
            new("日", 250), new("本", 1400),
            new("\u0015", 300),                                  // Ctrl+U clears the box
            new(".", 150), new("c", 150), new("s", 1200),
            new("\u0015", 300), new("\r", 600),                  // Enter stops typing, so Q quits again

            // Charts: braille plot and sparklines stream; drag both borders of the split.
            new("3", 1500),
            .. Drag(10, charts.Top, 10, charts.Top - 3, 800),
            .. Drag(taller.Gap, taller.Top + 2, taller.Gap + 10, taller.Top + 2, 1800),
            new("q", 0),
        ];
    }

    /// <summary>
    /// Writes each step, then waits its pause times <paramref name="pace"/>. Stops early when <paramref name="exited"/>
    /// completes, because a TUI that quit has nothing left to drive.
    /// </summary>
    internal static async Task PlayAsync(PtySession session, ReadOnlyMemory<AutoplayStep> steps, double pace, Task exited,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < steps.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (exited.IsCompleted)
                return;
            var step = steps.Span[i];
            await session.WriteInputAsync(step.Input, cancellationToken: cancellationToken);
            var pauseMs = (int)(step.PauseMs * pace);
            if (pauseMs > 0)
                await Task.WhenAny(exited, Task.Delay(pauseMs, cancellationToken));
        }
    }

    /// <summary>A left click at a 1-based cell: the press acts, the release follows.</summary>
    private static AutoplayStep[] Click(int column, int row, int pauseMs) =>
        [new(Mouse(0, column, row, 'M'), 120), new(Mouse(0, column, row, 'm'), pauseMs)];

    /// <summary>A left-button drag: press, four motion reports with the button held (code 32), release.</summary>
    private static AutoplayStep[] Drag(int fromColumn, int fromRow, int toColumn, int toRow, int pauseMs)
    {
        const int Moves = 4;
        var steps = new AutoplayStep[Moves + 2];
        steps[0] = new(Mouse(0, fromColumn, fromRow, 'M'), 150);
        for (var i = 1; i <= Moves; i++)
        {
            var column = fromColumn + (toColumn - fromColumn) * i / Moves;
            var row = fromRow + (toRow - fromRow) * i / Moves;
            steps[i] = new(Mouse(32, column, row, 'M'), 120);
        }
        steps[^1] = new(Mouse(0, toColumn, toRow, 'm'), pauseMs);
        return steps;
    }

    private static string Mouse(int code, int column, int row, char final) => $"\u001b[<{code};{column};{row}{final}";
}
