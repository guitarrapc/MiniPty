using System.Text;

namespace MiniPty.Internal;

internal static class PtyBytePump
{
    private const int SustainedOutputInitialCapacity = 32 * 1024;

    internal static Task<PtyPumpOutput> ReadAllAsync(
        Stream stream,
        Encoding encoding,
        bool decodeOutput,
        CancellationToken cancellationToken)
    {
        if (PtyTransportRead.IsTransport(stream))
        {
            if (OperatingSystem.IsMacOS())
                return PtyTransportPumpTask.Run(ct => ReadAllTransport(stream, encoding, decodeOutput, ct), cancellationToken);
            return Task.Run(() => ReadAllTransport(stream, encoding, decodeOutput, cancellationToken), cancellationToken);
        }

        return ReadAllCoreAsync(stream, encoding, decodeOutput, cancellationToken);
    }

    private static PtyPumpOutput ReadAllTransport(
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
                cancellationToken.ThrowIfCancellationRequested();
                var read = PtyTransportRead.Read(stream, bytes.Span);
                if (read <= 0)
                    break;

                ReserveForSustainedOutput(byteBuffer, read, bytes.Memory.Length);
                byteBuffer.Append(bytes.Span[..read]);
            }

            return new PtyPumpOutput(byteBuffer.Detach(), null, encoding);
        }

        using var charBuffer = new PtyGrowingBuffer<char>();
        using var chars = PtyReadBuffer.RentChars(encoding);
        var decoder = encoding.GetDecoder();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = PtyTransportRead.Read(stream, bytes.Span);
            if (read <= 0)
                break;

            ReserveForSustainedOutput(byteBuffer, read, bytes.Memory.Length);
            byteBuffer.Append(bytes.Span[..read]);
            AppendDecoded(decoder, bytes.Span[..read], chars.Span, charBuffer);
        }

        AppendDecoded(decoder, ReadOnlySpan<byte>.Empty, chars.Span, charBuffer, flush: true);
        return new PtyPumpOutput(byteBuffer.Detach(), charBuffer.Detach(), encoding);
    }

    private static async Task<PtyPumpOutput> ReadAllCoreAsync(
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

                ReserveForSustainedOutput(byteBuffer, read, bytes.Memory.Length);
                byteBuffer.Append(bytes.Span[..read]);
            }

            return new PtyPumpOutput(byteBuffer.Detach(), null, encoding);
        }

        using var charBuffer = new PtyGrowingBuffer<char>();
        using var chars = PtyReadBuffer.RentChars(encoding);
        var decoder = encoding.GetDecoder();

        while (true)
        {
            var read = await stream.ReadAsync(bytes.Memory, cancellationToken).ConfigureAwait(false);
            if (read <= 0)
                break;

            ReserveForSustainedOutput(byteBuffer, read, bytes.Memory.Length);
            byteBuffer.Append(bytes.Span[..read]);
            AppendDecoded(decoder, bytes.Span[..read], chars.Span, charBuffer);
        }

        AppendDecoded(decoder, ReadOnlySpan<byte>.Empty, chars.Span, charBuffer, flush: true);
        return new PtyPumpOutput(byteBuffer.Detach(), charBuffer.Detach(), encoding);
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

    private static void ReserveForSustainedOutput(PtyGrowingBuffer<byte> output, int read, int readBufferLength)
    {
        if (output.Length == readBufferLength && read == readBufferLength)
            output.EnsureCapacity(SustainedOutputInitialCapacity);
    }
}
