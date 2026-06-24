using System.Text;
using MiniPty.Internal;

namespace MiniPty.Capture;

internal static class PtyCapturePump
{
    private const int SustainedOutputInitialCapacity = 32 * 1024;

    private readonly record struct ByteChunkMeta(TimeSpan Time, int Start, int Length);

    private readonly record struct TextChunkMeta(TimeSpan Time, int Start, int Length);

    internal static async Task<PtyCapturePumpResult> ReadAsync(
        Stream stream,
        long originTimestamp,
        TimeProvider timeProvider,
        Encoding encoding,
        bool decodeOutput,
        CancellationToken cancellationToken)
    {
        // Typical PTY reads are few; large outputs may still produce many small reads (e.g. line-buffered children).
        var byteChunkMeta = new List<ByteChunkMeta>(capacity: 64);
        using var byteBuffer = new PtyGrowingBuffer<byte>();
        using var bytes = PtyReadBuffer.RentBytes();

        List<TextChunkMeta>? textChunkMeta = decodeOutput ? new List<TextChunkMeta>(capacity: 64) : null;
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
                byteChunkMeta.Add(new ByteChunkMeta(ElapsedSinceStart(originTimestamp, timeProvider), byteBuffer.Length, read));
                ReserveForSustainedOutput(byteBuffer, read, bytes.Memory.Length);
                byteBuffer.Append(slice);

                if (decodeOutput)
                    AppendTextChunk(originTimestamp, timeProvider, textChunkMeta!, charBuffer!, decoder!, slice, chars.Span, flush: false);
            }

            if (decodeOutput)
                AppendTextChunk(originTimestamp, timeProvider, textChunkMeta!, charBuffer!, decoder!, ReadOnlySpan<byte>.Empty, chars.Span, flush: true);

            var outputBytes = byteBuffer.Detach();
            var chunks = BuildByteChunks(outputBytes, byteChunkMeta);
            if (!decodeOutput)
                return new PtyCapturePumpResult(outputBytes, null, encoding, chunks, null);

            var outputChars = charBuffer!.Detach();
            var textChunks = BuildTextChunks(outputChars, textChunkMeta!);
            return new PtyCapturePumpResult(outputBytes, outputChars, encoding, chunks, textChunks);
        }
        finally
        {
            charBuffer?.Dispose();
        }
    }

    private static TimeSpan ElapsedSinceStart(long originTimestamp, TimeProvider timeProvider) =>
        timeProvider.GetElapsedTime(originTimestamp);

    private static void AppendTextChunk(
        long originTimestamp,
        TimeProvider timeProvider,
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

        textChunkMeta.Add(new TextChunkMeta(ElapsedSinceStart(originTimestamp, timeProvider), charBuffer.Length, charCount));
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

    private static void ReserveForSustainedOutput(PtyGrowingBuffer<byte> output, int read, int readBufferLength)
    {
        if (output.Length == 0 && read == readBufferLength)
            output.EnsureCapacity(SustainedOutputInitialCapacity);
    }
}
