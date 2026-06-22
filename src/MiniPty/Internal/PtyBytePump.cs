using System.Text;

namespace MiniPty.Internal;

internal static class PtyBytePump
{
    internal static async Task<PtyPumpOutput> ReadAllAsync(
        Stream stream,
        Encoding encoding,
        bool decodeOutput,
        CancellationToken cancellationToken)
    {
        using var byteBuffer = new PtyGrowingBuffer<byte>();
        using var bytes = PtyReadBuffer.RentBytes();

        if (!decodeOutput)
        {
            while (true)
            {
                var read = await stream.ReadAsync(bytes.Memory, cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                    break;

                byteBuffer.Append(bytes.Span[..read]);
            }

            return new PtyPumpOutput(byteBuffer.ToArray(), null, encoding);
        }

        using var charBuffer = new PtyGrowingBuffer<char>();
        using var chars = PtyReadBuffer.RentChars(encoding);
        var decoder = encoding.GetDecoder();

        while (true)
        {
            var read = await stream.ReadAsync(bytes.Memory, cancellationToken).ConfigureAwait(false);
            if (read <= 0)
                break;

            byteBuffer.Append(bytes.Span[..read]);
            AppendDecoded(decoder, bytes.Span[..read], chars.Span, charBuffer);
        }

        AppendDecoded(decoder, ReadOnlySpan<byte>.Empty, chars.Span, charBuffer, flush: true);
        return new PtyPumpOutput(byteBuffer.ToArray(), charBuffer.ToArray(), encoding);
    }

    private static void AppendDecoded(
        Decoder decoder,
        ReadOnlySpan<byte> bytes,
        Span<char> chars,
        PtyGrowingBuffer<char> output,
        bool flush = false)
    {
        var charCount = decoder.GetChars(bytes, chars, flush);
        if (charCount > 0)
            output.Append(chars[..charCount]);
    }
}
