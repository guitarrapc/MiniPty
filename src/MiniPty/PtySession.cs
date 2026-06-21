using MiniPty.Internal;

namespace MiniPty;

/// <summary>
/// A running pseudo-terminal child session.
/// <see cref="Dispose"/> kills the child if it is still running, then releases handles.
/// </summary>
public sealed class PtySession : IAsyncDisposable, IDisposable
{
    private readonly IPtyBackend _backend;
    private bool _disposed;

    internal PtySession(IPtyBackend backend) => _backend = backend;

    /// <summary>Write-only stream to the child stdin (PTY input).</summary>
    public Stream Input => _backend.Input;

    /// <summary>Read-only stream from the child stdout/stderr (PTY output).</summary>
    public Stream Output => _backend.Output;

    public int ProcessId => _backend.ProcessId;

    /// <summary>Polls the OS for child exit (<c>WaitForSingleObject(0)</c> / <c>waitpid(WNOHANG)</c>).</summary>
    public bool HasExited => _backend.HasExited;

    /// <summary>Exit code after <see cref="HasExited"/> is true.</summary>
    public int ExitCode => _backend.ExitCode;

    public PtySize Size => _backend.Size;

    public void Resize(int columns, int rows) => _backend.Resize(columns, rows);

    /// <summary>
    /// Signals end of stdin. Windows defers pipe close until wait; Unix writes EOT (staged).
    /// </summary>
    public void SignalEof() => _backend.SignalEof();

    public void Kill() => _backend.Kill();

    /// <summary>Cancellation stops waiting only; the child keeps running.</summary>
    public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default) =>
        _backend.WaitForExitAsync(cancellationToken, killOnCancellation: false);

    /// <summary>Cancellation calls <see cref="Kill"/> then throws <see cref="OperationCanceledException"/>.</summary>
    public Task<int> WaitForExitOrKillAsync(CancellationToken cancellationToken = default) =>
        _backend.WaitForExitAsync(cancellationToken, killOnCancellation: true);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _backend.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    internal void CloseOutputTransport() => _backend.CloseOutputTransport();
}
