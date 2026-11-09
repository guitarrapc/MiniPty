using MiniPty;
using MiniPty.Console;
using System.Text;

if (args is ["--child"])
    return DemoTui.Run();

if (args is ["--smoke"])
    return await Smoke.RunAsync();

var autoplay = args is ["--autoplay"];
if (args.Length != 0 && !autoplay)
{
    Console.Error.WriteLine("Usage: TuiDemo [--autoplay | --smoke]");
    return 2;
}

// Autoplay reads no keys, so only its output needs a terminal.
if (Console.IsOutputRedirected || (!autoplay && Console.IsInputRedirected))
{
    Console.Error.WriteLine("Run TuiDemo in an interactive terminal, or use --smoke for automated checks.");
    return 2;
}

return await DemoHost.RunAsync(autoplay);

/// <summary>
/// The host only transports bytes. The child owns the TUI and the user's terminal renders it.
/// Keeping both roles in one executable makes the sample runnable under JIT and NativeAOT.
/// </summary>
internal static class DemoHost
{
    internal static PtyStartInfo StartInfo(bool child, bool inheritCursor = false)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path is unavailable.");
        // Also support `dotnet TuiDemo.dll`, without assembly discovery or reflection.
        var isDotnet = string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase);
        string[] arguments = (isDotnet, child) switch
        {
            (true, true) => [Environment.GetCommandLineArgs()[0], "--child"],
            (true, false) => [Environment.GetCommandLineArgs()[0]],
            (false, true) => ["--child"],
            _ => [],
        };
        return new PtyStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            Size = new PtySize(80, 24),
            InheritCursor = inheritCursor,
        };
    }

    internal static async Task<int> RunAsync(bool autoplay)
    {
        // PTY output is UTF-8. Windows console byte writes otherwise decode it using the host's legacy
        // code page, which can corrupt Japanese text even though the PTY transport preserved every byte.
        // Input needs nothing here: MiniPty.Console reads the host console as UTF-8 while attached, and
        // autoplay writes its UTF-8 input itself.
        var outputEncoding = Console.OutputEncoding;
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
            return autoplay ? await RunAutoplayAsync() : await RunAttachedAsync();
        }
        finally
        {
            Console.OutputEncoding = outputEncoding;
        }
    }

    /// <summary>
    /// Plays <see cref="DemoAutoplay"/> instead of forwarding the keyboard: the host writes keys and mouse reports
    /// itself with <c>WriteInputAsync</c>, as a test or a recording does. Without MiniPty.Console nothing answers
    /// ConPTY's inherited-cursor query, so the session starts without <c>InheritCursor</c>, and the PTY keeps the size
    /// the terminal had at start. Ctrl+C ends the tour early and quits the TUI, which then restores the terminal.
    /// </summary>
    private static async Task<int> RunAutoplayAsync()
    {
        var size = new PtySize(Console.WindowWidth, Console.WindowHeight);
        // Attach sets these modes for the interactive host; here the host sets them itself: VT processing, and a line
        // feed that keeps its column, because ConPTY's output moves the cursor down with bare line feeds.
        using var vtOutput = DemoTui.VirtualTerminalOutput.Enable(disableNewlineAutoReturn: true);
        await using var session = Pty.Start(StartInfo(child: true) with { Size = size });
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            var output = CopyOutputAsync(session);
            var exited = session.WaitForExitAsync();
            try
            {
                await DemoAutoplay.PlayAsync(session, DemoAutoplay.Script(size.Columns, size.Rows), pace: 1, exited, stop.Token);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                if (!exited.IsCompleted)
                    await session.WriteInputAsync("\u0003"); // Ctrl+C quits even while the filter box takes text.
            }
            var status = await exited;
            await output; // Drain the final reset/alternate-screen sequences.
            return status.ExitCode;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    private static async Task<int> RunAttachedAsync()
    {
        await using var session = Pty.Start(StartInfo(child: true, inheritCursor: true));
        // Attach first: it enables host VT processing before ConPTY's inherited-cursor query is displayed.
        // Attach starts keyboard forwarding and size synchronization; no Console.ReadKey in the host.
        using (PtyConsoleInput.Attach(session))
        {
            var output = CopyOutputAsync(session);
            var status = await session.WaitForExitAsync();
            await output; // Drain the final reset/alternate-screen sequences before restoring host modes.
            return status.ExitCode;
        }
    }

    private static async Task CopyOutputAsync(PtySession session)
    {
        using var stdout = Console.OpenStandardOutput();
        try
        {
            await foreach (var chunk in session.ReadOutputAsync())
            {
                // Preserve UTF-8 and VT bytes, including sequences split across chunks.
                await stdout.WriteAsync(chunk.Data);
                await stdout.FlushAsync();
            }
        }
        catch
        {
            session.Kill(); // A failed display must not leave the host waiting for an invisible child.
            throw;
        }
    }
}

