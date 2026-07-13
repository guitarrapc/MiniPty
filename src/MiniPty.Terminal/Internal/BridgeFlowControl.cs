namespace MiniPty.Terminal.Internal;

/// <summary>
/// Watermark flow control for the WebSocket bridge: counts bytes sent to the client but not yet
/// acknowledged, pausing the terminal above the high watermark and resuming at or below the low
/// watermark. Transitions are guarded by a lock so a racing ACK cannot lose a resume; the
/// worst-case race cost is one extra in-flight chunk, which stays well under the xterm.js
/// client-buffer guidance.
/// </summary>
internal sealed class BridgeFlowControl
{
    private readonly long _highWatermark;
    private readonly long _lowWatermark;
    private readonly Lock _lock = new();
    private long _unacknowledged;
    private bool _paused;
    private bool _disabled;
    private PtyTerminal? _terminal;

    public BridgeFlowControl(long highWatermark, long lowWatermark)
    {
        _highWatermark = highWatermark;
        _lowWatermark = lowWatermark;
    }

    /// <summary>
    /// Attaches the terminal and applies a pause that was requested before attachment.
    /// </summary>
    public void Attach(PtyTerminal terminal)
    {
        lock (_lock)
        {
            _terminal = terminal;
            if (_paused && !_disabled)
                terminal.Pause();
        }
    }

    public void OnSent(int bytes)
    {
        lock (_lock)
        {
            if (_disabled || bytes <= 0)
                return;

            _unacknowledged = AddSaturating(_unacknowledged, bytes);
            if (!_paused && _unacknowledged >= _highWatermark)
            {
                _paused = true;
                _terminal?.Pause();
            }
        }
    }

    /// <summary>Adds a positive frame length without allowing the credit counter to wrap negative.</summary>
    internal static long AddSaturating(long current, int bytes) =>
        current > long.MaxValue - bytes ? long.MaxValue : current + bytes;

    /// <summary>
    /// Permanently stops flow control and releases any pause, used during bridge teardown so the
    /// post-kill drain cannot park on a pause the client will never acknowledge. Running inside
    /// the same lock as <see cref="OnSent"/> makes disable-then-pause races impossible.
    /// </summary>
    public void Disable()
    {
        lock (_lock)
        {
            _disabled = true;
            if (_paused)
            {
                _paused = false;
                _terminal?.Resume();
            }
        }
    }

    public void OnAcknowledged(long bytes)
    {
        if (bytes <= 0)
            return;

        lock (_lock)
        {
            if (_disabled)
                return;

            _unacknowledged = Math.Max(0, _unacknowledged - bytes);
            if (_paused && _unacknowledged <= _lowWatermark)
            {
                _paused = false;
                _terminal?.Resume();
            }
        }
    }
}
