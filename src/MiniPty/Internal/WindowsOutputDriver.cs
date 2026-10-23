using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace MiniPty.Internal;

/// <summary>
/// The Windows output driver that holds no thread: overlapped reads of MiniPty's end of the ConPTY output pipe,
/// completed on the runtime's I/O completion port, with no buffer while the session is idle.
/// </summary>
/// <remarks>
/// <para>
/// <b>Waiting.</b> While nothing is unread, the driver waits with a zero-byte read, which completes when output arrives
/// and leaves it in the pipe, so the session holds no ring. It then takes a ring and reads what the pipe holds with reads
/// that complete at once (completion-port notification is skipped for those), and publishes each. While output it
/// published is still unread (a consumer that has not caught up), the next read is posted into the ring itself, which
/// saves a round trip during a burst; once the consumer has released everything, that read is canceled and replaced
/// by a zero-byte read, and the ring goes back to the pool. While the ring is full, no read is posted at all: the pipe
/// fills, ConPTY stops, and the consumer's release resumes reading.
/// </para>
/// <para>
/// <b>Running.</b> At most one read is posted at a time, and at most one step runs: a completion, or a work item queued
/// when a parked driver is woken. Steps run on thread-pool threads, never on a caller's thread, and never block: they
/// read, publish, and post the next read. A completion that arrives before the step that posted its read has finished
/// is handed to that step instead of running beside it. A step that keeps finding output goes back to the pool after
/// <see cref="MaxReadsPerStep"/> reads and continues as a new work item, so a flood shares the pool with other
/// sessions, and the step's pin on the pipe never keeps disposal from closing it for long.
/// </para>
/// <para>
/// <b>Stopping.</b> End of output is the pipe breaking when ConPTY's host exits. Disposal closes the pipe, which ends the
/// posted read; the forced end of output after the close (the pipe held open by another process) cancels it with
/// <c>CancelIoEx</c> and takes what the pipe holds without waiting. The ring stays held while a read is posted into it,
/// so a completion never writes into a ring another session owns.
/// </para>
/// <para>
/// Drivers are pooled across sessions like reader threads, because each owns a <see cref="PreAllocatedOverlapped"/>,
/// about 200 B to create. A session pays only its pipe's binding to the completion port (40 B).
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed unsafe class WindowsOutputDriver : IPtyOutputDriver, IThreadPoolWorkItem
{
    private const int ErrorInvalidHandle = 6;
    private const int ErrorBrokenPipe = 109;
    private const int ErrorPipeNotConnected = 233;
    private const int ErrorOperationAborted = 995;
    private const int ErrorIoPending = 997;
    private const byte FileSkipCompletionPortOnSuccess = 0x1;
    private const byte FileSkipSetEventOnHandle = 0x2;

    /// <summary>
    /// Reads that complete at once one step makes before it lets its pool thread go: 64 KiB of output at most, so a
    /// flood shares the pool with other sessions and a session being disposed lets go of its pipe.
    /// </summary>
    private const int MaxReadsPerStep = 16;

    // What the driver is doing, guarded by _lock.
    private const int StateIdle = 0;
    /// <summary>A step runs. Completions and wakes that arrive meanwhile are left for it.</summary>
    private const int StateRunning = 1;
    /// <summary>No read is posted: the ring is full, and a wake resumes the driver.</summary>
    private const int StateParked = 2;
    /// <summary>A zero-byte read is posted.</summary>
    private const int StateZeroPosted = 3;
    /// <summary>A read into the ring is posted.</summary>
    private const int StateRingPosted = 4;

    private static readonly IOCompletionCallback s_completed = OnCompleted;
    private static readonly Lock s_poolLock = new();
    // Free drivers, linked through _nextFree and guarded by s_poolLock.
    private static WindowsOutputDriver? s_free;
    // What a zero-byte read points at: never written, but a read must not be given a null buffer.
    private static readonly byte[] s_zeroByteTarget = GC.AllocateArray<byte>(1, pinned: true);

    private readonly Lock _lock = new();
    private readonly PreAllocatedOverlapped _overlapped;
    private WindowsOutputDriver? _nextFree;

    // The session being served. Set before the first step and cleared when its pump finishes.
    private PtyOutputPump? _pump;
    private SafeFileHandle? _pipe;
    private ThreadPoolBoundHandle? _binding;

    // Guarded by _lock.
    private int _state;

    /// <summary>The driver's state, for tests that report where a session stalled.</summary>
    internal int StateForTests => Volatile.Read(ref _state);
    // A wake arrived while a step ran; the step looks again before it parks.
    private bool _recheck;
    // The posted read was canceled already, so a second cancel cannot reach the read posted after it.
    private bool _cancelIssued;
    // A completion that arrived while the step that posted its read still ran.
    private bool _earlyCompletion;
    private uint _earlyError;
    private uint _earlyBytes;

    private WindowsOutputDriver()
    {
        _overlapped = PreAllocatedOverlapped.UnsafeCreate(s_completed, this, null);
    }

    /// <summary>
    /// Starts reading <paramref name="pipe"/> for <paramref name="pump"/>: what the Windows backend's
    /// <see cref="IPtyBackend.StartOutput"/> does. <paramref name="pipe"/> is the overlapped server end of a named pipe;
    /// the backend owns it and closes it at disposal, which ends reading.
    /// </summary>
    internal static void Run(PtyOutputPump pump, SafeFileHandle pipe)
    {
        var driver = Rent();
        ThreadPoolBoundHandle binding;
        try
        {
            binding = ThreadPoolBoundHandle.BindHandle(pipe);
        }
        catch
        {
            Return(driver);
            throw;
        }

        // A read that completes at once is handled by the step that posted it; a queued completion would handle it twice.
        if (!WindowsInterop.SetFileCompletionNotificationModes(pipe, FileSkipCompletionPortOnSuccess | FileSkipSetEventOnHandle))
        {
            var error = Marshal.GetLastPInvokeError();
            binding.Dispose();
            Return(driver);
            throw new Win32Exception(error, "SetFileCompletionNotificationModes failed");
        }

        lock (driver._lock)
        {
            driver._pump = pump;
            driver._pipe = pipe;
            driver._binding = binding;
            driver._state = StateRunning;
            driver._recheck = false;
            driver._cancelIssued = false;
            driver._earlyCompletion = false;
        }

        pump.AttachDriver(driver);

        // The first step runs on the pool, so no output is read or published on the thread that started the session.
        ThreadPool.UnsafeQueueUserWorkItem(driver, preferLocal: false);
    }

    /// <summary>
    /// Resumes a driver parked on a full ring; cancels a read posted into the ring once nothing in it is unread, so the
    /// ring can go back; otherwise has the running step look again. Never blocks, and ignores a pump it no longer serves.
    /// </summary>
    public void Wake(PtyOutputPump pump)
    {
        var resume = false;
        lock (_lock)
        {
            if (_pump != pump)
                return;

            switch (_state)
            {
                case StateParked:
                    _state = StateRunning;
                    resume = true;
                    break;
                case StateRunning:
                    _recheck = true;
                    break;
                case StateRingPosted:
                    if (!pump.HasUnreadOutput && !pump.IsSessionDisposed)
                        CancelPostedReadLocked();
                    break;
            }
        }

        if (resume)
            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
    }

    /// <summary>
    /// Cancels the posted read, so the step that handles its completion re-checks the pump: the forced end of output,
    /// and disposal of a session whose pipe another process holds open. A cancel that finds no read posted is lost; the
    /// pump repeats it while it still needs it.
    /// </summary>
    public void CancelRead(PtyOutputPump pump)
    {
        lock (_lock)
        {
            if (_pump != pump)
                return;

            if (_state is StateZeroPosted or StateRingPosted)
                CancelPostedReadLocked();
            else if (_state == StateRunning)
                _recheck = true;
        }
    }

    /// <summary>
    /// The pump finished: release the binding and go back to the pool. Called by <see cref="PtyOutputPump.Finish"/> from
    /// the driver's last step, with no read posted.
    /// </summary>
    public void OnPumpFinished(PtyOutputPump pump)
    {
        ThreadPoolBoundHandle? binding;
        lock (_lock)
        {
            if (_pump != pump)
                return;

            binding = _binding;
            _pump = null;
            _pipe = null;
            _binding = null;
            _state = StateIdle;
        }

        binding?.Dispose();
        Return(this);
    }

    /// <summary>A step queued by <see cref="Run"/> or by a wake of a parked driver.</summary>
    void IThreadPoolWorkItem.Execute()
    {
        // A driver parked on a full ring ends the free-space handshake here; otherwise the drain would never see the
        // transport go quiet.
        var pump = _pump!;
        if (pump.IsWaitingForFreeSpace)
            pump.EndWaitForFreeSpace();

        Step(Completion.None, 0, 0);
    }

    private static void OnCompleted(uint errorCode, uint numBytes, NativeOverlapped* overlapped)
    {
        var driver = (WindowsOutputDriver)ThreadPoolBoundHandle.GetNativeOverlappedState(overlapped)!;
        driver.OnReadCompleted(errorCode, numBytes, overlapped);
    }

    /// <summary>A posted read completed, on a pool thread. Never throws.</summary>
    private void OnReadCompleted(uint errorCode, uint numBytes, NativeOverlapped* overlapped)
    {
        int posted;
        lock (_lock)
        {
            _binding!.FreeNativeOverlapped(overlapped);
            posted = _state;
            if (posted == StateRunning)
            {
                // The step that posted this read has not finished yet; it takes the result.
                _earlyCompletion = true;
                _earlyError = errorCode;
                _earlyBytes = numBytes;
                return;
            }

            _state = StateRunning;
        }

        Step(posted == StateRingPosted ? Completion.Ring : Completion.ZeroByte, errorCode, numBytes);
    }

    private enum Completion
    {
        None,
        ZeroByte,
        Ring,
    }

    /// <summary>
    /// Reads until a read stays posted, the ring is full, or output ends. Runs with <see cref="StateRunning"/>, on a pool
    /// thread, and never throws: a failure ends the pump with it.
    /// </summary>
    private void Step(Completion completed, uint errorCode, uint numBytes)
    {
        var pump = _pump!;
        Exception? error = null;
        try
        {
            if (Continue(pump, completed, errorCode, numBytes))
                return;
        }
        catch (Exception ex)
        {
            error = ex;
        }

        // End of output. No read is posted, so the ring and the driver can go.
        pump.Finish(error);
    }

    /// <summary>Returns <see langword="true"/> while reading goes on (a read is posted or the driver parked).</summary>
    private bool Continue(PtyOutputPump pump, Completion completed, uint errorCode, uint numBytes)
    {
        var pipe = _pipe!;
        // Disposal can close the pipe at any moment, and a marshalled call that finds it closed throws, which costs an
        // allocation per session. The pipe is pinned for the whole step instead, and calls use its raw value; a close
        // waits for the pin, and the posted read it then ends completes here later.
        if (pipe.IsClosed)
            return false;

        var pinned = false;
        try
        {
            pipe.DangerousAddRef(ref pinned);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        try
        {
            var handle = pipe.DangerousGetHandle();
            var dataReady = false;
            switch (completed)
            {
                case Completion.ZeroByte:
                    if (errorCode == 0)
                        dataReady = true;
                    else if (errorCode != ErrorOperationAborted)
                        return EndOrThrow((int)errorCode);
                    break;
                case Completion.Ring:
                    pump.ClearWakeWhenEmpty();
                    if (errorCode == 0 || (errorCode == ErrorOperationAborted && numBytes > 0))
                    {
                        if (numBytes > 0)
                            Publish(pump, (int)numBytes);
                    }
                    else if (errorCode != ErrorOperationAborted)
                    {
                        return EndOrThrow((int)errorCode);
                    }

                    break;
            }

            return ReadLoop(pump, handle, dataReady);
        }
        finally
        {
            if (pinned)
                pipe.DangerousRelease();
        }
    }

    /// <summary>
    /// The driver's loop: publish what reads complete at once, and post the next read or park. Returns
    /// <see langword="false"/> at end of output.
    /// </summary>
    private bool ReadLoop(PtyOutputPump pump, IntPtr handle, bool dataReady)
    {
        var readsNow = 0;
        while (true)
        {
            // A step that keeps finding output never posts a read, so it would keep its pool thread, and its pin on the
            // pipe, for as long as the child writes: other sessions would wait, and disposal could not close the pipe that
            // ends the flood. It goes back to the pool after a bounded number of reads.
            if (readsNow == MaxReadsPerStep)
            {
                ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
                return true;
            }

            var disposed = pump.IsSessionDisposed;
            if (!disposed)
            {
                pump.DeliverBacklogToSink();
                if (pump.IsFull)
                {
                    if (pump.BeginWaitForFreeSpace() && TryPark())
                        return true;

                    pump.EndWaitForFreeSpace();
                    continue;
                }
            }

            if (pump.IsEndOfOutputForced)
            {
                // Take what the pipe holds without waiting, then report end of output.
                var available = BytesAvailable(handle);
                if (available <= 0)
                    return false;

                pump.HoldRing();
                var span = pump.NextReadSpan;
                var read = ReadNow(handle, span[..Math.Min(span.Length, available)], out var posted);
                if (posted)
                {
                    // Not expected with bytes waiting; the next forced-end cancel releases it.
                    if (EnterPosted(pump, StateRingPosted, out var early))
                        return true;

                    if (!HandleEarlyRing(pump, early))
                        return false;

                    continue;
                }

                if (read <= 0)
                    return false;

                Publish(pump, read);
                readsNow++;
                continue;
            }

            // After disposal reads only let the transport end, into the ring, which nothing reads any more.
            if (disposed || dataReady || pump.HasUnreadOutput)
            {
                dataReady = false;
                pump.HoldRing();
                var read = ReadNow(handle, pump.NextReadSpan, out var posted);
                if (posted)
                {
                    if (EnterPosted(pump, StateRingPosted, out var early))
                        return true;

                    if (!HandleEarlyRing(pump, early))
                        return false;

                    continue;
                }

                if (read < 0)
                    return false;

                if (read > 0)
                    Publish(pump, read);

                readsNow++;
                continue;
            }

            // Nothing is unread: wait for output without a buffer.
            pump.LetGoOfRing();
            var zero = ReadNow(handle, Span<byte>.Empty, out var zeroPosted);
            if (zeroPosted)
            {
                if (EnterPosted(pump, StateZeroPosted, out var early))
                    return true;

                if (early.Error != 0 && early.Error != ErrorOperationAborted)
                    return EndOrThrow((int)early.Error);

                dataReady = early.Error == 0;
                continue;
            }

            if (zero < 0)
                return false;

            // Output is waiting in the pipe.
            dataReady = true;
            readsNow++;
        }
    }

    /// <summary>Handles a ring read that completed before its step left; returns <see langword="false"/> at end of output.</summary>
    private static bool HandleEarlyRing(PtyOutputPump pump, (uint Error, uint Bytes) early)
    {
        pump.ClearWakeWhenEmpty();
        if (early.Error == 0 || early.Error == ErrorOperationAborted)
        {
            if (early.Bytes > 0)
                Publish(pump, (int)early.Bytes);

            return true;
        }

        return EndOrThrow((int)early.Error);
    }

    /// <summary>
    /// Leaves the step with a read posted. Returns <see langword="false"/> when the read's completion already arrived,
    /// with its result, and the step goes on; otherwise the completion runs the next step.
    /// </summary>
    private bool EnterPosted(PtyOutputPump pump, int posted, out (uint Error, uint Bytes) early)
    {
        // A read posted into the ring keeps the ring; once the consumer has released everything, it is canceled so the
        // ring can go back. Asked for before the state is published, because from then on the completion may run the
        // next step at once, and this step must not touch the driver.
        var watchEmpty = posted == StateRingPosted && !pump.IsSessionDisposed;
        if (watchEmpty)
            pump.WakeDriverWhenEmpty();

        lock (_lock)
        {
            if (_earlyCompletion)
            {
                _earlyCompletion = false;
                early = (_earlyError, _earlyBytes);
                return false;
            }

            _state = posted;
            _cancelIssued = false;
            _recheck = false;
            // The consumer may have released everything before the request above; a wake after it finds this state.
            if (watchEmpty && !pump.HasUnreadOutput)
                CancelPostedReadLocked();
        }

        early = default;
        return true;
    }

    /// <summary>Parks on a full ring unless a wake arrived during the step. Returns whether it parked.</summary>
    private bool TryPark()
    {
        lock (_lock)
        {
            if (_recheck)
            {
                _recheck = false;
                return false;
            }

            _state = StateParked;
            return true;
        }
    }

    /// <summary>
    /// Cancels the posted read once. Its completion runs the next step, which re-checks the pump. Caller holds _lock and
    /// has checked that a read is posted, so the cancel cannot reach a later read.
    /// </summary>
    private void CancelPostedReadLocked()
    {
        if (_cancelIssued)
            return;

        var pipe = _pipe!;
        if (pipe.IsClosed)
            return;

        var pinned = false;
        try
        {
            pipe.DangerousAddRef(ref pinned);
            _cancelIssued = true;
            // Only this driver reads the pipe, and it posts one read at a time, so this names the posted read.
            _ = WindowsInterop.CancelIoEx(pipe.DangerousGetHandle(), IntPtr.Zero);
        }
        catch (ObjectDisposedException)
        {
            // Closed since the check: the close ends the read.
        }
        finally
        {
            if (pinned)
                pipe.DangerousRelease();
        }
    }

    private static void Publish(PtyOutputPump pump, int length)
    {
        var timestamp = Stopwatch.GetTimestamp();
        pump.OnOutputRead();
        pump.Publish(length, timestamp);
    }

    /// <summary>
    /// Issues one overlapped read into <paramref name="buffer"/> (zero bytes when empty). Returns the bytes it read at
    /// once, with <paramref name="posted"/> false; or sets <paramref name="posted"/> when the read stays posted; or returns
    /// -1 at end of output.
    /// </summary>
    private int ReadNow(IntPtr handle, Span<byte> buffer, out bool posted)
    {
        posted = false;
        var binding = _binding!;
        var overlapped = binding.AllocateNativeOverlapped(_overlapped);
        // The ring is pinned for its whole life, and the zero-byte target is pinned too.
        var target = buffer.IsEmpty
            ? (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(s_zeroByteTarget))
            : (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(buffer));
        if (WindowsInterop.ReadFile(handle, target, (uint)buffer.Length, IntPtr.Zero, overlapped))
        {
            // Completed at once. Completion-port notification is skipped on success, so no callback follows.
            var read = (int)overlapped->InternalHigh;
            binding.FreeNativeOverlapped(overlapped);
            return read;
        }

        var error = Marshal.GetLastPInvokeError();
        if (error == ErrorIoPending)
        {
            posted = true;
            return 0;
        }

        binding.FreeNativeOverlapped(overlapped);
        if (IsEndOfOutput(error))
            return -1;

        throw new IOException($"ReadFile failed (Win32 {error})");
    }

    /// <summary>Bytes waiting in the pipe, or -1 at end of output.</summary>
    private static int BytesAvailable(IntPtr handle)
    {
        uint available;
        if (WindowsInterop.PeekNamedPipe(handle, IntPtr.Zero, 0, IntPtr.Zero, &available, IntPtr.Zero))
            return (int)Math.Min(available, int.MaxValue);

        var error = Marshal.GetLastPInvokeError();
        if (IsEndOfOutput(error))
            return -1;

        throw new IOException($"PeekNamedPipe failed (Win32 {error})");
    }

    private static bool IsEndOfOutput(int error) =>
        error is ErrorBrokenPipe or ErrorInvalidHandle or ErrorPipeNotConnected;

    /// <summary>A read that failed ends output when the pipe broke or closed; any other failure is the pump's error.</summary>
    private static bool EndOrThrow(int error) =>
        IsEndOfOutput(error) ? false : throw new IOException($"ReadFile failed (Win32 {error})");

    private static WindowsOutputDriver Rent()
    {
        lock (s_poolLock)
        {
            var driver = s_free;
            if (driver is not null)
            {
                s_free = driver._nextFree;
                driver._nextFree = null;
                return driver;
            }
        }

        return new WindowsOutputDriver();
    }

    private static void Return(WindowsOutputDriver driver)
    {
        lock (s_poolLock)
        {
            driver._nextFree = s_free;
            s_free = driver;
        }
    }
}
