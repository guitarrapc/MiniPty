using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Porta.Pty;

namespace MiniPty.Benchmarks;

/// <summary>
/// What many idle shells cost the process that hosts them: an opt-in probe, not a BenchmarkDotNet benchmark.
/// <c>dotnet run -c Release --project src/MiniPty.Benchmarks -- --idle-probe [--library minipty|porta|porta-async] [--sessions N] [--idle-seconds N] [--ready-seconds N] [--report path.csv]</c>.
/// </summary>
/// <remarks>
/// <para>
/// A browser terminal, an editor, or an agent host keeps many shells open, and most of them wait for a person most of
/// the time. Each session here starts the OS shell (<c>cmd.exe</c> on Windows, <c>/bin/sh</c> elsewhere) in an 80 x 24
/// terminal and keeps one read pending for its whole life, as a terminal frontend does. The probe waits until every
/// session has shown its prompt and all output has stopped for 2 seconds. It then reads the process's threads,
/// handles, memory, and managed heap, and its CPU time, thread-pool work items, and allocations over an idle window.
/// The same window also runs before any session starts, and each idle figure is printed as before -> with sessions,
/// so what the runtime does by itself is not counted as the library's.
/// </para>
/// <para>
/// One library runs per process, and the probe exits with <see cref="Environment.Exit"/>, since Porta.Pty can leave a
/// foreground thread behind. A warm-up session starts and ends before the baseline, so code loading and each library's
/// shared threads are in the baseline; the report prints both counts, so a shared thread is still visible. Porta.Pty
/// reads into a 4 KiB buffer per session, as its README shows; MiniPty rents its 16 KiB ring only while output waits.
/// On Windows both libraries run on the in-box ConPTY.
/// </para>
/// <para>
/// The probe's own work runs on a dedicated thread and waits with <see cref="Thread.Sleep(int)"/>, so it needs no
/// thread-pool thread: with Porta.Pty's default options every pending read holds one, and the pool adds threads
/// slowly. Sessions that have shown no output after <c>--ready-seconds</c> are reported, and the measurement is taken
/// anyway, since that delay is part of what a host sees.
/// </para>
/// <para>
/// <c>--report</c> appends the run to a CSV file as well. The benchmark workflow writes it beside the BenchmarkDotNet
/// reports, and the README's Scaling table comes from it.
/// </para>
/// </remarks>
internal static class IdleProbe
{
    // An idle shell after its prompt: no output from any session for this long.
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(2);

    private static long s_lastOutput;

    internal static int Run(string[] args)
    {
        var library = Option(args, "library", "minipty");
        var serveLibrary = library switch
        {
            "minipty" => ServeLibrary.MiniPty,
            "porta" => ServeLibrary.PortaPty,
            "porta-async" => ServeLibrary.PortaPtyAsyncIo,
            _ => throw new ArgumentException($"Unknown library \"{library}\". Use minipty, porta, or porta-async."),
        };
        var sessions = int.Parse(Option(args, "sessions", "1000"), NumberStyles.None, CultureInfo.InvariantCulture);
        var idleSeconds = int.Parse(Option(args, "idle-seconds", "10"), NumberStyles.None, CultureInfo.InvariantCulture);
        var readySeconds = int.Parse(Option(args, "ready-seconds", "120"), NumberStyles.None, CultureInfo.InvariantCulture);
        if (!BenchmarkPtyCommands.IsSupported)
        {
            System.Console.Error.WriteLine("PTY is not supported on this platform.");
            return 1;
        }

        System.Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Idle probe: {RuntimeInformation.OSDescription}, {RuntimeInformation.FrameworkDescription}, {Environment.ProcessorCount} logical CPUs, library {library}, {sessions} sessions of {Shell()}"));

        // Warm-up: load each library's code and start its shared threads before the baseline.
        using (var warmUp = IdleSession.Start(serveLibrary))
        {
            if (!WaitUntil(() => warmUp.Bytes > 0, TimeSpan.FromSeconds(30)))
            {
                System.Console.Error.WriteLine("The warm-up shell showed no prompt within 30 seconds.");
                return 1;
            }
        }

        Thread.Sleep(1000);
        var baseline = Snapshot.Take();
        // The same idle window with no session open: what the runtime itself does while the process waits.
        var quiet = IdleWindow.Measure(idleSeconds);

        var open = new IdleSession[sessions];
        var started = Stopwatch.GetTimestamp();
        for (var i = 0; i < sessions; i++)
            open[i] = IdleSession.Start(serveLibrary);

        var startSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        var allRead = WaitUntil(() => CountShown(open) == sessions, TimeSpan.FromSeconds(readySeconds));
        var shownSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        var shown = CountShown(open);
        WaitUntil(() => Stopwatch.GetElapsedTime(Volatile.Read(ref s_lastOutput)) >= Quiet, TimeSpan.FromSeconds(30));

        var idle = Snapshot.Take();
        var waiting = IdleWindow.Measure(idleSeconds);

        var disposeStarted = Stopwatch.GetTimestamp();
        IdleSession.DisposeAll(open);
        var disposeSeconds = Stopwatch.GetElapsedTime(disposeStarted).TotalSeconds;

        // Totals, not per session: much of the growth is paid once (code pages, heap segments), so a quotient would shrink
        // as sessions are added. Private bytes only on Windows: on Linux .NET reports VmData there, which counts each
        // thread's reserved stack.
        var privateMiB = OperatingSystem.IsWindows()
            ? string.Create(CultureInfo.InvariantCulture, $"{baseline.PrivateBytes / 1048576.0:F1}->{idle.PrivateBytes / 1048576.0:F1}")
            : "n/a";
        System.Console.WriteLine("| Library | Sessions | Start all (s) | All prompts (s) | Threads | Pool threads | Handles | Working set (MiB) | Private (MiB) | GC heap (MiB) | Pinned heap (MiB) | Idle CPU (ms/s) | Idle work items/s | Idle alloc (B) | Dispose all (s) |");
        System.Console.WriteLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        System.Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"| {library} | {sessions} | {startSeconds:F1} | {(allRead ? shownSeconds.ToString("F1", CultureInfo.InvariantCulture) : $"{shown} shown in {readySeconds}")} | " +
            $"{baseline.Threads}->{idle.Threads} | {baseline.PoolThreads}->{idle.PoolThreads} | {baseline.Handles}->{idle.Handles} | " +
            $"{baseline.WorkingSet / 1048576.0:F1}->{idle.WorkingSet / 1048576.0:F1} | {privateMiB} | " +
            $"{baseline.Heap / 1048576.0:F2}->{idle.Heap / 1048576.0:F2} | {baseline.PinnedHeap / 1048576.0:F2}->{idle.PinnedHeap / 1048576.0:F2} | " +
            $"{quiet.CpuMs / idleSeconds:F1}->{waiting.CpuMs / idleSeconds:F1} | {quiet.WorkItems / (double)idleSeconds:F1}->{waiting.WorkItems / (double)idleSeconds:F1} | " +
            $"{quiet.Allocated}->{waiting.Allocated} | {disposeSeconds:F1} |"));

        var report = Option(args, "report", "");
        if (report.Length != 0)
        {
            AppendReport(report,
            [
                ("Method", serveLibrary.ToString()),
                ("Sessions", Invariant(sessions)),
                ("ReadySeconds", Invariant(readySeconds)),
                ("StartSeconds", Invariant(startSeconds, "F1")),
                ("PromptsShown", Invariant(shown)),
                ("PromptsSeconds", Invariant(shownSeconds, "F1")),
                ("ThreadsBefore", Invariant(baseline.Threads)),
                ("ThreadsAfter", Invariant(idle.Threads)),
                ("PoolThreadsBefore", Invariant(baseline.PoolThreads)),
                ("PoolThreadsAfter", Invariant(idle.PoolThreads)),
                ("HandlesBefore", Invariant(baseline.Handles)),
                ("HandlesAfter", Invariant(idle.Handles)),
                ("WorkingSetBeforeMiB", Invariant(baseline.WorkingSet / 1048576.0, "F1")),
                ("WorkingSetAfterMiB", Invariant(idle.WorkingSet / 1048576.0, "F1")),
                ("WorkingSetGrowthMiB", Invariant((idle.WorkingSet - baseline.WorkingSet) / 1048576.0, Signed)),
                ("PrivateGrowthMiB", OperatingSystem.IsWindows() ? Invariant((idle.PrivateBytes - baseline.PrivateBytes) / 1048576.0, Signed) : ""),
                ("HeapGrowthMiB", Invariant((idle.Heap - baseline.Heap) / 1048576.0, Signed)),
                ("PinnedHeapGrowthMiB", Invariant((idle.PinnedHeap - baseline.PinnedHeap) / 1048576.0, "+0.00;-0.00;0.00")),
                ("QuietCpuMsPerSec", Invariant(quiet.CpuMs / idleSeconds, "F1")),
                ("IdleCpuMsPerSec", Invariant(waiting.CpuMs / idleSeconds, "F1")),
                ("QuietWorkItemsPerSec", Invariant(quiet.WorkItems / (double)idleSeconds, "F1")),
                ("IdleWorkItemsPerSec", Invariant(waiting.WorkItems / (double)idleSeconds, "F1")),
                ("QuietAllocatedBytes", Invariant(quiet.Allocated)),
                ("IdleAllocatedBytes", Invariant(waiting.Allocated)),
                ("DisposeSeconds", Invariant(disposeSeconds, "F1")),
                ("Os", RuntimeInformation.OSDescription),
                ("Runtime", RuntimeInformation.FrameworkDescription),
                ("LogicalCpus", Invariant(Environment.ProcessorCount)),
            ]);
        }

        return 0;
    }

    // A growth keeps its sign, so a cell reads "+7.9" or "-0.3" as written.
    private const string Signed = "+0.0;-0.0;0.0";

    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Invariant(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);

    /// <summary>
    /// Appends one row to the CSV report of <c>--report</c>, with a header line when the file is new. The row starts with a
    /// Method column, as a BenchmarkDotNet CSV report does, so <c>scripts/UpdateReadmeBenchmarks.cs</c> reads it with the
    /// parser it uses for those reports. A field with a comma or a quote is quoted, and a quote in it is doubled.
    /// </summary>
    private static void AppendReport(string path, (string Name, string Value)[] fields)
    {
        var text = new System.Text.StringBuilder();
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            for (var i = 0; i < fields.Length; i++)
                text.Append(i == 0 ? "" : ",").Append(fields[i].Name);
            text.Append('\n');
        }

        for (var i = 0; i < fields.Length; i++)
        {
            var value = fields[i].Value;
            if (i != 0)
                text.Append(',');
            if (value.Contains(',') || value.Contains('"'))
                text.Append('"').Append(value.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
            else
                text.Append(value);
        }

        text.Append('\n');
        File.AppendAllText(path, text.ToString());
    }

    /// <summary>What the process does over an idle window in which the probe only sleeps.</summary>
    /// <remarks>
    /// The window starts 2 seconds after the snapshot's forced collections: right after them the runtime spent a fixed
    /// 20–60 ms of CPU (Linux, any library, 10 s and 30 s windows alike) while no thread of the process gained a tick
    /// later in the window. Allocations are read first and last, so the probe's own reads of the process fall outside it.
    /// </remarks>
    private readonly record struct IdleWindow(double CpuMs, long WorkItems, long Allocated)
    {
        internal static IdleWindow Measure(int seconds)
        {
            Thread.Sleep(2000);
            using var self = Process.GetCurrentProcess();
            var cpuBefore = self.TotalProcessorTime;
            var workItemsBefore = ThreadPool.CompletedWorkItemCount;
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            Thread.Sleep(TimeSpan.FromSeconds(seconds));
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            var workItems = ThreadPool.CompletedWorkItemCount - workItemsBefore;
            self.Refresh();
            return new IdleWindow((self.TotalProcessorTime - cpuBefore).TotalMilliseconds, workItems, allocated);
        }
    }

    private static int CountShown(IdleSession[] open)
    {
        var shown = 0;
        foreach (var session in open)
        {
            if (session.Bytes > 0)
                shown++;
        }

        return shown;
    }

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var started = Stopwatch.GetTimestamp();
        while (!condition())
        {
            if (Stopwatch.GetElapsedTime(started) >= timeout)
                return false;

            Thread.Sleep(50);
        }

        return true;
    }

    private static string Shell() =>
        OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe" : "/bin/sh";

    private static string Option(string[] args, string name, string fallback)
    {
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            if (args[i] == "--" + name)
                return args[i + 1];
        }

        return fallback;
    }

    /// <summary>One shell with one read pending on it, which counts the bytes that arrive.</summary>
    private abstract class IdleSession : IDisposable
    {
        private long _bytes;

        internal long Bytes => Volatile.Read(ref _bytes);

        internal static IdleSession Start(ServeLibrary library) =>
            library == ServeLibrary.MiniPty ? new MiniPtyIdle() : new PortaIdle(library == ServeLibrary.PortaPtyAsyncIo);

        /// <summary>Ends every session, each library's way.</summary>
        /// <remarks>
        /// MiniPty's disposals start together, since each one only awaits. Porta.Pty's <c>Dispose</c> blocks the thread
        /// that calls it, about 350 ms per session on the macOS runner, where 900 of them one after another took 317 s.
        /// So 32 threads of the probe's own end those sessions; the thread pool would add threads for them too slowly.
        /// </remarks>
        internal static void DisposeAll(IdleSession[] open)
        {
            if (open.Length != 0 && open[0] is PortaIdle)
            {
                var next = -1;
                var workers = new Thread[Math.Min(32, open.Length)];
                for (var w = 0; w < workers.Length; w++)
                {
                    workers[w] = new Thread(() =>
                    {
                        int i;
                        while ((i = Interlocked.Increment(ref next)) < open.Length)
                            open[i].Dispose();
                    })
                    { IsBackground = true, Name = "idle probe disposal" };
                    workers[w].Start();
                }

                foreach (var worker in workers)
                    worker.Join();

                return;
            }

            var disposals = new Task[open.Length];
            for (var i = 0; i < open.Length; i++)
                disposals[i] = open[i].DisposeAsync();

            Task.WaitAll(disposals);
        }

        protected void OnOutput(int count)
        {
            Interlocked.Add(ref _bytes, count);
            Volatile.Write(ref s_lastOutput, Stopwatch.GetTimestamp());
        }

        protected abstract Task DisposeAsync();

        public void Dispose() => DisposeAsync().GetAwaiter().GetResult();
    }

    private sealed class MiniPtyIdle : IdleSession
    {
        private readonly PtySession _session;
        private readonly Task _reader;

        internal MiniPtyIdle()
        {
            _session = Pty.Start(new PtyStartInfo
            {
                FileName = Shell(),
                Size = new PtySize(BenchmarkPtyCommands.Columns, BenchmarkPtyCommands.Rows),
            });
            _reader = ReadAsync();
        }

        private async Task ReadAsync()
        {
            try
            {
                await foreach (var chunk in _session.ReadOutputAsync().ConfigureAwait(false))
                    OnOutput(chunk.Data.Length);
            }
            catch (ObjectDisposedException)
            {
                // Disposed while reading.
            }
        }

        protected override async Task DisposeAsync()
        {
            await _session.DisposeAsync().ConfigureAwait(false);
            await _reader.ConfigureAwait(false);
        }
    }

    private sealed class PortaIdle : IdleSession
    {
        private readonly IPtyConnection _connection;
        private readonly byte[] _buffer = new byte[4096];
        private readonly Task _reader;

        internal PortaIdle(bool asyncIo)
        {
            if (OperatingSystem.IsWindows())
            {
                Environment.SetEnvironmentVariable("PORTAPTY_CONPTY", "inbox");
                if (PtyProvider.PseudoConsoleImplementation != "inbox")
                    throw new InvalidOperationException($"Porta.Pty runs on the \"{PtyProvider.PseudoConsoleImplementation}\" ConPTY, not the in-box one.");
            }

            // Porta.Pty passes exactly this dictionary, and MiniPty inherits the environment by default.
            var environment = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
                environment[(string)entry.Key] = (string?)entry.Value ?? string.Empty;

            _connection = PtyProvider.SpawnAsync(new PtyOptions
            {
                App = Shell(),
                CommandLine = [],
                Cwd = Environment.CurrentDirectory,
                Cols = BenchmarkPtyCommands.Columns,
                Rows = BenchmarkPtyCommands.Rows,
                Environment = environment,
                UseAsyncIo = asyncIo,
            }, CancellationToken.None).GetAwaiter().GetResult();
            _reader = ReadAsync();
        }

        private async Task ReadAsync()
        {
            try
            {
                while (true)
                {
                    var read = await _connection.ReaderStream.ReadAsync(_buffer).ConfigureAwait(false);
                    if (read == 0)
                        break;

                    OnOutput(read);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // The shell ended or the connection was disposed.
            }
        }

        protected override async Task DisposeAsync()
        {
            try
            {
                _connection.Kill();
            }
            catch (Exception)
            {
                // The shell has already exited.
            }

            _connection.Dispose();
            // On Windows the read may not end with the shell; it ends when the connection closes, or never on an old
            // build, so the disposal waits for it no longer than 5 seconds.
            await Task.WhenAny(_reader, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        }
    }

    /// <summary>The process after a full collection.</summary>
    /// <remarks>
    /// <see cref="PinnedHeap"/> is the pinned object heap, where MiniPty's 16 KiB output rings live: the managed heap of a
    /// MiniPty host grew by 7.1 MiB on the 4-CPU Windows runner and by 1.2 MiB on a 32-CPU machine (also with 4 CPUs set
    /// from the start), and this figure tells rings that its pool kept from other growth.
    /// </remarks>
    private readonly record struct Snapshot(int Threads, int PoolThreads, int Handles, long WorkingSet, long PrivateBytes, long Heap, long PinnedHeap)
    {
        internal static Snapshot Take()
        {
            // A full, compacting collection first, so the heap figure is what the sessions keep alive. The figure is
            // that collection's heap size less its free space: GC.GetTotalMemory read below zero here with Porta.Pty's
            // sessions open (.NET 10.0.15, Windows).
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            var collection = GC.GetGCMemoryInfo(GCKind.FullBlocking);
            var heap = collection.HeapSizeBytes - collection.FragmentedBytes;
            using var process = Process.GetCurrentProcess();
            // Each figure is read on its own: FreeBSD's .NET leaves some of them unsupported, and those read -1.
            return new Snapshot(
                Read(() => process.Threads.Count, -1),
                ThreadPool.ThreadCount,
                Read(() => process.HandleCount, -1),
                Read(() => process.WorkingSet64, -1L),
                Read(() => process.PrivateMemorySize64, -1L),
                heap,
                collection.GenerationInfo[4].SizeAfterBytes);
        }

        private static T Read<T>(Func<T> read, T unsupported)
        {
            try
            {
                return read();
            }
            catch (Exception ex) when (ex is PlatformNotSupportedException or NotSupportedException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return unsupported;
            }
        }
    }
}
