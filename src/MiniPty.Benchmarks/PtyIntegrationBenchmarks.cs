using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using MiniPty.Capture;

namespace MiniPty.Benchmarks;

/// <summary>
/// End-to-end PTY spawn, I/O, and completion benchmarks (OS and process overhead included).
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[SimpleJob(RuntimeMoniker.Net10_0, warmupCount: 2, iterationCount: 5, launchCount: 1)]
public class PtyIntegrationBenchmarks
{
    private PtyStartInfo echo = null!;
    private PtyStartInfo smallStdout = null!;
    private bool supported;

    [GlobalSetup]
    public void Setup()
    {
        supported = BenchmarkPtyCommands.IsSupported;
        if (!supported)
            return;

        echo = BenchmarkPtyCommands.Echo();
        smallStdout = BenchmarkPtyCommands.SmallStdout(32_768);
    }

    [BenchmarkCategory("Integration")]
    [Benchmark]
    public async Task<int> Capture_Echo()
    {
        if (!supported)
            return 0;

        var result = await PtyCapture.RunAsync(echo).ConfigureAwait(false);
        return result.Output.Length;
    }

    [BenchmarkCategory("Integration")]
    [Benchmark]
    public async Task<int> Session_CompleteEcho()
    {
        if (!supported)
            return 0;

        await using var session = Pty.Start(echo);
        var result = await session.CompleteAsync().ConfigureAwait(false);
        return result.Output.Length;
    }

    [BenchmarkCategory("Integration")]
    [Benchmark]
    public async Task<int> Capture_32KiBStdout()
    {
        if (!supported)
            return 0;

        var result = await PtyCapture.RunAsync(smallStdout).ConfigureAwait(false);
        return result.Output.Length;
    }

    [BenchmarkCategory("Integration")]
    [Benchmark]
    public async Task<int> Capture_32KiBStdout_WithPlainDisplay()
    {
        if (!supported)
            return 0;

        var result = await PtyCapture.RunAsync(smallStdout).ConfigureAwait(false);
        return result.ToDisplayText(PtyOutputDisplayMode.PlainText).Length;
    }
}
