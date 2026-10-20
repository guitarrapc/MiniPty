using System.Runtime.ExceptionServices;
using System.Threading.Tasks.Sources;

namespace MiniPty.Internal;

/// <summary>
/// <see cref="PtySession.Output"/>: a read-only stream view over the output pump's ring, with one copy into the
/// caller's buffer.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ReadAsync(Memory{byte}, CancellationToken)"/> never blocks: it copies published output, or returns
/// a value task backed by this object that the pump completes when output arrives. <see cref="Read(Span{byte})"/>
/// blocks, per the <see cref="Stream"/> contract. Reads copy across slots until the buffer is full or no more
/// output is published, and a slot is released as soon as it has been copied out.
/// </para>
/// <para>
/// A caller under a UI context that has been receiving published output synchronously for
/// <see cref="PtyConsumerYield.BudgetMs"/> gets the next read through a yield: the bytes are copied as usual, and the
/// value task completes when it is awaited, so the continuation is posted to that context and the dispatcher runs
/// its queue first (<see cref="PtyConsumerYield"/>).
/// </para>
/// <para>
/// The first read takes the session's single-consumer ownership and holds it across reads until end of output
/// or a failed read, as the raw transport stream did. Like any <see cref="Stream"/>, the view supports one read
/// at a time.
/// </para>
/// </remarks>
internal sealed class PtyOutputStream : Stream, IValueTaskSource<int>, IPtyOutputWaiter
{
    private static readonly Action<object?> s_onCanceled = static state => ((PtyOutputStream)state!).OnCanceled();

    private readonly PtySession _session;
    private readonly PtyOutputPump _pump;
    private ManualResetValueTaskSourceCore<int> _core;
    private CancellationTokenRegistration _registration;
    private CancellationToken _pendingToken;
    private Memory<byte> _pendingBuffer;
    private bool _holdsOwnership;
    private bool _blockingWait;
    private bool _blockingSignaled;
    // Bytes already copied by a yielded read, delivered when the yield is awaited (OnCompleted) or read (GetResult).
    private int _yieldPendingBytes;

    internal PtyOutputStream(PtySession session, PtyOutputPump pump)
    {
        _session = session;
        _pump = pump;
        _core.RunContinuationsAsynchronously = true;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int ReadByte()
    {
        Span<byte> one = stackalloc byte[1];
        return Read(one) == 0 ? -1 : one[0];
    }

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
            return 0;

        AcquireOwnership();
        try
        {
            while (true)
            {
                var read = TryRead(buffer, out var end);
                if (read > 0 || end)
                    return read;

                lock (this)
                {
                    _blockingWait = true;
                    _blockingSignaled = false;
                }

                if (_pump.ArmWait(this, wakeOnData: true))
                {
                    lock (this)
                    {
                        while (!_blockingSignaled)
                            Monitor.Wait(this);
                    }
                }

                lock (this)
                    _blockingWait = false;
            }
        }
        catch
        {
            ReleaseOwnership();
            throw;
        }
    }

    // The array and APM overloads would otherwise inherit Stream's async-over-sync defaults, which run the
    // blocking Read on a thread-pool worker and cannot cancel it once started.
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state) =>
        TaskToAsyncResult.Begin(ReadAsync(buffer, offset, count, CancellationToken.None), callback, state);

    public override int EndRead(IAsyncResult asyncResult) => TaskToAsyncResult.End<int>(asyncResult);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
            return new ValueTask<int>(0);

        cancellationToken.ThrowIfCancellationRequested();
        AcquireOwnership();
        try
        {
            while (true)
            {
                var read = TryRead(buffer.Span, out var end);
                if (read > 0 && PtyConsumerYield.ShouldYield())
                {
                    // The bytes are in the caller's buffer already; the yield only delays reporting them.
                    _core.Reset();
                    _yieldPendingBytes = read;
                    return new ValueTask<int>(this, _core.Version);
                }

                if (read > 0 || end)
                    return new ValueTask<int>(read);

                _core.Reset();
                // A yield abandoned without being awaited must not complete this wait before the pump signals it.
                _yieldPendingBytes = 0;
                _pendingBuffer = buffer;
                _pendingToken = cancellationToken;
                if (!_pump.ArmWait(this, wakeOnData: true))
                {
                    _pendingBuffer = default;
                    continue;
                }

                if (cancellationToken.CanBeCanceled)
                    _registration = cancellationToken.UnsafeRegister(s_onCanceled, this);

                return new ValueTask<int>(this, _core.Version);
            }
        }
        catch
        {
            ReleaseOwnership();
            throw;
        }
    }

    void IPtyOutputWaiter.OnOutputSignaled()
    {
        if (!Volatile.Read(ref _blockingWait))
        {
            _core.SetResult(0);
            return;
        }

        lock (this)
        {
            _blockingSignaled = true;
            Monitor.PulseAll(this);
        }
    }

    int IValueTaskSource<int>.GetResult(short token)
    {
        var registration = _registration;
        _registration = default;
        // Dispose, not Unregister: it waits out a cancellation callback still running elsewhere, so that callback
        // can never claim the next wait.
        registration.Dispose();
        var buffer = _pendingBuffer;
        _pendingBuffer = default;
        _pendingToken = default;
        // A yield read without being awaited (blocking on the value task) completes here, as a synchronous
        // completion would have.
        CompletePendingYield();
        // The caller resumed from an asynchronous completion, so its context has run its queue: a new run starts.
        PtyConsumerYield.StartRun();

        try
        {
            // A yield completes with the bytes it already copied; a signal completes with 0, and the read follows.
            var yielded = _core.GetResult(token);
            if (yielded > 0)
                return yielded;

            var read = TryRead(buffer.Span, out var end);
            if (read > 0 || end)
                return read;

            // Signals follow a publish, end of output, or disposal, each of which the read above observes.
            throw new InvalidOperationException("PTY output stream was signaled without a state change.");
        }
        catch
        {
            ReleaseOwnership();
            throw;
        }
    }

    ValueTaskSourceStatus IValueTaskSource<int>.GetStatus(short token) => _core.GetStatus(token);

    void IValueTaskSource<int>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
    {
        _core.OnCompleted(continuation, state, token, flags);
        // Completing a yield only now, with the continuation registered, makes the core post it to the context the
        // awaiter captured (or queue it, under ConfigureAwait(false)) instead of the awaiter running it inline.
        CompletePendingYield();
    }

    private void CompletePendingYield()
    {
        var bytes = _yieldPendingBytes;
        if (bytes == 0)
            return;

        _yieldPendingBytes = 0;
        _core.SetResult(bytes);
    }

    /// <summary>
    /// Copies published output. Returns the byte count, or 0 with <paramref name="end"/> set at end of output
    /// (ownership released), or 0 without it when the caller must wait.
    /// </summary>
    private int TryRead(Span<byte> buffer, out bool end)
    {
        end = false;
        _session.ThrowIfDisposed();
        var read = _pump.CopyTo(buffer);
        if (read > 0)
        {
            // Disposal may have returned the ring to the pool during the copy; never hand out those bytes.
            _session.ThrowIfDisposed();
            return read;
        }

        if (!_pump.IsProducerFinished)
            return 0;

        // The pump publishes before it finishes, so output published meanwhile is visible now.
        read = _pump.CopyTo(buffer);
        if (read > 0)
            return read;

        var error = _pump.Error;
        ReleaseOwnership();
        if (error is not null)
            ExceptionDispatchInfo.Throw(error);

        end = true;
        return 0;
    }

    private void AcquireOwnership()
    {
        if (_holdsOwnership)
        {
            _session.ThrowIfDisposed();
            return;
        }

        _session.AcquireOutputConsumer(PtySession.OutputConsumerRawOutput);
        _holdsOwnership = true;
        PtyConsumerYield.StartRun();
    }

    private void ReleaseOwnership()
    {
        if (!_holdsOwnership)
            return;

        _holdsOwnership = false;
        _session.ReleaseOutputConsumer(PtySession.OutputConsumerRawOutput);
    }

    private void OnCanceled()
    {
        if (!_pump.TryCancelWait())
            return;

        // Runs on the canceling thread, which may be a timer thread, and posts the waiter's continuation to the
        // context it captured. A context whose Post throws can never run it; the exception must not escape
        // Cancel or terminate the process from a timer thread.
        try
        {
            _core.SetException(new OperationCanceledException(_pendingToken));
        }
        catch (Exception)
        {
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        // The session owns the transport and the ring.
    }
}
