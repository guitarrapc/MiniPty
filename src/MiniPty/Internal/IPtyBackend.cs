namespace MiniPty.Internal;

internal interface IPtyBackend : IDisposable
{
    Stream Input { get; }
    Stream Output { get; }
    int ProcessId { get; }
    bool HasExited { get; }
    int ExitCode { get; }
    PtySize Size { get; }
    void Resize(int columns, int rows);
    void SignalEof();
    void Kill();
    Task<int> WaitForExitAsync(CancellationToken cancellationToken, bool killOnCancellation);
    void CloseOutputTransport();
}
