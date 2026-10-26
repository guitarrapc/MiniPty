using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace MiniPty.Internal;

/// <summary>
/// A periodic tick at about millisecond resolution on Windows, for <see cref="PtyOutputCoalesceScheduler"/>. .NET timers
/// fire at the system timer's resolution there, about 15.6 ms, so a coalescing wait they served lasted about 15 ms where it
/// lasts up to about 2 ms elsewhere. A high-resolution waitable timer fires about every 1.5 ms for a 1 ms period, and does
/// not raise the system timer's resolution for the whole process, as <c>timeBeginPeriod</c> would.
/// </summary>
/// <remarks>
/// <para>
/// A thread of its own waits for the timer and runs the tick, which only checks deadlines and signals consumers. Waiting on
/// the thread pool instead (<c>ThreadPool.RegisterWaitForSingleObject</c>) cost 35–44 µs of CPU per tick with the
/// runtime's own pool, whose wait thread hands each signal to a worker, against 4–9 µs here: about 3% of a core while a
/// flood kept the tick running. The pool's wait thread would have been a thread for as long as this one is, too.
/// </para>
/// <para>
/// Stopping ends the period but keeps the thread for <see cref="LingerMs"/>, so a scheduler that starts and stops with each
/// burst, as a large repaint makes it, starts no thread per burst. Once the linger passes with no start, the thread exits.
/// </para>
/// <para>
/// <see cref="Start"/> and <see cref="Stop"/> run under the scheduler's lock. Requires Windows 10 1803 or later, which every
/// ConPTY is; <see cref="TryCreate"/> returns null elsewhere, and the scheduler keeps its <see cref="Timer"/>.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WindowsPreciseTick
{
    private const int LingerMs = 30_000;
    private const uint Infinite = 0xFFFFFFFF;

    private readonly SafeWaitHandle _timer;
    private readonly Action _tick;
    // Guards the thread's lifetime against its own decision to exit.
    private readonly Lock _lock = new();
    private Thread? _thread;
    private bool _running;
    private long _stoppedAt;

    private WindowsPreciseTick(SafeWaitHandle timer, Action tick)
    {
        _timer = timer;
        _tick = tick;
    }

    /// <summary>
    /// Creates the process's tick around <paramref name="tick"/>, or returns null when Windows cannot create a
    /// high-resolution timer. The timer lives as long as the process.
    /// </summary>
    internal static WindowsPreciseTick? TryCreate(Action tick)
    {
        var timer = WindowsInterop.CreateWaitableTimerExW(
            IntPtr.Zero,
            IntPtr.Zero,
            WindowsInterop.CreateWaitableTimerHighResolution,
            WindowsInterop.TimerModifyStateAndSynchronize);
        if (timer.IsInvalid)
        {
            timer.Dispose();
            return null;
        }

        return new WindowsPreciseTick(timer, tick);
    }

    /// <summary>
    /// Ticks every <paramref name="periodMs"/> from now on. Returns <see langword="false"/> when the timer cannot be armed or
    /// its thread cannot start.
    /// </summary>
    internal bool Start(int periodMs)
    {
        lock (_lock)
        {
            if (!WindowsInterop.SetWaitableTimerEx(_timer, -periodMs * 10_000L, periodMs, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0))
                return false;

            Volatile.Write(ref _running, true);
            if (_thread is not null)
                return true;

            try
            {
                var thread = new Thread(Run) { IsBackground = true, Name = "MiniPty coalescing tick" };
                // Process-wide and long-lived: it must not keep the first scheduling caller's execution context.
                thread.UnsafeStart();
                _thread = thread;
                return true;
            }
            catch (Exception)
            {
                Volatile.Write(ref _running, false);
                WindowsInterop.CancelWaitableTimer(_timer);
                return false;
            }
        }
    }

    /// <summary>
    /// Stops ticking. The timer is armed once more, for the end of the linger, when the thread exits unless the tick started
    /// again meanwhile.
    /// </summary>
    internal void Stop()
    {
        lock (_lock)
        {
            if (!_running)
                return;

            Volatile.Write(ref _running, false);
            _stoppedAt = Stopwatch.GetTimestamp();
            // Arming the timer again also drops a period's signal still pending. If it fails, the timer is canceled, and
            // the thread waits for the next start.
            if (!WindowsInterop.SetWaitableTimerEx(_timer, -LingerMs * 10_000L, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0))
                WindowsInterop.CancelWaitableTimer(_timer);
        }
    }

    private void Run()
    {
        var timer = _timer.DangerousGetHandle();
        while (true)
        {
            if (WindowsInterop.WaitForSingleObject(timer, Infinite) == WindowsInterop.WaitFailed)
            {
                // Never seen, since the handle stays open; a failing wait must not spin a core. Ticks go on, coarsely.
                Thread.Sleep(1);
            }

            // A stop that lands right after this read lets one more tick run, which finds nothing to serve.
            if (Volatile.Read(ref _running))
            {
                try
                {
                    _tick();
                }
                catch (Exception)
                {
                    // An exception escaping a thread terminates the process; a tick that failed serves the next deadline later.
                }

                continue;
            }

            lock (_lock)
            {
                // Stopped: a period's signal taken just before the stop arrives at once, and the linger's after LingerMs.
                // Only the linger's ends the thread.
                if (!_running && Stopwatch.GetElapsedTime(_stoppedAt).TotalMilliseconds >= LingerMs / 2)
                {
                    _thread = null;
                    return;
                }
            }
        }
    }
}
