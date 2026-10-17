using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MiniPty.Internal;

/// <summary>
/// Dedicated background threads that run session output pumps, so blocking transport reads never occupy a
/// thread-pool worker and never run on a caller's thread.
/// </summary>
/// <remarks>
/// <para>
/// A pump owns its thread for as long as it reads. Once its transport reaches end of output, the thread parks
/// and serves the next session's pump; a thread idle for <see cref="IdleTimeoutMs"/> exits. Reuse keeps
/// steady-state spawning allocation-free: a new thread costs its managed objects, and the first pooled-array
/// return on a new thread allocates that thread's array-pool cache (one-shot completion grows its result
/// buffers on the reader thread).
/// </para>
/// <para>
/// Each thread owns one wake primitive: an auto-reset event on Windows, an eventfd or pipe on Unix. The running
/// pump waits on it while its ring is full, and on Unix together with the PTY master while it reads, because
/// closing a descriptor does not interrupt a read blocked on it in another thread. A wake addressed to a pump
/// that has finished is dropped, so a late signal never reaches another session, and the primitive is never
/// touched after the thread retires.
/// </para>
/// <para>
/// A Windows pump reads its pipe with a blocking <c>ReadFile</c> that no event can interrupt. The thread
/// therefore also holds a handle to itself, so <see cref="CancelRead"/> can release that read with
/// <c>CancelSynchronousIo</c> when the pipe's writer never goes away (see <see cref="PtyOutputPump.OnDrainTick"/>).
/// </para>
/// </remarks>
internal sealed class PtyReaderThread
{
    private const int IdleTimeoutMs = 30_000;
    private const uint Infinite = 0xFFFFFFFF;
    private const uint ThreadTerminate = 0x0001;

    private static readonly Lock s_lock = new();
    // Idle threads, most recently parked first, linked through _nextIdle and guarded by s_lock.
    private static PtyReaderThread? s_idle;

    // Guards _pump, so a wake is signaled only while its pump still runs here.
    private readonly Lock _lock = new();
    private readonly IntPtr _event;
    private readonly int _wakeRead = -1;
    private readonly int _wakeWrite = -1;
    // Windows: this thread's own handle, opened by the thread itself when it starts. Guarded by _lock.
    private IntPtr _threadHandle;
    private PtyOutputPump? _pump;
    private PtyReaderThread? _nextIdle;
    private bool _idle;

    private PtyReaderThread()
    {
        if (OperatingSystem.IsWindows())
        {
            _event = WindowsInterop.CreateEventW(IntPtr.Zero, bManualReset: false, bInitialState: false, IntPtr.Zero);
            if (_event == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateEvent failed");

            return;
        }

        int read;
        int write;
        unsafe
        {
            if (UnixInterop.minipty_wake_open(&read, &write) != 0)
                throw new IOException($"Creating the output reader wake descriptor failed (errno {Marshal.GetLastPInvokeError()})");
        }

        _wakeRead = read;
        _wakeWrite = write;
    }

    /// <summary>Gets the descriptor a Unix pump waits on together with the PTY master.</summary>
    internal int WakeFd => _wakeRead;

    /// <summary>Runs <paramref name="pump"/> on an idle reader thread, or on a new one.</summary>
    internal static void Run(PtyOutputPump pump)
    {
        PtyReaderThread? reader;
        lock (s_lock)
        {
            reader = s_idle;
            if (reader is not null)
            {
                s_idle = reader._nextIdle;
                reader._nextIdle = null;
                reader._idle = false;
            }
        }

        if (reader is not null)
        {
            reader.Assign(pump);
            return;
        }

        reader = new PtyReaderThread();
        reader.Assign(pump);
        var thread = new Thread(reader.Loop)
        {
            IsBackground = true,
            Name = "MiniPty output reader",
        };

        // The thread outlives the session that created it, so it must not keep that caller's execution context.
        thread.UnsafeStart();
    }

    /// <summary>Wakes this thread if it is still running <paramref name="pump"/>; otherwise does nothing.</summary>
    internal void Wake(PtyOutputPump pump)
    {
        lock (_lock)
        {
            if (_pump == pump)
                Signal();
        }
    }

    /// <summary>
    /// Interrupts whatever this thread is blocked in for <paramref name="pump"/>: the wake wait, the Unix wait on
    /// the master, or a Windows blocking <c>ReadFile</c>, which <c>CancelSynchronousIo</c> is the only way to release
    /// while the pipe's writer stays alive. Does nothing once the thread has moved on, and the lock orders the cancel
    /// against that handover, so it never reaches another session's read. A cancel that finds no read in progress
    /// is lost; a caller that depends on it repeats it.
    /// </summary>
    internal void CancelRead(PtyOutputPump pump)
    {
        lock (_lock)
        {
            if (_pump != pump)
                return;

            Signal();
            if (OperatingSystem.IsWindows() && _threadHandle != IntPtr.Zero)
                WindowsInterop.CancelSynchronousIo(_threadHandle);
        }
    }

    /// <summary>
    /// Blocks the running pump until a wake arrives or <paramref name="timeoutMs"/> elapses
    /// (<see cref="Timeout.Infinite"/> waits indefinitely). Spurious returns are possible; callers re-check state.
    /// </summary>
    internal bool WaitForWake(int timeoutMs)
    {
        if (OperatingSystem.IsWindows())
        {
            var result = WindowsInterop.WaitForSingleObject(_event, timeoutMs < 0 ? Infinite : (uint)timeoutMs);
            if (result == WindowsInterop.WaitFailed)
            {
                // Not expected for an event this thread owns; back off rather than spin.
                Thread.Sleep(10);
                return false;
            }

            return result == WindowsInterop.WaitObject0;
        }

        var ready = UnixInterop.minipty_pty_wait(-1, 0, _wakeRead, timeoutMs);
        if (ready < 0)
        {
            Thread.Sleep(10);
            return false;
        }

        if (ready == 0)
            return false;

        ConsumeWake();
        return true;
    }

    /// <summary>Clears a pending Unix wake after a combined master/wake wait reported it.</summary>
    internal void ConsumeWake()
    {
        if (!OperatingSystem.IsWindows())
            UnixInterop.minipty_wake_drain(_wakeRead);
    }

    private void Assign(PtyOutputPump pump)
    {
        pump.AttachReader(this);
        lock (_lock)
        {
            _pump = pump;
            Signal();
        }
    }

    private void Signal()
    {
        if (OperatingSystem.IsWindows())
            WindowsInterop.SetEvent(_event);
        else
            UnixInterop.minipty_wake_signal(_wakeWrite);
    }

    private void Loop()
    {
        if (OperatingSystem.IsWindows())
        {
            // CancelSynchronousIo needs THREAD_TERMINATE on the thread whose read it cancels. Only the thread itself
            // knows its id, so the handle is opened here rather than by the creator. A cancel issued before this
            // runs is lost, which is harmless: the thread has not started reading yet.
            var handle = WindowsInterop.OpenThread(ThreadTerminate, bInheritHandle: false, WindowsInterop.GetCurrentThreadId());
            lock (_lock)
                _threadHandle = handle;
        }

        while (true)
        {
            PtyOutputPump? pump;
            lock (_lock)
                pump = _pump;

            if (pump is null)
            {
                if (!WaitForWake(IdleTimeoutMs) && TryRetire())
                    return;

                continue;
            }

            // Never throws: the pump records failures for its consumers. It parks this thread itself, before it
            // publishes completion; the call below only covers a pump that ended without doing so.
            pump.Run();
            OnPumpFinished(pump);
        }
    }

    /// <summary>
    /// Releases this thread from <paramref name="pump"/> and parks it in the idle list, so it can serve another
    /// session while it finishes the tail of the old pump's completion. Does nothing once the thread has moved on.
    /// </summary>
    internal void OnPumpFinished(PtyOutputPump pump)
    {
        lock (_lock)
        {
            if (_pump != pump)
                return;

            _pump = null;
        }

        // Drop wakes aimed at the finished pump before serving another one. A pump handed over after this point
        // is picked up by the loop's next look at _pump, so a wake lost here never strands it.
        if (OperatingSystem.IsWindows())
            WindowsInterop.WaitForSingleObject(_event, 0);
        else
            ConsumeWake();

        lock (s_lock)
        {
            _idle = true;
            _nextIdle = s_idle;
            s_idle = this;
        }
    }

    /// <summary>Leaves the idle list after the idle timeout, unless a pump was handed to this thread meanwhile.</summary>
    private bool TryRetire()
    {
        lock (s_lock)
        {
            if (!_idle)
                return false;

            if (s_idle == this)
            {
                s_idle = _nextIdle;
            }
            else
            {
                var node = s_idle;
                while (node is not null && node._nextIdle != this)
                    node = node._nextIdle;
                if (node is not null)
                    node._nextIdle = _nextIdle;
            }

            _nextIdle = null;
            _idle = false;
        }

        // Nothing can hand this thread a pump any more, and Wake and CancelRead ignore it because _pump is null.
        if (OperatingSystem.IsWindows())
        {
            WindowsInterop.CloseHandle(_event);
            IntPtr threadHandle;
            lock (_lock)
            {
                threadHandle = _threadHandle;
                _threadHandle = IntPtr.Zero;
            }

            if (threadHandle != IntPtr.Zero)
                WindowsInterop.CloseHandle(threadHandle);
        }
        else
        {
            UnixInterop.minipty_wake_close(_wakeRead, _wakeWrite);
        }

        return true;
    }
}
