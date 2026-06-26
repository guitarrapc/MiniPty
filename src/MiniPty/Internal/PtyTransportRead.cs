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
    internal static Task<T> RunBlockingTransportPump<T>(Func<T> work, CancellationToken cancellationToken) =>
        Task.Factory.StartNew(work, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

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
