using System.Text;
using MiniPty;
using TerminalEmulator;

/// <summary>
/// Exercises a real PTY without a human terminal. A second pass runs the interactive host inside an outer
/// PTY, so MiniPty.Console's keyboard forwarding, cursor-query response, resize sync, and cleanup run too.
/// The sample emulator checks rendered cells: Windows ConPTY can rewrite and split the child's VT output.
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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var session = Pty.Start(DemoHost.StartInfo(child: !throughHost) with { KillProcessTree = true });
        var screen = new ScreenBuffer(80, 24);
        var parser = new VtParser(screen);
        var gate = new object();
        // The outer PTY acts as the user's VT terminal, including answering inherited-cursor queries.
        screen.Response += response => session.Input.Write(Encoding.UTF8.GetBytes(response));
        var output = ReadAsync();
        try
        {
            await ExpectAsync("Selected: 1/3   Toggle: OFF", alternate: true);
            await ExpectAsync("Unicode: 日本語 / café", alternate: true);
            lock (gate)
            {
                if (screen.CursorVisible || screen.ViewLine(0).Cells[0].Attributes.Foreground.Kind == ColorKind.Default)
                    throw new InvalidOperationException("Expected a hidden cursor and a colored heading.");
                if (screen.ViewLine(11).Cells[36].Attributes.Background.Kind == ColorKind.Default)
                    throw new InvalidOperationException("Expected the colored block-art landscape.");
                var sky = screen.ViewLine(11).Cells[36].Attributes.Background;
                if (sky == screen.ViewLine(11).Cells[60].Attributes.Background || sky == screen.ViewLine(14).Cells[36].Attributes.Background)
                    throw new InvalidOperationException("Expected different colors for sky, sun, and hills.");
            }

            await session.WriteInputAsync("\u001b[B"u8.ToArray(), timeout.Token);
            await ExpectAsync("Selected: 2/3   Toggle: OFF", alternate: true);
            await session.WriteInputAsync("\r"u8.ToArray(), timeout.Token);
            await ExpectAsync("Selected: 2/3   Toggle: ON", alternate: true);
            await session.WriteInputAsync("\u001b[A"u8.ToArray(), timeout.Token);
            await ExpectAsync("Selected: 1/3   Toggle: ON", alternate: true);

            await MouseAsync(0, 50, 7);
            await ExpectAsync("Selected: 3/3   Toggle: ON", alternate: true);
            lock (gate)
            {
                if (screen.ViewLine(6).Cells[48].Attributes.Background.Kind == ColorKind.Default
                    || screen.ViewLine(6).Cells[0].Attributes.Background.Kind != ColorKind.Default)
                    throw new InvalidOperationException("Mouse selection did not move the highlighted diagram box.");
            }
            await MouseAsync(0, 50, 7, release: true);
            await MouseAsync(0, 36, 17);
            await ExpectAsync("Selected: 3/3   Toggle: OFF", alternate: true);
            await MouseAsync(0, 36, 17, release: true);
            await MouseAsync(0, 23, 7); // Gap between boxes must not select a node.
            await ExpectAsync("Mouse: 23,7", alternate: true);
            await ExpectAsync("Selected: 3/3   Toggle: OFF", alternate: true);
            await MouseAsync(0, 23, 7, release: true);
            await MouseAsync(64, 50, 7);
            await ExpectAsync("Selected: 2/3   Toggle: OFF", alternate: true);

            Resize(100, 30);
            await ExpectAsync("Size: 100 x 30", alternate: true);
            await ExpectAsync("Unicode: 日本語 / café", alternate: true);
            await ExpectAsync("Terminal resize", alternate: true);
            Resize(40, 10);
            await ExpectAsync("Resize to at least", alternate: true);
            await MouseAsync(0, 2, 7); // Hidden controls are not clickable in compact layout.
            await ExpectAsync("Mouse: 2,7", alternate: true);
            await MouseAsync(0, 2, 7, release: true);
            Resize(80, 24);
            await ExpectAsync("Selected: 2/3   Toggle: OFF", alternate: true);

            await session.WriteInputAsync(Encoding.UTF8.GetBytes(quit), timeout.Token);
            var status = await session.WaitForExitAsync(timeout.Token);
            await output;
            await ExpectAsync("TUI exited cleanly.", alternate: false);
            lock (gate)
            {
                if (status.ExitCode != 0 || !screen.CursorVisible || screen.Mouse != MouseTracking.None || screen.MouseSgr)
                    throw new InvalidOperationException($"Exit={status.ExitCode}, cursor visible={screen.CursorVisible}, mouse={screen.Mouse}/{screen.MouseSgr}.");
            }
            Console.WriteLine($"PASS ({(throughHost ? "console host + nested PTY, Ctrl+C" : "direct PTY, Q")}): Unicode, color diagram/art, keys, mouse click/wheel, resize, screen/cursor/mouse restoration, exit=0");
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

        async Task ExpectAsync(string expected, bool alternate)
        {
            while (true)
            {
                lock (gate)
                {
                    if (screen.IsAlternateScreen == alternate)
                    {
                        for (var row = 0; row < screen.Rows; row++)
                        {
                            if (screen.ViewLine(row).ToText().Contains(expected, StringComparison.Ordinal))
                                return;
                        }
                    }
                }
                if (output.IsCompleted)
                {
                    await output;
                    throw new InvalidOperationException($"Output ended before screen contained '{expected}'.");
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
                    throw new TimeoutException($"Screen did not contain '{expected}' (alternate={alternate}).\n{dump}");
                }
            }
        }
    }
}
