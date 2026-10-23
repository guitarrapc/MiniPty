using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MiniPty.Tests;

/// <summary>
/// What an open session costs the host while it is idle, which is what a web host serving many browser tabs pays for
/// (<c>.github/docs/plans/plan_thread_free_io.md</c>).
/// </summary>
/// <remarks>
/// Each test counts process-wide resources, so it runs alone (<c>[NotInParallel]</c> without a key), and compares two
/// session counts rather than one count against a fixed number: the runtime starts and retires helper threads of its
/// own, and a difference between 16 and 64 sessions is what grows with sessions.
/// </remarks>
public sealed class PtyScaleTests
{
    private const string ThreadFreeIoSkip =
        "Thread-free I/O Phase 6 (Unix): each reading Unix session still holds a reader thread, and its ring from spawn " +
        "to end of output (.github/docs/plans/plan_thread_free_io.md).";

    private const int FewSessions = 16;
    private const int ManySessions = 64;

    /// <summary>
    /// Threads the runtime may add between the two counts on its own, such as a thread-pool worker injected while the
    /// sessions start, a tiered-compilation worker, or a timer thread.
    /// </summary>
    private const int RuntimeThreadTolerance = 4;

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Idle sessions hold no thread: their output waits on I/O completions, not on a reader thread each.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task IdleSessionsHoldNoThread()
    {
        Skip.When(!OperatingSystem.IsWindows(), ThreadFreeIoSkip);
        var sessions = new List<IdleSession>(ManySessions);
        try
        {
            await StartIdleSessionsAsync(sessions, FewSessions);
            var few = await SettledThreadCountAsync();

            await StartIdleSessionsAsync(sessions, ManySessions);
            var many = await SettledThreadCountAsync();

            // Windows exit watchers each wait on up to 63 children, so 64 sessions may take one watcher thread more.
            var watcherThreads = OperatingSystem.IsWindows() ? WatcherThreads(ManySessions) - WatcherThreads(FewSessions) : 0;
            await Assert.That(many - few - watcherThreads).IsLessThanOrEqualTo(RuntimeThreadTolerance);
        }
        finally
        {
            await DisposeAllAsync(sessions);
        }
    }

    /// <summary>
    /// An idle session whose output has been read holds no output buffer: it waits with a zero-byte read and takes a ring
    /// only while output is unread. A reader thread holds its ring from spawn to end of output instead, because a blocking
    /// read needs its buffer while it waits.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task IdleSessionsHoldNoOutputBuffer()
    {
        Skip.When(!OperatingSystem.IsWindows(), ThreadFreeIoSkip);
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
    /// go last (a reader thread, a consumer, a disposing thread) never strand in a per-thread cache.
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

    private static int WatcherThreads(int sessions) => (sessions + 62) / 63;

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
    /// The lowest thread count over a few samples once startup work has settled: helper threads the runtime started
    /// for the burst of spawns retire on their own, and a single sample can catch one still running.
    /// </summary>
    private static async Task<int> SettledThreadCountAsync()
    {
        await Task.Delay(1000);
        var lowest = int.MaxValue;
        for (var i = 0; i < 5; i++)
        {
            using var process = Process.GetCurrentProcess();
            lowest = Math.Min(lowest, process.Threads.Count);
            await Task.Delay(200);
        }

        return lowest;
    }

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
