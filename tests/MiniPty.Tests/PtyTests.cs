using System.Runtime.InteropServices;
using MiniPty;
using TUnit.Assertions;
using TUnit.Core;

namespace MiniPty.Tests;

[NotInParallel]
public sealed class PtyTests
{
    [Test]
    public async Task PtyEchoOutput()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
            var result = await Pty.Run(Spawn(cmd, ["/c", "echo pty-layer-echo"]));

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(result.Output).Contains("pty-layer-echo");
            return;
        }

        var shell = Environment.GetEnvironmentVariable("SHELL") ?? "/bin/bash";
        var unix = await Pty.Run(Spawn(shell, ["-lc", "printf pty-layer-echo"]));

        await Assert.That(unix.ExitCode).IsEqualTo(0);
        await Assert.That(unix.Output).Contains("pty-layer-echo");
    }

    [Test]
    public async Task PtyTtyCheck()
    {
        if (!TryResolvePwsh(out var pwsh))
            return;

        var result = await Pty.Run(Spawn(pwsh, ["-NoLogo", "-NoProfile", "-Command", "Write-Output (\"redirected=$([Console]::IsOutputRedirected)\")"]));

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Output).Contains("redirected=False").IgnoringCase();
    }

    [Test]
    public async Task PtyStdinEof()
    {
        const string marker = "pty-stdin-eof";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var sort = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sort.exe");
            var result = await Pty.Run(Spawn(sort, []) with { Input = $"zzz\r\n{marker}\r\naaa\r\n" });

            await Assert.That(result.Output).Contains(marker);
            await Assert.That(result.Output).Contains("aaa");
            return;
        }

        var shell = Environment.GetEnvironmentVariable("SHELL") ?? "/bin/bash";
        var unix = await Pty.Run(Spawn(shell, ["-lc", "cat"]) with { Input = marker });

        await Assert.That(unix.ExitCode).IsEqualTo(0);
        await Assert.That(unix.Output).Contains(marker);
    }

    [Test]
    public async Task PtyEmptyInputSignalsEof()
    {
        const string marker = "pty-empty-eof-complete";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (!TryResolvePwsh(out var pwsh))
                return;

            var result = await Pty.Run(Spawn(pwsh, ["-NoLogo", "-NoProfile", "-Command", $"[Console]::In.ReadToEnd() > $null; [Console]::Write('{marker}')"]) with { Input = string.Empty });

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(result.Output).Contains(marker);
            return;
        }

        var unix = await Pty.Run(UnixShell($"cat >/dev/null; printf {marker}") with { Input = string.Empty });

        await Assert.That(unix.ExitCode).IsEqualTo(0);
        await Assert.That(unix.Output).Contains(marker);
    }

    [Test]
    public async Task PtyStdinReadCompletesAfterInputEof()
    {
        const string marker = "pty-stdin-read-complete";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (!TryResolvePwsh(out var pwsh))
                return;

            var result = await Pty.Run(Spawn(pwsh, ["-NoLogo", "-NoProfile", "-Command", $"[Console]::In.ReadToEnd() > $null; [Console]::Write('{marker}')"]) with { Input = "line 1\r\nline 2\r\n" });

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(result.Output).Contains(marker);
            return;
        }

        var unix = await Pty.Run(UnixShell($"cat >/dev/null; printf {marker}") with { Input = "line 1\nline 2\n" });

        await Assert.That(unix.ExitCode).IsEqualTo(0);
        await Assert.That(unix.Output).Contains(marker);
    }

    [Test]
    public async Task PtyLargeOutputDoesNotBlock()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (!TryResolvePwsh(out var pwsh))
                return;

            var result = await Pty.Run(Spawn(pwsh, ["-NoLogo", "-NoProfile", "-Command", "[Console]::Out.Write(('x' * 1000000))"]));

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(result.Output.Length).IsGreaterThan(999_999);
            return;
        }

        var unix = await Pty.Run(UnixShell("yes x | head -c 1000000"));

        await Assert.That(unix.ExitCode).IsEqualTo(0);
        await Assert.That(unix.Output.Length).IsGreaterThan(999_999);
    }

    [Test]
    public async Task PtyExitCodeIsCaptured()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var result = await Pty.Run(WindowsCommand("exit /b 42"));

            await Assert.That(result.ExitCode).IsEqualTo(42);
            return;
        }

        var unix = await Pty.Run(UnixShell("exit 42"));

        await Assert.That(unix.ExitCode).IsEqualTo(42);
    }

    [Test]
    public async Task PtySignalExitCodeIsCapturedOnUnix()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var result = await Pty.Run(UnixShell("kill -TERM $$"));

        await Assert.That(result.ExitCode).IsEqualTo(143);
    }

    [Test]
    public async Task PtyChildSeesTtyOutput()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (!TryResolvePwsh(out var pwsh))
                return;

            var result = await Pty.Run(Spawn(pwsh, ["-NoLogo", "-NoProfile", "-Command", "[Console]::WriteLine([Console]::IsOutputRedirected)"]));

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(result.Output).Contains("False").IgnoringCase();
            return;
        }

        var unix = await Pty.Run(UnixShell("test -t 1 && printf true || printf false"));

        await Assert.That(unix.ExitCode).IsEqualTo(0);
        await Assert.That(unix.Output).Contains("true");
    }

    [Test]
    public async Task PtyAnsiOutputIsPreserved()
    {
        const string ansiRed = "\u001b[31mred\u001b[0m";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (!TryResolvePwsh(out var pwsh))
                return;

            var result = await Pty.Run(Spawn(pwsh, ["-NoLogo", "-NoProfile", "-Command", "[Console]::Write([char]27 + '[31mred' + [char]27 + '[0m')"]));

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(result.Output).Contains(ansiRed);
            return;
        }

        var unix = await Pty.Run(UnixShell("printf '\\033[31mred\\033[0m'"));

        await Assert.That(unix.ExitCode).IsEqualTo(0);
        await Assert.That(unix.Output).Contains(ansiRed);
    }

    [Test]
    public async Task PtyHasExitedPolls()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
            using var session = Pty.Start(Spawn(cmd, ["/c", "exit 0"]));

            await WaitUntilExited(session);

            await Assert.That(session.HasExited).IsTrue();
            return;
        }

        var shell = Environment.GetEnvironmentVariable("SHELL") ?? "/bin/bash";
        using var unixSession = Pty.Start(Spawn(shell, ["-lc", "exit 0"]));

        await WaitUntilExited(unixSession);

        await Assert.That(unixSession.HasExited).IsTrue();
    }

    [Test]
    public async Task PtyCancellationKill()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
            using var session = Pty.Start(Spawn(cmd, ["/c", "ping -n 30 127.0.0.1 >nul"]));

            await Assert.ThrowsAsync<OperationCanceledException>(() => session.WaitForExitOrKillAsync(cts.Token));
            await Task.Delay(200);
            await Assert.That(session.HasExited).IsTrue();
            return;
        }

        var shell = Environment.GetEnvironmentVariable("SHELL") ?? "/bin/bash";
        using var unixSession = Pty.Start(Spawn(shell, ["-lc", "sleep 30"]));

        await Assert.ThrowsAsync<OperationCanceledException>(() => unixSession.WaitForExitOrKillAsync(cts.Token));
        await Task.Delay(200);
        await Assert.That(unixSession.HasExited).IsTrue();
    }

    [Test]
    public async Task PtyCancellationWait()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
            using var session = Pty.Start(Spawn(cmd, ["/c", "ping -n 30 127.0.0.1 >nul"]));

            await Assert.ThrowsAsync<OperationCanceledException>(() => session.WaitForExitAsync(cts.Token));
            await Assert.That(session.HasExited).IsFalse();
            return;
        }

        var shell = Environment.GetEnvironmentVariable("SHELL") ?? "/bin/bash";
        using var unixSession = Pty.Start(Spawn(shell, ["-lc", "sleep 30"]));

        await Assert.ThrowsAsync<OperationCanceledException>(() => unixSession.WaitForExitAsync(cts.Token));
        await Assert.That(unixSession.HasExited).IsFalse();
    }

    [Test]
    public async Task PtyResizeUpdatesSize()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
            using var session = Pty.Start(Spawn(cmd, ["/c", "exit 0"]));

            session.Resize(100, 30);

            await Assert.That(session.Size.Columns).IsEqualTo(100);
            await Assert.That(session.Size.Rows).IsEqualTo(30);
            return;
        }

        var shell = Environment.GetEnvironmentVariable("SHELL") ?? "/bin/bash";
        using var unixSession = Pty.Start(Spawn(shell, ["-lc", "exit 0"]));

        unixSession.Resize(100, 30);

        await Assert.That(unixSession.Size.Columns).IsEqualTo(100);
        await Assert.That(unixSession.Size.Rows).IsEqualTo(30);
    }

    [Test]
    public async Task PtyChildSeesResizedSize()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (!TryResolvePwsh(out var pwsh))
                return;

            using var session = Pty.Start(Spawn(pwsh, ["-NoLogo", "-NoProfile", "-Command", "[Console]::ReadLine() > $null; [Console]::WriteLine(\"{0} {1}\" -f [Console]::WindowWidth, [Console]::WindowHeight)"]));
            var recorder = PtyOutputRecorder.Start(session);

            session.Resize(100, 30);
            await WriteLineAsync(session, "go");
            var exitCode = await session.WaitForExitAsync();
            var output = string.Concat((await recorder.CollectAsync()).Select(static chunk => chunk.Data));

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(output).Contains("100 30");
            return;
        }

        using var unixSession = Pty.Start(UnixShell("read line; stty size"));
        var unixRecorder = PtyOutputRecorder.Start(unixSession);

        unixSession.Resize(100, 30);
        await WriteLineAsync(unixSession, "go");
        var unixExitCode = await unixSession.WaitForExitAsync();
        var unixOutput = string.Concat((await unixRecorder.CollectAsync()).Select(static chunk => chunk.Data));

        await Assert.That(unixExitCode).IsEqualTo(0);
        await Assert.That(unixOutput).Contains("30 100");
    }

    [Test]
    public async Task PtyMatrixPwsh()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || !TryResolvePwsh(out var pwshPath))
            return;

        var result = await Pty.Run(Spawn(pwshPath, ["-NoLogo", "-NoProfile", "-Command", "matrix -c 120 -s 2"]));

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Chunks.Count).IsGreaterThan(1);
    }

    private static PtyOptions Spawn(string fileName, IReadOnlyList<string> arguments) =>
        new() { FileName = fileName, Arguments = arguments, Columns = 40, Rows = 8 };

    private static PtyOptions UnixShell(string command) => Spawn("sh", ["-c", command]);

    private static PtyOptions WindowsCommand(string command)
    {
        var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
        return Spawn(cmd, ["/c", command]);
    }

    private static async Task WriteLineAsync(PtySession session, string line)
    {
        await using var writer = new StreamWriter(session.Input, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(line);
    }

    private static async Task WaitUntilExited(PtySession session)
    {
        for (var attempt = 0; attempt < 50 && !session.HasExited; attempt++)
            await Task.Delay(20);
    }

    private static bool TryResolvePwsh(out string path)
    {
        path = "";
        var env = Environment.GetEnvironmentVariable("PWSH");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            path = env;
            return true;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var candidate = Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");
        if (File.Exists(candidate))
        {
            path = candidate;
            return true;
        }

        return false;
    }
}
