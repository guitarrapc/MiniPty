using System.Diagnostics;
using System.Text;
using MiniPty.Internal;

namespace MiniPty.Recording;

/// <summary>Drains <see cref="PtySession.Output"/> into timestamped chunks while the session runs.</summary>
public sealed class PtyOutputRecorder
{
    private readonly PtySession _session;
    private readonly Stopwatch _stopwatch;
    private readonly Task<List<PtyChunk>> _pump;

    private PtyOutputRecorder(PtySession session, Stopwatch stopwatch, Task<List<PtyChunk>> pump)
    {
        _session = session;
        _stopwatch = stopwatch;
        _pump = pump;
    }

    /// <summary>Starts reading the session output stream on a background thread.</summary>
    public static PtyOutputRecorder Start(PtySession session, Encoding? outputEncoding = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        outputEncoding ??= Encoding.UTF8;
        var stopwatch = Stopwatch.StartNew();
        var pump = Task.Run(() => PtyChunkReader.Read(session.Output, stopwatch, outputEncoding));
        return new PtyOutputRecorder(session, stopwatch, pump);
    }

    /// <summary>Elapsed time since recording started.</summary>
    public TimeSpan Elapsed => _stopwatch.Elapsed;

    /// <summary>
    /// Waits for the read pump to finish after the child has exited.
    /// Call after <see cref="PtySession.WaitForExitAsync"/>.
    /// </summary>
    public Task<IReadOnlyList<PtyChunk>> CollectAsync(
        TimeSpan? drainTimeout = null,
        TimeSpan? closeGrace = null,
        CancellationToken cancellationToken = default)
    {
        drainTimeout ??= TimeSpan.FromSeconds(5);
        closeGrace ??= TimeSpan.FromSeconds(1);
        return PtyOutputDrain.AwaitPumpAsync(
            _pump,
            _session.CloseOutputTransport,
            drainTimeout.Value,
            closeGrace.Value,
            throwOnTimeout: true,
            transportAlreadyClosed: false,
            cancellationToken);
    }
}
