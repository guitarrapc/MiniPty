namespace MiniPty.Terminal;

/// <summary>
/// Options for <see cref="PtyWebSocketBridge.RunAsync"/>.
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

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(LowWatermark, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(HighWatermark, LowWatermark);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ReceiveBufferSize, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MaxControlMessageSize, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(CloseTimeout, TimeSpan.Zero);
    }
}
