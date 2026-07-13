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
        PtyTerminal terminal = null!;

        terminal = PtyTerminal.Start(EchoMarkerChild("TERMINAL_MARKER"), new PtyTerminalOptions
        {
            Output = (data, _) =>
            {
                if (terminal.Completion.IsCompleted)
                    handlerCompletedBeforeExit = false;
                buffer.Write(data.Span);
                return ValueTask.CompletedTask;
            },
        });

        await using (terminal)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var status = await terminal.Completion.WaitAsync(cts.Token);

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
        var pauseEngaged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PtyTerminal terminal = null!;
        terminal = PtyTerminal.Start(BulkOutputChild(), new PtyTerminalOptions
        {
            Output = (data, _) =>
            {
                Interlocked.Add(ref delivered, data.Length);
                // Pause on the first delivered chunk so fast runners cannot drain the full bulk
                // stream before flow control engages.
                if (Interlocked.CompareExchange(ref pausedOnFirstChunk, 1, 0) == 0)
                {
                    terminal.Pause();
                    pauseEngaged.TrySetResult();
                }

                return ValueTask.CompletedTask;
            },
        });

        await using (terminal)
        {
            using var startCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await pauseEngaged.Task.WaitAsync(startCts.Token);

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
            await Assert.That(status.Signal).IsEqualTo(9);
            await Assert.That(status.ExitCode).IsEqualTo(137);
        }
        else
        {
            await Assert.That(status.Signal).IsNull();
        }
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
    public async Task PtyTerminalDisposeKillsRunningChild()
    {
        var terminal = PtyTerminal.Start(StdinBlockingChild(), new PtyTerminalOptions
        {
            Output = (_, _) => ValueTask.CompletedTask,
        });

        await terminal.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await terminal.WriteInputAsync("x"));
        Assert.Throws<ObjectDisposedException>(() => terminal.Kill());
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

    /// <summary>Child that emits sustained bulk output (256 KiB) then exits 0.</summary>
    private static PtyStartInfo BulkOutputChild() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Spawn(WindowsComSpec(), ["/c", "for /l %i in (1,1,2048) do @echo 0123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567"])
            : Spawn("sh", ["-c", "i=0; while [ $i -lt 2048 ]; do printf '%0128d\\n' \"$i\"; i=$((i+1)); done"]);

    private static string WindowsComSpec() =>
        Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
}
