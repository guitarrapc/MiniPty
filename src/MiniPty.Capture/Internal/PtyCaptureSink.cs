using System.Buffers;
using System.Text;
using MiniPty.Internal;

namespace MiniPty.Capture;

/// <summary>
/// <see cref="PtyCapture.RunAsync"/> output: merged bytes with one timestamped chunk per output read, plus text
/// decoded while output arrives when <see cref="PtyCompleteOptions.DecodeOutput"/> is set.
/// </summary>
/// <remarks>
/// <para>
/// The pump writes here on the session's reader thread right after each read, so a chunk's time is the read's
/// completion time. With <see cref="TimeProvider.System"/> it is taken from the read itself; another provider is
/// asked when the chunk arrives here, which is the same moment for reads delivered directly. Output read before
/// the sink attached is timestamped on arrival, and a read that completed before the capture origin counts as
/// time zero.
/// </para>
/// <para>
/// Chunk metadata is kept as offsets into the merged buffers and turned into chunks once, when output ends, so the
/// chunks share the merged arrays instead of copying each read.
/// </para>
/// </remarks>
internal sealed class PtyCaptureSink : PtyOutputSink
{
    private const int SustainedOutputInitialCapacity = 32 * 1024;

    private readonly long _originTimestamp;
    private readonly TimeProvider _timeProvider;
    private readonly bool _useReadTimestamps;
    private readonly Encoding _encoding;
    private readonly List<ChunkMeta> _byteChunks = new(capacity: 64);
    private readonly List<ChunkMeta>? _textChunks;
    private readonly PtyGrowingBuffer<char>? _chars;
    private readonly Decoder? _decoder;
    private ByteAccumulator _bytes = new();
    private char[]? _charScratch;
    private TimeSpan _lastTime;
    private PtyPumpPayload? _payload;
    private PtyCaptureChunk[]? _chunks;
    private PtyCaptureTextChunk[]? _textChunkResult;

    internal PtyCaptureSink(long originTimestamp, TimeProvider timeProvider, Encoding encoding, bool decodeOutput)
    {
        _originTimestamp = originTimestamp;
        _timeProvider = timeProvider;
        _useReadTimestamps = ReferenceEquals(timeProvider, TimeProvider.System);
        _encoding = encoding;
        if (!decodeOutput)
            return;

        _textChunks = new List<ChunkMeta>(capacity: 64);
        _chars = new PtyGrowingBuffer<char>();
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
        _bytes.ReserveForSustainedOutput(data.Length);
        _bytes.Append(data);

        if (_decoder is not null)
            Decode(time, data, flush: false);
    }

    /// <summary>Finishes the merged buffers and chunks on the reader thread, where the buffers were rented.</summary>
    internal override void OnOutputEnded()
    {
        try
        {
            if (_decoder is not null)
                Decode(_byteChunks.Count == 0 ? ElapsedSinceStart(0) : _lastTime, ReadOnlySpan<byte>.Empty, flush: true);

            var outputBytes = _bytes.Detach();
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
                outputChars = _chars!.Detach();
                textChunks = new PtyCaptureTextChunk[_textChunks!.Count];
                for (var i = 0; i < textChunks.Length; i++)
                {
                    var meta = _textChunks[i];
                    textChunks[i] = new PtyCaptureTextChunk(meta.Time, outputChars.AsMemory(meta.Start, meta.Length));
                }
            }

            _payload = new PtyPumpPayload(outputBytes, outputChars, _encoding);
            _chunks = chunks;
            _textChunkResult = textChunks;
        }
        finally
        {
            var scratch = _charScratch;
            if (scratch is not null)
            {
                _charScratch = null;
                ArrayPool<char>.Shared.Return(scratch);
            }

            _chars?.Dispose();
        }
    }

    internal override void Abort()
    {
        _bytes.Release();
        _chars?.Dispose();
        var scratch = _charScratch;
        if (scratch is not null)
        {
            _charScratch = null;
            ArrayPool<char>.Shared.Return(scratch);
        }
    }

    /// <summary>Builds the result. Called once, after the drain wait reported end of output.</summary>
    internal PtyCaptureResult ToResult(int exitCode) =>
        new(_payload ?? throw new InvalidOperationException("PTY output has not ended."), exitCode, _chunks!, _textChunkResult);

    private TimeSpan ElapsedSinceStart(long timestamp)
    {
        if (timestamp == 0 || !_useReadTimestamps)
            return _timeProvider.GetElapsedTime(_originTimestamp);

        var elapsed = _timeProvider.GetElapsedTime(_originTimestamp, timestamp);
        return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
    }

    private void Decode(TimeSpan time, ReadOnlySpan<byte> data, bool flush)
    {
        // Each write is at most one ring slot, so one maximum-size scratch buffer always fits it.
        var scratch = _charScratch ??= ArrayPool<char>.Shared.Rent(PtyReadBuffer.GetCharBufferLength(_encoding));
        var charCount = _decoder!.GetChars(data, scratch, flush);
        if (charCount <= 0)
            return;

        _textChunks!.Add(new ChunkMeta(time, _chars!.Length, charCount));
        _chars.Append(scratch.AsSpan(0, charCount));
    }

    /// <summary>
    /// Single destination buffer for merged bytes. Pre-sizes once for sustained output so reads copy straight
    /// into the result array.
    /// </summary>
    private struct ByteAccumulator
    {
        private byte[] buffer;
        private int length;

        public ByteAccumulator()
        {
            buffer = [];
        }

        internal readonly int Length => length;

        internal void ReserveForSustainedOutput(int read)
        {
            if (length == 0 && read >= PtyReadBuffer.Size)
                Grow(SustainedOutputInitialCapacity);
        }

        internal void Append(ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty)
                return;

            if (length + data.Length > buffer.Length)
                Grow(length + data.Length);

            data.CopyTo(buffer.AsSpan(length));
            length += data.Length;
        }

        internal byte[] Detach()
        {
            if (length == 0)
            {
                Release();
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
            Release();
            return trimmed;
        }

        private void Grow(int required)
        {
            if (required <= buffer.Length)
                return;

            var next = buffer.Length == 0 ? PtyReadBuffer.Size : buffer.Length * 2;
            while (next < required)
                next *= 2;

            var rented = ArrayPool<byte>.Shared.Rent(next);
            if (length > 0)
                buffer.AsSpan(0, length).CopyTo(rented);

            if (buffer.Length > 0)
                ArrayPool<byte>.Shared.Return(buffer);

            buffer = rented;
        }

        internal void Release()
        {
            if (buffer.Length > 0)
                ArrayPool<byte>.Shared.Return(buffer);

            buffer = [];
            length = 0;
        }
    }
}
