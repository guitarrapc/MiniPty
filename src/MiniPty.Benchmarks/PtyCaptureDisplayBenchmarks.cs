using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Jobs;
using MiniPty.Capture;

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
    private IReadOnlyList<PtyCaptureChunk> fewChunks = [];
    private IReadOnlyList<PtyCaptureChunk> manyChunks = [];

    [GlobalSetup]
    public void Setup()
    {
        consumer = new Consumer();
        var merged = BenchmarkSamples.AnsiHeavy(512);
        fewChunks = BenchmarkSamples.ChunkedAnsi(16, 64);
        manyChunks = BenchmarkSamples.ChunkedAnsi(256, 64);
        captureResult = new PtyCaptureResult(merged, 0, manyChunks);
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
        consumer.Consume(fewChunks.ToDisplayText(PtyOutputDisplayMode.PlainText));

    [BenchmarkCategory("Micro", "Capture")]
    [Benchmark]
    public void ManyChunks_PlainText() =>
        consumer.Consume(manyChunks.ToDisplayText(PtyOutputDisplayMode.PlainText));
}
