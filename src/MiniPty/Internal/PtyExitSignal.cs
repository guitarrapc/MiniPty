using System.Threading.Tasks.Sources;

namespace MiniPty.Internal;

/// <summary>
/// Receives a child exit synchronously on the thread that observed it, typically a process-wide watcher
/// thread. Implementations must only record state or queue work; blocking here stalls exit reporting for
/// every session.
/// </summary>
internal interface IPtyExitListener
{
    void OnChildExited();
}

/// <summary>
/// Child exit state shared by the platform backends: an allocation-free record of the exit status, an
/// optional listener notified on the first transition, and a task created only when somebody awaits exit.
/// </summary>
/// <remarks>
/// <para>
/// A mutable struct held in a backend field and only ever used through that field, so it is never copied.
/// Folding it into the backend saves an object per session, and creating the task lazily saves the
/// task-completion source and its task for sessions whose exit nobody awaits (for example, a session read
/// to the end through <c>ReadOutputAsync</c>).
/// </para>
/// <para>
/// Library-internal waits (one-shot completion) await exit through a single embedded waiter slot instead of
/// the task: the owner implements <see cref="IValueTaskSource{TResult}"/> by forwarding to this struct, so
/// such a wait allocates nothing. A second concurrent internal waiter falls back to the task.
/// </para>
/// <para>
/// Exit and task creation (or waiter arming) can race. Each side publishes its own write with a full fence
/// and then reads the other's, so at least one of them completes the task or the waiter.
/// </para>
/// </remarks>
internal struct PtyExitSignal
{
    private const int Running = 0;
    private const int Exited = 1;
    private const int DisposedBeforeExit = 2;

    private const int WaiterIdle = 0;
    private const int WaiterArmed = 1;
    private const int WaiterCompleted = 2;

    private TaskCompletionSource<PtyExitStatus>? _completion;
    private IPtyExitListener? _listener;
    private ManualResetValueTaskSourceCore<int> _waiter;
    private PtyExitStatus _status;
    private int _state;
    private int _waiterState;

    public void SetListener(IPtyExitListener listener) => Volatile.Write(ref _listener, listener);

    /// <summary>
    /// Gets a task that completes with the exit status, or faults with <see cref="ObjectDisposedException"/>
    /// when the owner was disposed before exit. Continuations run asynchronously.
    /// </summary>
    public Task<PtyExitStatus> GetTask()
    {
        var completion = Volatile.Read(ref _completion);
        if (completion is null)
        {
            var created = new TaskCompletionSource<PtyExitStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            completion = Interlocked.CompareExchange(ref _completion, created, null) ?? created;
            // Exit or disposal may have been recorded before the source was published.
            Complete(completion, Volatile.Read(ref _state));
        }

        return completion.Task;
    }

    /// <summary>
    /// Waits for the exit code without allocating when the exit is already known or the waiter slot is free.
    /// </summary>
    /// <param name="owner">The object that forwards <see cref="IValueTaskSource{TResult}"/> to this struct.</param>
    /// <remarks>Each returned value must be awaited exactly once. Continuations run asynchronously.</remarks>
    public ValueTask<int> WaitForExitCodeAsync(IValueTaskSource<int> owner)
    {
        if (Volatile.Read(ref _state) == Exited)
            return new ValueTask<int>(_status.ExitCode);

        if (Interlocked.CompareExchange(ref _waiterState, WaiterArmed, WaiterIdle) != WaiterIdle)
            return AwaitExitCodeAsync(GetTask());

        _waiter.RunContinuationsAsynchronously = true;
        var token = _waiter.Version;
        // Exit or disposal may have been recorded before the waiter armed.
        CompleteWaiter(Volatile.Read(ref _state));
        return new ValueTask<int>(owner, token);
    }

    public ValueTaskSourceStatus GetWaiterStatus(short token) => _waiter.GetStatus(token);

    public void OnWaiterCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
        _waiter.OnCompleted(continuation, state, token, flags);

    public int GetWaiterResult(short token) => _waiter.GetResult(token);

    /// <summary>Records exit. Returns <see langword="true"/> for the first transition only.</summary>
    public bool TrySetExited(PtyExitStatus status)
    {
        if (Volatile.Read(ref _state) != Running)
            return false;

        _status = status;
        if (Interlocked.CompareExchange(ref _state, Exited, Running) != Running)
            return false;

        if (Volatile.Read(ref _completion) is { } completion)
            Complete(completion, Exited);

        CompleteWaiter(Exited);
        Volatile.Read(ref _listener)?.OnChildExited();
        return true;
    }

    /// <summary>Records that the owner was disposed before exit, faulting any current or future wait.</summary>
    public void SetDisposedBeforeExit()
    {
        if (Interlocked.CompareExchange(ref _state, DisposedBeforeExit, Running) != Running)
            return;

        if (Volatile.Read(ref _completion) is { } completion)
            Complete(completion, DisposedBeforeExit);

        CompleteWaiter(DisposedBeforeExit);
    }

    private static async ValueTask<int> AwaitExitCodeAsync(Task<PtyExitStatus> exited) =>
        (await exited.ConfigureAwait(false)).ExitCode;

    /// <summary>Completes the armed waiter once, by whichever side observes both arming and the final state.</summary>
    private void CompleteWaiter(int state)
    {
        if (state == Running || Interlocked.CompareExchange(ref _waiterState, WaiterCompleted, WaiterArmed) != WaiterArmed)
            return;

        if (state == Exited)
            _waiter.SetResult(_status.ExitCode);
        else
            _waiter.SetException(new ObjectDisposedException(nameof(PtySession)));
    }

    private readonly void Complete(TaskCompletionSource<PtyExitStatus> completion, int state)
    {
        switch (state)
        {
            case Exited:
                completion.TrySetResult(_status);
                break;
            case DisposedBeforeExit:
                if (completion.TrySetException(new ObjectDisposedException(nameof(PtySession))))
                    _ = completion.Task.Exception; // Observed: waiters see it, nobody else must.
                break;
        }
    }
}
