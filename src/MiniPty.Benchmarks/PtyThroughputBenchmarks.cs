using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace MiniPty.Benchmarks;

/// <summary>
/// Steady-state output throughput: how fast a running child's output reaches the reader through the PTY.
/// </summary>
/// <remarks>
/// <para>
/// One child (<c>MiniPty.Benchmarks.Child --serve</c>) lives for the whole run, so spawn and exit are outside the
/// measurement; <see cref="PtyStartupBenchmarks"/> covers those. Each operation writes one trigger byte and reads
/// <see cref="PayloadBytes"/> of 77-character lines plus an end marker through <see cref="PtySession.ReadOutputAsync"/>.
/// The <c>MiB/s</c> column is <see cref="PayloadBytes"/> divided by the mean, so it includes the trigger's way in.
/// </para>
/// <para>
/// On Windows the bytes read are not the bytes written: ConPTY renders the child's output and sends its own VT
/// stream, so the throughput there is that of ConPTY's renderer, not of a byte pipe. The operation returns the bytes
/// it read so the two can be compared.
/// </para>
/// <para>
/// The operation and the harness's read loop use pooled <see cref="ValueTask"/> builders, so the allocation column
/// shows MiniPty's own cost per flood rather than the harness's async state.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[Config(typeof(Config))]
[SimpleJob(RuntimeMoniker.Net10_0, warmupCount: 3, iterationCount: 10, launchCount: 1)]
public class PtyThroughputBenchmarks
{
    private BenchmarkServeSession? serve;
    private bool second;

    /// <summary>Bytes the child writes per operation, before the end marker.</summary>
    [Params(65_536, 1_048_576)]
    public int PayloadBytes { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        if (BenchmarkPtyCommands.IsSupported)
            serve = BenchmarkServeSession.Start(BenchmarkPtyCommands.Serve(PayloadBytes));
    }

    [GlobalCleanup]
    public void Cleanup() => serve?.Dispose();

    [BenchmarkCategory("Steady", "Throughput")]
    [Benchmark]
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<long> ReadOutput()
    {
        if (serve is null)
            return 0;

        // Alternate the end marker, so a repaint of the previous one cannot end this operation early.
        second = !second;
        await serve.WriteAsync(second ? BenchmarkServeSession.FloodB : BenchmarkServeSession.FloodA).ConfigureAwait(false);
        return await serve.ReadUntilAsync(second ? BenchmarkServeSession.EndB : BenchmarkServeSession.EndA).ConfigureAwait(false);
    }

    private sealed class Config : ManualConfig
    {
        public Config() => AddColumn(new ThroughputColumn());
    }

    /// <summary>Payload bytes per second of mean operation time, in MiB/s.</summary>
    private sealed class ThroughputColumn : IColumn
    {
        public string Id => nameof(ThroughputColumn);
        public string ColumnName => "MiB/s";
        public bool AlwaysShow => true;
        public ColumnCategory Category => ColumnCategory.Statistics;
        public int PriorityInCategory => 100;
        public bool IsNumeric => true;
        public UnitType UnitType => UnitType.Dimensionless;
        public string Legend => "Payload bytes divided by the mean operation time, in MiB per second";

        public string GetValue(Summary summary, BenchmarkCase benchmarkCase) =>
            GetValue(summary, benchmarkCase, summary.Style);

        public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
        {
            var statistics = summary[benchmarkCase]?.ResultStatistics;
            if (statistics is null || statistics.Mean <= 0 || benchmarkCase.Parameters[nameof(PayloadBytes)] is not int bytes)
                return "NA";

            var seconds = statistics.Mean / 1_000_000_000d;
            return (bytes / seconds / (1024 * 1024)).ToString("N1", style.CultureInfo);
        }

        public bool IsAvailable(Summary summary) => true;

        public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;
    }
}
