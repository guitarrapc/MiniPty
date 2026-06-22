using System.Text;

namespace MiniPty.Internal;

internal static class PtyTextPump
{
    internal static Task<PtyPumpOutput> ReadAllAsync(
        Stream stream,
        Encoding encoding,
        CancellationToken cancellationToken) =>
        PtyBytePump.ReadAllAsync(stream, encoding, decodeOutput: true, cancellationToken);
}
