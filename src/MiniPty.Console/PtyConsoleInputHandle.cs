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
    /// Reads host input once and forwards it to the PTY.
    /// </summary>
    /// <remarks>
    /// <para>On Windows, call from the same thread that invoked <see cref="PtyConsoleInput.Attach"/>.
    /// The call blocks until input arrives or <paramref name="cancellationToken"/> is canceled.</para>
    /// <para>On Unix, input is forwarded by a background pump started by <see cref="PtyConsoleInput.Attach"/>;
    /// this method is a no-op.</para>
    /// </remarks>
    public void PumpInputOnce(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _attach.PumpInputOnce(cancellationToken);
    }

    /// <summary>
    /// Blocks until <paramref name="cancellationToken"/> is canceled.
    /// </summary>
    /// <remarks>
    /// <para>On Windows, repeatedly calls <see cref="PumpInputOnce"/> on the attach thread.</para>
    /// <para>On Unix, waits for cancellation while the background input pump runs.</para>
    /// <para>Typical embedders link this token to <see cref="PtySession.WaitForExitAsync"/> completion.</para>
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
