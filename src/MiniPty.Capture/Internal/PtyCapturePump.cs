using System.Diagnostics;
using System.Text;
using MiniPty.Internal;

namespace MiniPty.Capture;

internal static class PtyCapturePump
{
    private readonly record struct ByteChunkMeta(TimeSpan Time, int Start, int Length);

    private readonly record struct TextChunkMeta(TimeSpan Time, int Start, int Length);

    internal static async Task<PtyCapturePumpResult> ReadAsync(
        Stream stream,
        Stopwatch origin,
        Encoding encoding,
        bool decodeOutput,
        CancellationToken cancellationToken)
    {
        var byteChunkMeta = new List<ByteChunkMeta>();
        using var byteBuffer = new PtyGrowingBuffer<byte>();
        using var bytes = PtyReadBuffer.RentBytes();

        List<TextChunkMeta>? textChunkMeta = decodeOutput ? [] : null;
        PtyGrowingBuffer<char>? charBuffer = decodeOutput ? new PtyGrowingBuffer<char>() : null;
        using var chars = decodeOutput ? PtyReadBuffer.RentChars(encoding) : default;
        var decoder = decodeOutput ? encoding.GetDecoder() : null;

        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(bytes.Memory, cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                    break;

                var slice = bytes.Span[..read];
                byteChunkMeta.Add(new ByteChunkMeta(origin.Elapsed, byteBuffer.Length, read));
                byteBuffer.Append(slice);

                if (decodeOutput)
                    AppendTextChunk(origin, textChunkMeta!, charBuffer!, decoder!, slice, chars.Span, flush: false);
            }

            if (decodeOutput)
                AppendTextChunk(origin, textChunkMeta!, charBuffer!, decoder!, ReadOnlySpan<byte>.Empty, chars.Span, flush: true);

            var outputBytes = byteBuffer.ToArray();
            var chunks = BuildByteChunks(outputBytes, byteChunkMeta);
            if (!decodeOutput)
                return new PtyCapturePumpResult(outputBytes, null, encoding, chunks, null);

            var outputChars = charBuffer!.ToArray();
            var textChunks = BuildTextChunks(outputChars, textChunkMeta!);
            return new PtyCapturePumpResult(outputBytes, outputChars, encoding, chunks, textChunks);
        }
        finally
        {
            charBuffer?.Dispose();
        }
    }

    private static void AppendTextChunk(
        Stopwatch origin,
        List<TextChunkMeta> textChunkMeta,
        PtyGrowingBuffer<char> charBuffer,
        Decoder decoder,
        ReadOnlySpan<byte> bytes,
        Span<char> chars,
        bool flush)
    {
        var charCount = decoder.GetChars(bytes, chars, flush);
        if (charCount <= 0)
            return;

        textChunkMeta.Add(new TextChunkMeta(origin.Elapsed, charBuffer.Length, charCount));
        charBuffer.Append(chars[..charCount]);
    }

    private static PtyCaptureChunk[] BuildByteChunks(byte[] outputBytes, List<ByteChunkMeta> meta)
    {
        var chunks = new PtyCaptureChunk[meta.Count];
        for (var i = 0; i < meta.Count; i++)
        {
            var item = meta[i];
            chunks[i] = new PtyCaptureChunk(item.Time, outputBytes.AsMemory(item.Start, item.Length));
        }

        return chunks;
    }

    private static PtyCaptureTextChunk[] BuildTextChunks(char[] outputChars, List<TextChunkMeta> meta)
    {
        var chunks = new PtyCaptureTextChunk[meta.Count];
        for (var i = 0; i < meta.Count; i++)
        {
            var item = meta[i];
            chunks[i] = new PtyCaptureTextChunk(item.Time, outputChars.AsMemory(item.Start, item.Length));
        }

        return chunks;
    }
}
