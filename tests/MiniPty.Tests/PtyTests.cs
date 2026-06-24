using System.Runtime.InteropServices;
using System.Text;
using MiniPty.Capture;

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
            ? WindowsCommand("echo ready & ping -n 3 127.0.0.1 >nul")
            : UnixShell("printf ready; sleep 2"));

        using var cts = new CancellationTokenSource();
        await using var reader = session.ReadOutputAsync(cts.Token).GetAsyncEnumerator();
        await Assert.That(await reader.MoveNextAsync()).IsTrue();

        await cts.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await reader.MoveNextAsync());
        await Assert.That(session.HasExited).IsFalse();
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
    public async Task PtyEnvironmentOverlayOverridesAndInherits()
    {
        const string parentKey = "MINIPTY_TEST_PARENT_ENV";
        const string overlayKey = "MINIPTY_TEST_OVERLAY_ENV";
        var previous = Environment.GetEnvironmentVariable(parentKey);
        Environment.SetEnvironmentVariable(parentKey, "parent-value");
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (!TryResolveWindowsPowerShell(out var powershell))
                    return;

                var result = await PtyCapture.RunAsync(Spawn(powershell,
                    [
                        "-NoLogo",
                        "-NoProfile",
                        "-Command",
                        $"Write-Output ([Environment]::GetEnvironmentVariable('{parentKey}','Process')); Write-Output ([Environment]::GetEnvironmentVariable('{overlayKey}','Process'))"
                    ]) with
                    {
                        Environment = new Dictionary<string, string?> { [overlayKey] = "overlay-value" }
                    });

                await Assert.That(result.ExitCode).IsEqualTo(0);
                await Assert.That(result.Contains("parent-value")).IsTrue();
                await Assert.That(result.Contains("overlay-value")).IsTrue();
                return;
            }

            var unix = await PtyCapture.RunAsync(UnixShell($"printf '%s:%s' \"${parentKey}\" \"${overlayKey}\"") with
            {
                Environment = new Dictionary<string, string?> { [overlayKey] = "overlay-value" }
            });

            await Assert.That(unix.ExitCode).IsEqualTo(0);
            await Assert.That(unix.Contains("parent-value:overlay-value")).IsTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(parentKey, previous);
        }
    }

    [Test]
    public async Task PtyEnvironmentNullRemovesAndEmptySetsEmpty()
    {
        const string emptyKey = "MINIPTY_TEST_EMPTY_ENV";
        const string removeKey = "MINIPTY_TEST_REMOVE_ENV";
        var previousRemove = Environment.GetEnvironmentVariable(removeKey);
        Environment.SetEnvironmentVariable(removeKey, "remove-me");
        try
        {
            var environment = new Dictionary<string, string?> { [emptyKey] = string.Empty, [removeKey] = null };
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (!TryResolveWindowsPowerShell(out var powershell))
                    return;

                var result = await PtyCapture.RunAsync(Spawn(powershell,
                    [
                        "-NoLogo",
                        "-NoProfile",
                        "-Command",
                        $"$e=[Environment]::GetEnvironmentVariable('{emptyKey}','Process'); if ($null -eq $e) {{ 'EMPTY:MISSING' }} else {{ 'EMPTY:' + $e }}; $r=[Environment]::GetEnvironmentVariable('{removeKey}','Process'); if ($null -eq $r) {{ 'REMOVE:MISSING' }} else {{ 'REMOVE:' + $r }}"
                    ]) with
                    {
                        Environment = environment
                    });

                await Assert.That(result.ExitCode).IsEqualTo(0);
                await Assert.That(result.Contains("EMPTY:MISSING")).IsTrue();
                await Assert.That(result.Contains("REMOVE:MISSING")).IsTrue();
                return;
            }

            var unix = await PtyCapture.RunAsync(UnixShell(
                $"if [ \"${{{emptyKey}+x}}\" = x ]; then printf 'EMPTY:%s;' \"${emptyKey}\"; else printf 'EMPTY:MISSING;'; fi; if [ \"${{{removeKey}+x}}\" = x ]; then printf 'REMOVE:%s' \"${removeKey}\"; else printf 'REMOVE:MISSING'; fi") with
            {
                Environment = environment
            });

            await Assert.That(unix.ExitCode).IsEqualTo(0);
            await Assert.That(unix.Contains("EMPTY:;")).IsTrue();
            await Assert.That(unix.Contains("REMOVE:MISSING")).IsTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(removeKey, previousRemove);
        }
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
    public async Task PtyUnixDefaultTermIsAppliedWhenAbsent()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var previous = Environment.GetEnvironmentVariable("TERM");
        Environment.SetEnvironmentVariable("TERM", null);
        try
        {
            var result = await PtyCapture.RunAsync(UnixShell("printf '%s' \"$TERM\""));

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(result.Contains("xterm-256color")).IsTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("TERM", previous);
        }
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

    [Test]
    public async Task PtyUnixSanitizesInheritedTerminalSizeVariables()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var previousColumns = Environment.GetEnvironmentVariable("COLUMNS");
        var previousLines = Environment.GetEnvironmentVariable("LINES");
        Environment.SetEnvironmentVariable("COLUMNS", "999");
        Environment.SetEnvironmentVariable("LINES", "888");
        try
        {
            var result = await PtyCapture.RunAsync(UnixShell("if [ \"${COLUMNS+x}\" = x ]; then printf 'COLUMNS:%s;' \"$COLUMNS\"; else printf 'COLUMNS:MISSING;'; fi; if [ \"${LINES+x}\" = x ]; then printf 'LINES:%s' \"$LINES\"; else printf 'LINES:MISSING'; fi"));

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
            await File.WriteAllTextAsync(script, "printf path-overlay-shell-fallback");
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
    public async Task PtyWindowsEnvironmentOverlayIsCaseInsensitive()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || !TryResolveWindowsPowerShell(out var powershell))
            return;

        const string key = "MINIPTY_TEST_CASE_ENV";
        var previous = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, "parent-value");
        try
        {
            var result = await PtyCapture.RunAsync(Spawn(powershell,
                [
                    "-NoLogo",
                    "-NoProfile",
                    "-Command",
                    $"Write-Output ([Environment]::GetEnvironmentVariable('{key}','Process'))"
                ]) with
                {
                    Environment = new Dictionary<string, string?> { [key.ToLowerInvariant()] = "child-value" }
                });

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(result.Contains("child-value")).IsTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, previous);
        }
    }

    [Test]
    public async Task PtyEnvironmentRejectsInvalidKeysAndValues()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await using var session = Pty.Start(SpawnForValidation() with
            {
                Environment = new Dictionary<string, string?> { ["BAD=KEY"] = "value" }
            });
        });

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await using var session = Pty.Start(SpawnForValidation() with
            {
                Environment = new Dictionary<string, string?> { ["BAD"] = "bad\0value" }
            });
        });

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await using var session = Pty.Start(SpawnForValidation() with
            {
                TerminalName = "bad\0term"
            });
        });
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

    private static PtyStartInfo SpawnForValidation() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? WindowsCommand("exit /b 0")
        : Spawn("true", []);

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
