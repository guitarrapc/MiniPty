using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Jobs;
using MiniPty.Capture;
using MiniPty.Internal;

namespace MiniPty.Benchmarks;

/// <summary>
/// Text/display helpers on synthetic capture results (no PTY spawn).
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[SimpleJob(RuntimeMoniker.Net10_0, warmupCount: 3, iterationCount: 10)]
public class PtyCaptureDisplayBenchmarks
{
    private Consumer consumer = null!;
    private PtyCaptureResult captureResult = null!;
    private IReadOnlyList<PtyCaptureTextChunk> manyTextChunks = [];

    [GlobalSetup]
    public void Setup()
    {
        consumer = new Consumer();
        var merged = BenchmarkSamples.AnsiHeavy(512);
        var mergedBytes = Encoding.UTF8.GetBytes(merged);
        var mergedChars = merged.ToCharArray();
        (_, PtyCaptureTextChunk[] manyTextChunkArr) = BenchmarkSamples.ChunkedAnsi(256, 64);
        manyTextChunks = manyTextChunkArr;
        captureResult = new PtyCaptureResult(
            new PtyPumpPayload(mergedBytes, mergedChars, Encoding.UTF8),
            0,
            [],
            manyTextChunkArr);
    }

    [BenchmarkCategory("Micro", "Text")]
    [Benchmark]
    public void Result_PlainText() =>
        consumer.Consume(captureResult.ToDisplayText(PtyOutputDisplayMode.PlainText));

    [BenchmarkCategory("Micro", "Text")]
    [Benchmark]
    public void ManyChunks_PlainText() =>
        consumer.Consume(manyTextChunks.ToDisplayText(PtyOutputDisplayMode.PlainText));
}
