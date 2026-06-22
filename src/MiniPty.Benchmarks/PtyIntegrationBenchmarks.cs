using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using MiniPty.Capture;

namespace MiniPty.Benchmarks;

/// <summary>
/// End-to-end PTY benchmarks split by output path.
/// </summary>
/// <remarks>
/// <para><b>Binary</b> — <see cref="MiniPty.PtyCompleteOptions.DecodeOutput"/> = false; only merged bytes are retained.</para>
/// <para><b>Text</b> — default decode during pump (bytes + decoded chars) and display helpers.</para>
/// Allocations include OS process spawn; compare Binary vs Text to isolate decode and chunk overhead.
/// </remarks>
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

    [BenchmarkCategory("Integration", "Binary")]
    [Benchmark]
    public async Task<int> Session_Echo_Bytes()
    {
        if (!supported)
            return 0;

        await using var session = Pty.Start(echo);
        var result = await session.CompleteAsync(BenchmarkOptions.BytesOnly).ConfigureAwait(false);
        return result.Output.Length;
    }

    [BenchmarkCategory("Integration", "Binary")]
    [Benchmark]
    public async Task<int> Capture_Echo_Bytes()
    {
        if (!supported)
            return 0;

        var result = await PtyCapture.RunAsync(echo, BenchmarkOptions.CaptureBytesOnly).ConfigureAwait(false);
        return result.Output.Length;
    }

    [BenchmarkCategory("Integration", "Binary")]
    [Benchmark]
    public async Task<int> Capture_32KiB_Bytes()
    {
        if (!supported)
            return 0;

        var result = await PtyCapture.RunAsync(smallStdout, BenchmarkOptions.CaptureBytesOnly).ConfigureAwait(false);
        return result.Output.Length;
    }

    [BenchmarkCategory("Integration", "Text")]
    [Benchmark]
    public async Task<int> Session_Echo_Text()
    {
        if (!supported)
            return 0;

        await using var session = Pty.Start(echo);
        var result = await session.CompleteAsync(BenchmarkOptions.TextDecoded).ConfigureAwait(false);
        return result.GetText().Length;
    }

    [BenchmarkCategory("Integration", "Text")]
    [Benchmark]
    public async Task<int> Capture_Echo_Text()
    {
        if (!supported)
            return 0;

        var result = await PtyCapture.RunAsync(echo, BenchmarkOptions.CaptureTextDecoded).ConfigureAwait(false);
        return result.GetText().Length;
    }

    [BenchmarkCategory("Integration", "Text")]
    [Benchmark]
    public async Task<int> Capture_32KiB_Text()
    {
        if (!supported)
            return 0;

        var result = await PtyCapture.RunAsync(smallStdout, BenchmarkOptions.CaptureTextDecoded).ConfigureAwait(false);
        return result.GetText().Length;
    }

    [BenchmarkCategory("Integration", "Text")]
    [Benchmark]
    public async Task<int> Capture_32KiB_DisplayPlain()
    {
        if (!supported)
            return 0;

        var result = await PtyCapture.RunAsync(smallStdout, BenchmarkOptions.CaptureTextDecoded).ConfigureAwait(false);
        return result.ToDisplayText(PtyOutputDisplayMode.PlainText).Length;
    }
}
