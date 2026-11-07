using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;

namespace MiniPty.Benchmarks;

/// <summary>
/// Where a steady-state flood's allocations come from: an opt-in probe, not a BenchmarkDotNet benchmark.
/// <c>dotnet run -c Release --project src/MiniPty.Benchmarks -- --alloc-probe &lt;none|in-box|out-of-band&gt; [listener 0|1] [rounds] [payload bytes] [warm-up rounds] [list threads 1|0]</c>.
/// </summary>
/// <remarks>
/// <para>
/// BenchmarkDotNet's allocation column counts every thread's allocations over one iteration, which for a flood of a
/// quarter of a second is one or two operations. A few bytes per operation there may be an allocation of the flood, or
/// a thread the process added meanwhile: the first managed code on a new thread allocates its <see cref="Thread"/>
/// object and more, about a kilobyte. The column cannot tell the two apart.
/// </para>
/// <para>
/// This probe runs the 1 MiB flood of <see cref="PtyThroughputBenchmarks"/> and <see cref="PtyMetricsBenchmarks"/>
/// round after round, through the same <see cref="BenchmarkServeSession"/>, and waits for each round on its own thread
/// as BenchmarkDotNet does. Around each round it reads the process's allocated bytes (all threads, precise), the bytes
/// the waiting thread allocated, and the thread pool's thread count. After each round, outside the measured window, it
/// lists the process's operating-system threads and counts the ones it has not seen before. A round that allocated while
/// a thread appeared points at the thread; a round that allocated with no new thread points at an allocation to find.
/// With <c>listener</c> 1, a <see cref="MeterListener"/> enables every MiniPty instrument, as
/// <see cref="PtyMetricsBenchmarks"/> does with <c>Listener</c> true.
/// </para>
/// </remarks>
internal static class AllocProbe
{
    private const int DefaultWarmUpRounds = 20;

    // The wait for a round, as BenchmarkDotNet blocks on a ValueTask: a continuation sets the event, so waiting
    // allocates nothing.
    private static readonly ManualResetEventSlim s_roundDone = new();
    private static readonly Action s_signalRoundDone = static () => s_roundDone.Set();

    internal static int Run(string[] args)
    {
        var conPty = args.Length > 0 ? args[0] : BenchmarkPtyCommands.ConPtyChoices()[0];
        var listen = args.Length > 1 && args[1] == "1";
        var rounds = Argument(args, 2, 200);
        var payload = Argument(args, 3, 1_048_576);
        var warmUpRounds = Argument(args, 4, DefaultWarmUpRounds);
        // Listing the threads allocates the same common types (arrays, strings) just before each round, and an allocation
        // trace samples a type less often right after many of its allocations. A traced run lists none and shows threads
        // in the trace instead.
        var listThreads = !(args.Length > 5 && args[5] == "0");
        if (!BenchmarkPtyCommands.IsSupported)
        {
            System.Console.Error.WriteLine("PTY is not supported on this platform.");
            return 1;
        }

        if (Array.IndexOf(BenchmarkPtyCommands.ConPtyChoices(), conPty) < 0)
        {
            System.Console.Error.WriteLine(
                $"ConPty '{conPty}' is not available here; choose from: {string.Join(", ", BenchmarkPtyCommands.ConPtyChoices())}.");
            return 2;
        }

        using var listener = listen ? StartListener() : null;
        using var serve = BenchmarkServeSession.Start(ServeLibrary.MiniPty, payload, conPty);
        var seenThreads = new HashSet<int>();
        CountNewThreads(seenThreads);

        var second = false;
        for (var i = 0; i < warmUpRounds; i++)
        {
            second = !second;
            Flood(serve, second);
        }

        CountNewThreads(seenThreads);
        System.Console.WriteLine(
            $"alloc-probe: ConPty {conPty}, listener {(listen ? "on" : "off")}, {payload} bytes per round, {rounds} rounds after {warmUpRounds} warm-up rounds");

        long totalBytes = 0, bytesWithNewThreads = 0, waiterBytes = 0;
        int allocatingRounds = 0, roundsWithNewThreads = 0, allocatingRoundsWithNewThreads = 0;
        var roundsElapsed = Stopwatch.StartNew();
        for (var round = 1; round <= rounds; round++)
        {
            second = !second;
            var poolBefore = ThreadPool.ThreadCount;
            var workItemsBefore = ThreadPool.CompletedWorkItemCount;
            var waiterBefore = GC.GetAllocatedBytesForCurrentThread();
            var roundStarted = Stopwatch.GetTimestamp();
            var before = GC.GetTotalAllocatedBytes(precise: true);
            Flood(serve, second);
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
            var roundEnded = Stopwatch.GetTimestamp();
            var waiter = GC.GetAllocatedBytesForCurrentThread() - waiterBefore;
            var poolAfter = ThreadPool.ThreadCount;
            var workItems = ThreadPool.CompletedWorkItemCount - workItemsBefore;
            // Outside the measured window: listing the threads allocates.
            var newThreads = listThreads ? CountNewThreads(seenThreads) : 0;

            totalBytes += allocated;
            waiterBytes += waiter;
            if (newThreads > 0)
            {
                roundsWithNewThreads++;
                bytesWithNewThreads += allocated;
            }

            if (allocated == 0)
                continue;

            allocatingRounds++;
            if (newThreads > 0)
                allocatingRoundsWithNewThreads++;

            System.Console.WriteLine(
                $"  round {round}: {allocated} B ({waiter} B on the waiting thread: {s_callBytes} in the call, {s_waitBytes} waiting, "
                + $"{s_resultBytes} taking the result), {newThreads} new OS threads, pool threads {poolBefore} -> {poolAfter}, "
                + $"{workItems} work items; Stopwatch {roundStarted}..{roundEnded}, for matching a trace");
        }

        System.Console.WriteLine(
            $"alloc-probe: {totalBytes} B over {rounds} rounds ({(double)totalBytes / rounds:F2} B per round, {waiterBytes} B on the waiting thread), "
            + $"{allocatingRounds} rounds allocated, {allocatingRoundsWithNewThreads} of them with a new OS thread; "
            + $"{roundsWithNewThreads} rounds added threads, which allocated {bytesWithNewThreads} B; "
            + $"{totalBytes - bytesWithNewThreads} B in rounds without a new thread; {roundsElapsed.Elapsed.TotalSeconds:F1} s");
        return 0;
    }

    // The waiting thread's bytes in the last round's three parts: the call until it returned (the round's synchronous
    // start, up to its first wait), the wait, and taking the result.
    private static long s_callBytes, s_waitBytes, s_resultBytes;

    private static long Flood(BenchmarkServeSession serve, bool second)
    {
        // Alternate the end marker, as the benchmarks do, so a repaint of the previous one cannot end a round early.
        var started = GC.GetAllocatedBytesForCurrentThread();
        var round = second
            ? serve.RoundTripAsync(BenchmarkServeSession.FloodB, BenchmarkServeSession.EndB)
            : serve.RoundTripAsync(BenchmarkServeSession.FloodA, BenchmarkServeSession.EndA);
        var called = GC.GetAllocatedBytesForCurrentThread();
        if (!round.IsCompleted)
        {
            s_roundDone.Reset();
            round.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(s_signalRoundDone);
            s_roundDone.Wait();
        }

        var waited = GC.GetAllocatedBytesForCurrentThread();
        var result = round.GetAwaiter().GetResult();
        var ended = GC.GetAllocatedBytesForCurrentThread();
        s_callBytes = called - started;
        s_waitBytes = waited - called;
        s_resultBytes = ended - waited;
        return result;
    }

    /// <summary>The process's operating-system threads not in <paramref name="seen"/>, which it then holds too.</summary>
    private static int CountNewThreads(HashSet<int> seen)
    {
        using var process = Process.GetCurrentProcess();
        var added = 0;
        foreach (ProcessThread thread in process.Threads)
        {
            if (seen.Add(thread.Id))
                added++;
            thread.Dispose();
        }

        return added;
    }

    /// <summary>A listener that enables every MiniPty instrument, as <see cref="PtyMetricsBenchmarks"/> starts one.</summary>
    private static MeterListener StartListener()
    {
        var listener = new MeterListener
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
        return listener;
    }

    private static int Argument(string[] args, int index, int fallback) =>
        args.Length > index && int.TryParse(args[index], NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : fallback;
}
