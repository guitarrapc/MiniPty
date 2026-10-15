namespace MiniPty.Internal;

/// <summary>
/// Thin internal runtime contract for an active PTY child. Platform backends implement this after
/// <c>WindowsPtyBackend.Start</c> or <c>UnixPtyBackend.Start</c>; <see cref="PtySession"/> is the sole
/// public coordinator. Not a launch plugin point—keep members mapped to session operations only.
/// </summary>
internal interface IPtyBackend : IDisposable
{
    public Stream Input { get; }
    public Stream Output { get; }
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
    /// <summary>
    /// Windows closes the ConPTY transport once exit has been observed, because ConPTY output does not
    /// reach EOF until the pseudo-console is closed. Unix does nothing: the PTY reports EOF on its own.
    /// </summary>
    public void CloseTransportAfterExit();
    public void CloseOutputTransport();
}
