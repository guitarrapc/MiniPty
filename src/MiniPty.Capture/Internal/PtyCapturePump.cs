using System.Diagnostics;
using System.Text;
using MiniPty.Internal;

namespace MiniPty.Capture;

internal static class PtyCapturePump
{
    private readonly record struct ChunkMeta(TimeSpan Time, int Start, int Length);

    internal static async Task<PtyCapturePumpResult> ReadAsync(
        Stream stream,
        Stopwatch origin,
        Encoding encoding,
        CancellationToken cancellationToken)
    {
        var chunkMeta = new List<ChunkMeta>();
        var builder = new StringBuilder();
        using var bytes = PtyReadBuffer.RentBytes();
        using var chars = PtyReadBuffer.RentChars(encoding);
        var decoder = encoding.GetDecoder();

        while (true)
        {
            var read = await stream.ReadAsync(bytes.Memory, cancellationToken).ConfigureAwait(false);
            if (read <= 0)
                break;

            AppendChunk(origin, chunkMeta, builder, decoder, bytes.Span[..read], chars.Span, flush: false);
        }

        AppendChunk(origin, chunkMeta, builder, decoder, ReadOnlySpan<byte>.Empty, chars.Span, flush: true);

        var output = builder.ToString();
        var chunks = new PtyCaptureChunk[chunkMeta.Count];
        for (var i = 0; i < chunkMeta.Count; i++)
        {
            var meta = chunkMeta[i];
            chunks[i] = new PtyCaptureChunk(meta.Time, output.AsMemory(meta.Start, meta.Length));
        }

        return new PtyCapturePumpResult(output, chunks);
    }

    private static void AppendChunk(
        Stopwatch origin,
        List<ChunkMeta> chunkMeta,
        StringBuilder builder,
        Decoder decoder,
        ReadOnlySpan<byte> bytes,
        Span<char> chars,
        bool flush)
    {
        var charCount = decoder.GetChars(bytes, chars, flush);
        if (charCount <= 0)
            return;

        chunkMeta.Add(new ChunkMeta(origin.Elapsed, builder.Length, charCount));
        builder.Append(chars[..charCount]);
    }
}
