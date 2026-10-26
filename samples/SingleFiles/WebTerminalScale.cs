#:sdk Microsoft.NET.Sdk.Web
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property PublishAot=true
#:project ../../src/MiniPty/MiniPty.csproj
#:project ../../src/MiniPty.Terminal/MiniPty.Terminal.csproj

// WebTerminalScale: an ASP.NET Core host that serves many MiniPty sessions to browsers, shows what they cost the host
// process, and measures it.
//
// Serve:  dotnet run -c Release samples/SingleFiles/WebTerminalScale.cs
//   Opens http://localhost:5171/ with an xterm.js terminal, buttons that open and close idle sessions from the browser,
//   and the host's session count, thread count, and memory, refreshed every second.
//
// Bench:  dotnet run -c Release samples/SingleFiles/WebTerminalScale.cs -- bench [--sessions 16,64] [--echo 200]
//                                                                             [--flood-bytes 4194304]
//   Starts the server as a child process and opens idle shell sessions over WebSockets in steps. For each step it
//   prints the server's threads and memory and the keystroke echo latency. Then it floods one session and prints
//   throughput, WebSocket frames per 32 KiB, and the server's CPU time per MiB. Run it before and after a change to
//   MiniPty to compare.
//
// Smoke (CI): the published executable with --smoke runs a small bench and exits 0 when every step succeeded.
//
// The numbers it prints are recorded in .github/docs/references/pty_crossplatform.md (Output pump).

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MiniPty;
using MiniPty.Terminal;

if (!Pty.IsSupported)
{
    Console.Error.WriteLine("PTY is not supported on this operating system.");
    return 1;
}

var mode = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : "serve";
try
{
    if (args.Contains("--smoke"))
        return await Bench.RunAsync(["--sessions", "2,4", "--echo", "10", "--flood-bytes", "262144"], smoke: true);

    return mode switch
    {
        "serve" => await Server.RunAsync(args),
        "bench" => await Bench.RunAsync(args, smoke: false),
        "flood" => Flood.Run(args),
        _ => Usage(),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
    return 1;
}

static int Usage()
{
    Console.Error.WriteLine("usage: WebTerminalScale [serve [--port N] | bench [--sessions 16,64] [--echo N] [--flood-bytes N] | --smoke]");
    return 2;
}

/// <summary>The web host: dashboard, statistics, and one PTY session per WebSocket.</summary>
static class Server
{
    private const int MaxFloodBytes = 256 * 1024 * 1024;
    private static int s_sessions;

    public static async Task<int> RunAsync(string[] args)
    {
        var port = Args.GetInt(args, "--port", 5171);
        var quiet = args.Contains("--quiet");

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://localhost:{port}");
        if (quiet)
            builder.Logging.ClearProviders();

        var app = builder.Build();
        app.UseWebSockets();
        app.MapGet("/", static context =>
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            return context.Response.WriteAsync(Page.Html);
        });
        app.MapGet("/stats", WriteStatsAsync);
        app.Map("/ws", HandleTerminalAsync);

        if (!quiet)
            Console.Error.WriteLine($"WebTerminalScale listening on http://localhost:{port}/ (Ctrl+C to stop)");

        await app.RunAsync();
        return 0;
    }

    /// <summary>
    /// One session per WebSocket: the platform shell, or this program's flood mode for <c>?mode=flood&amp;bytes=N</c>.
    /// The request's abort token stops the bridge when the connection ends.
    /// </summary>
    private static async Task HandleTerminalAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var startInfo = context.Request.Query["mode"] == "flood"
            ? Self.FloodStartInfo(Math.Clamp(Args.ParseInt(context.Request.Query["bytes"], 1024 * 1024), 1, MaxFloodBytes))
            : Shell.StartInfo();

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        Interlocked.Increment(ref s_sessions);
        try
        {
            var status = await PtyWebSocketBridge.RunAsync(startInfo, socket, cancellationToken: context.RequestAborted);
            if (context.Request.Query["mode"] == "flood")
                Console.Error.WriteLine($"flood session ended: exit {status.ExitCode}{(status.Signal is { } signal ? $", signal {signal}" : "")}");
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or TimeoutException)
        {
            // The client went away; the bridge has already ended its child.
        }
        catch (Exception ex)
        {
            // Reported even with --quiet: a failed session must not look like a short one.
            Console.Error.WriteLine($"session failed: {ex}");
        }
        finally
        {
            Interlocked.Decrement(ref s_sessions);
        }
    }

    /// <summary>What the open sessions cost this process. Children are separate processes and are not counted.</summary>
    private static async Task WriteStatsAsync(HttpContext context)
    {
        using var process = Process.GetCurrentProcess();
        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";
        await using var writer = new Utf8JsonWriter(context.Response.Body);
        writer.WriteStartObject();
        writer.WriteNumber("sessions", Volatile.Read(ref s_sessions));
        writer.WriteNumber("threads", process.Threads.Count);
        writer.WriteNumber("poolThreads", ThreadPool.ThreadCount);
        writer.WriteNumber("workingSet", process.WorkingSet64);
        writer.WriteNumber("privateBytes", process.PrivateMemorySize64);
        writer.WriteNumber("gcHeap", GC.GetTotalMemory(forceFullCollection: false));
        writer.WriteNumber("cpuMs", process.TotalProcessorTime.TotalMilliseconds);
        writer.WriteNumber("handles", HandleCount(process));
        writer.WriteString("os", RuntimeInformation.OSDescription);
        writer.WriteString("runtime", RuntimeInformation.FrameworkDescription);
        writer.WriteEndObject();
        await writer.FlushAsync();
    }

    private static int HandleCount(Process process)
    {
        try
        {
            return process.HandleCount;
        }
        catch (PlatformNotSupportedException)
        {
            return -1;
        }
    }
}

/// <summary>Starts the server as a child process and measures it from outside, so client sockets are not counted.</summary>
static class Bench
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan EchoTimeout = TimeSpan.FromSeconds(10);

    public static async Task<int> RunAsync(string[] args, bool smoke)
    {
        var steps = Args.GetIntList(args, "--sessions", [16, 64]);
        var echoSamples = Args.GetInt(args, "--echo", 200);
        var floodBytes = Args.GetInt(args, "--flood-bytes", 4 * 1024 * 1024);
        var port = FreePort();
        var baseUri = new Uri($"http://127.0.0.1:{port}/");

        using var server = Self.Start(["serve", "--port", port.ToString(), "--quiet"]);
        using var http = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(30) };
        var clients = new List<Client>();
        try
        {
            await WaitForServerAsync(http, server);

            // One session opened, used, and closed first, so that startup work (JIT, Kestrel, the first spawn, the
            // watcher's and the thread pool's first threads) is not counted as the cost of the measured sessions.
            await using (var warm = await Client.ConnectAsync(baseUri, "shell"))
            {
                await warm.Ready.WaitAsync(ConnectTimeout);
                await warm.EchoAsync(EchoTimeout);
            }

            await WaitForSessionsAsync(http, 0);
            var baseline = await SettledStatsAsync(http);

            var rows = new List<(int Sessions, Stats Stats, double[] Echo)>();
            foreach (var target in steps)
            {
                await OpenUntilAsync(baseUri, clients, target);
                var stats = await SettledStatsAsync(http);
                var echo = await MeasureEchoAsync(clients, echoSamples);
                rows.Add((target, stats, echo));
            }

            await CloseAllAsync(clients);
            await WaitForSessionsAsync(http, 0);

            var flood = await MeasureFloodAsync(http, baseUri, floodBytes);
            Report(baseline, rows, flood);

            if (smoke)
            {
                var ok = rows.Count == steps.Length && rows.All(r => r.Echo.Length > 0) && flood.Bytes > 0 && flood.Exited;
                Console.WriteLine(ok ? "WebTerminalScale smoke OK" : "WebTerminalScale smoke FAILED");
                return ok ? 0 : 1;
            }

            return 0;
        }
        finally
        {
            await CloseAllAsync(clients);
            try
            {
                server.Kill(entireProcessTree: true);
                await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
            {
            }
        }
    }

    private static async Task OpenUntilAsync(Uri baseUri, List<Client> clients, int target)
    {
        // Connect in small batches: a burst of spawns on a small machine otherwise times out the slowest.
        while (clients.Count < target)
        {
            var batch = Math.Min(16, target - clients.Count);
            var connects = new Task<Client>[batch];
            for (var i = 0; i < batch; i++)
                connects[i] = Client.ConnectAsync(baseUri, "shell");

            foreach (var connect in connects)
                clients.Add(await connect);
        }

        foreach (var client in clients)
            await client.Ready.WaitAsync(ConnectTimeout);
    }

    /// <summary>
    /// Types one character into a session and waits for its echo, then erases it; round-robin over the sessions so
    /// every one is exercised. Returns the latencies in milliseconds, sorted.
    /// </summary>
    private static async Task<double[]> MeasureEchoAsync(List<Client> clients, int samples)
    {
        var latencies = new List<double>(samples);
        for (var i = 0; i < samples; i++)
        {
            var latency = await clients[i % clients.Count].EchoAsync(EchoTimeout);
            if (latency is { } ms)
                latencies.Add(ms);
        }

        latencies.Sort();
        return [.. latencies];
    }

    private static async Task<FloodResult> MeasureFloodAsync(HttpClient http, Uri baseUri, int bytes)
    {
        // The host's CPU over the flood: its bridge, its output loop, and its thread-pool workers, not the client's.
        var before = await GetStatsAsync(http);
        await using var client = await Client.ConnectAsync(baseUri, $"flood&bytes={bytes}");
        await client.Closed.WaitAsync(TimeSpan.FromMinutes(5));
        var after = await GetStatsAsync(http);
        // Only the bridge's exit message means the child finished; a connection that just closed may have lost output.
        return new FloodResult(client.Bytes, client.Frames, client.OutputSeconds, client.ExitMessage is not null, after.CpuMs - before.CpuMs);
    }

    private static async Task CloseAllAsync(List<Client> clients)
    {
        foreach (var client in clients)
            await client.DisposeAsync();

        clients.Clear();
    }

    private static async Task WaitForServerAsync(HttpClient http, Process server)
    {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 60;
        while (true)
        {
            if (server.HasExited)
                throw new InvalidOperationException($"The server exited with code {server.ExitCode} before it was ready.");

            try
            {
                await GetStatsAsync(http);
                return;
            }
            catch (HttpRequestException) when (Stopwatch.GetTimestamp() < deadline)
            {
                await Task.Delay(200);
            }
        }
    }

    private static async Task WaitForSessionsAsync(HttpClient http, int sessions)
    {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 60;
        while ((await GetStatsAsync(http)).Sessions != sessions)
        {
            if (Stopwatch.GetTimestamp() > deadline)
                throw new TimeoutException($"The server did not reach {sessions} sessions.");

            await Task.Delay(200);
        }
    }

    /// <summary>
    /// Statistics once startup work has settled: the lowest thread count over a few samples, since the runtime starts
    /// and retires helper threads on its own, and the memory of the last sample.
    /// </summary>
    private static async Task<Stats> SettledStatsAsync(HttpClient http)
    {
        await Task.Delay(1000);
        var settled = await GetStatsAsync(http);
        for (var i = 0; i < 4; i++)
        {
            await Task.Delay(300);
            var next = await GetStatsAsync(http);
            settled = next with { Threads = Math.Min(settled.Threads, next.Threads) };
        }

        return settled;
    }

    private static async Task<Stats> GetStatsAsync(HttpClient http)
    {
        using var json = JsonDocument.Parse(await http.GetStringAsync("stats"));
        var root = json.RootElement;
        return new Stats(
            root.GetProperty("sessions").GetInt32(),
            root.GetProperty("threads").GetInt32(),
            root.GetProperty("poolThreads").GetInt32(),
            root.GetProperty("workingSet").GetInt64(),
            root.GetProperty("privateBytes").GetInt64(),
            root.GetProperty("gcHeap").GetInt64(),
            root.GetProperty("handles").GetInt32(),
            root.GetProperty("os").GetString() ?? "",
            root.GetProperty("runtime").GetString() ?? "",
            root.TryGetProperty("cpuMs", out var cpu) ? cpu.GetDouble() : 0);
    }

    private static void Report(Stats baseline, List<(int Sessions, Stats Stats, double[] Echo)> rows, FloodResult flood)
    {
        Console.WriteLine($"WebTerminalScale bench: {baseline.Os}, {baseline.Runtime}, {Environment.ProcessorCount} logical CPUs");
        Console.WriteLine();
        Console.WriteLine($"Baseline after warm-up (0 sessions): {baseline.Threads} threads, {baseline.PoolThreads} pool threads, " +
            $"working set {Mb(baseline.WorkingSet)} MB, private {Mb(baseline.PrivateBytes)} MB, GC heap {Mb(baseline.GcHeap)} MB, " +
            $"{baseline.Handles} handles");
        Console.WriteLine();
        // Thread-pool threads grow and shrink with load on their own (a burst of connections injects workers), so the
        // per-session column counts only the other threads: MiniPty's reader and exit-watcher threads among them.
        Console.WriteLine("| Sessions | Threads | Pool threads | +Other threads per session | Working set (MB) | +KB per session | Private (MB) | Handles | Echo p50 (ms) | Echo p99 (ms) |");
        Console.WriteLine("|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var (sessions, stats, echo) in rows)
        {
            var otherThreads = stats.Threads - stats.PoolThreads - (baseline.Threads - baseline.PoolThreads);
            var kbPerSession = (stats.WorkingSet - baseline.WorkingSet) / 1024.0 / sessions;
            Console.WriteLine($"| {sessions} | {stats.Threads} | {stats.PoolThreads} | {otherThreads / (double)sessions:F2} | {Mb(stats.WorkingSet)} | " +
                $"{kbPerSession:F0} | {Mb(stats.PrivateBytes)} | {stats.Handles} | {Percentile(echo, 0.50):F2} | {Percentile(echo, 0.99):F2} |");
        }

        Console.WriteLine();
        var framesPer32KiB = flood.Bytes == 0 ? 0 : flood.Frames * 32768.0 / flood.Bytes;
        var mbPerSecond = flood.Seconds <= 0 ? 0 : flood.Bytes / 1048576.0 / flood.Seconds;
        var cpuMsPerMiB = flood.Bytes == 0 ? 0 : flood.HostCpuMs / (flood.Bytes / 1048576.0);
        Console.WriteLine($"Flood: {flood.Bytes / 1024.0:F0} KiB in {flood.Frames} frames (average {flood.Bytes / 1024.0 / Math.Max(1, flood.Frames):F1} KiB), " +
            $"{framesPer32KiB:F1} frames per 32 KiB, {mbPerSecond:F1} MB/s, host CPU {cpuMsPerMiB:F1} ms per MiB" +
            $"{(flood.Exited ? "" : " (no exit message)")}");
    }

    private static double Percentile(double[] sorted, double p) =>
        sorted.Length == 0 ? double.NaN : sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(p * sorted.Length) - 1)];

    private static string Mb(long bytes) => (bytes / 1048576.0).ToString("F1");

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed record Stats(int Sessions, int Threads, int PoolThreads, long WorkingSet, long PrivateBytes, long GcHeap, int Handles, string Os, string Runtime, double CpuMs);

    private sealed record FloodResult(long Bytes, int Frames, double Seconds, bool Exited, double HostCpuMs);
}

/// <summary>
/// One browser-like client: receives output, acknowledges it as the xterm.js flow-control guide describes, and can
/// type a character and time its echo.
/// </summary>
sealed class Client : IAsyncDisposable
{
    private const int AckChunk = 131072;
    // A character that neither a prompt nor a console title is likely to contain.
    private const byte EchoMarker = (byte)'q';
    private static readonly byte[] Erase = [0x7f];

    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();
    private Task _receive = Task.CompletedTask;
    private TaskCompletionSource? _awaitMarker;
    private TaskCompletionSource? _awaitAny;
    private long _firstOutput;
    private long _lastOutput;
    private long _unacked;

    public Task Ready => _ready.Task;

    /// <summary>Completes once the connection has ended, normally or not.</summary>
    public Task Closed => _closed.Task;

    /// <summary>The bridge's exit message, or null while the child has not been reported as exited.</summary>
    public string? ExitMessage { get; private set; }
    public long Bytes { get; private set; }
    public int Frames { get; private set; }
    public double OutputSeconds => _firstOutput == 0 ? 0 : (_lastOutput - _firstOutput) / (double)Stopwatch.Frequency;

    public static async Task<Client> ConnectAsync(Uri baseUri, string mode)
    {
        var client = new Client();
        var uri = new Uri(baseUri, $"ws?mode={mode}");
        await client._socket.ConnectAsync(new Uri(uri.ToString().Replace("http://", "ws://", StringComparison.Ordinal)), CancellationToken.None);
        client._receive = client.ReceiveLoopAsync();
        return client;
    }

    /// <summary>Returns the echo latency in milliseconds, or null when no echo arrived in time.</summary>
    public async Task<double?> EchoAsync(TimeSpan timeout)
    {
        var marker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _awaitMarker, marker);
        var start = Stopwatch.GetTimestamp();
        await SendAsync([EchoMarker], WebSocketMessageType.Binary);
        try
        {
            await marker.Task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            return null;
        }

        var latency = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        // Erase the character so the line does not grow; its echo is not timed.
        var any = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _awaitAny, any);
        await SendAsync(Erase, WebSocketMessageType.Binary);
        await Task.WhenAny(any.Task, Task.Delay(1000));
        return latency;
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        var text = new MemoryStream();
        try
        {
            while (true)
            {
                var result = await _socket.ReceiveAsync(buffer.AsMemory(), _stop.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (_socket.State == WebSocketState.CloseReceived)
                        await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    text.Write(buffer, 0, result.Count);
                    if (result.EndOfMessage)
                    {
                        var message = Encoding.UTF8.GetString(text.GetBuffer(), 0, (int)text.Length);
                        if (message.Contains("\"exit\"", StringComparison.Ordinal))
                            ExitMessage = message;
                        text.SetLength(0);
                    }

                    continue;
                }

                OnOutput(buffer.AsSpan(0, result.Count), result.EndOfMessage);
                _unacked += result.Count;
                if (_unacked >= AckChunk)
                {
                    var ack = Encoding.UTF8.GetBytes($"{{\"type\":\"ack\",\"bytes\":{_unacked}}}");
                    _unacked = 0;
                    await SendAsync(ack, WebSocketMessageType.Text);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
        }
        finally
        {
            _ready.TrySetResult();
            _closed.TrySetResult();
        }
    }

    private void OnOutput(ReadOnlySpan<byte> data, bool endOfMessage)
    {
        var now = Stopwatch.GetTimestamp();
        if (_firstOutput == 0)
            _firstOutput = now;

        _lastOutput = now;
        Bytes += data.Length;
        if (endOfMessage)
            Frames++;

        _ready.TrySetResult();
        if (data.Contains(EchoMarker))
            Interlocked.Exchange(ref _awaitMarker, null)?.TrySetResult();

        Interlocked.Exchange(ref _awaitAny, null)?.TrySetResult();
    }

    private async Task SendAsync(byte[] payload, WebSocketMessageType type)
    {
        await _sendLock.WaitAsync();
        try
        {
            if (_socket.State == WebSocketState.Open)
                await _socket.SendAsync(payload, type, endOfMessage: true, CancellationToken.None);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _sendLock.WaitAsync(timeout.Token);
                try
                {
                    await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
                }
                finally
                {
                    _sendLock.Release();
                }

                await _receive.WaitAsync(timeout.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or TimeoutException)
        {
        }
        finally
        {
            _stop.Cancel();
            _socket.Abort();
            _socket.Dispose();
            _stop.Dispose();
        }
    }
}

/// <summary>The child for <c>?mode=flood</c>: writes printable lines to its terminal as fast as it can, then exits.</summary>
static class Flood
{
    public static int Run(string[] args)
    {
        var remaining = args.Length > 1 && long.TryParse(args[1], out var n) ? n : 1024 * 1024;
        var line = Encoding.ASCII.GetBytes(new string('x', 79) + "\n");
        var block = new byte[64 * 1024 / line.Length * line.Length];
        for (var i = 0; i < block.Length; i += line.Length)
            line.CopyTo(block, i);

        using var stdout = Console.OpenStandardOutput();
        while (remaining > 0)
        {
            var count = (int)Math.Min(remaining, block.Length);
            stdout.Write(block, 0, count);
            remaining -= count;
        }

        stdout.Flush();
        return 0;
    }
}

static class Shell
{
    public static PtyStartInfo StartInfo()
    {
        if (OperatingSystem.IsWindows())
        {
            var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
            return new PtyStartInfo { FileName = cmd, Size = new PtySize(80, 24) };
        }

        // No startup files, so that a user's shell configuration does not change what is measured.
        return File.Exists("/bin/bash")
            ? new PtyStartInfo { FileName = "/bin/bash", Arguments = ["--noprofile", "--norc", "-i"], Size = new PtySize(80, 24) }
            : new PtyStartInfo { FileName = "/bin/sh", Arguments = ["-i"], Size = new PtySize(80, 24) };
    }
}

/// <summary>Runs this program again, as the bench's server or as a flood child.</summary>
static class Self
{
    public static PtyStartInfo FloodStartInfo(int bytes)
    {
        var (fileName, prefix) = Command();
        return new PtyStartInfo { FileName = fileName, Arguments = [.. prefix, "flood", bytes.ToString()], Size = new PtySize(80, 24) };
    }

    public static Process Start(string[] arguments)
    {
        var (fileName, prefix) = Command();
        var startInfo = new ProcessStartInfo(fileName) { UseShellExecute = false };
        foreach (var argument in prefix)
            startInfo.ArgumentList.Add(argument);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the server process.");
    }

    /// <summary>
    /// The executable that runs this program: the apphost or NativeAOT executable itself, or <c>dotnet</c> plus the
    /// assembly when the program was started through the muxer.
    /// </summary>
    private static (string FileName, string[] Prefix) Command()
    {
        var path = Environment.ProcessPath ?? throw new InvalidOperationException("The process path is unknown.");
        if (!Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return (path, []);

        var assembly = Path.Combine(AppContext.BaseDirectory, AppDomain.CurrentDomain.FriendlyName + ".dll");
        return (path, [assembly]);
    }
}

static class Args
{
    public static int GetInt(string[] args, string name, int fallback)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var value) ? value : fallback;
    }

    public static int[] GetIntList(string[] args, string name, int[] fallback)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0 || index + 1 >= args.Length)
            return fallback;

        var values = new List<int>();
        foreach (var part in args[index + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, out var value) && value > 0)
                values.Add(value);
        }

        values.Sort();
        return values.Count > 0 ? [.. values] : fallback;
    }

    public static int ParseInt(string? text, int fallback) => int.TryParse(text, out var value) ? value : fallback;
}

/// <summary>
/// The dashboard. Embedded so the NativeAOT publish stays a single asset-free executable. Its terminal speaks the
/// <c>PtyWebSocketBridge</c> protocol (binary frames for data, JSON text frames for resize, ack, and exit), and its
/// idle sessions acknowledge output the same way, so they never stall on flow control.
/// </summary>
static class Page
{
    public const string Html = """
<!doctype html>
<html>
<head>
<meta charset="utf-8">
<title>MiniPty WebTerminalScale</title>
<link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/@xterm/xterm@5.5.0/css/xterm.min.css">
<style>
  html,body{height:100%;margin:0;background:#111;color:#ddd;font:14px system-ui,sans-serif}
  #layout{display:flex;height:100%}
  #terminal{flex:1;min-width:0;background:#000}
  #panel{width:340px;padding:12px;box-sizing:border-box;overflow:auto;border-left:1px solid #333}
  h1{font-size:16px;margin:0 0 8px}
  table{border-collapse:collapse;width:100%;margin:8px 0}
  td{padding:3px 4px;border-bottom:1px solid #2a2a2a}
  td:last-child{text-align:right;font-variant-numeric:tabular-nums}
  button{margin:2px 4px 2px 0;padding:4px 8px;background:#2b2b2b;color:#ddd;border:1px solid #444;border-radius:4px;cursor:pointer}
  canvas{width:100%;height:140px;background:#181818;border:1px solid #2a2a2a}
  .legend span{display:inline-block;margin-right:12px}
  .note{color:#888;font-size:12px}
</style>
</head>
<body>
<div id="layout">
  <div id="terminal"></div>
  <div id="panel">
    <h1>Host cost</h1>
    <table id="stats"></table>
    <canvas id="chart" width="640" height="280"></canvas>
    <div class="legend"><span style="color:#4fc3f7">&#9632; sessions</span><span style="color:#ffb74d">&#9632; threads</span></div>
    <h1 style="margin-top:12px">Idle sessions from this page</h1>
    <div><button data-open="10">+10</button><button data-open="50">+50</button><button id="close">Close all</button></div>
    <p id="idle">0 idle sessions open</p>
    <p class="note">Each session runs a shell in the host. Threads that grow with sessions are the per-session reader
    threads that thread-free I/O removes. Browsers cap WebSocket connections per page (about 200&ndash;255); use the
    bench mode for more.</p>
  </div>
</div>
<script src="https://cdn.jsdelivr.net/npm/@xterm/xterm@5.5.0/lib/xterm.min.js"></script>
<script src="https://cdn.jsdelivr.net/npm/@xterm/addon-fit@0.10.0/lib/addon-fit.min.js"></script>
<script>
const ACK_CHUNK = 131072;
const wsUrl = `ws://${location.host}/ws`;
const encoder = new TextEncoder();

function ack(ws, state, length) {
  state.unacked += length;
  if (state.unacked >= ACK_CHUNK && ws.readyState === WebSocket.OPEN) {
    ws.send(JSON.stringify({ type: 'ack', bytes: state.unacked }));
    state.unacked = 0;
  }
}

// The interactive terminal.
const term = new Terminal();
const fit = new FitAddon.FitAddon();
term.loadAddon(fit);
term.open(document.getElementById('terminal'));
fit.fit();
term.focus();
const ws = new WebSocket(wsUrl);
ws.binaryType = 'arraybuffer';
const termState = { unacked: 0 };
ws.onopen = () => {
  sendResize();
  term.onData(data => { if (ws.readyState === WebSocket.OPEN) ws.send(encoder.encode(data)); });
  term.onResize(() => sendResize());
};
ws.onmessage = e => {
  if (typeof e.data === 'string') {
    const msg = JSON.parse(e.data);
    if (msg.type === 'exit') term.write(`\r\n\x1b[33m[process exited with code ${msg.exitCode}]\x1b[0m\r\n`);
    return;
  }
  const bytes = new Uint8Array(e.data);
  term.write(bytes, () => ack(ws, termState, bytes.length));
};
ws.onclose = () => term.write('\r\n\x1b[31m[connection closed]\x1b[0m\r\n');
window.addEventListener('resize', () => fit.fit());
function sendResize() {
  if (ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify({ type: 'resize', cols: term.cols, rows: term.rows }));
}

// Idle sessions: a shell each, output acknowledged and discarded.
const idle = [];
function updateIdle() { document.getElementById('idle').textContent = `${idle.length} idle sessions open`; }
document.querySelectorAll('[data-open]').forEach(button => button.onclick = () => {
  for (let i = 0; i < Number(button.dataset.open); i++) {
    const s = new WebSocket(wsUrl);
    s.binaryType = 'arraybuffer';
    const state = { unacked: 0 };
    s.onmessage = e => { if (typeof e.data !== 'string') ack(s, state, e.data.byteLength); };
    s.onclose = () => { const i = idle.indexOf(s); if (i >= 0) idle.splice(i, 1); updateIdle(); };
    idle.push(s);
  }
  updateIdle();
});
document.getElementById('close').onclick = () => { for (const s of [...idle]) s.close(); };

// Statistics, refreshed every second, with a chart of the last two minutes.
const history = [];
const mb = n => (n / 1048576).toFixed(1) + ' MB';
async function refresh() {
  try {
    const s = await (await fetch('/stats', { cache: 'no-store' })).json();
    const rows = [
      ['Sessions', s.sessions], ['Threads', s.threads], ['Thread-pool threads', s.poolThreads],
      ['Working set', mb(s.workingSet)], ['Private bytes', mb(s.privateBytes)], ['GC heap', mb(s.gcHeap)],
      ['Handles', s.handles], ['OS', s.os], ['Runtime', s.runtime]];
    document.getElementById('stats').innerHTML = rows.map(([k, v]) => `<tr><td>${k}</td><td>${v}</td></tr>`).join('');
    history.push(s);
    if (history.length > 120) history.shift();
    draw();
  } catch { }
}
function draw() {
  const c = document.getElementById('chart'), g = c.getContext('2d');
  g.clearRect(0, 0, c.width, c.height);
  const max = Math.max(10, ...history.map(h => Math.max(h.sessions, h.threads)));
  for (const [key, color] of [['sessions', '#4fc3f7'], ['threads', '#ffb74d']]) {
    g.strokeStyle = color; g.lineWidth = 3; g.beginPath();
    history.forEach((h, i) => {
      const x = i * c.width / 119, y = c.height - 8 - h[key] * (c.height - 16) / max;
      i ? g.lineTo(x, y) : g.moveTo(x, y);
    });
    g.stroke();
  }
  g.fillStyle = '#888'; g.font = '20px system-ui'; g.fillText(String(max), 6, 24);
}
refresh();
setInterval(refresh, 1000);
</script>
</body>
</html>
""";
}
