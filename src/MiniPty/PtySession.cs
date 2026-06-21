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

    /// <summary>Operating-system process identifier of the child.</summary>
    public int ProcessId => _backend.ProcessId;

    /// <summary>Polls the OS for child exit (<c>WaitForSingleObject(0)</c> / <c>waitpid(WNOHANG)</c>).</summary>
    public bool HasExited => _backend.HasExited;

    /// <summary>Exit code after <see cref="HasExited"/> is <see langword="true"/>.</summary>
    public int ExitCode => _backend.ExitCode;

    /// <summary>Current terminal dimensions.</summary>
    public PtySize Size => _backend.Size;

    /// <summary>Resizes the pseudo-terminal.</summary>
    /// <param name="columns">Width in character cells.</param>
    /// <param name="rows">Height in character cells.</param>
    public void Resize(int columns, int rows) => _backend.Resize(columns, rows);

    /// <summary>
    /// Signals end of stdin. Windows defers pipe close until wait; Unix writes EOT (staged).
    /// </summary>
    public void SignalEof() => _backend.SignalEof();

    /// <summary>Terminates the child process.</summary>
    public void Kill() => _backend.Kill();

    /// <summary>Cancellation stops waiting only; the child keeps running.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The child exit code.</returns>
    public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default) =>
        _backend.WaitForExitAsync(cancellationToken, killOnCancellation: false);

    /// <summary>Cancellation calls <see cref="Kill"/> then throws <see cref="OperationCanceledException"/>.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The child exit code.</returns>
    public Task<int> WaitForExitOrKillAsync(CancellationToken cancellationToken = default) =>
        _backend.WaitForExitAsync(cancellationToken, killOnCancellation: true);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _backend.Dispose();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    internal void CloseOutputTransport() => _backend.CloseOutputTransport();
}
