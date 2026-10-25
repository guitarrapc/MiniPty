using System.Diagnostics;

namespace MiniPty.Internal;

/// <summary>
/// Burst coalescing for a consumer that pays per chunk, such as a bridge that sends one WebSocket message per chunk: while
/// output arrives in a burst, its reader waits for a larger chunk instead of taking each read as it is published, with a
/// short bound on the wait. The pump keeps publishing each read as it completes; only the consumer that asks waits.
/// </summary>
/// <remarks>
/// <para>
/// A reader enters a burst once it has taken <see cref="BurstBytes"/> within <see cref="BurstWindowMs"/>, a rate no
/// keystroke echo reaches, so interactive output is never held. In a burst it takes its next chunk only once
/// <see cref="MinimumBytes"/> are unread, or once <see cref="MaxDelayMs"/> has passed since it asked; a wait that ends at
/// that deadline means output slowed down, and ends the burst. The pump completes such a wait only at its minimum
/// (<see cref="PtyOutputPump.ArmWait"/>), so the consumer is not woken once per read in between: that wake-up, and the
/// thread-pool workers spinning after it, is what a flood of small reads costs the host.
/// </para>
/// <para>
/// Why the consumer: a macOS master holds 1 KiB, so a read returns at most that much, and before the child writes its next
/// KiB no reader can tell a flood that goes on from one that has ended. Joining reads there means waiting, which trades
/// latency for fewer chunks, and that trade belongs to the party that pays per chunk
/// (<c>.github/docs/plans/plan_thread_free_io.md</c>, Phase 7).
/// </para>
/// </remarks>
internal static class PtyOutputCoalescing
{
    /// <summary>
    /// The chunk a reader waits for in a burst: half the ring, so the producer keeps reading into the other half while the
    /// consumer sends this one. It must stay below what the ring holds when the producer parks (more than
    /// <see cref="PtyOutputPump.Capacity"/> minus <see cref="PtyOutputPump.PublishSize"/>), or a wait could outlast a
    /// parked producer until its deadline.
    /// </summary>
    internal const int MinimumBytes = PtyOutputPump.Capacity / 2;

    /// <summary>The longest a reader in a burst waits for <see cref="MinimumBytes"/> before it takes what is unread.</summary>
    internal const int MaxDelayMs = 1;

    /// <summary>Output taken within <see cref="BurstWindowMs"/> that starts a burst: 4 MB/s, far above any echo.</summary>
    internal const int BurstBytes = 4 * 1024;

    /// <summary>The window <see cref="BurstBytes"/> is counted over.</summary>
    internal const int BurstWindowMs = 1;

    /// <summary>
    /// <see cref="MaxDelayMs"/> in <see cref="Stopwatch"/> ticks. Not read-only only so that a test that runs alone can
    /// make a wait outlast scheduling noise.
    /// </summary>
    internal static long MaxDelayTicks = Stopwatch.Frequency * MaxDelayMs / 1000;

    internal static readonly long BurstWindowTicks = Stopwatch.Frequency * BurstWindowMs / 1000;
}

/// <summary>
/// One reader's burst coalescing state (<see cref="PtyOutputCoalescing"/>), held by a <see cref="PtyCoalescingOutputReader"/>
/// and reused by each of its coalescing enumerations. Touched by the consumer, except <see cref="Deadline"/>,
/// <see cref="Scheduled"/>, and the expiry, which <see cref="PtyOutputCoalesceScheduler"/>'s tick reads and claims.
/// </summary>
internal sealed class PtyOutputCoalescer(PtyOutputPump pump)
{
    // Consumer side: whether the reader is in a burst, whether a coalescing wait is armed, and the bytes delivered in the
    // current window, which starts at _windowStart (Stopwatch ticks).
    private bool _inBurst;
    private bool _waitArmed;
    private int _windowBytes;
    private long _windowStart;
    // Set by the tick when it ended the armed wait at its deadline; read and cleared by the consumer when the wait ends.
    private int _expired;

    /// <summary>
    /// The armed coalescing wait's deadline in <see cref="Stopwatch"/> ticks, or 0 when none is armed. Written by the
    /// consumer when it arms the wait and when the wait ends, and claimed by <see cref="Expire"/>.
    /// </summary>
    internal long Deadline;

    /// <summary>Whether <see cref="PtyOutputCoalesceScheduler"/> watches this state. Guarded by the scheduler's lock.</summary>
    internal bool Scheduled;

    /// <summary>Whether the reader is in a burst. For tests, on the consumer's thread.</summary>
    internal bool IsInBurst => _inBurst;

    /// <summary>Starts an enumeration outside a burst.</summary>
    internal void Reset()
    {
        _inBurst = false;
        _waitArmed = false;
        _windowBytes = 0;
        _windowStart = 0;
        Volatile.Write(ref _expired, 0);
        Volatile.Write(ref Deadline, 0);
    }

    /// <summary>
    /// The unread output the next take needs: 1 outside a burst, or <see cref="PtyOutputCoalescing.MinimumBytes"/> in one
    /// while less is unread and output can still arrive.
    /// </summary>
    internal int NextMinimum()
    {
        if (!_inBurst
            || pump.UnreadCount >= PtyOutputCoalescing.MinimumBytes
            || pump.IsProducerFinished
            || pump.IsSessionDisposed)
        {
            return 1;
        }

        return PtyOutputCoalescing.MinimumBytes;
    }

    /// <summary>The reader armed a coalescing wait: it gets its deadline, which the scheduler watches.</summary>
    internal void OnWaitArmed()
    {
        _waitArmed = true;
        Volatile.Write(ref Deadline, Stopwatch.GetTimestamp() + PtyOutputCoalescing.MaxDelayTicks);
        PtyOutputCoalesceScheduler.Schedule(this);
    }

    /// <summary>
    /// The reader's wait completed, however it completed. A coalescing wait that ended at its deadline means output slowed
    /// down, which ends the burst.
    /// </summary>
    internal void OnWaitEnded()
    {
        if (!_waitArmed)
            return;

        _waitArmed = false;
        Volatile.Write(ref Deadline, 0);
        if (Interlocked.Exchange(ref _expired, 0) != 0)
        {
            _inBurst = false;
            _windowBytes = 0;
            _windowStart = 0;
        }
    }

    /// <summary>Counts a delivered chunk towards the burst window, and starts a burst once the window holds enough.</summary>
    internal void OnDelivered(int length)
    {
        if (_inBurst)
            return;

        var now = Stopwatch.GetTimestamp();
        if (now - _windowStart > PtyOutputCoalescing.BurstWindowTicks)
        {
            _windowStart = now;
            _windowBytes = 0;
        }

        _windowBytes += length;
        if (_windowBytes >= PtyOutputCoalescing.BurstBytes)
            _inBurst = true;
    }

    /// <summary>The enumeration ended. The scheduler drops state without a deadline at its next tick.</summary>
    internal void OnEnded()
    {
        _waitArmed = false;
        Volatile.Write(ref Deadline, 0);
    }

    /// <summary>
    /// Ends the armed coalescing wait if its deadline has passed at <paramref name="now"/>: the wait's minimum drops to 1
    /// byte, so what is unread is taken now (or the next publish is). Called by the scheduler's tick. Returns
    /// <see langword="true"/> once no deadline is left to watch.
    /// </summary>
    internal bool Expire(long now)
    {
        var deadline = Volatile.Read(ref Deadline);
        if (deadline == 0)
            return true;

        if (now < deadline)
            return false;

        // A consumer that ended the wait and armed another meanwhile changed the deadline; that one is looked at again.
        if (Interlocked.CompareExchange(ref Deadline, 0, deadline) != deadline)
            return false;

        Volatile.Write(ref _expired, 1);
        pump.RelaxWaitMinimum();
        return true;
    }
}

/// <summary>
/// Process-wide deadlines of coalescing reads: one timer ticks every <see cref="TickMs"/> while any reader waits in a burst,
/// and lowers the minimum of each wait whose deadline passed (<see cref="PtyOutputCoalescer.Expire"/>), as
/// <see cref="PtyOutputDrainScheduler"/> does for the post-exit drain. No reader holds a timer of its own, and the timer
/// stops while none waits.
/// </summary>
/// <remarks>
/// A deadline is served at the first tick after it, so a burst's last chunk waits up to about twice
/// <see cref="PtyOutputCoalescing.MaxDelayMs"/> where timers have millisecond resolution. Registration allocates nothing
/// after the state array has grown to the peak number of readers waiting at once.
/// </remarks>
internal static class PtyOutputCoalesceScheduler
{
    private const int TickMs = PtyOutputCoalescing.MaxDelayMs;

    private static readonly Lock s_lock = new();
    private static PtyOutputCoalescer?[] s_states = new PtyOutputCoalescer?[4];
    private static int s_count;
    private static Timer? s_timer;
    private static bool s_timerRunning;

    // Touched only by the tick, which runs one at a time.
    private static PtyOutputCoalescer?[] s_snapshot = new PtyOutputCoalescer?[4];
    private static int s_ticking;

    /// <summary>
    /// Watches <paramref name="state"/>'s deadline (<see cref="PtyOutputCoalescer.Deadline"/>), which its reader set before
    /// calling. A state already watched stays watched.
    /// </summary>
    internal static void Schedule(PtyOutputCoalescer state)
    {
        lock (s_lock)
        {
            if (state.Scheduled)
                return;

            state.Scheduled = true;
            if (s_count == s_states.Length)
                Array.Resize(ref s_states, s_count * 2);

            s_states[s_count++] = state;
            if (s_timerRunning)
                return;

            s_timerRunning = true;
            s_timer ??= CreateTimer();
            s_timer.Change(TickMs, TickMs);
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
        if (Interlocked.Exchange(ref s_ticking, 1) != 0)
            return;

        try
        {
            int count;
            lock (s_lock)
            {
                count = s_count;
                if (s_snapshot.Length < count)
                    s_snapshot = new PtyOutputCoalescer?[s_states.Length];

                Array.Copy(s_states, s_snapshot, count);
            }

            var now = Stopwatch.GetTimestamp();
            for (var i = 0; i < count; i++)
            {
                var state = s_snapshot[i]!;
                s_snapshot[i] = null;

                bool done;
                try
                {
                    done = state.Expire(now);
                }
                catch (Exception)
                {
                    // A timer callback must never throw: that would terminate the host process.
                    done = true;
                }

                if (done)
                    Remove(state);
            }
        }
        finally
        {
            Volatile.Write(ref s_ticking, 0);
        }
    }

    private static void Remove(PtyOutputCoalescer state)
    {
        lock (s_lock)
        {
            // State whose reader set a new deadline since this tick looked stays for the next tick.
            if (Volatile.Read(ref state.Deadline) != 0)
                return;

            for (var i = 0; i < s_count; i++)
            {
                if (s_states[i] != state)
                    continue;

                s_states[i] = s_states[--s_count];
                s_states[s_count] = null;
                state.Scheduled = false;
                break;
            }

            if (s_count == 0 && s_timerRunning)
            {
                s_timerRunning = false;
                s_timer!.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }
    }
}
