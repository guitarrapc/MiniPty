using System.Runtime.ExceptionServices;
using System.Threading.Tasks.Sources;

namespace MiniPty.Internal;

/// <summary>
/// Output consumer for one-shot completion (<c>CompleteAsync</c>, <c>PtyCapture.RunAsync</c>): the pump writes
/// into it directly on the thread that delivers output, and the caller awaits end of output with the completion
/// deadlines.
/// </summary>
/// <remarks>
/// <para>
/// Writing where output is delivered removes the hand-off through the ring and needs no consumer thread while
/// output flows. A sink only accumulates and decodes, so the work stays short and never blocks delivery.
/// Writes are serialized with attach and detach under the pump lock, and the caller reads the result only after
/// the drain wait reports end of output.
/// </para>
/// <para>
/// Delivery runs on I/O completions on Windows and on the loop shared by every session on Unix, so a sink must cost the
/// same on any thread: no pool with per-thread caches, no growth that copies what
/// was already written, and no result built until the caller asks for it on its own thread.
/// </para>
/// <para>
/// The drain wait is a single reusable wait slot, so awaiting end of output allocates nothing. Its deadlines
/// follow <see cref="PtyCompleteOptions"/>: the transport closes at <see cref="PtyCompleteOptions.OutputDrainGrace"/>
/// after exit at the latest, and the wait fails with <see cref="TimeoutException"/> when output reading has not
/// finished <see cref="PtyCompleteOptions.OutputReaderCloseTimeout"/> after the close.
/// </para>
/// </remarks>
internal abstract class PtyOutputSink : IValueTaskSource<bool>, IPtyOutputWaiter
{
    private static readonly Action<object?> s_onCanceled = static state => ((PtyOutputSink)state!).OnCanceled();

    private ManualResetValueTaskSourceCore<bool> _core;
    private CancellationTokenRegistration _registration;
    private CancellationToken _token;
    private PtyOutputPump? _pump;
    private long _graceMs = -1;
    private long _closeTimeoutMs = -1;
    private int _timedOut;

    /// <summary>
    /// Receives output in order. Runs on the thread that delivers output, or on the attaching thread for output read
    /// before the sink attached to a pump that has already finished.
    /// </summary>
    /// <param name="data">
    /// Output of one or more transport reads, of any length (a backlog read before the sink attached arrives in as few
    /// writes as the ring allows); valid only during the call.
    /// </param>
    /// <param name="timestamp">
    /// <see cref="System.Diagnostics.Stopwatch"/> timestamp of the read's completion, or 0 for output read before the
    /// sink attached.
    /// </param>
    internal abstract void Write(ReadOnlySpan<byte> data, long timestamp);

    /// <summary>
    /// Called once after the last <see cref="Write"/>, on the same thread, before end of output is published.
    /// Sinks finish their streaming state here (flush decoders). The result itself is built later, on the caller's
    /// thread, once the drain wait reports end of output.
    /// </summary>
    internal virtual void OnOutputEnded()
    {
    }

    /// <summary>
    /// Returns pooled buffers after a failed completion detached the sink; nobody takes its result afterwards. Called
    /// once neither a write nor <see cref="OnOutputEnded"/> can still be in progress (detaching is serialized with
    /// both). Idempotent, and safe whether or not <see cref="OnOutputEnded"/> ran, and after it failed part way.
    /// </summary>
    internal virtual void Abort()
    {
    }

    internal bool IsGraceExpired(long now, long exitTick)
    {
        var graceMs = Volatile.Read(ref _graceMs);
        return graceMs >= 0 && now - exitTick >= graceMs;
    }

    /// <summary>Fails the drain wait once output reading has outlived the close timeout. Runs on a drain tick.</summary>
    /// <returns><see langword="true"/> once the timeout has fired.</returns>
    internal bool CheckCloseTimeout(long now, long closeRequestedTick, PtyOutputPump pump)
    {
        var closeTimeoutMs = Volatile.Read(ref _closeTimeoutMs);
        if (closeTimeoutMs < 0 || now - closeRequestedTick < closeTimeoutMs)
            return false;

        // A full fence before reading the wait state, paired with the waiter's arm-then-check below.
        Interlocked.Exchange(ref _timedOut, 1);
        pump.SignalWaiter();
        return true;
    }

    /// <summary>
    /// Waits for end of output after child exit. Returns <see langword="true"/> once output reading has finished, and
    /// <see langword="false"/> on a wake that changed nothing; callers loop until <see langword="true"/>.
    /// </summary>
    /// <exception cref="TimeoutException">Output reading did not finish within the close timeout.</exception>
    /// <exception cref="ObjectDisposedException">The session was disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    internal ValueTask<bool> WaitForDrainAsync(
        PtyOutputPump pump,
        TimeSpan outputDrainGrace,
        TimeSpan outputReaderCloseTimeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_pump is null)
        {
            _pump = pump;
            Volatile.Write(ref _graceMs, PtyIo.ToWaitMilliseconds(outputDrainGrace));
            Volatile.Write(ref _closeTimeoutMs, PtyIo.ToWaitMilliseconds(outputReaderCloseTimeout));
            // The pump may have left the scheduler before these deadlines existed.
            PtyOutputDrainScheduler.Schedule(pump);
        }

        if (CheckDrained())
            return new ValueTask<bool>(true);

        _core.Reset();
        _core.RunContinuationsAsynchronously = true;
        if (!pump.ArmWait(this, wakeOnData: false))
            return new ValueTask<bool>(CheckDrained());

        // The close timeout may have fired after the check above, when no wait was armed to signal, and it fires
        // only once; take the wait back and report it now.
        if (Volatile.Read(ref _timedOut) != 0 && pump.TryCancelWait())
            return new ValueTask<bool>(CheckDrained());

        _token = cancellationToken;
        if (cancellationToken.CanBeCanceled)
            _registration = cancellationToken.UnsafeRegister(s_onCanceled, this);

        return new ValueTask<bool>(this, _core.Version);
    }

    void IPtyOutputWaiter.OnOutputSignaled() => _core.SetResult(true);

    bool IValueTaskSource<bool>.GetResult(short token)
    {
        var registration = _registration;
        _registration = default;
        // Dispose, not Unregister: it waits out a cancellation callback still running elsewhere, so that callback
        // can never claim the next wait.
        registration.Dispose();

        _core.GetResult(token);
        return CheckDrained();
    }

    ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token) => _core.GetStatus(token);

    void IValueTaskSource<bool>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
        _core.OnCompleted(continuation, state, token, flags);

    private bool CheckDrained()
    {
        if (Volatile.Read(ref _timedOut) != 0)
            throw new TimeoutException("PTY output did not finish draining within the configured timeout.");

        var pump = _pump!;
        if (pump.IsSessionDisposed)
            throw new ObjectDisposedException(nameof(PtySession));

        if (!pump.IsProducerFinished)
            return false;

        if (pump.Error is { } error)
            ExceptionDispatchInfo.Throw(error);

        return true;
    }

    private void OnCanceled()
    {
        if (_pump!.TryCancelWait())
            _core.SetException(new OperationCanceledException(_token));
    }
}
