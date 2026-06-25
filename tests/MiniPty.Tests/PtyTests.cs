using System.Runtime.InteropServices;
using System.Text;
using MiniPty.Capture;

namespace MiniPty.Tests;

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
            await Assert.That(result.Contains("pty-layer-echo")).IsTrue();
            return;
        }

        var unix = await PtyCapture.RunAsync(UnixShell("printf pty-layer-echo"));

        await Assert.That(unix.ExitCode).IsEqualTo(0);
        await Assert.That(unix.Contains("pty-layer-echo")).IsTrue();
    }

    [Test]
    public async Task PtyCaptureBytesOnlySkipsPumpDecode()
    {
        const string marker = "pty-bytes-only";

        var result = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? await PtyCapture.RunAsync(
                WindowsCommand($"echo {marker}"),
                new PtyCaptureOptions { Completion = new PtyCompleteOptions { DecodeOutput = false } })
            : await PtyCapture.RunAsync(
                UnixShell($"printf {marker}"),
                new PtyCaptureOptions { Completion = new PtyCompleteOptions { DecodeOutput = false } });

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.ContainsUtf8(marker)).IsTrue();
        await Assert.That(result.Chunks.Count).IsGreaterThan(0);
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
            await Assert.That(result.Contains("redirected=False", StringComparison.OrdinalIgnoreCase)).IsTrue();
            return;
        }

        var unix = await PtyCapture.RunAsync(UnixShell("test -t 1 && printf redirected=False || printf redirected=True"));

        await Assert.That(unix.ExitCode).IsEqualTo(0);
        await Assert.That(unix.Contains("redirected=False", StringComparison.OrdinalIgnoreCase)).IsTrue();
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

            await Assert.That(result.Contains(marker)).IsTrue();
            await Assert.That(result.Contains("aaa")).IsTrue();
            return;
        }

        // Canonical PTY line discipline needs a submitted line before EOT signals EOF; run cat directly (not a login shell).
        var unix = await PtyCapture.RunAsync(
            Spawn("cat", []),
            new PtyCaptureOptions { Completion = new() { Input = $"{marker}\n" } });

        await Assert.That(unix.ExitCode).IsEqualTo(0);
        await Assert.That(unix.Contains(marker)).IsTrue();
    }

    [Test]
    public async Task PtyStdinEof_withoutTrailingNewline()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        const string marker = "pty-stdin-eof-no-nl";
        var unix = await PtyCapture.RunAsync(
            Spawn("cat", []),
            new PtyCaptureOptions { Completion = new() { Input = marker } });

        await Assert.That(unix.ExitCode).IsEqualTo(0);
        await Assert.That(unix.Contains(marker)).IsTrue();
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
            await Assert.That(result.Contains(marker)).IsTrue();
            return;
        }

        var unix = await PtyCapture.RunAsync(
            UnixShell($"cat >/dev/null; printf {marker}"),
            new PtyCaptureOptions { Completion = new() { Input = string.Empty } });

        await Assert.That(unix.ExitCode).IsEqualTo(0);
        await Assert.That(unix.Contains(marker)).IsTrue();
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
            await Assert.That(result.Contains(marker)).IsTrue();
            return;
        }

        var unix = await PtyCapture.RunAsync(
            UnixShell($"cat >/dev/null; printf {marker}"),
            new PtyCaptureOptions { Completion = new() { Input = "line 1\nline 2\n" } });

        await Assert.That(unix.ExitCode).IsEqualTo(0);
        await Assert.That(unix.Contains(marker)).IsTrue();
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
    public async Task PtyReadOutputAsyncReadsBytesUntilExit()
    {
        const string marker = "minipty-streaming-output";

        await using var session = Pty.Start(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? WindowsCommand($"echo {marker}")
            : UnixShell($"printf {marker}"));

        var text = await ReadOutputTextAsync(session, marker);
        var exitCode = await session.WaitForExitAsync();

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(text).Contains(marker);
    }

    [Test]
    public async Task PtyReadOutputAsyncSupportsPersistentCommandLoop()
    {
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        PtyStartInfo startInfo;
        if (isWindows)
        {
            if (!TryResolveWindowsPowerShell(out var powershell))
                return;

            startInfo = Spawn(powershell,
            [
                "-NoLogo",
                "-NoProfile",
                "-Command",
                "[Console]::Out.WriteLine('ready'); $a=[Console]::In.ReadLine(); [Console]::Out.WriteLine('first:' + $a); $b=[Console]::In.ReadLine(); [Console]::Out.WriteLine('second:' + $b)"
            ]);
        }
        else
        {
            startInfo = Spawn("sh", ["-c", "printf 'ready\\n'; IFS= read -r a; printf 'first:%s\\n' \"$a\"; IFS= read -r b; printf 'second:%s\\n' \"$b\""]);
        }

        var newline = isWindows ? "\r\n" : "\n";

        await using var session = Pty.Start(startInfo);
        var outputTask = ReadOutputTextAsync(session, "second:beta");

        await session.WriteInputAsync("alpha" + newline);
        await session.WriteInputAsync("beta" + newline);

        var text = await outputTask;
        var exitCode = await session.WaitForExitAsync();

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(text).Contains("first:alpha");
        await Assert.That(text).Contains("second:beta");
    }

    [Test]
    public async Task PtyReadOutputAsyncRejectsConcurrentReaders()
    {
        await using var session = Pty.Start(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? WindowsCommand("echo ready & ping -n 3 127.0.0.1 >nul")
            : UnixShell("printf ready; sleep 2"));

        await using var first = session.ReadOutputAsync().GetAsyncEnumerator();
        await Assert.That(await first.MoveNextAsync()).IsTrue();

        await using var second = session.ReadOutputAsync().GetAsyncEnumerator();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await second.MoveNextAsync());
    }

    [Test]
    public async Task PtyReadOutputAsyncCancellationDoesNotKillChild()
    {
        await using var session = Pty.Start(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? WindowsCommand("echo ready & set /p DUMMY=")
            : UnixShell("printf 'ready\\n'; IFS= read -r _"));

        using var cts = new CancellationTokenSource();
        await using var reader = session.ReadOutputAsync(cts.Token).GetAsyncEnumerator();
        await Assert.That(await reader.MoveNextAsync()).IsTrue();

        await cts.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await reader.MoveNextAsync());
        await Assert.That(session.HasExited).IsFalse();
    }

    [Test]
    public async Task PtyReadOutputAsyncCanRestartAfterCancellation()
    {
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        PtyStartInfo startInfo;
        if (isWindows)
        {
            if (!TryResolveWindowsPowerShell(out var powershell))
                return;

            startInfo = Spawn(powershell,
            [
                "-NoLogo",
                "-NoProfile",
                "-Command",
                "[Console]::Out.WriteLine('ready'); $line=[Console]::In.ReadLine(); [Console]::Out.WriteLine('after:' + $line)"
            ]);
        }
        else
        {
            startInfo = Spawn("sh", ["-c", "printf 'ready\\n'; IFS= read -r line; printf 'after:%s\\n' \"$line\""]);
        }

        await using var session = Pty.Start(startInfo);

        using (var cts = new CancellationTokenSource())
        {
            await using var reader = session.ReadOutputAsync(cts.Token).GetAsyncEnumerator();
            await Assert.That(await reader.MoveNextAsync()).IsTrue();
            await cts.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await reader.MoveNextAsync());
        }

        var newline = isWindows ? "\r\n" : "\n";
        var outputTask = ReadOutputTextAsync(session, "after:restart");
        await session.WriteInputAsync("restart" + newline);
        var text = await outputTask;
        var exitCode = await session.WaitForExitAsync();

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(text).Contains("after:restart");
    }

    [Test]
    public async Task PtyReadOutputAsyncThrowsObjectDisposedWhenDisposedWhileReading()
    {
        var session = Pty.Start(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? WindowsCommand("echo ready & ping -n 8 127.0.0.1 >nul")
            : UnixShell("printf ready; sleep 8"));

        await using var reader = session.ReadOutputAsync().GetAsyncEnumerator();
        await Assert.That(await reader.MoveNextAsync()).IsTrue();

        session.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await reader.MoveNextAsync());
    }

    [Test]
    public async Task PtyReadOutputAsyncDrainsLargeOutputAfterExit()
    {
        const int minimumLength = 128 * 1024;

        await using var session = Pty.Start(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Spawn(Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe", ["/c", "for /l %i in (1,1,4096) do @echo 0123456789abcdef0123456789abcdef"])
            : UnixShell("yes 0123456789abcdef0123456789abcdef | head -c 131072"));

        var length = 0;
        await foreach (var chunk in session.ReadOutputAsync())
            length += chunk.Data.Length;

        var exitCode = await session.WaitForExitAsync();

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(length).IsGreaterThanOrEqualTo(minimumLength);
    }

    [Test]
    public async Task PtyOutputChunkDataCanSplitUtf8Sequences()
    {
        const string marker = "utf8-boundary";
        const string expected = "\u20AC" + marker;

        var bytes = Encoding.UTF8.GetBytes(expected);
        var chunks = new[]
        {
            new PtyOutputChunk(bytes.AsMemory(0, 1)),
            new PtyOutputChunk(bytes.AsMemory(1))
        };

        using var output = new MemoryStream();
        var chunkDecode = new StringBuilder();

        foreach (var chunk in chunks)
        {
            await output.WriteAsync(chunk.Data);
            chunkDecode.Append(Encoding.UTF8.GetString(chunk.Data.Span));
        }

        var decoded = Encoding.UTF8.GetString(output.GetBuffer().AsSpan(0, checked((int)output.Length)));

        await Assert.That(decoded).Contains(expected);
        await Assert.That(chunkDecode.ToString()).DoesNotContain(expected);
    }

    [Test]
    public async Task PtyReadOutputAsyncDrainsOutputAcrossBoundedBufferCapacity()
    {
        const int minimumLength = 2 * 1024 * 1024;

        PtyStartInfo startInfo;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (!TryResolveWindowsPowerShell(out var powershell))
                return;

            startInfo = Spawn(powershell, ["-NoLogo", "-NoProfile", "-Command", "[Console]::Out.Write(('x' * 2097152))"]);
        }
        else
        {
            startInfo = UnixShell("yes x | head -c 2097152");
        }

        await using var session = Pty.Start(startInfo);

        var length = 0;
        await foreach (var chunk in session.ReadOutputAsync())
            length += chunk.Data.Length;

        var exitCode = await session.WaitForExitAsync();

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(length).IsGreaterThanOrEqualTo(minimumLength);
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
            await Assert.That(result.Contains("False", StringComparison.OrdinalIgnoreCase)).IsTrue();
            return;
        }

        var unix = await PtyCapture.RunAsync(UnixShell("test -t 1 && printf true || printf false"));

        await Assert.That(unix.ExitCode).IsEqualTo(0);
        await Assert.That(unix.Contains("true")).IsTrue();
    }

    [Test]
    public async Task PtyUnixTerminalNameSetsTerm()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var result = await PtyCapture.RunAsync(UnixShell("printf '%s' \"$TERM\"") with
        {
            TerminalName = "xterm-test"
        });

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Contains("xterm-test")).IsTrue();
    }

    [Test]
    public async Task PtyUnixExplicitTermRemovalSuppressesDefault()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var result = await PtyCapture.RunAsync(Spawn("env", []) with
        {
            Environment = new Dictionary<string, string?> { ["TERM"] = null }
        });

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(ContainsEnvironmentLine(result.GetTextString(), "TERM")).IsFalse();
    }

    /// <summary>
    /// Exercises the native <c>envp == null</c> path (<c>minipty_build_inherited_envp</c>), which is not covered by
    /// <see cref="PtyEnvironmentTests"/>. Mutates process environment, so it must not run in parallel with itself.
    /// </summary>
    [Test]
    [NotInParallel("native-unix-environ")]
    public async Task PtyUnixNativePathSanitizesInheritedTerminalSizeVariables()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var previousColumns = Environment.GetEnvironmentVariable("COLUMNS");
        var previousLines = Environment.GetEnvironmentVariable("LINES");
        Environment.SetEnvironmentVariable("COLUMNS", "999");
        Environment.SetEnvironmentVariable("LINES", "888");
        try
        {
            var result = await PtyCapture.RunAsync(
                UnixShell("test -z \"${COLUMNS+x}\" && test -z \"${LINES+x}\" && printf 'COLUMNS:MISSING;LINES:MISSING'"));

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(result.Contains("COLUMNS:MISSING")).IsTrue();
            await Assert.That(result.Contains("LINES:MISSING")).IsTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("COLUMNS", previousColumns);
            Environment.SetEnvironmentVariable("LINES", previousLines);
        }
    }

    [Test]
    public async Task PtyUnixPathLookupUsesOverlayAndFallsBackToShellForPlainScripts()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var tempRoot = Path.Combine(Path.GetTempPath(), "minipty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var script = Path.Combine(tempRoot, "minipty-plain-script");
            await File.WriteAllTextAsync(script, "#!/bin/sh\nprintf path-overlay-shell-fallback\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var result = await PtyCapture.RunAsync(Spawn("minipty-plain-script", []) with
            {
                Environment = new Dictionary<string, string?> { ["PATH"] = tempRoot }
            });

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(result.Contains("path-overlay-shell-fallback")).IsTrue();
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Test]
    public async Task PtyUnixPathLookupUsesFixedFallbackWhenPathIsAbsent()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var result = await PtyCapture.RunAsync(Spawn("sh", ["-c", "printf path-absent-fallback"]) with
        {
            Environment = new Dictionary<string, string?> { ["PATH"] = null }
        });

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Contains("path-absent-fallback")).IsTrue();
    }

    [Test]
    public async Task PtyUnixPathLookupTreatsEmptyPathEntriesAsCurrentDirectory()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var tempRoot = Path.Combine(Path.GetTempPath(), "minipty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var script = Path.Combine(tempRoot, "minipty-current-path-script");
            await File.WriteAllTextAsync(script, "printf current-path-entry");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var result = await PtyCapture.RunAsync(Spawn("minipty-current-path-script", []) with
            {
                WorkingDirectory = tempRoot,
                Environment = new Dictionary<string, string?> { ["PATH"] = ":" }
            });

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(result.Contains("current-path-entry")).IsTrue();
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Test]
    public async Task PtyWindowsTerminalNameDoesNotSetTerm()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || !TryResolveWindowsPowerShell(out var powershell))
            return;

        var result = await PtyCapture.RunAsync(Spawn(powershell,
            [
                "-NoLogo",
                "-NoProfile",
                "-Command",
                "$term=[Environment]::GetEnvironmentVariable('TERM','Process'); if ($null -eq $term) { 'TERM:MISSING' } else { 'TERM:' + $term }"
            ]) with
            {
                TerminalName = "xterm-test",
                Environment = new Dictionary<string, string?> { ["TERM"] = null }
            });

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Contains("TERM:MISSING")).IsTrue();
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
            await Assert.That(result.Contains(ansiRed) || result.Contains("red")).IsTrue();
            return;
        }

        var unix = await PtyCapture.RunAsync(UnixShell("printf '\\033[31mred\\033[0m'"));

        await Assert.That(unix.ExitCode).IsEqualTo(0);
        await Assert.That(unix.Contains(ansiRed)).IsTrue();
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
            using var session = Pty.Start(Spawn(cmd, ["/c", "set /p DUMMY="]));

            await Assert.ThrowsAsync<OperationCanceledException>(() => session.WaitForExitAsync(cts.Token));
            await Assert.That(session.HasExited).IsFalse();
            return;
        }

        using var unixSession = Pty.Start(Spawn("sh", ["-c", "IFS= read -r _"]));

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
            await Assert.That(result.Contains("100 30")).IsTrue();
            return;
        }

        // Block on read until after Resize so a fast ARM runner cannot query size before SIGWINCH.
        await using var unixSession = Pty.Start(
            UnixShell("read _; set -- $(stty size); printf 'SIZE:%s:%s\\n' \"$1\" \"$2\""));
        unixSession.Resize(new(100, 30));
        var unixResult = await unixSession.CompleteAsync(new PtyCompleteOptions { Input = "go\n" });

        await Assert.That(unixResult.ExitCode).IsEqualTo(0);
        await Assert.That(unixResult.Contains("SIZE:30:100")).IsTrue();
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

    [Test]
    public async Task PtyCompleteAsyncRejectsWhileReadOutputAsyncActive()
    {
        await using var session = Pty.Start(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? WindowsCommand("echo ready & ping -n 6 127.0.0.1 >nul")
            : UnixShell("printf ready; sleep 6"));

        await using var reader = session.ReadOutputAsync().GetAsyncEnumerator();
        await Assert.That(await reader.MoveNextAsync()).IsTrue();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.CompleteAsync(new PtyCompleteOptions()));
    }

    [Test]
    public async Task PtyReadOutputAsyncRejectsWhileRawOutputActive()
    {
        await using var session = Pty.Start(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? WindowsCommand("echo ready & ping -n 6 127.0.0.1 >nul")
            : UnixShell("printf ready; sleep 6"));

        var bytes = new byte[256];
        var readTask = session.Output.ReadAsync(bytes);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var reader = session.ReadOutputAsync().GetAsyncEnumerator();
            await reader.MoveNextAsync();
        });

        await readTask;
    }

    [Test]
    public async Task PtyCompleteAsyncRejectsWhileRawOutputActive()
    {
        await using var session = Pty.Start(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? WindowsCommand("echo ready & ping -n 6 127.0.0.1 >nul")
            : UnixShell("printf ready; sleep 6"));

        var bytes = new byte[256];
        var readTask = session.Output.ReadAsync(bytes);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.CompleteAsync(new PtyCompleteOptions()));

        await readTask;
    }

    [Test]
    public async Task PtyDisposeDuringWaitForExitThrowsObjectDisposed()
    {
        var session = Pty.Start(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? WindowsCommand("ping -n 8 127.0.0.1 >nul")
            : UnixShell("sleep 8"));

        var waitTask = session.WaitForExitAsync();
        await Task.Delay(200);
        session.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await waitTask);
    }

    [Test]
    public async Task PtyDisposeDuringWriteInputThrowsObjectDisposed()
    {
        var session = Pty.Start(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? WindowsCommand("ping -n 8 127.0.0.1 >nul")
            : UnixShell("sleep 8"));

        session.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await session.WriteInputAsync("hello"));
    }

    [Test]
    public async Task PtyReadOutputAsyncConcurrentWithWaitForExitAsync()
    {
        const string marker = "lifecycle-concurrent-wait";

        await using var session = Pty.Start(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? WindowsCommand($"echo {marker}")
            : UnixShell($"printf {marker}"));

        var waitTask = session.WaitForExitAsync();
        var text = await ReadOutputTextAsync(session, marker);
        var exitCode = await waitTask;

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(text).Contains(marker);
    }

    [Test]
    public async Task PtyCancelReadDoesNotCancelWaitForExit()
    {
        using var readCts = new CancellationTokenSource();
        using var waitCts = new CancellationTokenSource();

        await using var session = Pty.Start(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? WindowsCommand("echo ready & set /p DUMMY=")
            : UnixShell("printf ready; sleep 8"));

        var waitTask = session.WaitForExitAsync(waitCts.Token);
        await using var reader = session.ReadOutputAsync(readCts.Token).GetAsyncEnumerator();
        await Assert.That(await reader.MoveNextAsync()).IsTrue();

        await readCts.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await reader.MoveNextAsync());

        waitCts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await waitTask);
        await Assert.That(session.HasExited).IsFalse();
    }

    [Test]
    public async Task PtyKillDuringReadOutputAsyncDrainsOutput()
    {
        const string marker = "lifecycle-kill-drain";

        await using var session = Pty.Start(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Spawn(Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe",
            ["/c", $"echo {marker} & ping -n 20 127.0.0.1 >nul"])
            : UnixShell($"printf '{marker}'; sleep 20"));

        using var output = new MemoryStream();
        var readTask = Task.Run(async () =>
        {
            await foreach (var chunk in session.ReadOutputAsync())
                await output.WriteAsync(chunk.Data);
        });

        await Task.Delay(300);
        session.Kill();
        await readTask;

        var text = Encoding.UTF8.GetString(output.GetBuffer().AsSpan(0, checked((int)output.Length)));
        await Assert.That(text).Contains(marker);
    }

    [Test]
    public async Task PtyWindowsSpawnAllowsImmediateWriteInput()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        if (!TryResolveWindowsPowerShell(out var powershell))
            return;

        await using var session = Pty.Start(Spawn(powershell,
        [
            "-NoLogo",
            "-NoProfile",
            "-Command",
            "$line = [Console]::In.ReadLine(); [Console]::Out.WriteLine('got:' + $line)"
        ]));

        await session.WriteInputAsync("immediate\r\n");
        var text = await ReadOutputTextAsync(session, "got:immediate");

        await Assert.That(text).Contains("got:immediate");
    }

    [Test]
    public async Task PtyWindowsSpawnAllowsImmediateResize()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        await using var session = Pty.Start(WindowsCommand("exit /b 0"));
        session.Resize(new(100, 30));
        await Assert.That(session.Size).IsEqualTo(new PtySize(100, 30));
    }

    [Test]
    public async Task PtyWindowsEmptyStdinSendEofDoesNotFailFast()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        await using var session = Pty.Start(WindowsCommand("exit /b 0"));
        session.SendEof();
        var exitCode = await session.WaitForExitAsync();

        await Assert.That(exitCode).IsEqualTo(0);
    }

    private static PtyStartInfo Spawn(string fileName, IReadOnlyList<string> arguments) =>
        new() { FileName = fileName, Arguments = arguments, Size = new(40, 8) };

    private static PtyStartInfo UnixShell(string command) => Spawn("sh", ["-c", command]);

    private static PtyStartInfo WindowsCommand(string command)
    {
        var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
        return Spawn(cmd, ["/c", command]);
    }

    private static bool ContainsEnvironmentLine(string text, string key)
    {
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.StartsWith(key + "=", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static async Task<string> ReadOutputTextAsync(PtySession session, string marker)
    {
        using var output = new MemoryStream();
        await foreach (var chunk in session.ReadOutputAsync())
        {
            await output.WriteAsync(chunk.Data);
            var text = Encoding.UTF8.GetString(output.GetBuffer().AsSpan(0, checked((int)output.Length)));
            if (text.Contains(marker, StringComparison.Ordinal))
                return text;
        }

        return Encoding.UTF8.GetString(output.GetBuffer().AsSpan(0, checked((int)output.Length)));
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
