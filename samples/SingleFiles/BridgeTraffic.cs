#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
#:project ../../src/MiniPty/MiniPty.csproj
#:project ../../src/MiniPty.Terminal/MiniPty.Terminal.csproj

// BridgeTraffic: traffic through PtyWebSocketBridge over loopback WebSockets, for the bridges' burst coalescing. It prints
// what each kind of output costs as frames, and what the joining costs interactive output as latency. It starts itself as
// the PTY child of every session.
//
//   dotnet run -c Release samples/SingleFiles/BridgeTraffic.cs -- [--only name,...]
//
// Throughput rows report frames per 32 KiB, each session's rate (first frame to last), and this process's CPU per MiB,
// which includes the client side of the loopback socket. Latency rows report a keystroke's echo: after idle, right after a
// 5 KiB tail, and right after a prompt-like run of small writes, and how long a 6 KiB repaint's tail waits behind its head.
//
// The numbers it prints are recorded in .github/docs/specs/terminal.md (Output frames).

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using MiniPty;
using MiniPty.Terminal;

if (args.Length > 0 && args[0] == "child")
    return Child.Run(args);

var onlyIndex = Array.IndexOf(args, "--only");
var only = onlyIndex >= 0 && onlyIndex + 1 < args.Length ? args[onlyIndex + 1].Split(',') : null;
bool Want(string name) => only is null || only.Contains(name);

Console.WriteLine($"BridgeTraffic: {RuntimeInformation.OSDescription}, {RuntimeInformation.FrameworkDescription}, " +
    $"{Environment.ProcessorCount} logical CPUs");

var throughput = new (string Name, int Sessions, Func<PtyStartInfo> StartInfo)[]
{
    ("flood32x4M", 32, () => Self.Child("flood", $"{4 << 20}")),
    ("flood8x8M", 8, () => Self.Child("flood", $"{8 << 20}")),
    ("command", 1, RealCommand),
    ("steady7M-800", 1, () => Self.Child("steady", "7000000", "800", $"{8 << 20}")),
    ("steady4.5M-500", 1, () => Self.Child("steady", "4500000", "500", $"{8 << 20}")),
    ("steady2.5M-500", 1, () => Self.Child("steady", "2500000", "500", $"{4 << 20}")),
    ("steady2M-100", 1, () => Self.Child("steady", "2000000", "100", $"{4 << 20}")),
    ("steady1.4M-100", 1, () => Self.Child("steady", "1400000", "100", $"{2 << 20}")),
    ("steady1M-100", 1, () => Self.Child("steady", "1000000", "100", $"{2 << 20}")),
    ("steady0.7M-100", 1, () => Self.Child("steady", "700000", "100", $"{1 << 20}")),
    ("steady0.4M-100", 1, () => Self.Child("steady", "400000", "100", $"{1 << 20}")),
};

foreach (var (name, sessions, startInfo) in throughput)
{
    if (!Want(name))
        continue;

    var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
    var results = await Task.WhenAll(Enumerable.Range(0, sessions).Select(_ => RunSessionAsync(startInfo(), echo: null)));
    var cpuMs = (Process.GetCurrentProcess().TotalProcessorTime - cpuBefore).TotalMilliseconds;
    long bytes = results.Sum(r => r.Bytes);
    long frames = results.Sum(r => r.Frames);
    var rates = results.Select(r => r.Seconds > 0 ? r.Bytes / 1e6 / r.Seconds : 0).OrderBy(r => r).ToArray();
    Console.WriteLine(
        $"{name}: {sessions} session(s), {bytes / 1048576.0:F1} MiB, {frames * 32768.0 / Math.Max(1, bytes):F2} frames per 32 KiB, " +
        $"rate per session {rates[0]:F2}–{rates[^1]:F2} MB/s, CPU {cpuMs / Math.Max(1e-9, bytes / 1048576.0):F1} ms per MiB" +
        (results.All(r => r.Exited) ? "" : " (missing exit message)"));
}

// A repaint is one write of 6 KiB every 50 ms; its tail is the time from its first frame to its last.
if (Want("repaint6K"))
{
    var result = await RunSessionAsync(Self.Child("output", "6144", "1", "50", "60"), echo: null);
    var spans = Bursts(result.FrameTimes).Select(b => b.Span).Skip(1).Order().ToArray();
    Console.WriteLine($"repaint6K: {spans.Length} repaints, {result.Frames / (double)Math.Max(1, spans.Length + 1):F2} frames per repaint, " +
        $"tail {Stats(spans)}");
}

// Echo latency: a key sent after idle, 0.5 ms after the head of a 5 KiB tail, and 0.5 ms after the first of four
// back-to-back writes of 40 bytes, as a prompt that repaints itself in pieces.
if (Want("echo-idle"))
    await EchoAsync("echo-idle", Self.Child("output", "0", "1", "50", "60"), afterOutput: false);
if (Want("echo-after-tail"))
    await EchoAsync("echo-after-tail", Self.Child("output", "5120", "1", "100", "60"), afterOutput: true);
if (Want("echo-after-small-writes"))
    await EchoAsync("echo-after-small-writes", Self.Child("output", "160", "4", "100", "60"), afterOutput: true);
// Six writes of 40 bytes 150 µs apart, each read on its own, as a progress line redrawn in steps: the most a run of small
// writes can make a key's echo wait where four chunks within a millisecond start a burst.
if (Want("echo-after-spaced-writes"))
    await EchoAsync("echo-after-spaced-writes", Self.Child("output", "240", "6", "100", "60", "150"), afterOutput: true);

return 0;

static async Task EchoAsync(string name, PtyStartInfo startInfo, bool afterOutput)
{
    var echo = new EchoPlan(afterOutput);
    var result = await RunSessionAsync(startInfo, echo);
    var latencies = echo.Latencies.Skip(2).Order().ToArray();
    Console.WriteLine($"{name}: {latencies.Length} echoes, {Stats(latencies)}");
}

static string Stats(double[] sorted) => sorted.Length == 0
    ? "no samples"
    : $"p50 {sorted[sorted.Length / 2]:F2} ms, p90 {sorted[sorted.Length * 9 / 10]:F2} ms, max {sorted[^1]:F2} ms";

// Frames more than 20 ms apart belong to different bursts of output.
static List<(int First, int Count, double Span)> Bursts(List<long> times)
{
    var bursts = new List<(int, int, double)>();
    var start = 0;
    for (var i = 1; i <= times.Count; i++)
    {
        if (i < times.Count && Stopwatch.GetElapsedTime(times[i - 1], times[i]).TotalMilliseconds < 20)
            continue;

        bursts.Add((start, i - start, Stopwatch.GetElapsedTime(times[start], times[i - 1]).TotalMilliseconds));
        start = i;
    }

    return bursts;
}

static PtyStartInfo RealCommand()
{
    if (OperatingSystem.IsWindows())
    {
        var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
        return new PtyStartInfo { FileName = cmd, Arguments = ["/c", @"dir /s C:\Windows\System32\DriverStore\FileRepository"], Size = new PtySize(120, 40) };
    }

    return new PtyStartInfo { FileName = "/bin/sh", Arguments = ["-c", "ls -lR /usr/share"], Size = new PtySize(120, 40) };
}

static async Task<SessionResult> RunSessionAsync(PtyStartInfo startInfo, EchoPlan? echo)
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    using var client = new TcpClient { NoDelay = true };
    var connect = client.ConnectAsync(IPAddress.Loopback, port);
    using var serverTcp = await listener.AcceptTcpClientAsync();
    serverTcp.NoDelay = true;
    await connect;

    using var serverSocket = WebSocket.CreateFromStream(serverTcp.GetStream(), new WebSocketCreationOptions { IsServer = true });
    using var clientSocket = WebSocket.CreateFromStream(client.GetStream(), new WebSocketCreationOptions { IsServer = false });
    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
    var bridge = PtyWebSocketBridge.RunAsync(startInfo, serverSocket, cancellationToken: cts.Token);
    using var sendLock = new SemaphoreSlim(1, 1);

    async Task SendAsync(byte[] payload, WebSocketMessageType type)
    {
        await sendLock.WaitAsync(cts.Token);
        try
        {
            await clientSocket.SendAsync(payload, type, endOfMessage: true, cts.Token);
        }
        finally
        {
            sendLock.Release();
        }
    }

    var keyTimer = echo is { AfterOutput: false } ? Task.Run(() => echo.SendOnTimerAsync(SendAsync, cts.Token)) : Task.CompletedTask;
    var buffer = new byte[256 * 1024];
    long bytes = 0;
    var frames = 0;
    long first = 0, last = 0;
    var frameTimes = new List<long>();
    var exited = false;
    var received = 0;
    while (true)
    {
        var result = await clientSocket.ReceiveAsync(buffer.AsMemory(received), cts.Token);
        if (result.MessageType == WebSocketMessageType.Close)
        {
            if (clientSocket.State == WebSocketState.CloseReceived)
                await clientSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);
            break;
        }

        received += result.Count;
        if (!result.EndOfMessage)
            continue;

        var length = received;
        received = 0;
        if (result.MessageType == WebSocketMessageType.Binary)
        {
            var now = Stopwatch.GetTimestamp();
            var gap = frames == 0 ? double.MaxValue : Stopwatch.GetElapsedTime(last, now).TotalMilliseconds;
            if (frames == 0)
                first = now;
            last = now;
            frameTimes.Add(now);
            frames++;
            bytes += length;
            echo?.OnFrame(buffer.AsSpan(0, length), now, gap, SendAsync, cts.Token);
            await SendAsync(Encoding.UTF8.GetBytes($"{{\"type\":\"ack\",\"bytes\":{length}}}"), WebSocketMessageType.Text);
            continue;
        }

        if (Encoding.UTF8.GetString(buffer, 0, length).Contains("\"exit\"", StringComparison.Ordinal))
            exited = true;
    }

    await bridge;
    echo?.Stop();
    await keyTimer;
    return new SessionResult(bytes, frames, frames > 1 ? Stopwatch.GetElapsedTime(first, last).TotalSeconds : 0, exited, frameTimes);
}

readonly record struct SessionResult(long Bytes, int Frames, double Seconds, bool Exited, List<long> FrameTimes);

/// <summary>
/// Sends a key, '#', which no child output contains, and times its echo: on a timer after idle, or 0.5 ms after the head
/// of each burst of output. One key is in flight at a time.
/// </summary>
sealed class EchoPlan(bool afterOutput)
{
    private static readonly byte[] Key = "#"u8.ToArray();
    private long _sentAt;
    private volatile bool _stopped;

    public bool AfterOutput => afterOutput;
    public List<double> Latencies { get; } = [];

    public void OnFrame(ReadOnlySpan<byte> frame, long now, double gapMs, Func<byte[], WebSocketMessageType, Task> send, CancellationToken token)
    {
        var sentAt = Interlocked.Read(ref _sentAt);
        if (sentAt != 0 && frame.IndexOf((byte)'#') >= 0)
        {
            lock (Latencies)
                Latencies.Add(Stopwatch.GetElapsedTime(sentAt, now).TotalMilliseconds);
            Interlocked.Exchange(ref _sentAt, 0);
            return;
        }

        // The head of a burst: a frame after 20 ms of quiet.
        if (afterOutput && sentAt == 0 && gapMs > 20 && !_stopped)
            _ = Task.Run(() => SendAfterAsync(Stopwatch.GetTimestamp() + Stopwatch.Frequency / 2000, send, token), CancellationToken.None);
    }

    public async Task SendOnTimerAsync(Func<byte[], WebSocketMessageType, Task> send, CancellationToken token)
    {
        try
        {
            // The child's start-up output comes first.
            await Task.Delay(500, token);
            while (!_stopped)
            {
                if (Interlocked.Read(ref _sentAt) == 0)
                    await SendAfterAsync(Stopwatch.GetTimestamp(), send, token);
                await Task.Delay(50, token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Stop() => _stopped = true;

    private async Task SendAfterAsync(long at, Func<byte[], WebSocketMessageType, Task> send, CancellationToken token)
    {
        while (Stopwatch.GetTimestamp() < at)
            Thread.SpinWait(20);

        if (Interlocked.CompareExchange(ref _sentAt, Stopwatch.GetTimestamp(), 0) != 0)
            return;

        try
        {
            await send(Key, WebSocketMessageType.Binary);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException or IOException)
        {
            // The session ended under the key; it is not an echo to time.
            _stopped = true;
        }
    }
}

static class Child
{
    public static int Run(string[] args)
    {
        using var stdout = Console.OpenStandardOutput();
        switch (args[1])
        {
            case "flood":
                Flood(stdout, long.Parse(args[2]));
                return 0;
            case "steady":
                Steady(stdout, double.Parse(args[2]), int.Parse(args[3]), long.Parse(args[4]));
                return 0;
            case "output":
                Output(stdout, int.Parse(args[2]), int.Parse(args[3]), int.Parse(args[4]), int.Parse(args[5]), args.Length > 6 ? int.Parse(args[6]) : 0);
                return 0;
            default:
                return 2;
        }
    }

    private static void Flood(Stream stdout, long remaining)
    {
        var line = Encoding.ASCII.GetBytes(new string('x', 79) + "\n");
        var block = new byte[64 * 1024 / line.Length * line.Length];
        for (var i = 0; i < block.Length; i += line.Length)
            line.CopyTo(block, i);
        while (remaining > 0)
        {
            var count = (int)Math.Min(remaining, block.Length);
            stdout.Write(block, 0, count);
            remaining -= count;
        }
    }

    // One write per line, each when the rate makes it due. The child spins between writes, so the pace holds at any rate.
    private static void Steady(Stream stdout, double bytesPerSecond, int lineBytes, long total)
    {
        var line = Encoding.ASCII.GetBytes(new string('y', lineBytes - 1) + "\n");
        var start = Stopwatch.GetTimestamp();
        long written = 0;
        while (written < total)
        {
            var due = start + (long)(written / bytesPerSecond * Stopwatch.Frequency);
            while (Stopwatch.GetTimestamp() < due)
                Thread.SpinWait(20);

            stdout.Write(line, 0, line.Length);
            written += line.Length;
        }
    }

    // <bytes> <writes> <intervalMs> <count> [<gapUs>]: every interval, <bytes> of printable text in <writes> writes, <gapUs>
    // apart (back to back by default), while a thread keeps a read of the terminal pending, so that the terminal echoes keys
    // as it would for a shell.
    private static void Output(Stream stdout, int bytes, int writes, int intervalMs, int count, int gapUs)
    {
        var input = new Thread(static () =>
        {
            using var stdin = Console.OpenStandardInput();
            var buffer = new byte[256];
            while (stdin.Read(buffer, 0, buffer.Length) > 0)
            {
            }
        }) { IsBackground = true };
        input.Start();

        var text = new byte[Math.Max(1, bytes)];
        for (var i = 0; i < text.Length; i++)
            text[i] = (byte)(i % 80 == 79 ? '\n' : 'a' + (i % 26));
        var piece = Math.Max(1, bytes / Math.Max(1, writes));
        Thread.Sleep(500);
        for (var i = 0; i < count; i++)
        {
            for (var offset = 0; bytes > 0 && offset < bytes; offset += piece)
            {
                if (offset > 0 && gapUs > 0)
                {
                    var until = Stopwatch.GetTimestamp() + Stopwatch.Frequency * gapUs / 1_000_000;
                    while (Stopwatch.GetTimestamp() < until)
                        Thread.SpinWait(20);
                }

                stdout.Write(text, offset, Math.Min(piece, bytes - offset));
            }

            Thread.Sleep(intervalMs);
        }

        Thread.Sleep(200);
    }
}

static class Self
{
    public static PtyStartInfo Child(params string[] arguments)
    {
        var path = Environment.ProcessPath!;
        string[] prefix = Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? [Path.Combine(AppContext.BaseDirectory, AppDomain.CurrentDomain.FriendlyName + ".dll")]
            : [];
        return new PtyStartInfo { FileName = path, Arguments = [.. prefix, "child", .. arguments], Size = new PtySize(120, 40) };
    }
}
