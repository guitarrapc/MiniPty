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
        await foreach (var chunk in session.ReadOutputAsync().WithCancellation(default))
        {
            received += chunk.Data.Span.Count((byte)0);
            chunks++;
        }

        var handOffs = ThreadPool.CompletedWorkItemCount - before;
        var passes = UnixPtyBackend.LoopPassesForTests - passesBefore;
        await Assert.That(received).IsEqualTo(Size);
        await Assert.That(handOffs).IsLessThan(chunks / 4L).Because($"{handOffs} thread-pool work items for {chunks} chunks");
        if (OperatingSystem.IsMacOS())
            await Assert.That(passes).IsLessThan(chunks / 4L).Because($"{passes} loop passes for {chunks} chunks");
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
    /// On macOS the consumer also waits for the master on its own thread between reads, for up to a millisecond; the
    /// disposal ends such a read the same way, once that wait returns.
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
    /// On macOS a consumer reading a flood waits for the master on its own thread when it finds it empty, instead of
    /// parking on the loop: a macOS master holds 1 KiB and the child refills it only after each read, so a consumer that
    /// parked there was handed every chunk by the loop, through the thread pool, which made macOS floods about half as
    /// fast as a blocking reader's. Judged by the waits the consumer made and by the loop's passes, not by timing.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task OnMacOSAConsumerWaitsForTheMasterOnItsOwnThread()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        const int Size = 1024 * 1024;
        await using var session = Pty.Start(Shell($"dd if=/dev/zero bs={Size} count=1 2>/dev/null"));
        var waitsBefore = UnixPtyBackend.ConsumerWaitsForTests;
        var listingsBefore = UnixPtyBackend.InlineReaderListingsForTests;
        var passesBefore = UnixPtyBackend.LoopPassesForTests;
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

        var waits = UnixPtyBackend.ConsumerWaitsForTests - waitsBefore;
        var passes = UnixPtyBackend.LoopPassesForTests - passesBefore;
        await Assert.That(received).IsEqualTo(Size);
        await Assert.That(UnixPtyBackend.InlineReaderListingsForTests - listingsBefore).IsGreaterThanOrEqualTo(1L);
        await Assert.That(waits).IsGreaterThan(chunks / 2L).Because($"{waits} waits on the consumer's thread for {chunks} chunks");
        await Assert.That(passes).IsLessThan(chunks / 4L).Because($"{passes} loop passes for {chunks} chunks");
    }

    /// <summary>
    /// A consumer on a thread with a scheduling context, a UI thread, never waits for the master in the kernel on macOS:
    /// it reads what the master holds and otherwise parks, as on Linux, so the thread is never blocked. Judged by the
    /// waits consumers made while this one read a flood.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task AConsumerWithASchedulingContextNeverWaitsForTheMasterInTheKernel()
    {
        if (!OperatingSystem.IsMacOS())
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
