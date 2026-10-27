using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using Perfolizer.Mathematics.OutlierDetection;

namespace MiniPty.Benchmarks;

/// <summary>
/// Keystroke round trip on a running session: one byte written to the terminal until the child's echo of it is read.
/// </summary>
/// <remarks>
/// <para>
/// One child (<c>MiniPty.Benchmarks.Child --serve</c>) lives for the whole run with its input raw, as a shell or an
/// editor runs, and echoes each byte itself; spawn and exit are outside the measurement. The path is
/// <see cref="PtySession.WriteInputAsync(ReadOnlyMemory{byte}, CancellationToken)"/>, the terminal's input side, the
/// child's read and write, the terminal's output side, and <see cref="PtySession.ReadOutputAsync"/>. On Windows that
/// includes ConPTY parsing the input into a key event and rendering the echo.
/// </para>
/// <para>
/// Each iteration is one round trip (invocation count 1), so the percentile columns are percentiles of single
/// keystrokes rather than of averages, and outliers are kept because the slow keystrokes are the ones a user notices.
/// BenchmarkDotNet's warning that the iteration time is small does not apply: one round trip is what is measured, and
/// it lasts far longer than the timer's resolution. The echoed byte alternates so a repaint of the previous echo cannot
/// end a round trip early.
/// </para>
/// <para>
/// The operation and the harness's read loop use pooled <see cref="ValueTask"/> builders, so the allocation column
/// shows MiniPty's own cost per keystroke rather than the harness's async state.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[Outliers(OutlierMode.DontRemove)]
[Config(typeof(Config))]
[SimpleJob(RuntimeMoniker.Net10_0, launchCount: 1, warmupCount: 20, iterationCount: 200, invocationCount: 1)]
public class PtyLatencyBenchmarks
{
    private BenchmarkServeSession? serve;
    private bool second;

    [GlobalSetup]
    public void Setup()
    {
        if (BenchmarkPtyCommands.IsSupported)
            serve = BenchmarkServeSession.Start(BenchmarkPtyCommands.Serve(0));
    }

    [GlobalCleanup]
    public void Cleanup() => serve?.Dispose();

    [BenchmarkCategory("Steady", "Latency")]
    [Benchmark]
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<long> KeystrokeEcho()
    {
        if (serve is null)
            return 0;

        second = !second;
        var key = second ? BenchmarkServeSession.KeyB : BenchmarkServeSession.KeyA;
        await serve.WriteAsync(key).ConfigureAwait(false);
        return await serve.ReadUntilAsync(key).ConfigureAwait(false);
    }

    private sealed class Config : ManualConfig
    {
        public Config() => AddColumn(StatisticColumn.P50, StatisticColumn.P95, StatisticColumn.Max);
    }
}
