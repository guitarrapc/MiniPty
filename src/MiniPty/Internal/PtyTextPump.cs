using System.Text;

namespace MiniPty.Internal;

internal static class PtyTextPump
{
    internal static async Task<string> ReadAllAsync(Stream stream, Encoding encoding, CancellationToken cancellationToken)
    {
        var bytes = new byte[4096];
        var chars = new char[encoding.GetMaxCharCount(bytes.Length)];
        var decoder = encoding.GetDecoder();
        var builder = new StringBuilder();

        while (true)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(0, bytes.Length), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
                break;

            AppendDecoded(decoder, bytes, read, chars, builder, flush: false);
        }

        AppendDecoded(decoder, [], 0, chars, builder, flush: true);
        return builder.ToString();
    }

    private static void AppendDecoded(
        Decoder decoder,
        byte[] bytes,
        int count,
        char[] chars,
        StringBuilder builder,
        bool flush)
    {
        var charCount = decoder.GetChars(bytes, 0, count, chars, 0, flush);
        if (charCount > 0)
            builder.Append(chars, 0, charCount);
    }
}
