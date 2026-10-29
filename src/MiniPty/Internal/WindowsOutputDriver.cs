using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace MiniPty.Internal;

/// <summary>
/// The Windows output driver: overlapped reads of MiniPty's end of the ConPTY output pipe, completed on a completion port
/// of MiniPty's own that one thread waits on for every session (<see cref="Loop"/>), with no buffer while the session is
/// idle.
/// </summary>
/// <remarks>
/// <para>
/// <b>Waiting.</b> The driver always waits with a zero-byte read, which completes when output arrives and leaves it in the
/// pipe. It then takes a ring, reads what the pipe holds with a read that completes at once (completion-port
/// notification is skipped for those), publishes it, and waits again. A wait that has to stay posted lets go of the
/// ring, which goes back to the pool once the consumer has released everything, so an idle session holds none. While
/// the ring is full, no read is posted at all: the pipe fills, ConPTY stops, and the consumer's release resumes reading.
/// </para>
/// <para>
/// The driver never posts a read into the ring while it waits, even when published output is still unread. Such a read
/// would save the zero-byte read's round trip, but it holds the ring, and a consumer that keeps up releases everything
/// right after each publish, so the read must then be canceled with <c>CancelIoEx</c> to give the ring back: one more
/// completion per write. The out-of-band ConPTY writes a flood about 256 B at a time, so that cost a third more work
/// items and CPU than the zero-byte wait alone. Reads into the ring stay posted only after disposal, to let the transport
/// end, and in the forced end of output.
/// </para>
/// <para>
/// <b>Running.</b> Every step runs on the loop's thread: a completion, or a look the loop was asked for (the driver's
/// first step, a wake of a driver parked on a full ring, a cancel, or a step that reached its bound). Steps never block:
/// they read, publish, and post the next read. At most one read is posted at a time, and a read that completes at once
/// queues no completion, so the loop never handles a completion while the step that posted its read runs. A step that
/// keeps finding output stops after <see cref="MaxReadsPerStep"/> reads and has the loop look at it again after the
/// other sessions, so a flood shares the loop, and the step's pin on the pipe never keeps disposal from closing it for
/// long. The pump completes a consumer's wait asynchronously, so no consumer code runs on the loop.
/// </para>
/// <para>
/// <b>Why a thread of its own.</b> Bound to the runtime's completion port, a pipe's completion reached a pool worker
/// through the runtime's I/O poller thread: two wakes, and a pool work item per ConPTY write besides the consumer's own.
/// A 1-byte write took 23–33 μs to reach the reader that way, against 11–14 μs on a port of its own with a thread waiting
/// on it, as quick as a thread blocked in <c>ReadFile</c>; that hop was most of what kept MiniPty's keystrokes behind
/// Porta.Pty's on 2 CPUs. One thread serves every session, as on Unix, and starts with the first.
/// </para>
/// <para>
/// <b>No reads on the consumer's thread.</b> On Unix a consumer that finds its ring empty reads the master itself
/// (<see cref="IPtyOutputDriver.TryReadInline"/>), because a child there writes faster than a consumer reads. ConPTY renders
/// more slowly than a consumer reads, so a consumer reading the pipe itself found it empty after nearly every write
/// (about 4,000 waits per MiB, as many as writes), and its zero-byte read, posted only once it came back, caught ConPTY's
/// next write later than the one this driver posts right after it publishes. On 2 CPUs floods were slower that way and
/// cost more CPU, so this driver keeps the default.
/// </para>
/// <para>
/// <b>Stopping.</b> End of output is the pipe breaking when ConPTY's host exits. Disposal closes the pipe, which ends the
/// posted read; the forced end of output after the close (the pipe held open by another process) cancels it with
/// <c>CancelIoEx</c> and takes what the pipe holds without waiting. The ring stays held while a read is posted into it,
/// so a completion never writes into a ring another session owns.
/// </para>
/// <para>
/// <b>Failures.</b> A failure while the loop serves a session ends that session's output with it, and the loop and the
/// other sessions go on, as on Unix. Every call into the pump runs inside a step, which ends output with what it threw. A
/// step that fails with a read posted cannot end output yet, because the read still points into this driver and its ring,
/// and the driver would go back to the pool before the read's completion arrived: it cancels the read, and the step that
/// handles the completion ends output with the failure.
/// </para>
/// <para>
/// Drivers are pooled across sessions, and each keeps its read's overlapped structure and the handle the loop finds it by
/// in one native block for the process's life, so a session pays nothing for its read or its pipe's binding to the port.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed unsafe class WindowsOutputDriver : IPtyOutputDriver
{
    private const int ErrorInvalidHandle = 6;
    private const int ErrorBrokenPipe = 109;
    private const int ErrorPipeNotConnected = 233;
    private const int ErrorOperationAborted = 995;
    private const int ErrorIoPending = 997;
    private const byte FileSkipCompletionPortOnSuccess = 0x1;
    private const byte FileSkipSetEventOnHandle = 0x2;

    /// <summary>
    /// Reads that complete at once one step makes before it lets the loop serve other sessions: 64 KiB of output at most,
    /// so a flood shares the loop and a session being disposed lets go of its pipe.
    /// </summary>
    private const int MaxReadsPerStep = 16;

    // What the driver is doing. Written on the loop's thread, and by Run before the driver's first step.
    private const int StateIdle = 0;
    /// <summary>A step runs, or the loop was asked to run one: the first, or the next after a step reached its bound.</summary>
    private const int StateRunning = 1;
    /// <summary>No read is posted: the ring is full, and a wake resumes the driver.</summary>
    private const int StateParked = 2;
    /// <summary>A zero-byte read is posted.</summary>
    private const int StateZeroPosted = 3;
    /// <summary>A read into the ring is posted: after disposal, or in the forced end of output.</summary>
    private const int StateRingPosted = 4;

    // What other threads ask of the driver (_requests), served on the loop's thread.
    private const int RequestWake = 1;
    private const int RequestCancel = 2;

    private static readonly Lock s_poolLock = new();
    // Free drivers, linked through _nextFree and guarded by s_poolLock.
    private static WindowsOutputDriver? s_free;
    // What a zero-byte read points at: never written, but a read must not be given a null buffer.
    private static readonly byte[] s_zeroByteTarget = GC.AllocateArray<byte>(1, pinned: true);

    /// <summary>A pump whose session's reads on the loop fail, for the test that one failure ends one session. Null: none.</summary>
    internal static PtyOutputPump? FailReadsOfPumpForTests;

    /// <summary>
    /// A pump whose session's step fails right after it posts its zero-byte read, for the test that a failure with a read
    /// posted ends output only once the read completed. Null: none.
    /// </summary>
    internal static PtyOutputPump? FailWithReadPostedOfPumpForTests;

    // The read's overlapped structure and the handle the loop finds this driver by. Native, so a posted read never points
    // into the managed heap; never freed, since the pool keeps every driver.
    private readonly ReadBlock* _block;
    private WindowsOutputDriver? _nextFree;

    // The session being served. Set before the first step and cleared when its pump finishes.
    private PtyOutputPump? _pump;
    private SafeFileHandle? _pipe;
    private int _state;
    // RequestWake and RequestCancel, set by any thread and taken by the loop when it looks at the driver.
    private int _requests;
    // The posted read was canceled already, so a second cancel cannot reach the read posted after it.
    private bool _cancelIssued;
    // A failure kept until no read is posted, when the next step ends output with it. On the loop's thread.
    private Exception? _failure;

    /// <summary>Whether this driver waits on the loop's queue of drivers to look at. Guarded by the loop's lock.</summary>
    internal bool ServiceQueued;

    /// <summary>The next driver on the loop's queue. Guarded by the loop's lock.</summary>
    internal WindowsOutputDriver? NextService;

    private WindowsOutputDriver()
    {
        _block = (ReadBlock*)NativeMemory.AllocZeroed((nuint)sizeof(ReadBlock));
        _block->Driver = GCHandle.ToIntPtr(GCHandle.Alloc(this));
    }

    /// <summary>A driver's read: the overlapped structure first, so a completion's overlapped pointer is the block's.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ReadBlock
    {
        public NativeOverlapped Overlapped;
        public IntPtr Driver;
    }

    /// <summary>The driver's state, for tests that report where a session stalled.</summary>
    internal int StateForTests => Volatile.Read(ref _state);

    /// <summary>Whether the driver waits with a zero-byte read posted, which holds no ring.</summary>
    internal bool IsWaitingWithoutBufferForTests => Volatile.Read(ref _state) == StateZeroPosted;

    /// <summary>
    /// Starts reading <paramref name="pipe"/> for <paramref name="pump"/>: what the Windows backend's
    /// <see cref="IPtyBackend.StartOutput"/> does. <paramref name="pipe"/> is the overlapped server end of a named pipe;
    /// the backend owns it and closes it at disposal, which ends reading.
    /// </summary>
    internal static void Run(PtyOutputPump pump, SafeFileHandle pipe)
    {
        var driver = Rent();
        try
        {
            Loop.Bind(pipe);
            // A read that completes at once is handled by the step that issued it; a queued completion would handle it twice.
            if (!WindowsInterop.SetFileCompletionNotificationModes(pipe, FileSkipCompletionPortOnSuccess | FileSkipSetEventOnHandle))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetFileCompletionNotificationModes failed");
        }
        catch
        {
            Return(driver);
            throw;
        }

        driver._pump = pump;
        driver._pipe = pipe;
        driver._cancelIssued = false;
        driver._failure = null;
        Volatile.Write(ref driver._requests, 0);
        Volatile.Write(ref driver._state, StateRunning);
        pump.AttachDriver(driver);

        // The first step runs on the loop, so no output is read or published on the thread that started the session.
        Loop.RequestService(driver);
    }

    /// <summary>
    /// Resumes a driver parked on a full ring; otherwise has the loop look at it after the step that runs. A posted read
    /// needs no wake: its completion runs the next step. Never blocks, and ignores a pump it no longer serves.
    /// </summary>
    public void Wake(PtyOutputPump pump) => Request(pump, RequestWake);

    /// <summary>
    /// Cancels the posted read, so the step that handles its completion re-checks the pump: the forced end of output,
    /// and disposal of a session whose pipe another process holds open. A cancel that finds no read posted has the driver
    /// look again; the pump repeats it while it still needs it.
    /// </summary>
    public void CancelRead(PtyOutputPump pump) => Request(pump, RequestCancel);

    private void Request(PtyOutputPump pump, int request)
    {
        // A driver that serves another session since ignores the call; one that slips past this check costs that session
        // a spurious look, or a canceled zero-byte read that its completion posts again.
        if (Volatile.Read(ref _pump) != pump)
            return;

        Interlocked.Or(ref _requests, request);
        Loop.RequestService(this);
    }

    /// <summary>
    /// The pump finished: go back to the pool. Called by <see cref="PtyOutputPump.Finish"/> from the driver's last step,
    /// on the loop, with no read posted.
    /// </summary>
    public void OnPumpFinished(PtyOutputPump pump)
    {
        if (_pump != pump)
            return;

        Volatile.Write(ref _pump, null);
        _pipe = null;
        Volatile.Write(ref _state, StateIdle);
        Return(this);
    }

    /// <summary>A posted read completed. On the loop.</summary>
    private void OnCompleted(uint errorCode, uint numBytes)
    {
        var posted = _state;
        if (posted is not (StateZeroPosted or StateRingPosted))
            return;

        Volatile.Write(ref _state, StateRunning);
        Step(posted == StateRingPosted ? Completion.Ring : Completion.ZeroByte, errorCode, numBytes);
    }

    /// <summary>The loop looks at this driver because something asked it to. On the loop.</summary>
    private void OnServiced()
    {
        var requests = Interlocked.Exchange(ref _requests, 0);
        switch (_state)
        {
            case StateRunning:
                Step(Completion.None, 0, 0);
                break;

            case StateParked:
                Volatile.Write(ref _state, StateRunning);
                Step(Completion.Resumed, 0, 0);
                break;

            case StateZeroPosted or StateRingPosted:
                if ((requests & RequestCancel) != 0)
                    CancelPostedRead();
                break;
        }
    }

    private enum Completion
    {
        /// <summary>No read completed: the first step, or the next after a step reached its bound.</summary>
        None,
        ZeroByte,
        Ring,
        /// <summary>A wake of a driver parked on a full ring.</summary>
        Resumed,
    }

    /// <summary>
    /// Reads until a read stays posted, the ring is full, the step's bound, or end of output. Runs on the loop with
    /// <see cref="StateRunning"/>, and never throws: a failure ends the pump with it, once no read is posted.
    /// </summary>
    private void Step(Completion completed, uint errorCode, uint numBytes)
    {
        var pump = _pump!;
        // A failure kept for this step ends output now; what the completed read brought is dropped with it.
        var error = _failure;
        if (error is null)
        {
            try
            {
                if (completed == Completion.Resumed)
                {
                    // A driver parked on a full ring ends the free-space handshake here; otherwise the drain would never see
                    // the transport go quiet.
                    if (pump.IsWaitingForFreeSpace)
                        pump.EndWaitForFreeSpace();

                    completed = Completion.None;
                }

                if (Continue(pump, completed, errorCode, numBytes))
                    return;
            }
            catch (Exception ex)
            {
                if (_state is StateZeroPosted or StateRingPosted)
                {
                    FailOnLoop(ex);
                    return;
                }

                error = ex;
            }
        }

        // End of output. No read is posted, so the ring and the driver can go.
        _failure = null;
        if (error is not null)
        {
            try
            {
                // A step that failed between parking and resuming leaves the handshake open; the drain waits on it.
                if (pump.IsWaitingForFreeSpace)
                    pump.EndWaitForFreeSpace();
            }
            catch (Exception)
            {
                // Finish reports the failure that brought the step here.
            }
        }

        pump.Finish(error);
    }

    /// <summary>
    /// Ends this session's output with <paramref name="error"/>, a failure while the loop served it; the loop and the other
    /// sessions go on. A read still posted points into this driver and its ring, so it is canceled, and the step that
    /// handles its completion ends output; otherwise the loop's next look at the driver does. On the loop.
    /// </summary>
    private void FailOnLoop(Exception error)
    {
        if (Volatile.Read(ref _pump) is null)
            return;

        _failure ??= error;
        if (_state is StateZeroPosted or StateRingPosted)
        {
            // A read canceled already, or ended by the pipe's close, completes all the same.
            CancelPostedRead();
            return;
        }

        Volatile.Write(ref _state, StateRunning);
        Loop.RequestService(this);
    }

    /// <summary>Returns <see langword="true"/> while reading goes on (a read is posted, the driver parked, or it waits for its next step).</summary>
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
            // A step that keeps finding output never posts a read, so it would keep the loop, and its pin on the pipe, for
            // as long as the child writes: other sessions would wait, and disposal could not close the pipe that ends the
            // flood. It has the loop come back to it after the others.
            if (readsNow == MaxReadsPerStep)
            {
                Loop.RequestService(this);
                return true;
            }

            var disposed = pump.IsSessionDisposed;
            if (!disposed)
            {
                pump.DeliverBacklogToSink();
                if (pump.IsFull)
                {
                    // A wake that arrives from now on is served after this step, and resumes the driver.
                    if (pump.BeginWaitForFreeSpace())
                    {
                        Volatile.Write(ref _state, StateParked);
                        return true;
                    }

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
                    EnterPosted(StateRingPosted);
                    return true;
                }

                if (read <= 0)
                    return false;

                Publish(pump, read);
                readsNow++;
                continue;
            }

            // Output is waiting, so this read completes at once. After disposal reads only let the transport end, into the
            // ring, which nothing reads any more, and may stay posted there.
            if (disposed || dataReady)
            {
                dataReady = false;
                if (FailReadsOfPumpForTests is { } failing && failing == pump)
                    throw new IOException("Injected read failure");

                pump.HoldRing();
                var read = ReadNow(handle, pump.NextReadSpan, out var posted);
                if (posted)
                {
                    // Before disposal not expected: the wait reported output waiting, and only this driver reads the
                    // pipe. Such a read holds the ring until output arrives.
                    EnterPosted(StateRingPosted);
                    return true;
                }

                if (read < 0)
                    return false;

                if (read > 0)
                    Publish(pump, read);

                readsNow++;
                continue;
            }

            // Wait for output without a buffer, even while output is unread (see the remarks).
            var zero = ReadNow(handle, Span<byte>.Empty, out var zeroPosted);
            if (zeroPosted)
            {
                // The state says a read is posted before anything else can fail, so a failure waits for its completion.
                EnterPosted(StateZeroPosted);
                if (FailWithReadPostedOfPumpForTests is { } failing && failing == pump)
                    throw new IOException("Injected failure with a read posted");

                // The wait stays posted, so let go of the ring: it goes back once the consumer has released everything.
                // Not before the read, so output already waiting keeps the ring.
                pump.LetGoOfRing();
                return true;
            }

            if (zero < 0)
                return false;

            // Output is waiting in the pipe. Only the read that takes it counts toward the step's bound.
            dataReady = true;
        }
    }

    /// <summary>Leaves the step with a read posted; its completion runs the next step, on the loop, after this one.</summary>
    private void EnterPosted(int posted)
    {
        _cancelIssued = false;
        Volatile.Write(ref _state, posted);
    }

    /// <summary>
    /// Cancels the posted read once. Its completion runs the next step, which re-checks the pump. On the loop, which has
    /// checked that a read is posted, so the cancel cannot reach a later read.
    /// </summary>
    private void CancelPostedRead()
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
            _ = WindowsInterop.CancelIoEx(pipe.DangerousGetHandle(), (IntPtr)(&_block->Overlapped));
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
        var overlapped = &_block->Overlapped;
        *overlapped = default;
        // The ring is pinned for its whole life, and the zero-byte target is pinned too.
        var target = buffer.IsEmpty
            ? (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(s_zeroByteTarget))
            : (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(buffer));
        if (WindowsInterop.ReadFile(handle, target, (uint)buffer.Length, IntPtr.Zero, overlapped))
        {
            // Completed at once. Completion-port notification is skipped on success, so no completion follows.
            return (int)overlapped->InternalHigh;
        }

        var error = Marshal.GetLastPInvokeError();
        if (error == ErrorIoPending)
        {
            posted = true;
            return 0;
        }

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

    /// <summary>
    /// The completion port that every session's output pipe is bound to, and the one thread that waits on it and runs every
    /// driver's steps. It starts with the first session and lives as long as the process.
    /// </summary>
    /// <remarks>
    /// Each pass takes one completion, or none when drivers asked for a look, then looks at the drivers that asked
    /// before the pass began. A driver that asks during the pass, as a step that reached its bound does, is looked at in
    /// the next one, after the next completion, so one flood cannot keep the loop from the other sessions. A request posts
    /// a packet without an overlapped structure to wake the loop, only when the queue was empty, since the loop does not
    /// wait for a completion while the queue has drivers.
    /// </remarks>
    internal static class Loop
    {
        private const uint Infinite = 0xFFFFFFFF;
        private const nuint WakeKey = 1;
        private const int WaitTimeout = 258;

        /// <summary>
        /// How long the loop waits before it waits on the port again after a wait failed without a completion, as the
        /// Unix loop does after a failed <c>epoll_wait</c> or <c>kevent</c>.
        /// </summary>
        private const int FailedWaitBackoffMs = 10;

        private static readonly Lock s_lock = new();
        private static IntPtr s_port;
        // Drivers that asked for a look, linked through NextService. Guarded by s_lock.
        private static WindowsOutputDriver? s_head;
        private static WindowsOutputDriver? s_tail;
        // Written by the loop's thread only.
        private static long s_passes;

        /// <summary>Passes the loop has made, for tests that check it does not spin.</summary>
        internal static long Passes => Volatile.Read(ref s_passes);

        /// <summary>While set, every wait on the port fails as a closed port's would, for the test that the loop backs off.</summary>
        internal static volatile bool FailWaitsForTests;

        /// <summary>Binds <paramref name="pipe"/> to the loop's port, starting the loop on the first call.</summary>
        internal static void Bind(SafeFileHandle pipe)
        {
            var port = EnsureStarted();
            var pinned = false;
            try
            {
                pipe.DangerousAddRef(ref pinned);
                if (WindowsInterop.CreateIoCompletionPort(pipe.DangerousGetHandle(), port, 0, 0) == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateIoCompletionPort failed");
            }
            finally
            {
                if (pinned)
                    pipe.DangerousRelease();
            }
        }

        /// <summary>Has the loop look at <paramref name="driver"/>. Cheap, never blocks, from any thread.</summary>
        internal static void RequestService(WindowsOutputDriver driver)
        {
            bool wasEmpty;
            lock (s_lock)
            {
                if (driver.ServiceQueued)
                    return;

                driver.ServiceQueued = true;
                wasEmpty = s_head is null;
                if (s_tail is null)
                    s_head = driver;
                else
                    s_tail.NextService = driver;

                s_tail = driver;
            }

            if (wasEmpty)
                _ = WindowsInterop.PostQueuedCompletionStatus(s_port, 0, WakeKey, null);
        }

        private static IntPtr EnsureStarted()
        {
            lock (s_lock)
            {
                if (s_port != IntPtr.Zero)
                    return s_port;

                var port = WindowsInterop.CreateIoCompletionPort(new IntPtr(-1), IntPtr.Zero, 0, 1);
                if (port == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateIoCompletionPort failed");

                try
                {
                    new Thread(static state => Run((IntPtr)state!))
                    {
                        IsBackground = true,
                        Name = "MiniPty output loop",
                    }.Start(port);
                }
                catch
                {
                    WindowsInterop.CloseHandle(port);
                    throw;
                }

                s_port = port;
                return port;
            }
        }

        private static void Run(IntPtr port)
        {
            while (true)
            {
                bool queued;
                lock (s_lock)
                    queued = s_head is not null;

                uint bytes = 0;
                nuint key = 0;
                NativeOverlapped* overlapped = null;
                bool completed;
                int error;
                if (FailWaitsForTests)
                {
                    completed = false;
                    error = ErrorInvalidHandle;
                }
                else
                {
                    completed = WindowsInterop.GetQueuedCompletionStatus(port, &bytes, &key, &overlapped, queued ? 0 : Infinite);
                    error = completed ? 0 : Marshal.GetLastPInvokeError();
                }

                Volatile.Write(ref s_passes, s_passes + 1);
                if (overlapped is not null)
                {
                    // A completion: the read's own result, failed or not.
                    var driver = (WindowsOutputDriver)GCHandle.FromIntPtr(((ReadBlock*)overlapped)->Driver).Target!;
                    Serve(driver, (uint)error, bytes, isCompletion: true);
                }
                else if (!completed && error != WaitTimeout)
                {
                    // The wait itself failed, which only a port closed or broken under the loop makes it do. Waiting again
                    // at once would spin the loop for as long as that lasts; drivers that asked for a look are still
                    // served below, at this pace.
                    Thread.Sleep(FailedWaitBackoffMs);
                }

                // Otherwise a wake, or no completion within the timeout.
                ServeRequests();
            }
        }

        /// <summary>Looks at every driver that asked before this pass; one that asks meanwhile waits for the next.</summary>
        private static void ServeRequests()
        {
            WindowsOutputDriver? driver;
            lock (s_lock)
            {
                driver = s_head;
                s_head = null;
                s_tail = null;
            }

            while (driver is not null)
            {
                WindowsOutputDriver? next;
                lock (s_lock)
                {
                    next = driver.NextService;
                    driver.NextService = null;
                    driver.ServiceQueued = false;
                }

                Serve(driver, 0, 0, isCompletion: false);
                driver = next;
            }
        }

        private static void Serve(WindowsOutputDriver driver, uint error, uint bytes, bool isCompletion)
        {
            // A step never throws, and every call into the pump runs inside one. What is left outside is the driver's own
            // bookkeeping; should that fail, the failure ends that session, as a step's would, and never the process from
            // this thread.
            try
            {
                if (isCompletion)
                    driver.OnCompleted(error, bytes);
                else
                    driver.OnServiced();
            }
            catch (Exception ex)
            {
                try
                {
                    driver.FailOnLoop(ex);
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
