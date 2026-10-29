using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;

namespace MiniPty.Benchmarks;

/// <summary>
/// The jobs of the steady-state benchmarks: each runs on the whole machine (<see cref="AllCpus"/>) and again on two
/// logical CPUs (<see cref="TwoCpus"/>).
/// </summary>
/// <remarks>
/// <para>
/// A library that hands each chunk of output to another thread is fast only while an idle thread-pool thread is
/// spinning when the chunk arrives. With CPUs to spare that is almost always so. On CI's hosted runners, which have 2
/// cores and 4 threads, the child, the kernel's tty work or the ConPTY host, and the library's threads share the CPUs,
/// spinning threads are preempted, and a hand-off becomes a sleep and a wake. Floods that won on a 16-core machine
/// lost on CI for that reason. The <see cref="TwoCpus"/> job brings that scarcity to any machine, so a change can be
/// judged on both before it reaches CI.
/// </para>
/// <para>
/// BenchmarkDotNet sets the affinity of the benchmark process right after starting it. On Windows that holds for every
/// thread of the process; on Linux it holds for the main thread, and every thread started after it inherits it. The
/// serve child and, on Windows, the ConPTY host start later from the benchmark process and inherit it too. Logical
/// CPUs 0 and 1 are one core's two hardware threads on Hyper-V guests, as on CI's runners and in WSL, and two cores
/// where the OS numbers hardware threads apart.
/// </para>
/// <para>
/// The job runs only where it differs from the whole machine: on Windows and Linux (BenchmarkDotNet sets no affinity
/// on macOS) with more than two logical CPUs.
/// </para>
/// </remarks>
internal static class SteadyStateJobs
{
    /// <summary>The job id of the runs on every CPU the machine gives the benchmark.</summary>
    internal const string AllCpus = "AllCpus";

    /// <summary>The job id of the runs on logical CPUs 0 and 1.</summary>
    internal const string TwoCpus = "TwoCpus";

    private static readonly nint TwoCpusMask = 0b11;

    /// <summary>Adds <paramref name="job"/> to <paramref name="config"/> as <see cref="AllCpus"/> and, where it differs, as <see cref="TwoCpus"/>.</summary>
    internal static void Add(ManualConfig config, Job job)
    {
        config.AddJob(job.WithId(AllCpus));
        if ((OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) && Environment.ProcessorCount > 2)
            config.AddJob(job.WithAffinity(TwoCpusMask).WithId(TwoCpus));
    }
}
