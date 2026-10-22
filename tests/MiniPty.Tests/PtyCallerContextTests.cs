using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using MiniPty.Capture;
using MiniPty.Terminal;

namespace MiniPty.Tests;

/// <summary>
/// Caller-context safety: awaiting MiniPty from a GUI thread must not block that thread, and idle sessions must
/// not occupy thread-pool workers (Caller Context and GUI Hosts in <c>.github/docs/specs/core_session.md</c>).
/// </summary>
/// <remarks>
/// Every test drives the API from <see cref="SingleThreadSynchronizationContext"/>, which stands in for a
/// WinUI / WPF dispatcher. Each test's first comment names the failure it reproduced before output was read on a
/// library-owned thread and exit was observed by the exit watcher. Tests marked as guards passed then too, and
/// protect behavior that must stay.
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
        // The producer used to start on the caller's context and deadlock it before any chunk arrived.
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
        // Every read after the first used to block the calling thread inside ReadFile / read(2).
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
        // The exit wait used to poll on the caller's context until the child exited.
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

            var status = ui.RunAsync(() => session.WaitForExitAsync());
            exit = status;
            await AssertUiThreadStaysFreeAsync(ui);

            await session.WriteInputAsync(LineInput);
            await Assert.That(await CompletesWithinAsync(status, OperationTimeout)).IsTrue();
            await Assert.That((await status).ExitCode).IsEqualTo(0);
        }
        finally
        {
            await CleanupAsync(ui, session, drain, exit);
        }
    }

    [Test]
    public async Task CompleteAsyncOnUiThreadDoesNotBlockIt()
    {
        // One-shot completion used to wait for exit on the caller's context.
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
        // The same through MiniPty.Capture, which shares the one-shot completion path.
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
        // blocks its caller. ConPTY accepts input into its own buffer instead while its output flows. The
        // backed-up-output tests below cover Windows.
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
    public Task WriteInputAsyncTextOnUiThreadDoesNotBlockItWhileOutputIsBackedUp() =>
        AssertWriteInputDoesNotBlockUiThreadWhileOutputIsBackedUpAsync(textOverload: true);

    [Test]
    public Task WriteInputAsyncBytesOnUiThreadDoesNotBlockItWhileOutputIsBackedUp() =>
        AssertWriteInputDoesNotBlockUiThreadWhileOutputIsBackedUpAsync(textOverload: false);

    private static async Task AssertWriteInputDoesNotBlockUiThreadWhileOutputIsBackedUpAsync(bool textOverload)
    {
        // Once output nobody reads has filled the ring and the pipe, ConPTY stops reading input after about 4 KiB.
        // A synchronous write would then block its caller until output is read, which deadlocks a UI thread that
        // also runs the output loop. On Unix the child never reads, so the tty input queue fills instead.
        using var ui = new SingleThreadSynchronizationContext();
        var session = Pty.Start(FloodingChild());
        // Long enough for the flood to fill the ring and the transport behind it.
        await Task.Delay(500);
        // 64 KiB on Windows, far past the 4 KiB ConPTY input pipe. 4 MiB on Unix, far past a tty and its kernel buffers.
        var lineCount = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? 1024 : 65536;
        var text = string.Concat(Enumerable.Repeat(new string('x', 63) + LineInput[^1], lineCount));
        var bytes = Encoding.ASCII.GetBytes(text);
        var waitedAtIssue = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var write = ui.RunAsync(() =>
        {
            var task = textOverload ? session.WriteInputAsync(text).AsTask() : session.WriteInputAsync(bytes).AsTask();
            waitedAtIssue.TrySetResult(!task.IsCompleted);
            return task;
        });

        try
        {
            await AssertUiThreadStaysFreeAsync(ui);
            // The write had to wait, so a free UI thread proves it did not block. A write that completed or failed
            // at issue would leave the UI thread free for another reason.
            await Assert.That(await waitedAtIssue.Task.WaitAsync(OperationTimeout)).IsTrue();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // ConPTY takes input again once its output drains. The Unix child never reads, so its write stays queued.
                _ = Task.Run(async () =>
                {
                    await foreach (var _ in session.ReadOutputAsync())
                    {
                    }
                });
                await write.WaitAsync(OperationTimeout);
            }
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
        // Guard: PtyTerminal already detached its pump from the caller's context.
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
        // Each idle session used to park two pool workers (the output producer and the exit poller),
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

    /// <summary>Writes output without end and never reads input.</summary>
    private static PtyStartInfo FloodingChild() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Spawn(WindowsComSpec(), ["/c", "for /L %i in (1,1,100000000) do @echo %i"])
            : Spawn("yes", ["flood"]);

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
