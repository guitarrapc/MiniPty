using System.Diagnostics;
using System.Text;
using MiniPty.Capture;
using MiniPty.Internal;

namespace MiniPty.Tests;

/// <summary>
/// One-shot completion sinks, driven directly the way the output pump drives them.
/// </summary>
public sealed class PtyOutputSinkTests
{
    /// <summary>
    /// Output read before capture attached is delivered first and timestamped on delivery, ahead of a read that was
    /// already in flight and completed earlier. Chunk times must still never decrease.
    /// </summary>
    [Test]
    public async Task CaptureChunkTimesNeverDecrease()
    {
        var origin = Stopwatch.GetTimestamp();
        var sink = new PtyCaptureSink(origin, TimeProvider.System, Encoding.UTF8, decodeOutput: true);
        var inFlightReadCompleted = Stopwatch.GetTimestamp();
        await Task.Delay(20);

        sink.Write("backlog"u8, timestamp: 0);
        sink.Write("in-flight"u8, inFlightReadCompleted);
        sink.OnOutputEnded();
        var result = sink.ToResult(exitCode: 0);

        await Assert.That(result.Chunks.Count).IsEqualTo(2);
        await Assert.That(result.Chunks[1].Time).IsGreaterThanOrEqualTo(result.Chunks[0].Time);
        var text = result.GetTextChunks();
        for (var i = 1; i < text.Count; i++)
            await Assert.That(text[i].Time).IsGreaterThanOrEqualTo(text[i - 1].Time);
    }

    /// <summary>
    /// Reads that follow one another closely join one chunk, as the reader thread's coalescing window joined them, so a
    /// driver that publishes each read as it completes does not multiply Capture's chunks: within the gap of the previous
    /// read, the budget from the chunk's first read, and one publish of size. The joined chunk keeps its first read's
    /// time, and its text joins too. Output delivered without a read timestamp never joins.
    /// </summary>
    [Test]
    public async Task CaptureJoinsReadsThatFollowClosely()
    {
        var origin = Stopwatch.GetTimestamp();
        long At(int microseconds) => origin + Stopwatch.Frequency * (1000 + microseconds) / 1_000_000;
        var sink = new PtyCaptureSink(origin, TimeProvider.System, Encoding.UTF8, decodeOutput: true);

        // Within the gap: one chunk.
        sink.Write("a"u8, At(0));
        sink.Write("b"u8, At(100));
        sink.Write("c"u8, At(200));
        // A longer gap starts a chunk, which takes reads 400 µs apart until its budget runs out.
        for (var i = 0; i < 7; i++)
            sink.Write("d"u8, At(1500 + i * 400));
        // A read that would make the chunk larger than one publish starts a chunk.
        sink.Write(Fill('e', PtyOutputPump.PublishSize - 1), At(10_000));
        sink.Write("ff"u8, At(10_100));
        // Output read before the sink attached never joins.
        sink.Write("g"u8, timestamp: 0);
        sink.Write("h"u8, timestamp: 0);
        sink.OnOutputEnded();
        var result = sink.ToResult(exitCode: 0);

        string[] expected = ["abc", "dddddd", "d", new string('e', PtyOutputPump.PublishSize - 1), "ff", "g", "h"];
        await Assert.That(result.Chunks.Count).IsEqualTo(expected.Length);
        var text = result.GetTextChunks();
        await Assert.That(text.Count).IsEqualTo(expected.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            await Assert.That(Encoding.UTF8.GetString(result.Chunks[i].Data.Span)).IsEqualTo(expected[i]);
            await Assert.That(new string(text[i].Text.Span)).IsEqualTo(expected[i]);
            await Assert.That(text[i].Time).IsEqualTo(result.Chunks[i].Time);
        }

        // A joined chunk keeps the time of its first read.
        await Assert.That(result.Chunks[0].Time).IsEqualTo(Stopwatch.GetElapsedTime(origin, At(0)));
    }

    private static byte[] Fill(char value, int length)
    {
        var bytes = new byte[length];
        bytes.AsSpan().Fill((byte)value);
        return bytes;
    }

    /// <summary>
    /// A write can hold more than one read: output published before a sink attached arrives as one contiguous span of
    /// up to the whole output ring. Decoding sized its buffer for one read and threw on such a span. Characters split
    /// across writes must still decode whole.
    /// </summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SinksDecodeAWriteLargerThanOneRead(bool capture)
    {
        // One character per byte across the whole ring, the most characters a write can decode to, then a three-byte
        // character split between two writes.
        var text = new string('a', PtyOutputPump.Capacity);
        var bytes = Encoding.UTF8.GetBytes(text);
        var euro = Encoding.UTF8.GetBytes("€");

        string decoded;
        if (capture)
        {
            var sink = new PtyCaptureSink(Stopwatch.GetTimestamp(), TimeProvider.System, Encoding.UTF8, decodeOutput: true);
            sink.Write(bytes, timestamp: 0);
            sink.Write(euro.AsSpan(0, 1), timestamp: 0);
            sink.Write(euro.AsSpan(1), timestamp: 0);
            sink.OnOutputEnded();
            decoded = sink.ToResult(exitCode: 0).GetTextString();
        }
        else
        {
            var sink = new PtyCompletionSink(Encoding.UTF8, decodeOutput: true);
            sink.Write(bytes, timestamp: 0);
            sink.Write(euro.AsSpan(0, 1), timestamp: 0);
            sink.Write(euro.AsSpan(1), timestamp: 0);
            sink.OnOutputEnded();
            decoded = new string(sink.ToPayload().Chars!);
        }

        await Assert.That(decoded).IsEqualTo(text + "€");
    }

    /// <summary>
    /// Multi-megabyte output fed in writes of uneven sizes keeps every byte and decodes to the text that was written,
    /// with characters whose bytes straddle two byte segments and a surrogate pair that meets a character segment
    /// with one element left, too short for the decoder. The output is larger than the segment pool keeps.
    /// </summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SinksKeepMultiMegabyteOutputWithCharactersAcrossSegments(bool capture)
    {
        const int TargetBytes = 3 * 1024 * 1024;
        var segmentChars = PtySegmentPool<char>.SegmentLength;
        var segmentBytes = PtySegmentPool<byte>.SegmentLength;

        var builder = new StringBuilder(TargetBytes);
        // The pair starts at the last element of the first character segment.
        builder.Append('a', segmentChars - 1).Append("😀");
        // The three bytes of '€' start at the last byte of the first byte segment.
        builder.Append('a', segmentBytes - 1 - Encoding.UTF8.GetByteCount(builder.ToString())).Append('€');
        // Then a fixed mix of one- to four-byte characters, so further pairs and sequences land on boundaries.
        string[] pieces = ["a", "é", "€", "😀", "\r\n"];
        var random = new Random(1234);
        var byteCount = Encoding.UTF8.GetByteCount(builder.ToString());
        while (byteCount < TargetBytes)
        {
            var piece = pieces[random.Next(pieces.Length)];
            builder.Append(piece);
            byteCount += Encoding.UTF8.GetByteCount(piece);
        }

        var text = builder.ToString();
        var bytes = Encoding.UTF8.GetBytes(text);
        // Writes of 1 byte up to a whole ring, the range a sink is given.
        var writes = new List<int>();
        for (var offset = 0; offset < bytes.Length;)
        {
            var length = Math.Min(random.Next(1, PtyOutputPump.Capacity + 1), bytes.Length - offset);
            writes.Add(length);
            offset += length;
        }

        ReadOnlyMemory<byte> output;
        string decoded;
        if (capture)
        {
            var sink = new PtyCaptureSink(Stopwatch.GetTimestamp(), TimeProvider.System, Encoding.UTF8, decodeOutput: true);
            Feed(sink);
            var result = sink.ToResult(exitCode: 0);
            output = result.Output;
            decoded = result.GetTextString();

            await Assert.That(result.Chunks.Count).IsEqualTo(writes.Count);
            var chunkBytes = new List<byte>(bytes.Length);
            var chunkText = new StringBuilder(text.Length);
            for (var i = 0; i < result.Chunks.Count; i++)
                chunkBytes.AddRange(result.Chunks[i].Data.Span);
            var textChunks = result.GetTextChunks();
            for (var i = 0; i < textChunks.Count; i++)
                chunkText.Append(textChunks[i].Text.Span);

            await Assert.That(chunkBytes.ToArray().AsSpan().SequenceEqual(bytes)).IsTrue();
            await Assert.That(chunkText.ToString()).IsEqualTo(text);
        }
        else
        {
            var sink = new PtyCompletionSink(Encoding.UTF8, decodeOutput: true);
            Feed(sink);
            var payload = sink.ToPayload();
            output = payload.Bytes;
            decoded = new string(payload.Chars!);
        }

        await Assert.That(output.Span.SequenceEqual(bytes)).IsTrue();
        await Assert.That(decoded).IsEqualTo(text);

        void Feed(PtyOutputSink sink)
        {
            var offset = 0;
            foreach (var length in writes)
            {
                sink.Write(bytes.AsSpan(offset, length), timestamp: 0);
                offset += length;
            }

            sink.OnOutputEnded();
        }
    }

    /// <summary>
    /// Text chunks decoded on demand from byte chunks (Capture without <see cref="PtyCompleteOptions.DecodeOutput"/>)
    /// decode a byte chunk larger than one read, and keep one text chunk per byte chunk that produced text.
    /// </summary>
    [Test]
    public async Task CaptureTextChunksOnDemandDecodeAChunkLargerThanOneRead()
    {
        var text = new string('é', PtyOutputPump.Capacity / 2);
        var sink = new PtyCaptureSink(Stopwatch.GetTimestamp(), TimeProvider.System, Encoding.UTF8, decodeOutput: false);
        sink.Write(Encoding.UTF8.GetBytes(text), timestamp: 0);
        sink.Write("!"u8, timestamp: 0);
        sink.OnOutputEnded();

        var chunks = sink.ToResult(exitCode: 0).GetTextChunks();

        await Assert.That(chunks.Count).IsEqualTo(2);
        await Assert.That(new string(chunks[0].Text.Span)).IsEqualTo(text);
        await Assert.That(new string(chunks[1].Text.Span)).IsEqualTo("!");
    }

    /// <summary>
    /// A sink fed on one thread and finished on another, as a sink is once output is delivered by I/O completions on
    /// pool workers, allocates nothing from run to run beyond its result. Buffers rented from a pool that caches per
    /// thread were stranded in the caches of threads that ended, so every run grew its buffers anew.
    /// </summary>
    [Test]
    [NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SinksFedAndFinishedOnOtherThreadsAllocateNothingBeyondTheResult(bool decodeOutput)
    {
        var read = new byte[PtyReadBuffer.Size];
        read.AsSpan().Fill((byte)'x');
        const int Reads = 8;
        const int Runs = 20;

        // What the sink allocates on the threads that run it, beyond its result. Counted per thread, so work elsewhere in
        // the process (the test framework, the runtime) cannot reach the number.
        long Run()
        {
            var sink = new PtyCompletionSink(Encoding.UTF8, decodeOutput);
            // Each step on a thread of its own, which ends afterwards, as pool workers come and go.
            var allocated = OnNewThread(() =>
            {
                for (var i = 0; i < Reads; i++)
                    sink.Write(read, timestamp: 0);
            });
            allocated += OnNewThread(sink.OnOutputEnded);
            long resultBytes = 0;
            allocated += OnNewThread(() =>
            {
                var payload = sink.ToPayload();
                resultBytes = payload.Bytes.Length + (payload.Chars?.Length ?? 0) * sizeof(char);
            });
            return allocated - resultBytes;
        }

        // Warm up, so that buffers and the payload types exist before measuring.
        for (var i = 0; i < 3; i++)
            Run();

        long beyondResults = 0;
        for (var i = 0; i < Runs; i++)
            beyondResults += Run();

        beyondResults /= Runs;

        // The payload, the result arrays' headers, and the decoder's flush cost a few hundred bytes per run; one regrown
        // 4 KiB buffer alone exceeds this, and stranded pool arrays cost 6.4 KB per run (81 KB with decoding).
        await Assert.That(beyondResults).IsLessThan(1024L);
    }

    /// <summary>Runs <paramref name="action"/> on a new thread, which then ends; returns what it allocated there.</summary>
    private static long OnNewThread(Action action)
    {
        long allocated = 0;
        var thread = new Thread(() =>
        {
            var start = GC.GetAllocatedBytesForCurrentThread();
            action();
            allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        });
        thread.Start();
        thread.Join();
        return allocated;
    }

    /// <summary>
    /// A failed completion returns the sink's pooled segments, and its result can no longer be taken. Aborting must be
    /// safe at any point: midway, twice, after output ended, and after the result was taken, which it leaves intact
    /// because the result owns arrays of its own.
    /// </summary>
    [Test]
    public async Task AbortIsIdempotentAndNeverTouchesATakenResult()
    {
        var midway = new PtyCompletionSink(Encoding.UTF8, decodeOutput: true);
        midway.Write(new byte[PtyReadBuffer.Size], timestamp: 0);
        midway.Abort();
        midway.Abort();

        var ended = new PtyCompletionSink(Encoding.UTF8, decodeOutput: true);
        ended.Write("done"u8, timestamp: 0);
        ended.OnOutputEnded();
        ended.Abort();

        var taken = new PtyCompletionSink(Encoding.UTF8, decodeOutput: true);
        taken.Write("done"u8, timestamp: 0);
        taken.OnOutputEnded();
        var payload = taken.ToPayload();
        taken.Abort();
        // The segments the result was copied from are back in the pool; writing over them must not reach the result.
        var reuse = new PtyCompletionSink(Encoding.UTF8, decodeOutput: true);
        reuse.Write("XXXX"u8, timestamp: 0);

        await Assert.That(midway.ToPayload).Throws<InvalidOperationException>();
        await Assert.That(ended.ToPayload).Throws<InvalidOperationException>();
        await Assert.That(taken.ToPayload).Throws<InvalidOperationException>();
        await Assert.That(Encoding.UTF8.GetString(payload.Bytes)).IsEqualTo("done");
        await Assert.That(new string(payload.Chars!)).IsEqualTo("done");
        reuse.Abort();
    }
}
