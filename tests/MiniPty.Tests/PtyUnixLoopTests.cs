using System.Diagnostics;
using System.Text;
using MiniPty.Internal;

namespace MiniPty.Tests;

/// <summary>
/// The Unix engine: one loop, the exit watcher's thread, reads every session's output and writes every session's queued
/// input, through epoll on Linux and kqueue on macOS and FreeBSD (<c>.github/docs/plans/plan_thread_free_io.md</c>,
/// Phase 6). These tests cover what one shared thread must get right for every session at once: fairness, a failure that
/// stays with its session, no spinning on a master that stays ready, and many sessions coming and going.
/// </summary>
public sealed class PtyUnixLoopTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A session flooding output does not delay another session's keystroke echo beyond a bound.
    /// </summary>
    /// <remarks>
    /// The loop reads each session at most a few reads per pass. That bound is not what this test isolates: a terminal
    /// delivers output more slowly than the loop reads it, so the flood's master runs dry between reads and the loop moves
    /// on by itself. The test passed with the bound removed, also with the flood going to a one-shot result, whose sink
    /// keeps its ring from ever filling.
    /// </remarks>
    [Test]
    [NotInParallel]
    public async Task AFloodDoesNotDelayAnotherSessionsEcho()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var flood = Pty.Start(Shell("yes 0123456789abcdefghijklmnopqrstuvwxyz"));
        using var stopFlood = new CancellationTokenSource();
        long floodBytes = 0;
        var floodReader = Task.Run(async () =>
        {
            try
            {
                await foreach (var chunk in flood.ReadOutputAsync(stopFlood.Token))
                    Interlocked.Add(ref floodBytes, chunk.Data.Length);
            }
            catch (OperationCanceledException)
            {
            }
        });

        await using var echo = Pty.Start(Shell("cat"));
        await using var output = echo.ReadOutputAsync().GetAsyncEnumerator();
        // Let the flood get going before measuring.
        while (Interlocked.Read(ref floodBytes) < 1024 * 1024)
            await Task.Delay(10);

        // Echoes for half a second, so the flood keeps going all along however fast each echo is.
        var floodAtStart = Interlocked.Read(ref floodBytes);
        var measuring = Stopwatch.StartNew();
        var latencies = new List<double>();
        while (latencies.Count < 40 || measuring.ElapsedMilliseconds < 500)
        {
            var started = Stopwatch.GetTimestamp();
            await echo.WriteInputAsync("a");
            // The terminal echoes the character at once; cat sees nothing until a newline.
            while (true)
            {
                await Assert.That(await output.MoveNextAsync().AsTask().WaitAsync(Timeout)).IsTrue();
                if (output.Current.Data.Span.IndexOf((byte)'a') >= 0)
                    break;
            }

            latencies.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            // Erases the character again (VERASE is DEL), so the line never grows: a macOS terminal holds at most 1,024
            // bytes on one line, then drops input and echoes BEL instead, and a fast loop echoes more than that in 500 ms.
            await echo.WriteInputAsync("\x7f");
        }

        var floodDuring = Interlocked.Read(ref floodBytes) - floodAtStart;
        stopFlood.Cancel();
        await floodReader.WaitAsync(Timeout);
        latencies.Sort();
        var slowest = latencies[^1];

        // The flood kept going throughout, and echo stayed interactive.
        await Assert.That(floodDuring).IsGreaterThan(1024L * 1024);
        await Assert.That(slowest).IsLessThan(100.0).Because($"{latencies.Count} echoes, median {latencies[latencies.Count / 2]:F2} ms, slowest {slowest:F1} ms");
    }

    /// <summary>
    /// A failure while the loop serves one session ends that session's output with the failure; another session keeps
    /// its output, and the loop keeps running.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task AFailureInOneSessionEndsOnlyThatSession()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var failing = Pty.Start(Shell("cat"));
        await using var healthy = Pty.Start(Shell("cat"));
        await using var healthyOutput = healthy.ReadOutputAsync().GetAsyncEnumerator();

        UnixPtyBackend.FailLoopReadsOfPidForTests = failing.ProcessId;
        try
        {
            await failing.WriteInputAsync("trigger\n");
            var failure = await Assert.That(async () =>
            {
                await foreach (var _ in failing.ReadOutputAsync())
                {
                }
            }).Throws<IOException>();
            await Assert.That(failure!.Message).Contains("Injected read failure");
        }
        finally
        {
            UnixPtyBackend.FailLoopReadsOfPidForTests = 0;
        }

        await healthy.WriteInputAsync("still here\n");
        var text = new StringBuilder();
        while (!text.ToString().Contains("still here", StringComparison.Ordinal))
        {
            await Assert.That(await healthyOutput.MoveNextAsync().AsTask().WaitAsync(Timeout)).IsTrue();
            text.Append(Encoding.UTF8.GetString(healthyOutput.Current.Data.Span));
        }
    }

    /// <summary>
    /// A master that reports hang-up while its session is parked on a full ring reports it once, not on every pass, and
    /// its output still arrives in full when the consumer reads. A level-triggered hang-up while the loop cannot read
    /// would spin the loop for as long as the consumer stays away.
    /// </summary>
    /// <remarks>
    /// On Linux the child writes more than the ring holds; the rest waits in the terminal, which keeps it after the hang-up.
    /// On macOS the child's exit waits until the master has read what the terminal holds, because the session holds a
    /// descriptor of the slave, so the master hangs up while parked only when the terminal is empty: the child writes
    /// exactly what fills the ring. FreeBSD discards what a last close leaves unread after 1 s, so it takes the same size.
    /// </remarks>
    [Test]
    [NotInParallel]
    public async Task AHungUpMasterOnAFullRingDoesNotSpinTheLoop()
    {
        if (OperatingSystem.IsWindows())
            return;

        var size = OperatingSystem.IsLinux()
            ? PtyOutputPump.Capacity + 4096
            : PtyOutputPump.Capacity - PtyOutputPump.PublishSize + 1;
        await using var session = Pty.Start(Shell($"head -c {size} /dev/zero | tr '\\0' x"));
        await session.WaitForExitAsync().WaitAsync(Timeout);
        await WaitUntilAsync(() => session.OutputPump.IsWaitingForFreeSpace);

        var before = UnixPtyBackend.LoopPassesForTests;
        await Task.Delay(500);
        var passes = UnixPtyBackend.LoopPassesForTests - before;

        var received = 0;
        await foreach (var chunk in session.ReadOutputAsync().WithCancellation(default))
            received += chunk.Data.Span.Count((byte)'x');

        await Assert.That(passes).IsLessThan(50L);
        await Assert.That(received).IsEqualTo(size);
    }

    /// <summary>
    /// A master that stays readable while its session is parked on a full ring does not spin the loop: read interest is
    /// dropped while the ring is full, and comes back, with everything the terminal held meanwhile, once the consumer
    /// reads. Level-triggered readiness that stayed on would report the master on every pass.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task AParkedMasterWithUnreadOutputDoesNotSpinTheLoop()
    {
        if (OperatingSystem.IsWindows())
            return;

        // Far more than the ring and the terminal hold, so the child blocks in its write while the session is parked.
        const int Size = 1024 * 1024;
        await using var session = Pty.Start(Shell($"head -c {Size} /dev/zero | tr '\\0' x"));
        await WaitUntilAsync(() => session.OutputPump.IsWaitingForFreeSpace);

        var before = UnixPtyBackend.LoopPassesForTests;
        await Task.Delay(500);
        var passes = UnixPtyBackend.LoopPassesForTests - before;

        var received = 0;
        await foreach (var chunk in session.ReadOutputAsync().WithCancellation(default))
            received += chunk.Data.Span.Count((byte)'x');

        await Assert.That(passes).IsLessThan(50L);
        await Assert.That(received).IsEqualTo(Size);
    }

    /// <summary>
    /// A thousand sessions opened and closed leave no thread, descriptor, or ring behind: the loop frees each session's
    /// registration, and its slot serves the next.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task AThousandSessionsOpenedAndClosedLeaveNothingBehind()
    {
        if (OperatingSystem.IsWindows())
            return;

        await RunBatchAsync(50);
        var threads = ThreadCount();
        var descriptors = DescriptorCount();
        var rings = PtyBufferPool.RentedCount;

        for (var batch = 0; batch < 20; batch++)
            await RunBatchAsync(50);

        // Helper threads the runtime started for the burst retire on their own.
        await Task.Delay(1000);
        await Assert.That(ThreadCount() - threads).IsLessThanOrEqualTo(4);
        await Assert.That(DescriptorCount() - descriptors).IsLessThanOrEqualTo(4);
        await Assert.That(PtyBufferPool.RentedCount).IsEqualTo(rings);

        static async Task RunBatchAsync(int count)
        {
            var sessions = new PtySession[count];
            for (var i = 0; i < count; i++)
                sessions[i] = Pty.Start(Shell("echo churn"));

            foreach (var session in sessions)
            {
                await foreach (var _ in session.ReadOutputAsync())
                {
                }

                await session.DisposeAsync();
            }
        }

        static int ThreadCount()
        {
            using var process = Process.GetCurrentProcess();
            return process.Threads.Count;
        }

        // The process's open descriptors; macOS and FreeBSD list them under /dev/fd.
        static int DescriptorCount() => Directory.GetFiles(OperatingSystem.IsLinux() ? "/proc/self/fd" : "/dev/fd").Length;
    }

    /// <summary>
    /// Unix sessions read on the loop by default, and on reader threads under the rollback switch.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task TheRollbackSwitchSelectsReaderThreads()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using (var session = Pty.Start(Shell("echo rollback-marker")))
        {
            await Assert.That(session.OutputPump.Driver).IsNotNull();
            await Assert.That(session.OutputPump.Driver).IsNotTypeOf<PtyReaderThread>();
            await Assert.That(await ReadAllTextAsync(session)).Contains("rollback-marker");
        }

        AppContext.SetSwitch(PtyOutputDrivers.UseReaderThreadsSwitch, true);
        try
        {
            await using var session = Pty.Start(Shell("echo rollback-marker"));
            await Assert.That(session.OutputPump.Driver).IsTypeOf<PtyReaderThread>();
            await Assert.That(await ReadAllTextAsync(session)).Contains("rollback-marker");
        }
        finally
        {
            AppContext.SetSwitch(PtyOutputDrivers.UseReaderThreadsSwitch, false);
        }
    }

    private static PtyStartInfo Shell(string command) => new() { FileName = "sh", Arguments = ["-c", command] };

    private static async Task<string> ReadAllTextAsync(PtySession session)
    {
        var text = new StringBuilder();
        await foreach (var chunk in session.ReadOutputAsync().WithCancellation(default))
            text.Append(Encoding.UTF8.GetString(chunk.Data.Span));

        return text.ToString();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 10;
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() > deadline)
                throw new TimeoutException("The condition did not hold within 10 s.");

            await Task.Delay(10);
        }
    }
}
