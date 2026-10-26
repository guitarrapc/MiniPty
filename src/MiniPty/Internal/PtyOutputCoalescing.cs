using System.Diagnostics;

namespace MiniPty.Internal;

/// <summary>
/// Burst coalescing for a consumer that pays per chunk, such as a bridge that sends one message per chunk. While output
/// arrives in a burst, its reader waits for a larger chunk instead of taking each read as it is published, with a short
/// bound on the wait. The pump keeps publishing each read as it completes, and only the reader that asked waits.
/// </summary>
/// <remarks>
/// <para>
/// A reader enters a burst once it has been handed <see cref="BurstBytes"/>, or <see cref="BurstChunks"/> chunks, within
/// <see cref="BurstWindowMs"/>. A keystroke echo never comes near either, but larger output can: one chunk of that size
/// counts, so the tail of a large repaint or paste can wait too, and so can output right after a run of small writes that
/// were read one by one. In a burst the reader takes its next chunk only once <see cref="MinimumBytes"/> are unread, or once
/// the wait's deadline, <see cref="MaxDelayMs"/> after it asked, has been served.
/// </para>
/// <para>
/// A wait that output completes before its deadline keeps the burst, and so does one whose deadline still found output
/// unread: output kept coming, however slowly, so a stream of small writes stays in its burst, each chunk taken at a
/// deadline. A wait completed later than its deadline by output that came after it keeps the burst only while what is
/// unread then amounts to <see cref="BurstKeepBytes"/> per <see cref="BurstWindowMs"/> since the read asked, which covers a
/// flood whose deadline a late tick did not serve. Otherwise output stopped, and the burst ends at that wait, whose output
/// the consumer takes at once. What that wait leaves unread does not count toward the next burst, however many chunks it
/// comes out as, because it piled up over the whole wait.
/// </para>
/// <para>
/// The pump completes a wait in a burst only at its minimum (<see cref="PtyOutputPump.ArmWait"/>), at its deadline, or at
/// end of output or disposal, so the consumer is not woken once per read in between. Each wake-up queues the consumer's
/// continuation to the thread pool, which a flood of small reads would otherwise pay once per read.
/// </para>
/// <para>
/// Joining reads needs waiting where a read returns little: a macOS master holds 1 KiB, and before the child writes its
/// next KiB no reader can tell a flood that goes on from one that has ended. The wait trades latency for fewer chunks, so
/// it is left to the consumer that pays per chunk.
/// </para>
/// </remarks>
internal static class PtyOutputCoalescing
{
    /// <summary>
    /// The chunk a reader waits for in a burst: half the ring, so the producer keeps reading into the other half while the
    /// consumer sends this one. It must not exceed what the ring holds when the producer parks, which is more than
    /// <see cref="PtyOutputPump.Capacity"/> minus <see cref="PtyOutputPump.PublishSize"/>, or a wait would outlast a
    /// parked producer until its deadline.
    /// </summary>
    internal const int MinimumBytes = PtyOutputPump.Capacity / 2;

    /// <summary>
    /// How long after it asks a reader in a burst waits for <see cref="MinimumBytes"/>. The scheduler serves the deadline at
    /// its next tick, which fires about every millisecond on every platform (a high-resolution timer on Windows, the I/O
    /// loop on Unix), so the wait lasts up to about twice this.
    /// </summary>
    internal const int MaxDelayMs = 1;

    /// <summary>Output handed out within <see cref="BurstWindowMs"/> that starts a burst: 4 MB/s, which no echo reaches.</summary>
    internal const int BurstBytes = 4 * 1024;

    /// <summary>The window <see cref="BurstBytes"/> and <see cref="BurstChunks"/> are counted over.</summary>
    internal const int BurstWindowMs = 1;

    /// <summary>
    /// Chunks handed out within <see cref="BurstWindowMs"/> that start a burst as well: a stream of small writes read one by
    /// one, which never reaches <see cref="BurstBytes"/>. Lines of 100 bytes at 0.4 MB/s come four to a millisecond; joined,
    /// they took 46–48 frames per 32 KiB instead of 333. A run of writes that the terminal hands over at once is one chunk.
    /// </summary>
    internal const int BurstChunks = 4;

    /// <summary>
    /// Output per <see cref="BurstWindowMs"/> that keeps a burst after output completed a wait only after its deadline, with
    /// no deadline served in between that found output, as when a late tick did not serve it: half the rate that starts a
    /// burst, so a flood slowed by a busy host does not leave and enter a burst on every wait.
    /// </summary>
    internal const int BurstKeepBytes = BurstBytes / 2;

    /// <summary>
    /// <see cref="MaxDelayMs"/> in <see cref="Stopwatch"/> ticks. Writable so that a test that runs alone can make a wait
    /// outlast scheduling delays.
    /// </summary>
    internal static long MaxDelayTicks = Stopwatch.Frequency * MaxDelayMs / 1000;

    /// <summary>
    /// <see cref="BurstWindowMs"/> in <see cref="Stopwatch"/> ticks. Writable so that a test that runs alone can widen the
    /// window: to count small chunks into one window despite scheduling delays, or to scale the rate that keeps a burst to a
    /// deadline that outlasts them.
    /// </summary>
    internal static long BurstWindowTicks = Stopwatch.Frequency * BurstWindowMs / 1000;

    // Compile-time bounds: converting a negative constant to uint does not compile. The minimum must fit in what the ring
    // holds when the producer parks, and the ring must stay below the short.MaxValue the pump uses for a wait that output
    // does not complete.
    private const uint MinimumFitsTheParkedRing = (uint)(PtyOutputPump.Capacity - PtyOutputPump.PublishSize - MinimumBytes);
    private const uint RingFitsTheShortMinimum = (uint)(short.MaxValue - 1 - PtyOutputPump.Capacity);
}

/// <summary>
/// The session's <see cref="PtyOutputReader"/> once an enumeration asked to coalesce bursts: the same reader with the
/// coalescing state (<see cref="PtyOutputCoalescing"/>), so that a session read plainly pays nothing for it.
/// </summary>
/// <remarks>
/// The consumer's thread owns the state, with three exceptions. The thread that completes a wait records when it did.
/// <see cref="PtyOutputCoalesceScheduler"/>'s tick reads and claims <see cref="Deadline"/>, and records whether the deadline
/// found output. The scheduler keeps <see cref="Scheduled"/> and <see cref="ScheduleIndex"/> under its lock.
/// </remarks>
internal sealed class PtyCoalescingOutputReader(PtySession session, PtyOutputPump pump) : PtyOutputReader(session, pump)
{
    private const byte FlagInBurst = 1;
    private const byte FlagWaitArmed = 2;

    // Whether the reader is in a burst and whether a coalescing wait is armed, in one byte that only the consumer writes,
    // so that the reader's own fields fit in 48 bytes. Two flags of their own grew every bridge session's reader by 8 B.
    private byte _consumerFlags;
    // The output a burst left unread when a late wait ended it. It is not counted toward the next burst, however many
    // chunks it comes out as.
    private int _skipBytes;
    // The bytes and chunks handed out in the current burst window, which starts at _windowStart (Stopwatch ticks). A byte
    // for the chunks, which only count to BurstChunks, so that the state packs with the flags.
    private int _windowBytes;
    private byte _windowChunks;
    private long _windowStart;
    // Whether the armed wait's deadline found output unread, written by the tick that served it before it completes the
    // wait. Output that kept coming keeps the burst.
    private bool _deadlineFoundOutput;
    // The armed wait's deadline as the consumer judges it, which the scheduler's Deadline loses when it is served.
    private long _waitDeadline;
    // When the armed wait was completed (Stopwatch ticks), written by the thread that completed it, or 0.
    private long _completedAt;

    /// <summary>
    /// The armed coalescing wait's deadline in <see cref="Stopwatch"/> ticks, or 0 when none is armed. Written by the
    /// consumer when it arms the wait and when the wait ends, and claimed by <see cref="Expire"/>.
    /// </summary>
    internal long Deadline;

    /// <summary>
    /// Whether the scheduler holds this reader. Written under the scheduler's lock, and read without it by the consumer,
    /// which skips that lock while the reader stays held.
    /// </summary>
    internal bool Scheduled;

    /// <summary>This reader's place in the scheduler's array, or -1. Guarded by the scheduler's lock.</summary>
    internal int ScheduleIndex = -1;

    /// <summary>Whether the reader is in a burst. For tests, on the consumer's thread.</summary>
    internal bool IsInBurst => InBurst;

    private bool InBurst
    {
        get => (_consumerFlags & FlagInBurst) != 0;
        set => _consumerFlags = value ? (byte)(_consumerFlags | FlagInBurst) : (byte)(_consumerFlags & ~FlagInBurst);
    }

    private bool WaitArmed
    {
        get => (_consumerFlags & FlagWaitArmed) != 0;
        set => _consumerFlags = value ? (byte)(_consumerFlags | FlagWaitArmed) : (byte)(_consumerFlags & ~FlagWaitArmed);
    }

    /// <summary>Starts an enumeration outside a burst.</summary>
    internal void ResetCoalescing()
    {
        InBurst = false;
        WaitArmed = false;
        _skipBytes = 0;
        _windowBytes = 0;
        _windowChunks = 0;
        _windowStart = 0;
        Volatile.Write(ref Deadline, 0);
    }

    /// <summary>
    /// The unread output the next take needs: 1 outside a burst, or <see cref="PtyOutputCoalescing.MinimumBytes"/> in one
    /// while less is unread and output can still arrive.
    /// </summary>
    internal int NextMinimum()
    {
        if (!InBurst
            || _pump.UnreadCount >= PtyOutputCoalescing.MinimumBytes
            || _pump.IsProducerFinished
            || _pump.IsSessionDisposed)
        {
            return 1;
        }

        return PtyOutputCoalescing.MinimumBytes;
    }

    /// <summary>
    /// Prepares a coalescing wait before the pump arms it, because a signal can complete the wait as soon as it is armed.
    /// </summary>
    internal void BeforeWait()
    {
        _waitDeadline = Stopwatch.GetTimestamp() + PtyOutputCoalescing.MaxDelayTicks;
        Volatile.Write(ref _completedAt, 0);
        Volatile.Write(ref _deadlineFoundOutput, false);
    }

    /// <summary>The pump armed the coalescing wait: its deadline goes to the scheduler.</summary>
    internal void OnWaitArmed()
    {
        WaitArmed = true;
        // A full fence before reading Scheduled, paired with the one in the scheduler's Remove: either this sees the
        // reader dropped and schedules it again, or Remove sees the new deadline and keeps the reader.
        Interlocked.Exchange(ref Deadline, _waitDeadline);
        if (!Volatile.Read(ref Scheduled))
            PtyOutputCoalesceScheduler.Schedule(this);
    }

    /// <summary>Records when the wait was completed. Called by the completing thread, before the consumer resumes.</summary>
    internal void OnWaitCompleted() => Volatile.Write(ref _completedAt, Stopwatch.GetTimestamp());

    /// <summary>
    /// The consumer resumed from its wait. A coalescing wait that output completed before its deadline keeps the burst. The
    /// time of completion decides, not the time the consumer resumed, so a consumer that resumes late keeps a burst whose
    /// output came in time. A wait whose deadline found output unread keeps it too: output kept coming, however slowly. A
    /// wait completed later by output that came after its deadline keeps the burst only while what is unread now, including
    /// any left unread when the read asked, amounts to at least <see cref="PtyOutputCoalescing.BurstKeepBytes"/> per window
    /// since it asked, as for a flood whose deadline a late tick did not serve. Otherwise output stopped, and the burst ends.
    /// </summary>
    internal void OnWaitEnded()
    {
        if (!WaitArmed)
            return;

        WaitArmed = false;
        Volatile.Write(ref Deadline, 0);
        var completedAt = Volatile.Read(ref _completedAt);
        if (completedAt != 0 && completedAt < _waitDeadline)
            return;

        var unread = _pump.UnreadCount;
        // completedAt is zero when cancellation ended the wait, which ends the enumeration too.
        if (completedAt != 0)
        {
            if (Volatile.Read(ref _deadlineFoundOutput))
                return;

            var waited = Stopwatch.GetTimestamp() - (_waitDeadline - PtyOutputCoalescing.MaxDelayTicks);
            // Dividing keeps the comparison in range. Multiplying the wait by the keep rate overflows for a wait of 2^52
            // ticks or more, about 52 days at a 1 GHz timestamp, and the wrapped product can keep the burst.
            if ((long)unread * PtyOutputCoalescing.BurstWindowTicks / PtyOutputCoalescing.BurstKeepBytes >= waited)
                return;
        }

        InBurst = false;
        _windowBytes = 0;
        _windowChunks = 0;
        _windowStart = 0;
        _skipBytes = unread;
    }

    /// <summary>
    /// Counts a chunk handed out toward the burst window, and starts a burst once the window holds enough bytes or chunks.
    /// </summary>
    internal void OnDelivered(int length)
    {
        if (InBurst)
            return;

        if (_skipBytes > 0)
        {
            var skipped = Math.Min(length, _skipBytes);
            _skipBytes -= skipped;
            length -= skipped;
            if (length == 0)
                return;
        }

        var now = Stopwatch.GetTimestamp();
        if (now - _windowStart > PtyOutputCoalescing.BurstWindowTicks)
        {
            _windowStart = now;
            _windowBytes = 0;
            _windowChunks = 0;
        }

        _windowBytes += length;
        _windowChunks++;
        if (_windowBytes >= PtyOutputCoalescing.BurstBytes || _windowChunks >= PtyOutputCoalescing.BurstChunks)
            InBurst = true;
    }

    /// <summary>The enumeration ended. The scheduler drops a reader without a deadline at its next tick.</summary>
    internal void OnEnded()
    {
        WaitArmed = false;
        Volatile.Write(ref Deadline, 0);
    }

    /// <summary>
    /// Serves the armed wait's deadline if it has passed at <paramref name="now"/>: the wait's minimum drops to 1 byte, so
    /// what is unread is taken now, or the next publish is. Whether output was unread is recorded first, before the wait
    /// can complete, and keeps the burst (<see cref="OnWaitEnded"/>). Called by the scheduler's tick. Returns <see langword="true"/>
    /// when the reader has no deadline, so the scheduler can let it go. A served deadline returns <see langword="false"/>:
    /// a burst's consumer arms its next wait on another thread soon after, and finding the reader still held spares it the
    /// scheduler's lock.
    /// </summary>
    internal bool Expire(long now)
    {
        var deadline = Volatile.Read(ref Deadline);
        if (deadline == 0)
            return true;

        if (now < deadline)
            return false;

        // A consumer that ended the wait and armed another meanwhile changed the deadline. The new one is looked at again.
        if (Interlocked.CompareExchange(ref Deadline, 0, deadline) != deadline)
            return false;

        if (_pump.UnreadCount > 0)
            Volatile.Write(ref _deadlineFoundOutput, true);

        _pump.RelaxWaitMinimum();
        return false;
    }

    /// <summary><see cref="PtySession.ReadOutputCoalescedAsync"/>, with the token that must travel with the enumerable.</summary>
    internal sealed class CoalescingEnumerable(PtyCoalescingOutputReader reader, CancellationToken readToken) : IAsyncEnumerable<PtyOutputChunk>
    {
        public IAsyncEnumerator<PtyOutputChunk> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            reader.Begin(readToken, cancellationToken, coalesce: true);
    }
}

/// <summary>
/// Process-wide deadlines of coalescing reads: one timer ticks every <see cref="TickMs"/> while the scheduler holds a
/// reader, and serves each deadline that has passed (<see cref="PtyCoalescingOutputReader.Expire"/>), as
/// <see cref="PtyOutputDrainScheduler"/> does for the post-exit drain. No reader holds a timer of its own. A reader leaves
/// at the first tick that finds it without a deadline, and the timer stops once none is left.
/// </summary>
/// <remarks>
/// <para>
/// A deadline is served at the first tick after it, so a burst's last chunk waits up to about twice
/// <see cref="PtyOutputCoalescing.MaxDelayMs"/>. A consumer takes the lock only when its reader is not held: on its first
/// wait, or after a tick found the reader with no deadline and dropped it. A tick that serves a deadline keeps the reader,
/// whose consumer usually arms its next wait before the next tick. Joining and leaving cost O(1), and nothing is allocated
/// once the array has grown to the peak number of readers waiting at once.
/// </para>
/// <para>
/// .NET timers do not keep a millisecond. On Windows they fire at the system timer's resolution, about 15.6 ms, and a
/// burst's last chunk waited about 15 ms there. On Linux they compute due times from <c>CLOCK_MONOTONIC_COARSE</c>, which
/// advances at the kernel's tick rate, and a 1 ms timer fired every 4 ms under a 250 Hz kernel. So Windows ticks on a
/// high-resolution waitable timer (<see cref="WindowsPreciseTick"/>), and Unix on the I/O loop, whose wait times out on the
/// precise clock (<see cref="UnixPtyBackend.TryStartCoalesceTicks"/>). A <see cref="Timer"/> ticks only where neither can
/// start.
/// </para>
/// </remarks>
internal static class PtyOutputCoalesceScheduler
{
    private const int TickMs = PtyOutputCoalescing.MaxDelayMs;

    private static readonly Lock s_lock = new();
    private static PtyCoalescingOutputReader?[] s_readers = new PtyCoalescingOutputReader?[4];
    private static int s_count;
    private static Timer? s_timer;
    // Windows: the high-resolution tick, or null before the first start and once it could not be created or started.
    private static WindowsPreciseTick? s_preciseTick;
    private static bool s_preciseTickTried;
    private static bool s_timerRunning;
    // What ticks while s_timerRunning, so that the stop stops the same.
    private static PtyCoalesceTickSource s_tickSource;

    // Touched only by the tick, which runs one at a time.
    private static PtyCoalescingOutputReader?[] s_snapshot = new PtyCoalescingOutputReader?[4];
    private static int s_ticking;
    private static long s_ticks;

    /// <summary>
    /// Set by a test that runs alone to serve deadlines itself: ticks then serve none, as a tick that runs late on a coarse
    /// timer serves none in time.
    /// </summary>
    internal static bool HoldTicksForTests;

    /// <summary>How many ticks have fired since the process started. For tests.</summary>
    internal static long TicksForTests => Interlocked.Read(ref s_ticks);

    /// <summary>What ticks now, or <see cref="PtyCoalesceTickSource.None"/> while nothing does. For tests.</summary>
    internal static PtyCoalesceTickSource TickSourceForTests
    {
        get
        {
            lock (s_lock)
                return s_tickSource;
        }
    }

    /// <summary>Serves a tick from the Unix I/O loop, which calls it about every millisecond while asked to.</summary>
    internal static void ServeTick() => Tick();

    /// <summary>Holds <paramref name="reader"/>, whose deadline is set, until it has none left to serve.</summary>
    internal static void Schedule(PtyCoalescingOutputReader reader)
    {
        lock (s_lock)
        {
            if (reader.ScheduleIndex < 0)
            {
                if (s_count == s_readers.Length)
                    Array.Resize(ref s_readers, s_count * 2);

                reader.ScheduleIndex = s_count;
                s_readers[s_count++] = reader;
            }

            Volatile.Write(ref reader.Scheduled, true);
            if (s_timerRunning)
                return;

            s_timerRunning = true;
            StartTickingLocked();
        }
    }

    private static void StartTickingLocked()
    {
        if (OperatingSystem.IsWindows())
        {
            if (!s_preciseTickTried)
            {
                s_preciseTickTried = true;
                s_preciseTick = WindowsPreciseTick.TryCreate(static () => Tick());
            }

            if (s_preciseTick is { } precise)
            {
                if (precise.Start(TickMs))
                {
                    s_tickSource = PtyCoalesceTickSource.WaitableTimer;
                    return;
                }

                // Not armed, or no thread: the coarse timer serves the deadlines from now on.
                s_preciseTick = null;
            }
        }
        else if (UnixPtyBackend.TryStartCoalesceTicks())
        {
            s_tickSource = PtyCoalesceTickSource.UnixLoop;
            return;
        }

        s_timer ??= CreateTimer();
        s_timer.Change(TickMs, TickMs);
        s_tickSource = PtyCoalesceTickSource.Timer;
    }

    private static void StopTickingLocked()
    {
        var source = s_tickSource;
        s_tickSource = PtyCoalesceTickSource.None;
        switch (source)
        {
            case PtyCoalesceTickSource.WaitableTimer when OperatingSystem.IsWindows():
                s_preciseTick!.Stop();
                break;
            case PtyCoalesceTickSource.UnixLoop:
                UnixPtyBackend.StopCoalesceTicks();
                break;
            case PtyCoalesceTickSource.Timer:
                s_timer!.Change(Timeout.Infinite, Timeout.Infinite);
                break;
        }
    }

    private static Timer CreateTimer()
    {
        // Process-wide and long-lived: it must not keep the first scheduling caller's execution context.
        if (ExecutionContext.IsFlowSuppressed())
            return new Timer(static _ => Tick(), null, Timeout.Infinite, Timeout.Infinite);

        using (ExecutionContext.SuppressFlow())
            return new Timer(static _ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    private static void Tick()
    {
        Interlocked.Increment(ref s_ticks);
        if (Volatile.Read(ref HoldTicksForTests) || Interlocked.Exchange(ref s_ticking, 1) != 0)
            return;

        try
        {
            int count;
            lock (s_lock)
            {
                count = s_count;
                if (s_snapshot.Length < count)
                    s_snapshot = new PtyCoalescingOutputReader?[s_readers.Length];

                Array.Copy(s_readers, s_snapshot, count);
            }

            var now = Stopwatch.GetTimestamp();
            for (var i = 0; i < count; i++)
            {
                var reader = s_snapshot[i]!;
                s_snapshot[i] = null;

                bool done;
                try
                {
                    done = reader.Expire(now);
                }
                catch (Exception)
                {
                    // A timer callback must never throw: that would terminate the host process.
                    done = true;
                }

                if (done)
                    Remove(reader);
            }
        }
        finally
        {
            Volatile.Write(ref s_ticking, 0);
        }
    }

    private static void Remove(PtyCoalescingOutputReader reader)
    {
        lock (s_lock)
        {
            if (reader.ScheduleIndex < 0)
                return;

            // The other half of the fence in OnWaitArmed, which publishes the deadline and then reads Scheduled: a reader
            // that armed a wait since the tick looked keeps its place.
            Volatile.Write(ref reader.Scheduled, false);
            Interlocked.MemoryBarrier();
            if (Volatile.Read(ref reader.Deadline) != 0)
            {
                Volatile.Write(ref reader.Scheduled, true);
                return;
            }

            var index = reader.ScheduleIndex;
            var last = s_readers[--s_count]!;
            s_readers[index] = last;
            last.ScheduleIndex = index;
            s_readers[s_count] = null;
            reader.ScheduleIndex = -1;

            if (s_count == 0 && s_timerRunning)
            {
                s_timerRunning = false;
                StopTickingLocked();
            }
        }
    }
}

/// <summary>What serves <see cref="PtyOutputCoalesceScheduler"/>'s tick while it holds a reader.</summary>
internal enum PtyCoalesceTickSource : byte
{
    /// <summary>Nothing ticks: the scheduler holds no reader.</summary>
    None,

    /// <summary>A <see cref="System.Threading.Timer"/>, where neither of the others could start.</summary>
    Timer,

    /// <summary>A high-resolution waitable timer on Windows (<see cref="WindowsPreciseTick"/>).</summary>
    WaitableTimer,

    /// <summary>The Unix I/O loop's wait timeout (<see cref="UnixPtyBackend.TryStartCoalesceTicks"/>).</summary>
    UnixLoop,
}
