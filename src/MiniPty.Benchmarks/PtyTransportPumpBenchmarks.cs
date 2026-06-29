using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using MiniPty.Internal;

namespace MiniPty.Benchmarks;

/// <summary>
/// Isolates transport pump scheduling cost: <see cref="Task.Run{TResult}(Func{TResult}, CancellationToken)"/>
/// vs macOS <see cref="PtyTransportPumpTask.Run{T}"/>.
/// Run on macOS to compare against integration echo benchmarks.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0, warmupCount: 3, iterationCount: 10, launchCount: 1)]
public class PtyTransportPumpBenchmarks
{
    [Benchmark]
    public async Task<int> TaskRun_EmptyPump()
    {
        return await Task.Run(() => 0, CancellationToken.None).ConfigureAwait(false);
    }

    [Benchmark]
    public async Task<int> PreferLocalPump_EmptyPump()
    {
        return await PtyTransportPumpTask.Run(_ => 0, CancellationToken.None).ConfigureAwait(false);
    }
}
