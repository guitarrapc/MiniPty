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
/// Delivery runs on the loop shared by every session, on Windows as on Unix, so a sink must cost the
/// same on any thread: no pool with per-thread caches, no growth that copies what
/// was already written, and no result built until the caller asks for it on its own thread.
/// </para>
/// <para>
/// The drain wait is a single reusable wait slot, so awaiting end of output allocates nothing. Its deadlines
/// follow <see cref="PtyCompleteOptions"/>: the transport closes at <see cref="PtyCompleteOptions.OutputDrainGrace"/>
/// after exit at the latest, and the wait fails with <see cref="TimeoutException"/> when output reading has not
/// finished <see cref="PtyCompleteOptions.OutputReaderCloseTimeout"/> after the close.
/// </para>
/// <para>
/// <b>Failure.</b> The pump hands output in through <see cref="Deliver"/> and ends it through <see cref="EndOutput"/>.
/// Output past <see cref="PtyCompleteOptions.MaxOutputBytes"/>, a <see cref="Write"/> that throws, and a transport that
/// fails all fail the sink once (<see cref="Failure"/>): it gives its buffers back, discards what still arrives, and
/// kills the child. The completion waits for the child's exit before it drains output, so without the kill a child
/// blocked on a full terminal, or writing on, would keep that wait open for good: a 2.2 GiB flood once stopped
/// collecting at 2 GiB, where a buffer's length wrapped, and the completion waited with the child blocked and no error
/// for as long as it was watched. The completion throws the failure once the exit is observed.
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
    // Delivery's own state, written only by the thread that delivers output (serialized under the pump lock).
    private long _limit = Array.MaxLength;
    private long _delivered;
    private PtySession? _session;
    private Exception? _failure;

    /// <summary>
    /// Bounds the output this sink collects at <paramref name="maxOutputBytes"/> (<see cref="Array.MaxLength"/> at most,
    /// what one result array holds) and names the session whose child a failure ends. Called before the sink attaches.
    /// </summary>
    internal void Configure(PtySession session, int? maxOutputBytes)
    {
        _session = session;
        _limit = Math.Min(maxOutputBytes ?? Array.MaxLength, Array.MaxLength);
    }

    /// <summary>
    /// What stopped this sink collecting output, or null: output past the limit (<see cref="IOException"/>), a failed
    /// <see cref="Write"/>, or the transport's failure. Read by the completion after the exit or the drain wait.
    /// </summary>
    internal Exception? Failure => Volatile.Read(ref _failure);

    /// <summary>
    /// The pump's way in: <see cref="Write"/> while the output stays within the limit and the sink has not failed, and
    /// otherwise nothing. Never throws. Runs where <see cref="Write"/> runs.
    /// </summary>
    internal void Deliver(ReadOnlySpan<byte> data, long timestamp)
    {
        if (_failure is not null)
            return;

        if (data.Length > _limit - _delivered)
        {
            // "Ended", not "killed": a child that already exited, or whose session is being disposed, is not killed here.
            Fail(new IOException(
                $"The child's output exceeded {_limit} bytes, the most this completion collects (PtyCompleteOptions.MaxOutputBytes); collection stopped and the child was ended."));
            return;
        }

        _delivered += data.Length;
        try
        {
            Write(data, timestamp);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    /// <summary>
    /// The pump's end of output: fails the sink when the transport failed (<paramref name="error"/>), and otherwise lets
    /// it finish its streaming state (<see cref="OnOutputEnded"/>). Never throws.
    /// </summary>
    internal void EndOutput(Exception? error)
    {
        if (error is not null)
            Fail(error);

        if (_failure is not null)
            return;

        try
        {
            OnOutputEnded();
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    /// <summary>
    /// Fails the sink once: records <paramref name="error"/>, gives the buffers back now, since nobody takes the result,
    /// and kills the child, whose exit the completion waits for. On the thread that delivers output.
    /// </summary>
    private void Fail(Exception error)
    {
        if (Interlocked.CompareExchange(ref _failure, error, null) is not null)
            return;

        Abort();
        _session?.KillAfterOutputFailure();
    }

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
    /// Returns pooled buffers after a failed completion detached the sink, or when the sink fails itself; nobody takes
    /// its result afterwards. Called once neither a write nor <see cref="OnOutputEnded"/> can still be in progress
    /// (detaching is serialized with both, and a failing sink calls it from its own write). Idempotent, and safe whether
    /// or not <see cref="OnOutputEnded"/> ran, and after it failed part way.
    /// </summary>
    internal virtual void Abort()
    {
    }

    internal bool IsGraceExpired(long now, long exitTick)
    {
        var graceMs = Volatile.Read(ref _graceMs);
        return graceMs >= 0 && now - exitTick >= graceMs;
    }

    /// <summary>
    /// Fails the drain wait once output reading has outlived the close timeout, counted from when the close is done
    /// (<paramref name="closeDoneTick"/>). Runs on a drain tick.
    /// </summary>
    /// <returns><see langword="true"/> once the timeout has fired.</returns>
    internal bool CheckCloseTimeout(long now, long closeDoneTick, PtyOutputPump pump)
    {
        var closeTimeoutMs = Volatile.Read(ref _closeTimeoutMs);
        if (closeTimeoutMs < 0 || now - closeDoneTick < closeTimeoutMs)
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
