namespace MiniPty.Internal;

/// <summary>
/// Process-wide post-exit drain: one timer ticks every <see cref="TickMs"/> while any session is between child
/// exit and end of output, and lets each such pump decide whether to close its transport (see
/// <see cref="PtyOutputPump.OnDrainTick"/>).
/// </summary>
/// <remarks>
/// <para>
/// ConPTY reports end of output only after the pseudo-console closes, and a Unix grandchild can hold the slave
/// open after the child exits, so something has to close the transport once output has gone quiet. One shared
/// timer does that for every session: no session holds a thread or a timer of its own for it, and the timer
/// stops while no session is draining. Ticks run on the thread pool and only check state or close a transport.
/// </para>
/// <para>
/// Registration allocates nothing per session after the pump array has grown to the peak number of draining
/// sessions.
/// </para>
/// </remarks>
internal static class PtyOutputDrainScheduler
{
    private const int TickMs = 10;

    private static readonly Lock s_lock = new();
    private static PtyOutputPump?[] s_pumps = new PtyOutputPump?[4];
    private static int s_count;
    private static Timer? s_timer;
    private static bool s_timerRunning;

    // Touched only by the tick, which runs one at a time.
    private static PtyOutputPump?[] s_snapshot = new PtyOutputPump?[4];
    private static int s_ticking;

    /// <summary>Starts ticking <paramref name="pump"/>, or keeps it for another tick if it is already scheduled.</summary>
    internal static void Schedule(PtyOutputPump pump)
    {
        lock (s_lock)
        {
            if (pump.DrainScheduled)
            {
                // A tick may be deciding to drop this pump right now on stale state; keep it for one more look.
                pump.DrainRescheduled = true;
                return;
            }

            pump.DrainScheduled = true;
            if (s_count == s_pumps.Length)
                Array.Resize(ref s_pumps, s_count * 2);

            s_pumps[s_count++] = pump;
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
        // A slow tick (a pseudo-console close can take a moment) must not overlap the next one.
        if (Interlocked.Exchange(ref s_ticking, 1) != 0)
            return;

        try
        {
            int count;
            lock (s_lock)
            {
                count = s_count;
                if (s_snapshot.Length < count)
                    s_snapshot = new PtyOutputPump?[s_pumps.Length];

                Array.Copy(s_pumps, s_snapshot, count);
            }

            var now = Environment.TickCount64;
            for (var i = 0; i < count; i++)
            {
                var pump = s_snapshot[i]!;
                s_snapshot[i] = null;

                bool done;
                try
                {
                    done = pump.OnDrainTick(now);
                }
                catch (Exception)
                {
                    // A timer callback must never throw: that would terminate the host process.
                    done = true;
                }

                if (done)
                    Remove(pump);
            }
        }
        finally
        {
            Volatile.Write(ref s_ticking, 0);
        }
    }

    private static void Remove(PtyOutputPump pump)
    {
        lock (s_lock)
        {
            if (pump.DrainRescheduled)
            {
                pump.DrainRescheduled = false;
                return;
            }

            for (var i = 0; i < s_count; i++)
            {
                if (s_pumps[i] != pump)
                    continue;

                s_pumps[i] = s_pumps[--s_count];
                s_pumps[s_count] = null;
                pump.DrainScheduled = false;
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
