using System.Runtime.ExceptionServices;
using System.Threading.Tasks.Sources;

namespace MiniPty.Internal;

/// <summary>
/// <see cref="PtySession.ReadOutputAsync"/>: one enumerator per session, reused by every enumeration, that hands
/// out ring slots in place.
/// </summary>
/// <remarks>
/// <para>
/// <c>MoveNextAsync</c> completes synchronously while a chunk is published. Otherwise it returns a value task
/// backed by this object, which the pump completes when output arrives. The continuation therefore resumes on
/// the caller's captured context only when it had to wait, and no library code runs on that context. Nothing
/// is allocated per call: the wait slot, the registration, and the chunk all live here.
/// </para>
/// <para>
/// A caller under a UI context that has been receiving published chunks synchronously for
/// <see cref="PtyConsumerYield.BudgetMs"/> gets the next one through a yield instead: a value task that completes
/// as soon as it is awaited, so the continuation is posted to that context and the dispatcher runs its queue first
/// (<see cref="PtyConsumerYield"/>).
/// </para>
/// <para>
/// A chunk is the unread part of one ring slot, valid until the next <c>MoveNextAsync</c>, which releases the
/// slot back to the pump. Enumeration holds the session's single-consumer ownership from
/// <see cref="GetAsyncEnumerator"/> until end of output, a failure, or <see cref="DisposeAsync"/>, matching the
/// iterator it replaced.
/// </para>
/// </remarks>
internal sealed class PtyOutputReader :
    IAsyncEnumerable<PtyOutputChunk>,
    IAsyncEnumerator<PtyOutputChunk>,
    IValueTaskSource<bool>,
    IPtyOutputWaiter
{
    private static readonly Action<object?> s_onCanceled = static state => ((PtyOutputReader)state!).OnCanceled();

    private readonly PtySession _session;
    private readonly PtyOutputPump _pump;
    private ManualResetValueTaskSourceCore<bool> _core;
    private CancellationTokenRegistration _registration;
    private CancellationToken _token;
    private CancellationTokenSource? _linkedTokenSource;
    private PtyOutputChunk _current;
    private bool _holdsChunk;
    private bool _active;
    // A yield was returned and completes when it is awaited (OnCompleted) or read (GetResult).
    private bool _yieldPending;

    internal PtyOutputReader(PtySession session, PtyOutputPump pump)
    {
        _session = session;
        _pump = pump;
        _core.RunContinuationsAsynchronously = true;
    }

    public PtyOutputChunk Current => _current;

    public IAsyncEnumerator<PtyOutputChunk> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        Begin(default, cancellationToken);

    /// <summary>
    /// Starts an enumeration. Failures surface from the first <c>MoveNextAsync</c>, as they did from the iterator,
    /// so a conflicting reader can still be created and disposed without disturbing the active one.
    /// </summary>
    internal IAsyncEnumerator<PtyOutputChunk> Begin(CancellationToken readToken, CancellationToken enumeratorToken)
    {
        if (_session.IsDisposed)
            return FaultedEnumerator.Disposed;

        if (!_session.TryAcquireOutputConsumer(PtySession.OutputConsumerReadOutputAsync))
            return FaultedEnumerator.Conflict;

        if (!enumeratorToken.CanBeCanceled || enumeratorToken == readToken)
        {
            _token = readToken;
        }
        else if (!readToken.CanBeCanceled)
        {
            _token = enumeratorToken;
        }
        else
        {
            _linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(readToken, enumeratorToken);
            _token = _linkedTokenSource.Token;
        }

        _holdsChunk = false;
        _current = default;
        _yieldPending = false;
        PtyConsumerYield.StartRun();
        _active = true;
        return this;
    }

    public ValueTask<bool> MoveNextAsync()
    {
        if (!_active)
            return new ValueTask<bool>(false);

        ReleaseChunk();
        try
        {
            _session.ThrowIfDisposed();
            _token.ThrowIfCancellationRequested();
            while (true)
            {
                if (TryTakeOrEnd(out var hasChunk))
                {
                    // End of output is never delayed; only a chunk can be handed over through a yield.
                    if (hasChunk && PtyConsumerYield.ShouldYield())
                    {
                        // Taking only peeked, so the chunk stays published and GetResult takes it again. Until then
                        // nothing is held, as during any other wait.
                        _holdsChunk = false;
                        _current = default;
                        _core.Reset();
                        _yieldPending = true;
                        return new ValueTask<bool>(this, _core.Version);
                    }

                    return new ValueTask<bool>(hasChunk);
                }

                _core.Reset();
                // A yield abandoned without being awaited must not complete this wait before the pump signals it.
                _yieldPending = false;
                if (!_pump.ArmWait(this, wakeOnData: true))
                    continue;

                if (_token.CanBeCanceled)
                    _registration = _token.UnsafeRegister(s_onCanceled, this);

                return new ValueTask<bool>(this, _core.Version);
            }
        }
        catch
        {
            End();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_active)
        {
            ReleaseChunk();
            End();
        }

        return default;
    }

    void IPtyOutputWaiter.OnOutputSignaled() => _core.SetResult(true);

    bool IValueTaskSource<bool>.GetResult(short token)
    {
        var registration = _registration;
        _registration = default;
        // Dispose, not Unregister: it waits out a cancellation callback still running elsewhere, so that callback
        // can never claim the next wait.
        registration.Dispose();
        // A yield read without being awaited (blocking on the value task) completes here, as a synchronous
        // completion would have.
        CompletePendingYield();
        // The caller resumed from an asynchronous completion, so its context has run its queue: a new run starts.
        PtyConsumerYield.StartRun();

        try
        {
            _core.GetResult(token);
            _session.ThrowIfDisposed();
            if (TryTakeOrEnd(out var hasChunk))
                return hasChunk;

            // Signals follow a publish, end of output, or disposal, each of which the take above observes.
            throw new InvalidOperationException("PTY output reader was signaled without a state change.");
        }
        catch
        {
            End();
            throw;
        }
    }

    ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token) => _core.GetStatus(token);

    void IValueTaskSource<bool>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
    {
        _core.OnCompleted(continuation, state, token, flags);
        // Completing a yield only now, with the continuation registered, makes the core post it to the context the
        // awaiter captured (or queue it, under ConfigureAwait(false)) instead of the awaiter running it inline.
        CompletePendingYield();
    }

    private void CompletePendingYield()
    {
        if (!_yieldPending)
            return;

        _yieldPending = false;
        _core.SetResult(true);
    }

    /// <summary>
    /// Takes the next chunk (<paramref name="hasChunk"/> = <see langword="true"/>) or observes end of output
    /// (<see langword="false"/>, enumeration ended). Returns <see langword="false"/> when the caller must wait.
    /// </summary>
    private bool TryTakeOrEnd(out bool hasChunk)
    {
        if (TryTake())
        {
            hasChunk = true;
            return true;
        }

        if (!_pump.IsProducerFinished)
        {
            hasChunk = false;
            return false;
        }

        // The pump publishes before it finishes, so a slot published meanwhile is visible now.
        if (TryTake())
        {
            hasChunk = true;
            return true;
        }

        var error = _pump.Error;
        End();
        if (error is not null)
            ExceptionDispatchInfo.Throw(error);

        hasChunk = false;
        return true;
    }

    private bool TryTake()
    {
        if (!_pump.TryPeek(out var data))
            return false;

        _current = new PtyOutputChunk(data);
        _holdsChunk = true;
        return true;
    }

    private void ReleaseChunk()
    {
        if (!_holdsChunk)
            return;

        _holdsChunk = false;
        _current = default;
        _pump.Release();
    }

    private void End()
    {
        if (!_active)
            return;

        _active = false;
        _token = default;
        var linked = _linkedTokenSource;
        _linkedTokenSource = null;
        linked?.Dispose();
        _session.ReleaseOutputConsumer(PtySession.OutputConsumerReadOutputAsync);
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
            _core.SetException(new OperationCanceledException(_token));
        }
        catch (Exception)
        {
        }
    }

    /// <summary><see cref="PtySession.ReadOutputAsync"/> with a cancelable token, which must travel with the enumerable.</summary>
    internal sealed class WithToken(PtyOutputReader reader, CancellationToken readToken) : IAsyncEnumerable<PtyOutputChunk>
    {
        public IAsyncEnumerator<PtyOutputChunk> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            reader.Begin(readToken, cancellationToken);
    }

    /// <summary>An enumeration that could not start; it fails from its first <c>MoveNextAsync</c>.</summary>
    private sealed class FaultedEnumerator(bool conflict) : IAsyncEnumerator<PtyOutputChunk>
    {
        internal static readonly FaultedEnumerator Conflict = new(conflict: true);
        internal static readonly FaultedEnumerator Disposed = new(conflict: false);

        public PtyOutputChunk Current => default;

        public ValueTask<bool> MoveNextAsync() =>
            conflict
                ? throw new InvalidOperationException(PtySession.OutputConsumerConflictMessage)
                : throw new ObjectDisposedException(nameof(PtySession));

        public ValueTask DisposeAsync() => default;
    }
}
