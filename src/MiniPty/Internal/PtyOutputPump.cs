using System.Diagnostics;
using System.Threading.Tasks.Sources;

namespace MiniPty.Internal;

/// <summary>Receives a wake-up from <see cref="PtyOutputPump"/> for the wait it armed.</summary>
internal interface IPtyOutputWaiter
{
    /// <summary>Runs on whichever thread signaled; implementations only complete their pending wait.</summary>
    void OnOutputSignaled();
}

/// <summary>
/// The only reader of a session's output transport, from spawn until end of output: what its output driver reads goes
/// into a byte ring that the session's one active consumer drains.
/// </summary>
/// <remarks>
/// <para>
/// <b>Driver.</b> The pump decides what happens to output; an <see cref="IPtyOutputDriver"/> decides how reads are
/// waited for. The driver calls the producer members (<see cref="NextReadSpan"/>, <see cref="Publish"/>,
/// <see cref="Finish"/>, and the free-space handshake) as output arrives, and the pump calls the driver back when its
/// state changes in a way the driver cannot see from the transport: the consumer freed space, a sink attached, the
/// close was requested, disposal began, or the drain gave up on the transport ending. Windows reads through I/O
/// completions (<see cref="WindowsOutputDriver"/>), Unix on the exit watcher's loop, and every platform under the
/// rollback switch on a <see cref="PtyReaderThread"/> blocking in the transport.
/// </para>
/// <para>
/// <b>Ring.</b> <see cref="Capacity"/> bytes in one pinned buffer from <see cref="PtyBufferPool"/>. Each transport
/// read, plus the bytes that arrive within a short micro-window after it, is published at once, up to
/// <see cref="PublishSize"/>, so bulk output is published in few pieces without delaying interactive output. A
/// consumer takes all contiguous published bytes as one chunk (up to the end of the buffer and the backend's
/// <see cref="IPtyBackend.OutputChunkLimit"/>), so a consumer that fell behind, such as a WebSocket still sending
/// the previous chunk, takes several reads at once. When less than <see cref="PublishSize"/> is free the pump stops
/// reading, and backpressure reaches the child through the OS pipe. The ring is single-producer, single-consumer:
/// the pump owns <see cref="_tail"/>, the active consumer owns <see cref="_head"/>, and <see cref="_count"/>
/// publishes bytes between them with full fences.
/// </para>
/// <para>
/// <b>Consumers.</b> <c>ReadOutputAsync</c> and the <c>Output</c> stream read the ring on their own thread and
/// wait through <see cref="ArmWait"/> without blocking it. One-shot completion attaches a
/// <see cref="PtyOutputSink"/> instead, and the pump writes into it directly on the driver's thread; output
/// published before the sink attached is delivered first.
/// </para>
/// <para>
/// <b>End of output.</b> Unix PTYs report end of output once every slave descriptor closes. ConPTY reports it
/// when its host exits, which a released pseudo-console's host does with its last client (the out-of-band ConPTY,
/// and the in-box one from Windows 11 24H2) and an older in-box host does only once the pseudo-console closes; a
/// grandchild can keep either host, or a Linux slave, open. So after child exit
/// <see cref="PtyOutputDrainScheduler"/> closes the transport once reads have been quiet for
/// <see cref="DefaultPostExitQuietMs"/> (one-shot completion also bounds this by its grace and close timeouts). When
/// even the close does not end the transport, because another process holds ConPTY's output pipe open, the
/// pump ends output itself once reads have been quiet for the same interval after the close
/// (<see cref="OnDrainTick"/>).
/// </para>
/// <para>
/// <b>Lifetime.</b> The ring is taken from the pool when the driver first reads into it (<see cref="HoldRing"/>), and
/// given back once the driver has let go of it (<see cref="LetGoOfRing"/>) and no unread output remains, or the session
/// was disposed, whichever comes last, so the driver never writes into an array another renter owns and a consumer's
/// chunk stays valid until it releases it. A driver that blocks in the transport holds the ring until end of output; one
/// that can wait without a buffer lets go of it whenever the consumer has caught up, so an idle session holds none.
/// Disposal makes the pump discard output until the transport ends, which ConPTY needs to finish closing.
/// </para>
/// </remarks>
internal sealed class PtyOutputPump : IValueTaskSource
{
    /// <summary>
    /// Most bytes the pump publishes at once (one transport read and the continuation reads appended to it), and the
    /// free space it waits for before reading again, so a consumer that frees a few bytes at a time does not cause a
    /// read per release.
    /// </summary>
    internal const int PublishSize = PtyReadBuffer.Size;

    /// <summary>Bytes of output the ring holds: four publishes' worth.</summary>
    internal const int Capacity = 4 * PublishSize;

    /// <summary>
    /// Post-exit silence after which the transport is closed so the reader sees end of output, and after which a
    /// close that did not end it is given up on. Backends report it through <see cref="IPtyBackend.PostExitQuietMs"/>.
    /// </summary>
    internal const int DefaultPostExitQuietMs = 100;

    private const int FlagProducerFinished = 1;
    private const int FlagSessionDisposed = 2;
    private const int FlagCloseRequested = 4;
    private const int FlagExitObserved = 8;
    /// <summary>The drain has given up on the OS ending the transport; the reader drains without blocking and stops.</summary>
    private const int FlagEndOfOutputForced = 16;

    // Who has the ring (_ringState), guarded by lock (this): nobody, the driver, or unread output it left behind.
    private const int RingNone = 0;
    private const int RingHeld = 1;
    private const int RingKept = 2;

    private readonly IPtyBackend _backend;
    // A second concurrent stop waiter falls back to a task; the first uses the allocation-free slot below.
    private TaskCompletionSource? _stoppedFallback;
    // The last ring taken. Not cleared when it goes back to the pool: the consumer reads it only while output is unread,
    // which keeps it here, and a consumer racing disposal must find an array, not null.
    private byte[]? _ring;
    private int _ringState;
    // Set by a driver that wants a wake once the consumer has released all unread output (see WakeDriverWhenEmpty).
    private int _wakeWhenEmpty;
    // Consumer side: where unread output starts, and how much of it the last TryPeek handed out.
    private int _head;
    private int _peeked;
    // Producer side: where the next read writes. Always (_head + _count) % Capacity once a read is published.
    private int _tail;
    // Published bytes the consumer has not released.
    private int _count;
    private int _flags;
    private int _producerWaiting;
    // The armed consumer wait's generation, or 0 when no wait is armed. A signaler claims the generation it read.
    private int _consumerWaiting;
    // Consumer side: the generation of the last wait armed.
    private int _waitGeneration;
    // The least unread output a data signal completes the armed wait for: 1 for a plain read, more for a reader that
    // coalesces a burst (PtyOutputReader), NoDataWait for a wait that output does not complete. A short, as the bool it
    // replaced, so that every session's pump stays the size it was: the ring never holds more than Capacity.
    private short _waiterMinimumData;
    private const short NoDataWait = short.MaxValue;
    private IPtyOutputWaiter? _waiter;
    private PtyOutputSink? _sink;
    private IPtyOutputDriver? _driver;
    private Exception? _error;
    private long _lastReadTick;
    private long _exitTick;
    private long _closeRequestedTick;
    private ManualResetValueTaskSourceCore<bool> _stopped;
    private int _stopWaitState;

    internal PtyOutputPump(IPtyBackend backend)
    {
        _backend = backend;
    }

    /// <summary>Guarded by the <see cref="PtyOutputDrainScheduler"/> lock.</summary>
    internal bool DrainScheduled;

    /// <summary>Guarded by the <see cref="PtyOutputDrainScheduler"/> lock.</summary>
    internal bool DrainRescheduled;

    internal bool IsProducerFinished => (Volatile.Read(ref _flags) & FlagProducerFinished) != 0;

    internal bool IsSessionDisposed => (Volatile.Read(ref _flags) & FlagSessionDisposed) != 0;

    /// <summary>Whether the transport close has started: set before <see cref="IPtyBackend.CloseOutputTransport"/> runs.</summary>
    internal bool IsCloseRequested => (Volatile.Read(ref _flags) & FlagCloseRequested) != 0;

    /// <summary>Whether the reader is parked on a full ring until the consumer frees <see cref="PublishSize"/> bytes.</summary>
    internal bool IsWaitingForFreeSpace => Volatile.Read(ref _producerWaiting) != 0;

    /// <summary>The transport failure that ended the pump, if any. Read only after <see cref="IsProducerFinished"/>.</summary>
    internal Exception? Error => _error;

    internal long ExitTick => Volatile.Read(ref _exitTick);

    /// <summary>Starts reading with the backend's output driver. Called once, by the session constructor.</summary>
    internal void Start() => _backend.StartOutput(this);

    /// <summary>Records the driver the pump calls back. The driver calls it before its first producer call.</summary>
    internal void AttachDriver(IPtyOutputDriver driver) => Volatile.Write(ref _driver, driver);

    // ---------------------------------------------------------------- Producer (the output driver)
    //
    // The driver calls these as it waits for the transport, one call at a time for this pump. Its loop is:
    // DeliverBacklogToSink; when IsFull, the free-space handshake; then HoldRing, read into NextReadSpan, OnOutputRead for
    // each read that returned bytes, and Publish; and Finish once at the end, also after a failure. After disposal it
    // skips the first two steps and keeps reading only to let the transport end. A driver that can wait for output
    // without a buffer calls LetGoOfRing before such a wait, and WakeDriverWhenEmpty to learn when a read it left posted
    // into the ring is all that keeps the ring.

    /// <summary>
    /// Whether less than <see cref="PublishSize"/> is free, so the driver waits for the consumer instead of reading. It
    /// is checked on its own, not as the alternative to an attached sink: a failed completion can detach its sink after
    /// <see cref="DeliverBacklogToSink"/>, leaving the backlog in place, and reading into a full ring would overwrite
    /// unread output. It also holds after the close: the transport may still hold output then (ConPTY flushes its
    /// final frame after <c>ClosePseudoConsole</c>), and ending output there would drop it.
    /// </summary>
    internal bool IsFull => Capacity - Volatile.Read(ref _count) < PublishSize;

    /// <summary>
    /// Whether the drain has given up on the OS ending the transport (see <see cref="OnDrainTick"/>): the driver takes
    /// what the transport holds without waiting, including output that landed between the drain's quiet check and the
    /// read's cancellation, then finishes.
    /// </summary>
    internal bool IsEndOfOutputForced => (Volatile.Read(ref _flags) & FlagEndOfOutputForced) != 0;

    /// <summary>
    /// The span the next read fills: at least <see cref="PublishSize"/> is free unless the ring is full, so it holds no
    /// unread output. It ends at <see cref="PublishSize"/> or at the end of the buffer, and the next publish continues
    /// at the start. After disposal nothing reads the ring, so the driver reuses the same span only to let the
    /// transport end. Only while the driver holds the ring (<see cref="HoldRing"/>); the buffer is pinned, so a read
    /// may stay posted into the span until it completes.
    /// </summary>
    internal Span<byte> NextReadSpan
    {
        get
        {
            Debug.Assert(_ringState == RingHeld);
            var start = _tail;
            return _ring!.AsSpan(start, Math.Min(PublishSize, Capacity - start));
        }
    }

    /// <summary>Whether published output is still unread (or handed out and not released).</summary>
    internal bool HasUnreadOutput => Volatile.Read(ref _count) != 0;

    /// <summary>Published bytes the consumer has not released.</summary>
    internal int UnreadCount => Volatile.Read(ref _count);

    /// <summary>
    /// Makes sure the driver holds a ring before it reads: the one unread output kept, or one from the pool. Cheap once
    /// held.
    /// </summary>
    internal void HoldRing()
    {
        // Only the driver moves the ring into or out of RingHeld, so it can read its own state without the lock.
        if (Volatile.Read(ref _ringState) == RingHeld)
            return;

        lock (this)
        {
            if (_ringState == RingNone)
                _ring = PtyBufferPool.Rent();

            Volatile.Write(ref _ringState, RingHeld);
        }
    }

    /// <summary>
    /// The driver no longer reads into the ring: it waits for output without a buffer, or it has finished. The ring goes
    /// back to the pool at once when nothing in it is unread or the session was disposed; otherwise it stays for the
    /// consumer, and goes back once the consumer has released the last of it (or at disposal), unless the driver holds
    /// it again first. Never call it while a read posted into the ring is outstanding.
    /// </summary>
    internal void LetGoOfRing()
    {
        if (Volatile.Read(ref _ringState) != RingHeld)
            return;

        lock (this)
        {
            Volatile.Write(ref _ringState, RingKept);
            ReturnKeptRingLocked();
        }
    }

    /// <summary>
    /// Asks for one <see cref="IPtyOutputDriver.Wake"/> once the consumer has released all unread output, for a driver
    /// whose read posted into the ring is then the only thing keeping it. Returns <see langword="false"/> when nothing is
    /// unread already, so the driver acts at once; the request then stands no longer.
    /// </summary>
    internal bool WakeDriverWhenEmpty()
    {
        // A full fence before the check, paired with the consumer's release-then-check in OnRingEmptied: one sees the other.
        Interlocked.Exchange(ref _wakeWhenEmpty, 1);
        if (Volatile.Read(ref _count) != 0)
            return true;

        Volatile.Write(ref _wakeWhenEmpty, 0);
        return false;
    }

    /// <summary>Withdraws <see cref="WakeDriverWhenEmpty"/>: the read it was about has completed.</summary>
    internal void ClearWakeWhenEmpty() => Volatile.Write(ref _wakeWhenEmpty, 0);

    /// <summary>
    /// Stamps a read that returned bytes, for the post-exit quiet interval: per read, so the empty tail of a
    /// coalescing window does not postpone the quiet close.
    /// </summary>
    internal void OnOutputRead() => Volatile.Write(ref _lastReadTick, Environment.TickCount64);

    /// <summary>
    /// Publishes the first <paramref name="length"/> bytes of <see cref="NextReadSpan"/>, read at
    /// <paramref name="timestamp"/>: to an attached sink, or to the ring for the consumer. Discarded after disposal.
    /// </summary>
    internal void Publish(int length, long timestamp)
    {
        if (!IsSessionDisposed)
            Deliver(_tail, length, timestamp);
    }

    /// <summary>Hands an attached sink the output published before it attached. Does nothing without a sink.</summary>
    internal void DeliverBacklogToSink()
    {
        if (Volatile.Read(ref _sink) is not null && Volatile.Read(ref _count) > 0)
            DeliverBacklog();
    }

    /// <summary>
    /// First half of the free-space handshake, when <see cref="IsFull"/>: marks the producer as waiting, then re-checks.
    /// Returns <see langword="true"/> when the driver must wait for a <see cref="IPtyOutputDriver.Wake"/>: the ring is
    /// still full, no sink drains it, and the session is not disposed. A consumer that freed space before it saw the
    /// mark does not wake the driver, which the re-check after the mark covers. Always followed by
    /// <see cref="EndWaitForFreeSpace"/>.
    /// </summary>
    internal bool BeginWaitForFreeSpace()
    {
        Interlocked.Exchange(ref _producerWaiting, 1);
        return IsFull && !IsSessionDisposed && Volatile.Read(ref _sink) is null;
    }

    /// <summary>
    /// Second half of the free-space handshake. Time parked on a full ring is not transport silence, so this restarts
    /// the quiet interval before the drain can see that the producer no longer waits; otherwise a tick before the next
    /// read would find the last read long past and close a Unix master that still holds unread output. A consumer
    /// slow to free space therefore only delays the end of output.
    /// </summary>
    internal void EndWaitForFreeSpace()
    {
        Volatile.Write(ref _lastReadTick, Environment.TickCount64);
        Volatile.Write(ref _producerWaiting, 0);
    }

    private void Deliver(int start, int length, long timestamp)
    {
        lock (this)
        {
            if (_sink is { } sink)
            {
                // Output published before the sink attached goes first, so the sink sees output in order. The span just
                // read is not published, so the next read reuses it.
                DrainRingInto(sink);
                sink.Write(_ring!.AsSpan(start, length), timestamp);
                return;
            }
        }

        _tail = Advance(start, length);
        Interlocked.Add(ref _count, length);
        SignalConsumer(dataArrived: true);
    }

    /// <summary>The ring position <paramref name="length"/> bytes after <paramref name="position"/>, never past the end.</summary>
    private static int Advance(int position, int length)
    {
        var next = position + length;
        return next == Capacity ? 0 : next;
    }

    private void DeliverBacklog()
    {
        lock (this)
        {
            if (_sink is { } sink)
                DrainRingInto(sink);
        }
    }

    /// <summary>
    /// Hands all published output to <paramref name="sink"/>, in at most two writes: up to the end of the buffer, then
    /// from its start. Caller holds the pump lock.
    /// </summary>
    private void DrainRingInto(PtyOutputSink sink)
    {
        int count;
        var drained = false;
        while ((count = Volatile.Read(ref _count)) > 0)
        {
            var head = _head;
            var length = Math.Min(count, Capacity - head);
            sink.Write(_ring!.AsSpan(head, length), timestamp: 0);
            _head = Advance(head, length);
            Interlocked.Add(ref _count, -length);
            drained = true;
        }

        if (drained)
            ReturnKeptRingLocked();
    }

    /// <summary>
    /// Ends the pump: the transport reported end of output, the driver stopped after the forced end, or
    /// <paramref name="error"/> failed reading. Called once by the driver, never throws. Disposal tears the transport
    /// down under the driver, so a failure after disposal is end of output, not an error.
    /// </summary>
    internal void Finish(Exception? error)
    {
        if (error is not null && !IsSessionDisposed)
            _error = error;

        lock (this)
        {
            if (!IsSessionDisposed && _sink is { } sink)
            {
                try
                {
                    DrainRingInto(sink);
                    sink.OnOutputEnded();
                }
                catch (Exception ex)
                {
                    _error ??= ex;
                }
            }

            // The pump no longer touches the ring or the driver. Let go of both before completion becomes
            // observable: a ring with nothing unread is back in the pool before the consumer sees end of output (and
            // otherwise goes back when the consumer releases the rest, or at disposal), and a session started right
            // after finds the driver already free.
            if (_ringState == RingHeld)
            {
                Volatile.Write(ref _ringState, RingKept);
                ReturnKeptRingLocked();
            }

            _driver!.OnPumpFinished(this);
            SetFlag(FlagProducerFinished);
        }

        SignalConsumer(dataArrived: false);
        if (Interlocked.CompareExchange(ref _stopWaitState, 2, 1) == 1)
        {
            // Same hazard as in SignalConsumer: a throwing context must not take the driver's thread down.
            try
            {
                _stopped.SetResult(true);
            }
            catch (Exception)
            {
            }
        }

        // Its continuations run asynchronously, so completing it here never runs a waiter's code on this thread.
        Volatile.Read(ref _stoppedFallback)?.TrySetResult();
    }

    // ---------------------------------------------------------------- Ring consumer (ReadOutputAsync, Output)

    /// <summary>
    /// Gets all contiguous unread output, up to the end of the buffer and the backend's chunk limit, without releasing
    /// it. <see cref="Release"/> releases exactly what the last call handed out, even if more was published since.
    /// </summary>
    internal bool TryPeek(out ReadOnlyMemory<byte> data)
    {
        var count = Volatile.Read(ref _count);
        if (count == 0)
        {
            data = default;
            return false;
        }

        var head = _head;
        // Read per peek rather than cached: the backend returns a constant, and the pump object stays smaller.
        var length = Math.Min(Math.Min(count, Capacity - head), _backend.OutputChunkLimit);
        _peeked = length;
        // Unread output keeps the ring, and the count read above was published after the ring was taken.
        data = _ring!.AsMemory(head, length);
        return true;
    }

    /// <summary>Releases the output the last <see cref="TryPeek"/> handed out, so the pump can read into it again.</summary>
    internal void Release()
    {
        var length = _peeked;
        _peeked = 0;
        _head = Advance(_head, length);
        if (Interlocked.Add(ref _count, -length) == 0 && length != 0)
            OnRingEmptied();

        WakeProducerIfSpace();
    }

    /// <summary>Copies published output into <paramref name="destination"/>, releasing what it copies.</summary>
    internal int CopyTo(Span<byte> destination)
    {
        var copied = 0;
        var emptied = false;
        int count;
        while (copied < destination.Length && (count = Volatile.Read(ref _count)) > 0)
        {
            var head = _head;
            var take = Math.Min(Math.Min(count, Capacity - head), destination.Length - copied);
            _ring!.AsSpan(head, take).CopyTo(destination[copied..]);
            copied += take;
            _head = Advance(head, take);
            emptied = Interlocked.Add(ref _count, -take) == 0;
        }

        if (copied > 0)
        {
            if (emptied)
                OnRingEmptied();

            WakeProducerIfSpace();
        }

        return copied;
    }

    /// <summary>
    /// The consumer released the last unread output: a ring the driver let go of goes back to the pool, and a driver that
    /// asked for it (<see cref="WakeDriverWhenEmpty"/>) is woken to let go of the one it holds for a posted read.
    /// </summary>
    private void OnRingEmptied()
    {
        if (Volatile.Read(ref _ringState) == RingKept)
        {
            lock (this)
                ReturnKeptRingLocked();
        }

        if (Volatile.Read(ref _wakeWhenEmpty) != 0 && Interlocked.Exchange(ref _wakeWhenEmpty, 0) != 0)
            Volatile.Read(ref _driver)?.Wake(this);
    }

    /// <summary>
    /// Returns a ring the driver let go of, once nothing in it is unread or the session was disposed (whose chunks are
    /// invalid). Caller holds the pump lock.
    /// </summary>
    private void ReturnKeptRingLocked()
    {
        if (_ringState != RingKept || (Volatile.Read(ref _count) != 0 && !IsSessionDisposed))
            return;

        Volatile.Write(ref _ringState, RingNone);
        PtyBufferPool.Return(_ring!);
    }

    /// <summary>
    /// Wakes a producer parked on a full ring once it has room to read again. The consumer frees space before it
    /// looks at the flag, and the producer sets the flag before it looks at the space, so one of them sees the other.
    /// </summary>
    private void WakeProducerIfSpace()
    {
        if (Volatile.Read(ref _producerWaiting) != 0 && !IsFull)
            Volatile.Read(ref _driver)?.Wake(this);
    }

    /// <summary>
    /// Arms <paramref name="waiter"/> for the next signal: published output (when <paramref name="wakeOnData"/>, once at
    /// least <paramref name="minimumData"/> bytes are unread), end of output, disposal, or a drain timeout.
    /// </summary>
    /// <param name="waiter">The waiter to complete.</param>
    /// <param name="wakeOnData">Whether published output completes the wait.</param>
    /// <param name="minimumData">
    /// The least unread output that completes the wait: 1 for a plain read. A reader coalescing a burst asks for more, and
    /// <see cref="RelaxWaitMinimum"/> lowers it again at the reader's deadline, so output is never held back for longer
    /// than its consumer asked. The pump itself never delays output.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the wait is armed and the waiter will be signaled; <see langword="false"/> when
    /// the condition already holds and the caller should re-check synchronously instead of waiting.
    /// </returns>
    internal bool ArmWait(IPtyOutputWaiter waiter, bool wakeOnData, int minimumData = 1)
    {
        Debug.Assert(minimumData is >= 1 and <= Capacity);
        Volatile.Write(ref _waiter, waiter);
        // Before the generation: a signaler that reads this wait's generation reads its minimum too.
        Volatile.Write(ref _waiterMinimumData, wakeOnData ? (short)minimumData : NoDataWait);

        // Each wait gets a generation of its own, never 0, so that a signaler still holding an earlier wait's generation
        // fails its claim instead of completing this one (see SignalConsumer).
        var generation = ++_waitGeneration;
        if (generation == 0)
            generation = _waitGeneration = 1;

        Interlocked.Exchange(ref _consumerWaiting, generation);

        // Re-checked after publishing the wait (Dekker with the producer's publish-then-check).
        var ready = (wakeOnData && Volatile.Read(ref _count) >= minimumData)
            || (Volatile.Read(ref _flags) & (FlagProducerFinished | FlagSessionDisposed)) != 0;
        if (!ready)
            return true;

        // Withdraw, unless a signaler already claimed the wait; then the signal is on its way.
        return Interlocked.CompareExchange(ref _consumerWaiting, 0, generation) != generation;
    }

    /// <summary>Claims the armed wait for cancellation. Exactly one of a signal and a cancellation wins.</summary>
    internal bool TryCancelWait()
    {
        var generation = Volatile.Read(ref _consumerWaiting);
        return generation != 0 && Interlocked.CompareExchange(ref _consumerWaiting, 0, generation) == generation;
    }

    /// <summary>Signals the armed wait for a condition the waiter checks itself (a drain timeout).</summary>
    internal void SignalWaiter() => SignalConsumer(dataArrived: false);

    /// <summary>
    /// Lowers the minimum of an armed wait that coalesces a burst to 1 byte, at its consumer's deadline, and completes it
    /// when output is unread; otherwise the next publish does. Any thread. A wait that output does not complete, or a
    /// plain one, is left as it is, so a deadline that outlived its wait changes nothing but an earlier flush.
    /// </summary>
    internal void RelaxWaitMinimum()
    {
        var minimum = Volatile.Read(ref _waiterMinimumData);
        if (minimum <= 1 || minimum == NoDataWait)
            return;

        if (Interlocked.CompareExchange(ref _waiterMinimumData, (short)1, minimum) == minimum)
            SignalConsumer(dataArrived: true);
    }

    private void SignalConsumer(bool dataArrived)
    {
        var generation = Volatile.Read(ref _consumerWaiting);
        if (generation == 0)
            return;

        // A publish signals after it counts its output, and a consumer that is not waiting can take that output in
        // between, finish with it, and arm a new wait. Claiming that wait then completed it with nothing to take, and the
        // consumer failed with "signaled without a state change": a 32 MiB flood through the WebSocket bridge lost the
        // rest of its output in about one of ten runs. So a data signal claims only a wait that output is still there
        // for, checked after reading the wait: until that wait is withdrawn, the consumer cannot take output without
        // being signaled. A withdrawn wait takes its generation with it, so a consumer that withdraws because it saw the
        // output itself, takes it, and arms another wait leaves this claim failing. Either way the output this signal was
        // about has been taken, and the new wait is signaled by the next change of state, which its own re-check in
        // ArmWait cannot miss. The same check holds a wait that coalesces a burst until its minimum is unread, and keeps
        // output from completing a wait that does not want it (NoDataWait, more than the ring holds).
        if (dataArrived && Volatile.Read(ref _count) < Volatile.Read(ref _waiterMinimumData))
            return;

        if (Interlocked.CompareExchange(ref _consumerWaiting, 0, generation) == generation)
        {
            // Completing the wait posts the consumer's continuation to the context it captured, on this thread: the
            // driver's thread, or a thread disposing the session. A context whose Post throws (a UI dispatcher that
            // already shut down) can never run that continuation; the exception must not end output reading,
            // escape Dispose, or terminate the process from the driver's thread.
            try
            {
                Volatile.Read(ref _waiter)!.OnOutputSignaled();
            }
            catch (Exception)
            {
            }
        }
    }

    // ---------------------------------------------------------------- Sink consumer (one-shot completion)

    /// <summary>Routes output to <paramref name="sink"/>, starting with what the ring already holds.</summary>
    internal void AttachSink(PtyOutputSink sink)
    {
        lock (this)
        {
            Volatile.Write(ref _sink, sink);
            if (IsProducerFinished)
            {
                // The pump is done, so this thread has the ring to itself.
                DrainRingInto(sink);
                sink.OnOutputEnded();
                return;
            }
        }

        // A pump parked on a full ring drains it into the sink.
        Volatile.Read(ref _driver)?.Wake(this);
    }

    /// <summary>
    /// Stops routing output to <paramref name="sink"/> after a failed completion. Returns once no write into it is
    /// in progress; output read meanwhile goes back to the ring.
    /// </summary>
    internal void DetachSink(PtyOutputSink sink)
    {
        lock (this)
        {
            if (_sink == sink)
                Volatile.Write(ref _sink, null);
        }
    }

    // ---------------------------------------------------------------- Exit, drain, close, disposal

    /// <summary>Records child exit and starts post-exit drain ticks. Runs on the thread that observed exit.</summary>
    internal void OnChildExited()
    {
        Volatile.Write(ref _exitTick, Environment.TickCount64);
        if (TrySetFlag(FlagExitObserved))
            PtyOutputDrainScheduler.Schedule(this);
    }

    /// <summary>
    /// One post-exit drain step, run by <see cref="PtyOutputDrainScheduler"/>: closes the transport once reads
    /// have gone quiet (or a completion grace expires); then, if the close has not ended the transport either,
    /// ends output once reads have gone quiet again; and lets a completion sink watch its close timeout throughout.
    /// </summary>
    /// <returns><see langword="true"/> once this pump needs no further ticks.</returns>
    internal bool OnDrainTick(long now)
    {
        var flags = Volatile.Read(ref _flags);
        if ((flags & (FlagProducerFinished | FlagSessionDisposed)) != 0)
            return true;

        // A completion can schedule ticks as soon as its exit wait resumes, which can precede the exit listener:
        // exit waits complete before the listener runs. Without the exit tick, the quiet and grace deadlines would
        // read as long past and close the transport at once.
        if ((flags & FlagExitObserved) == 0)
            return false;

        var sink = Volatile.Read(ref _sink);
        if ((flags & FlagCloseRequested) == 0)
        {
            // One-shot completion never closes before the first read: ConPTY can emit its first frame after a fast
            // child has already exited, and closing then would lose it. The grace bounds that wait.
            var quiet = IsQuietSince(now, ExitTick) && (sink is null || Volatile.Read(ref _lastReadTick) != 0);
            if (!quiet && (sink is null || !sink.IsGraceExpired(now, ExitTick)))
                return false;

            RequestClose();
            return false;
        }

        // The close normally ends the transport within milliseconds: ConPTY writes its final frame within about
        // 1 ms and its host exits, which breaks the pipe. It does not when another process holds the pipe's write
        // end: a process the host started with handle inheritance while CreatePseudoConsole was duplicating its
        // ends into the console host inherited them, and the pipe breaks only when that process exits. So once
        // reads have been quiet for the same interval after the close, the pump ends output itself: the reader is
        // released from its blocking read and drains what is left without blocking. Repeated every tick until the
        // reader has finished, because a cancel that lands between two reads cancels nothing.
        var closeRequestedTick = Volatile.Read(ref _closeRequestedTick);
        if (IsQuietSince(now, closeRequestedTick))
        {
            SetFlag(FlagEndOfOutputForced);
            Volatile.Read(ref _driver)?.CancelRead(this);
        }

        // Only a completion sink has a deadline of its own to watch; every pump ticks until its reader finishes.
        return sink is not null && sink.CheckCloseTimeout(now, closeRequestedTick, this);
    }

    /// <summary>
    /// Whether the reader has read nothing for the backend's <see cref="IPtyBackend.PostExitQuietMs"/>, counted from
    /// the later of its last read and <paramref name="since"/>.
    /// </summary>
    private bool IsQuietSince(long now, long since)
    {
        // A pump parked on a full ring is not reading, so silence says nothing about the transport.
        if (Volatile.Read(ref _producerWaiting) != 0)
            return false;

        return now - Math.Max(Volatile.Read(ref _lastReadTick), since) >= _backend.PostExitQuietMs;
    }

    /// <summary>
    /// Closes the transport so the reader reaches end of output: the pseudo-console on Windows, the master on Unix
    /// (physically once the reader lets go of it). Never throws.
    /// </summary>
    internal void RequestClose()
    {
        if ((Volatile.Read(ref _flags) & FlagCloseRequested) != 0)
            return;

        // Written before the flag, so a tick that sees the flag also sees when the close started.
        Volatile.Write(ref _closeRequestedTick, Environment.TickCount64);
        if (!TrySetFlag(FlagCloseRequested))
            return;

        try
        {
            _backend.CloseOutputTransport();
        }
        catch (Exception)
        {
            // The transport already failed or is being disposed; the reader ends either way.
        }

        Volatile.Read(ref _driver)?.Wake(this);
    }

    /// <summary>
    /// First half of session disposal: in-flight waits fail with <see cref="ObjectDisposedException"/>, and the
    /// pump switches to discarding output until the transport ends.
    /// </summary>
    internal void OnSessionDisposing()
    {
        SetFlag(FlagSessionDisposed);
        WakeDriver();
        SignalConsumer(dataArrived: false);
    }

    /// <summary>
    /// Second half of session disposal, after the backend was disposed: wakes a Unix reader to observe the disposal,
    /// which ends reading even while the master stays open for a hung-up child, releases a Windows read that the
    /// closed pseudo-console did not end (the output pipe held open by another process), and gives back a ring that
    /// only unread output kept: chunks are invalid after disposal. A ring the driver still holds goes back when it lets
    /// go of it.
    /// </summary>
    internal void OnSessionDisposed()
    {
        Volatile.Read(ref _driver)?.CancelRead(this);
        if (Volatile.Read(ref _ringState) == RingKept)
        {
            lock (this)
                ReturnKeptRingLocked();
        }
    }

    /// <summary>
    /// Completes once the driver has let go of the transport and the ring. Every caller waits: the first
    /// through the allocation-free slot, any concurrent one through a task created only then.
    /// </summary>
    internal ValueTask WaitForStopAsync()
    {
        if (IsProducerFinished)
            return default;

        if (Interlocked.CompareExchange(ref _stopWaitState, 1, 0) != 0)
            return new ValueTask(GetStopFallbackTask());

        _stopped.RunContinuationsAsynchronously = true;
        var token = _stopped.Version;

        // Finish may have run before the wait was armed.
        if (IsProducerFinished && Interlocked.CompareExchange(ref _stopWaitState, 2, 1) == 1)
            _stopped.SetResult(true);

        return new ValueTask(this, token);
    }

    private Task GetStopFallbackTask()
    {
        var fallback = Volatile.Read(ref _stoppedFallback);
        if (fallback is null)
        {
            var created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fallback = Interlocked.CompareExchange(ref _stoppedFallback, created, null) ?? created;
        }

        // Finish may have run before the task was published; it reads the field only after publishing completion.
        if (IsProducerFinished)
            fallback.TrySetResult();

        return fallback.Task;
    }

    void IValueTaskSource.GetResult(short token) => _stopped.GetResult(token);

    ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _stopped.GetStatus(token);

    void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
        _stopped.OnCompleted(continuation, state, token, flags);

    /// <summary>
    /// Wakes the driver to re-check state. The Unix backend uses it when input is queued, so that the reader thread,
    /// which writes queued input, also waits for the master to take more.
    /// </summary>
    internal void WakeDriver() => Volatile.Read(ref _driver)?.Wake(this);

    /// <summary>The driver attached to this pump, for tests that check which one a backend chose.</summary>
    internal IPtyOutputDriver? Driver => Volatile.Read(ref _driver);

    private void SetFlag(int flag) => Interlocked.Or(ref _flags, flag);

    private bool TrySetFlag(int flag) => (Interlocked.Or(ref _flags, flag) & flag) == 0;
}
