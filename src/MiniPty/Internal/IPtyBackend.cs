namespace MiniPty.Internal;

/// <summary>
/// Thin internal runtime contract for an active PTY child. Platform backends implement this after
/// <c>WindowsPtyBackend.Start</c> or <c>UnixPtyBackend.Start</c>; <see cref="PtySession"/> is the sole
/// public coordinator. Not a launch plugin point—keep members mapped to session operations only.
/// </summary>
internal interface IPtyBackend : IDisposable
{
    public Stream Input { get; }
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

    // Output transport. Only the session's PtyOutputPump reads it, on its reader thread.

    /// <summary>
    /// Pins the transport for the reader. Returns <see langword="false"/> when it is already closed, which the
    /// reader treats as end of output. Paired with <see cref="EndOutputRead"/>.
    /// </summary>
    public bool TryBeginOutputRead();
    public void EndOutputRead();
    /// <summary>
    /// Blocks until output arrives. Returns the byte count, 0 at end of output, or -1 when
    /// <paramref name="reader"/> was woken first (Unix), so the pump re-checks its state.
    /// </summary>
    public int ReadOutput(Span<byte> buffer, PtyReaderThread reader);
    /// <summary>
    /// Reads output that is available now or arrives within a short micro-window, for appending to the slot
    /// the last read filled. Returns 0 when none arrived; <paramref name="endOfOutput"/> reports end of output.
    /// </summary>
    public int ReadMoreOutput(Span<byte> buffer, PtyReaderThread reader, out bool endOfOutput);
    /// <summary>
    /// Longest time, in <see cref="System.Diagnostics.Stopwatch"/> ticks, that one slot keeps appending
    /// <see cref="ReadMoreOutput"/> results after its first read; 0 or less appends until the slot fills or a
    /// micro-window passes with no output.
    /// </summary>
    public long CoalesceBudgetTicks { get; }
    /// <summary>Gets whether the transport was closed, after which reads report end of output without blocking.</summary>
    public bool IsOutputClosed { get; }
    /// <summary>
    /// Closes the transport so the reader reaches end of output: the pseudo-console on Windows (output already
    /// produced still drains), the master on Unix (released once the reader and any in-flight call let go).
    /// Afterwards <see cref="Resize"/> is a no-op.
    /// </summary>
    public void CloseOutputTransport();
}
