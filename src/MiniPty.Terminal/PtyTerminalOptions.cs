namespace MiniPty.Terminal;

/// <summary>
/// Receives one PTY output chunk pushed by <see cref="PtyTerminal"/>.
/// </summary>
/// <remarks>
/// <para>
/// <paramref name="data"/> is valid only until the returned <see cref="ValueTask"/> completes;
/// handlers that retain bytes must copy them. The terminal does not read further output until the
/// returned task completes, so a slow handler applies backpressure all the way to the child
/// process (strict handoff, then OS PTY pipe fill) without copying or dropping data.
/// </para>
/// <para>
/// Handlers must honor <paramref name="cancellationToken"/>; it is canceled when the terminal is
/// disposed. A handler exception stops the pump, kills the child, and faults
/// <see cref="PtyTerminal.Completion"/> with that exception.
/// </para>
/// </remarks>
/// <param name="data">Output bytes. Ephemeral; valid until the returned task completes.</param>
/// <param name="cancellationToken">Canceled when the terminal is disposed.</param>
public delegate ValueTask PtyTerminalOutputHandler(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

/// <summary>
/// Options for <see cref="PtyTerminal.Start"/>.
/// </summary>
public sealed class PtyTerminalOptions
{
    /// <summary>
    /// Gets the handler that receives PTY output. Required; supplying it at start eliminates any
    /// window where early child output could be produced with no consumer attached.
    /// </summary>
    public required PtyTerminalOutputHandler Output { get; init; }
}
