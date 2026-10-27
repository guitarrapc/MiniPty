using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using MiniPty.Capture;
using MiniPty.Internal;

namespace MiniPty.Benchmarks;

/// <summary>
/// Startup benchmarks: every operation spawns a child, reads what it writes, and waits for its exit, split by output
/// path.
/// </summary>
/// <remarks>
/// <para>
/// Each time is dominated by process creation (and on Windows by starting the console host), not by moving output:
/// the 32 KiB scenarios cost mostly the .NET startup of the benchmark child. Read them for spawn and exit cost and for
/// the allocations of each path. Output throughput and keystroke latency on a running session are measured by
/// <see cref="PtyThroughputBenchmarks"/> and <see cref="PtyLatencyBenchmarks"/>.
/// </para>
/// <para><b>Binary</b> — <see cref="MiniPty.PtyCompleteOptions.DecodeOutput"/> = false; only merged bytes are retained.</para>
/// <para><b>Text</b> — default decode during pump (bytes + decoded chars) and display helpers.</para>
/// <para><b>Exit</b> — <see cref="PtySession.WaitForExitAsync"/> on a persistent session that never reads output.</para>
/// Allocations include OS process spawn. Capture cost scales with <b>PTY read count</b>, not only merged byte length.
/// Compare <see cref="Session_32KiB_Bytes"/> vs <see cref="Capture_32KiB_Bytes"/> to isolate per-read chunk metadata.
/// Bulk stdout scenarios use the shared <c>MiniPty.Benchmarks.Child</c> helper on all platforms.
/// </remarks>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[SimpleJob(RuntimeMoniker.Net10_0, warmupCount: 2, iterationCount: 5, launchCount: 1)]
public class PtyStartupBenchmarks
{
    private PtyStartInfo echo = null!;
    private PtyStartInfo exit0 = null!;
    private PtyStartInfo smallStdout = null!;
    private bool supported;

    [GlobalSetup]
    public void Setup()
    {
        supported = BenchmarkPtyCommands.IsSupported;
        if (!supported)
            return;

        echo = BenchmarkPtyCommands.Echo();
        exit0 = BenchmarkPtyCommands.Exit0();
        smallStdout = BenchmarkPtyCommands.SmallStdout(32_768);
    }

    [BenchmarkCategory("Startup", "Binary")]
    [Benchmark]
    public async Task<int> Session_Exit0_Bytes()
    {
        if (!supported)
            return 0;

        await using var session = Pty.Start(exit0);
        var result = await session.CompleteAsync(BenchmarkOptions.BytesOnly).ConfigureAwait(false);
        return result.ExitCode;
    }

    [BenchmarkCategory("Startup", "Exit")]
    [Benchmark]
    public async Task<int> Session_Exit0_WaitForExit()
    {
        if (!supported)
            return 0;

        await using var session = Pty.Start(exit0);
        return (await session.WaitForExitAsync().ConfigureAwait(false)).ExitCode;
    }

    [BenchmarkCategory("Startup", "Binary")]
    [Benchmark]
    public async Task<int> Session_Echo_Bytes()
    {
        if (!supported)
            return 0;

        await using var session = Pty.Start(echo);
        var result = await session.CompleteAsync(BenchmarkOptions.BytesOnly).ConfigureAwait(false);
        return result.Output.Length;
    }

    [BenchmarkCategory("Startup", "Binary")]
    [Benchmark]
    public async Task<int> Capture_Echo_Bytes()
    {
        if (!supported)
            return 0;

        var result = await PtyCapture.RunAsync(echo, BenchmarkOptions.CaptureBytesOnly).ConfigureAwait(false);
        return result.Output.Length;
    }

    [BenchmarkCategory("Startup", "Binary")]
    [Benchmark]
    public async Task<int> Session_32KiB_Bytes()
    {
        if (!supported)
            return 0;

        await using var session = Pty.Start(smallStdout);
        var result = await session.CompleteAsync(BenchmarkOptions.BytesOnly).ConfigureAwait(false);
        return result.Output.Length;
    }

    [BenchmarkCategory("Startup", "Streaming", "Binary")]
    [Benchmark]
    public async Task<int> Session_32KiB_StreamBytes()
    {
        if (!supported)
            return 0;

        await using var session = Pty.Start(smallStdout);
        var length = 0;
        await foreach (var chunk in session.ReadOutputAsync())
            length += chunk.Data.Length;

        return length;
    }

    [BenchmarkCategory("Startup", "Streaming", "Binary")]
    [Benchmark]
    public async Task<int> Session_32KiB_OutputStreamBytes()
    {
        if (!supported)
            return 0;

        await using var session = Pty.Start(smallStdout);
        // The post-exit drain ends output on its own, so reading to the end needs no exit or drain orchestration.
        return await ReadOutputStreamAsync(session.Output).ConfigureAwait(false);
    }

    private static async Task<int> ReadOutputStreamAsync(Stream output)
    {
        using var bytes = PtyReadBuffer.RentBytes();
        var length = 0;
        while (true)
        {
            var read = await output.ReadAsync(bytes.Memory).ConfigureAwait(false);
            if (read <= 0)
                break;

            length += read;
        }

        return length;
    }

    [BenchmarkCategory("Startup", "Binary")]
    [Benchmark]
    public async Task<int> Capture_32KiB_Bytes()
    {
        if (!supported)
            return 0;

        var result = await PtyCapture.RunAsync(smallStdout, BenchmarkOptions.CaptureBytesOnly).ConfigureAwait(false);
        return result.Output.Length;
    }

    [BenchmarkCategory("Startup", "Text")]
    [Benchmark]
    public async Task<int> Session_Echo_Text()
    {
        if (!supported)
            return 0;

        await using var session = Pty.Start(echo);
        var result = await session.CompleteAsync(BenchmarkOptions.TextDecoded).ConfigureAwait(false);
        return result.GetText().Length;
    }

    [BenchmarkCategory("Startup", "Text")]
    [Benchmark]
    public async Task<int> Capture_Echo_Text()
    {
        if (!supported)
            return 0;

        var result = await PtyCapture.RunAsync(echo, BenchmarkOptions.CaptureTextDecoded).ConfigureAwait(false);
        return result.GetText().Length;
    }

    [BenchmarkCategory("Startup", "Text")]
    [Benchmark]
    public async Task<int> Capture_32KiB_Text()
    {
        if (!supported)
            return 0;

        var result = await PtyCapture.RunAsync(smallStdout, BenchmarkOptions.CaptureTextDecoded).ConfigureAwait(false);
        return result.GetText().Length;
    }

    [BenchmarkCategory("Startup", "Text")]
    [Benchmark]
    public async Task<int> Capture_32KiB_DisplayPlain()
    {
        if (!supported)
            return 0;

        var result = await PtyCapture.RunAsync(smallStdout, BenchmarkOptions.CaptureTextDecoded).ConfigureAwait(false);
        return result.ToDisplayText(PtyOutputDisplayMode.PlainText).Length;
    }
}
