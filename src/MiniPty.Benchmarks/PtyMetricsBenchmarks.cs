using System.Diagnostics.Metrics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;

namespace MiniPty.Benchmarks;

/// <summary>
/// What MiniPty's instruments cost on the output path: the 1 MiB flood of <see cref="PtyThroughputBenchmarks"/>, with
/// nothing listening to the <c>MiniPty</c> meter and with a <see cref="MeterListener"/> that enables every instrument.
/// </summary>
/// <remarks>
/// <para>
/// Without a listener each instrument costs a check of its <c>Enabled</c> flag. With one, every read adds to
/// <c>minipty.output.bytes</c>, every stop on a full ring and every consumer's native wait (Unix) takes two timestamps
/// and records a histogram value, and each measurement reaches the listener's callback. The callbacks here do nothing,
/// so the difference is MiniPty's side of the cost. An exporter adds its own aggregation to it.
/// </para>
/// <para>
/// No measurement on the output path carries tags, so recording allocates nothing with or without the listener. The
/// allocation column also shows what the flood itself allocates. On Windows the flood runs on the in-box ConPTY only,
/// since the instruments do not depend on the console host.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[ThreadingDiagnoser]
[Config(typeof(Config))]
public class PtyMetricsBenchmarks
{
    private BenchmarkServeSession? serve;
    private MeterListener? listener;
    private bool second;

    /// <summary>Whether a <see cref="MeterListener"/> enables every MiniPty instrument during the run.</summary>
    [Params(false, true)]
    public bool Listener { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        if (!BenchmarkPtyCommands.IsSupported)
            return;

        if (Listener)
        {
            listener = new MeterListener
            {
                InstrumentPublished = static (instrument, meterListener) =>
                {
                    if (instrument.Meter.Name == "MiniPty")
                        meterListener.EnableMeasurementEvents(instrument);
                },
            };
            listener.SetMeasurementEventCallback<long>(static (_, _, _, _) => { });
            listener.SetMeasurementEventCallback<double>(static (_, _, _, _) => { });
            listener.Start();
        }

        var conPty = BenchmarkPtyCommands.ConPtyChoices()[0];
        serve = BenchmarkServeSession.Start(ServeLibrary.MiniPty, 1_048_576, conPty);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        serve?.Dispose();
        listener?.Dispose();
    }

    [BenchmarkCategory("Steady", "Metrics")]
    [Benchmark]
    public ValueTask<long> Flood()
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
        public Config()
        {
            SteadyStateJobs.Add(this, Job.Default
                .WithRuntime(CoreRuntime.Core10_0)
                .WithLaunchCount(1)
                .WithWarmupCount(3)
                .WithIterationCount(10));
            AddDiagnoser(new ProcessCpuDiagnoser());
        }
    }
}
