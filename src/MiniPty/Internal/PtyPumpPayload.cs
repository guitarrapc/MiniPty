using System.Text;

namespace MiniPty.Internal;

/// <summary>
/// Raw pump output transferred from <see cref="PtyBytePump"/> to public result types.
/// </summary>
internal sealed class PtyPumpPayload(byte[] bytes, char[]? chars, Encoding encoding)
{
    internal byte[] Bytes { get; } = bytes;

    /// <summary>Non-null only when the pump decoded during I/O.</summary>
    internal char[]? Chars { get; } = chars;

    internal Encoding Encoding { get; } = encoding;
}
