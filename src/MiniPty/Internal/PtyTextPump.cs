using System.Text;

namespace MiniPty.Internal;

internal static class PtyTextPump
{
    internal static async Task<string> ReadAllAsync(Stream stream, Encoding encoding, CancellationToken cancellationToken)
    {
        using var bytes = PtyReadBuffer.RentBytes();
        using var chars = PtyReadBuffer.RentChars(encoding);
        var decoder = encoding.GetDecoder();
        var builder = new StringBuilder();

        while (true)
        {
            var read = await stream.ReadAsync(bytes.Memory, cancellationToken).ConfigureAwait(false);
            if (read <= 0)
                break;

            AppendDecoded(decoder, bytes.Span[..read], chars.Span, builder, flush: false);
        }

        AppendDecoded(decoder, ReadOnlySpan<byte>.Empty, chars.Span, builder, flush: true);
        return builder.ToString();
    }

    private static void AppendDecoded(
        Decoder decoder,
        ReadOnlySpan<byte> bytes,
        Span<char> chars,
        StringBuilder builder,
        bool flush)
    {
        var charCount = decoder.GetChars(bytes, chars, flush);
        if (charCount > 0)
            builder.Append(chars[..charCount]);
    }
}
