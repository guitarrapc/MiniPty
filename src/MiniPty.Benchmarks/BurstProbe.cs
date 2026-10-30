using System.Diagnostics;
using System.Globalization;
using MiniPty.Internal;

namespace MiniPty.Benchmarks;

/// <summary>
/// What sparse bursts of output cost the thread pool: an opt-in probe, not a BenchmarkDotNet benchmark.
/// <c>dotnet run -c Release --project src/MiniPty.Benchmarks -- --burst-probe [sessions] [bytes] [interval ms] [bursts]</c>.
/// </summary>
/// <remarks>
/// <para>
/// Each session runs the benchmark child's burst mode (<c>--burst</c>), which writes <c>bytes</c> every <c>interval ms</c>,
/// <c>bursts</c> times, and is read through one <c>ReadOutputAsync</c> enumeration. Meanwhile a probe queues a thread-pool
/// work item every 2 ms and records how long it waited to run: a consumer that blocks a pool thread after each burst, as a
/// wait for the master does when the child has stopped, shows there as queueing delay once there are more sessions than
/// idle pool threads. The report gives the probe's delay percentiles, the consumer waits made on their own thread, the
/// thread-pool work items and the process's CPU time over the run.
/// </para>
/// <para>
/// The defaults, 64 sessions writing 2 KiB every 5 ms, 200 times, are the case that found the cost on Linux: a burst of 1–4
/// KiB is all a TUI's redraw or a long log line writes, and a wait that starts after it finds nothing.
/// </para>
/// </remarks>
internal static class BurstProbe
{
    private const int ProbeIntervalMs = 2;

    internal static async Task<int> RunAsync(string[] args)
    {
        var sessions = Argument(args, 0, 64);
        var bytes = Argument(args, 1, 2048);
        var intervalMs = Argument(args, 2, 5);
        var bursts = Argument(args, 3, 200);
        if (!BenchmarkPtyCommands.IsSupported)
        {
            System.Console.Error.WriteLine("PTY is not supported on this platform.");
            return 1;
        }

        var child = BenchmarkPtyCommands.BenchmarkChildPath;
        var expected = (long)bytes * bursts;
        var delays = new List<double>(bursts * intervalMs / ProbeIntervalMs + 16);
        var delaysLock = new object();
        using var done = new CancellationTokenSource();

        var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var workItemsBefore = ThreadPool.CompletedWorkItemCount;
        var waitsBefore = OperatingSystem.IsWindows() ? 0 : UnixPtyBackend.ConsumerWaitsForTests;
        var started = Stopwatch.GetTimestamp();

        var readers = new Task<long>[sessions];
        for (var i = 0; i < sessions; i++)
        {
            readers[i] = Task.Run(async () =>
            {
                await using var session = Pty.Start(new PtyStartInfo
                {
                    FileName = child,
                    Arguments = ["--burst", bytes.ToString(CultureInfo.InvariantCulture), intervalMs.ToString(CultureInfo.InvariantCulture), bursts.ToString(CultureInfo.InvariantCulture)],
                    Size = new PtySize(BenchmarkPtyCommands.Columns, BenchmarkPtyCommands.Rows),
                });
                long received = 0;
                await foreach (var chunk in session.ReadOutputAsync())
                    received += chunk.Data.Span.Count((byte)'x');

                return received;
            });
        }

        var probe = Task.Run(async () =>
        {
            while (!done.IsCancellationRequested)
            {
                var queued = Stopwatch.GetTimestamp();
                ThreadPool.UnsafeQueueUserWorkItem(static state =>
                {
                    var (queuedAt, list, gate) = ((long, List<double>, object))state!;
                    var delay = Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds;
                    lock (gate)
                        list.Add(delay);
                }, (queued, delays, delaysLock));
                try
                {
                    await Task.Delay(ProbeIntervalMs, done.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }
        });

        var received = await Task.WhenAll(readers);
        done.Cancel();
        await probe;
        var elapsed = Stopwatch.GetElapsedTime(started);
        process.Refresh();
        var cpu = process.TotalProcessorTime - cpuBefore;
        var workItems = ThreadPool.CompletedWorkItemCount - workItemsBefore;
        var waits = OperatingSystem.IsWindows() ? 0 : UnixPtyBackend.ConsumerWaitsForTests - waitsBefore;

        double[] sorted;
        lock (delaysLock)
            sorted = delays.Order().ToArray();
        double Percentile(double q) => sorted.Length == 0 ? double.NaN : sorted[Math.Min(sorted.Length - 1, (int)(q * sorted.Length))];
        var short_ = received.Count(r => r != expected);

        System.Console.WriteLine($"Burst probe: {sessions} sessions x {bursts} bursts of {bytes} B every {intervalMs} ms, {elapsed.TotalMilliseconds:F0} ms");
        System.Console.WriteLine("| Probe p50 (ms) | Probe p99 (ms) | Probe max (ms) | Probes run | Consumer waits | Work items | Process CPU (ms) |");
        System.Console.WriteLine("|---|---|---|---|---|---|---|");
        System.Console.WriteLine(
            $"| {Percentile(0.5).ToString("F3", CultureInfo.InvariantCulture)} | {Percentile(0.99).ToString("F2", CultureInfo.InvariantCulture)} | " +
            $"{(sorted.Length == 0 ? double.NaN : sorted[^1]).ToString("F2", CultureInfo.InvariantCulture)} | {sorted.Length} | {waits} | {workItems} | " +
            $"{cpu.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)} |");
        if (short_ != 0)
        {
            System.Console.Error.WriteLine($"{short_} of {sessions} sessions did not deliver {expected} bytes.");
            return 1;
        }

        return 0;
    }

    private static int Argument(string[] args, int index, int fallback) =>
        args.Length > index && int.TryParse(args[index], NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : fallback;
}
