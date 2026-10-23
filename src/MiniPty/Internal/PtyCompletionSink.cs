using System.Text;

namespace MiniPty.Internal;

/// <summary>
/// <see cref="PtySession.CompleteAsync"/> output: merged bytes, plus text decoded while output arrives when
/// <see cref="PtyCompleteOptions.DecodeOutput"/> is set.
/// </summary>
/// <remarks>
/// Output collects in segments (<see cref="PtySegmentedBuffer{T}"/>), so a write costs what it carries, and is merged
/// into the result arrays once, by <see cref="ToPayload"/> on the caller's thread. Nothing here depends on which thread
/// delivers output: the segments come from a pool without per-thread caches.
/// </remarks>
internal sealed class PtyCompletionSink : PtyOutputSink
{
    private readonly Encoding _encoding;
    private readonly Decoder? _decoder;
    // Mutable structs, never readonly: a readonly field would hand every call a copy.
    private PtySegmentedBuffer<byte> _bytes;
    private PtySegmentedBuffer<char> _chars;
    private PtySinkState _state;

    internal PtyCompletionSink(Encoding encoding, bool decodeOutput)
    {
        _encoding = encoding;
        if (decodeOutput)
            _decoder = encoding.GetDecoder();
    }

    internal override void Write(ReadOnlySpan<byte> data, long timestamp)
    {
        _bytes.Append(data);
        if (_decoder is not null)
            PtyTextDecoding.Decode(_decoder, _encoding, data, flush: false, ref _chars);
    }

    /// <summary>Flushes the decoder. The result arrays are built later, on the caller's thread, by <see cref="ToPayload"/>.</summary>
    internal override void OnOutputEnded()
    {
        if (_decoder is not null)
            PtyTextDecoding.Decode(_decoder, _encoding, ReadOnlySpan<byte>.Empty, flush: true, ref _chars);

        _state = PtySinkState.Ended;
    }

    /// <summary>
    /// Returns the segments of a failed completion, whose result nobody takes, whether or not output had ended.
    /// </summary>
    internal override void Abort()
    {
        _state = PtySinkState.Aborted;
        _bytes.Release();
        _chars.Release();
    }

    /// <summary>
    /// Builds the result, merging the segments into its arrays. Called once, after the drain wait reported end of
    /// output.
    /// </summary>
    internal PtyPumpPayload ToPayload()
    {
        PtySinkStates.Take(ref _state);
        var bytes = _bytes.ToArray();
        var chars = _decoder is null ? null : _chars.ToArray();
        return new PtyPumpPayload(bytes, chars, _encoding);
    }
}
