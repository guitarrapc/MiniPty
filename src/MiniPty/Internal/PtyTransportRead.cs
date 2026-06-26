namespace MiniPty.Internal;

/// <summary>
/// Ungated PTY transport reads for one-shot completion pumps.
/// </summary>
internal static class PtyTransportRead
{
    /// <summary>
    /// Runs blocking transport pump work on a dedicated thread so the read loop starts immediately
    /// instead of waiting behind a saturated thread pool (fast macOS PTY children can finish before
    /// a queued thread-pool read begins, losing output while exit code is still 0).
    /// </summary>
    internal static Task<T> RunBlockingTransportPump<T>(
        Func<CancellationToken, T> work,
        CancellationToken cancellationToken) =>
        Task.Factory.StartNew(
            () => work(cancellationToken),
            cancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    internal static void SignalTransportPumpStarted(Stream stream)
    {
        switch (stream)
        {
            case PtyFdReadStream unix:
                unix.SignalTransportPumpStarted();
                break;
            case PtyHandleReadStream windows:
                windows.SignalTransportPumpStarted();
                break;
        }
    }

    internal enum TransportPumpReadStatus
    {
        Data,
        Retry,
        End,
    }

    /// <summary>
    /// Reads one transport pump chunk. macOS PTY masters can return a spurious zero-byte EOF while
    /// the child is still running; yield and retry until the session reports exit.
    /// </summary>
    internal static TransportPumpReadStatus ReadTransportPumpChunk(Stream stream, Span<byte> buffer, out int read)
    {
        read = Read(stream, buffer);
        if (read > 0)
            return TransportPumpReadStatus.Data;

        if (stream is PtyFdReadStream unix && !unix.IsChildExited)
        {
            Thread.Sleep(0);
            return TransportPumpReadStatus.Retry;
        }

        return TransportPumpReadStatus.End;
    }

    internal static int Read(Stream stream, Span<byte> buffer) =>
        stream switch
        {
            PtyHandleReadStream windows => windows.ReadTransport(buffer),
            PtyFdReadStream unix => unix.ReadTransport(buffer),
            _ => throw new InvalidOperationException("Unsupported PTY output transport.")
        };

    internal static bool IsTransport(Stream stream) =>
        stream is PtyHandleReadStream or PtyFdReadStream;
}
