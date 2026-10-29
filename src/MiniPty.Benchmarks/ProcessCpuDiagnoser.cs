using System.Diagnostics;
using BenchmarkDotNet.Analysers;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Validators;

namespace MiniPty.Benchmarks;

/// <summary>
/// Adds a <c>Process CPU</c> column: the CPU time the benchmark process spent per operation over the measured
/// iterations, on all of its threads.
/// </summary>
/// <remarks>
/// <para>
/// The time column alone cannot tell a library that does the work on one thread from one that hands it between
/// threads and waits for them. With CPUs to spare the two can take the same time while one uses several times the CPU
/// of the other, and on a runner with 2 cores that CPU shows up as time. This column shows the cost on any machine.
/// </para>
/// <para>
/// The figure covers every thread of the benchmark process: the library, the harness, and the runtime (GC, JIT). It
/// does not include the serve child or the ConPTY host, which do the same work for every library. BenchmarkDotNet
/// forces a full collection around each iteration, which this figure includes. That is negligible when an iteration
/// holds many operations, as in the throughput benchmarks, but it would dominate a keystroke that is an iteration of
/// its own, so the latency benchmarks do not use this diagnoser.
/// </para>
/// <para>
/// The host process reads the benchmark process's CPU time when the benchmark reports that the measured iterations
/// begin and end. The benchmark waits for the host to handle each report before it continues, so the reading covers
/// exactly those iterations. The OS counts CPU time in scheduler ticks (10 ms on Linux, 15.6 ms on Windows), which
/// is small next to the seconds of CPU that ten measured iterations take.
/// </para>
/// </remarks>
internal sealed class ProcessCpuDiagnoser : IDiagnoser
{
    // Host side only. BenchmarkDotNet runs one benchmark process at a time, so one start reading is enough.
    private readonly Dictionary<BenchmarkCase, TimeSpan> measured = [];
    private TimeSpan start;

    public IEnumerable<string> Ids => [nameof(ProcessCpuDiagnoser)];

    public IEnumerable<IExporter> Exporters => [];

    public IEnumerable<IAnalyser> Analysers => [];

    public RunMode GetRunMode(BenchmarkCase benchmarkCase) => RunMode.NoOverhead;

    public void Handle(HostSignal signal, DiagnoserActionParameters parameters)
    {
        if (signal == HostSignal.BeforeActualRun)
            start = CpuTime(parameters.Process);
        else if (signal == HostSignal.AfterActualRun)
            measured[parameters.BenchmarkCase] = CpuTime(parameters.Process) - start;
    }

    public IEnumerable<Metric> ProcessResults(DiagnoserResults results)
    {
        if (!measured.TryGetValue(results.BenchmarkCase, out var cpu))
            yield break;

        // Only the measured iterations, which the readings above bracket; warmup and pilot iterations are workload too.
        long operations = 0;
        foreach (var measurement in results.Measurements)
        {
            if (measurement.IterationMode == IterationMode.Workload && measurement.IterationStage == IterationStage.Actual)
                operations += measurement.Operations;
        }

        if (operations > 0)
            yield return new Metric(ProcessCpuMetric.Instance, cpu.TotalNanoseconds / operations);
    }

    public void DisplayResults(ILogger logger)
    {
    }

    public IEnumerable<ValidationError> Validate(ValidationParameters validationParameters) => [];

    private static TimeSpan CpuTime(Process process)
    {
        process.Refresh();
        return process.TotalProcessorTime;
    }

    private sealed class ProcessCpuMetric : IMetricDescriptor
    {
        public static readonly ProcessCpuMetric Instance = new();

        public string Id => "ProcessCpu";
        public string DisplayName => "Process CPU";
        public string Legend => "CPU time of the benchmark process per operation over the measured iterations, all threads (not the child or the ConPTY host)";
        public string NumberFormat => "N2";
        public UnitType UnitType => UnitType.Time;
        public string Unit => "ns";
        public bool TheGreaterTheBetter => false;
        public int PriorityInCategory => 0;

        public bool GetIsAvailable(Metric metric) => true;
    }
}
