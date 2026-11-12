using System.Collections;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using MiniPty.Capture;
using MiniPty.Terminal;
using Porta.Pty;

namespace MiniPty.Benchmarks;

/// <summary>
/// How much output a session takes and what a flood does to the rest of the host: an opt-in probe, not a
/// BenchmarkDotNet benchmark.
/// <c>dotnet run -c Release --project src/MiniPty.Benchmarks -- --flood-probe &lt;scenario&gt; [--option value ...]</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every scenario runs the benchmark child's flood mode (<c>--flood</c>), which writes printable 77-character lines as
/// fast as the terminal takes them, and reads it through the library under test the way a terminal frontend does.
/// </para>
/// <list type="table">
/// <item><term><c>sustained</c></term><description>One flood of <c>--bytes</c>: throughput over time in 250 ms windows,
/// CPU, the shared I/O thread's CPU, allocations, collections, peak memory, threads and handles.</description></item>
/// <item><term><c>slow</c></term><description>An endless flood read by a consumer that waits <c>--delay-ms</c> after
/// each chunk, for <c>--seconds</c>: memory over time and the stalls that hold the child back.</description></item>
/// <item><term><c>echo</c></term><description>Keystrokes typed into a flooding session every 50 ms: how long until their
/// echo comes out of the flood.</description></item>
/// <item><term><c>neighbors</c></term><description><c>--floods</c> endless floods beside one quiet session: the quiet
/// session's keystroke round trip, the floods' total and per-session throughput, and the shared I/O thread's CPU.</description></item>
/// <item><term><c>control</c></term><description>Ctrl+C, <c>Kill()</c> and disposal of a flooding session: how long until
/// the child is gone and output ends, and how much output still arrives. <c>--verbose true</c> prints every round.</description></item>
/// <item><term><c>oneshot</c></term><description><c>CompleteAsync</c> or <c>PtyCapture.RunAsync</c> on a flood of
/// <c>--bytes</c> (<c>--api complete|capture</c>, <c>--decode true|false</c>): time, peak memory against the output's
/// size, chunks.</description></item>
/// <item><term><c>signals</c></term><description>Linux: a flooding child's signal masks and process group, from
/// <c>/proc</c>.</description></item>
/// </list>
/// <para>
/// <c>--library minipty|porta|porta-async</c> picks the library (<c>oneshot</c> is MiniPty only), and on Windows
/// <c>--conpty in-box|out-of-band</c> the ConPTY. <c>--read coalesced</c> reads MiniPty through the bridges' internal
/// coalescing reader instead of <c>ReadOutputAsync</c>. <c>--program</c> floods with a <c>yes</c>-like program instead
/// of the benchmark child. On Windows <c>--host-ctrl-c enable</c> makes the probe process Ctrl+C before it starts a
/// child, since a host that ignores Ctrl+C passes that on to every child. Each case should run in a process of its own,
/// since peak memory is the process's.
/// </para>
/// </remarks>
internal static class FloodProbe
{
    private static readonly string[] Scenarios = ["sustained", "slow", "echo", "neighbors", "control", "oneshot", "signals", "bridge"];

    /// <summary>The line a <c>--program</c> such as <c>yes</c> repeats: 77 characters without <c>%</c>, like the child's.</summary>
    private const string YesLine = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789abcdefghijklmno";

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || Array.IndexOf(Scenarios, args[0]) < 0)
        {
            System.Console.Error.WriteLine("usage: --flood-probe <" + string.Join('|', Scenarios) + "> [--library minipty|porta|porta-async] [--conpty in-box|out-of-band] [--bytes N] [--seconds N] [--delay-ms N] [--floods 0,1,4] [--rounds N] [--api complete|capture] [--decode true|false]");
            return 2;
        }

        if (!BenchmarkPtyCommands.IsSupported)
        {
            System.Console.Error.WriteLine("PTY is not supported on this platform.");
            return 1;
        }

        var options = new Options(args[1..]);
        // Windows: a process that ignores Ctrl+C passes that to every process it starts, the PTY child included. With
        // --host-ctrl-c enable the probe processes Ctrl+C again before it starts any child, to tell that inheritance apart
        // from what the terminal does with an interrupt.
        if (OperatingSystem.IsWindows() && options.Get("host-ctrl-c", "") == "enable" && !IoThread.EnableCtrlC())
            System.Console.Error.WriteLine("SetConsoleCtrlHandler(NULL, FALSE) failed.");
        System.Console.WriteLine(string.Create(CultureInfo.InvariantCulture, 
            $"Flood probe {args[0]}: {RuntimeInformation.OSDescription}, {RuntimeInformation.FrameworkDescription}, {Environment.ProcessorCount} logical CPUs, library {options.Library}, ConPTY {options.ConPty}, read {options.Get("read", "plain")}"));
        return args[0] switch
        {
            "sustained" => await SustainedAsync(options),
            "slow" => await SlowAsync(options),
            "echo" => await EchoAsync(options),
            "neighbors" => await NeighborsAsync(options),
            "control" => await ControlAsync(options),
            "signals" => await SignalsAsync(options),
            "bridge" => await BridgeAsync(options),
            _ => await OneShotAsync(options),
        };
    }

    // ---------------------------------------------------------------- Scenarios

    private static async Task<int> SustainedAsync(Options options)
    {
        var bytes = options.GetLong("bytes", OperatingSystem.IsWindows() ? 256L << 20 : 1L << 30);
        var samples = new List<(long Time, long Bytes)>(16384);
        long heapMax = 0;
        var threadsBefore = IoThread.Times();
        var before = Snapshot.Take();
        var flood = FloodSession.Start(options, bytes, echo: null, delayMs: 0);
        using var stop = new ManualResetEventSlim();
        var sampler = new Thread(() =>
        {
            while (!stop.Wait(250))
            {
                samples.Add((Stopwatch.GetTimestamp(), flood.Bytes));
                heapMax = Math.Max(heapMax, GC.GetTotalMemory(forceFullCollection: false));
            }
        })
        { IsBackground = true, Name = "flood probe sampler" };
        sampler.Start();

        var finished = await Task.WhenAny(flood.Consumer, Task.Delay(TimeSpan.FromMinutes(15))) == flood.Consumer;
        stop.Set();
        sampler.Join();
        var after = Snapshot.Take();
        var threadsAfter = IoThread.Times();
        await flood.DisposeAsync();
        if (!finished)
        {
            System.Console.Error.WriteLine("The flood did not end within 15 minutes.");
            return 1;
        }

        var seconds = Stopwatch.GetElapsedTime(flood.FirstByte, flood.LastByte).TotalSeconds;
        var mib = flood.Bytes / 1048576.0;
        var windows = new List<double>(samples.Count);
        for (var i = 1; i < samples.Count; i++)
        {
            // Whole windows inside the flood only: the first one starts with the spawn, the last one ends after it.
            if (samples[i - 1].Time < flood.FirstByte || samples[i].Time > flood.LastByte)
                continue;

            windows.Add((samples[i].Bytes - samples[i - 1].Bytes) / 1048576.0 / Stopwatch.GetElapsedTime(samples[i - 1].Time, samples[i].Time).TotalSeconds);
        }

        var quarter = Math.Max(1, windows.Count / 4);
        var sorted = windows.Order().ToArray();
        var expected = OperatingSystem.IsWindows() ? (long?)null : bytes + bytes / 78; // The terminal turns each \n into \r\n.
        System.Console.WriteLine("| Library | ConPTY | Written MiB | Read MiB | s | MiB/s | 250 ms windows min / p50 / max | First / last quarter | Avg chunk B | Work items/MiB | CPU ms/MiB | I/O thread CPU | Alloc B/MiB | GC 0/1/2 | Peak WS MiB | Max GC heap MiB | Threads | Handles |");
        System.Console.WriteLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        System.Console.WriteLine(string.Create(CultureInfo.InvariantCulture, 
            $"| {options.Library} | {options.ConPty} | {bytes / 1048576.0:F0} | {mib:F1} | {seconds:F2} | {mib / seconds:F1} | " +
            $"{At(sorted, 0):F0} / {At(sorted, 0.5):F0} / {(sorted.Length == 0 ? double.NaN : sorted[^1]):F0} | " +
            $"{Mean(windows, 0, quarter):F0} / {Mean(windows, windows.Count - quarter, quarter):F0} | " +
            $"{flood.Bytes / (double)Math.Max(1, flood.Chunks):F0} | {(after.WorkItems - before.WorkItems) / mib:F0} | {(after.Cpu - before.Cpu).TotalMilliseconds / mib:F2} | {IoThreadShare(before, after)} | {(after.Allocated - before.Allocated) / mib:F0} | " +
            $"{after.Gen0 - before.Gen0}/{after.Gen1 - before.Gen1}/{after.Gen2 - before.Gen2} | {after.PeakWorkingSet / 1048576.0:F0} | {heapMax / 1048576.0:F1} | " +
            $"{before.Threads}->{after.Threads} | {before.Handles}->{after.Handles} |"));
        IoThread.Report(threadsBefore, threadsAfter, Stopwatch.GetElapsedTime(before.Timestamp, after.Timestamp).TotalSeconds);
        // macOS writes CR CR LF for some newlines once the reader falls behind, at most one per KiB of its 1 KiB queue
        // (platform_support.md), so there a read may exceed the expected count by that much; never fall short of it.
        var extraAllowed = OperatingSystem.IsMacOS() && expected is { } bound ? bound / 1024 : 0;
        if (expected is { } want && (flood.Bytes < want || flood.Bytes - want > extraAllowed))
        {
            System.Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Read {flood.Bytes} bytes, expected {want}{(extraAllowed > 0 ? $" to {want + extraAllowed}" : "")}."));
            return 1;
        }

        if (expected is { } read && flood.Bytes > read)
            System.Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Read {flood.Bytes - read} bytes more than written: CR CR LF from the macOS terminal."));

        return 0;
    }

    private static async Task<int> SlowAsync(Options options)
    {
        var seconds = options.GetInt("seconds", 10);
        var delayMs = options.GetInt("delay-ms", 2);
        using var stalls = new StallListener();
        var before = Snapshot.Take();
        var flood = FloodSession.Start(options, 0, echo: null, delayMs);
        var memory = new List<(long WorkingSet, long Private, long Heap)>(seconds + 1);
        for (var i = 0; i < seconds; i++)
        {
            await Task.Delay(1000);
            var now = Snapshot.Take();
            memory.Add((now.WorkingSet, now.PrivateBytes, GC.GetTotalMemory(forceFullCollection: false)));
        }

        var after = Snapshot.Take();
        var bytes = flood.Bytes;
        var end = await EndAsync(flood, "kill");
        var (exitMs, endMs) = (end.GoneMs, end.ReaderEndMs);

        // The first second includes the start; memory is judged on the rest.
        var steady = memory.Skip(1).ToArray();
        System.Console.WriteLine("| Library | ConPTY | Delay ms | Read MiB/s | Chunks/s | WS MiB min-max | Private MiB min-max | GC heap MiB min-max | Stalls | Stalled s | CPU % of one core | Kill->child gone ms | Kill->reader end ms |");
        System.Console.WriteLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        var elapsed = Stopwatch.GetElapsedTime(before.Timestamp, after.Timestamp).TotalSeconds;
        System.Console.WriteLine(string.Create(CultureInfo.InvariantCulture, 
            $"| {options.Library} | {options.ConPty} | {delayMs} | {bytes / 1048576.0 / elapsed:F2} | {flood.Chunks / elapsed:F0} | " +
            $"{steady.Min(m => m.WorkingSet) / 1048576.0:F1}-{steady.Max(m => m.WorkingSet) / 1048576.0:F1} | " +
            $"{steady.Min(m => m.Private) / 1048576.0:F1}-{steady.Max(m => m.Private) / 1048576.0:F1} | " +
            $"{steady.Min(m => m.Heap) / 1048576.0:F2}-{steady.Max(m => m.Heap) / 1048576.0:F2} | " +
            $"{stalls.Count} | {stalls.Seconds:F2} | {(after.Cpu - before.Cpu).TotalSeconds / elapsed * 100:F1} | {exitMs:F1} | {endMs:F1} |"));
        return 0;
    }

    private static async Task<int> EchoAsync(Options options)
    {
        var seconds = options.GetInt("seconds", 5);
        var echo = new EchoTracker();
        // The Windows child reads keys and echoes them only when asked (Flood.cs); a Unix terminal echoes by itself.
        Environment.SetEnvironmentVariable("MINIPTY_FLOOD_ECHO", "1");
        var flood = FloodSession.Start(options, 0, echo, delayMs: 0);
        await Task.Delay(1000);
        var bytesBefore = flood.Bytes;
        var started = Stopwatch.GetTimestamp();
        await TypeAsync(flood, echo, seconds);
        var floodSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        var bytes = flood.Bytes - bytesBefore;
        // Echoes of the last keys may still be on their way.
        await Task.Delay(1000);
        await flood.DisposeAsync();

        var latencies = echo.Latencies();
        System.Console.WriteLine("| Library | ConPTY | Flood MiB/s | Keys | Echoed | Lost | Echo p50 ms | p90 ms | p99 ms | max ms |");
        System.Console.WriteLine("|---|---|---|---|---|---|---|---|---|---|");
        System.Console.WriteLine(string.Create(CultureInfo.InvariantCulture, 
            $"| {options.Library} | {options.ConPty} | {bytes / 1048576.0 / floodSeconds:F1} | {echo.Sent} | {latencies.Length} | {echo.Lost + echo.Pending} | " +
            $"{At(latencies, 0.5):F2} | {At(latencies, 0.9):F2} | {At(latencies, 0.99):F2} | {(latencies.Length == 0 ? double.NaN : latencies[^1]):F2} |"));
        return 0;
    }

    private static async Task<int> NeighborsAsync(Options options)
    {
        var seconds = options.GetInt("seconds", 5);
        var counts = options.GetList("floods", OperatingSystem.IsWindows() ? [0, 1, 4, 16] : [0, 1, 4, 16, 64]);
        System.Console.WriteLine("| Library | ConPTY | Floods | Echo round trips | p50 ms | p90 ms | p99 ms | max ms | Floods MiB/s | Per flood MiB/s min-max | CPU % of one core | I/O thread CPU | Work items/s | Threads |");
        System.Console.WriteLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var count in counts)
        {
            var floods = new FloodSession[count];
            for (var i = 0; i < count; i++)
                floods[i] = FloodSession.Start(options, 0, echo: null, delayMs: 0);

            var quiet = BenchmarkServeSession.Start(options.ServeLibrary, 1, options.ConPty);
            try
            {
                await Task.Delay(2000);
                var startBytes = new long[count];
                for (var i = 0; i < count; i++)
                    startBytes[i] = floods[i].Bytes;

                var before = Snapshot.Take();
                var latencies = new List<double>(seconds * 60);
                var second = false;
                var until = Stopwatch.GetTimestamp() + seconds * Stopwatch.Frequency;
                while (Stopwatch.GetTimestamp() < until)
                {
                    second = !second;
                    var key = second ? BenchmarkServeSession.KeyB : BenchmarkServeSession.KeyA;
                    var sent = Stopwatch.GetTimestamp();
                    await quiet.RoundTripAsync(key, key);
                    latencies.Add(Stopwatch.GetElapsedTime(sent).TotalMilliseconds);
                    await Task.Delay(20);
                }

                var after = Snapshot.Take();
                var elapsed = Stopwatch.GetElapsedTime(before.Timestamp, after.Timestamp).TotalSeconds;
                var rates = new double[count];
                for (var i = 0; i < count; i++)
                    rates[i] = (floods[i].Bytes - startBytes[i]) / 1048576.0 / elapsed;

                var sorted = latencies.Order().ToArray();
                System.Console.WriteLine(string.Create(CultureInfo.InvariantCulture, 
                    $"| {options.Library} | {options.ConPty} | {count} | {sorted.Length} | {At(sorted, 0.5):F2} | {At(sorted, 0.9):F2} | {At(sorted, 0.99):F2} | {sorted[^1]:F2} | " +
                    $"{rates.Sum():F1} | {(count == 0 ? "-" : string.Create(CultureInfo.InvariantCulture, $"{rates.Min():F1}-{rates.Max():F1}"))} | " +
                    $"{(after.Cpu - before.Cpu).TotalSeconds / elapsed * 100:F0} | {IoThreadShare(before, after)} | {(after.WorkItems - before.WorkItems) / elapsed:F0} | {after.Threads} |"));
            }
            finally
            {
                // All at once: each disposal waits up to 10 s for its reader, which under a starved thread pool adds up.
                quiet.Dispose();
                var disposals = new Task[count];
                for (var i = 0; i < count; i++)
                    disposals[i] = floods[i].DisposeAsync().AsTask();

                await Task.WhenAll(disposals);
            }

            // Let the children and their console hosts go before the next row.
            await Task.Delay(1000);
        }

        return 0;
    }

    private static async Task<int> ControlAsync(Options options)
    {
        var rounds = options.GetInt("rounds", 5);
        string[] actions = ["ctrl-c", "kill", "dispose"];
        System.Console.WriteLine("| Library | ConPTY | Action | Rounds | Child gone ms (median / max) | Last byte ms (median / max) | Reader end ms (median / max) | Dispose call ms (median / max) | KiB after (median / max) |");
        System.Console.WriteLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var action in actions)
        {
            var results = new List<EndResult>();
            for (var round = 0; round < rounds; round++)
            {
                var flood = FloodSession.Start(options, 0, echo: null, delayMs: 0);
                await Task.Delay(500);
                var result = await EndAsync(flood, action);
                results.Add(result);
                // --verbose true: every round, to see a distribution the median and maximum hide.
                if (options.Get("verbose", "false") == "true")
                {
                    System.Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"round {action} {round}: gone {result.GoneMs:F1} ms, last byte {result.LastByteMs:F1} ms, reader end {result.ReaderEndMs:F1} ms, {result.KiBAfter:F0} KiB after"));
                }
            }

            string Cell(Func<EndResult, double> value, string format) =>
                string.Create(CultureInfo.InvariantCulture, $"{Median(results.Select(value).ToList()).ToString(format, CultureInfo.InvariantCulture)} / {results.Max(value).ToString(format, CultureInfo.InvariantCulture)}");

            System.Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"| {options.Library} | {options.ConPty} | {action} | {rounds} | {Cell(r => r.GoneMs, "F1")} | {Cell(r => r.LastByteMs, "F1")} | " +
                $"{Cell(r => r.ReaderEndMs, "F1")} | {Cell(r => r.DisposeMs, "F1")} | {Cell(r => r.KiBAfter, "F0")} |"));
        }

        return 0;
    }

    /// <summary>
    /// Linux: a flooding child's signal masks, process group, session and foreground group, from <c>/proc</c>, to tell why
    /// an interrupt ends it sooner under one library than another.
    /// </summary>
    private static async Task<int> SignalsAsync(Options options)
    {
        if (!OperatingSystem.IsLinux())
            return 0;

        var flood = FloodSession.Start(options, 0, echo: null, delayMs: 0);
        await Task.Delay(500);
        var pid = flood.ProcessId;
        foreach (var line in File.ReadAllLines($"/proc/{pid}/status"))
        {
            if (line.StartsWith("Sig", StringComparison.Ordinal) || line.StartsWith("Shd", StringComparison.Ordinal) || line.StartsWith("Threads", StringComparison.Ordinal))
                System.Console.WriteLine(line);
        }

        var stat = File.ReadAllText($"/proc/{pid}/stat");
        var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
        // Fields from the state (field 3) on: ppid 4, pgrp 5, session 6, tty_nr 7, tpgid 8.
        System.Console.WriteLine($"pid {pid} ppid {fields[1]} pgrp {fields[2]} session {fields[3]} tty_nr {fields[4]} tpgid {fields[5]}");
        await flood.DisposeAsync();
        return 0;
    }

    /// <summary>
    /// Keystrokes typed into a flooding session through <see cref="PtyWebSocketBridge"/>, end to end over loopback TCP:
    /// how long until each echo comes back in the bridge's output frames, how fast the flood goes through, and in how many
    /// frames. The client acknowledges output every 128 KiB, as the xterm.js flow-control guide does, after
    /// <c>--ack-delay-ms</c>, which stands in for a browser that renders slower than the flood.
    /// </summary>
    private static async Task<int> BridgeAsync(Options options)
    {
        const int AckChunk = 128 * 1024;
        var seconds = options.GetInt("seconds", 5);
        var ackDelayMs = options.GetInt("ack-delay-ms", 0);
        // The Windows child reads keys and echoes them only when asked (Flood.cs); a Unix terminal echoes by itself.
        Environment.SetEnvironmentVariable("MINIPTY_FLOOD_ECHO", "1");

        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen();
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        var connect = client.ConnectAsync(listener.LocalEndPoint!);
        using var server = await listener.AcceptAsync();
        await connect;
        server.NoDelay = true;
        using var serverSocket = WebSocket.CreateFromStream(new NetworkStream(server), new WebSocketCreationOptions { IsServer = true });
        using var clientSocket = WebSocket.CreateFromStream(new NetworkStream(client), new WebSocketCreationOptions { IsServer = false });

        using var stop = new CancellationTokenSource();
        var bridge = PtyWebSocketBridge.RunAsync(FloodSession.MiniPtyStartInfo(options, 0), serverSocket, cancellationToken: stop.Token);

        // A WebSocket takes one send at a time: acknowledgements and keystrokes share the client's.
        using var sendLock = new SemaphoreSlim(1, 1);
        async Task SendAsync(ReadOnlyMemory<byte> payload, WebSocketMessageType type)
        {
            await sendLock.WaitAsync();
            try
            {
                await clientSocket.SendAsync(payload, type, endOfMessage: true, CancellationToken.None);
            }
            finally
            {
                sendLock.Release();
            }
        }

        var echo = new EchoTracker();
        long bytes = 0;
        long frames = 0;
        var receive = Task.Run(async () =>
        {
            var buffer = new byte[64 * 1024];
            long unacked = 0;
            try
            {
                while (true)
                {
                    var result = await clientSocket.ReceiveAsync(buffer.AsMemory(), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return;

                    if (result.MessageType != WebSocketMessageType.Binary)
                        continue;

                    echo.Scan(buffer.AsSpan(0, result.Count));
                    Interlocked.Add(ref bytes, result.Count);
                    if (result.EndOfMessage)
                        Interlocked.Increment(ref frames);

                    unacked += result.Count;
                    if (unacked < AckChunk)
                        continue;

                    if (ackDelayMs > 0)
                        await Task.Delay(ackDelayMs);

                    var ack = System.Text.Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{{\"type\":\"ack\",\"bytes\":{unacked}}}"));
                    unacked = 0;
                    await SendAsync(ack, WebSocketMessageType.Text);
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                // The bridge ended the connection.
            }
        });

        await Task.Delay(1000);
        var bytesBefore = Interlocked.Read(ref bytes);
        var framesBefore = Interlocked.Read(ref frames);
        var started = Stopwatch.GetTimestamp();
        var key = new byte[2];
        key[0] = (byte)'%';
        var letter = 0;
        var until = started + seconds * Stopwatch.Frequency;
        while (Stopwatch.GetTimestamp() < until)
        {
            key[1] = (byte)('a' + letter);
            letter = (letter + 1) % 26;
            echo.OnSent(key[1], Stopwatch.GetTimestamp());
            await SendAsync(key.ToArray(), WebSocketMessageType.Binary);
            await Task.Delay(50);
        }

        var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
        var floodBytes = Interlocked.Read(ref bytes) - bytesBefore;
        var floodFrames = Interlocked.Read(ref frames) - framesBefore;
        // Echoes of the last keys may still be on their way.
        await Task.Delay(Math.Max(1000, ackDelayMs * 20));
        stop.Cancel();
        try
        {
            await bridge.WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or WebSocketException)
        {
        }

        clientSocket.Abort();
        await Task.WhenAny(receive, Task.Delay(5000));

        var latencies = echo.Latencies();
        System.Console.WriteLine("| ConPTY | ACK delay ms | Flood MiB/s | Frames per 32 KiB | Keys | Echoed | Lost | Echo p50 ms | p90 ms | p99 ms | max ms |");
        System.Console.WriteLine("|---|---|---|---|---|---|---|---|---|---|---|");
        System.Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"| {options.ConPty} | {ackDelayMs} | {floodBytes / 1048576.0 / elapsed:F1} | {(floodBytes == 0 ? double.NaN : floodFrames * 32768.0 / floodBytes):F1} | {echo.Sent} | {latencies.Length} | {echo.Lost + echo.Pending} | " +
            $"{At(latencies, 0.5):F2} | {At(latencies, 0.9):F2} | {At(latencies, 0.99):F2} | {(latencies.Length == 0 ? double.NaN : latencies[^1]):F2} |"));
        return 0;
    }

    private static async Task<int> OneShotAsync(Options options)
    {
        var bytes = options.GetLong("bytes", 64L << 20);
        var api = options.Get("api", "complete");
        var decode = options.Get("decode", "true") == "true";
        long heapMax = 0;
        using var stop = new ManualResetEventSlim();
        var sampler = new Thread(() =>
        {
            while (!stop.Wait(5))
                heapMax = Math.Max(heapMax, GC.GetTotalMemory(forceFullCollection: false));
        })
        { IsBackground = true, Name = "flood probe sampler" };
        var before = Snapshot.Take();
        sampler.Start();
        var startInfo = FloodSession.MiniPtyStartInfo(options, bytes);
        long output = 0;
        long text = 0;
        var chunks = "-";
        string? failure = null;
        object? result = null;
        try
        {
            var completion = new PtyCompleteOptions { DecodeOutput = decode };
            if (api == "capture")
            {
                var capture = await PtyCapture.RunAsync(startInfo, new PtyCaptureOptions { Completion = completion });
                result = capture;
                output = capture.Output.Length;
                text = decode ? capture.GetText().Length : 0;
                chunks = capture.Chunks.Count.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                await using var session = Pty.Start(startInfo);
                var complete = await session.CompleteAsync(completion);
                result = complete;
                output = complete.Output.Length;
                text = decode ? complete.GetText().Length : 0;
            }
        }
        catch (Exception ex)
        {
            failure = ex.GetType().FullName + ": " + ex.Message.ReplaceLineEndings(" ");
            System.Console.Error.WriteLine(ex);
        }

        var elapsed = Stopwatch.GetElapsedTime(before.Timestamp).TotalSeconds;
        stop.Set();
        sampler.Join();
        var after = Snapshot.Take();
        GC.KeepAlive(result);
        System.Console.WriteLine("| API | Decode | Written MiB | Output MiB | Text Mchars | Chunks | s | MiB/s | Peak WS MiB | Peak WS / output | Max GC heap MiB | Alloc MiB | GC 0/1/2 | Failure |");
        System.Console.WriteLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        var outputMib = output / 1048576.0;
        System.Console.WriteLine(string.Create(CultureInfo.InvariantCulture, 
            $"| {api} | {decode} | {bytes / 1048576.0:F0} | {outputMib:F1} | {text / 1048576.0:F1} | {chunks} | {elapsed:F2} | {(output == 0 ? 0 : outputMib / elapsed):F1} | " +
            $"{after.PeakWorkingSet / 1048576.0:F0} | {(output == 0 ? double.NaN : after.PeakWorkingSet / (double)output):F2} | {heapMax / 1048576.0:F0} | " +
            $"{(after.Allocated - before.Allocated) / 1048576.0:F0} | {after.Gen0 - before.Gen0}/{after.Gen1 - before.Gen1}/{after.Gen2 - before.Gen2} | {failure ?? "-"} |"));
        return failure is null ? 0 : 1;
    }

    // ---------------------------------------------------------------- Shared steps

    /// <summary>Types <c>%</c> and a letter into <paramref name="flood"/> every 50 ms for <paramref name="seconds"/>.</summary>
    private static async Task TypeAsync(FloodSession flood, EchoTracker echo, int seconds)
    {
        var key = new byte[2];
        key[0] = (byte)'%';
        var letter = 0;
        var until = Stopwatch.GetTimestamp() + seconds * Stopwatch.Frequency;
        while (Stopwatch.GetTimestamp() < until)
        {
            key[1] = (byte)('a' + letter);
            letter = (letter + 1) % 26;
            echo.OnSent(key[1], Stopwatch.GetTimestamp());
            await flood.WriteAsync(key);
            await Task.Delay(50);
        }
    }

    /// <summary>What ending a flooding session took, in ms from the action.</summary>
    private readonly record struct EndResult(double GoneMs, double LastByteMs, double ReaderEndMs, double DisposeMs, double KiBAfter);

    /// <summary>
    /// Ends a flooding session by <paramref name="action"/> (<c>ctrl-c</c>, <c>kill</c> or <c>dispose</c>), then disposes
    /// it, and returns how long until its child was gone, its last byte arrived and its reader ended, how long the
    /// disposal itself took, and the KiB of output read after the action.
    /// </summary>
    private static async Task<EndResult> EndAsync(FloodSession flood, string action)
    {
        var bytes = flood.Bytes;
        var gone = Task.Run(() => WaitUntilGone(flood.ProcessId));
        var started = Stopwatch.GetTimestamp();
        switch (action)
        {
            case "ctrl-c":
                await flood.WriteAsync(new byte[] { 3 });
                break;
            case "kill":
                flood.Kill();
                break;
            default:
                await flood.DisposeAsync();
                break;
        }

        var readerEnd = await Task.WhenAny(flood.Consumer, Task.Delay(TimeSpan.FromSeconds(30))) == flood.Consumer
            ? Stopwatch.GetElapsedTime(started, flood.ConsumerEnded).TotalMilliseconds
            : double.PositiveInfinity;
        var goneAt = await gone;
        var goneMs = goneAt == 0 ? double.PositiveInfinity : Stopwatch.GetElapsedTime(started, goneAt).TotalMilliseconds;
        var lastByteMs = flood.LastByte > started ? Stopwatch.GetElapsedTime(started, flood.LastByte).TotalMilliseconds : 0;
        await flood.DisposeAsync();
        return new EndResult(goneMs, lastByteMs, readerEnd, flood.DisposeMs, (flood.Bytes - bytes) / 1024.0);
    }

    /// <summary>Polls until process <paramref name="pid"/> is gone; its time, or 0 after 30 s.</summary>
    private static long WaitUntilGone(int pid)
    {
        var until = Stopwatch.GetTimestamp() + 30 * Stopwatch.Frequency;
        while (Stopwatch.GetTimestamp() < until)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited)
                    return Stopwatch.GetTimestamp();
            }
            catch (ArgumentException)
            {
                return Stopwatch.GetTimestamp();
            }
            catch (InvalidOperationException)
            {
                return Stopwatch.GetTimestamp();
            }

            Thread.Sleep(1);
        }

        return 0;
    }

    private static string IoThreadShare(Snapshot before, Snapshot after)
    {
        // The thread starts with the first session, so a snapshot taken before it has none: it had used no CPU yet.
        if (after.IoThreadCpu is not { } end)
            return "-";

        var start = before.IoThreadCpu ?? TimeSpan.Zero;

        var seconds = Stopwatch.GetElapsedTime(before.Timestamp, after.Timestamp).TotalSeconds;
        return string.Create(CultureInfo.InvariantCulture, $"{(end - start).TotalSeconds / seconds * 100:F0}%");
    }

    private static double At(double[] sorted, double quantile) =>
        sorted.Length == 0 ? double.NaN : sorted[Math.Min(sorted.Length - 1, (int)(quantile * sorted.Length))];

    private static double Median(List<double> values) => At(values.Order().ToArray(), 0.5);

    private static double Mean(List<double> values, int start, int count)
    {
        if (values.Count == 0)
            return double.NaN;

        double sum = 0;
        for (var i = start; i < start + count; i++)
            sum += values[i];

        return sum / count;
    }

    // ---------------------------------------------------------------- Sessions

    /// <summary>A flood child on one library, read by a consumer that counts what arrives.</summary>
    private abstract class FloodSession : IAsyncDisposable
    {
        private readonly EchoTracker? _echo;
        private long _bytes;
        private long _chunks;
        private long _firstByte;
        private long _lastByte;
        private long _consumerEnded;
        private int _disposed;

        protected FloodSession(EchoTracker? echo) => _echo = echo;

        /// <summary>The consumer; it completes when output ends.</summary>
        internal Task Consumer { get; private protected set; } = Task.CompletedTask;

        internal long Bytes => Volatile.Read(ref _bytes);

        internal long Chunks => Volatile.Read(ref _chunks);

        internal long FirstByte => Volatile.Read(ref _firstByte);

        internal long LastByte => Volatile.Read(ref _lastByte);

        internal long ConsumerEnded => Volatile.Read(ref _consumerEnded);

        internal int ProcessId { get; private protected set; }

        internal static FloodSession Start(Options options, long bytes, EchoTracker? echo, int delayMs) =>
            options.ServeLibrary == ServeLibrary.MiniPty
                ? new MiniPtyFlood(MiniPtyStartInfo(options, bytes), echo, delayMs, options.Get("read", "plain") == "coalesced")
                : new PortaFlood(options, bytes, echo, delayMs);

        internal static PtyStartInfo MiniPtyStartInfo(Options options, long bytes) => new()
        {
            FileName = options.Program ?? BenchmarkPtyCommands.BenchmarkChildPath,
            Arguments = options.Program is null ? ["--flood", bytes.ToString(CultureInfo.InvariantCulture)] : [YesLine],
            Size = new PtySize(BenchmarkPtyCommands.Columns, BenchmarkPtyCommands.Rows),
            ConPty = options.ConPty == BenchmarkPtyCommands.ConPtyOutOfBand
                ? ConPtyLibrary.TryLoadFromApplicationDirectory() is { HasConsoleHost: true } library
                    ? library
                    : throw new InvalidOperationException("The out-of-band ConPTY is not staged in the benchmark output.")
                : null,
        };

        /// <summary>Counts one chunk; called by the consumer only.</summary>
        protected void OnChunk(ReadOnlySpan<byte> data)
        {
            var now = Stopwatch.GetTimestamp();
            if (_firstByte == 0)
                Volatile.Write(ref _firstByte, now);

            Volatile.Write(ref _lastByte, now);
            Volatile.Write(ref _bytes, _bytes + data.Length);
            Volatile.Write(ref _chunks, _chunks + 1);
            _echo?.Scan(data);
        }

        protected void OnEnded() => Volatile.Write(ref _consumerEnded, Stopwatch.GetTimestamp());

        internal abstract ValueTask WriteAsync(ReadOnlyMemory<byte> data);

        internal abstract void Kill();

        /// <summary>How long the library's own disposal call took, in ms.</summary>
        internal double DisposeMs { get; private set; }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            var started = Stopwatch.GetTimestamp();
            await DisposeCoreAsync();
            DisposeMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            await Task.WhenAny(Consumer, Task.Delay(TimeSpan.FromSeconds(10)));
        }

        protected abstract ValueTask DisposeCoreAsync();
    }

    private sealed class MiniPtyFlood : FloodSession
    {
        private readonly PtySession _session;

        internal MiniPtyFlood(PtyStartInfo startInfo, EchoTracker? echo, int delayMs, bool coalesced) : base(echo)
        {
            _session = Pty.Start(startInfo);
            ProcessId = _session.ProcessId;
            Consumer = Task.Run(() => ConsumeAsync(delayMs, coalesced));
        }

        private async Task ConsumeAsync(int delayMs, bool coalesced)
        {
            try
            {
                // --read coalesced: the bridges' internal reader, which joins a burst's reads (up to 8 KiB or about 1-2 ms).
                var chunks = coalesced ? _session.ReadOutputCoalescedAsync() : _session.ReadOutputAsync();
                await foreach (var chunk in chunks.ConfigureAwait(false))
                {
                    OnChunk(chunk.Data.Span);
                    if (delayMs > 0)
                        await Task.Delay(delayMs).ConfigureAwait(false);
                }
            }
            catch (ObjectDisposedException)
            {
                // Disposed while reading.
            }
            finally
            {
                OnEnded();
            }
        }

        internal override ValueTask WriteAsync(ReadOnlyMemory<byte> data) => _session.WriteInputAsync(data);

        internal override void Kill() => _session.Kill();

        protected override ValueTask DisposeCoreAsync() => _session.DisposeAsync();
    }

    private sealed class PortaFlood : FloodSession
    {
        private readonly IPtyConnection _connection;
        private readonly byte[] _buffer = new byte[16 * 1024];

        internal PortaFlood(Options options, long bytes, EchoTracker? echo, int delayMs) : base(echo)
        {
            if (OperatingSystem.IsWindows())
            {
                var expected = options.ConPty == BenchmarkPtyCommands.ConPtyOutOfBand ? "oob" : "inbox";
                Environment.SetEnvironmentVariable("PORTAPTY_CONPTY", expected);
                if (PtyProvider.PseudoConsoleImplementation != expected)
                    throw new InvalidOperationException($"Porta.Pty runs on the \"{PtyProvider.PseudoConsoleImplementation}\" ConPTY, not \"{expected}\".");
            }

            var environment = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
                environment[(string)entry.Key] = (string?)entry.Value ?? string.Empty;

            _connection = PtyProvider.SpawnAsync(new PtyOptions
            {
                App = options.Program ?? BenchmarkPtyCommands.BenchmarkChildPath,
                CommandLine = options.Program is null ? ["--flood", bytes.ToString(CultureInfo.InvariantCulture)] : [YesLine],
                Cwd = Environment.CurrentDirectory,
                Cols = BenchmarkPtyCommands.Columns,
                Rows = BenchmarkPtyCommands.Rows,
                Environment = environment,
                UseAsyncIo = options.ServeLibrary == ServeLibrary.PortaPtyAsyncIo,
            }, CancellationToken.None).GetAwaiter().GetResult();
            ProcessId = _connection.Pid;
            Consumer = Task.Run(() => ConsumeAsync(delayMs));
            _connection.ProcessExited += (_, _) => _ = Task.Run(EndWhenQuietAsync);
        }

        /// <summary>
        /// Ends the reader once the child has exited and no output has arrived for 300 ms: on Windows Porta.Pty's reader
        /// does not end when the child exits, since the pseudo-console stays open until the connection is disposed. Only
        /// the reader's end is delayed; last-byte times and throughput are not.
        /// </summary>
        private async Task EndWhenQuietAsync()
        {
            var bytes = Bytes;
            while (!Consumer.IsCompleted)
            {
                await Task.Delay(300).ConfigureAwait(false);
                if (Bytes == bytes)
                    break;

                bytes = Bytes;
            }

            if (!Consumer.IsCompleted)
                _connection.Dispose();
        }

        private async Task ConsumeAsync(int delayMs)
        {
            try
            {
                while (true)
                {
                    int read;
                    try
                    {
                        read = await _connection.ReaderStream.ReadAsync(_buffer).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                    {
                        break;
                    }

                    if (read == 0)
                        break;

                    OnChunk(_buffer.AsSpan(0, read));
                    if (delayMs > 0)
                        await Task.Delay(delayMs).ConfigureAwait(false);
                }
            }
            finally
            {
                OnEnded();
            }
        }

        internal override ValueTask WriteAsync(ReadOnlyMemory<byte> data) => _connection.WriterStream.WriteAsync(data);

        internal override void Kill()
        {
            try
            {
                _connection.Kill();
            }
            catch (Exception)
            {
                // Already gone.
            }
        }

        protected override ValueTask DisposeCoreAsync()
        {
            Kill();
            _connection.Dispose();
            return default;
        }
    }

    /// <summary>
    /// Pairs typed keys (<c>%</c> and a letter) with their echo in the output, which the flood never contains.
    /// </summary>
    private sealed class EchoTracker
    {
        private readonly Lock _gate = new();
        private readonly Queue<(byte Key, long Sent)> _pending = new();
        private readonly List<double> _latencies = new(1024);
        private bool _afterPercent;

        internal int Sent { get; private set; }

        internal int Lost { get; private set; }

        internal int Pending
        {
            get
            {
                lock (_gate)
                    return _pending.Count;
            }
        }

        internal void OnSent(byte key, long timestamp)
        {
            lock (_gate)
            {
                _pending.Enqueue((key, timestamp));
                Sent++;
            }
        }

        internal void Scan(ReadOnlySpan<byte> data)
        {
            var i = 0;
            if (_afterPercent && data.Length > 0)
            {
                _afterPercent = false;
                Match(data[0]);
                i = 1;
            }

            while (i < data.Length)
            {
                var next = data[i..].IndexOf((byte)'%');
                if (next < 0)
                    return;

                i += next + 1;
                if (i == data.Length)
                {
                    _afterPercent = true;
                    return;
                }

                Match(data[i]);
                i++;
            }
        }

        internal double[] Latencies()
        {
            lock (_gate)
                return _latencies.Order().ToArray();
        }

        private void Match(byte key)
        {
            var now = Stopwatch.GetTimestamp();
            lock (_gate)
            {
                var known = false;
                foreach (var entry in _pending)
                {
                    if (entry.Key == key)
                    {
                        known = true;
                        break;
                    }
                }

                // An echo of a key matched before, repainted by ConPTY.
                if (!known)
                    return;

                while (_pending.TryDequeue(out var head))
                {
                    if (head.Key == key)
                    {
                        _latencies.Add(Stopwatch.GetElapsedTime(head.Sent, now).TotalMilliseconds);
                        return;
                    }

                    Lost++;
                }
            }
        }
    }

    /// <summary>Counts the stops of output reading on a full ring (<c>minipty.output.stall.duration</c>).</summary>
    private sealed class StallListener : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _count;
        private double _seconds;

        internal StallListener()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "MiniPty" && instrument.Name == "minipty.output.stall.duration")
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<double>((_, value, _, _) =>
            {
                Interlocked.Increment(ref _count);
                lock (_listener)
                    _seconds += value;
            });
            _listener.Start();
        }

        internal long Count => Interlocked.Read(ref _count);

        internal double Seconds
        {
            get
            {
                lock (_listener)
                    return _seconds;
            }
        }

        public void Dispose() => _listener.Dispose();
    }

    // ---------------------------------------------------------------- Process state

    private readonly record struct Snapshot(
        long Timestamp,
        TimeSpan Cpu,
        TimeSpan? IoThreadCpu,
        int Gen0,
        int Gen1,
        int Gen2,
        long Allocated,
        long WorkItems,
        int Threads,
        int Handles,
        long WorkingSet,
        long PrivateBytes,
        long PeakWorkingSet)
    {
        internal static Snapshot Take()
        {
            var allocated = GC.GetTotalAllocatedBytes(precise: true);
            using var process = Process.GetCurrentProcess();
            // Each process figure is read on its own: FreeBSD's .NET leaves some of them unsupported, and those read -1.
            return new Snapshot(
                Stopwatch.GetTimestamp(),
                Read(() => process.TotalProcessorTime, TimeSpan.Zero),
                IoThread.Cpu(),
                GC.CollectionCount(0),
                GC.CollectionCount(1),
                GC.CollectionCount(2),
                allocated,
                ThreadPool.CompletedWorkItemCount,
                Read(() => process.Threads.Count, -1),
                Read(() => process.HandleCount, -1),
                Read(() => process.WorkingSet64, -1L),
                Read(() => process.PrivateMemorySize64, -1L),
                Read(() => process.PeakWorkingSet64, -1L));
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

    /// <summary>
    /// CPU time per thread of this process, by thread id, with each thread's name: MiniPty's one I/O thread, which reads
    /// every session's output, is <c>MiniPty output loop</c> on Windows and <c>MiniPty child watcher</c> on Unix (Linux
    /// truncates thread names to 15 characters).
    /// </summary>
    private static class IoThread
    {
        internal static TimeSpan? Cpu()
        {
            foreach (var (_, (name, cpu)) in Times())
            {
                if (name is "MiniPty output loop" or "MiniPty child w")
                    return cpu;
            }

            return null;
        }

        /// <summary>Every thread's name and CPU time, by thread id; empty off Windows and Linux.</summary>
        internal static Dictionary<int, (string Name, TimeSpan Cpu)> Times()
        {
            var times = new Dictionary<int, (string, TimeSpan)>();
            if (OperatingSystem.IsLinux())
                Linux(times);
            else if (OperatingSystem.IsWindows())
                Windows(times);

            return times;
        }

        /// <summary>Prints the threads that used the most CPU between two <see cref="Times"/>.</summary>
        internal static void Report(Dictionary<int, (string Name, TimeSpan Cpu)> before, Dictionary<int, (string Name, TimeSpan Cpu)> after, double seconds)
        {
            var rows = new List<(string Name, double Ms)>();
            foreach (var (id, (name, cpu)) in after)
            {
                var start = before.TryGetValue(id, out var earlier) ? earlier.Cpu : TimeSpan.Zero;
                rows.Add((string.IsNullOrEmpty(name) ? "(unnamed)" : name, (cpu - start).TotalMilliseconds));
            }

            System.Console.WriteLine("| Thread | CPU ms | % of one core |");
            System.Console.WriteLine("|---|---|---|");
            foreach (var group in rows.GroupBy(r => r.Name).Select(g => (Name: g.Key, Count: g.Count(), Ms: g.Sum(r => r.Ms))).OrderByDescending(g => g.Ms).Take(8))
                System.Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"| {group.Name} (x{group.Count}) | {group.Ms:F0} | {group.Ms / 10 / seconds:F0} |"));
        }

        private static void Linux(Dictionary<int, (string, TimeSpan)> times)
        {
            foreach (var task in Directory.EnumerateDirectories("/proc/self/task"))
            {
                try
                {
                    var name = File.ReadAllText(Path.Combine(task, "comm")).TrimEnd('\n');
                    var stat = File.ReadAllText(Path.Combine(task, "stat"));
                    var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                    // Fields from the state (field 3) on: utime is field 14 and stime field 15, in clock ticks of 10 ms.
                    var ticks = long.Parse(fields[11], CultureInfo.InvariantCulture) + long.Parse(fields[12], CultureInfo.InvariantCulture);
                    times[int.Parse(Path.GetFileName(task), CultureInfo.InvariantCulture)] = (name, TimeSpan.FromMilliseconds(ticks * 10));
                }
                catch (IOException)
                {
                    // The thread ended meanwhile.
                }
            }
        }

        private static void Windows(Dictionary<int, (string, TimeSpan)> times)
        {
            using var process = Process.GetCurrentProcess();
            foreach (ProcessThread thread in process.Threads)
            {
                using (thread)
                {
                    var handle = OpenThread(0x0800 /* THREAD_QUERY_LIMITED_INFORMATION */, false, (uint)thread.Id);
                    if (handle == 0)
                        continue;

                    try
                    {
                        var name = string.Empty;
                        if (GetThreadDescription(handle, out var description) >= 0 && description != 0)
                        {
                            name = Marshal.PtrToStringUni(description) ?? string.Empty;
                            LocalFree(description);
                        }

                        times[thread.Id] = (name, thread.TotalProcessorTime);
                    }
                    catch (InvalidOperationException)
                    {
                        // The thread ended meanwhile.
                    }
                    finally
                    {
                        CloseHandle(handle);
                    }
                }
            }
        }

        [DllImport("kernel32.dll")]
        private static extern nint OpenThread(uint access, bool inherit, uint threadId);

        [DllImport("kernel32.dll")]
        private static extern int GetThreadDescription(nint thread, out nint description);

        [DllImport("kernel32.dll")]
        private static extern nint LocalFree(nint memory);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(nint handle);

        /// <summary>Makes this process, and the processes it starts from now on, process Ctrl+C.</summary>
        internal static bool EnableCtrlC() => SetConsoleCtrlHandler(0, false);

        [DllImport("kernel32.dll")]
        private static extern bool SetConsoleCtrlHandler(nint handler, bool add);
    }

    /// <summary><c>--name value</c> pairs.</summary>
    private sealed class Options
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        internal Options(string[] args)
        {
            for (var i = 0; i + 1 < args.Length; i += 2)
                _values[args[i].TrimStart('-')] = args[i + 1];

            Library = Get("library", "minipty");
            ServeLibrary = Library switch
            {
                "minipty" => ServeLibrary.MiniPty,
                "porta" => ServeLibrary.PortaPty,
                "porta-async" => ServeLibrary.PortaPtyAsyncIo,
                _ => throw new ArgumentException($"Unknown library \"{Library}\"."),
            };
            ConPty = OperatingSystem.IsWindows() ? Get("conpty", BenchmarkPtyCommands.ConPtyInBox) : BenchmarkPtyCommands.ConPtyNone;
        }

        internal string Library { get; }

        internal ServeLibrary ServeLibrary { get; }

        internal string ConPty { get; }

        /// <summary>
        /// <c>--program</c>: a <c>yes</c>-like program to flood with instead of the benchmark child, given
        /// <see cref="YesLine"/> as its one argument; for telling the terminal's behavior apart from the .NET child's.
        /// </summary>
        internal string? Program => _values.TryGetValue("program", out var value) ? value : null;

        internal string Get(string name, string fallback) => _values.TryGetValue(name, out var value) ? value : fallback;

        internal int GetInt(string name, int fallback) => (int)GetLong(name, fallback);

        internal long GetLong(string name, long fallback) =>
            _values.TryGetValue(name, out var value) ? long.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture) : fallback;

        internal int[] GetList(string name, int[] fallback) =>
            _values.TryGetValue(name, out var value)
                ? value.Split(',').Select(v => int.Parse(v, NumberStyles.None, CultureInfo.InvariantCulture)).ToArray()
                : fallback;
    }
}
