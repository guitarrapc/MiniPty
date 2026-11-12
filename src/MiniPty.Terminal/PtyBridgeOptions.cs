namespace MiniPty.Terminal;

/// <summary>
/// Options for terminal WebSocket and stdio bridges, including persistent WebSocket connections.
/// </summary>
/// <remarks>
/// Flow control follows the xterm.js watermark/ACK guidance: the server counts bytes sent but not
/// yet acknowledged by the client; above <see cref="HighWatermark"/> output delivery pauses (which
/// ultimately blocks the child via PTY backpressure), and an <c>ack</c> control message that brings
/// the count to <see cref="LowWatermark"/> or below resumes delivery. Byte-counted credit is
/// self-clocking: a lost resume message cannot deadlock the stream.
/// </remarks>
public sealed record PtyBridgeOptions
{
    /// <summary>
    /// Gets the unacknowledged-byte count above which output delivery pauses.
    /// Default is 384 KiB; keep well under the ~500 KB xterm.js write-buffer guidance.
    /// </summary>
    public long HighWatermark { get; init; } = 3 * 131_072;

    /// <summary>
    /// Gets the unacknowledged-byte count at or below which output delivery resumes.
    /// Zero resumes only after every outstanding byte is acknowledged. Default is 128 KiB (2^17),
    /// matching the recommended client ACK chunk size.
    /// </summary>
    public long LowWatermark { get; init; } = 131_072;

    /// <summary>
    /// Gets the receive buffer size in bytes for client input and control frames. Default is 16 KiB.
    /// </summary>
    public int ReceiveBufferSize { get; init; } = 16 * 1024;

    /// <summary>
    /// Gets the maximum accepted control (text) message size in bytes. Larger messages close the
    /// socket with <c>PolicyViolation</c>. Default is 4 KiB.
    /// </summary>
    public int MaxControlMessageSize { get; init; } = 4096;

    /// <summary>
    /// Gets a value indicating whether an <c>exit</c> control message is sent after the final
    /// output frame when the child exits. Default is <see langword="true"/>.
    /// </summary>
    public bool SendExitMessage { get; init; } = true;

    /// <summary>
    /// Gets the maximum time to wait for the close handshake against a slow or dead client.
    /// Default is 5 seconds.
    /// </summary>
    public TimeSpan CloseTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets a value indicating whether the bridge drops the client's input instead of writing it to the child.
    /// Default is <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Use it to show a program to someone who must not type into it, as ttyd does by default. The client can still
    /// resize the terminal, acknowledge output, and close the connection, and it receives output and the <c>exit</c>
    /// message as usual. Closing the connection still ends the child on <see cref="PtyWebSocketBridge"/> and
    /// <see cref="PtyStdioBridge"/>.
    /// </para>
    /// <para>
    /// Input frames are read and discarded, not refused, because a terminal emulator sends some input without a
    /// keystroke, such as focus and mouse reports. Its answers to the child's queries, such as the cursor position or
    /// the device attributes, are input too and are dropped, so a child that waits for an answer waits until its own
    /// timeout. The frontend should also stop taking input (xterm.js <c>disableStdin</c>), so that the user sees that
    /// the terminal does not take keystrokes.
    /// </para>
    /// <para>
    /// A start with <see cref="PtyStartInfo.InheritCursor"/> is refused with <see cref="ArgumentException"/> on every
    /// platform: on Windows, ConPTY holds the child until the frontend answers its cursor position query, and that
    /// answer would be dropped. On <see cref="PtyWebSocketSessionManager"/>, the option applies to every session the
    /// manager owns.
    /// </para>
    /// </remarks>
    public bool ReadOnly { get; init; }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(LowWatermark, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(HighWatermark, LowWatermark);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ReceiveBufferSize, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MaxControlMessageSize, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(CloseTimeout, TimeSpan.Zero);
    }

    /// <summary>Refuses a start the bridge cannot serve with these options.</summary>
    /// <exception cref="ArgumentException"><see cref="ReadOnly"/> is set and the start inherits the cursor.</exception>
    internal void ThrowIfCannotServe(PtyStartInfo startInfo)
    {
        if (ReadOnly && startInfo.InheritCursor)
        {
            throw new ArgumentException(
                "A read-only bridge cannot start a session that inherits the cursor: the frontend's answer to the cursor position query is input, which the bridge drops, and on Windows the child waits for that answer.",
                nameof(startInfo));
        }
    }
}
