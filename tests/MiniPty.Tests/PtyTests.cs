using System.Diagnostics;
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

            await Assert.That(result.ExitCode).IsEqualTo(0);
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
        const string marker = "pty-stdin-eof-no-nl";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var sort = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sort.exe");
            var result = await PtyCapture.RunAsync(
                Spawn(sort, []),
                new PtyCaptureOptions { Completion = new() { Input = marker } });

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(result.Contains(marker)).IsTrue();
            return;
        }

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
            var sort = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sort.exe");
            var result = await PtyCapture.RunAsync(
                Spawn(sort, []),
                new PtyCaptureOptions { Completion = new() { Input = "line 1\r\nline 2\r\n" } });

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(result.Contains("line 1")).IsTrue();
            await Assert.That(result.Contains("line 2")).IsTrue();
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
        var exitCode = (await session.WaitForExitAsync()).ExitCode;

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(text).Contains(marker);
    }

    /// <summary>
    /// Bulk stdout on Windows ConPTY arrives whole, in chunks no larger than the output ring.
    /// </summary>
    /// <remarks>
    /// This used to assert that coalescing batched the 32 KiB into fewer than 40 chunks (about 13 on an idle
    /// desktop, against about 125 uncoalesced). The count is a measurement of the host, not of the reader: ConPTY's
    /// slices arrive further apart than the coalescing window on GitHub-hosted runners (42–94 chunks on x64 and
    /// ARM64) and on the same desktop under CPU oversubscription (63–123), so no bound separates a slow host from a
    /// reader that does not coalesce. The benchmarks track the chunking.
    /// </remarks>
    [Test]
    public async Task PtyReadOutputAsyncDeliversBulkStdoutOnWindows()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        if (!TryResolveWindowsPowerShell(out var powershell))
            return;

        await using var session = Pty.Start(Spawn(powershell,
            ["-NoLogo", "-NoProfile", "-Command", "[Console]::Out.Write(([string]::new('x',32768)))"]));

        var totalBytes = 0;
        var chunks = 0;
        var maxChunk = 0;
        await foreach (var chunk in session.ReadOutputAsync())
        {
            chunks++;
            totalBytes += chunk.Data.Length;
            if (chunk.Data.Length > maxChunk)
                maxChunk = chunk.Data.Length;
        }

        await Assert.That(totalBytes).IsGreaterThanOrEqualTo(32_768);
        await Assert.That(chunks).IsGreaterThan(0);
        await Assert.That(maxChunk).IsLessThanOrEqualTo(MiniPty.Internal.PtyOutputPump.Capacity);
    }

    /// <summary>
    /// Bulk output that piles up while the consumer is busy is handed out as few large chunks, whatever size each read
    /// has. The loop publishes each read as it completes, and a macOS read returns at most 1 KiB, so a consumer that keeps
    /// up gets about 32 chunks of 32 KiB there; this consumer is slower than the child on purpose.
    /// </summary>
    [Test]
    public async Task PtyReadOutputAsyncPreservesLargeChunksOnUnix()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        await using var session = Pty.Start(Spawn("head", ["-c", "32768", "/dev/zero"]));

        var totalBytes = 0;
        var chunks = 0;
        var maxChunk = 0;
        await using var output = session.ReadOutputAsync().GetAsyncEnumerator();
        while (true)
        {
            await Task.Delay(10);
            if (!await output.MoveNextAsync())
                break;

            chunks++;
            totalBytes += output.Current.Data.Length;
            if (output.Current.Data.Length > maxChunk)
                maxChunk = output.Current.Data.Length;
        }

        await Assert.That(totalBytes).IsEqualTo(32_768);
        await Assert.That(chunks).IsLessThanOrEqualTo(16);
        await Assert.That(maxChunk).IsGreaterThanOrEqualTo(4096);
    }

    /// <summary>
    /// Transport-pump completion must drain the full child stdout before closing ConPTY on Windows.
    /// </summary>
    [Test]
    public async Task PtyCompleteAsyncDrainsBulkStdout()
    {
        if (!TryCreateBulkStdout32KiB(out var startInfo))
            return;

        await using var session = Pty.Start(startInfo);
        var result = await session.CompleteAsync(new PtyCompleteOptions { DecodeOutput = false });

        await Assert.That(result.ExitCode).IsEqualTo(0);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            await Assert.That(result.Output.Length).IsGreaterThanOrEqualTo(32_768);
        else
            await Assert.That(result.Output.Length).IsEqualTo(32_768);
    }

    /// <summary>
    /// Capture shares completion orchestration with <see cref="PtySession.CompleteAsync"/>.
    /// </summary>
    [Test]
    public async Task PtyCaptureDrainsBulkStdout()
    {
        if (!TryCreateBulkStdout32KiB(out var startInfo))
            return;

        var result = await PtyCapture.RunAsync(
            startInfo,
            new PtyCaptureOptions { Completion = new PtyCompleteOptions { DecodeOutput = false } });

        await Assert.That(result.ExitCode).IsEqualTo(0);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            await Assert.That(result.Output.Length).IsGreaterThanOrEqualTo(32_768);
        else
            await Assert.That(result.Output.Length).IsEqualTo(32_768);
        await Assert.That(result.Chunks.Count).IsGreaterThan(0);
    }

    /// <summary>
    /// Windows one-shot completion should not consume the full <see cref="PtyCompleteOptions.OutputDrainGrace"/>
    /// budget for short echo output. Uses a large grace and a relative upper bound so slow child startup
    /// on CI runners does not false-fail while a regression that waits the full grace still does.
    /// </summary>
    [Test]
    public async Task PtyCompleteAsyncReturnsBeforeOutputDrainGraceOnWindows()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        await using var session = Pty.Start(WindowsCommand("echo pty-drain-latency"));

        var options = new PtyCompleteOptions
        {
            DecodeOutput = true,
            OutputDrainGrace = TimeSpan.FromSeconds(10),
        };
        var maxElapsed = options.OutputDrainGrace - TimeSpan.FromSeconds(2);

        var elapsed = Stopwatch.StartNew();
        var result = await session.CompleteAsync(options);
        elapsed.Stop();

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Contains("pty-drain-latency")).IsTrue();
        await Assert.That(elapsed.Elapsed).IsLessThan(maxElapsed);
    }

    /// <summary>
    /// Post-exit quiet detection must not truncate output across representative gap classes.
    /// </summary>
    [Test]
    [Arguments(50, "__GAP_HEAD__", "__GAP_TAIL__")]
    [Arguments(100, "__THRESHOLD_HEAD__", "__THRESHOLD_TAIL__")]
    [Arguments(150, "__LONG_GAP_HEAD__", "__LONG_GAP_TAIL__")]
    public async Task PtyCompleteAsyncRetainsIntermittentStdoutAcrossGapClassesOnWindows(
        int gapMilliseconds,
        string headMarker,
        string tailMarker)
    {
        if (!TryStartWindowsIntermittentStdout(gapMilliseconds, [headMarker, tailMarker], out var startInfo))
            return;

        await using var session = Pty.Start(startInfo);
        var result = await session.CompleteAsync(new PtyCompleteOptions { DecodeOutput = true });

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await AssertContainsAllMarkers(result, headMarker, tailMarker);
    }

    /// <summary>
    /// Multiple intermittent segments must all appear in merged one-shot output.
    /// </summary>
    [Test]
    public async Task PtyCompleteAsyncRetainsMultipleGapStdoutOnWindows()
    {
        if (!TryStartWindowsIntermittentStdout(50,
                ["__MULTI_A__", "__MULTI_B__", "__MULTI_C__"],
                out var startInfo))
            return;

        await using var session = Pty.Start(startInfo);
        var result = await session.CompleteAsync(new PtyCompleteOptions { DecodeOutput = true });

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await AssertContainsAllMarkers(result, "__MULTI_A__", "__MULTI_B__", "__MULTI_C__");
    }

    /// <summary>
    /// Capture shares transport-pump drain orchestration with <see cref="PtySession.CompleteAsync"/>
    /// and must retain output across representative gap classes.
    /// </summary>
    [Test]
    [Arguments(50, "__CAP_GAP_HEAD__", "__CAP_GAP_TAIL__")]
    [Arguments(100, "__CAP_THRESHOLD_HEAD__", "__CAP_THRESHOLD_TAIL__")]
    [Arguments(150, "__CAP_LONG_HEAD__", "__CAP_LONG_TAIL__")]
    public async Task PtyCaptureRetainsIntermittentStdoutAcrossGapClassesOnWindows(
        int gapMilliseconds,
        string headMarker,
        string tailMarker)
    {
        if (!TryStartWindowsIntermittentStdout(gapMilliseconds, [headMarker, tailMarker], out var startInfo))
            return;

        var result = await PtyCapture.RunAsync(
            startInfo,
            new PtyCaptureOptions { Completion = new PtyCompleteOptions { DecodeOutput = true } });

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await AssertContainsAllMarkers(result, headMarker, tailMarker);
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
        var exitCode = (await session.WaitForExitAsync()).ExitCode;

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
        var exitCode = (await session.WaitForExitAsync()).ExitCode;

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

        var exitCode = (await session.WaitForExitAsync()).ExitCode;

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

    /// <summary>
    /// Regression test: sustained large output (2 MiB) must drain without data loss through the bounded output buffer.
    /// </summary>
    [Test]
    public async Task PtyReadOutputAsyncDrainsLargeOutputWithoutDropping()
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

        var exitCode = (await session.WaitForExitAsync()).ExitCode;

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(length).IsGreaterThanOrEqualTo(minimumLength);
    }

    /// <summary>
    /// One-shot results keep multi-megabyte output whole, and the text decoded while it arrives matches the bytes.
    /// This much output spans hundreds of result segments, more than the segment pool keeps. On Unix every line holds
    /// a three-byte character, so characters split between reads and between segments.
    /// </summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OneShotResultsKeepMultiMegabyteOutput(bool capture)
    {
        const int Size = 4 * 1024 * 1024;

        PtyStartInfo startInfo;
        var windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        if (windows)
        {
            if (!TryResolveWindowsPowerShell(out var powershell))
                return;

            startInfo = Spawn(powershell, ["-NoLogo", "-NoProfile", "-Command", $"[Console]::Out.Write(('x' * {Size}))"]);
        }
        else
        {
            // "€\n" is four bytes, so the cut falls between lines.
            startInfo = UnixShell($"yes € | head -c {Size}");
        }

        var options = new PtyCompleteOptions { DecodeOutput = true };
        ReadOnlyMemory<byte> output;
        string text;
        int exitCode;
        if (capture)
        {
            var result = await PtyCapture.RunAsync(startInfo, new PtyCaptureOptions { Completion = options });
            (output, text, exitCode) = (result.Output, result.GetTextString(), result.ExitCode);
        }
        else
        {
            await using var session = Pty.Start(startInfo);
            var result = await session.CompleteAsync(options);
            (output, text, exitCode) = (result.Output, result.GetTextString(), result.ExitCode);
        }

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(text).IsEqualTo(Encoding.UTF8.GetString(output.Span));
        if (windows)
        {
            // ConPTY renders the line into rows and adds sequences of its own, so only the characters are counted.
            var count = 0;
            foreach (var c in text)
            {
                if (c == 'x')
                    count++;
            }

            await Assert.That(count).IsGreaterThanOrEqualTo(Size);
        }
        else
        {
            // The terminal turns each newline into CR LF. A macOS terminal writes CR CR LF instead when its 1 KiB output
            // queue fills right after the CR, because it puts the CR again when it retries the LF, so those are folded.
            if (OperatingSystem.IsMacOS())
                text = text.Replace("\r\r\n", "\r\n", StringComparison.Ordinal);

            await Assert.That(text).IsEqualTo(new StringBuilder().Insert(0, "€\r\n", Size / 4).ToString());
        }
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
    public async Task PtyWaitForExitAsyncReportsSignalOnUnix()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        await using var session = Pty.Start(UnixShell("kill -TERM $$"));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var status = await session.WaitForExitAsync(cts.Token);

        await Assert.That(status.ExitCode).IsEqualTo(143);
        await Assert.That(status.Signal).IsEqualTo(15);
        await Assert.That(status.NodePtyExitCode).IsEqualTo(0);
        // ExitCode compatibility pin: PtyExitStatus.ExitCode always equals PtySession.ExitCode.
        await Assert.That(session.ExitCode).IsEqualTo(143);
        await Assert.That(session.ExitStatus).IsEqualTo(status);
    }

    [Test]
    public async Task PtyWaitForExitAsyncHasNoSignalOnNormalExit()
    {
        await using var session = Pty.Start(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? WindowsCommand("exit /b 42")
            : UnixShell("exit 42"));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var status = await session.WaitForExitAsync(cts.Token);

        await Assert.That(status.ExitCode).IsEqualTo(42);
        await Assert.That(status.Signal).IsNull();
        await Assert.That(status.NodePtyExitCode).IsEqualTo(42);
        await Assert.That(session.ExitStatus).IsEqualTo(status);
    }

    [Test]
    public async Task PtyKillWithSignalTerminatesChildAndReportsSignalOnUnix()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        await using (var session = Pty.Start(UnixShell("IFS= read -r _")))
        {
            session.Kill(PtySignal.Terminate);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var status = await session.WaitForExitAsync(cts.Token);

            await Assert.That(status.ExitCode).IsEqualTo(143);
            await Assert.That(status.Signal).IsEqualTo(15);
        }

        await using (var session = Pty.Start(UnixShell("IFS= read -r _")))
        {
            session.Kill(PtySignal.Kill);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var status = await session.WaitForExitAsync(cts.Token);

            await Assert.That(status.ExitCode).IsEqualTo(137);
            await Assert.That(status.Signal).IsEqualTo(9);
        }
    }

    [Test]
    public async Task PtyKillWithSignalTerminatesChildOnWindows()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        await using var session = Pty.Start(WindowsCommand("set /p DUMMY="));
        session.Kill(PtySignal.Terminate);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var status = await session.WaitForExitAsync(cts.Token);

        // Windows terminate is advisory-signal: TerminateProcess(h, 1) and never a signal report.
        await Assert.That(status.ExitCode).IsEqualTo(1);
        await Assert.That(status.Signal).IsNull();
    }

    /// <summary>
    /// The reported signal 9 is what proves SIGKILL. A Kill() that sent SIGHUP instead would end the child with signal 1
    /// before its trap is installed, or leave it running after, and fail either way.
    /// </summary>
    [Test]
    public async Task PtyKillSendsSigkillOnUnix()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        await using var session = Pty.Start(UnixShell("trap '' HUP; IFS= read -r _"));
        session.Kill();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var status = await session.WaitForExitAsync(cts.Token);

        await Assert.That(status.ExitCode).IsEqualTo(137);
        await Assert.That(status.Signal).IsEqualTo(9);
    }

    [Test]
    public async Task PtyKillWithUndefinedSignalThrows()
    {
        await using var session = Pty.Start(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? WindowsCommand("set /p DUMMY=")
            : UnixShell("IFS= read -r _"));

        Assert.Throws<ArgumentOutOfRangeException>(() => session.Kill((PtySignal)9999));
        await Assert.That(session.HasExited).IsFalse();
    }

    [Test]
    public async Task PtyWaitForExitAsyncCancellationDoesNotKillChild()
    {
        await using var session = Pty.Start(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? WindowsCommand("set /p DUMMY=")
            : UnixShell("IFS= read -r _"));

        using var cts = new CancellationTokenSource();
        var waitTask = session.WaitForExitAsync(cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await waitTask);
        await Assert.That(session.HasExited).IsFalse();
    }

    /// <summary>
    /// Without a cancelable token every call returns the session's one exit task, so waiting repeatedly allocates
    /// nothing per call. Both calls come before exit, so the task cannot be shared merely because it completed.
    /// </summary>
    [Test]
    public async Task PtyWaitForExitAsyncWithoutATokenReturnsTheSameTask()
    {
        await using var session = Pty.Start(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? WindowsCommand("set /p DUMMY=")
            : UnixShell("IFS= read -r _"));

        var first = session.WaitForExitAsync();
        var second = session.WaitForExitAsync(CancellationToken.None);

        await Assert.That(ReferenceEquals(first, second)).IsTrue();
        await Assert.That(first.IsCompleted).IsFalse();
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

    // Retried under parallel CI due to occasional Unix exec/path overlay visibility races.
    [Test]
    [Retry(3)]
    public async Task PtyUnixPathLookupUsesOverlayAndFallsBackToShellForPlainScripts()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var tempRoot = CreateUnixExecTestDirectory();
        try
        {
            var script = Path.Combine(tempRoot, "minipty-plain-script");
            await File.WriteAllTextAsync(script, "printf path-overlay-shell-fallback\n");
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

    /// <summary>
    /// A script whose shebang names a missing interpreter fails to exec (<c>ENOENT</c>), as with <c>execvp</c>, and the
    /// start fails with that <c>errno</c>. It must not run under <c>sh</c> instead: the script was written for another
    /// interpreter.
    /// </summary>
    [Test]
    [Retry(3)]
    public async Task PtyUnixPathLookupFailsWhenShebangInterpreterMissing()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var tempRoot = CreateUnixExecTestDirectory();
        try
        {
            var script = Path.Combine(tempRoot, "minipty-bad-shebang-script");
            await File.WriteAllTextAsync(script, "#!/no-such-minipty-interpreter\nprintf bad-shebang-ran-under-sh\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var failure = await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => StartAndDispose(Spawn("minipty-bad-shebang-script", []) with
            {
                Environment = new Dictionary<string, string?> { ["PATH"] = tempRoot }
            }));

            await Assert.That(failure!.NativeErrorCode).IsEqualTo(UnixENOENT);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    /// <summary>
    /// A file without execute permission earlier on <c>PATH</c> is skipped (<c>EACCES</c>), and the executable of the
    /// same name in a later entry runs, as with <c>execvp</c>. Running the first file through <c>sh</c> would ignore its
    /// permission and stop the search there.
    /// </summary>
    [Test]
    [Retry(3)]
    public async Task PtyUnixPathLookupSkipsFilesWithoutExecutePermission()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var tempRoot = CreateUnixExecTestDirectory();
        try
        {
            var notExecutableDirectory = Directory.CreateDirectory(Path.Combine(tempRoot, "not-executable")).FullName;
            var executableDirectory = Directory.CreateDirectory(Path.Combine(tempRoot, "executable")).FullName;

            var notExecutable = Path.Combine(notExecutableDirectory, "minipty-lookup-target");
            await File.WriteAllTextAsync(notExecutable, "printf not-executable-entry-ran\n");
            File.SetUnixFileMode(notExecutable, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            var executable = Path.Combine(executableDirectory, "minipty-lookup-target");
            await File.WriteAllTextAsync(executable, "printf executable-entry-ran\n");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var result = await PtyCapture.RunAsync(Spawn("minipty-lookup-target", []) with
            {
                Environment = new Dictionary<string, string?> { ["PATH"] = notExecutableDirectory + ":" + executableDirectory }
            });

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(result.Contains("executable-entry-ran")).IsTrue();
            await Assert.That(result.Contains("not-executable-entry-ran")).IsFalse();
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    /// <summary>
    /// A file without execute permission does not start, whether it is the only match on <c>PATH</c> or named by its full
    /// path: the start fails with <c>EACCES</c>, as <c>execvp</c> reports it.
    /// </summary>
    [Test]
    [Retry(3)]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PtyUnixFileWithoutExecutePermissionDoesNotStart(bool fullPath)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var tempRoot = CreateUnixExecTestDirectory();
        try
        {
            var script = Path.Combine(tempRoot, "minipty-not-executable");
            await File.WriteAllTextAsync(script, "printf not-executable-file-ran\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            var failure = await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => StartAndDispose(Spawn(fullPath ? script : "minipty-not-executable", []) with
            {
                Environment = new Dictionary<string, string?> { ["PATH"] = tempRoot }
            }));

            await Assert.That(failure!.NativeErrorCode).IsEqualTo(UnixEACCES);
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

    /// <summary>
    /// Empty <c>PATH</c> components resolve as the current directory after <c>chdir</c>.
    /// Retried under parallel CI: <c>forkpty</c> on Linux arm can still race exec payload visibility
    /// despite parent hold-until-dispose (see platform_support.md).
    /// </summary>
    [Test]
    [Retry(3)]
    public async Task PtyUnixPathLookupTreatsEmptyPathEntriesAsCurrentDirectory()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var tempRoot = CreateUnixExecTestDirectory();
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
    public async Task PtyUnixWorkingDirectoryVisibleToChild()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var tempRoot = Path.Combine(Path.GetTempPath(), "minipty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var result = await PtyCapture.RunAsync(Spawn("sh", ["-c", "pwd"]) with
            {
                WorkingDirectory = tempRoot,
            });

            await Assert.That(result.ExitCode).IsEqualTo(0);
            var pwd = result.GetTextString().Trim();
            var leaf = Path.GetFileName(tempRoot);
            await Assert.That(pwd.EndsWith('/' + leaf, StringComparison.Ordinal)).IsTrue();
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Test]
    public async Task PtyUnixInternalCwdKeyNotVisibleToChild()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var tempRoot = Path.Combine(Path.GetTempPath(), "minipty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var result = await PtyCapture.RunAsync(Spawn("sh", ["-c", "if [ -n \"$MINIPTY_CWD\" ]; then echo LEAKED; else echo CLEAN; fi"]) with
            {
                WorkingDirectory = tempRoot,
            });

            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(result.Contains("CLEAN")).IsTrue();
            await Assert.That(result.Contains("LEAKED")).IsFalse();
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    // Retried under parallel CI due to occasional spawn/exec timing flakiness.
    [Test]
    [Retry(3)]
    public async Task PtyUnixParallelSpawnCompletes()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        // Burst fewer concurrent spawns so this test stays stable when CI runs the full suite in parallel.
        const int batchSize = 4;
        const int batches = 4;
        for (var batch = 0; batch < batches; batch++)
        {
            var tasks = new Task<PtyCaptureResult>[batchSize];
            for (var i = 0; i < batchSize; i++)
                tasks[i] = PtyCapture.RunAsync(Spawn("/usr/bin/printf", ["parallel-ok"]));

            var results = await Task.WhenAll(tasks);
            for (var i = 0; i < results.Length; i++)
            {
                await Assert.That(results[i].ExitCode).IsEqualTo(0);
                await Assert.That(results[i].ContainsUtf8("parallel-ok")).IsTrue();
            }
        }
    }

    [Test]
    [Retry(3)]
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
            using var session = Pty.Start(Spawn(cmd, ["/c", "set /p DUMMY="]));

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                session.CompleteAsync(new PtyCompleteOptions { KillOnCancellation = true }, cts.Token));
            await WaitUntilExited(session);
            await Assert.That(session.HasExited).IsTrue();
            return;
        }

        using var unixSession = Pty.Start(Spawn("sh", ["-c", "IFS= read -r _"]));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            unixSession.CompleteAsync(new PtyCompleteOptions { KillOnCancellation = true }, cts.Token));
        await WaitUntilExited(unixSession);
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
    public async Task PtyResizeAcceptsPixelDimensions()
    {
        using var session = Pty.Start(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? WindowsCommand("set /p DUMMY=")
            : UnixShell("IFS= read -r _"));

        session.Resize(new PtySize(90, 25), new PtyPixelSize(900, 500));

        await Assert.That(session.Size).IsEqualTo(new PtySize(90, 25));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            session.Resize(new PtySize(90, 25), new PtyPixelSize(-1, 500)));
    }

    /// <summary>
    /// Wide windows exceed 512 cells; a clamp there made the frontend grid and the PTY disagree. Sizes are now
    /// bounded only by what the platform API encodes, and out-of-range values clamp rather than throw.
    /// </summary>
    [Test]
    public async Task PtyResizeAppliesSizesUpToThePlatformLimit()
    {
        using var session = Pty.Start(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? WindowsCommand("set /p DUMMY=")
            : UnixShell("IFS= read -r _"));
        int max = OperatingSystem.IsWindows() ? short.MaxValue : ushort.MaxValue;

        session.Resize(new PtySize(600, 200));
        await Assert.That(session.Size).IsEqualTo(new PtySize(600, 200));

        session.Resize(new PtySize(max, max));
        await Assert.That(session.Size).IsEqualTo(new PtySize(max, max));

        session.Resize(new PtySize(max + 1, 100_000));
        await Assert.That(session.Size).IsEqualTo(new PtySize(max, max));

        // A window before layout can report zero cells; that must not fault a size-changed handler.
        session.Resize(new PtySize(0, -5));
        await Assert.That(session.Size).IsEqualTo(new PtySize(1, 1));
    }

    [Test]
    public async Task PtyStartAppliesSizesUpToThePlatformLimit()
    {
        int max = OperatingSystem.IsWindows() ? short.MaxValue : ushort.MaxValue;
        var wide = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? WindowsCommand("set /p DUMMY=")
            : UnixShell("IFS= read -r _");

        using var wideSession = Pty.Start(wide with { Size = new PtySize(600, 200) });
        await Assert.That(wideSession.Size).IsEqualTo(new PtySize(600, 200));

        using var clampedSession = Pty.Start(wide with { Size = new PtySize(100_000, 0) });
        await Assert.That(clampedSession.Size).IsEqualTo(new PtySize(max, 1));
    }

    /// <summary>The child, not only <c>Size</c>, must observe a grid wider than the former 512-cell cap.</summary>
    [Test]
    public async Task PtyChildSeesWideResizedSize()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (!TryResolveWindowsPowerShell(out var powershell))
                return;

            await using var session = Pty.Start(
                Spawn(powershell, ["-NoLogo", "-NoProfile", "-Command", "[Console]::In.ReadLine() | Out-Null; $s = $Host.UI.RawUI.WindowSize; Write-Output (\"{0} {1}\" -f $s.Width, $s.Height); exit 0"]));
            session.Resize(new(600, 200));
            var result = await session.CompleteAsync(new PtyCompleteOptions { Input = "go\r\n" });

            // Exited once with 5 on windows-11-arm (not reproduced on x64, idle or loaded); the output says why next time.
            var output = Escape(result.GetTextString());
            await Assert.That(result.ExitCode).IsEqualTo(0).Because("the output was: " + output);
            await Assert.That(result.Contains("600 200")).IsTrue().Because("the output was: " + output);
            return;
        }

        await using var unixSession = Pty.Start(
            UnixShell("read _; set -- $(stty size); printf 'SIZE:%s:%s\\n' \"$1\" \"$2\""));
        unixSession.Resize(new(600, 200));
        var unixResult = await unixSession.CompleteAsync(new PtyCompleteOptions { Input = "go\n" });

        await Assert.That(unixResult.ExitCode).IsEqualTo(0);
        await Assert.That(unixResult.Contains("SIZE:200:600")).IsTrue();

        static string Escape(string text) =>
            text.Replace("\u001b", "\\e", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal);
    }

    /// <summary>
    /// A resize after the child exits, while the transport is still open, applies the size. On Windows a grandchild keeps
    /// the console host, and with it the pseudo-console, after the child exits. Without one, a host released at spawn
    /// (Windows 11 24H2 and later, and the out-of-band ConPTY) ends with its last client 2–5 ms after the child, and a
    /// resize after that is skipped, as exit tolerance allows, before the output pipe has reported the end: of 20 resizes
    /// 5 ms after the exit, none applied. A loaded full run delayed this thread that long now and then.
    /// </summary>
    [Test]
    public async Task PtyResizeAfterExitWithOpenTransportDoesNotThrow()
    {
        using var session = Pty.Start(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? WindowsCommand("start /b ping -n 6 127.0.0.1 >nul & exit /b 0")
            : UnixShell("exit 0"));
        var initial = session.Size;

        // HasExited observes exit without closing the transport. The post-exit drain closes it after 100 ms of quiet, so
        // the poll spins rather than sleeping: a timer wait on a loaded runner outlasted that and found it closed.
        var deadline = Environment.TickCount64 + 15_000;
        var spinner = new SpinWait();
        while (!session.HasExited)
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException("The child did not exit.");

            spinner.SpinOnce(sleep1Threshold: -1);
        }

        session.Resize(new PtySize(100, 30));
        // Read after the resize returned: the flag is set before the close starts, so a clear flag means the resize ran
        // while the transport was open.
        var closeStarted = session.OutputPump.IsCloseRequested;

        if (OperatingSystem.IsWindows())
        {
            // The grandchild keeps the host alive, so the size is applied. A runner that kept this thread off a core past
            // the drain's quiet period closed the pseudo-console first, and the resize is then the documented no-op that
            // PtyResizeAfterTransportClosedIsNoOp covers.
            if (!closeStarted)
                await Assert.That(session.Size).IsEqualTo(new PtySize(100, 30));
            else
                await Assert.That(session.Size == new PtySize(100, 30) || session.Size == initial).IsTrue();
            return;
        }

        // A Unix platform that rejects the ioctl once the slave is gone leaves Size unchanged instead of throwing.
        await Assert.That(session.Size == new PtySize(100, 30) || session.Size == initial).IsTrue();
    }

    [Test]
    public async Task PtyResizeAfterTransportClosedIsNoOp()
    {
        using var session = Pty.Start(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? WindowsCommand("exit /b 0")
            : UnixShell("exit 0"));
        var initial = session.Size;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await session.WaitForExitAsync(cts.Token);
        // Post-exit drain closes the transport; close it directly so every platform reaches that state
        // deterministically (Unix PTYs usually report EOF without needing the forced close).
        session.CloseOutputTransport();

        session.Resize(new PtySize(100, 30));
        session.Resize(new PtySize(100, 30), new PtyPixelSize(900, 500));

        await Assert.That(session.Size).IsEqualTo(initial);
        // Argument validation still precedes the exit state.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            session.Resize(new PtySize(100, 30), new PtyPixelSize(-1, 500)));
    }

    [Test]
    public async Task PtyResizeAfterDisposeThrowsObjectDisposed()
    {
        var session = Pty.Start(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? WindowsCommand("set /p DUMMY=")
            : UnixShell("IFS= read -r _"));
        session.Dispose();

        Assert.Throws<ObjectDisposedException>(() => session.Resize(new PtySize(100, 30)));
        Assert.Throws<ObjectDisposedException>(() =>
            session.Resize(new PtySize(100, 30), new PtyPixelSize(900, 500)));
        await Task.CompletedTask;
    }

    [Test]
    public async Task PtyCommandLineRejectsIncompatibleUsage()
    {
        var mixed = new PtyStartInfo
        {
            FileName = "unused",
            Arguments = ["argument"],
            CommandLine = "raw",
        };
        Assert.Throws<ArgumentException>(() => Pty.Start(mixed));

        if (!OperatingSystem.IsWindows())
        {
            Assert.Throws<PlatformNotSupportedException>(() => Pty.Start(new PtyStartInfo
            {
                FileName = "sh",
                CommandLine = "-c true",
            }));
        }

        await Task.CompletedTask;
    }

    [Test]
    public async Task PtyWindowsCommandLineRunsPreEscapedText()
    {
        if (!OperatingSystem.IsWindows())
            return;

        await using var session = Pty.Start(new PtyStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe",
            CommandLine = "/c echo RAW_COMMAND_LINE_MARKER",
        });
        var result = await session.CompleteAsync();

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Contains("RAW_COMMAND_LINE_MARKER")).IsTrue();
    }

    [Test]
    public async Task PtyActiveProcessNameTracksUnixForegroundProcess()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var session = Pty.Start(Spawn("sh", []));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await session.WriteInputAsync("sleep 5\n", cancellationToken: cts.Token);

        string? processName;
        do
        {
            processName = session.ActiveProcessName;
            if (processName == "sleep")
                break;
            await Task.Delay(20, cts.Token);
        }
        while (true);

        await Assert.That(processName).IsEqualTo("sleep");
        session.Kill(PtySignal.Kill);
    }

    [Test]
    public async Task PtyActiveProcessNameTracksPipelineAfterGroupLeaderExits()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var session = Pty.Start(Spawn("sh", []));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await session.WriteInputAsync("sh -c 'exit 0' | sleep 5\n", cancellationToken: cts.Token);
        await Task.Delay(200, cts.Token);

        string? processName;
        do
        {
            processName = session.ActiveProcessName;
            if (processName == "sleep")
                break;
            await Task.Delay(20, cts.Token);
        }
        while (true);

        await Assert.That(processName).IsEqualTo("sleep");
        session.Kill(PtySignal.Kill);
    }

    [Test]
    public async Task PtyChildSeesResizedSize()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (!TryResolveWindowsPowerShell(out var powershell))
                return;

            // Block on stdin until after Resize so a fast runner cannot query size before ConPTY applies it.
            await using var session = Pty.Start(
                Spawn(powershell, ["-NoLogo", "-NoProfile", "-Command", "[Console]::In.ReadLine() | Out-Null; $s = $Host.UI.RawUI.WindowSize; Write-Output (\"{0} {1}\" -f $s.Width, $s.Height); exit 0"]));
            session.Resize(new(100, 30));
            var result = await session.CompleteAsync(new PtyCompleteOptions { Input = "go\r\n" });

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
            ? WindowsCommand("set /p DUMMY=")
            : UnixShell("IFS= read -r _"));

        using var cts = new CancellationTokenSource();
        var waitTask = session.WaitForExitAsync();
        // A cancelable token takes a separate path, a wait over the exit task, which must fail the same way.
        var cancelableWaitTask = session.WaitForExitAsync(cts.Token);
        await Task.Delay(50);
        session.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await waitTask);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await cancelableWaitTask);
    }

    [Test]
    public async Task PtyDisposeDuringWriteInputThrowsObjectDisposed()
    {
        var session = Pty.Start(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? WindowsCommand("set /p DUMMY=")
            : UnixShell("IFS= read -r _"));

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
        var status = await waitTask;

        await Assert.That(status.ExitCode).IsEqualTo(0);
        await Assert.That(text).Contains(marker);
    }

    /// <summary>
    /// Output is read from spawn into the session's buffer, so what a child writes before any consumer attaches
    /// survives the child's exit and the post-exit transport close, and a reader started afterwards gets all of it.
    /// </summary>
    [Test]
    public async Task PtyOutputWrittenBeforeAnyReaderIsDeliveredAfterExitAndClose()
    {
        const string marker = "lifecycle-read-after-end";

        await using var session = Pty.Start(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? WindowsCommand($"echo {marker}")
            : UnixShell($"printf {marker}"));

        await WaitUntilOutputSettled(session);
        var text = await ReadAllOutputTextAsync(session).WaitAsync(TimeSpan.FromSeconds(15));

        await Assert.That(text).Contains(marker);
    }

    /// <summary>
    /// One-shot completion attached after the child exited and the transport closed takes the buffered output, and
    /// what the closed transport still holds. Whether output has fully ended by then depends on how many reads ConPTY's
    /// frames took (see <see cref="WaitUntilOutputSettled"/>); the attach after a finished pump, which finishes the
    /// result on the attaching thread, is covered deterministically by
    /// <c>PtyOutputPumpTests.CompleteAsyncAfterOutputEndedTakesTheBufferedOutput</c>.
    /// </summary>
    [Test]
    public async Task PtyCompleteAsyncAfterExitAndCloseReturnsBufferedOutput()
    {
        const string marker = "lifecycle-complete-after-end";

        await using var session = Pty.Start(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? WindowsCommand($"echo {marker}")
            : UnixShell($"printf {marker}"));

        await WaitUntilOutputSettled(session);
        var result = await session.CompleteAsync(new PtyCompleteOptions { DecodeOutput = true })
            .WaitAsync(TimeSpan.FromSeconds(15));

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Contains(marker)).IsTrue();
    }

    /// <summary>
    /// The blocking <c>Output.Read</c> waits for output that arrives later and copies a slot across several small
    /// reads, then returns 0 at end of output.
    /// </summary>
    [Test]
    public async Task PtyOutputSynchronousReadWaitsForOutputAndReachesEnd()
    {
        const string marker = "lifecycle-sync-read";

        await using var session = Pty.Start(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? WindowsCommand($"ping -n 2 127.0.0.1 >nul & echo {marker}")
            : UnixShell($"sleep 0.3; printf {marker}"));

        var text = await Task.Run(() =>
        {
            using var output = new MemoryStream();
            var buffer = new byte[16];
            int read;
            while ((read = session.Output.Read(buffer, 0, buffer.Length)) > 0)
                output.Write(buffer, 0, read);

            return Encoding.UTF8.GetString(output.GetBuffer().AsSpan(0, checked((int)output.Length)));
        }).WaitAsync(TimeSpan.FromSeconds(15));

        await Assert.That(text).Contains(marker);
    }

    [Test]
    public async Task PtyCancelReadDoesNotCancelWaitForExit()
    {
        using var readCts = new CancellationTokenSource();
        using var waitCts = new CancellationTokenSource();

        await using var session = Pty.Start(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? WindowsCommand("echo ready & set /p DUMMY=")
            : UnixShell("printf ready; IFS= read -r _"));

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
            ["/c", $"echo {marker} & set /p DUMMY="])
            : UnixShell($"printf '{marker}'; IFS= read -r _"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var output = new MemoryStream();
        await using var reader = session.ReadOutputAsync(cts.Token).GetAsyncEnumerator();

        // Synchronize on actual child output instead of a fixed delay. Under loaded CI the reader
        // and fork/exec may not run within an arbitrary timeout, allowing Kill to win before printf.
        while (output.GetBuffer().AsSpan(0, checked((int)output.Length)).IndexOf("lifecycle-kill-drain"u8) < 0)
        {
            await Assert.That(await reader.MoveNextAsync()).IsTrue();
            output.Write(reader.Current.Data.Span);
        }

        session.Kill();
        while (await reader.MoveNextAsync())
            output.Write(reader.Current.Data.Span);

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

    /// <summary>
    /// ConPTY makes inheritable duplicates of its pipe ends while it starts its console host inside
    /// <c>Pty.Start</c>. A process the host application starts with handle inheritance in that window inherits
    /// ConPTY's output pipe and keeps it open after the pseudo-console closes. End of output must not wait for that
    /// process (Post-exit drain in <c>.github/docs/references/pty_crossplatform.md</c>). The spawner threads
    /// therefore start processes around <c>Pty.Start</c>, and a session in which no spawn lands in the window passes
    /// without a held-open pipe.
    /// </summary>
    /// <remarks>
    /// Timed from the child's exit, not from <c>Pty.Start</c>: under CPU oversubscription <c>cmd /c echo</c> alone took
    /// up to 3.2 s to exit, before and after any change to end of output, while output ended 0.2–0.6 s after exit.
    /// </remarks>
    [Test]
    [NotInParallel]
    public async Task PtyWindowsOutputEndsWhileAnotherProcessHoldsTheOutputPipe()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        using var stop = new CancellationTokenSource();
        using var spawnGate = new ManualResetEventSlim();
        var spawned = new System.Collections.Concurrent.ConcurrentBag<Process>();
        // Dedicated threads: pool workers blocked here would starve the consumer's own continuations.
        var spawners = new Thread[4];
        for (var i = 0; i < spawners.Length; i++)
        {
            spawners[i] = new Thread(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        spawnGate.Wait(stop.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // An exception on a dedicated thread would take the test host down.
                        return;
                    }

                    // Process.Start always inherits handles. Redirected stdio makes the child hold the pipes it got.
                    // About 10 s, so a held pipe still shows seconds after a slow exit; each session kills them.
                    spawned.Add(Process.Start(new ProcessStartInfo("ping.exe", "-n 11 127.0.0.1")
                    {
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    })!);
                }
            })
            {
                IsBackground = true,
            };
            spawners[i].Start();
        }

        try
        {
            for (var i = 0; i < 8; i++)
            {
                PtySession session;
                spawnGate.Set();
                try
                {
                    session = Pty.Start(WindowsCommand("echo hi"));
                }
                finally
                {
                    spawnGate.Reset();
                }

                double afterExitMs;
                await using (session)
                {
                    // Stamped when the exit wait completes, not when it is awaited after the end of output.
                    var exitedAt = session.WaitForExitAsync().ContinueWith(static _ => Stopwatch.GetTimestamp(), TaskScheduler.Default);
                    await foreach (var _ in session.ReadOutputAsync())
                    {
                    }

                    var endedAt = Stopwatch.GetTimestamp();
                    afterExitMs = Stopwatch.GetElapsedTime(await exitedAt.WaitAsync(TimeSpan.FromSeconds(30)), endedAt).TotalMilliseconds;
                }

                KillSpawned(spawned);

                // Exit, 100 ms quiet, close, 100 ms quiet, end: well under a second after exit even on a loaded
                // machine. A leaked handle holds end of output until the ping child exits, about 10 s after it started.
                await Assert.That(afterExitMs).IsLessThan(2000);
            }
        }
        finally
        {
            stop.Cancel();
            foreach (var spawner in spawners)
                spawner.Join();

            KillSpawned(spawned);
        }

        // Kill, not Kill(entireProcessTree: true): a ping's only child is its console host, which exits with it, and the
        // tree kill enumerates and opens every process on the machine for each call. Under CPU oversubscription that
        // took up to 8 s per ping, and one run of this test spent 10 minutes here.
        static void KillSpawned(System.Collections.Concurrent.ConcurrentBag<Process> spawned)
        {
            while (spawned.TryTake(out var process))
            {
                try
                {
                    process.Kill();
                }
                catch (InvalidOperationException)
                {
                    // Already exited.
                }

                process.Dispose();
            }
        }
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
        var exitCode = (await session.WaitForExitAsync()).ExitCode;

        await Assert.That(exitCode).IsEqualTo(0);
    }

    [Test]
    public async Task PtyMacOsShScriptEmptySendEofCompletes()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        // posix_spawn + sh -c can still be running when the first wait poll ends; empty SendEof must
        // not deliver EOT early enough to leave /bin/sh waiting on stdin. Use a script that exits
        // without writing to stdout — printf would block until the master reads the PTY output.
        await using var session = Pty.Start(UnixShell("exit 0"));
        session.SendEof();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var exitCode = (await session.WaitForExitAsync(cts.Token)).ExitCode;

        await Assert.That(exitCode).IsEqualTo(0);
    }

    private static PtyStartInfo Spawn(string fileName, IReadOnlyList<string> arguments) =>
        new() { FileName = fileName, Arguments = arguments, Size = new(40, 8) };

    private static PtyStartInfo UnixShell(string command) => Spawn("sh", ["-c", command]);

    private static bool TryStartWindowsIntermittentStdout(
        int gapMilliseconds,
        string[] markers,
        out PtyStartInfo startInfo)
    {
        startInfo = default!;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;

        if (!TryResolveWindowsPowerShell(out var powershell))
            return false;

        if (markers.Length == 0)
            return false;

        startInfo = Spawn(powershell,
            ["-NoLogo", "-NoProfile", "-Command", BuildWindowsIntermittentStdoutCommand(gapMilliseconds, markers)]);
        return true;
    }

    private static string BuildWindowsIntermittentStdoutCommand(int gapMilliseconds, string[] markers)
    {
        var command = $"Write-Output '{markers[0]}'";
        for (var i = 1; i < markers.Length; i++)
            command += $"; Start-Sleep -Milliseconds {gapMilliseconds}; Write-Output '{markers[i]}'";

        return command;
    }

    private static async Task AssertContainsAllMarkers(PtyResult result, params string[] markers)
    {
        foreach (var marker in markers)
            await Assert.That(result.Contains(marker)).IsTrue();
    }

    private static async Task AssertContainsAllMarkers(PtyCaptureResult result, params string[] markers)
    {
        foreach (var marker in markers)
            await Assert.That(result.Contains(marker)).IsTrue();
    }

    private static bool TryCreateBulkStdout32KiB(out PtyStartInfo startInfo)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (!TryResolveWindowsPowerShell(out var powershell))
            {
                startInfo = default!;
                return false;
            }

            startInfo = Spawn(powershell,
                ["-NoLogo", "-NoProfile", "-Command", "[Console]::Out.Write(([string]::new('x',32768)))"]);
            return true;
        }

        startInfo = Spawn("head", ["-c", "32768", "/dev/zero"]);
        return true;
    }

    // errno values shared by Linux, macOS, and FreeBSD.
    private const int UnixENOENT = 2;
    private const int UnixEACCES = 13;

    private static Task StartAndDispose(PtyStartInfo startInfo)
    {
        Pty.Start(startInfo).Dispose();
        return Task.CompletedTask;
    }

    /// <summary>GitHub-hosted /tmp may be <c>noexec</c>; keep exec-test fixtures under the test output directory.</summary>
    private static string CreateUnixExecTestDirectory()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "minipty-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

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

    private static async Task<string> ReadAllOutputTextAsync(PtySession session)
    {
        using var output = new MemoryStream();
        await foreach (var chunk in session.ReadOutputAsync())
            await output.WriteAsync(chunk.Data);

        return Encoding.UTF8.GetString(output.GetBuffer().AsSpan(0, checked((int)output.Length)));
    }

    /// <summary>
    /// Waits until the child has exited and the post-exit drain has closed the transport, with the reader either finished
    /// or parked on a full ring. Nothing more reaches the session after that until a consumer reads.
    /// </summary>
    /// <remarks>
    /// Output need not have ended. When output nobody reads fills the ring, the frame ConPTY flushes after the close waits
    /// in the pipe, and the reader waits for a consumer to free space
    /// (<c>PtyOutputPumpTests.FramesThatFillTheRingAfterTheCloseReachALateReader</c>). With a ring of four slots, the few
    /// small frames of a single <c>echo</c> filled it under CPU contention: waiting for end of output timed out at 10 s in
    /// 3 of 10 full-suite runs pinned to two cores, with the reader parked on its fourth read. The ring now holds bytes,
    /// so such frames no longer fill it (<c>PtyOutputPumpTests.SmallFramesDoNotFillTheRing</c>), but a larger output
    /// still can.
    /// </remarks>
    private static async Task WaitUntilOutputSettled(PtySession session)
    {
        await session.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        var pump = session.OutputPump;
        for (var attempt = 0; attempt < 500 && !IsSettled(pump); attempt++)
            await Task.Delay(20);

        await Assert.That(IsSettled(pump)).IsTrue();

        static bool IsSettled(MiniPty.Internal.PtyOutputPump pump) =>
            pump.IsProducerFinished || (pump.IsCloseRequested && pump.IsWaitingForFreeSpace);
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
