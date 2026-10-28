using MiniPty;
using MiniPty.Console;
using System.Text;

if (args is ["--child"])
    return DemoTui.Run();

if (args is ["--smoke"])
    return await Smoke.RunAsync();

if (args.Length != 0)
{
    Console.Error.WriteLine("Usage: TuiDemo [--smoke]");
    return 2;
}

if (Console.IsInputRedirected || Console.IsOutputRedirected)
{
    Console.Error.WriteLine("Run TuiDemo in an interactive terminal, or use --smoke for automated checks.");
    return 2;
}

return await DemoHost.RunAsync();

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

    internal static async Task<int> RunAsync()
    {
        // PTY output is UTF-8. Windows console byte writes otherwise decode it using the host's legacy
        // code page, which can corrupt Japanese text even though the PTY transport preserved every byte.
        // Input needs nothing here: MiniPty.Console reads the host console as UTF-8 while attached.
        var outputEncoding = Console.OutputEncoding;
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
            return await RunAttachedAsync();
        }
        finally
        {
            Console.OutputEncoding = outputEncoding;
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

