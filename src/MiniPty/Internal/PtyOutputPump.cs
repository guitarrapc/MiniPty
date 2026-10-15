using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;

namespace MiniPty.Internal;

/// <summary>Receives a wake-up from <see cref="PtyOutputPump"/> for the wait it armed.</summary>
internal interface IPtyOutputWaiter
{
    /// <summary>Runs on whichever thread signaled; implementations only complete their pending wait.</summary>
    void OnOutputSignaled();
}

/// <summary>
/// The only reader of a session's output transport: reads on a dedicated <see cref="PtyReaderThread"/> from
/// spawn until end of output, into a ring of fixed-size slots that the session's one active consumer drains.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ring.</b> <see cref="SlotCount"/> slots of <see cref="SlotSize"/> bytes in one array from the shared
/// pool. Each transport read fills one slot, and bytes that arrive within a short micro-window are appended to
/// it, so bulk output is delivered in few chunks without delaying interactive output. When every slot is
/// published and unread the pump stops reading, and backpressure reaches the child through the OS pipe. The
/// ring is single-producer, single-consumer: the pump owns <see cref="_tail"/>, the active consumer owns
/// <see cref="_head"/>, and <see cref="_count"/> publishes between them with full fences.
/// </para>
/// <para>
/// <b>Consumers.</b> <c>ReadOutputAsync</c> and the <c>Output</c> stream read the ring on their own thread and
/// wait through <see cref="ArmWait"/> without blocking it. One-shot completion attaches a
/// <see cref="PtyOutputSink"/> instead, and the pump writes into it directly on the reader thread; slots
/// published before the sink attached are delivered first.
/// </para>
/// <para>
/// <b>End of output.</b> Unix PTYs report end of output once every slave descriptor closes. ConPTY reports it
/// only after the pseudo-console closes, and a Unix grandchild can hold the slave open, so after child exit
/// <see cref="PtyOutputDrainScheduler"/> closes the transport once reads have been quiet for
/// <see cref="PostExitQuietMs"/> (one-shot completion also bounds this by its grace and close timeouts).
/// </para>
/// <para>
/// <b>Lifetime.</b> The pump and the session each hold a reference to the ring; it returns to the pool when both
/// are done, so the reader never writes into an array another renter owns. Disposal makes the pump discard
/// output until the transport ends, which ConPTY needs to finish closing.
/// </para>
/// </remarks>
internal sealed class PtyOutputPump : IValueTaskSource
{
    internal const int SlotCount = 4;
    internal const int SlotSize = PtyReadBuffer.Size;

    /// <summary>Post-exit silence after which the transport is closed so the reader sees end of output.</summary>
    internal const int PostExitQuietMs = 100;

    private const int FlagProducerFinished = 1;
    private const int FlagSessionDisposed = 2;
    private const int FlagCloseRequested = 4;
    private const int FlagExitObserved = 8;

    private readonly IPtyBackend _backend;
    private readonly byte[] _ring;
    private SlotLengths _lengths;
    // Consumer side: the slot being read, and how much of it the Output stream has already copied out.
    private int _head;
    private int _headOffset;
    // Producer side: the slot the next read fills.
    private int _tail;
    // Published slots the consumer has not released.
    private int _count;
    private int _flags;
    private int _ringReferences = 2;
    private int _producerWaiting;
    private int _consumerWaiting;
    private bool _waiterWantsData;
    private IPtyOutputWaiter? _waiter;
    private PtyOutputSink? _sink;
    private PtyReaderThread? _reader;
    private Exception? _error;
    private long _lastReadTick;
    private long _exitTick;
    private long _closeRequestedTick;
    private ManualResetValueTaskSourceCore<bool> _stopped;
    private int _stopWaitState;

    internal PtyOutputPump(IPtyBackend backend)
    {
        _backend = backend;
        _ring = ArrayPool<byte>.Shared.Rent(SlotCount * SlotSize);
    }

    [InlineArray(SlotCount)]
    private struct SlotLengths
    {
        private int _element;
    }

    /// <summary>Guarded by the <see cref="PtyOutputDrainScheduler"/> lock.</summary>
    internal bool DrainScheduled;

    /// <summary>Guarded by the <see cref="PtyOutputDrainScheduler"/> lock.</summary>
    internal bool DrainRescheduled;

    internal bool IsProducerFinished => (Volatile.Read(ref _flags) & FlagProducerFinished) != 0;

    internal bool IsSessionDisposed => (Volatile.Read(ref _flags) & FlagSessionDisposed) != 0;

    /// <summary>The transport failure that ended the pump, if any. Read only after <see cref="IsProducerFinished"/>.</summary>
    internal Exception? Error => _error;

    internal long ExitTick => Volatile.Read(ref _exitTick);

    /// <summary>Starts reading on a reader thread. Called once, by the session constructor.</summary>
    internal void Start() => PtyReaderThread.Run(this);

    internal void AttachReader(PtyReaderThread reader) => Volatile.Write(ref _reader, reader);

    // ---------------------------------------------------------------- Producer (reader thread)

    /// <summary>Reads until end of output, then publishes completion. Runs on the reader thread and never throws.</summary>
    internal void Run()
    {
        try
        {
            if (_backend.TryBeginOutputRead())
            {
                try
                {
                    Produce(_reader!);
                }
                finally
                {
                    _backend.EndOutputRead();
                }
            }
        }
        catch (Exception ex)
        {
            // Disposal tears the transport down under the reader; that is end of output, not a failure.
            if (!IsSessionDisposed)
                _error = ex;
        }

        Finish();
    }

    private void Produce(PtyReaderThread reader)
    {
        while (true)
        {
            if (!IsSessionDisposed)
            {
                if (Volatile.Read(ref _sink) is not null)
                {
                    if (Volatile.Read(ref _count) > 0)
                        DeliverBacklog();
                }
                else if (Volatile.Read(ref _count) == SlotCount)
                {
                    // A closed Unix master reports end of output without a read; the ring keeps what it holds.
                    if (_backend.IsOutputClosed)
                        return;

                    WaitForFreeSlot(reader);
                    continue;
                }
            }

            // After disposal nothing reads the ring, so the reader reuses the same slot only to let the transport end.
            var slot = _tail;
            var buffer = _ring.AsSpan(slot * SlotSize, SlotSize);
            var read = _backend.ReadOutput(buffer, reader);
            if (read < 0)
                continue; // Woken: re-check state.
            if (read == 0)
                return;

            var timestamp = Stopwatch.GetTimestamp();
            // Stamped per read that returned bytes, so the empty tail of the micro-window does not postpone the
            // post-exit quiet close.
            Volatile.Write(ref _lastReadTick, Environment.TickCount64);
            var length = read;
            var end = false;
            while (length < SlotSize)
            {
                var more = _backend.ReadMoreOutput(buffer[length..], reader, out end);
                if (more <= 0)
                    break;

                length += more;
                Volatile.Write(ref _lastReadTick, Environment.TickCount64);
            }

            if (!IsSessionDisposed)
                Deliver(slot, length, timestamp);

            if (end)
                return;
        }
    }

    private void Deliver(int slot, int length, long timestamp)
    {
        lock (this)
        {
            if (_sink is { } sink)
            {
                // Slots published before the sink attached go first, so the sink sees output in order.
                DrainRingInto(sink);
                sink.Write(_ring.AsSpan(slot * SlotSize, length), timestamp);
                return;
            }
        }

        _lengths[slot] = length;
        _tail = (slot + 1) % SlotCount;
        Interlocked.Increment(ref _count);
        SignalConsumer(dataArrived: true);
    }

    private void DeliverBacklog()
    {
        lock (this)
        {
            if (_sink is { } sink)
                DrainRingInto(sink);
        }
    }

    /// <summary>Hands every published slot to <paramref name="sink"/>. Caller holds the pump lock.</summary>
    private void DrainRingInto(PtyOutputSink sink)
    {
        while (Volatile.Read(ref _count) > 0)
        {
            var head = _head;
            var offset = _headOffset;
            sink.Write(_ring.AsSpan(head * SlotSize + offset, _lengths[head] - offset), timestamp: 0);
            _headOffset = 0;
            _head = (head + 1) % SlotCount;
            Interlocked.Decrement(ref _count);
        }
    }

    private void WaitForFreeSlot(PtyReaderThread reader)
    {
        Interlocked.Exchange(ref _producerWaiting, 1);

        // Re-checked after publishing the flag: a consumer that released a slot before seeing it does not wake us.
        if (Volatile.Read(ref _count) == SlotCount
            && !IsSessionDisposed
            && !_backend.IsOutputClosed
            && Volatile.Read(ref _sink) is null)
        {
            reader.WaitForWake(Timeout.Infinite);
        }

        Volatile.Write(ref _producerWaiting, 0);
    }

    private void Finish()
    {
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

            // The pump no longer touches the ring or the thread. Let go of both before completion becomes
            // observable: a consumer that disposes the session as soon as it sees end of output then makes the
            // final ring release, returning the array on its own thread rather than stranding it in this thread's
            // pool cache, and a session started right after finds this thread already parked.
            ReleaseRing();
            _reader!.OnPumpFinished(this);
            SetFlag(FlagProducerFinished);
        }

        SignalConsumer(dataArrived: false);
        if (Interlocked.CompareExchange(ref _stopWaitState, 2, 1) == 1)
            _stopped.SetResult(true);
    }

    // ---------------------------------------------------------------- Ring consumer (ReadOutputAsync, Output)

    /// <summary>Gets the unread part of the oldest published slot without releasing it.</summary>
    internal bool TryPeek(out ReadOnlyMemory<byte> data)
    {
        if (Volatile.Read(ref _count) == 0)
        {
            data = default;
            return false;
        }

        var head = _head;
        data = _ring.AsMemory(head * SlotSize + _headOffset, _lengths[head] - _headOffset);
        return true;
    }

    /// <summary>Releases the oldest published slot so the pump can refill it.</summary>
    internal void Release()
    {
        _headOffset = 0;
        _head = (_head + 1) % SlotCount;
        Interlocked.Decrement(ref _count);
        if (Volatile.Read(ref _producerWaiting) != 0)
            Volatile.Read(ref _reader)?.Wake(this);
    }

    /// <summary>Copies published output into <paramref name="destination"/>, releasing every slot it empties.</summary>
    internal int CopyTo(Span<byte> destination)
    {
        var copied = 0;
        while (copied < destination.Length && Volatile.Read(ref _count) > 0)
        {
            var head = _head;
            var available = _lengths[head] - _headOffset;
            var take = Math.Min(available, destination.Length - copied);
            _ring.AsSpan(head * SlotSize + _headOffset, take).CopyTo(destination[copied..]);
            copied += take;
            if (take == available)
                Release();
            else
                _headOffset += take;
        }

        return copied;
    }

    /// <summary>
    /// Arms <paramref name="waiter"/> for the next signal: published output (when <paramref name="wakeOnData"/>),
    /// end of output, disposal, or a drain timeout.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the wait is armed and the waiter will be signaled; <see langword="false"/> when
    /// the condition already holds and the caller should re-check synchronously instead of waiting.
    /// </returns>
    internal bool ArmWait(IPtyOutputWaiter waiter, bool wakeOnData)
    {
        Volatile.Write(ref _waiter, waiter);
        _waiterWantsData = wakeOnData;
        Interlocked.Exchange(ref _consumerWaiting, 1);

        // Re-checked after publishing the wait (Dekker with the producer's publish-then-check).
        var ready = (wakeOnData && Volatile.Read(ref _count) > 0)
            || (Volatile.Read(ref _flags) & (FlagProducerFinished | FlagSessionDisposed)) != 0;
        if (!ready)
            return true;

        // Withdraw, unless a signaler already claimed the wait; then the signal is on its way.
        return Interlocked.CompareExchange(ref _consumerWaiting, 0, 1) != 1;
    }

    /// <summary>Claims the armed wait for cancellation. Exactly one of a signal and a cancellation wins.</summary>
    internal bool TryCancelWait() => Interlocked.CompareExchange(ref _consumerWaiting, 0, 1) == 1;

    /// <summary>Signals the armed wait for a condition the waiter checks itself (a drain timeout).</summary>
    internal void SignalWaiter() => SignalConsumer(dataArrived: false);

    private void SignalConsumer(bool dataArrived)
    {
        if (Volatile.Read(ref _consumerWaiting) == 0 || (dataArrived && !_waiterWantsData))
            return;

        if (Interlocked.CompareExchange(ref _consumerWaiting, 0, 1) == 1)
            Volatile.Read(ref _waiter)!.OnOutputSignaled();
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
        Volatile.Read(ref _reader)?.Wake(this);
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
    /// have gone quiet (or a completion grace expires), then lets a completion sink watch its close timeout.
    /// </summary>
    /// <returns><see langword="true"/> once this pump needs no further ticks.</returns>
    internal bool OnDrainTick(long now)
    {
        var flags = Volatile.Read(ref _flags);
        if ((flags & (FlagProducerFinished | FlagSessionDisposed)) != 0)
            return true;

        var sink = Volatile.Read(ref _sink);
        if ((flags & FlagCloseRequested) == 0)
        {
            if (!IsQuiet(now, sink) && (sink is null || !sink.IsGraceExpired(now, ExitTick)))
                return false;

            RequestClose();
        }

        // The transport ends shortly. Only a completion sink still has a deadline to watch.
        return sink is null || sink.CheckCloseTimeout(now, Volatile.Read(ref _closeRequestedTick), this);
    }

    private bool IsQuiet(long now, PtyOutputSink? sink)
    {
        // A pump parked on a full ring is not reading, so silence says nothing about the transport.
        if (Volatile.Read(ref _producerWaiting) != 0)
            return false;

        var lastRead = Volatile.Read(ref _lastReadTick);

        // One-shot completion never closes before the first read: ConPTY can emit its first frame after a fast
        // child has already exited, and closing then would lose it. The grace bounds that wait.
        if (sink is not null && lastRead == 0)
            return false;

        return now - Math.Max(lastRead, ExitTick) >= PostExitQuietMs;
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

        Volatile.Read(ref _reader)?.Wake(this);
    }

    /// <summary>
    /// First half of session disposal: in-flight waits fail with <see cref="ObjectDisposedException"/>, and the
    /// pump switches to discarding output until the transport ends.
    /// </summary>
    internal void OnSessionDisposing()
    {
        SetFlag(FlagSessionDisposed);
        WakeReader();
        SignalConsumer(dataArrived: false);
    }

    /// <summary>
    /// Second half of session disposal, after the backend closed the transport: wakes a Unix reader to observe the
    /// close, and drops the session's reference to the ring.
    /// </summary>
    internal void OnSessionDisposed()
    {
        WakeReader();
        ReleaseRing();
    }

    /// <summary>Completes once the reader thread has let go of the transport and the ring.</summary>
    internal ValueTask WaitForStopAsync()
    {
        if (IsProducerFinished || Interlocked.CompareExchange(ref _stopWaitState, 1, 0) != 0)
            return default;

        _stopped.RunContinuationsAsynchronously = true;
        var token = _stopped.Version;

        // Finish may have run before the wait was armed.
        if (IsProducerFinished && Interlocked.CompareExchange(ref _stopWaitState, 2, 1) == 1)
            _stopped.SetResult(true);

        return new ValueTask(this, token);
    }

    void IValueTaskSource.GetResult(short token) => _stopped.GetResult(token);

    ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _stopped.GetStatus(token);

    void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
        _stopped.OnCompleted(continuation, state, token, flags);

    private void WakeReader() => Volatile.Read(ref _reader)?.Wake(this);

    private void ReleaseRing()
    {
        if (Interlocked.Decrement(ref _ringReferences) == 0)
            ArrayPool<byte>.Shared.Return(_ring);
    }

    private void SetFlag(int flag) => Interlocked.Or(ref _flags, flag);

    private bool TrySetFlag(int flag) => (Interlocked.Or(ref _flags, flag) & flag) == 0;
}
