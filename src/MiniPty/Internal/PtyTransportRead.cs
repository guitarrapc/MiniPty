namespace MiniPty.Internal;

/// <summary>
/// Ungated PTY transport reads for one-shot completion pumps.
/// </summary>
internal static class PtyTransportRead
{
    internal static int Read(Stream stream, Span<byte> buffer)
    {
        MarkTransportReadLoopEntered(stream);
        return stream switch
        {
            PtyHandleReadStream windows => windows.ReadTransport(buffer),
            PtyFdReadStream unix => unix.ReadTransport(buffer),
            _ => throw new InvalidOperationException("Unsupported PTY output transport.")
        };
    }

    internal static bool IsTransport(Stream stream) =>
        stream is PtyHandleReadStream or PtyFdReadStream;

    internal static bool IsReadLoopEntered(Stream stream) =>
        stream switch
        {
            PtyHandleReadStream windows => windows.TransportReadLoopEntered,
            PtyFdReadStream unix => unix.TransportReadLoopEntered,
            _ => false
        };

    private static void MarkTransportReadLoopEntered(Stream stream)
    {
        switch (stream)
        {
            case PtyHandleReadStream windows:
                windows.MarkTransportReadLoopEntered();
                break;
            case PtyFdReadStream unix:
                unix.MarkTransportReadLoopEntered();
                break;
        }
    }
}
