namespace MiniPty.Terminal;

/// <summary>
/// Receives one PTY output chunk pushed by <see cref="PtyTerminal"/>.
/// </summary>
/// <remarks>
/// <para>
/// <paramref name="data"/> is valid only until the returned <see cref="ValueTask"/> completes;
/// handlers that retain bytes must copy them. The terminal does not read further output until the
/// returned task completes, so a slow handler applies backpressure all the way to the child
/// process (the session's bounded output buffer, then OS PTY pipe fill) without copying or dropping data.
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

    /// <summary>
    /// Gets whether output that arrives in a burst is joined into larger chunks, waiting at most about a millisecond per
    /// chunk, while output outside a burst, such as a keystroke echo, is pushed at once. Set by the bridges, which send one
    /// message per chunk; see <c>PtySession.ReadOutputCoalescedAsync</c>.
    /// </summary>
    internal bool CoalesceBursts { get; init; }
}
