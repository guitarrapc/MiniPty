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
    /// A burst that arrives while the consumer waits costs one hand-off, not one per ring: the loop reads once for a
    /// consumer that waits after reading inline, and the consumer reads the rest on its own thread, or, while the child
    /// streams, waits for the master there, which the child's write wakes. A loop that read on filled the ring and parked on
    /// it, the consumer could not read inline while it was parked, and each ring then cost a wake of the loop and of a pool
    /// thread (four of each per 64 KiB on Linux). Judged by the loop's reads per pass for the waiting consumer, thread-pool
    /// work items, loop passes and the consumer's waits per burst, not by timing. The child answers each line of input with
    /// 64 KiB after 2 ms, longer than the consumer waits on its own thread, so every burst reaches a consumer parked on the
    /// loop, which is the path the loop's one read is about; on Linux a burst apart from the last also starts before the
    /// child counts as streaming, so a consumer that catches the kernel's worker between refills in its first 32 KiB is
    /// handed the next read by the loop as well, a hand-off or two more per burst.
    /// </summary>
    /// <remarks>
    /// The count of loop passes leaves out the passes that read for a consumer the loop took reading back from. A consumer
    /// whose thread is preempted for over a millisecond, as on a loaded CI runner, has the loop read for it until it
    /// reads inline again, and on macOS that is a pass per 1 KiB the master holds: hundreds of passes in a run on a
    /// 3-core macOS runner, none on an idle machine. How many depends on the scheduler, not on the loop, and the waits on
    /// the consumer's thread and the hand-offs still fail a consumer that never reads inline.
    /// </remarks>
    [Test]
    [NotInParallel]
    public async Task ABurstReachesAWaitingConsumerWithOneHandOff()
    {
        if (!(OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()))
            return;

        const int Burst = 64 * 1024;
        const int Bursts = 64;
        await using var session = Pty.Start(Shell(
            "stty -echo; perl -e '$| = 1; $s = \"x\" x " + Burst + "; while (<STDIN>) { select(undef, undef, undef, 0.002); print $s }'"));
        await using var output = session.ReadOutputAsync().GetAsyncEnumerator();

        async Task BurstAsync()
        {
            await session.WriteInputAsync("\n");
            var seen = 0;
            while (seen < Burst)
            {
                await Assert.That(await output.MoveNextAsync().AsTask().WaitAsync(Timeout)).IsTrue();
                seen += output.Current.Data.Span.Count((byte)'x');
            }

            await Assert.That(seen).IsEqualTo(Burst);
        }

        for (var i = 0; i < 8; i++)
            await BurstAsync();

        var before = ThreadPool.CompletedWorkItemCount;
        var passesBefore = UnixPtyBackend.LoopPassesForTests;
        var waitsBefore = UnixPtyBackend.ConsumerWaitsForTests;
        var loopReadsBefore = UnixPtyBackend.LoopReadsForWaitingConsumerForTests;
        var loopPassesBefore = UnixPtyBackend.LoopPassesForWaitingConsumerForTests;
        var takenBackBefore = UnixPtyBackend.LoopPassesForLoopConsumerForTests;
        var started = Stopwatch.GetTimestamp();
        for (var i = 0; i < Bursts; i++)
            await BurstAsync();

        var elapsedMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var handOffs = ThreadPool.CompletedWorkItemCount - before;
        var passes = UnixPtyBackend.LoopPassesForTests - passesBefore;
        var waits = UnixPtyBackend.ConsumerWaitsForTests - waitsBefore;
        var loopReads = UnixPtyBackend.LoopReadsForWaitingConsumerForTests - loopReadsBefore;
        var loopPasses = UnixPtyBackend.LoopPassesForWaitingConsumerForTests - loopPassesBefore;
        var takenBack = UnixPtyBackend.LoopPassesForLoopConsumerForTests - takenBackBefore;
        // One read per pass that hands a waiting consumer output; a loop that read on made four on Linux (on macOS the master
        // holds one read, so the pass stops after it either way).
        await Assert.That(loopPasses).IsGreaterThanOrEqualTo(Bursts / 2L).Because($"{loopPasses} loop passes handed the waiting consumer output in {Bursts} bursts");
        await Assert.That(loopReads).IsEqualTo(loopPasses).Because($"{loopReads} loop reads in {loopPasses} passes for the waiting consumer");
        await Assert.That(handOffs).IsLessThan(4L * Bursts).Because($"{handOffs} thread-pool work items for {Bursts} bursts");
        // Besides a pass per hand-off, the loop wakes about once a millisecond to check on inline readers while one is listed.
        // Passes that read for a consumer the loop took reading back from are the scheduler's doing (see remarks).
        await Assert.That(passes - takenBack).IsLessThan(2L * Bursts + elapsedMs)
            .Because($"{passes} loop passes, {takenBack} of them reading for a consumer taken back from, for {Bursts} bursts over {elapsedMs} ms");
        await Assert.That(waits).IsGreaterThanOrEqualTo(Bursts / 2L).Because($"{waits} waits on the consumer's thread for {Bursts} bursts");
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
