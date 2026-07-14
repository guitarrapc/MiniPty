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
    public Task<int> WaitForExitAsync(CancellationToken cancellationToken, bool killOnCancellation, bool closeTransportOnExit = true);
    /// <summary>
    /// Blocks until the child exits or <paramref name="cancellationToken"/> is canceled; allocation-free path for <see cref="PtySession.ReadOutputAsync"/> exit observation.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel the wait.</param>
    /// <param name="closeTransportOnExit">
    /// When <see langword="true"/>, Windows closes the ConPTY transport after exit is observed.
    /// Unix ignores this flag; PTY reads EOF naturally and callers close the transport when draining.
    /// </param>
    public void PollForChildExitUntilExited(CancellationToken cancellationToken, bool closeTransportOnExit);
    public void CloseOutputTransport();
}
