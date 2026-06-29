namespace MiniPty.Console;

/// <summary>
/// Active host-terminal attach for a <see cref="PtySession"/>.
/// </summary>
public sealed class PtyConsoleInputHandle : IDisposable
{
    private readonly Internal.PtyConsoleAttach _attach;
    private int _disposed;

    internal PtyConsoleInputHandle(Internal.PtyConsoleAttach attach) => _attach = attach;

    /// <summary>
    /// Reserved no-op in v1. Input is forwarded by the background pump started by <see cref="PtyConsoleInput.Attach"/>.
    /// </summary>
    public void PumpInputOnce(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _attach.PumpInputOnce(cancellationToken);
    }

    /// <summary>
    /// Blocks until <paramref name="cancellationToken"/> is canceled.
    /// </summary>
    /// <remarks>
    /// A background input pump runs while attached. Typical embedders link this token to
    /// <see cref="PtySession.WaitForExitAsync"/> completion.
    /// </remarks>
    public void PumpInputUntil(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _attach.PumpInputUntil(cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _attach.Dispose();
    }
}
