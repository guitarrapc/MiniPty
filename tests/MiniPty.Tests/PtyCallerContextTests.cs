using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using MiniPty.Capture;
using MiniPty.Terminal;

namespace MiniPty.Tests;

/// <summary>
/// Caller-context safety (plan_gui_host_readiness.md G1): awaiting MiniPty from a GUI thread must not block
/// that thread, and idle sessions must not occupy thread-pool workers.
/// </summary>
/// <remarks>
/// Every test drives the API from <see cref="SingleThreadSynchronizationContext"/>, which stands in for a
/// WinUI / WPF dispatcher. Findings F1–F4 and F8 in the plan are the failures these tests reproduce. Tests
/// marked as guards already pass and protect behavior the G1 restructuring must keep. Tests for findings a
/// later G1 step fixes are skipped, and each skip reason names the step that removes it.
/// </remarks>
public sealed class PtyCallerContextTests
{
    /// <summary>Window over which UI-thread busy time is measured while the child is idle.</summary>
    private static readonly TimeSpan BusyWindow = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Busy time allowed within <see cref="BusyWindow"/>. A free UI thread is busy for well under a
    /// millisecond; a blocked one is busy for the whole window.
    /// </summary>
    private static readonly TimeSpan BusyBudget = TimeSpan.FromMilliseconds(100);

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(10);

    [Test]
    public async Task ReadOutputAsyncOnUiThreadDeliversOutputWithoutBlockingIt()
    {
        // F1: the producer used to start on the caller's context and deadlock it before any chunk arrived.
        using var ui = new SingleThreadSynchronizationContext();
        var session = Pty.Start(ReadyThenLineChild());
        var ready = new MarkerWatch("READY");
        var consumer = ui.RunAsync(async () =>
        {
            await foreach (var chunk in session.ReadOutputAsync())
                ready.Append(chunk.Data.Span);
        });

        try
        {
            await Assert.That(await SeenWithinAsync(ready, consumer)).IsTrue();
            // The consumer now awaits the next chunk from an idle child.
            await AssertUiThreadStaysFreeAsync(ui);
        }
        finally
        {
            await CleanupAsync(ui, session, consumer);
        }
    }

    [Test]
    public async Task OutputReadAsyncOnUiThreadDoesNotBlockItWhileIdle()
    {
        // F2: every read after the first used to block the calling thread inside ReadFile / read(2).
        using var ui = new SingleThreadSynchronizationContext();
        var session = Pty.Start(ReadyThenLineChild());
        var ready = new MarkerWatch("READY");
        var consumer = ui.RunAsync(async () =>
        {
            var buffer = new byte[4096];
            while (true)
            {
                var read = await session.Output.ReadAsync(buffer);
                if (read == 0)
                    break;

                ready.Append(buffer.AsSpan(0, read));
            }
        });

        try
        {
            await Assert.That(await SeenWithinAsync(ready, consumer)).IsTrue();
            await AssertUiThreadStaysFreeAsync(ui);
        }
        finally
        {
            await CleanupAsync(ui, session, consumer);
        }
    }

    [Test]
    public async Task WaitForExitAsyncOnUiThreadDoesNotBlockIt()
    {
        // F3: the exit wait used to poll on the caller's context until the child exited.
        using var ui = new SingleThreadSynchronizationContext();
        var session = Pty.Start(ReadyThenLineChild());
        var ready = new MarkerWatch("READY");
        // Output is drained off the UI thread so only the exit wait is under test.
        var drain = Task.Run(async () =>
        {
            await foreach (var chunk in session.ReadOutputAsync())
                ready.Append(chunk.Data.Span);
        });
        Task? exit = null;

        try
        {
            await Assert.That(await SeenWithinAsync(ready, drain)).IsTrue();

            var exitCode = ui.RunAsync(() => session.WaitForExitAsync());
            exit = exitCode;
            await AssertUiThreadStaysFreeAsync(ui);

            await session.WriteInputAsync(LineInput);
            await Assert.That(await CompletesWithinAsync(exitCode, OperationTimeout)).IsTrue();
            await Assert.That(await exitCode).IsEqualTo(0);
        }
        finally
        {
            await CleanupAsync(ui, session, drain, exit);
        }
    }

    [Test]
    public async Task CompleteAsyncOnUiThreadDoesNotBlockIt()
    {
        // F4: one-shot completion used to wait for exit on the caller's context.
        using var ui = new SingleThreadSynchronizationContext();
        var session = Pty.Start(SleepThenDoneChild());
        var completion = ui.RunAsync(() => session.CompleteAsync());

        try
        {
            // The child runs for about two seconds without output, so the measurement window is idle.
            await AssertUiThreadStaysFreeAsync(ui);
            await Assert.That(await CompletesWithinAsync(completion, OperationTimeout)).IsTrue();

            var result = await completion;
            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(result.Contains("DONE")).IsTrue();
        }
        finally
        {
            await CleanupAsync(ui, session, completion);
        }
    }

    [Test]
    public async Task PtyCaptureRunAsyncOnUiThreadDoesNotBlockIt()
    {
        // F4 through MiniPty.Capture, which shares the one-shot completion path.
        using var ui = new SingleThreadSynchronizationContext();
        var capture = ui.RunAsync(() => PtyCapture.RunAsync(SleepThenDoneChild()));

        try
        {
            // RunAsync spawns the child synchronously on the UI thread, which can take over 100 ms under load.
            // That spawn is inherent; the waiting that follows is what must leave the UI thread free.
            await Task.Delay(TimeSpan.FromMilliseconds(300));
            await AssertUiThreadStaysFreeAsync(ui);
            await Assert.That(await CompletesWithinAsync(capture, OperationTimeout)).IsTrue();

            var result = await capture;
            await Assert.That(result.ExitCode).IsEqualTo(0);
            await Assert.That(result.Contains("DONE")).IsTrue();
        }
        finally
        {
            await CleanupAsync(ui, session: null, capture);
        }
    }

    [Test]
    public Task WriteInputAsyncTextOnUiThreadDoesNotBlockItWhenTheChildIsNotReading() =>
        AssertWriteInputDoesNotBlockUiThreadAsync(textOverload: true);

    // Both overloads take what the tty accepts and queue the rest for the reader thread.
    [Test]
    public Task WriteInputAsyncBytesOnUiThreadDoesNotBlockItWhenTheChildIsNotReading() =>
        AssertWriteInputDoesNotBlockUiThreadAsync(textOverload: false);

    private static async Task AssertWriteInputDoesNotBlockUiThreadAsync(bool textOverload)
    {
        // A Unix tty input queue fills when the child does not read, and a blocking write to the master then
        // blocks its caller. ConPTY accepts input into its own buffer instead (plan F9), so Windows is not
        // affected.
        if (OperatingSystem.IsWindows())
            return;

        using var ui = new SingleThreadSynchronizationContext();
        var session = Pty.Start(Spawn("sleep", ["30"]));
        // 4 MiB of short lines: well past the tty line buffer and the kernel flip-buffer limit.
        var text = string.Concat(Enumerable.Repeat(new string('x', 63) + "\n", 65536));
        var bytes = Encoding.ASCII.GetBytes(text);
        var write = ui.RunAsync(() => textOverload
            ? session.WriteInputAsync(text).AsTask()
            : session.WriteInputAsync(bytes).AsTask());

        try
        {
            await AssertUiThreadStaysFreeAsync(ui);
        }
        finally
        {
            await CleanupAsync(ui, session, write);
        }
    }

    [Test]
    public async Task DisposeOnUiThreadReturnsPromptly()
    {
        // Guard: closing a tab disposes the session on the UI thread.
        using var ui = new SingleThreadSynchronizationContext();
        var session = Pty.Start(ReadyThenLineChild());
        var ready = new MarkerWatch("READY");
        var drain = Task.Run(async () =>
        {
            await foreach (var chunk in session.ReadOutputAsync())
                ready.Append(chunk.Data.Span);
        });

        try
        {
            await Assert.That(await SeenWithinAsync(ready, drain)).IsTrue();

            var elapsed = await ui.RunAsync(() =>
            {
                var stopwatch = Stopwatch.StartNew();
                session.Dispose();
                return Task.FromResult(stopwatch.Elapsed);
            }).WaitAsync(OperationTimeout);

            await Assert.That(elapsed).IsLessThan(TimeSpan.FromSeconds(1));
            await Assert.That(await ui.IsResponsiveAsync(ProbeTimeout)).IsTrue();
        }
        finally
        {
            await CleanupAsync(ui, session, drain);
        }
    }

    [Test]
    public async Task DisposeAsyncOnUiThreadDoesNotBlockIt()
    {
        // Guard: DisposeAsync may wait for teardown, but only by awaiting, never by blocking the UI thread.
        using var ui = new SingleThreadSynchronizationContext();
        var session = Pty.Start(ReadyThenLineChild());
        var ready = new MarkerWatch("READY");
        var drain = Task.Run(async () =>
        {
            await foreach (var chunk in session.ReadOutputAsync())
                ready.Append(chunk.Data.Span);
        });

        try
        {
            await Assert.That(await SeenWithinAsync(ready, drain)).IsTrue();

            var dispose = ui.RunAsync(() => session.DisposeAsync().AsTask());
            await Assert.That(await CompletesWithinAsync(dispose, OperationTimeout)).IsTrue();
            await Assert.That(await ui.IsResponsiveAsync(ProbeTimeout)).IsTrue();
        }
        finally
        {
            await CleanupAsync(ui, session, drain);
        }
    }

    [Test]
    public async Task PtyTerminalOnUiThreadDeliversOutputWithoutBlockingIt()
    {
        // Guard: PtyTerminal already detaches its pump from the caller's context (plan F5).
        using var ui = new SingleThreadSynchronizationContext();
        var ready = new MarkerWatch("READY");
        var terminal = await ui.RunAsync(() => Task.FromResult(PtyTerminal.Start(ReadyThenLineChild(), new PtyTerminalOptions
        {
            Output = (data, _) =>
            {
                ready.Append(data.Span);
                return ValueTask.CompletedTask;
            },
        })));

        try
        {
            await Assert.That(await CompletesWithinAsync(ready.Seen, OperationTimeout)).IsTrue();
            await AssertUiThreadStaysFreeAsync(ui);

            await ui.RunAsync(() => terminal.WriteInputAsync(LineInput).AsTask()).WaitAsync(OperationTimeout);
            var status = await ui.RunAsync(() => terminal.Completion).WaitAsync(OperationTimeout);
            await Assert.That(status.ExitCode).IsEqualTo(0);
        }
        finally
        {
            try
            {
                await terminal.DisposeAsync().AsTask().WaitAsync(CleanupTimeout);
            }
            catch (TimeoutException)
            {
            }
        }
    }

    [Test]
    [NotInParallel]
    public async Task IdleSessionsDoNotOccupyThreadPoolWorkers()
    {
        // F8: each idle session used to park two pool workers (the output producer and the exit poller),
        // so a few terminal tabs starved every other continuation in the host.
        const int SessionCount = 4;
        await Task.Delay(200);
        var before = BusyWorkerThreads();

        var terminals = new List<PtyTerminal>(SessionCount);
        var readies = new Task[SessionCount];
        try
        {
            for (var i = 0; i < SessionCount; i++)
            {
                var ready = new MarkerWatch("READY");
                readies[i] = ready.Seen;
                terminals.Add(PtyTerminal.Start(ReadyThenLineChild(), new PtyTerminalOptions
                {
                    Output = (data, _) =>
                    {
                        ready.Append(data.Span);
                        return ValueTask.CompletedTask;
                    },
                }));
            }

            await Assert.That(await CompletesWithinAsync(Task.WhenAll(readies), OperationTimeout)).IsTrue();
            // Let startup continuations finish so only idle sessions remain.
            await Task.Delay(500);
            var occupied = BusyWorkerThreads() - before;

            await Assert.That(occupied).IsLessThanOrEqualTo(2);
        }
        finally
        {
            foreach (var terminal in terminals)
            {
                try
                {
                    await terminal.DisposeAsync().AsTask().WaitAsync(CleanupTimeout);
                }
                catch (TimeoutException)
                {
                }
            }
        }
    }

    private static async Task AssertUiThreadStaysFreeAsync(SingleThreadSynchronizationContext ui)
    {
        var busy = await ui.MeasureBusyTimeAsync(BusyWindow);
        await Assert.That(busy).IsLessThan(BusyBudget);
        await Assert.That(await ui.IsResponsiveAsync(ProbeTimeout)).IsTrue();
        await Assert.That(ui.CallbackException).IsNull();
    }

    private static async Task<bool> CompletesWithinAsync(Task task, TimeSpan timeout)
    {
        try
        {
            await task.WaitAsync(timeout);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Waits for <paramref name="watch"/> to see its marker, surfacing a consumer failure instead of
    /// reporting it as a timeout.
    /// </summary>
    private static async Task<bool> SeenWithinAsync(MarkerWatch watch, Task consumer)
    {
        try
        {
            await Task.WhenAny(watch.Seen, consumer).WaitAsync(OperationTimeout);
        }
        catch (TimeoutException)
        {
            return false;
        }

        if (!watch.Seen.IsCompleted)
            await consumer;

        return watch.Seen.IsCompleted;
    }

    /// <summary>
    /// Best-effort teardown off the UI thread. The test's assertions already ran, so teardown must neither
    /// hang the run nor replace the original failure with its own.
    /// </summary>
    private static async Task CleanupAsync(SingleThreadSynchronizationContext ui, PtySession? session, params Task?[] observers)
    {
        if (session is not null)
        {
            try
            {
                await Task.Run(session.Dispose).WaitAsync(CleanupTimeout);
            }
            catch (TimeoutException)
            {
            }
        }

        foreach (var observer in observers)
        {
            if (observer is null)
                continue;

            try
            {
                await observer.WaitAsync(CleanupTimeout);
            }
            catch
            {
                // Disposal ends consumers and waiters with ObjectDisposedException or similar.
            }
        }

        ui.Dispose();
    }

    private static int BusyWorkerThreads()
    {
        ThreadPool.GetMaxThreads(out var maxWorkers, out _);
        ThreadPool.GetAvailableThreads(out var availableWorkers, out _);
        return maxWorkers - availableWorkers;
    }

    private static PtyStartInfo Spawn(string fileName, IReadOnlyList<string> arguments) =>
        new() { FileName = fileName, Arguments = arguments, Size = new(80, 24) };

    /// <summary>Prints READY, then blocks on one stdin line and exits 0.</summary>
    private static PtyStartInfo ReadyThenLineChild() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Spawn(WindowsComSpec(), ["/c", "echo READY & set /p DUMMY="])
            : Spawn("sh", ["-c", "printf 'READY\\n'; IFS= read -r _"]);

    /// <summary>Runs for about two seconds without output or input, then prints DONE and exits 0.</summary>
    private static PtyStartInfo SleepThenDoneChild() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Spawn(WindowsComSpec(), ["/c", "ping -n 3 127.0.0.1 >nul & echo DONE"])
            : Spawn("sh", ["-c", "sleep 2; printf 'DONE\\n'"]);

    /// <summary>Line terminator that submits a line: ConPTY needs CR, a Unix tty accepts LF.</summary>
    private static string LineInput => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "x\r" : "x\n";

    private static string WindowsComSpec() =>
        Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";

    /// <summary>Accumulates output from any thread and completes once an ASCII marker has appeared.</summary>
    private sealed class MarkerWatch(string marker)
    {
        private readonly byte[] _marker = Encoding.ASCII.GetBytes(marker);
        private readonly List<byte> _bytes = [];
        private readonly Lock _lock = new();
        private readonly TaskCompletionSource _seen = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Seen => _seen.Task;

        public void Append(ReadOnlySpan<byte> data)
        {
            lock (_lock)
            {
                _bytes.AddRange(data);
                if (CollectionsMarshal.AsSpan(_bytes).IndexOf(_marker) >= 0)
                    _seen.TrySetResult();
            }
        }
    }
}
