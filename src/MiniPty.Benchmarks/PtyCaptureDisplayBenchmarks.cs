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
/// CPU and allocation benchmarks for capture display helpers.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[SimpleJob(RuntimeMoniker.Net10_0, warmupCount: 3, iterationCount: 10)]
public class PtyCaptureDisplayBenchmarks
{
    private Consumer consumer = null!;
    private PtyCaptureResult captureResult = null!;
    private IReadOnlyList<PtyCaptureTextChunk> fewTextChunks = [];
    private IReadOnlyList<PtyCaptureTextChunk> manyTextChunks = [];

    [GlobalSetup]
    public void Setup()
    {
        consumer = new Consumer();
        var merged = BenchmarkSamples.AnsiHeavy(512);
        var mergedBytes = Encoding.UTF8.GetBytes(merged);
        var mergedChars = merged.ToCharArray();
        (PtyCaptureChunk[] fewByteChunks, PtyCaptureTextChunk[] fewTextChunkArr) = BenchmarkSamples.ChunkedAnsi(16, 64);
        (PtyCaptureChunk[] manyByteChunks, PtyCaptureTextChunk[] manyTextChunkArr) = BenchmarkSamples.ChunkedAnsi(256, 64);
        fewTextChunks = fewTextChunkArr;
        manyTextChunks = manyTextChunkArr;
        captureResult = new PtyCaptureResult(
            new PtyPumpPayload(mergedBytes, mergedChars, Encoding.UTF8),
            0,
            manyByteChunks,
            manyTextChunkArr);
    }

    [BenchmarkCategory("Micro", "Capture")]
    [Benchmark]
    public void Result_PlainText() =>
        consumer.Consume(captureResult.ToDisplayText(PtyOutputDisplayMode.PlainText));

    [BenchmarkCategory("Micro", "Capture")]
    [Benchmark]
    public void Result_AnsiText() =>
        consumer.Consume(captureResult.ToDisplayText(PtyOutputDisplayMode.AnsiText));

    [BenchmarkCategory("Micro", "Capture")]
    [Benchmark]
    public void FewChunks_PlainText() =>
        consumer.Consume(fewTextChunks.ToDisplayText(PtyOutputDisplayMode.PlainText));

    [BenchmarkCategory("Micro", "Capture")]
    [Benchmark]
    public void ManyChunks_PlainText() =>
        consumer.Consume(manyTextChunks.ToDisplayText(PtyOutputDisplayMode.PlainText));
}
