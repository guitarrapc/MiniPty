using System.Buffers;
using System.Text;
using MiniPty.Internal;

namespace MiniPty.Capture;

internal static class PtyCapturePump
{
    private const int SustainedOutputInitialCapacity = 32 * 1024;

    private readonly record struct ByteChunkMeta(TimeSpan Time, int Start, int Length);

    private readonly record struct TextChunkMeta(TimeSpan Time, int Start, int Length);

    internal static Task<PtyCapturePumpResult> ReadAsync(
        Stream stream,
        long originTimestamp,
        TimeProvider timeProvider,
        Encoding encoding,
        bool decodeOutput,
        CancellationToken cancellationToken)
    {
        if (PtyTransportRead.IsTransport(stream))
        {
            return PtyTransportRead.RunBlockingTransportPump(
                () => ReadTransport(stream, originTimestamp, timeProvider, encoding, decodeOutput, cancellationToken),
                cancellationToken);
        }

        return ReadCoreAsync(stream, originTimestamp, timeProvider, encoding, decodeOutput, cancellationToken);
    }

    private static PtyCapturePumpResult ReadTransport(
        Stream stream,
        long originTimestamp,
        TimeProvider timeProvider,
        Encoding encoding,
        bool decodeOutput,
        CancellationToken cancellationToken)
    {
        var byteChunkMeta = new List<ByteChunkMeta>(capacity: 64);
        var byteAccumulator = new CaptureByteAccumulator();
        using var bytes = PtyReadBuffer.RentBytes();

        List<TextChunkMeta>? textChunkMeta = decodeOutput ? new List<TextChunkMeta>(capacity: 64) : null;
        PtyGrowingBuffer<char>? charBuffer = decodeOutput ? new PtyGrowingBuffer<char>() : null;
        using var chars = decodeOutput ? PtyReadBuffer.RentChars(encoding) : default;
        var decoder = decodeOutput ? encoding.GetDecoder() : null;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = PtyTransportRead.Read(stream, bytes.Span);
                if (read <= 0)
                    break;

                var slice = bytes.Span[..read];
                byteChunkMeta.Add(new ByteChunkMeta(ElapsedSinceStart(originTimestamp, timeProvider), byteAccumulator.Length, read));
                byteAccumulator.ReserveForSustainedOutput(read);
                byteAccumulator.Append(slice);

                if (decodeOutput)
                    AppendTextChunk(originTimestamp, timeProvider, textChunkMeta!, charBuffer!, decoder!, slice, chars.Span, flush: false);
            }

            if (decodeOutput)
                AppendTextChunk(originTimestamp, timeProvider, textChunkMeta!, charBuffer!, decoder!, ReadOnlySpan<byte>.Empty, chars.Span, flush: true);

            var outputBytes = byteAccumulator.Detach();
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

    private static async Task<PtyCapturePumpResult> ReadCoreAsync(
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
        if (output.Length == readBufferLength && read == readBufferLength)
            output.EnsureCapacity(SustainedOutputInitialCapacity);
    }

    /// <summary>
    /// Single destination buffer for <see cref="ReadTransport"/> merged bytes.
    /// Pre-sizes once for sustained output so transport reads copy directly into the result array.
    /// </summary>
    private struct CaptureByteAccumulator
    {
        private byte[] buffer;
        private int length;

        public CaptureByteAccumulator()
        {
            buffer = [];
        }

        internal int Length => length;

        internal void ReserveForSustainedOutput(int read)
        {
            if (length == 0 && read >= PtyReadBuffer.Size)
                EnsureCapacity(SustainedOutputInitialCapacity);
        }

        internal void Append(ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty)
                return;

            GrowIfNeeded(length + data.Length);
            data.CopyTo(buffer.AsSpan(length));
            length += data.Length;
        }

        internal byte[] Detach()
        {
            if (length == 0)
            {
                ReleaseBuffer();
                return [];
            }

            if (buffer.Length == length)
            {
                var exact = buffer;
                buffer = [];
                length = 0;
                return exact;
            }

            var trimmed = GC.AllocateUninitializedArray<byte>(length);
            buffer.AsSpan(0, length).CopyTo(trimmed);
            ReleaseBuffer();
            length = 0;
            return trimmed;
        }

        private void GrowIfNeeded(int required)
        {
            if (required <= buffer.Length)
                return;

            Grow(required);
        }

        private void EnsureCapacity(int required)
        {
            if (required <= buffer.Length)
                return;

            Grow(required);
        }

        private void Grow(int required)
        {
            var next = buffer.Length == 0 ? PtyReadBuffer.Size : buffer.Length * 2;
            while (next < required)
                next *= 2;

            var rented = ArrayPool<byte>.Shared.Rent(next);
            if (length > 0)
                buffer.AsSpan(0, length).CopyTo(rented);

            ReturnBuffer();
            buffer = rented;
        }

        private void ReturnBuffer()
        {
            if (buffer.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(buffer);
                buffer = [];
            }
        }

        private void ReleaseBuffer()
        {
            ReturnBuffer();
            length = 0;
        }
    }
}
