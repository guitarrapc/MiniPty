using System.Diagnostics;
using System.Text;
using MiniPty.Internal;

namespace MiniPty.Tests;

/// <summary>
/// The Linux engine: one loop, the exit watcher's thread, reads every session's output and writes every session's queued
/// input (<c>.github/docs/plans/plan_thread_free_io.md</c>, Phase 6). These tests cover what one shared thread must get
/// right for every session at once: fairness, a failure that stays with its session, no spinning on a master that stays
/// ready, and many sessions coming and going.
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
        if (!OperatingSystem.IsLinux())
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
        if (!OperatingSystem.IsLinux())
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
    [Test]
    [NotInParallel]
    public async Task AHungUpMasterOnAFullRingDoesNotSpinTheLoop()
    {
        if (!OperatingSystem.IsLinux())
            return;

        // More than the ring holds, so the session parks; the rest waits in the terminal, and the child exits.
        const int Size = PtyOutputPump.Capacity + 4096;
        await using var session = Pty.Start(Shell($"head -c {Size} /dev/zero | tr '\\0' x"));
        await session.WaitForExitAsync().WaitAsync(Timeout);
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
        if (!OperatingSystem.IsLinux())
            return;

        await RunBatchAsync(50);
        var threads = ThreadCount();
        var descriptors = Directory.GetFiles("/proc/self/fd").Length;
        var rings = PtyBufferPool.RentedCount;

        for (var batch = 0; batch < 20; batch++)
            await RunBatchAsync(50);

        // Helper threads the runtime started for the burst retire on their own.
        await Task.Delay(1000);
        await Assert.That(ThreadCount() - threads).IsLessThanOrEqualTo(4);
        await Assert.That(Directory.GetFiles("/proc/self/fd").Length - descriptors).IsLessThanOrEqualTo(4);
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
    }

    /// <summary>
    /// Linux sessions read on the loop by default, and on reader threads under the rollback switch.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task TheRollbackSwitchSelectsReaderThreads()
    {
        if (!OperatingSystem.IsLinux())
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
