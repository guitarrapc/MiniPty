using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using MiniPty.Capture;
using MiniPty.Internal;

namespace MiniPty.Benchmarks;

/// <summary>
/// End-to-end PTY benchmarks split by output path.
/// </summary>
/// <remarks>
/// <para><b>Binary</b> — <see cref="MiniPty.PtyCompleteOptions.DecodeOutput"/> = false; only merged bytes are retained.</para>
/// <para><b>Text</b> — default decode during pump (bytes + decoded chars) and display helpers.</para>
/// Allocations include OS process spawn. Capture cost scales with <b>PTY read count</b>, not only merged byte length.
/// Compare <see cref="Session_32KiB_Bytes"/> vs <see cref="Capture_32KiB_Bytes"/> to isolate per-read chunk metadata.
/// Bulk stdout scenarios use the shared <c>MiniPty.Benchmarks.Child</c> helper on all platforms.
/// </remarks>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[SimpleJob(RuntimeMoniker.Net10_0, warmupCount: 2, iterationCount: 5, launchCount: 1)]
public class PtyIntegrationBenchmarks
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

    [BenchmarkCategory("Integration", "Binary")]
    [Benchmark]
    public async Task<int> Session_Exit0_Bytes()
    {
        if (!supported)
            return 0;

        await using var session = Pty.Start(exit0);
        var result = await session.CompleteAsync(BenchmarkOptions.BytesOnly).ConfigureAwait(false);
        return result.ExitCode;
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
    public async Task<int> Session_32KiB_Bytes()
    {
        if (!supported)
            return 0;

        await using var session = Pty.Start(smallStdout);
        var result = await session.CompleteAsync(BenchmarkOptions.BytesOnly).ConfigureAwait(false);
        return result.Output.Length;
    }

    [BenchmarkCategory("Integration", "Streaming", "Binary")]
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

    [BenchmarkCategory("Integration", "Streaming", "Binary")]
    [Benchmark]
    public async Task<int> Session_32KiB_OutputStreamBytes()
    {
        if (!supported)
            return 0;

        await using var session = Pty.Start(smallStdout);
        var readTask = ReadOutputStreamAsync(session.Output);
        await session.WaitForExitInternalAsync(CancellationToken.None, killOnCancellation: false).ConfigureAwait(false);
        return await PtyOutputDrain.AwaitPumpAsync(
            readTask,
            session.Output,
            session.CloseOutputTransport,
            BenchmarkOptions.BytesOnly.OutputDrainGrace,
            BenchmarkOptions.BytesOnly.OutputReaderCloseTimeout,
            throwOnTimeout: true,
            transportAlreadyClosed: false,
            CancellationToken.None).ConfigureAwait(false);
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
