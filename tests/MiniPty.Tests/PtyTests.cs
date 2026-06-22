using System.Runtime.InteropServices;
using MiniPty;
using MiniPty.Capture;
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
            var result = await PtyCapture.RunAsync(Spawn(cmd, ["/c", "echo pty-layer-echo"]));

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(PtyMemory.Contains(result.Output, "pty-layer-echo")).IsTrue();
            return;
        }

        var unix = await PtyCapture.RunAsync(UnixShell("printf pty-layer-echo"));

        await Assert.That(unix.ExitCode).IsEqualTo(0);
        await Assert.That(PtyMemory.Contains(unix.Output, "pty-layer-echo")).IsTrue();
    }

    [Test]
    public async Task PtyTtyCheck()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (!TryResolveWindowsPowerShell(out var powershell))
                return;

            var result = await PtyCapture.RunAsync(
                Spawn(powershell, ["-NoLogo", "-NoProfile", "-Command", "Write-Output (\"redirected=$([Console]::IsOutputRedirected)\")"]));

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(PtyMemory.Contains(result.Output, "redirected=False", StringComparison.OrdinalIgnoreCase)).IsTrue();
            return;
        }

        var unix = await PtyCapture.RunAsync(UnixShell("test -t 1 && printf redirected=False || printf redirected=True"));

        await Assert.That(unix.ExitCode).IsEqualTo(0);
        await Assert.That(PtyMemory.Contains(unix.Output, "redirected=False", StringComparison.OrdinalIgnoreCase)).IsTrue();
    }

    [Test]
    public async Task PtyStdinEof()
    {
        const string marker = "pty-stdin-eof";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var sort = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sort.exe");
            var result = await PtyCapture.RunAsync(
                Spawn(sort, []),
                new PtyCaptureOptions { Completion = new() { Input = $"zzz\r\n{marker}\r\naaa\r\n" } });

            await Assert.That(PtyMemory.Contains(result.Output, marker)).IsTrue();
            await Assert.That(PtyMemory.Contains(result.Output, "aaa")).IsTrue();
            return;
        }

        // Canonical PTY line discipline needs a submitted line before EOT signals EOF; run cat directly (not a login shell).
        var unix = await PtyCapture.RunAsync(
            Spawn("cat", []),
            new PtyCaptureOptions { Completion = new() { Input = $"{marker}\n" } });

        await Assert.That(unix.ExitCode).IsEqualTo(0);
        await Assert.That(PtyMemory.Contains(unix.Output, marker)).IsTrue();
    }

    [Test]
    public async Task PtyEmptyInputSignalsEof()
    {
        const string marker = "pty-empty-eof-complete";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var result = await PtyCapture.RunAsync(
                WindowsCommand($"find /v \"\" >nul & echo {marker}"),
                new PtyCaptureOptions { Completion = new() { Input = string.Empty } });

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(PtyMemory.Contains(result.Output, marker)).IsTrue();
            return;
        }

        var unix = await PtyCapture.RunAsync(
            UnixShell($"cat >/dev/null; printf {marker}"),
            new PtyCaptureOptions { Completion = new() { Input = string.Empty } });

        await Assert.That(unix.ExitCode).IsEqualTo(0);
        await Assert.That(PtyMemory.Contains(unix.Output, marker)).IsTrue();
    }

    [Test]
    public async Task PtyStdinReadCompletesAfterInputEof()
    {
        const string marker = "pty-stdin-read-complete";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var result = await PtyCapture.RunAsync(
                WindowsCommand($"find /v \"\" >nul & echo {marker}"),
                new PtyCaptureOptions { Completion = new() { Input = "line 1\r\nline 2\r\n" } });

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(PtyMemory.Contains(result.Output, marker)).IsTrue();
            return;
        }

        var unix = await PtyCapture.RunAsync(
            UnixShell($"cat >/dev/null; printf {marker}"),
            new PtyCaptureOptions { Completion = new() { Input = "line 1\nline 2\n" } });

        await Assert.That(unix.ExitCode).IsEqualTo(0);
        await Assert.That(PtyMemory.Contains(unix.Output, marker)).IsTrue();
    }

    [Test]
    public async Task PtyLargeOutputDoesNotBlock()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (!TryResolveWindowsPowerShell(out var powershell))
                return;

            var result = await PtyCapture.RunAsync(
                Spawn(powershell, ["-NoLogo", "-NoProfile", "-Command", "[Console]::Out.Write(('x' * 1000000))"]));

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(result.Output.Length).IsGreaterThan(999_999);
            return;
        }

        var unix = await PtyCapture.RunAsync(UnixShell("yes x | head -c 1000000"));

        await Assert.That(unix.ExitCode).IsEqualTo(0);
        await Assert.That(unix.Output.Length).IsGreaterThan(999_999);
    }

    [Test]
    public async Task PtyExitCodeIsCaptured()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var result = await PtyCapture.RunAsync(WindowsCommand("exit /b 42"));

            await Assert.That(result.ExitCode).IsEqualTo(42);
            return;
        }

        var unix = await PtyCapture.RunAsync(UnixShell("exit 42"));

        await Assert.That(unix.ExitCode).IsEqualTo(42);
    }

    [Test]
    public async Task PtySignalExitCodeIsCapturedOnUnix()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var result = await PtyCapture.RunAsync(UnixShell("kill -TERM $$"));

        await Assert.That(result.ExitCode).IsEqualTo(143);
    }

    [Test]
    public async Task PtyChildSeesTtyOutput()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (!TryResolveWindowsPowerShell(out var powershell))
                return;

            var result = await PtyCapture.RunAsync(
                Spawn(powershell, ["-NoLogo", "-NoProfile", "-Command", "[Console]::WriteLine([Console]::IsOutputRedirected)"]));

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(PtyMemory.Contains(result.Output, "False", StringComparison.OrdinalIgnoreCase)).IsTrue();
            return;
        }

        var unix = await PtyCapture.RunAsync(UnixShell("test -t 1 && printf true || printf false"));

        await Assert.That(unix.ExitCode).IsEqualTo(0);
        await Assert.That(PtyMemory.Contains(unix.Output, "true")).IsTrue();
    }

    [Test]
    public async Task PtyAnsiOutputIsPreserved()
    {
        const string ansiRed = "\u001b[31mred\u001b[0m";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (!TryResolveWindowsPowerShell(out var powershell))
                return;

            var result = await PtyCapture.RunAsync(
                Spawn(powershell, ["-NoLogo", "-NoProfile", "-Command", "[Console]::Write([char]27 + '[31mred' + [char]27 + '[0m')"]));

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(PtyMemory.Contains(result.Output, ansiRed) || PtyMemory.Contains(result.Output, "red")).IsTrue();
            return;
        }

        var unix = await PtyCapture.RunAsync(UnixShell("printf '\\033[31mred\\033[0m'"));

        await Assert.That(unix.ExitCode).IsEqualTo(0);
        await Assert.That(PtyMemory.Contains(unix.Output, ansiRed)).IsTrue();
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

        using var unixSession = Pty.Start(Spawn("true", []));

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
            using var session = Pty.Start(Spawn(cmd, ["/c", "ping -n 8 127.0.0.1 >nul"]));

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                session.CompleteAsync(new PtyCompleteOptions { KillOnCancellation = true }, cts.Token));
            await Task.Delay(200);
            await Assert.That(session.HasExited).IsTrue();
            return;
        }

        using var unixSession = Pty.Start(Spawn("sleep", ["8"]));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            unixSession.CompleteAsync(new PtyCompleteOptions { KillOnCancellation = true }, cts.Token));
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
            using var session = Pty.Start(Spawn(cmd, ["/c", "ping -n 8 127.0.0.1 >nul"]));

            await Assert.ThrowsAsync<OperationCanceledException>(() => session.WaitForExitAsync(cts.Token));
            await Assert.That(session.HasExited).IsFalse();
            return;
        }

        using var unixSession = Pty.Start(Spawn("sleep", ["8"]));

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

            session.Resize(new(100, 30));

            await Assert.That(session.Size.Columns).IsEqualTo(100);
            await Assert.That(session.Size.Rows).IsEqualTo(30);
            return;
        }

        using var unixSession = Pty.Start(Spawn("true", []));

        unixSession.Resize(new(100, 30));

        await Assert.That(unixSession.Size.Columns).IsEqualTo(100);
        await Assert.That(unixSession.Size.Rows).IsEqualTo(30);
    }

    [Test]
    public async Task PtyChildSeesResizedSize()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (!TryResolveWindowsPowerShell(out var powershell))
                return;

            await using var session = Pty.Start(
                Spawn(powershell, ["-NoLogo", "-NoProfile", "-Command", "$s = $Host.UI.RawUI.WindowSize; Start-Sleep -Milliseconds 200; Write-Output (\"{0} {1}\" -f $s.Width, $s.Height); exit 0"]));
            session.Resize(new(100, 30));
            var result = await session.CompleteAsync();

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(PtyMemory.Contains(result.Output, "100 30")).IsTrue();
            return;
        }

        // Block on read until after Resize so a fast ARM runner cannot query size before SIGWINCH.
        await using var unixSession = Pty.Start(
            UnixShell("read _; set -- $(stty size); printf 'SIZE:%s:%s\\n' \"$1\" \"$2\""));
        unixSession.Resize(new(100, 30));
        var unixResult = await unixSession.CompleteAsync(new PtyCompleteOptions { Input = "go\n" });

        await Assert.That(unixResult.ExitCode).IsEqualTo(0);
        await Assert.That(PtyMemory.Contains(unixResult.Output, "SIZE:30:100")).IsTrue();
    }

    [Test]
    public async Task PtyMatrixPwsh()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || !TryResolvePwsh(out var pwshPath))
            return;

        if (!await TryResolveMatrixCmdlet(pwshPath))
            return;

        var result = await PtyCapture.RunAsync(Spawn(pwshPath, ["-NoLogo", "-NoProfile", "-Command", "matrix -c 120 -s 2"]));

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Chunks.Count).IsGreaterThan(1);
    }

    private static PtyStartInfo Spawn(string fileName, IReadOnlyList<string> arguments) =>
        new() { FileName = fileName, Arguments = arguments, Size = new(40, 8) };

    private static PtyStartInfo UnixShell(string command) => Spawn("sh", ["-c", command]);

    private static PtyStartInfo WindowsCommand(string command)
    {
        var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
        return Spawn(cmd, ["/c", command]);
    }

    private static async Task WaitUntilExited(PtySession session)
    {
        for (var attempt = 0; attempt < 50 && !session.HasExited; attempt++)
            await Task.Delay(20);
    }

    private static bool TryResolveWindowsPowerShell(out string path)
    {
        path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        return File.Exists(path);
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

    private static async Task<bool> TryResolveMatrixCmdlet(string pwshPath)
    {
        var probe = await PtyCapture.RunAsync(
            Spawn(pwshPath, ["-NoLogo", "-NoProfile", "-Command", "if (Get-Command matrix -ErrorAction SilentlyContinue) { exit 0 } else { exit 1 }"]));

        return probe.ExitCode == 0;
    }
}
