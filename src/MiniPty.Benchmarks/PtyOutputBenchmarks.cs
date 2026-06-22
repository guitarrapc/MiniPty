using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Jobs;

namespace MiniPty.Benchmarks;

/// <summary>
/// Microbenchmarks for <see cref="PtyOutput.ToDisplayText(string, PtyOutputDisplayMode)"/> (text/display path only).
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[SimpleJob(RuntimeMoniker.Net10_0, warmupCount: 3, iterationCount: 10)]
public class PtyOutputBenchmarks
{
    private Consumer consumer = null!;
    private string ansiLine = "";
    private string ansiMedium = "";
    private string ansiLarge = "";
    private string plainLarge = "";

    [GlobalSetup]
    public void Setup()
    {
        consumer = new Consumer();
        ansiLine = BenchmarkSamples.AnsiLine;
        ansiMedium = BenchmarkSamples.AnsiHeavy(256);
        ansiLarge = BenchmarkSamples.AnsiHeavy(4_096);
        plainLarge = BenchmarkSamples.PlainHeavy(65_536);
    }

    [BenchmarkCategory("Micro", "Text")]
    [Benchmark]
    public void PlainText_Small() =>
        consumer.Consume(PtyOutput.ToDisplayText(ansiLine, PtyOutputDisplayMode.PlainText));

    [BenchmarkCategory("Micro", "Text")]
    [Benchmark]
    public void AnsiText_Small() =>
        consumer.Consume(PtyOutput.ToDisplayText(ansiLine, PtyOutputDisplayMode.AnsiText));

    [BenchmarkCategory("Micro", "Text")]
    [Benchmark]
    public void PlainText_MediumAnsi() =>
        consumer.Consume(PtyOutput.ToDisplayText(ansiMedium, PtyOutputDisplayMode.PlainText));

    [BenchmarkCategory("Micro", "Text")]
    [Benchmark]
    public void PlainText_LargeAnsi() =>
        consumer.Consume(PtyOutput.ToDisplayText(ansiLarge, PtyOutputDisplayMode.PlainText));

    [BenchmarkCategory("Micro", "Text")]
    [Benchmark]
    public void PlainText_LargePlain() =>
        consumer.Consume(PtyOutput.ToDisplayText(plainLarge, PtyOutputDisplayMode.PlainText));
}
