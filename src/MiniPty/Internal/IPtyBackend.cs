namespace MiniPty.Internal;

/// <summary>
/// Thin internal runtime contract for an active PTY child. Platform backends implement this after
/// <c>WindowsPtyBackend.Start</c> or <c>UnixPtyBackend.Start</c>; <see cref="PtySession"/> is the sole
/// public coordinator. Not a launch plugin point—keep members mapped to session operations only.
/// </summary>
/// <remarks>
/// <see cref="IDisposable.Dispose"/> is called at most once, by the session, which admits only the first of
/// concurrent disposals. Other members can still run on other threads during and after it: caller threads, the
/// exit watcher, timers, and the output driver. So a backend must never use a native handle after Dispose has
/// released it; Windows hands a released handle's value to the next object the process opens.
/// </remarks>
internal interface IPtyBackend : IDisposable
{
    /// <summary>Gets the stdin stream: a <see cref="PtyInputStream"/> over the input queue.</summary>
    public Stream Input { get; }

    // Input. A write takes what the transport accepts now on the calling thread, and the rest is queued in order.
    // On Unix the session's output driver (the watcher loop on Linux, its reader thread on macOS and FreeBSD) writes the
    // queue when the master can take more. On
    // Windows I/O completions on the overlapped input pipe write it as ConPTY reads input again. PtyInputWrites turns
    // a queued write into the caller's task or a blocking wait.

    /// <summary>
    /// Writes what the transport takes now and queues the rest in issue order. Returns the queued write, or null when
    /// everything was written. The caller keeps the memory unchanged until the write completes. When
    /// <paramref name="ownedBuffer"/> is set, the backend owns that pooled array, which backs
    /// <paramref name="bytes"/>, and returns it when done, also when this throws.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The session was disposed.</exception>
    /// <exception cref="IOException">Input is closed, or the transport failed.</exception>
    public PtyPendingInput? QueueInput(ReadOnlyMemory<byte> bytes, byte[]? ownedBuffer);
    /// <summary>As the memory overload. Bytes that must wait are copied into a pooled buffer.</summary>
    public PtyPendingInput? QueueInput(ReadOnlySpan<byte> bytes);
    /// <summary>Gives the backend the pump whose output driver writes queued input.</summary>
    public void SetOutputPump(PtyOutputPump pump);

    public int ProcessId { get; }
    public bool HasExited { get; }
    public int ExitCode { get; }
    /// <summary>
    /// Raw OS signal that terminated the child, observed after exit. Null on normal exit, before
    /// exit, when the wait status was lost (ECHILD), and always on Windows.
    /// </summary>
    public int? ExitSignal { get; }
    public PtySize Size { get; }
    public string? ActiveProcessName { get; }
    public void Resize(int columns, int rows, int pixelWidth, int pixelHeight);
    /// <summary>
    /// Clears the pseudo-console's screen buffer, for a frontend that has cleared its own display. A no-op where the
    /// platform has no such operation: Unix, and Windows on the in-box ConPTY. Like <see cref="Resize"/>, a no-op once
    /// the transport is closed.
    /// </summary>
    public void Clear();
    public void SendEof();
    public void Kill();
    public void Kill(PtySignal signal);
    /// <summary>
    /// Completes with the exit status once the OS reports that the child exited, or faults with
    /// <see cref="ObjectDisposedException"/> when the backend is disposed first. Continuations run
    /// asynchronously. Completion is driven by an OS exit notification, so waiting on it holds no thread.
    /// The task is created on first access, so a session whose exit nobody awaits never allocates it.
    /// </summary>
    public Task<PtyExitStatus> Exited { get; }
    /// <summary>
    /// Waits for the exit code without allocating when the exit is already known or no other internal wait
    /// is pending. For library-internal waits that await the result exactly once.
    /// </summary>
    public ValueTask<int> WaitForExitCodeAsync();
    /// <summary>
    /// Sets the listener notified, allocation-free, on the thread that observes child exit.
    /// </summary>
    public void SetExitListener(IPtyExitListener listener);

    // Output transport. Only the session's PtyOutputPump reads it, through the output driver this backend starts.

    /// <summary>
    /// Starts reading output for <paramref name="pump"/> with this backend's <see cref="IPtyOutputDriver"/>. Called
    /// once, by the session constructor. Windows starts a <see cref="WindowsOutputDriver"/>, and Linux registers the master
    /// on the exit watcher's loop; neither holds a thread per session. macOS and FreeBSD, and every platform under the
    /// rollback switch (<see cref="PtyOutputDrivers"/>), run the pump on a <see cref="PtyReaderThread"/> over the backend's
    /// <see cref="IPtyBlockingOutput"/>.
    /// </summary>
    public void StartOutput(PtyOutputPump pump);
    /// <summary>
    /// Most bytes one output chunk hands a consumer. Platform backends return <see cref="PtyOutputPump.Capacity"/>, so
    /// a consumer that fell behind takes all contiguous output at once; a scripted backend returns
    /// <see cref="PtyOutputPump.PublishSize"/>, so that tests can hand its full-size reads out one at a time.
    /// </summary>
    public int OutputChunkLimit { get; }
    /// <summary>
    /// Reader silence, in milliseconds, that the post-exit drain takes to mean the transport has nothing more:
    /// counted after child exit before the transport is closed, and again after the close before output is ended
    /// without it. Platform backends return <see cref="PtyOutputPump.DefaultPostExitQuietMs"/>; a scripted backend
    /// whose reads are paced by sleeps lengthens it, so that a test's timing margin does not depend on how promptly
    /// a loaded host schedules its threads.
    /// </summary>
    public int PostExitQuietMs { get; }
    /// <summary>Gets whether the transport was closed, after which reads report end of output without blocking.</summary>
    public bool IsOutputClosed { get; }
    /// <summary>
    /// Closes the transport so the reader reaches end of output: the pseudo-console on Windows (output already
    /// produced still drains), the master on Unix (released once the reader and any in-flight call let go).
    /// Afterwards <see cref="Resize"/> is a no-op.
    /// </summary>
    public void CloseOutputTransport();
}
