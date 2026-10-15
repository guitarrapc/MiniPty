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
    /// A failed completion returns the sink's pooled buffers. Aborting must be safe at any point: midway, twice, and
    /// after the result was finished, without touching the finished result.
    /// </summary>
    [Test]
    public async Task AbortIsIdempotentAndKeepsAFinishedResult()
    {
        var midway = new PtyCompletionSink(Encoding.UTF8, decodeOutput: true);
        midway.Write(new byte[PtyReadBuffer.Size], timestamp: 0);
        midway.Abort();
        midway.Abort();

        var finished = new PtyCompletionSink(Encoding.UTF8, decodeOutput: true);
        finished.Write("done"u8, timestamp: 0);
        finished.OnOutputEnded();
        finished.Abort();
        var payload = finished.ToPayload();

        await Assert.That(Encoding.UTF8.GetString(payload.Bytes)).IsEqualTo("done");
        await Assert.That(new string(payload.Chars!)).IsEqualTo("done");
    }
}
