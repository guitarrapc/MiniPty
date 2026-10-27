using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace MiniPty.Benchmarks;

/// <summary>
/// Steady-state output throughput: how fast a running child's output reaches the reader through the PTY, on MiniPty
/// and on Porta.Pty.
/// </summary>
/// <remarks>
/// <para>
/// One child (<c>MiniPty.Benchmarks.Child --serve</c>) lives for the whole run, so spawn and exit are outside the
/// measurement; <see cref="PtyStartupBenchmarks"/> covers those. Each operation writes one trigger byte and reads
/// <see cref="PayloadBytes"/> of 77-character lines plus an end marker through the library's read.
/// The <c>MiB/s</c> column is <see cref="PayloadBytes"/> divided by the mean, so it includes the trigger's way in.
/// </para>
/// <para>
/// Each method is one library on the same child (<see cref="BenchmarkServeSession"/>): <see cref="MiniPty"/> is the
/// baseline, <see cref="PortaPty"/> is Porta.Pty as it starts by default, and <see cref="PortaPtyAsyncIo"/> is Porta.Pty
/// with <c>UseAsyncIo</c>.
/// </para>
/// <para>
/// On Windows the bytes read are not the bytes written: ConPTY renders the child's output and sends its own VT
/// stream, so the throughput there is that of ConPTY's renderer, not of a byte pipe. The operation returns the bytes
/// it read so the two can be compared. <see cref="ConPty"/> runs it on the ConPTY Windows ships and on the out-of-band
/// one, which renders about twice as fast; the in-box one occasionally withholds a finished flood until the screen
/// changes again, which the harness nudges and reports (<see cref="BenchmarkServeSession"/>).
/// </para>
/// <para>
/// An operation is one pooled async round trip (<see cref="BenchmarkServeSession.RoundTripAsync"/>), so the allocation
/// column shows each library's own cost per flood rather than the harness's async state.
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

    /// <summary>The ConPTY implementation on Windows (both when the out-of-band one is staged); <c>none</c> elsewhere.</summary>
    [ParamsSource(nameof(ConPtyChoices))]
    public string ConPty { get; set; } = BenchmarkPtyCommands.ConPtyNone;

    public static IEnumerable<string> ConPtyChoices() => BenchmarkPtyCommands.ConPtyChoices();

    [GlobalSetup(Target = nameof(MiniPty))]
    public void SetupMiniPty() => Setup(ServeLibrary.MiniPty);

    [GlobalSetup(Target = nameof(PortaPty))]
    public void SetupPortaPty() => Setup(ServeLibrary.PortaPty);

    [GlobalSetup(Target = nameof(PortaPtyAsyncIo))]
    public void SetupPortaPtyAsyncIo() => Setup(ServeLibrary.PortaPtyAsyncIo);

    [GlobalCleanup]
    public void Cleanup() => serve?.Dispose();

    [BenchmarkCategory("Steady", "Throughput")]
    [Benchmark(Baseline = true)]
    public ValueTask<long> MiniPty() => ReadOutput();

    [BenchmarkCategory("Steady", "Throughput")]
    [Benchmark]
    public ValueTask<long> PortaPty() => ReadOutput();

    [BenchmarkCategory("Steady", "Throughput")]
    [Benchmark]
    public ValueTask<long> PortaPtyAsyncIo() => ReadOutput();

    private void Setup(ServeLibrary library)
    {
        if (BenchmarkPtyCommands.IsSupported)
            serve = BenchmarkServeSession.Start(library, PayloadBytes, ConPty);
    }

    private ValueTask<long> ReadOutput()
    {
        if (serve is null)
            return new ValueTask<long>(0L);

        // Alternate the end marker, so a repaint of the previous one cannot end this operation early.
        second = !second;
        return second
            ? serve.RoundTripAsync(BenchmarkServeSession.FloodB, BenchmarkServeSession.EndB)
            : serve.RoundTripAsync(BenchmarkServeSession.FloodA, BenchmarkServeSession.EndA);
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
