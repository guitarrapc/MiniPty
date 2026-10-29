#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property AllowUnsafeBlocks=true
#:property PublishAot=true
#:project ../../src/MiniPty/MiniPty.csproj
#:project ../../src/MiniPty.Console/MiniPty.Console.csproj

// ConsoleAttach: run a shell inside a PTY and hand it this terminal, with MiniPty.Console.
//
//   dotnet samples/SingleFiles/ConsoleAttach.cs     (from an interactive terminal; type `exit` to end)
//
// PtyConsoleInput.Attach forwards the keyboard (and mouse reports and replies to terminal queries) to the PTY and keeps
// the PTY at this window's size. Copying the PTY's output to stdout stays the application's job. For full-screen
// programs, see samples/TuiDemo.

using System.Runtime.InteropServices;
using System.Text;
using MiniPty;
using MiniPty.Console;

if (!Pty.IsSupported)
{
    Console.Error.WriteLine("PTY is not supported on this operating system.");
    return 1;
}

if (Console.IsInputRedirected || Console.IsOutputRedirected)
{
    Console.Error.WriteLine("ConsoleAttach requires an interactive terminal (stdin and stdout must be a TTY).");
    Console.Error.WriteLine("Run from a real console, for example:");
    Console.Error.WriteLine("  dotnet samples/SingleFiles/ConsoleAttach.cs");
    Console.Error.WriteLine("On Windows, use Windows Terminal, PowerShell, or cmd — not Git Bash or MSYS.");
    return 0;
}

// PTY output is UTF-8. A Windows console with a legacy output code page would misdecode it (Japanese text, for
// example), so write UTF-8 while the shell runs and restore the console afterwards. MiniPty.Console sets the input side
// itself while attached; the output side belongs to whoever writes the output.
var outputEncoding = Console.OutputEncoding;
try
{
    Console.OutputEncoding = Encoding.UTF8;
    return RunInteractiveShell();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
    return 1;
}
finally
{
    Console.OutputEncoding = outputEncoding;
}

static int RunInteractiveShell()
{
    using var session = Pty.Start(CreateShellStartInfo());

    // Write status before Attach. Once attached, Unix stdout is raw (OPOST off: \n no longer returns to the start of the
    // line), and the shell's own output starts arriving.
    Console.Error.WriteLine($"Starting PTY shell (pid {session.ProcessId})...");
    Console.Error.WriteLine("Keyboard input is forwarded to the child; PTY output is written to this terminal.");
    Console.Error.WriteLine("Exit the shell (for example type exit) to end the sample.");
    Console.Error.WriteLine("Attaching host terminal...");

    int exitCode;
    using (var attachCts = new CancellationTokenSource())
    using (var consoleInput = PtyConsoleInput.Attach(session))
    {
        // Attach before pumping output. On Windows the session inherits the cursor, so ConPTY's first output is a
        // cursor position query, and only a host with VT processing on answers it; Attach turns that on. A query
        // written before Attach would never be answered and the child would wait forever. ConPTY holds the child's
        // output until the answer arrives, so nothing can fill the output pipe while attaching.
        var pumpTask = PumpOutputToHostAsync(session);

        var exitTask = session.WaitForExitAsync(attachCts.Token);
        _ = exitTask.ContinueWith(
            static (_, state) => ((CancellationTokenSource)state!).Cancel(),
            attachCts,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        // Keys are forwarded in the background from Attach on; this only blocks until the child exits.
        consoleInput.PumpInputUntil(attachCts.Token);

        exitCode = exitTask.IsCompletedSuccessfully ? exitTask.Result.ExitCode : session.ExitCode ?? -1;
        // Drain the shell's last output before detaching, so it reaches the terminal before its modes are restored.
        pumpTask.GetAwaiter().GetResult();
    }

    // PtyConsoleInput disposed: host termios restored. Avoid stderr/stdout interleaving while attached.
    if (!OperatingSystem.IsWindows())
        ResetUnixHostCursorColumn();

    Console.Error.WriteLine($"Shell exited with code {exitCode}.");
    return 0;
}

static void ResetUnixHostCursorColumn()
{
#pragma warning disable CA2000 // Host standard output is process-owned and must not be disposed.
    var stdout = Console.OpenStandardOutput();
#pragma warning restore CA2000

    // Raw host stdout does not map LF to CR+LF; reset the column before the parent shell redraws.
    stdout.Write("\r\n"u8);
    stdout.Flush();
}

static async Task PumpOutputToHostAsync(PtySession session)
{
#pragma warning disable CA2000 // Host standard output is process-owned and must not be disposed.
    var stdout = Console.OpenStandardOutput();
#pragma warning restore CA2000

    // The session's only output reader. The bytes go to the terminal unchanged: it interprets the escape sequences and
    // reassembles a UTF-8 character split across two chunks.
    await foreach (var chunk in session.ReadOutputAsync().ConfigureAwait(false))
    {
        await stdout.WriteAsync(chunk.Data, CancellationToken.None).ConfigureAwait(false);
        await stdout.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }
}

static PtyStartInfo CreateShellStartInfo()
{
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        if (!TryResolveWindowsPowerShell(out var powershell))
            throw new InvalidOperationException("Windows PowerShell is required for ConsoleAttach on Windows.");

        return new PtyStartInfo
        {
            FileName = powershell,
            Arguments = ["-NoLogo", "-NoProfile"],
            Size = new PtySize(80, 24), // replaced by this window's size when MiniPty.Console attaches
            // Keep the host's existing screen instead of letting ConPTY clear it. The host terminal answers
            // ConPTY's cursor position query, and the attached input pump forwards that answer to the PTY.
            InheritCursor = true,
        };
    }

    if (File.Exists("/bin/bash"))
    {
        return new PtyStartInfo
        {
            FileName = "/bin/bash",
            Arguments = ["-i"],
            Size = new PtySize(80, 24),
        };
    }

    return new PtyStartInfo
    {
        FileName = "/bin/sh",
        Arguments = ["-i"],
        Size = new PtySize(80, 24),
    };
}

static bool TryResolveWindowsPowerShell(out string path)
{
    path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell",
        "v1.0",
        "powershell.exe");
    return File.Exists(path);
}
