namespace MiniPty.Console;

/// <summary>
/// Active host-terminal attach for a <see cref="PtySession"/>.
/// </summary>
public sealed class PtyConsoleInputHandle : IDisposable
{
    private readonly Internal.PtyConsoleAttach _attach;

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
    public void PumpInputOnce(CancellationToken cancellationToken = default) =>
        _attach.PumpInputOnce(cancellationToken);

    /// <inheritdoc />
    public void Dispose() => _attach.Dispose();
}
