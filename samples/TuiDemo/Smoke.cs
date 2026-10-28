using System.Text;
using MiniPty;
using TerminalEmulator;

/// <summary>
/// Exercises a real PTY without a human terminal. A second pass runs the interactive host inside an outer
/// PTY, so MiniPty.Console's keyboard forwarding, cursor-query response, resize sync, and cleanup run too.
/// The sample emulator checks rendered cells: Windows ConPTY can rewrite and split the child's VT output.
/// Animated cells are checked to change within a timeout rather than at fixed frames: the point is that a steady
/// stream of small writes keeps reaching the screen through each path, and slow CI runners must not fail it.
/// </summary>
internal static class Smoke
{
    internal static async Task<int> RunAsync()
    {
        try
        {
            await RunScenarioAsync(throughHost: false, quit: "q");
            await RunScenarioAsync(throughHost: true, quit: "\u0003");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"TUI smoke failed: {exception}");
            return 1;
        }
    }

    private static async Task RunScenarioAsync(bool throughHost, string quit)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var session = Pty.Start(DemoHost.StartInfo(child: !throughHost) with { KillProcessTree = true });
        var screen = new ScreenBuffer(80, 24);
        var parser = new VtParser(screen);
        var gate = new object();
        // The outer PTY acts as the user's VT terminal, including answering inherited-cursor queries.
        screen.Response += response => session.Input.Write(Encoding.UTF8.GetBytes(response));
        var output = ReadAsync();
        try
        {
            await ExpectAsync("Selected: 1/3   Paused: no", alternate: true);
            await ExpectAsync("Unicode: 日本語 / café", alternate: true);
            await ExpectAsync("Spinners", alternate: true);
            lock (gate)
            {
                if (screen.CursorVisible || screen.ViewLine(0).Cells[0].Attributes.Background.Kind == ColorKind.Default)
                    throw new InvalidOperationException("Expected a hidden cursor and a colored header bar.");
                var first = screen.ViewLine(1).Cells[32].Attributes.Background;
                var last = screen.ViewLine(1).Cells[71].Attributes.Background;
                if (first.Kind == ColorKind.Default || last.Kind == ColorKind.Default || first == last)
                    throw new InvalidOperationException("Expected a color gradient across row 2.");
            }

            // Animation streams on its own: a spinner, a job's percent, and the log change without any input.
            var spinner = await ChangesAsync(() => Cell(3, 2), "braille spinner");
            await ChangesAsync(() => Slice(3, 58, 4), "Build percent");
            var newest = await NonBlankAsync(() => Slice(DemoLayout.LogBottom - 1, 2, 30), "log line");
            await WaitUntilAsync(() => Slice(DemoLayout.LogBottom - 2, 2, 30) == newest, "the log to scroll its newest line up");

            await session.WriteInputAsync("\u001b[B"u8.ToArray(), timeout.Token);
            await ExpectAsync("Selected: 2/3   Paused: no", alternate: true);
            await session.WriteInputAsync(" "u8.ToArray(), timeout.Token);
            await ExpectAsync("Selected: 2/3   Paused: yes", alternate: true);
            await ExpectFrozenAsync();
            await session.WriteInputAsync(" "u8.ToArray(), timeout.Token);
            await ExpectAsync("Paused: no", alternate: true);
            await ChangesAsync(() => Cell(3, 2), "braille spinner after resume");
            await session.WriteInputAsync("\u001b[A"u8.ToArray(), timeout.Token);
            await ExpectAsync("Selected: 1/3", alternate: true);

            await MouseAsync(0, 40, DemoLayout.FirstJobRow + 2);
            await ExpectAsync("Selected: 3/3", alternate: true);
            lock (gate)
            {
                if (screen.ViewLine(DemoLayout.FirstJobRow + 1).Cells[30].Attributes.Background.Kind == ColorKind.Default
                    || screen.ViewLine(DemoLayout.FirstJobRow - 1).Cells[30].Attributes.Background.Kind != ColorKind.Default)
                    throw new InvalidOperationException("Mouse selection did not move the highlighted job row.");
            }
            await MouseAsync(0, 40, DemoLayout.FirstJobRow + 2, release: true);
            await MouseAsync(0, DemoLayout.PauseLeft + 2, DemoLayout.ButtonRow);
            await ExpectAsync("Selected: 3/3   Paused: yes", alternate: true);
            await ExpectAsync("[ Resume ]", alternate: true);
            await MouseAsync(0, DemoLayout.PauseLeft + 2, DemoLayout.ButtonRow, release: true);
            await MouseAsync(0, DemoLayout.PauseLeft + 2, DemoLayout.ButtonRow);
            await ExpectAsync("Selected: 3/3   Paused: no", alternate: true);
            await MouseAsync(0, DemoLayout.PauseLeft + 2, DemoLayout.ButtonRow, release: true);
            await MouseAsync(0, 23, 5); // The gap between the panels must not select anything.
            await ExpectAsync("Mouse: 23,5", alternate: true);
            await ExpectAsync("Selected: 3/3", alternate: true);
            await MouseAsync(0, 23, 5, release: true);
            await MouseAsync(64, 40, 5);
            await ExpectAsync("Selected: 2/3", alternate: true);

            // The dialog is modal; closing it redraws the panels it covered.
            await session.WriteInputAsync("\r"u8.ToArray(), timeout.Token);
            await ExpectAsync("[ Close ]", alternate: true);
            await ExpectAsync("┌ Test ", alternate: true);
            await session.WriteInputAsync("\r"u8.ToArray(), timeout.Token);
            await WaitUntilAsync(() => !ScreenContains("[ Close ]"), "the dialog to close by Enter");
            await MouseAsync(0, DemoLayout.DetailsLeft + 2, DemoLayout.ButtonRow);
            await ExpectAsync("[ Close ]", alternate: true);
            await MouseAsync(0, DemoLayout.DetailsLeft + 2, DemoLayout.ButtonRow, release: true);
            await MouseAsync(0, DemoLayout.CloseLeft + 3, DemoLayout.CloseRow);
            await WaitUntilAsync(() => !ScreenContains("[ Close ]"), "the dialog to close by its button");
            await MouseAsync(0, DemoLayout.CloseLeft + 3, DemoLayout.CloseRow, release: true);
            // Column 24 of rows 6 and 10 (the Jobs panel's left border) lay under the dialog.
            await WaitUntilAsync(() => Slice(5, 23, 1) == "│" && Slice(9, 23, 1) == "└",
                "the panels under the dialog to be restored");

            Resize(100, 30);
            await ExpectAsync("Size: 100 x 30", alternate: true);
            await ExpectAsync("Unicode: 日本語 / café", alternate: true);
            await ExpectAsync("Spinners", alternate: true);
            Resize(40, 10);
            await ExpectAsync("Resize to at least", alternate: true);
            await MouseAsync(0, 2, 7); // Hidden controls are not clickable in compact layout.
            await ExpectAsync("Mouse: 2,7", alternate: true);
            await MouseAsync(0, 2, 7, release: true);
            Resize(80, 24);
            await ExpectAsync("Selected: 2/3", alternate: true);
            await ExpectAsync("Spinners", alternate: true);

            await session.WriteInputAsync(Encoding.UTF8.GetBytes(quit), timeout.Token);
            var status = await session.WaitForExitAsync(timeout.Token);
            await output;
            await ExpectAsync("TUI exited cleanly.", alternate: false);
            lock (gate)
            {
                if (status.ExitCode != 0 || !screen.CursorVisible || screen.Mouse != MouseTracking.None || screen.MouseSgr)
                    throw new InvalidOperationException($"Exit={status.ExitCode}, cursor visible={screen.CursorVisible}, mouse={screen.Mouse}/{screen.MouseSgr}.");
            }
            Console.WriteLine($"PASS ({(throughHost ? "console host + nested PTY, Ctrl+C" : "direct PTY, Q")}): Unicode, colors, animated spinners/progress/log, pause, keys, mouse click/wheel, dialog, resize, screen/cursor/mouse restoration, exit=0 (spinner seen: {spinner})");
        }
        finally
        {
            timeout.Cancel();
            // Observe the only output reader before disposing its session, including failure/timeout paths.
            try { await output; }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        }

        async Task ReadAsync()
        {
            await foreach (var chunk in session.ReadOutputAsync(timeout.Token))
            {
                lock (gate)
                    parser.Feed(chunk.Data.Span);
            }
        }

        void Resize(int columns, int rows)
        {
            lock (gate)
            {
                screen.Resize(columns, rows);
                session.Resize(new PtySize(columns, rows));
            }
        }

        string Cell(int row, int column)
        {
            lock (gate)
                return row < screen.Rows && column < screen.Columns ? screen.ViewLine(row).Cells[column].Text ?? " " : string.Empty;
        }

        /// <summary>The text of <paramref name="length"/> cells from a 0-based position.</summary>
        string Slice(int row, int column, int length)
        {
            lock (gate)
            {
                if (row >= screen.Rows)
                    return string.Empty;
                var cells = screen.ViewLine(row).Cells;
                var text = new StringBuilder(length);
                for (var i = column; i < column + length && i < cells.Length; i++)
                {
                    if (!cells[i].IsContinuation)
                        text.Append(cells[i].Text ?? " ");
                }
                return text.ToString();
            }
        }

        bool ScreenContains(string text)
        {
            lock (gate)
            {
                for (var row = 0; row < screen.Rows; row++)
                {
                    if (screen.ViewLine(row).ToText().Contains(text, StringComparison.Ordinal))
                        return true;
                }
                return false;
            }
        }

        async Task<string> ChangesAsync(Func<string> probe, string what)
        {
            var before = probe();
            await WaitUntilAsync(() => probe() != before, what + " to change on its own");
            return before;
        }

        async Task<string> NonBlankAsync(Func<string> probe, string what)
        {
            await WaitUntilAsync(() => probe().Trim().Length > 0, what + " to appear");
            return probe();
        }

        /// <summary>While paused the TUI must emit nothing that changes the animated cells.</summary>
        async Task ExpectFrozenAsync()
        {
            // Let output written before the pause took effect reach the screen first.
            await Task.Delay(300, timeout.Token);
            var spinnerBefore = Cell(3, 2);
            var percentBefore = Slice(3, 58, 4);
            await Task.Delay(600, timeout.Token);
            if (Cell(3, 2) != spinnerBefore || Slice(3, 58, 4) != percentBefore)
                throw new InvalidOperationException("The animation kept running while paused.");
        }

        async Task MouseAsync(int button, int column, int row, bool release = false)
        {
            string report;
            lock (gate)
            {
                report = MouseReport.Encode(screen.Mouse, screen.MouseSgr, button, row - 1, column - 1,
                    release, motion: false, shift: false, alt: false, ctrl: false)
                    ?? throw new InvalidOperationException("The TUI did not request mouse reporting.");
                if (!screen.MouseSgr)
                    throw new InvalidOperationException("Expected SGR mouse coordinates.");
            }
            await session.WriteInputAsync(Encoding.UTF8.GetBytes(report), timeout.Token);
        }

        Task ExpectAsync(string expected, bool alternate) =>
            WaitUntilAsync(() =>
            {
                lock (gate)
                    return screen.IsAlternateScreen == alternate && ScreenContains(expected);
            }, $"screen to contain '{expected}' (alternate={alternate})");

        async Task WaitUntilAsync(Func<bool> condition, string what)
        {
            while (!condition())
            {
                if (output.IsCompleted)
                {
                    await output;
                    throw new InvalidOperationException($"Output ended before {what}.");
                }
                try { await Task.Delay(20, timeout.Token); }
                catch (OperationCanceledException)
                {
                    var dump = new StringBuilder();
                    lock (gate)
                    {
                        for (var row = 0; row < screen.Rows; row++)
                            dump.AppendLine(screen.ViewLine(row).ToText());
                    }
                    throw new TimeoutException($"Timed out waiting for {what}.\n{dump}");
                }
            }
        }
    }
}
