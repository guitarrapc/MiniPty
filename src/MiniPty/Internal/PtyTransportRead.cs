namespace MiniPty.Internal;

/// <summary>
/// Ungated PTY transport reads for one-shot completion pumps.
/// </summary>
internal static class PtyTransportRead
{
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
