using System.Diagnostics;
using System.Reflection;
using System.Text;
using MiniPty.Internal;

namespace MiniPty.Tests;

/// <summary>
/// The Unix engine: one loop, the exit watcher's thread, reads every session's output and writes every session's queued
/// input, through epoll on Linux and kqueue on macOS and FreeBSD. These tests cover what one shared thread must get right for every session at once: fairness, a failure that
/// stays with its session, no spinning on a master that stays ready, and many sessions coming and going.
/// </summary>
public sealed class PtyUnixLoopTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A session flooding output does not delay another session's keystroke echo beyond a bound.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The loop reads each session at most a few reads per pass. That bound is not what this test isolates: a terminal
    /// delivers output more slowly than the loop reads it, so the flood's master runs dry between reads and the loop moves
    /// on by itself. The test passed with the bound removed, also with the flood going to a one-shot result, whose sink
    /// keeps its ring from ever filling.
    /// </para>
    /// <para>
    /// A slow echo is described by what else ran meanwhile (<see cref="EchoClock"/>), so that a failure tells a host that
    /// stopped the whole process from a loop that served the flood ahead of the echo. On 2026-11-05 one echo in 37,589
    /// took 100.4 ms on CI's macos-26-intel runner, and nothing recorded which it was.
    /// </para>
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
        var echoes = new List<EchoClock.Echo>();
        var echoPid = echo.ProcessId; // Temporary (I3)
        TemporaryEchoTrace.Start(echoPid, 1 << 20); // Temporary (I3)
        using (var clock = new EchoClock(() => Interlocked.Read(ref floodBytes)))
        {
            var measuring = Stopwatch.StartNew();
            while (echoes.Count < 40 || measuring.ElapsedMilliseconds < 500)
            {
                var started = clock.Begin();
                TemporaryEchoTrace.Record(echoPid, TraceTestWrite, ThreadCpuMicros()); // Temporary (I3)
                await echo.WriteInputAsync("a");
                TemporaryEchoTrace.Record(echoPid, TraceTestWritten, ThreadCpuMicros()); // Temporary (I3)
                // The terminal echoes the character at once; cat sees nothing until a newline.
                while (true)
                {
                    var moving = output.MoveNextAsync(); // Temporary (I3): was awaited in the assertion below
                    var movedAtOnce = moving.IsCompleted; // Temporary (I3)
                    TemporaryEchoTrace.Record(echoPid, TraceTestMoveNext, movedAtOnce ? 1 : 0); // Temporary (I3)
                    var moved = movedAtOnce ? moving.Result : await moving.AsTask().WaitAsync(Timeout); // Temporary (I3)
                    if (!movedAtOnce) // Temporary (I3)
                        TemporaryEchoTrace.Record(echoPid, TraceTestResumed, ThreadCpuMicros()); // Temporary (I3)
                    await Assert.That(moved).IsTrue(); // Temporary (I3): was await Assert.That(await output.MoveNextAsync().AsTask().WaitAsync(Timeout)).IsTrue();
                    TemporaryEchoTrace.Record(echoPid, TraceTestAsserted, ThreadCpuMicros()); // Temporary (I3)
                    TemporaryEchoTrace.Record(echoPid, TraceTestChunk, output.Current.Data.Length); // Temporary (I3)
                    if (output.Current.Data.Span.IndexOf((byte)'a') >= 0)
                        break;
                }

                echoes.Add(clock.End(started));
                // Erases the character again (VERASE is DEL), so the line never grows: a macOS terminal holds at most 1,024
                // bytes on one line, then drops input and echoes BEL instead, and a fast loop echoes more than that in 500 ms.
                await echo.WriteInputAsync("\x7f");
            }
        }

        TemporaryEchoTrace.Stop(); // Temporary (I3)
        var floodDuring = Interlocked.Read(ref floodBytes) - floodAtStart;
        stopFlood.Cancel();
        await floodReader.WaitAsync(Timeout);
        var echoesInOrder = echoes.ToArray(); // Temporary (I3)
        echoes.Sort(static (a, b) => b.Milliseconds.CompareTo(a.Milliseconds));
        var slowest = echoes[0].Milliseconds;
        var summary = $"{echoes.Count} echoes, median {echoes[echoes.Count / 2].Milliseconds:F2} ms, slowest: "
            + string.Join("; ", echoes.Take(3).Select(static e => e.ToString()));
        System.Console.WriteLine(summary);
        System.Console.WriteLine($"echo-trace {Environment.ProcessorCount} CPUs, {SynchronizationContext.Current?.GetType().Name ?? "no context"}"); // Temporary (I3)
        foreach (var slow in echoes.Take(3)) // Temporary (I3)
            PrintEchoTrace(slow, Array.FindIndex(echoesInOrder, e => e.Started == slow.Started), echoPid); // Temporary (I3)

        // The flood kept going throughout, and echo stayed interactive.
        await Assert.That(floodDuring).IsGreaterThan(1024L * 1024);
        await Assert.That(slowest).IsLessThan(100.0).Because(summary);
    }

    // Temporary (I3): the test's own events in TemporaryEchoTrace, next to the library's.
    private const int TraceTestWrite = 100; // Temporary (I3)
    private const int TraceTestWritten = 101; // Temporary (I3)
    private const int TraceTestMoveNext = 102; // Temporary (I3)
    private const int TraceTestResumed = 103; // Temporary (I3)
    private const int TraceTestChunk = 104; // Temporary (I3)
    private const int TraceTestAsserted = 105; // Temporary (I3)

    // Temporary (I3): the calling thread's CPU time in microseconds, or -1, so a trace tells a test thread that was
    // descheduled (wall time without CPU time) from one that was busy. CLOCK_THREAD_CPUTIME_ID is 16 on macOS, 3 on Linux.
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "clock_gettime")]
    private static extern int ClockGetTime(int clock, out TimeSpec value); // Temporary (I3)

    private struct TimeSpec // Temporary (I3)
    {
        public long Seconds;
        public long Nanoseconds;
    }

    private static int ThreadCpuMicros() // Temporary (I3)
        => ClockGetTime(OperatingSystem.IsMacOS() ? 16 : 3, out var value) == 0
            ? (int)(value.Seconds * 1_000_000 + value.Nanoseconds / 1000)
            : -1;

    /// <summary>
    /// Temporary (I3): prints the traced events from a millisecond before <paramref name="slow"/> began to just after it
    /// ended, in time order, as lines starting with <c>echo-trace</c>. The loop's waits are printed as one line each, at
    /// their end, with how long they took; the loop's services of the flood are counted, not printed.
    /// </summary>
    private static void PrintEchoTrace(EchoClock.Echo slow, int index, int echoPid) // Temporary (I3)
    {
        var (timestamps, pids, codes, args, threads, count, full) = TemporaryEchoTrace.Snapshot();
        // A macOS echo takes about 25 μs, so a longer lead-in fills the print with earlier echoes.
        var from = slow.Started - Stopwatch.Frequency / 20000;
        var to = slow.Ended + Stopwatch.Frequency / 5000;
        var lookBack = slow.Started - Stopwatch.Frequency / 20;
        var window = new List<int>();
        for (var i = 0; i < count; i++)
        {
            if (timestamps[i] != 0 && timestamps[i] >= lookBack && timestamps[i] <= to)
                window.Add(i);
        }

        window.Sort((a, b) => timestamps[a].CompareTo(timestamps[b]));
        System.Console.WriteLine($"echo-trace slow echo {slow.Milliseconds:F1} ms, echo #{index} (0 is the first), JIT on its thread {slow.JitOnThreadMilliseconds:F1} ms, pid {echoPid}, trace of {count} events{(full ? " (full)" : "")}:");
        long waitBegan = 0;
        var waitTimeout = 0;
        var otherServices = 0;
        var lines = 0;
        var idleWaits = 0;
        long idleFrom = 0;
        double idleLongest = 0;
        var lastCpu = new Dictionary<int, (long Timestamp, int Micros)>();
        foreach (var i in window)
        {
            var code = codes[i];
            var cpuNote = "";
            if (code is TraceTestWrite or TraceTestWritten or TraceTestResumed or TraceTestAsserted)
            {
                // The CPU time the thread used since its previous sample, against the wall time.
                if (lastCpu.TryGetValue(threads[i], out var previous) && previous.Micros >= 0 && args[i] >= 0)
                {
                    cpuNote = $" (since T{threads[i]}'s previous sample: cpu {(args[i] - previous.Micros) / 1000.0:F3} ms, "
                        + $"wall {Stopwatch.GetElapsedTime(previous.Timestamp, timestamps[i]).TotalMilliseconds:F3} ms)";
                }

                lastCpu[threads[i]] = (timestamps[i], args[i]);
            }

            if (code == TemporaryEchoTrace.LoopWaitBegin)
            {
                waitBegan = timestamps[i];
                waitTimeout = args[i];
                continue;
            }

            if (timestamps[i] < from)
                continue;

            if (code == TemporaryEchoTrace.LoopServiceOther)
            {
                otherServices++;
                continue;
            }

            // Waits that ended with no report (the loop's timeout for its inline readers) are summed into one line, printed
            // before the next other event.
            if (code == TemporaryEchoTrace.LoopWaitEnd && args[i] == 0)
            {
                var waited = waitBegan == 0 ? 0 : Stopwatch.GetElapsedTime(waitBegan, timestamps[i]).TotalMilliseconds;
                if (idleWaits++ == 0)
                    idleFrom = timestamps[i];
                idleLongest = Math.Max(idleLongest, waited);
                continue;
            }

            if (idleWaits > 0)
            {
                System.Console.WriteLine(
                    $"echo-trace   {Stopwatch.GetElapsedTime(slow.Started, idleFrom).TotalMilliseconds,9:F3} ms      loop "
                    + $"{idleWaits} waits ended without a report, the longest {idleLongest:F3} ms");
                idleWaits = 0;
                idleLongest = 0;
                lines++;
            }

            if (++lines > 400)
            {
                System.Console.WriteLine("echo-trace   (cut at 400 lines)");
                break;
            }

            var at = Stopwatch.GetElapsedTime(slow.Started, timestamps[i]).TotalMilliseconds;
            var name = code switch
            {
                TraceTestWrite => "TestWrite",
                TraceTestWritten => "TestWritten",
                TraceTestMoveNext => "TestMoveNext sync=",
                TraceTestResumed => "TestResumed",
                TraceTestAsserted => "TestAsserted",
                TraceTestChunk => "TestChunk bytes=",
                _ => TemporaryEchoTrace.Name(code),
            };
            var detail = code == TemporaryEchoTrace.LoopWaitEnd
                ? $"{name} reports={args[i]} after {(waitBegan == 0 ? "?" : Stopwatch.GetElapsedTime(waitBegan, timestamps[i]).TotalMilliseconds.ToString("F3"))} ms (timeout {waitTimeout}), flood services so far {otherServices}"
                : cpuNote.Length > 0 || code is TraceTestWrite or TraceTestWritten or TraceTestResumed or TraceTestAsserted
                    ? name + cpuNote
                    : $"{name} {args[i]}";
            System.Console.WriteLine($"echo-trace   {at,9:F3} ms T{threads[i],-3} {(pids[i] == 0 ? "loop" : "echo")} {detail}");
        }

        if (idleWaits > 0)
        {
            System.Console.WriteLine(
                $"echo-trace   {Stopwatch.GetElapsedTime(slow.Started, idleFrom).TotalMilliseconds,9:F3} ms      loop "
                + $"{idleWaits} waits ended without a report, the longest {idleLongest:F3} ms");
        }
    }

    /// <summary>
    /// Times echoes and records what else ran during each: a thread of its own ticks every millisecond and samples the
    /// flood's byte count and the thread pool's queue. For one echo, a long gap between ticks means the process did not
    /// run (the host descheduled it, or a collection suspended it); the flood standing still while ticks continued means
    /// the flood's reader did not run either; the flood flowing throughout means only the echo was held up, by the loop
    /// or by the thread pool, whose queue length tells which.
    /// </summary>
    private sealed class EchoClock : IDisposable
    {
        private const int Capacity = 1 << 16;
        private readonly long[] _ticks = new long[Capacity];
        private readonly long[] _flood = new long[Capacity];
        private readonly long[] _queued = new long[Capacity];
        private readonly Func<long> _floodBytes;
        private readonly Thread _thread;
        private volatile bool _stop;
        private int _count;

        public EchoClock(Func<long> floodBytes)
        {
            _floodBytes = floodBytes;
            _thread = new Thread(Run) { IsBackground = true, Name = "EchoClock" };
            _thread.Start();
        }

        public Start Begin() => new(
            Stopwatch.GetTimestamp(),
            GC.GetTotalPauseDuration(),
            Environment.CurrentManagedThreadId,
            System.Runtime.JitInfo.GetCompilationTime(currentThread: true),
            System.Runtime.JitInfo.GetCompilationTime(currentThread: false));

        public Echo End(Start started)
        {
            var ended = Stopwatch.GetTimestamp();
            // Compilation on the echo's own thread holds the echo up; it is known only when the echo ended on the thread it
            // began on. Compilation elsewhere in the process runs beside it.
            var jitOnThread = Environment.CurrentManagedThreadId == started.Thread
                ? (System.Runtime.JitInfo.GetCompilationTime(currentThread: true) - started.JitOnThread).TotalMilliseconds
                : double.NaN;
            return new Echo(
                this,
                started.Timestamp,
                ended,
                Stopwatch.GetElapsedTime(started.Timestamp, ended).TotalMilliseconds,
                (GC.GetTotalPauseDuration() - started.Paused).TotalMilliseconds,
                jitOnThread,
                (System.Runtime.JitInfo.GetCompilationTime(currentThread: false) - started.JitInProcess).TotalMilliseconds);
        }

        /// <summary>What <see cref="End"/> needs from the start of an echo.</summary>
        public readonly record struct Start(long Timestamp, TimeSpan Paused, int Thread, TimeSpan JitOnThread, TimeSpan JitInProcess);

        public void Dispose()
        {
            _stop = true;
            _thread.Join();
        }

        private void Run()
        {
            while (!_stop)
            {
                var i = _count;
                if (i == Capacity)
                    return;

                _ticks[i] = Stopwatch.GetTimestamp();
                _flood[i] = _floodBytes();
                _queued[i] = ThreadPool.PendingWorkItemCount;
                Volatile.Write(ref _count, i + 1);
                Thread.Sleep(1);
            }
        }

        /// <summary>One echo, described once the clock has stopped.</summary>
        public readonly record struct Echo(
            EchoClock Clock,
            long Started,
            long Ended,
            double Milliseconds,
            double PausedMilliseconds,
            double JitOnThreadMilliseconds,
            double JitInProcessMilliseconds)
        {
            public override string ToString()
            {
                var clock = Clock;
                var count = Volatile.Read(ref clock._count);
                double longestGap = 0, floodStill = 0;
                long floodFrom = -1, floodTo = -1, mostQueued = 0;
                long stillSince = 0;
                for (var i = 1; i < count; i++)
                {
                    if (clock._ticks[i] < Started || clock._ticks[i - 1] > Ended)
                        continue;

                    longestGap = Math.Max(longestGap, Stopwatch.GetElapsedTime(clock._ticks[i - 1], clock._ticks[i]).TotalMilliseconds);
                    mostQueued = Math.Max(mostQueued, clock._queued[i]);
                    if (floodFrom < 0)
                    {
                        floodFrom = clock._flood[i - 1];
                        stillSince = clock._ticks[i - 1];
                    }

                    if (clock._flood[i] != clock._flood[i - 1])
                        stillSince = clock._ticks[i];
                    else
                        floodStill = Math.Max(floodStill, Stopwatch.GetElapsedTime(stillSince, clock._ticks[i]).TotalMilliseconds);
                    floodTo = clock._flood[i];
                }

                var flood = floodFrom < 0 ? "no ticks" : $"flood +{(floodTo - floodFrom) / 1024} KiB, still for {floodStill:F1} ms";
                var jitOnThread = double.IsNaN(JitOnThreadMilliseconds) ? "?, the echo moved threads" : $"{JitOnThreadMilliseconds:F1} ms";
                return $"{Milliseconds:F1} ms ({flood}, longest gap between ticks {longestGap:F1} ms, "
                    + $"pool queue up to {mostQueued}, GC paused {PausedMilliseconds:F1} ms, "
                    + $"JIT on the echo's thread {jitOnThread}, in the process {JitInProcessMilliseconds:F1} ms)";
            }
        }
    }

    /// <summary>
    /// A failure while the loop serves one session ends that session's output with the failure; another session keeps
    /// its output, and the loop keeps running.
    /// </summary>
    /// <remarks>
    /// The failing session is read only once the loop has failed it. The failure is injected only when the loop serves
    /// the session, and a Unix consumer reads inline, so a consumer reading from the start could take the echo first
    /// and then wait for good. See Platform-specific Test Constraints in <c>.github/docs/specs/platform_support.md</c>.
    /// </remarks>
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
            await WaitUntilAsync(() => failing.OutputPump.IsProducerFinished);
            using var timeout = new CancellationTokenSource(Timeout);
            var failure = await Assert.That(async () =>
            {
                await foreach (var _ in failing.ReadOutputAsync(timeout.Token))
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

        // A spin makes thousands of passes. A live child that epoll cannot watch through a pidfd, as on Linux before
        // 5.3, is also polled every 10 ms, which adds about 50. PidfdsWork is false on 5.3 too, which only loosens the
        // bound there.
        var limit = OperatingSystem.IsLinux() && !PtyUnixChildSignalTests.PidfdsWork ? 100L : 50L;
        var before = UnixPtyBackend.LoopPassesForTests;
        await Task.Delay(500);
        var passes = UnixPtyBackend.LoopPassesForTests - before;

        var received = 0;
        await foreach (var chunk in session.ReadOutputAsync().WithCancellation(default))
            received += chunk.Data.Span.Count((byte)'x');

        await Assert.That(passes).IsLessThan(limit);
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
    /// Unix sessions read on the exit watcher's loop: the backend itself is the session's output driver, and the session
    /// holds no thread of its own.
    /// </summary>
    [Test]
    public async Task SessionsReadOnTheLoop()
    {
        if (OperatingSystem.IsWindows())
            return;

        var backend = UnixPtyBackend.Start(Shell("echo loop-marker"));
        await using var session = new PtySession(backend);
        await Assert.That(ReferenceEquals(session.OutputPump.Driver, backend)).IsTrue();
        await Assert.That(await ReadAllTextAsync(session).WaitAsync(Timeout)).Contains("loop-marker");
    }

    /// <summary>
    /// A master the loop cannot register fails the start, since nothing else would read the session: <c>Pty.Start</c>
    /// throws <see cref="IOException"/>, and the failed start's disposal hangs the child up, which ends it, and the
    /// watcher reaps it. Registration is refused through a test hook, by pid, because the real causes (no epoll or
    /// kqueue, a kernel watch limit) cannot be produced for one session. The start hook is process-wide, so this test
    /// runs alone.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task AMasterTheLoopCannotRegisterFailsTheStartAndEndsTheChild()
    {
        if (OperatingSystem.IsWindows())
            return;

        var pid = 0;
        PtyStartTestHook.Fault = (stage, processId) =>
        {
            if (stage != PtyStartStage.BackendBuilt || Interlocked.CompareExchange(ref pid, processId, 0) != 0)
                return;

            UnixPtyBackend.FailLoopRegistrationOfPidForTests = processId;
        };

        try
        {
            var failure = await Assert.That(() => Pty.Start(Shell("IFS= read -r _"))).Throws<IOException>();
            await Assert.That(failure!.Message).Contains("could not be registered");
            await Assert.That(pid).IsNotEqualTo(0);
            // kill(pid, 0) succeeds on a zombie too, so this also waits for the watcher's reap.
            await WaitUntilAsync(() => kill(pid, 0) != 0);
        }
        finally
        {
            PtyStartTestHook.Fault = null;
            UnixPtyBackend.FailLoopRegistrationOfPidForTests = 0;
        }
    }

    /// <summary>
    /// A first open of the watcher's epoll or kqueue that fails does not disable a load of MiniPty for good. The start that
    /// meets it fails, since no master can be registered; the next start opens the descriptors again, its session reads on
    /// the loop, and its exit is reported. Before, the open was never retried, and every later start failed. The test runs
    /// in a load of MiniPty of its own, whose watcher has opened nothing yet, so the open really fails and the test's own
    /// load is untouched.
    /// </summary>
    [Test]
    // A second load context exists only under the JIT, which runs this suite. The warnings are about trimming and
    // single-file publishing, which this test never meets.
#pragma warning disable IL2026, IL2072, IL2075, IL3000
    public async Task AFailedFirstOpenOfTheLoopIsRetriedByTheNextStart()
    {
        if (OperatingSystem.IsWindows())
            return;

        var context = new System.Runtime.Loader.AssemblyLoadContext(nameof(AFailedFirstOpenOfTheLoopIsRetriedByTheNextStart));
        var assembly = context.LoadFromAssemblyPath(typeof(Pty).Assembly.Location);
        var failOpen = assembly.GetType("MiniPty.Internal.UnixPtyBackend", throwOnError: true)!
            .GetField(nameof(UnixPtyBackend.FailWatchOpenForTests), BindingFlags.NonPublic | BindingFlags.Static)!;
        var startInfoType = assembly.GetType("MiniPty.PtyStartInfo", throwOnError: true)!;
        var start = assembly.GetType("MiniPty.Pty", throwOnError: true)!.GetMethod(nameof(Pty.Start), [startInfoType])!;

        failOpen.SetValue(null, true);
        Exception? failure = null;
        try
        {
            await ((IAsyncDisposable)StartIn("IFS= read -r _")).DisposeAsync();
        }
        catch (TargetInvocationException ex)
        {
            failure = ex.InnerException;
        }
        finally
        {
            failOpen.SetValue(null, false);
        }

        await Assert.That(failure).IsTypeOf<IOException>();

        var session = StartIn("echo retry-marker");
        await using ((IAsyncDisposable)session)
        {
            var output = (Stream)session.GetType().GetProperty(nameof(PtySession.Output))!.GetValue(session)!;
            var text = await new StreamReader(output).ReadToEndAsync().WaitAsync(Timeout);
            await Assert.That(text).Contains("retry-marker");
            var exit = (Task)session.GetType().GetMethod(nameof(PtySession.WaitForExitAsync))!.Invoke(session, [CancellationToken.None])!;
            await exit.WaitAsync(Timeout);
        }

        object StartIn(string command)
        {
            var startInfo = Activator.CreateInstance(startInfoType)!;
            startInfoType.GetProperty(nameof(PtyStartInfo.FileName))!.SetValue(startInfo, "sh");
            startInfoType.GetProperty(nameof(PtyStartInfo.Arguments))!.SetValue(startInfo, new[] { "-c", command });
            return start.Invoke(null, [startInfo])!;
        }
    }
#pragma warning restore IL2026, IL2072, IL2075, IL3000

    /// <summary>
    /// A flood that its consumer keeps up with is read on the consumer's thread, so another thread hands the consumer
    /// output about once per wait, not once per read: the consumer's continuation is queued to the thread pool only when it
    /// waited. A hand-off for every read was what cost floods their speed on CI's 2-core runners, where each one was a sleep
    /// and a wake. Counted as thread-pool work items, which the reads themselves do not queue.
    /// </summary>
    /// <remarks>
    /// The child writes in 1 MiB writes, so the terminal holds output whenever the consumer comes back, as for the
    /// benchmarks' flood. A child that writes more slowly than the consumer reads makes the consumer wait, rightly, every
    /// few reads (<c>head | tr</c> took about one hand-off per four reads). A macOS master hands out 1 KiB per read, and
    /// its writer refills the terminal only after each read, so a consumer there finds it empty after almost every read
    /// and waits for it on its own thread instead of parking; the loop's passes are counted there too, since each park
    /// would be one.
    /// </remarks>
    [Test]
    [NotInParallel]
    public async Task AFloodTheConsumerKeepsUpWithIsNotHandedOverOncePerRead()
    {
        if (OperatingSystem.IsWindows())
            return;

        const int Size = 4 * 1024 * 1024;
        await using var session = Pty.Start(Shell($"dd if=/dev/zero bs={1024 * 1024} count=4 2>/dev/null"));
        var received = 0;
        var chunks = 0;
        var before = ThreadPool.CompletedWorkItemCount;
        var passesBefore = UnixPtyBackend.LoopPassesForTests;
        var takenBackBefore = UnixPtyBackend.LoopPassesForLoopConsumerForTests;
        var askedAgainBefore = UnixPtyBackend.LoopLooksAskedAgainByConsumerForTests;
        var started = Stopwatch.GetTimestamp();
        await foreach (var chunk in session.ReadOutputAsync().WithCancellation(default))
        {
            received += chunk.Data.Span.Count((byte)0);
            chunks++;
        }

        var elapsedMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var handOffs = ThreadPool.CompletedWorkItemCount - before;
        var passes = UnixPtyBackend.LoopPassesForTests - passesBefore;
        var takenBack = UnixPtyBackend.LoopPassesForLoopConsumerForTests - takenBackBefore;
        var askedAgain = UnixPtyBackend.LoopLooksAskedAgainByConsumerForTests - askedAgainBefore;
        await Assert.That(received).IsEqualTo(Size);
        await Assert.That(handOffs).IsLessThan(chunks / 4L).Because($"{handOffs} thread-pool work items for {chunks} chunks");
        await Assert.That(passes - takenBack - askedAgain).IsLessThan(chunks / 4L + elapsedMs)
            .Because($"{passes} loop passes, {takenBack} reading for a consumer taken back from and {askedAgain} asked for again by the consumer, for {chunks} chunks over {elapsedMs} ms");
    }

    /// <summary>
    /// Every requested burst reaches a consumer whose read is already pending, without lost or extra bytes. When the
    /// loop serves a consumer that was reading inline, it reads only once in that pass before handing reading back.
    /// </summary>
    /// <remarks>
    /// Request output only after MoveNextAsync returns a pending read. A child-side sleep cannot establish that order:
    /// the consumer can be preempted until after the child writes. The scheduler can also let the loop take reading back
    /// from an inline consumer, so neither the proportion of inline hand-offs nor process-wide work-item counts have a
    /// fixed bound here. Both ownership paths must deliver all output; throughput belongs in the benchmarks.
    /// </remarks>
    [Test]
    [NotInParallel]
    public async Task BurstsReachAParkedConsumerInFull()
    {
        if (!(OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()))
            return;

        const int Burst = 64 * 1024; // A power of two, so doubling one byte reaches it exactly.
        const int Bursts = 64;
        using var timeout = new CancellationTokenSource(Timeout);
        var cancellationToken = timeout.Token;
        // Shell builtins only, besides stty: dash, busybox ash (Alpine), and macOS's sh all run it, and no interpreter
        // has to be installed. The burst is built once by doubling, and each request line is answered with all of it.
        await using var session = Pty.Start(Shell(
            "stty -echo; s=x; while [ ${#s} -lt " + Burst + " ]; do s=$s$s; done; printf ready; "
            + "i=0; while [ $i -lt " + Bursts + " ]; do read -r _; printf %s \"$s\"; i=$((i + 1)); done"));
        await using var output = session.ReadOutputAsync(cancellationToken).GetAsyncEnumerator();

        // The ready marker proves echo is disabled before any request is sent. It can arrive in multiple chunks.
        var ready = new StringBuilder();
        while (ready.Length < 5)
        {
            await Assert.That(await output.MoveNextAsync()).IsTrue();
            ready.Append(Encoding.UTF8.GetString(output.Current.Data.Span));
        }
        await Assert.That(ready.ToString()).IsEqualTo("ready");

        var loopReadsBefore = UnixPtyBackend.LoopReadsForWaitingConsumerForTests;
        var loopPassesBefore = UnixPtyBackend.LoopPassesForWaitingConsumerForTests;
        var takenBackBefore = UnixPtyBackend.LoopPassesForLoopConsumerForTests;
        var request = "\n"u8.ToArray();
        for (var i = 0; i < Bursts; i++)
        {
            // Retain and await this ValueTask exactly once. Until the request is written, the child has no output.
            var pending = output.MoveNextAsync();
            await Assert.That(pending.IsCompleted).IsFalse().Because($"burst {i} must start with a parked consumer");
            await session.WriteInputAsync(request, cancellationToken);
            var seen = 0;
            do
            {
                await Assert.That(await pending).IsTrue();
                var data = output.Current.Data;
                await Assert.That(data.Span.Count((byte)'x')).IsEqualTo(data.Length);
                seen += data.Length;
                if (seen < Burst)
                    pending = output.MoveNextAsync();
            } while (seen < Burst);

            await Assert.That(seen).IsEqualTo(Burst);
        }

        // EOF also detects trailing output and lets the loop finish updating its counters after the last publish.
        await Assert.That(await output.MoveNextAsync()).IsFalse();
        var exit = await session.WaitForExitAsync(cancellationToken);
        await Assert.That(exit.ExitCode).IsEqualTo(0);
        var loopReads = UnixPtyBackend.LoopReadsForWaitingConsumerForTests - loopReadsBefore;
        var loopPasses = UnixPtyBackend.LoopPassesForWaitingConsumerForTests - loopPassesBefore;
        var takenBack = UnixPtyBackend.LoopPassesForLoopConsumerForTests - takenBackBefore;
        await Assert.That(loopPasses + takenBack).IsGreaterThanOrEqualTo((long)Bursts)
            .Because("each burst must wake a pending read through one of the loop's consumer ownership paths");
        await Assert.That(loopReads).IsEqualTo(loopPasses)
            .Because($"{loopReads} loop reads in {loopPasses} passes for the waiting inline consumer");
    }

    /// <summary>
    /// A consumer that comes back to a ring the loop filled and parked on while it was away leaves that park to the loop: it
    /// takes the chunk, and the park ends when its release frees space and wakes the loop. A consumer that ended the park
    /// while the ring was still full left the loop's free-space wait and the release's wake out of step, and on kqueue the
    /// loop's deferred interest could then disarm the master after the consumer armed it, so nobody watched the master.
    /// Judged by the parks consumers ended, which the attempt must not add to, and by the pump's free-space wait, which the
    /// loop holds again by the time the chunk is taken either way.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task AParkOnARingStillFullSurvivesTheConsumersInlineRead()
    {
        if (OperatingSystem.IsWindows())
            return;

        const int Size = 4 * 1024 * 1024;
        await using var session = Pty.Start(Shell($"dd if=/dev/zero bs={1024 * 1024} count=4 2>/dev/null"));
        var pump = session.OutputPump;
        var hookRuns = 0;
        var parkedWhenTheConsumerCameBack = false;
        long parksEndedBeforeTheAttempt = 0;
        UnixPtyBackend.InlineReadHookForTests = () =>
        {
            // Once: stay away until the loop has read for this consumer, filled the ring and parked on it.
            if (Interlocked.Exchange(ref hookRuns, 1) != 0)
                return;

            var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5;
            while (!pump.IsWaitingForFreeSpace && Stopwatch.GetTimestamp() < deadline)
                Thread.Sleep(1);

            // The loop parks inside its pass and still holds the producer's turn for a moment; the consumer must find the
            // park with the turn free, or its attempt is turned away before it reaches the park.
            Thread.Sleep(5);
            parkedWhenTheConsumerCameBack = pump.IsWaitingForFreeSpace;
            Volatile.Write(ref parksEndedBeforeTheAttempt, UnixPtyBackend.ParksEndedByConsumerForTests);
        };

        try
        {
            var received = 0;
            var checkedPark = false;
            var parkSurvived = false;
            long parksEndedByTheAttempt = 0;
            await using var output = session.ReadOutputAsync().GetAsyncEnumerator();
            while (await output.MoveNextAsync().AsTask().WaitAsync(Timeout))
            {
                received += output.Current.Data.Span.Count((byte)0);
                // The first chunk after the hook's wait is the ring the loop filled, taken and not yet released: the ring was
                // full throughout the consumer's attempt, so the attempt must not have ended the park, and the loop is
                // parked on it now.
                if (!checkedPark && Volatile.Read(ref hookRuns) != 0)
                {
                    checkedPark = true;
                    parksEndedByTheAttempt = UnixPtyBackend.ParksEndedByConsumerForTests - Volatile.Read(ref parksEndedBeforeTheAttempt);
                    parkSurvived = pump.IsWaitingForFreeSpace;
                }
            }

            await Assert.That(parkedWhenTheConsumerCameBack).IsTrue().Because("the loop did not park on a full ring while the consumer was away");
            await Assert.That(parksEndedByTheAttempt).IsEqualTo(0L).Because("the consumer's inline read ended the loop's park on a ring that was still full");
            await Assert.That(parkSurvived).IsTrue().Because("the loop is not parked on the ring the consumer holds");
            await Assert.That(received).IsEqualTo(Size);
        }
        finally
        {
            UnixPtyBackend.InlineReadHookForTests = null;
        }
    }

    /// <summary>
    /// A consumer that pauses many times during a flood, each pause longer than the loop waits before it takes reading
    /// back, has the loop fill the ring and park during each pause; back from a pause it releases its chunk, which wakes
    /// the loop, and ends the park itself when it reads inline before the loop's pass does. Whichever side ends it, the
    /// child is not held up and every byte arrives. Judged by the parks the consumer ended, which must be at least one, and
    /// by the bytes.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task TheLoopReadsForAConsumerThatStopsAfterEndingItsPark()
    {
        if (OperatingSystem.IsWindows())
            return;

        const int Size = 4 * 1024 * 1024;
        await using var session = Pty.Start(Shell($"dd if=/dev/zero bs={1024 * 1024} count=4 2>/dev/null"));
        var parksBefore = UnixPtyBackend.ParksEndedByConsumerForTests;
        var received = 0;
        var chunks = 0;
        await foreach (var chunk in session.ReadOutputAsync().WithCancellation(default))
        {
            received += chunk.Data.Span.Count((byte)0);
            if (++chunks % 4 == 0 && chunks <= 256)
                await Task.Delay(3);
        }

        var parks = UnixPtyBackend.ParksEndedByConsumerForTests - parksBefore;
        await Assert.That(received).IsEqualTo(Size);
        await Assert.That(parks).IsGreaterThanOrEqualTo(1L).Because("the consumer never ended a park");
        await WaitUntilAsync(() => session.HasExited);
    }

    /// <summary>
    /// The consumer's inline reads and the loop's reads take turns on one ring without reordering or losing output. A
    /// counting child is read by one enumeration that stops partway, then by a second that pauses every few chunks for
    /// longer than the loop waits before it reads for a consumer, so reading moves between the two many times; every number
    /// arrives once, in order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The child turns the terminal's newline translation off (<c>stty -onlcr</c>), so the output is exactly what
    /// <c>seq</c> wrote. With it on, a macOS master read with pauses like this one's gives <c>\r\r\n</c> a few times per
    /// run, without MiniPty too (<c>.github/docs/references/pty_crossplatform.md</c>, Unix line discipline).
    /// </para>
    /// </remarks>
    [Test]
    public async Task OutputStaysInOrderWhileReadingMovesBetweenTheConsumerAndTheLoop()
    {
        if (OperatingSystem.IsWindows())
            return;

        const int Count = 200_000;
        await using var session = Pty.Start(Shell($"stty -onlcr; seq 1 {Count}"));
        var text = new StringBuilder();
        await using (var first = session.ReadOutputAsync().GetAsyncEnumerator())
        {
            while (text.Length < 256 * 1024 && await first.MoveNextAsync().AsTask().WaitAsync(Timeout))
                text.Append(Encoding.ASCII.GetString(first.Current.Data.Span));
        }

        // Between the two enumerations the loop reads into the ring.
        await Task.Delay(20);
        var chunks = 0;
        await foreach (var chunk in session.ReadOutputAsync().WithCancellation(default))
        {
            text.Append(Encoding.ASCII.GetString(chunk.Data.Span));
            if (++chunks % 16 == 0)
                await Task.Delay(3);
        }

        var lines = text.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var firstWrong = -1;
        for (var i = 0; i < Count && i < lines.Length; i++)
        {
            if (lines[i] != (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))
            {
                firstWrong = i;
                break;
            }
        }

        await Assert.That(firstWrong).IsEqualTo(-1).Because(firstWrong < 0 ? "" : $"line {firstWrong + 1} is '{lines[firstWrong]}'");
        await Assert.That(lines.Length).IsEqualTo(Count);
    }

    /// <summary>
    /// A consumer that has been reading a flood inline and then stops reading for a while, busy elsewhere, has the loop
    /// read for it again. What the child writes meanwhile reaches the ring: the child is not held up and exits (on macOS
    /// its exit waits until the master has read what it wrote), and the post-exit drain, which closes the master once reads
    /// go quiet, finds nothing left unread in it. Without the loop taking over, nobody would read the master during the
    /// pause, and on Linux the drain would close it with the tail in it.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task TheLoopReadsForAConsumerThatStopsReadingInline()
    {
        if (OperatingSystem.IsWindows())
            return;

        const int Flood = 1024 * 1024;
        const int Tail = 2048;
        await using var session = Pty.Start(Shell(
            $"head -c {Flood} /dev/zero | tr '\\0' x; sleep 0.3; head -c {Tail} /dev/zero | tr '\\0' y"));
        await using var output = session.ReadOutputAsync().GetAsyncEnumerator();
        var floodSeen = 0;
        while (floodSeen < Flood)
        {
            await Assert.That(await output.MoveNextAsync().AsTask().WaitAsync(Timeout)).IsTrue();
            floodSeen += output.Current.Data.Span.Count((byte)'x');
        }

        // Busy elsewhere, holding the last chunk, while the child writes its tail and exits.
        await Task.Delay(1000);
        var exitedMeanwhile = session.HasExited;
        var tailSeen = 0;
        while (await output.MoveNextAsync().AsTask().WaitAsync(Timeout))
            tailSeen += output.Current.Data.Span.Count((byte)'y');

        await Assert.That(tailSeen).IsEqualTo(Tail);
        await Assert.That(exitedMeanwhile).IsTrue();
    }

    /// <summary>
    /// Disposing a session while its consumer reads a flood inline on another thread ends that consumer's enumeration with
    /// <see cref="ObjectDisposedException"/> and ends the child, without waiting for the flood. The consumer's read holds a
    /// reference on the master, so the descriptor stays open, and its number unused, until that read returns.
    /// </summary>
    /// <remarks>
    /// On macOS and Linux the consumer also waits for the master on its own thread between reads, for up to a millisecond;
    /// the disposal ends such a read the same way, once that wait returns.
    /// </remarks>
    [Test]
    public async Task DisposingTheSessionWhileItsConsumerReadsInlineEndsTheRead()
    {
        if (OperatingSystem.IsWindows())
            return;

        var session = Pty.Start(Shell("yes 0123456789abcdefghijklmnopqrstuvwxyz"));
        long bytes = 0;
        var reader = Task.Run(async () =>
        {
            await foreach (var chunk in session.ReadOutputAsync().WithCancellation(default))
                Interlocked.Add(ref bytes, chunk.Data.Length);
        });

        while (Interlocked.Read(ref bytes) < 1024 * 1024)
            await Task.Delay(10);

        await session.DisposeAsync().AsTask().WaitAsync(Timeout);
        Exception? ended = null;
        try
        {
            await reader.WaitAsync(Timeout);
        }
        catch (Exception ex)
        {
            ended = ex;
        }

        await Assert.That(ended is ObjectDisposedException).IsTrue().Because(ended?.ToString() ?? "the read ended without an exception");
        // The disposal hangs the child up, and the watcher reaps it.
        await WaitUntilAsync(() => session.HasExited);
    }

    /// <summary>
    /// A consumer reading a flood waits for the master on its own thread when it finds it empty, instead of parking on the
    /// loop. A macOS master holds 1 KiB and the child refills it only after each read, so a consumer that parked there was
    /// handed every chunk by the loop, through the thread pool, which made macOS floods about half as fast as a blocking
    /// reader's; a Linux master holds more, so the consumer there waits only now and then, when it catches the kernel
    /// between refills. Judged by the waits the consumer made, the hand-offs, and the loop's passes, not by timing.
    /// </summary>
    /// <remarks>
    /// The count of loop passes leaves out what a loaded machine adds on its own, as in
    /// <see cref="AFloodTheConsumerKeepsUpWithIsNotHandedOverOncePerRead"/>: the loop's millisecond check on inline
    /// readers, the passes that read for a consumer taken back from, and the looks a consumer asks the loop for again
    /// after turning it away. Counted whole, they failed CI's <c>ubuntu-24.04</c> runner (125 passes for 171 chunks), and
    /// up to one run in five of a 2-CPU Linux container with every core busy, where the looks asked for again made up 116 of
    /// 125 passes with two hand-offs in all.
    /// </remarks>
    [Test]
    [NotInParallel]
    public async Task AConsumerWaitsForTheMasterOnItsOwnThread()
    {
        if (!(OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()))
            return;

        const int Size = 1024 * 1024;
        await using var session = Pty.Start(Shell($"dd if=/dev/zero bs={Size} count=1 2>/dev/null"));
        var waitsBefore = UnixPtyBackend.ConsumerWaitsForTests;
        var listingsBefore = UnixPtyBackend.InlineReaderListingsForTests;
        var passesBefore = UnixPtyBackend.LoopPassesForTests;
        var takenBackBefore = UnixPtyBackend.LoopPassesForLoopConsumerForTests;
        var askedAgainBefore = UnixPtyBackend.LoopLooksAskedAgainByConsumerForTests;
        var handOffsBefore = ThreadPool.CompletedWorkItemCount;
        var started = Stopwatch.GetTimestamp();
        var received = 0;
        var chunks = 0;
        await using (var output = session.ReadOutputAsync().GetAsyncEnumerator())
        {
            while (await output.MoveNextAsync().AsTask().WaitAsync(Timeout))
            {
                received += output.Current.Data.Span.Count((byte)0);
                chunks++;
            }
        }

        var elapsedMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var waits = UnixPtyBackend.ConsumerWaitsForTests - waitsBefore;
        var passes = UnixPtyBackend.LoopPassesForTests - passesBefore;
        var takenBack = UnixPtyBackend.LoopPassesForLoopConsumerForTests - takenBackBefore;
        var askedAgain = UnixPtyBackend.LoopLooksAskedAgainByConsumerForTests - askedAgainBefore;
        var handOffs = ThreadPool.CompletedWorkItemCount - handOffsBefore;
        await Assert.That(received).IsEqualTo(Size);
        await Assert.That(UnixPtyBackend.InlineReaderListingsForTests - listingsBefore).IsGreaterThanOrEqualTo(1L);
        if (OperatingSystem.IsMacOS())
            await Assert.That(waits).IsGreaterThan(chunks / 2L).Because($"{waits} waits on the consumer's thread for {chunks} chunks");
        else
            await Assert.That(waits).IsGreaterThanOrEqualTo(1L).Because($"{waits} waits on the consumer's thread for {chunks} chunks");
        await Assert.That(handOffs).IsLessThan(chunks / 4L).Because($"{handOffs} thread-pool work items for {chunks} chunks");
        await Assert.That(passes - takenBack - askedAgain).IsLessThan(chunks / 4L + elapsedMs)
            .Because($"{passes} loop passes, {takenBack} reading for a consumer taken back from and {askedAgain} asked for again by the consumer, for {chunks} chunks over {elapsedMs} ms");
    }

    /// <summary>
    /// A consumer waits for the master on its own thread only after a read that says the child had more than a read's worth:
    /// the whole master on macOS (1 KiB), a whole read on Linux (4,095 bytes). After a short read, a line of output at a
    /// time, it parks at once: a wait there would hold a thread-pool thread for the wait per line, for output that is not
    /// coming.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task AConsumerDoesNotWaitAfterAShortRead()
    {
        if (!(OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()))
            return;

        await using var session = Pty.Start(Shell("for i in 1 2 3 4 5 6 7 8; do echo line$i; sleep 0.01; done"));
        var waitsBefore = UnixPtyBackend.ConsumerWaitsForTests;
        var lines = 0;
        await foreach (var chunk in session.ReadOutputAsync().WithCancellation(default))
            lines += chunk.Data.Span.Count((byte)'\n');

        await Assert.That(lines).IsEqualTo(8);
        await Assert.That(UnixPtyBackend.ConsumerWaitsForTests - waitsBefore).IsEqualTo(0L);
    }

    /// <summary>
    /// On Linux a child that writes bursts of a few reads, 16 KiB every 10 ms, never makes its consumer wait for the master:
    /// a burst of fewer than eight full reads is what a TUI's redraw or a long log line writes before the child stops, and a
    /// wait after it would hold a thread-pool thread for the timeout per burst. A park before the child counts as streaming
    /// resets the count, so the bursts do not add up to a wait either. macOS waits after one full read by design.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task OnLinuxAConsumerDoesNotWaitAfterABurstOfAFewReads()
    {
        if (!OperatingSystem.IsLinux())
            return;

        await using var session = Pty.Start(Shell("for i in 1 2 3 4 5 6 7 8; do head -c 16384 /dev/zero | tr '\\0' x; sleep 0.01; done"));
        var waitsBefore = UnixPtyBackend.ConsumerWaitsForTests;
        var received = 0;
        await foreach (var chunk in session.ReadOutputAsync().WithCancellation(default))
            received += chunk.Data.Span.Count((byte)'x');

        await Assert.That(received).IsEqualTo(8 * 16384);
        await Assert.That(UnixPtyBackend.ConsumerWaitsForTests - waitsBefore).IsEqualTo(0L);
    }

    /// <summary>
    /// A consumer on a thread with a scheduling context, a UI thread, never waits for the master in the kernel: it reads
    /// what the master holds and otherwise parks, so the thread is never blocked. Judged by the waits consumers made while
    /// this one read a flood.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task AConsumerWithASchedulingContextNeverWaitsForTheMasterInTheKernel()
    {
        if (!(OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()))
            return;

        const int Size = 1024 * 1024;
        await using var session = Pty.Start(Shell($"dd if=/dev/zero bs={Size} count=1 2>/dev/null"));
        using var ui = new SingleThreadSynchronizationContext();
        var waitsBefore = UnixPtyBackend.ConsumerWaitsForTests;
        var received = await ui.RunAsync(async () =>
        {
            var received = 0;
            await foreach (var chunk in session.ReadOutputAsync())
                received += chunk.Data.Span.Count((byte)0);

            return received;
        }).WaitAsync(Timeout);

        await Assert.That(received).IsEqualTo(Size);
        await Assert.That(UnixPtyBackend.ConsumerWaitsForTests - waitsBefore).IsEqualTo(0L);
    }

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

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
