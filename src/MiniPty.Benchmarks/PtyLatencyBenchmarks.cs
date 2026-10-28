using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using Perfolizer.Mathematics.OutlierDetection;

namespace MiniPty.Benchmarks;

/// <summary>
/// Keystroke round trip on a running session: one byte written to the terminal until the child's echo of it is read,
/// on MiniPty and on Porta.Pty.
/// </summary>
/// <remarks>
/// <para>
/// One child (<c>MiniPty.Benchmarks.Child --serve</c>) lives for the whole run with its input raw, as a shell or an
/// editor runs, and echoes each byte itself; spawn and exit are outside the measurement. The path is the library's
/// write, the terminal's input side, the child's read and write, the terminal's output side, and the library's read.
/// On Windows that includes ConPTY parsing the input into a key event and rendering the echo, and <see cref="ConPty"/>
/// runs it on both implementations: the in-box one paints on its own frame schedule, which sets the round trip there
/// for any library.
/// </para>
/// <para>
/// Each method is one library on the same child (<see cref="BenchmarkServeSession"/>): <see cref="MiniPty"/> is the
/// baseline, <see cref="PortaPty"/> is Porta.Pty as it starts by default (each read holds a thread-pool thread), and
/// <see cref="PortaPtyAsyncIo"/> is Porta.Pty with <c>UseAsyncIo</c> (no thread held while idle), the mode closest to
/// MiniPty's design.
/// </para>
/// <para>
/// Each iteration is one round trip (invocation count 1), so the percentile columns are percentiles of single
/// keystrokes rather than of averages, and outliers are kept because the slow keystrokes are the ones a user notices.
/// BenchmarkDotNet's warning that the iteration time is small does not apply: one round trip is what is measured, and
/// it lasts far longer than the timer's resolution. The echoed byte alternates so a repaint of the previous echo cannot
/// end a round trip early.
/// </para>
/// <para>
/// An operation is one pooled async round trip (<see cref="BenchmarkServeSession.RoundTripAsync"/>), so the allocation
/// column shows each library's own cost per keystroke rather than the harness's async state.
/// </para>
/// <para>
/// The run turns tiered compilation off (<c>DOTNET_TieredCompilation=0</c>, which the serve child inherits too), so each
/// method is compiled once, fully optimized, before the first measured keystroke. With tiering on, the runtime recompiles
/// the hot methods in the background about 100 ms after the last new method, which lands inside the measured iterations
/// however long the warmup: on 2 to 4 CPUs those compiles took the CPU from the round trip, and 1 to 2.5 ms keystrokes
/// matched them one for one, for every library, which one depending on when its run happened. That is the cost of
/// starting a process, not of a running session.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[Outliers(OutlierMode.DontRemove)]
[Config(typeof(Config))]
public class PtyLatencyBenchmarks
{
    private BenchmarkServeSession? serve;
    private bool second;

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

    [BenchmarkCategory("Steady", "Latency")]
    [Benchmark(Baseline = true)]
    public ValueTask<long> MiniPty() => KeystrokeEcho();

    [BenchmarkCategory("Steady", "Latency")]
    [Benchmark]
    public ValueTask<long> PortaPty() => KeystrokeEcho();

    [BenchmarkCategory("Steady", "Latency")]
    [Benchmark]
    public ValueTask<long> PortaPtyAsyncIo() => KeystrokeEcho();

    private void Setup(ServeLibrary library)
    {
        if (BenchmarkPtyCommands.IsSupported)
            serve = BenchmarkServeSession.Start(library, 0, ConPty);
    }

    private ValueTask<long> KeystrokeEcho()
    {
        if (serve is null)
            return new ValueTask<long>(0L);

        second = !second;
        var key = second ? BenchmarkServeSession.KeyB : BenchmarkServeSession.KeyA;
        return serve.RoundTripAsync(key, key);
    }

    private sealed class Config : ManualConfig
    {
        public Config()
        {
            AddJob(Job.Default
                .WithRuntime(CoreRuntime.Core10_0)
                .WithLaunchCount(1)
                .WithWarmupCount(20)
                .WithIterationCount(200)
                .WithInvocationCount(1)
                .WithUnrollFactor(1)
                .WithEnvironmentVariable("DOTNET_TieredCompilation", "0"));
            AddColumn(StatisticColumn.P50, StatisticColumn.P95, StatisticColumn.Max);
        }
    }
}
