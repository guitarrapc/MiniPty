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
            // The layout stretches to the PTY's 80 × 24; positions come from the same geometry the renderer uses.
            var g = new DemoGeometry(80, 24);
            await ExpectAsync("Selected: 1/3   Paused: no", alternate: true);
            await ExpectAsync("Unicode: 日本語 / café", alternate: true);
            await ExpectAsync("Spinners", alternate: true);
            await ExpectAsync("Q/Ctrl+C: quit", alternate: true);
            lock (gate)
            {
                if (screen.CursorVisible || screen.ViewLine(0).Cells[0].Attributes.Background.Kind == ColorKind.Default)
                    throw new InvalidOperationException("Expected a hidden cursor and a colored header bar.");
                var first = screen.ViewLine(1).Cells[32].Attributes.Background;
                var last = screen.ViewLine(1).Cells[g.Width - 1].Attributes.Background;
                if (first.Kind == ColorKind.Default || last.Kind == ColorKind.Default || first == last)
                    throw new InvalidOperationException("Expected a color gradient across row 2 to the right edge.");
                if (screen.ViewLine(3).Cells[g.Width - 1].Text != "│" || screen.ViewLine(g.PageBottom - 1).Cells[g.Width - 1].Text != "┘")
                    throw new InvalidOperationException("Expected the job rows and the log to reach the right edge and the page bottom.");
            }

            // Animation streams on its own: a spinner, a job's percent, and the log change without any input.
            var percent = g.BarCells + 36;
            var spinner = await ChangesAsync(() => Cell(3, 2), "braille spinner");
            await ChangesAsync(() => Slice(3, percent, 4), "Build percent");
            var newest = await NonBlankAsync(() => Slice(g.LogBottom - 1, 2, 30), "log line");
            await WaitUntilAsync(() => Slice(g.LogBottom - 2, 2, 30) == newest, "the log to scroll its newest line up");

            await SendAsync("\u001b[B");
            await ExpectAsync("Selected: 2/3   Paused: no", alternate: true);
            await SendAsync(" ");
            await ExpectAsync("Selected: 2/3   Paused: yes", alternate: true);
            await ExpectFrozenAsync(() => Cell(3, 2), () => Slice(3, percent, 4));
            await SendAsync(" ");
            await ExpectAsync("Paused: no", alternate: true);
            await ChangesAsync(() => Cell(3, 2), "braille spinner after resume");
            await SendAsync("\u001b[A");
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

            // The dialog is modal and centered; closing it redraws the panels it covered.
            await SendAsync("\r");
            await ExpectAsync("[ Close ]", alternate: true);
            await ExpectAsync("┌ Test ", alternate: true);
            await SendAsync("\r");
            await WaitUntilAsync(() => !ScreenContains("[ Close ]"), "the dialog to close by Enter");
            await MouseAsync(0, DemoLayout.DetailsLeft + 2, DemoLayout.ButtonRow);
            await ExpectAsync("[ Close ]", alternate: true);
            await MouseAsync(0, DemoLayout.DetailsLeft + 2, DemoLayout.ButtonRow, release: true);
            await MouseAsync(0, g.CloseLeft + 3, DemoLayout.CloseRow);
            await WaitUntilAsync(() => !ScreenContains("[ Close ]"), "the dialog to close by its button");
            await MouseAsync(0, g.CloseLeft + 3, DemoLayout.CloseRow, release: true);
            // Column 24 of rows 6 and 10 (the Jobs panel's left border) lay under the dialog.
            await WaitUntilAsync(() => Slice(5, 23, 1) == "│" && Slice(9, 23, 1) == "└",
                "the panels under the dialog to be restored");

            // Files: switch by key, then move, scroll, and fold with the keys terminals encode differently.
            await SendAsync("2");
            await ExpectAsync("┌ Files ", alternate: true);
            await WaitUntilAsync(() => !ScreenContains("Spinners"), "the dashboard to be replaced");
            await ExpectAsync("Item: 1/20 ", alternate: true);
            await ExpectAsync("press / to filter", alternate: true);
            await SendAsync("\u001b[B\u001b[B");
            await ExpectAsync("Item: 3/20 ", alternate: true);
            await SendAsync("\u001b[F");
            await ExpectAsync("Item: 20/20 ", alternate: true);
            await ExpectAsync("└─ global.json", alternate: true);
            // 日本語メモ.txt is 14 cells wide; its size must still end in the same column as its neighbors'.
            var wideRow = DemoLayout.FirstFileRow - 1 + 16 - (20 - g.FileRows);
            await WaitUntilAsync(() => Slice(wideRow, g.TreeWidth - 8, 6) == "  412B" && Slice(wideRow + 1, g.TreeWidth - 8, 6) == "   96B",
                "the wide-character row's size column to line up");
            await SendAsync("\u001b[H");
            await ExpectAsync("Item: 1/20 ", alternate: true);
            await SendAsync("\u001b[6~");
            await ExpectAsync($"Item: {1 + g.FileRows}/20 ", alternate: true);
            await SendAsync("\u001b[5~");
            await ExpectAsync("Item: 1/20 ", alternate: true);
            await SendAsync("\u001b[B\r");
            await ExpectAsync("Item: 2/22 ", alternate: true);
            await ExpectAsync("├─ + workflows/", alternate: true);
            await SendAsync("\u001b[D");
            await ExpectAsync("Item: 2/20 ", alternate: true);
            await SendAsync("\u001b[C");
            await ExpectAsync("Item: 2/22 ", alternate: true);
            await MouseAsync(0, 10, DemoLayout.FirstFileRow + 1); // A click on the selected folder folds it.
            await ExpectAsync("Item: 2/20 ", alternate: true);
            await MouseAsync(0, 10, DemoLayout.FirstFileRow + 1, release: true);
            await MouseAsync(0, 10, DemoLayout.FirstFileRow);
            await ExpectAsync("Item: 1/20 ", alternate: true);
            await MouseAsync(0, 10, DemoLayout.FirstFileRow, release: true);
            await MouseAsync(65, 10, DemoLayout.FirstFileRow + 2);
            await ExpectAsync("Item: 2/20 ", alternate: true);

            // The filter box takes text: Japanese arrives as UTF-8 through each path, including MiniPty.Console's
            // input on Windows; Q is a letter while typing, so only Ctrl+C would quit.
            await SendAsync("/");
            await WaitUntilAsync(() => CursorVisible(), "the cursor to show in the filter box");
            await ExpectAsync("Enter: stop typing", alternate: true);
            await SendAsync("日本");
            await ExpectAsync("Item: 2/2 ", alternate: true);
            await ExpectAsync("├─ 日本語メモ.txt", alternate: true);
            await WaitUntilAsync(() => CursorAt(DemoLayout.FilterRow - 1, DemoLayout.FilterLeft - 1 + 4), "the cursor to sit after the typed text");
            await SendAsync("q");
            await ExpectAsync("Item: 0/0 ", alternate: true);
            await ExpectAsync("No item matches the filter.", alternate: true);
            // An empty list moved the cursor to the first row, where it stays as items come back.
            await SendAsync("\u007f");
            await ExpectAsync("Item: 1/2 ", alternate: true);
            await SendAsync("\u0015");
            await ExpectAsync("Item: 1/20 ", alternate: true);
            await SendAsync("demo");
            await ExpectAsync("Item: 1/6 ", alternate: true);
            await ExpectAsync("DemoRenderer.cs", alternate: true);
            await SendAsync("\r");
            await WaitUntilAsync(() => !CursorVisible(), "the cursor to hide after Enter");
            await ExpectAsync("Q/Ctrl+C: quit", alternate: true);
            await ExpectAsync("Item: 1/6 ", alternate: true);
            await MouseAsync(0, 10, DemoLayout.FilterRow); // A click on the box types again.
            await WaitUntilAsync(() => CursorVisible(), "the cursor to show after a click on the box");
            await MouseAsync(0, 10, DemoLayout.FilterRow, release: true);
            await SendAsync("\u0015\r");
            await ExpectAsync("Item: 1/20 ", alternate: true);
            await WaitUntilAsync(() => !CursorVisible(), "the cursor to hide again");

            // Charts by Tab: the braille plot and a sparkline move on their own and freeze while paused.
            await SendAsync("\t");
            await ExpectAsync("┌ Signals sine mix ", alternate: true);
            await ChangesAsync(() => Slice(4, 7, g.PlotColumns), "braille plot");
            await ChangesAsync(() => Slice(g.ChartsTop, 43, g.SparkLength), "cpu sparkline");
            await SendAsync(" ");
            await ExpectAsync("Paused: yes", alternate: true);
            await ExpectFrozenAsync(() => Slice(4, 7, g.PlotColumns), () => Slice(g.ChartsTop, 43, g.SparkLength));
            await SendAsync(" ");
            await ExpectAsync("Paused: no", alternate: true);

            // Back by Shift+Tab, and to the dashboard by clicking its tab; each page kept its state.
            await SendAsync("\u001b[Z");
            await ExpectAsync("Item: 1/20 ", alternate: true);
            await MouseAsync(0, DemoLayout.TabLeft[DemoLayout.DashboardPage] + 2, DemoLayout.TabRow);
            await ExpectAsync("Spinners", alternate: true);
            await ExpectAsync("Selected: 2/3", alternate: true);
            await MouseAsync(0, DemoLayout.TabLeft[DemoLayout.DashboardPage] + 2, DemoLayout.TabRow, release: true);

            // A larger terminal stretches the layout instead of leaving it in the corner.
            Resize(100, 30);
            var large = new DemoGeometry(100, 30);
            await ExpectAsync("Size: 100 x 30", alternate: true);
            await ExpectAsync("Unicode: 日本語 / café", alternate: true);
            await WaitUntilAsync(() => Slice(3, large.Width - 1, 1) == "│" && Slice(large.PageBottom - 1, 0, 1) == "└"
                && Slice(large.BottomRow - 1, 1, 14) == "Q/Ctrl+C: quit", "the dashboard to fill 100 × 30");
            await SendAsync("3");
            await ExpectAsync("┌ Signals sine mix ", alternate: true);
            await WaitUntilAsync(() => Slice(4, large.Width - 1, 1) == "│" && Slice(large.ChartsTop - 1, 0, 1) == "┌",
                "the chart to fill 100 × 30");
            await ChangesAsync(() => Slice(4, 7, large.PlotColumns), "the wider braille plot");
            await SendAsync("1");
            await ExpectAsync("Spinners", alternate: true);
            Resize(40, 10);
            await ExpectAsync("Resize to at least", alternate: true);
            await MouseAsync(0, 2, 7); // Hidden controls are not clickable in compact layout.
            await ExpectAsync("Mouse: 2,7", alternate: true);
            await MouseAsync(0, 2, 7, release: true);
            Resize(80, 24);
            await ExpectAsync("Selected: 2/3", alternate: true);
            await ExpectAsync("Spinners", alternate: true);

            await SendAsync(quit);
            var status = await session.WaitForExitAsync(timeout.Token);
            await output;
            await ExpectAsync("TUI exited cleanly.", alternate: false);
            lock (gate)
            {
                if (status.ExitCode != 0 || !screen.CursorVisible || screen.Mouse != MouseTracking.None || screen.MouseSgr)
                    throw new InvalidOperationException($"Exit={status.ExitCode}, cursor visible={screen.CursorVisible}, mouse={screen.Mouse}/{screen.MouseSgr}.");
            }
            Console.WriteLine($"PASS ({(throughHost ? "console host + nested PTY, Ctrl+C" : "direct PTY, Q")}): Unicode, colors, animated spinners/progress/log, pause, keys, mouse click/wheel, dialog, tabs, file tree scroll/fold/wide names, filter box with Japanese input, braille chart/sparklines, stretched layout, resize, screen/cursor/mouse restoration, exit=0 (spinner seen: {spinner})");
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

        bool CursorVisible()
        {
            lock (gate)
                return screen.CursorVisible;
        }

        /// <summary>Whether the terminal cursor shows at a 0-based position.</summary>
        bool CursorAt(int row, int column)
        {
            lock (gate)
                return screen.CursorVisible && screen.CursorRow == row && screen.CursorColumn == column;
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
        async Task ExpectFrozenAsync(Func<string> first, Func<string> second)
        {
            // Let output written before the pause took effect reach the screen first.
            await Task.Delay(300, timeout.Token);
            var firstBefore = first();
            var secondBefore = second();
            await Task.Delay(600, timeout.Token);
            if (first() != firstBefore || second() != secondBefore)
                throw new InvalidOperationException("The animation kept running while paused.");
        }

        Task SendAsync(string text) => session.WriteInputAsync(Encoding.UTF8.GetBytes(text), timeout.Token).AsTask();

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
