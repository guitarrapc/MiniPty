using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MiniPty.Tests;

/// <summary>
/// What an open session costs the host while it is idle, which is what a web host serving many browser tabs pays for
/// (<c>.github/docs/references/pty_crossplatform.md</c>, Why this engine).
/// </summary>
/// <remarks>
/// Each test counts process-wide resources, so it runs alone (<c>[NotInParallel]</c> without a key), and compares two
/// session counts rather than one count against a fixed number: the runtime starts and retires helper threads of its
/// own, and a difference between 16 and 64 sessions is what grows with sessions.
/// </remarks>
public sealed class PtyScaleTests
{
    private const int FewSessions = 16;
    private const int ManySessions = 64;

    /// <summary>
    /// Threads outside the thread pool that the runtime may add between the two counts on its own, such as a
    /// tiered-compilation worker or a timer thread.
    /// </summary>
    private const int RuntimeThreadTolerance = 4;

    /// <summary>
    /// Thread-pool workers the runtime may add between the two counts. The pool injects workers while a burst of spawns
    /// keeps the CPU busy, and keeps each for 20 s before it retires, so the count after the burst is the pool's choice,
    /// not MiniPty's. Hosted Windows runners counted 6–7 threads more at 64 sessions than at 16 under a single tolerance
    /// of 4 that covered pool workers too, and a desktop under load counted up to 2 pool workers more. A worker held by
    /// each idle session would add one for each of the 48 sessions added; half of that tells the two apart.
    /// </summary>
    private const int PoolThreadTolerance = (ManySessions - FewSessions) / 2;

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Idle sessions hold no thread: their output waits on I/O completions (Windows) or on the one watcher loop (Unix), not
    /// on a thread each.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task IdleSessionsHoldNoThread()
    {
        var sessions = new List<IdleSession>(ManySessions);
        try
        {
            await StartIdleSessionsAsync(sessions, FewSessions);
            var few = await SettledThreadCountsAsync();

            await StartIdleSessionsAsync(sessions, ManySessions);
            var many = await SettledThreadCountsAsync();

            var counts = $"{FewSessions} sessions: {few}; {ManySessions} sessions: {many}";
            await Assert.That(many.OutsidePool - few.OutsidePool).IsLessThanOrEqualTo(RuntimeThreadTolerance).Because(counts);
            await Assert.That(many.PoolWorkers - few.PoolWorkers).IsLessThanOrEqualTo(PoolThreadTolerance).Because(counts);
        }
        finally
        {
            await DisposeAllAsync(sessions);
        }
    }

    /// <summary>
    /// An idle session whose output has been read holds no output buffer: it waits with a zero-byte read (Windows) or for
    /// the loop's readiness report (Unix), and takes a ring only while output is unread. A driver that blocked in the
    /// transport would hold its ring from spawn to end of output, because a blocking read needs its buffer while it waits.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task IdleSessionsHoldNoOutputBuffer()
    {
        var sessions = new List<IdleSession>(FewSessions);
        var before = MiniPty.Internal.PtyBufferPool.RentedCount;
        try
        {
            await StartIdleSessionsAsync(sessions, FewSessions);
            await Task.Delay(500);

            await Assert.That(MiniPty.Internal.PtyBufferPool.RentedCount - before).IsEqualTo(0);
        }
        finally
        {
            await DisposeAllAsync(sessions);
        }
    }

    /// <summary>
    /// A ring buffer returned on one thread serves the next rent on another, so rings released by whichever party lets
    /// go last (an output driver, a consumer, a disposing thread) never strand in a per-thread cache.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task OutputBuffersReturnedOnAnyThreadServeTheNextRent()
    {
        var rented = await Task.Run(MiniPty.Internal.PtyBufferPool.Rent);
        var returnThread = new Thread(() => MiniPty.Internal.PtyBufferPool.Return(rented));
        returnThread.Start();
        returnThread.Join();

        // A fresh thread, which has never rented or returned anything, takes a buffer without allocating one. Another
        // test's late release may hand it a different buffer than the one above, which is just as good.
        byte[]? again = null;
        var allocated = -1L;
        var rentThread = new Thread(() =>
        {
            var start = GC.GetAllocatedBytesForCurrentThread();
            again = MiniPty.Internal.PtyBufferPool.Rent();
            allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        });
        rentThread.Start();
        rentThread.Join();

        try
        {
            await Assert.That(allocated).IsEqualTo(0L);
            await Assert.That(again!.Length).IsEqualTo(MiniPty.Internal.PtyOutputPump.Capacity);
        }
        finally
        {
            if (again is not null)
                MiniPty.Internal.PtyBufferPool.Return(again);
        }
    }

    /// <summary>
    /// The Windows exit watcher waits on every child without a thread of its own: 1,000 pending waits add none. A
    /// design that waited on 63 handles per thread would add 16. Events stand in for children, since the watcher waits
    /// on any handle that signals once.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task WindowsExitWatcherWaitsWithoutAThreadPerHandle()
    {
        if (!OperatingSystem.IsWindows())
            return;

        const int Handles = 1000;
        var events = new EventWaitHandle[Handles];
        var observer = new CountingExitObserver(Handles);
        try
        {
            for (var i = 0; i < Handles; i++)
                events[i] = new EventWaitHandle(false, EventResetMode.ManualReset);

            var before = await SettledThreadCountsAsync();
            for (var i = 0; i < Handles; i++)
                MiniPty.Internal.WindowsExitWatcher.Register(events[i].SafeWaitHandle.DangerousGetHandle(), observer);
            var waiting = await SettledThreadCountsAsync();

            for (var i = 0; i < Handles; i++)
                events[i].Set();
            await observer.AllExited.WaitAsync(TimeSpan.FromSeconds(30));

            var counts = $"before: {before}; {Handles} waits: {waiting}";
            await Assert.That(waiting.OutsidePool - before.OutsidePool).IsLessThanOrEqualTo(RuntimeThreadTolerance).Because(counts);
            await Assert.That(observer.Exits).IsEqualTo(Handles);
        }
        finally
        {
            foreach (var handle in events)
                handle?.Dispose();
        }
    }

    /// <summary>
    /// Exit is observed through the watcher alone, since nothing else asks the child.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task EveryExitOfManyOpenSessionsIsObserved()
    {
        const int Sessions = 200;
        const int ExitCode = 3;
        var sessions = new List<IdleSession>(Sessions);
        try
        {
            var before = await SettledThreadCountsAsync();
            while (sessions.Count < Sessions)
                sessions.Add(new IdleSession(Pty.Start(ExitOnLineChild(ExitCode))));
            foreach (var session in sessions)
                await session.Ready.WaitAsync(StartTimeout);
            var open = await SettledThreadCountsAsync();

            foreach (var session in sessions)
                await session.Session.WriteInputAsync("\r"u8.ToArray());

            var exits = new Task<PtyExitStatus>[Sessions];
            for (var i = 0; i < Sessions; i++)
                exits[i] = sessions[i].Session.WaitForExitAsync();
            var statuses = await Task.WhenAll(exits).WaitAsync(TimeSpan.FromSeconds(60));

            var counts = $"before: {before}; {Sessions} sessions: {open}";
            await Assert.That(open.OutsidePool - before.OutsidePool).IsLessThanOrEqualTo(RuntimeThreadTolerance).Because(counts);
            foreach (var status in statuses)
                await Assert.That(status.ExitCode).IsEqualTo(ExitCode);
        }
        finally
        {
            await DisposeAllAsync(sessions);
        }
    }

    /// <summary>Prints READY, waits for a line, then exits with <paramref name="exitCode"/>.</summary>
    private static PtyStartInfo ExitOnLineChild(int exitCode) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new PtyStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe",
                Arguments = ["/c", $"echo READY & set /p DUMMY= & exit {exitCode}"],
            }
            : new PtyStartInfo { FileName = "sh", Arguments = ["-c", $"printf 'READY\\n'; IFS= read -r _; exit {exitCode}"] };

    /// <summary>Counts exits reported by <see cref="MiniPty.Internal.WindowsExitWatcher"/>.</summary>
    private sealed class CountingExitObserver(int expected) : MiniPty.Internal.IWindowsExitObserver
    {
        private readonly TaskCompletionSource _allExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _exits;

        public int Exits => Volatile.Read(ref _exits);

        public Task AllExited => _allExited.Task;

        public void OnProcessExited()
        {
            if (Interlocked.Increment(ref _exits) == expected)
                _allExited.TrySetResult();
        }
    }

    /// <summary>
    /// Starts sessions until <paramref name="target"/> are open, each with a consumer that reads its output as a web host
    /// does, and waits until every one has printed its marker and then waits for input.
    /// </summary>
    internal static async Task StartIdleSessionsAsync(List<IdleSession> sessions, int target)
    {
        var started = new List<IdleSession>();
        while (sessions.Count < target)
        {
            var session = new IdleSession(Pty.Start(IdleChild()));
            sessions.Add(session);
            started.Add(session);
        }

        foreach (var session in started)
            await session.Ready.WaitAsync(StartTimeout);
    }

    internal static async Task DisposeAllAsync(List<IdleSession> sessions)
    {
        foreach (var session in sessions)
            await session.DisposeAsync();
    }

    /// <summary>
    /// The lowest thread counts over a few samples once startup work has settled: helper threads the runtime started
    /// for the burst of spawns retire on their own, and a single sample can catch one still running. Threads outside the
    /// thread pool and pool workers are counted apart (see <see cref="PoolThreadTolerance"/>).
    /// </summary>
    private static async Task<ThreadCounts> SettledThreadCountsAsync()
    {
        await Task.Delay(1000);
        var outsidePool = int.MaxValue;
        var poolWorkers = int.MaxValue;
        for (var i = 0; i < 5; i++)
        {
            using var process = Process.GetCurrentProcess();
            var workers = ThreadPool.ThreadCount;
            outsidePool = Math.Min(outsidePool, process.Threads.Count - workers);
            poolWorkers = Math.Min(poolWorkers, workers);
            await Task.Delay(200);
        }

        return new ThreadCounts(outsidePool, poolWorkers);
    }

    private readonly record struct ThreadCounts(int OutsidePool, int PoolWorkers);

    /// <summary>Prints READY, then waits for a line of input that never comes.</summary>
    private static PtyStartInfo IdleChild() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new PtyStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe",
                Arguments = ["/c", "echo READY & set /p DUMMY="],
            }
            : new PtyStartInfo { FileName = "sh", Arguments = ["-c", "printf 'READY\\n'; IFS= read -r _"] };

    /// <summary>A session whose output is read to the end by a background consumer, as a web host reads it.</summary>
    internal sealed class IdleSession : IAsyncDisposable
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _consumer;

        public IdleSession(PtySession session)
        {
            Session = session;
            _consumer = ConsumeAsync();
        }

        public PtySession Session { get; }

        public Task Ready => _ready.Task;

        private async Task ConsumeAsync()
        {
            var seen = new StringBuilder();
            try
            {
                await foreach (var chunk in Session.ReadOutputAsync(_stop.Token))
                {
                    if (_ready.Task.IsCompleted)
                        continue;

                    seen.Append(Encoding.UTF8.GetString(chunk.Data.Span));
                    if (seen.ToString().Contains("READY", StringComparison.Ordinal))
                        _ready.TrySetResult();
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
            }
            finally
            {
                _ready.TrySetResult();
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            try
            {
                await _consumer.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException)
            {
            }

            await Session.DisposeAsync();
            _stop.Dispose();
        }
    }
}
