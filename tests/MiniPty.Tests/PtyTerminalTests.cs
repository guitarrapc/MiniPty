using System.Runtime.InteropServices;
using System.Text;
using MiniPty.Terminal;

namespace MiniPty.Tests;

public sealed class PtyTerminalTests
{
    [Test]
    public async Task PtyTerminalDeliversOutputBeforeCompletion()
    {
        var buffer = new MemoryStream();
        var handlerCompletedBeforeExit = true;
        Task<PtyExitStatus>? completionTask = null;

        var terminal = PtyTerminal.Start(EchoMarkerChild("TERMINAL_MARKER"), new PtyTerminalOptions
        {
            Output = (data, _) =>
            {
                if (completionTask?.IsCompleted == true)
                    handlerCompletedBeforeExit = false;
                buffer.Write(data.Span);
                return ValueTask.CompletedTask;
            },
        });
        completionTask = terminal.Completion;

        await using (terminal)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var status = await completionTask.WaitAsync(cts.Token);

            await Assert.That(status.ExitCode).IsEqualTo(0);
            await Assert.That(Encoding.UTF8.GetString(buffer.ToArray())).Contains("TERMINAL_MARKER");
            await Assert.That(handlerCompletedBeforeExit).IsTrue();
        }
    }

    [Test]
    public async Task PtyTerminalSlowHandlerDoesNotDropOutput()
    {
        var buffer = new MemoryStream();

        await using var terminal = PtyTerminal.Start(EchoMarkerChild("SLOW_HANDLER_MARKER"), new PtyTerminalOptions
        {
            Output = async (data, ct) =>
            {
                await Task.Delay(20, ct);
                buffer.Write(data.Span);
            },
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var status = await terminal.Completion.WaitAsync(cts.Token);

        await Assert.That(status.ExitCode).IsEqualTo(0);
        await Assert.That(Encoding.UTF8.GetString(buffer.ToArray())).Contains("SLOW_HANDLER_MARKER");
    }

    [Test]
    public async Task PtyTerminalPauseStopsDeliveryAndResumeRecovers()
    {
        var delivered = 0L;
        var pausedOnFirstChunk = 0;
        var firstHandlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstHandlerRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminal = PtyTerminal.Start(PauseFlowTestChild(), new PtyTerminalOptions
        {
            Output = async (data, ct) =>
            {
                Interlocked.Add(ref delivered, data.Length);
                if (Interlocked.CompareExchange(ref pausedOnFirstChunk, 1, 0) == 0)
                {
                    firstHandlerEntered.TrySetResult();
                    await firstHandlerRelease.Task.WaitAsync(ct);
                }
            },
        });

        await using (terminal)
        {
            using var startCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await firstHandlerEntered.Task.WaitAsync(startCts.Token);
            terminal.Pause();
            firstHandlerRelease.TrySetResult();

            // Allow at most the single in-flight chunk to land, then verify no further delivery.
            await Task.Delay(200);
            var afterPause = Interlocked.Read(ref delivered);
            await Task.Delay(300);
            await Assert.That(Interlocked.Read(ref delivered)).IsEqualTo(afterPause);
            await Assert.That(afterPause).IsGreaterThan(0);
            await Assert.That(terminal.HasExited).IsFalse();

            terminal.Resume();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var status = await terminal.Completion.WaitAsync(cts.Token);

            await Assert.That(status.ExitCode).IsEqualTo(0);
            await Assert.That(Interlocked.Read(ref delivered)).IsGreaterThan(afterPause);
        }
    }

    [Test]
    public async Task PtyTerminalResizeAfterCompletionIsNoOp()
    {
        await using var terminal = PtyTerminal.Start(EchoMarkerChild("RESIZE_AFTER_EXIT"), new PtyTerminalOptions
        {
            Output = static (_, _) => ValueTask.CompletedTask,
        });
        await terminal.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        // A frontend size change that arrives after exit but before the tab is torn down.
        terminal.Resize(new PtySize(100, 40));
        terminal.Resize(new PtySize(100, 40), new PtyPixelSize(1000, 800));

        // Windows closes the pseudo-console before Completion resolves, so the size cannot change.
        // Unix may still have the master open, where applying the size is equally harmless.
        if (OperatingSystem.IsWindows())
            await Assert.That(terminal.Size).IsEqualTo(new PtySize(80, 24));
    }

    [Test]
    public async Task PtyTerminalWriteInputAndResizePassThrough()
    {
        var buffer = new MemoryStream();
        var bufferLock = new Lock();

        await using var terminal = PtyTerminal.Start(EchoInputChild(), new PtyTerminalOptions
        {
            Output = (data, _) =>
            {
                lock (bufferLock)
                {
                    buffer.Write(data.Span);
                }
                return ValueTask.CompletedTask;
            },
        });

        terminal.Resize(new PtySize(100, 40));
        await Assert.That(terminal.Size).IsEqualTo(new PtySize(100, 40));
        await Assert.That(terminal.ProcessId).IsGreaterThan(0);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await terminal.WriteInputAsync("INPUT_MARKER\n", cancellationToken: cts.Token);

        while (true)
        {
            cts.Token.ThrowIfCancellationRequested();
            lock (bufferLock)
            {
                if (Encoding.UTF8.GetString(buffer.ToArray()).Contains("INPUT_MARKER"))
                    break;
            }
            await Task.Delay(10, cts.Token);
        }

        terminal.Kill();
        await terminal.Completion.WaitAsync(cts.Token);
        await Assert.That(terminal.HasExited).IsTrue();
    }

    [Test]
    public async Task PtyTerminalKillResolvesCompletion()
    {
        await using var terminal = PtyTerminal.Start(StdinBlockingChild(), new PtyTerminalOptions
        {
            Output = (_, _) => ValueTask.CompletedTask,
        });

        terminal.Kill();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var status = await terminal.Completion.WaitAsync(cts.Token);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            await Assert.That(status.Signal).IsEqualTo(1);
            await Assert.That(status.ExitCode).IsEqualTo(129);
            await Assert.That(status.NodePtyExitCode).IsEqualTo(0);
        }
        else
        {
            await Assert.That(status.Signal).IsNull();
        }
    }

    [Test]
    public async Task PtyTerminalKillAllowsUnixHangupHandlerToExitNormally()
    {
        if (OperatingSystem.IsWindows())
            return;

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyText = new StringBuilder();
        await using var terminal = PtyTerminal.Start(UnixHangupHandlerChild(), new PtyTerminalOptions
        {
            Output = (data, _) =>
            {
                readyText.Append(Encoding.UTF8.GetString(data.Span));
                if (readyText.ToString().Contains("READY", StringComparison.Ordinal))
                    ready.TrySetResult();
                return ValueTask.CompletedTask;
            },
        });

        using (var readyCts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            await ready.Task.WaitAsync(readyCts.Token);

        terminal.Kill();

        using var completionCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var status = await terminal.Completion.WaitAsync(completionCts.Token);

        await Assert.That(status.ExitCode).IsEqualTo(0);
        await Assert.That(status.Signal).IsNull();
    }

    [Test]
    public async Task PtyTerminalHandlerExceptionFaultsCompletionAndKillsChild()
    {
        await using var terminal = PtyTerminal.Start(EchoMarkerChild("BOOM_TRIGGER"), new PtyTerminalOptions
        {
            Output = (_, _) => throw new InvalidOperationException("handler failed"),
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await terminal.Completion.WaitAsync(cts.Token));

        await Assert.That(exception!.Message).IsEqualTo("handler failed");

        // Kill is fire-and-forget; the OS may report exit slightly after Completion faults.
        while (!terminal.HasExited)
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, cts.Token);
        }
        await Assert.That(terminal.HasExited).IsTrue();
    }

    [Test]
    public async Task PtyTerminalAttachTakesOverExistingSession()
    {
        var output = new MemoryStream();
        var session = Pty.Start(EchoMarkerChild("ATTACHED_MARKER"));
        await using var terminal = PtyTerminal.Attach(session, new PtyTerminalOptions
        {
            Output = (data, _) =>
            {
                output.Write(data.Span);
                return ValueTask.CompletedTask;
            },
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var status = await terminal.Completion.WaitAsync(cts.Token);

        await Assert.That(status.ExitCode).IsEqualTo(0);
        await Assert.That(Encoding.UTF8.GetString(output.ToArray())).Contains("ATTACHED_MARKER");
    }

    [Test]
    public async Task PtyTerminalClearIsSafeNoOp()
    {
        await using var terminal = PtyTerminal.Start(StdinBlockingChild(), new PtyTerminalOptions
        {
            Output = (_, _) => ValueTask.CompletedTask,
        });

        terminal.Clear();
        terminal.Kill(PtySignal.Kill);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await terminal.Completion.WaitAsync(cts.Token);
    }

    [Test]
    public async Task PtyTerminalDisposeKillsRunningChild()
    {
        var terminal = PtyTerminal.Start(StdinBlockingChild(), new PtyTerminalOptions
        {
            Output = (_, _) => ValueTask.CompletedTask,
        });

        await terminal.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await terminal.WriteInputAsync("x"));
        Assert.Throws<ObjectDisposedException>(() => terminal.Kill());
        Assert.Throws<ObjectDisposedException>(() => terminal.Clear());
    }

    /// <summary>
    /// A child that leaves the terminal ends output on Linux while it keeps running, so the pump waits for its exit.
    /// Disposal must still reach the session instead of waiting for that exit. The child ignores the hang-up, so only
    /// the kill that disposal schedules ends it.
    /// </summary>
    [Test]
    public async Task PtyTerminalDisposeEndsAChildThatLeftTheTerminal()
    {
        if (OperatingSystem.IsWindows())
            return;

        var received = new StringBuilder();
        var terminal = PtyTerminal.Start(
            Spawn("sh", ["-c", "trap '' HUP; printf ready; exec >/dev/null 2>&1 </dev/null; exec sleep 60"]),
            new PtyTerminalOptions
            {
                Output = (data, _) =>
                {
                    lock (received)
                    {
                        received.Append(Encoding.UTF8.GetString(data.Span));
                    }
                    return ValueTask.CompletedTask;
                },
            });
        var pid = terminal.ProcessId;
        try
        {
            await Assert.That(await PtyBaselineTests.WaitUntilAsync(
                () => { lock (received) return received.ToString().Contains("ready", StringComparison.Ordinal); },
                TimeSpan.FromSeconds(15))).IsTrue();
            // Long enough for output to end and the pump to wait for the exit.
            await Task.Delay(500);

            // WaitAsync(TimeSpan) so a hang surfaces as TimeoutException.
            await terminal.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

            await Assert.That(terminal.Completion.IsCanceled).IsTrue();
            await Assert.That(await PtyBaselineTests.WaitUntilAsync(() => !PtyBaselineTests.IsRunning(pid), TimeSpan.FromSeconds(10))).IsTrue();
        }
        finally
        {
            PtyBaselineTests.KillAll([pid]);
        }
    }

    [Test]
    public async Task PtyTerminalStartValidatesArguments()
    {
        Assert.Throws<ArgumentNullException>(() => PtyTerminal.Start(null!, new PtyTerminalOptions
        {
            Output = (_, _) => ValueTask.CompletedTask,
        }));
        Assert.Throws<ArgumentNullException>(() => PtyTerminal.Start(StdinBlockingChild(), null!));
        Assert.Throws<ArgumentNullException>(() => PtyTerminal.Start(StdinBlockingChild(), new PtyTerminalOptions
        {
            Output = null!,
        }));
        await Task.CompletedTask;
    }

    private static PtyStartInfo Spawn(string fileName, IReadOnlyList<string> arguments) =>
        new() { FileName = fileName, Arguments = arguments, Size = new(80, 24) };

    /// <summary>Child that prints the marker and exits 0.</summary>
    private static PtyStartInfo EchoMarkerChild(string marker) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Spawn(WindowsComSpec(), ["/c", $"echo {marker}"])
            : Spawn("sh", ["-c", $"printf '{marker}\\n'"]);

    /// <summary>Child that echoes one stdin line back to stdout, then waits for another line (killed by test).
    /// Windows uses delayed expansion because %LINE% would be expanded before set /p runs.</summary>
    private static PtyStartInfo EchoInputChild() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Spawn(WindowsComSpec(), ["/v:on", "/c", "set /p LINE= & echo GOT:!LINE! & set /p DUMMY="])
            : Spawn("sh", ["-c", "IFS= read -r line; printf 'GOT:%s\\n' \"$line\"; IFS= read -r _"]);

    /// <summary>Child blocked on stdin until killed.</summary>
    private static PtyStartInfo StdinBlockingChild() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Spawn(WindowsComSpec(), ["/c", "set /p DUMMY="])
            : Spawn("sh", ["-c", "IFS= read -r _"]);

    /// <summary>
    /// Unix child that installs a HUP trap, prints READY, then blocks on stdin until killed.
    /// </summary>
    private static PtyStartInfo UnixHangupHandlerChild() =>
        Spawn("sh", ["-c", "trap 'exit 0' HUP; printf 'READY\\n'; IFS= read -r _"]);

    /// <summary>
    /// Child that emits one line immediately, then bulk output, and exits 0. The output only has to outlast what the
    /// session and the terminal take while paused, so that the child stays blocked. Windows types a file: echoing the
    /// lines one by one outlasted the test's 30 s on a loaded hosted ARM64 runner.
    /// </summary>
    /// <remarks>
    /// Windows writes 64 KiB: 16 KiB that nobody read already kept the child from exiting, and every byte more costs
    /// time after resume. Under CPU oversubscription ConPTY drains only about 10 KB/s whatever the line shape (Windows 11
    /// build 26200 with 48 busy processes: 256 KiB took 20–60 s in 512, 128, or 32 lines), and 256 KiB outlasted the
    /// 30 s on both hosted Windows runners. A Unix pty buffers up to about 64 KB of its own, so Unix keeps 256 KiB.
    /// </remarks>
    private static PtyStartInfo PauseFlowTestChild() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? WindowsCommandLine($"echo PAUSE_FLOW_START & {WindowsFloodFile.TypeCommand(PauseFlowLines, BulkOutputLineWidth)}")
            : Spawn("sh", ["-c", "printf 'PAUSE_FLOW_START\\n'; i=0; while [ $i -lt 2048 ]; do printf '%0128d\\n' \"$i\"; i=$((i+1)); done"]);

    /// <summary>Child that emits sustained bulk output (256 KiB) then exits 0.</summary>
    private static PtyStartInfo BulkOutputChild() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? WindowsCommandLine(WindowsFloodFile.TypeCommand(BulkOutputLines, BulkOutputLineWidth))
            : Spawn("sh", ["-c", "i=0; while [ $i -lt 2048 ]; do printf '%0128d\\n' \"$i\"; i=$((i+1)); done"]);

    // 256 KiB in few lines: a drain costs a console frame per line, about 8 ms each under CPU oversubscription.
    private const int BulkOutputLines = 512;
    private const int BulkOutputLineWidth = 512;
    // 64 KiB at BulkOutputLineWidth; see PauseFlowTestChild.
    private const int PauseFlowLines = 128;

    /// <summary>A cmd.exe child given a pre-escaped command line, so a quoted path inside it reaches cmd intact.</summary>
    private static PtyStartInfo WindowsCommandLine(string command) =>
        new() { FileName = WindowsComSpec(), CommandLine = $"/s /c \"{command}\"", Size = new(80, 24) };

    private static string WindowsComSpec() =>
        Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
}
