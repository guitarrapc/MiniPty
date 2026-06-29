#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property AllowUnsafeBlocks=true
#:property PublishAot=true
#:project ../src/MiniPty/MiniPty.csproj
#:project ../src/MiniPty.Console/MiniPty.Console.csproj

using System.Runtime.InteropServices;
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
    Console.Error.WriteLine("  dotnet samples/ConsoleAttach.cs");
    Console.Error.WriteLine("On Windows, use Windows Terminal, PowerShell, or cmd — not Git Bash or MSYS.");
    return 0;
}

try
{
    return RunInteractiveShell();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
    return 1;
}

static int RunInteractiveShell()
{
    using var session = Pty.Start(CreateShellStartInfo());

    // Status on stderr before raw host stdout (Unix OPOST off) and before the PTY output pump.
    Console.Error.WriteLine($"Starting PTY shell (pid {session.ProcessId})...");
    Console.Error.WriteLine("Keyboard input is forwarded to the child; PTY output is written to this terminal.");
    Console.Error.WriteLine("Exit the shell (for example type exit) to end the sample.");
    Console.Error.WriteLine("Attaching host terminal...");

    var pumpTask = PumpOutputToHostAsync(session);

    int exitCode;
    using (var attachCts = new CancellationTokenSource())
    using (var consoleInput = PtyConsoleInput.Attach(session))
    {
        var exitTask = session.WaitForExitAsync(attachCts.Token);
        _ = exitTask.ContinueWith(
            static (_, state) => ((CancellationTokenSource)state!).Cancel(),
            attachCts,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        consoleInput.PumpInputUntil(attachCts.Token);

        exitCode = exitTask.IsCompletedSuccessfully ? exitTask.Result : session.ExitCode ?? -1;
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
            Size = new PtySize(80, 24),
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
