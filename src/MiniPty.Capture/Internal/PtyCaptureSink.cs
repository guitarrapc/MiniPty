using System.Text;
using MiniPty.Internal;

namespace MiniPty.Capture;

/// <summary>
/// <see cref="PtyCapture.RunAsync"/> output: merged bytes with one timestamped chunk per output read, plus text
/// decoded while output arrives when <see cref="PtyCompleteOptions.DecodeOutput"/> is set.
/// </summary>
/// <remarks>
/// <para>
/// The pump writes here right after each read, so a chunk's time is the read's completion time. With
/// <see cref="TimeProvider.System"/> it is taken from the read itself; another provider is asked when the chunk
/// arrives here, which is the same moment for reads delivered directly. Output read before the sink attached is
/// timestamped on arrival, and a read that completed before the capture origin counts as time zero.
/// </para>
/// <para>
/// Bytes and decoded text collect in segments (<see cref="PtySegmentedBuffer{T}"/>), so a write costs what it carries
/// wherever it is delivered. Chunk metadata is kept as offsets, and <see cref="ToResult"/> merges the segments into
/// one array each and turns the metadata into chunks over them on the caller's thread, so the chunks share the merged
/// arrays instead of copying each read.
/// </para>
/// </remarks>
internal sealed class PtyCaptureSink : PtyOutputSink
{
    private readonly long _originTimestamp;
    private readonly TimeProvider _timeProvider;
    private readonly bool _useReadTimestamps;
    private readonly Encoding _encoding;
    private readonly List<ChunkMeta> _byteChunks = new(capacity: 64);
    private readonly List<ChunkMeta>? _textChunks;
    private readonly Decoder? _decoder;
    // Mutable structs, never readonly: a readonly field would hand every call a copy.
    private PtySegmentedBuffer<byte> _bytes;
    private PtySegmentedBuffer<char> _chars;
    private TimeSpan _lastTime;
    private PtySinkState _state;

    internal PtyCaptureSink(long originTimestamp, TimeProvider timeProvider, Encoding encoding, bool decodeOutput)
    {
        _originTimestamp = originTimestamp;
        _timeProvider = timeProvider;
        _useReadTimestamps = ReferenceEquals(timeProvider, TimeProvider.System);
        _encoding = encoding;
        if (!decodeOutput)
            return;

        _textChunks = new List<ChunkMeta>(capacity: 64);
        _decoder = encoding.GetDecoder();
    }

    private readonly record struct ChunkMeta(TimeSpan Time, int Start, int Length);

    internal override void Write(ReadOnlySpan<byte> data, long timestamp)
    {
        // Output read before the sink attached is delivered first, timestamped on delivery, ahead of a read that was
        // already in flight and completed earlier; never let the timeline step back.
        var time = ElapsedSinceStart(timestamp);
        if (time < _lastTime)
            time = _lastTime;

        _lastTime = time;
        _byteChunks.Add(new ChunkMeta(time, _bytes.Length, data.Length));
        _bytes.Append(data);

        if (_decoder is not null)
            Decode(time, data, flush: false);
    }

    /// <summary>Flushes the decoder. The result is built later, on the caller's thread, by <see cref="ToResult"/>.</summary>
    internal override void OnOutputEnded()
    {
        if (_decoder is not null)
            Decode(_byteChunks.Count == 0 ? ElapsedSinceStart(0) : _lastTime, ReadOnlySpan<byte>.Empty, flush: true);

        _state = PtySinkState.Ended;
    }

    /// <summary>Returns the segments of a failed capture, whose result nobody takes, whether or not output had ended.</summary>
    internal override void Abort()
    {
        _state = PtySinkState.Aborted;
        _bytes.Release();
        _chars.Release();
    }

    /// <summary>
    /// Builds the result: merges the segments into one array each and turns the chunk metadata into chunks over them.
    /// Called once, after the drain wait reported end of output.
    /// </summary>
    internal PtyCaptureResult ToResult(int exitCode)
    {
        PtySinkStates.Take(ref _state);
        var outputBytes = _bytes.ToArray();
        var chunks = new PtyCaptureChunk[_byteChunks.Count];
        for (var i = 0; i < chunks.Length; i++)
        {
            var meta = _byteChunks[i];
            chunks[i] = new PtyCaptureChunk(meta.Time, outputBytes.AsMemory(meta.Start, meta.Length));
        }

        char[]? outputChars = null;
        PtyCaptureTextChunk[]? textChunks = null;
        if (_decoder is not null)
        {
            outputChars = _chars.ToArray();
            textChunks = new PtyCaptureTextChunk[_textChunks!.Count];
            for (var i = 0; i < textChunks.Length; i++)
            {
                var meta = _textChunks[i];
                textChunks[i] = new PtyCaptureTextChunk(meta.Time, outputChars.AsMemory(meta.Start, meta.Length));
            }
        }

        return new PtyCaptureResult(new PtyPumpPayload(outputBytes, outputChars, _encoding), exitCode, chunks, textChunks);
    }

    private TimeSpan ElapsedSinceStart(long timestamp)
    {
        if (timestamp == 0 || !_useReadTimestamps)
            return _timeProvider.GetElapsedTime(_originTimestamp);

        var elapsed = _timeProvider.GetElapsedTime(_originTimestamp, timestamp);
        return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
    }

    /// <summary>Decodes one write's bytes, recording one text chunk for the characters they complete.</summary>
    private void Decode(TimeSpan time, ReadOnlySpan<byte> data, bool flush)
    {
        var start = _chars.Length;
        PtyTextDecoding.Decode(_decoder!, _encoding, data, flush, ref _chars);
        var charCount = _chars.Length - start;
        if (charCount > 0)
            _textChunks!.Add(new ChunkMeta(time, start, charCount));
    }
}
