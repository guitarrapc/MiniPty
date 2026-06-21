using System.Diagnostics;
using System.Text;
using MiniPty.Internal;

namespace MiniPty.Capture;

internal static class PtyCapturePump
{
    internal static async Task<IReadOnlyList<PtyCaptureChunk>> ReadAsync(
        Stream stream,
        Stopwatch origin,
        Encoding encoding,
        CancellationToken cancellationToken)
    {
        var chunks = new List<PtyCaptureChunk>();
        var bytes = new byte[4096];
        var chars = new char[encoding.GetMaxCharCount(bytes.Length)];
        var decoder = encoding.GetDecoder();

        while (true)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(0, bytes.Length), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
                break;

            var charCount = decoder.GetChars(bytes, 0, read, chars, 0, flush: false);
            if (charCount > 0)
                chunks.Add(new PtyCaptureChunk(origin.Elapsed, new string(chars, 0, charCount)));
        }

        var trailing = decoder.GetChars(Array.Empty<byte>(), 0, 0, chars, 0, flush: true);
        if (trailing > 0)
            chunks.Add(new PtyCaptureChunk(origin.Elapsed, new string(chars, 0, trailing)));

        return chunks;
    }
}
