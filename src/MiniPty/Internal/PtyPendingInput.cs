using System.Buffers;
using System.Threading.Tasks.Sources;

namespace MiniPty.Internal;

/// <summary>A backend's ordered input queue, which removes a queued write whose caller canceled it.</summary>
internal interface IPtyInputQueue
{
    /// <summary>
    /// Removes <paramref name="entry"/> if it is still queued and completes it as canceled. Bytes of it already
    /// written stay written; the rest is discarded.
    /// </summary>
    public void CancelInput(PtyPendingInput entry);
}

/// <summary>
/// A write queued because the transport did not take all of it at once. Completes once the backend has written all
/// of it, or with the error that ended the queue. Writes that complete at once need no entry.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Remaining"/> and <see cref="Next"/> belong to the owning queue and are guarded by its lock. Completion
/// runs outside that lock, and continuations run asynchronously, so a waiter's continuation never runs while the
/// queue is held or on the thread that writes the queue.
/// </para>
/// <para>
/// Entries are reused. A host that writes faster than the child reads waits on one write in every pipe-buffer's
/// worth, and an entry per wait was the only allocation on that path. An entry returns to a process-wide spare slot
/// once both parties are done with it: the completer when <see cref="Complete"/> has returned, and the waiter when it
/// has read the result. Recycling at either point alone would let the next write reset the entry while the other
/// party still uses it. As with any <see cref="ValueTask"/>, the result must be read once.
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
        entry._owned = owned;
        entry._completed = 0;
        entry._users = 2;
        entry.Token = default;
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
        }

        return new ValueTask(this, version);
    }

    /// <summary>Completes the entry once; later calls do nothing. Returns the pooled buffer.</summary>
    internal void Complete(Exception? error)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
            return;

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

        Release();
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
        // Dropping the owner keeps a parked spare from holding a finished session alive.
        Owner = null!;
        _core.Reset();
        _ = Interlocked.CompareExchange(ref s_spare, this, null);
    }

    void IValueTaskSource.GetResult(short token)
    {
        // Only the waiter's one read of a completed result releases the entry. A stale or early read just throws.
        if (token != _core.Version || _core.GetStatus(token) == ValueTaskSourceStatus.Pending)
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
