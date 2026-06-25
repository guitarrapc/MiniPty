namespace MiniPty.Internal;

internal interface IPtyBackend : IDisposable
{
    public Stream Input { get; }
    public Stream Output { get; }
    public int ProcessId { get; }
    public bool HasExited { get; }
    public int ExitCode { get; }
    public PtySize Size { get; }
    public void Resize(int columns, int rows);
    public void SendEof();
    public void Kill();
    public Task<int> WaitForExitAsync(CancellationToken cancellationToken, bool killOnCancellation, bool closeTransportOnExit = true);
    public void CloseOutputTransport();
}
