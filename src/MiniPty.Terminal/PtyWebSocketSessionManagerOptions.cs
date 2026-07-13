namespace MiniPty.Terminal;

/// <summary>
/// Options for <see cref="PtyWebSocketSessionManager"/>.
/// </summary>
public sealed record PtyWebSocketSessionManagerOptions
{
    /// <summary>Gets how long a session may remain detached before it is killed and removed.</summary>
    public TimeSpan DetachedSessionTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets how often detached sessions are checked for expiration.</summary>
    public TimeSpan ExpirationScanInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets the maximum number of live sessions owned by one manager.</summary>
    public int MaxSessions { get; init; } = 64;

    /// <summary>
    /// Gets the fixed replay-buffer capacity allocated per session. Output backpressures the PTY
    /// when this buffer fills while detached. Default is 512 KiB.
    /// </summary>
    public int ReplayBufferSize { get; init; } = 512 * 1024;

    /// <summary>Gets the maximum raw output payload sent in one WebSocket message.</summary>
    public int MaxOutputFrameSize { get; init; } = 64 * 1024;

    /// <summary>Gets the underlying WebSocket framing and control limits.</summary>
    public PtyBridgeOptions BridgeOptions { get; init; } = new();

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(DetachedSessionTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ExpirationScanInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MaxSessions, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(ReplayBufferSize, 64 * 1024);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MaxOutputFrameSize, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxOutputFrameSize, ReplayBufferSize);
        ArgumentNullException.ThrowIfNull(BridgeOptions);
        BridgeOptions.Validate();
    }
}
