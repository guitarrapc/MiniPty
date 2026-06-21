using System.Diagnostics;
using System.Text;
using MiniPty.Internal;

namespace MiniPty;

/// <summary>Drains <see cref="PtySession.Output"/> into timestamped chunks while the session runs.</summary>
public sealed class PtyOutputRecorder
{
    private readonly PtySession _session;
    private readonly Task<List<PtyOutputChunk>> _pump;

    private PtyOutputRecorder(PtySession session, Task<List<PtyOutputChunk>> pump)
    {
        _session = session;
        _pump = pump;
    }

    /// <summary>Starts background output capture for <paramref name="session"/>.</summary>
    /// <param name="session">An active PTY session.</param>
    /// <param name="outputEncoding">Encoding used to decode PTY bytes (default UTF-8).</param>
    /// <returns>A recorder that collects chunks via <see cref="CollectAsync"/>.</returns>
    public static PtyOutputRecorder Start(PtySession session, Encoding? outputEncoding = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        outputEncoding ??= Encoding.UTF8;
        var stopwatch = Stopwatch.StartNew();
        var pump = Task.Run(() => PtyChunkReader.Read(session.Output, stopwatch, outputEncoding));
        return new PtyOutputRecorder(session, pump);
    }

    /// <summary>Waits for the output pump to finish and returns captured chunks.</summary>
    /// <param name="drainTimeout">Maximum wait after the child exits.</param>
    /// <param name="closeGrace">Grace period after closing the output transport.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<IReadOnlyList<PtyOutputChunk>> CollectAsync(
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
