using System.Buffers;
using System.Text;

namespace MiniPty.Internal;

/// <summary>
/// <see cref="PtySession.CompleteAsync"/> output: merged bytes, plus text decoded while output arrives when
/// <see cref="PtyCompleteOptions.DecodeOutput"/> is set.
/// </summary>
internal sealed class PtyCompletionSink : PtyOutputSink
{
    private const int SustainedOutputInitialCapacity = 32 * 1024;

    private readonly Encoding _encoding;
    private readonly PtyGrowingBuffer<byte> _bytes = new();
    private readonly PtyGrowingBuffer<char>? _chars;
    private readonly Decoder? _decoder;
    private char[]? _charScratch;
    private PtyPumpPayload? _payload;

    internal PtyCompletionSink(Encoding encoding, bool decodeOutput)
    {
        _encoding = encoding;
        if (!decodeOutput)
            return;

        _chars = new PtyGrowingBuffer<char>();
        _decoder = encoding.GetDecoder();
    }

    internal override void Write(ReadOnlySpan<byte> data, long timestamp)
    {
        // A second full read means sustained output: grow once to the bulk size instead of doubling up to it.
        if (_bytes.Length == PtyReadBuffer.Size && data.Length == PtyReadBuffer.Size)
            _bytes.EnsureCapacity(SustainedOutputInitialCapacity);

        _bytes.Append(data);
        if (_decoder is not null)
            Decode(data, flush: false);
    }

    /// <summary>Finishes the result on the reader thread, where the buffers were rented.</summary>
    internal override void OnOutputEnded()
    {
        char[]? chars = null;
        if (_decoder is not null)
        {
            Decode(ReadOnlySpan<byte>.Empty, flush: true);
            chars = _chars!.Detach();
        }

        var scratch = _charScratch;
        if (scratch is not null)
        {
            _charScratch = null;
            ArrayPool<char>.Shared.Return(scratch);
        }

        _payload = new PtyPumpPayload(_bytes.Detach(), chars, _encoding);
    }

    // Detached buffers are owned by the payload, so after OnOutputEnded only what it did not reach is returned.
    internal override void Abort()
    {
        _bytes.Dispose();
        _chars?.Dispose();
        var scratch = _charScratch;
        if (scratch is not null)
        {
            _charScratch = null;
            ArrayPool<char>.Shared.Return(scratch);
        }
    }

    /// <summary>Gets the result. Called once, after the drain wait reported end of output.</summary>
    internal PtyPumpPayload ToPayload() =>
        _payload ?? throw new InvalidOperationException("PTY output has not ended.");

    private void Decode(ReadOnlySpan<byte> data, bool flush)
    {
        // Each write is at most one ring slot, so one maximum-size scratch buffer always fits it.
        var scratch = _charScratch ??= ArrayPool<char>.Shared.Rent(PtyReadBuffer.GetCharBufferLength(_encoding));
        var charCount = _decoder!.GetChars(data, scratch, flush);
        if (charCount > 0)
            _chars!.Append(scratch.AsSpan(0, charCount));
    }
}
