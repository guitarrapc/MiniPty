using System.Buffers;
using System.Threading.Tasks.Sources;

namespace MiniPty.Internal;

/// <summary>A backend's ordered input queue, which removes a queued write whose caller canceled it.</summary>
internal interface IPtyInputQueue
{
    /// <summary>
    /// Removes <paramref name="entry"/> if it is still queued and completes it as canceled. Bytes of it already
    /// handed to the transport still reach the child, and the rest is discarded.
    /// </summary>
    public void CancelInput(PtyPendingInput entry);
}

/// <summary>
/// A write queued because the transport did not take all of it at once. Completes once the backend has written all
/// of it, or with the error that ended the queue. Writes that complete at once need no entry.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Remaining"/> and the queue links belong to the owning queue and are guarded by its lock. Completion
/// runs outside that lock and never runs a continuation inline, so no waiter's code runs while the queue is held.
/// A blocking writer waits in <see cref="Wait"/> instead, and the completing thread wakes it directly, so it does not
/// need a thread-pool worker to resume.
/// </para>
/// <para>
/// Entries are reused, because otherwise a host that writes faster than the terminal takes input would allocate one
/// for each wait. An entry returns to a process-wide spare slot once both parties are done with it: the completer when
/// <see cref="Complete"/> has returned, and the waiter when it has read the result. Recycling at either point alone
/// would let the next write reset the entry while the other party still uses it. As with any
/// <see cref="ValueTask"/>, the result must be read once.
/// </para>
/// </remarks>
internal sealed class PtyPendingInput : IValueTaskSource
{
    // One spare for the whole process: the common case is one writer waiting at a time, and a miss only allocates.
    private static PtyPendingInput? s_spare;

    private ManualResetValueTaskSourceCore<bool> _core;
    private byte[]? _owned;
    private int _completed;
    // Parties still using the entry: the completer and the waiter. The last to let go recycles it.
    private int _users;
    // Set by the waiter's first read of the result, so a second read cannot release the entry again.
    private int _resultRead;
    // Set while a thread blocks in Wait, so Complete wakes it directly.
    private int _blockingWaiter;
    private CancellationTokenRegistration _registration;

    private PtyPendingInput(IPtyInputQueue owner)
    {
        Owner = owner;
        _core.RunContinuationsAsynchronously = true;
    }

    /// <summary>Queue that holds the entry and removes it on cancellation.</summary>
    internal IPtyInputQueue Owner { get; private set; }

    /// <summary>Not yet handed to the transport. Guarded by the owner's lock.</summary>
    internal ReadOnlyMemory<byte> Remaining;

    /// <summary>Next queued write, or the next entry of a chain being completed. Guarded by the owner's lock.</summary>
    internal PtyPendingInput? Next;

    // Previous queued write, so a canceled entry leaves the queue without a walk from the head. Writes that share one
    // token are canceled newest first, and a walk would make canceling them quadratic. Guarded by the owner's lock.
    private PtyPendingInput? _previous;

    // Whether the entry is in its owner's queue. A detached chain keeps its links until it is completed outside the
    // lock, so the links alone cannot tell a cancellation that the entry already left. Guarded by the owner's lock.
    private bool _queued;

    /// <summary>Token whose cancellation removes this entry; reported by the cancellation exception.</summary>
    internal CancellationToken Token { get; private set; }

    /// <summary>Returns an entry for a write that has to wait, reusing the spare when there is one.</summary>
    /// <param name="owner">Queue that holds the entry and removes it on cancellation.</param>
    /// <param name="remaining">Bytes not yet handed to the transport.</param>
    /// <param name="owned">Pooled buffer behind <paramref name="remaining"/>, returned on completion; null when the caller owns the memory.</param>
    internal static PtyPendingInput Rent(IPtyInputQueue owner, ReadOnlyMemory<byte> remaining, byte[]? owned)
    {
        var entry = Interlocked.Exchange(ref s_spare, null);
        if (entry is null)
            entry = new PtyPendingInput(owner);
        else
            entry.Owner = owner;

        entry.Remaining = remaining;
        entry.Next = null;
        entry._previous = null;
        entry._queued = false;
        entry._owned = owned;
        entry._completed = 0;
        entry._resultRead = 0;
        entry._blockingWaiter = 0;
        entry._users = 2;
        return entry;
    }

    /// <summary>
    /// Returns the caller's task for this entry, registering <paramref name="cancellationToken"/> to remove it from
    /// the queue. Call outside the owner's lock: an already canceled token runs the removal synchronously.
    /// </summary>
    internal ValueTask ToValueTask(CancellationToken cancellationToken)
    {
        var version = _core.Version;
        if (cancellationToken.CanBeCanceled)
        {
            Token = cancellationToken;
            _registration = cancellationToken.UnsafeRegister(
                static state =>
                {
                    var entry = (PtyPendingInput)state!;
                    entry.Owner.CancelInput(entry);
                },
                this);

            // The entry may have completed before the registration existed, so Complete could not drop it. A full fence
            // keeps the store above from passing the check below, pairing with the exchange in Complete, so one side
            // always sees the other.
            Interlocked.MemoryBarrier();
            if (Volatile.Read(ref _completed) != 0)
                _registration.Unregister();
        }

        return new ValueTask(this, version);
    }

    /// <summary>
    /// Blocks until the entry completes, then reads the result: returns, or throws the error that ended the write.
    /// This is the waiter's one read.
    /// </summary>
    internal void Wait()
    {
        var version = _core.Version;
        if (_core.GetStatus(version) == ValueTaskSourceStatus.Pending)
        {
            lock (this)
            {
                // A full fence before the status check, paired with the one in Complete before it checks for a
                // waiter: one side always sees the other.
                Interlocked.Exchange(ref _blockingWaiter, 1);
                while (_core.GetStatus(version) == ValueTaskSourceStatus.Pending)
                    Monitor.Wait(this);
            }
        }

        ((IValueTaskSource)this).GetResult(version);
    }

    /// <summary>Completes the entry once; later calls do nothing. Returns the pooled buffer.</summary>
    internal void Complete(Exception? error)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
            return;

        // Dropped here, not only when the result is read, so a token cannot keep a write nobody awaits reachable.
        // It does not wait for a callback that is running, which may be the one completing this entry.
        _registration.Unregister();
        Remaining = default;
        var owned = _owned;
        _owned = null;
        if (owned is not null)
            ArrayPool<byte>.Shared.Return(owned);

        // A context whose Post throws can never run the continuation; that must not escape into the thread that
        // writes the queue or a disposing caller.
        try
        {
            if (error is null)
                _core.SetResult(true);
            else
                _core.SetException(error);
        }
        catch (Exception)
        {
        }

        Interlocked.MemoryBarrier();
        if (Volatile.Read(ref _blockingWaiter) != 0)
        {
            lock (this)
                Monitor.PulseAll(this);
        }

        Release();
    }

    /// <summary>Appends <paramref name="entry"/> to the queue from <paramref name="head"/> to <paramref name="tail"/>. Caller holds the owner's lock.</summary>
    internal static void Enqueue(ref PtyPendingInput? head, ref PtyPendingInput? tail, PtyPendingInput entry)
    {
        entry._previous = tail;
        entry.Next = null;
        entry._queued = true;
        if (tail is null)
            head = entry;
        else
            tail.Next = entry;

        tail = entry;
    }

    /// <summary>Removes and returns the head of the queue. Caller holds the owner's lock.</summary>
    internal static PtyPendingInput DequeueHead(ref PtyPendingInput? head, ref PtyPendingInput? tail)
    {
        var entry = head!;
        head = entry.Next;
        if (head is null)
            tail = null;
        else
            head._previous = null;

        entry.Next = null;
        entry._queued = false;
        return entry;
    }

    /// <summary>
    /// Removes <paramref name="entry"/> from the queue if it is still there. Returns <see langword="false"/> when it
    /// has already left. Caller holds the owner's lock.
    /// </summary>
    internal static bool Remove(ref PtyPendingInput? head, ref PtyPendingInput? tail, PtyPendingInput entry)
    {
        if (!entry._queued)
            return false;

        if (entry._previous is null)
            head = entry.Next;
        else
            entry._previous.Next = entry.Next;

        if (entry.Next is null)
            tail = entry._previous;
        else
            entry.Next._previous = entry._previous;

        entry.Next = null;
        entry._previous = null;
        entry._queued = false;
        return true;
    }

    /// <summary>
    /// Empties the queue and returns its entries as a chain linked through <see cref="Next"/>, for
    /// <see cref="CompleteChain"/>. Caller holds the owner's lock.
    /// </summary>
    internal static PtyPendingInput? DetachAll(ref PtyPendingInput? head, ref PtyPendingInput? tail)
    {
        var chain = head;
        for (var entry = chain; entry is not null; entry = entry.Next)
        {
            entry._previous = null;
            entry._queued = false;
        }

        head = tail = null;
        return chain;
    }

    /// <summary>Completes every entry of a chain linked through <see cref="Next"/>. Call outside the owner's lock.</summary>
    internal static void CompleteChain(PtyPendingInput? chain, Exception? error)
    {
        while (chain is not null)
        {
            var next = chain.Next;
            chain.Next = null;
            chain.Complete(error);
            chain = next;
        }
    }

    private void Release()
    {
        if (Interlocked.Decrement(ref _users) != 0)
            return;

        // Nobody uses the entry any more: reset it for the next write, and park it unless a spare is parked already.
        // Dropping the owner and the token keeps a parked spare from holding a finished session or its token alive.
        Owner = null!;
        Token = default;
        _registration = default;
        _core.Reset();
        _ = Interlocked.CompareExchange(ref s_spare, this, null);
    }

    void IValueTaskSource.GetResult(short token)
    {
        // Only the waiter's first read of a completed result releases the entry. A stale, early, or repeated read only
        // reads the result or throws.
        if (token != _core.Version
            || _core.GetStatus(token) == ValueTaskSourceStatus.Pending
            || Interlocked.Exchange(ref _resultRead, 1) != 0)
        {
            _core.GetResult(token);
            return;
        }

        var registration = _registration;
        _registration = default;
        // Waits for a cancellation callback that is running, so none can reach the entry once it is reused.
        registration.Dispose();
        try
        {
            _core.GetResult(token);
        }
        finally
        {
            Release();
        }
    }

    ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _core.GetStatus(token);

    void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
        _core.OnCompleted(continuation, state, token, flags);
}
