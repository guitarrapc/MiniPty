#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property PublishAot=false

// AspireTerminal's load: browser-like clients of terminal.cs that make the dashboard's MiniPty charts move without anyone
// at the keyboard. The AppHost starts it when you press Start on the "load" resource, and Stop ends it.
//
// Each round opens LOAD_SESSIONS idle shells (minipty.session.active and minipty.session.start.duration), floods one more
// session with LOAD_FLOOD_BYTES read by a client that acknowledges slowly (minipty.output.bytes, and
// minipty.output.stall.duration), closes them all, and rests. The slow client is what a browser tab that cannot draw
// fast enough looks like to the server: the bridge stops delivering once 384 KiB are unacknowledged, MiniPty's output
// buffer fills, and reading stops, which blocks the shell until the client catches up. Nothing is lost.
//
// TERMINAL_URL and MINIPTY_TERMINAL_TOKEN come from the AppHost.

using System.Net.WebSockets;
using System.Text;

var terminal = Environment.GetEnvironmentVariable("TERMINAL_URL") is { Length: > 0 } url
    ? new Uri(url)
    : throw new InvalidOperationException("TERMINAL_URL is not set; start this from apphost.cs.");
var token = Environment.GetEnvironmentVariable("MINIPTY_TERMINAL_TOKEN")
    ?? throw new InvalidOperationException("MINIPTY_TERMINAL_TOKEN is not set; start this from apphost.cs.");
var sessions = EnvInt("LOAD_SESSIONS", 8);
var floodBytes = EnvInt("LOAD_FLOOD_BYTES", 4 * 1024 * 1024);
var ackDelay = TimeSpan.FromMilliseconds(EnvInt("LOAD_ACK_DELAY_MS", 100));
// The AppHost passes the endpoint as http://; ClientWebSocket takes ws:// only.
var endpoint = new UriBuilder(terminal) { Scheme = terminal.Scheme == Uri.UriSchemeHttps ? "wss" : "ws", Path = "/ws" }.Uri;

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};
// An orchestrator stops a process on Unix with SIGTERM, which would otherwise end it without its cleanup below.
using var sigterm = OperatingSystem.IsWindows()
    ? null
    : System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, context =>
    {
        context.Cancel = true;
        stop.Cancel();
    });

var floodFile = FloodFile.Create(floodBytes);
try
{
    for (var round = 1; !stop.IsCancellationRequested; round++)
    {
        var idle = new List<IdleClient>(sessions);
        try
        {
            for (var i = 0; i < sessions; i++)
                idle.Add(await IdleClient.ConnectAsync(endpoint, token, stop.Token));

            var flood = await Flood.RunAsync(endpoint, token, floodFile, ackDelay, stop.Token);
            Console.WriteLine($"round {round}: {sessions} idle sessions open; flood read {flood.Bytes / 1024} KiB in " +
                $"{flood.Elapsed.TotalSeconds:F1} s with {flood.Acks} acknowledgements {ackDelay.TotalMilliseconds:F0} ms late" +
                (flood.Exited ? "" : ", but the connection closed before the shell exited"));
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException)
        {
            // The terminal restarted or refused the connection: report it and try again next round.
            Console.WriteLine($"round {round}: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            foreach (var client in idle)
                await client.DisposeAsync();
        }

        await Task.Delay(TimeSpan.FromSeconds(5), stop.Token);
    }
}
catch (OperationCanceledException) when (stop.IsCancellationRequested)
{
}
finally
{
    File.Delete(floodFile);
}

return 0;

static int EnvInt(string name, int fallback) =>
    int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;

/// <summary>The WebSocket a browser tab would open: this run's token offered as a subprotocol, as the page does.</summary>
static class Socket
{
    public static async Task<ClientWebSocket> ConnectAsync(Uri endpoint, string token, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol("minipty");
        socket.Options.AddSubProtocol("minipty-token." + token);
        await socket.ConnectAsync(endpoint, cancellationToken);
        return socket;
    }

    public static Task SendTextAsync(ClientWebSocket socket, string text, CancellationToken cancellationToken) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);

    public static Task SendInputAsync(ClientWebSocket socket, string text, CancellationToken cancellationToken) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Binary, endOfMessage: true, cancellationToken);

    public static Task AckAsync(ClientWebSocket socket, long bytes, CancellationToken cancellationToken) =>
        SendTextAsync(socket, $$"""{"type":"ack","bytes":{{bytes}}}""", cancellationToken);
}

/// <summary>An open tab nobody types into: it acknowledges whatever the shell prints at once, so it never holds output back.</summary>
sealed class IdleClient : IAsyncDisposable
{
    private readonly ClientWebSocket _socket;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _receive;

    private IdleClient(ClientWebSocket socket)
    {
        _socket = socket;
        _receive = ReceiveAsync();
    }

    public static async Task<IdleClient> ConnectAsync(Uri endpoint, string token, CancellationToken cancellationToken) =>
        new(await Socket.ConnectAsync(endpoint, token, cancellationToken));

    private async Task ReceiveAsync()
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var result = await _socket.ReceiveAsync(buffer, _stop.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;
                if (result.MessageType == WebSocketMessageType.Binary)
                    await Socket.AckAsync(_socket, result.Count, _stop.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Closing the socket ends the bridge, and the bridge ends the shell.
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
        }

        _stop.Cancel();
        await _receive;
        _socket.Dispose();
        _stop.Dispose();
    }
}

/// <summary>A shell that prints a file and exits, read by a client that acknowledges each 128 KiB late.</summary>
static class Flood
{
    private const int AckChunk = 128 * 1024;

    public static async Task<(long Bytes, int Acks, TimeSpan Elapsed, bool Exited)> RunAsync(Uri endpoint, string token, string file, TimeSpan ackDelay, CancellationToken cancellationToken)
    {
        using var socket = await Socket.ConnectAsync(endpoint, token, cancellationToken);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var command = OperatingSystem.IsWindows() ? $"type \"{file}\" & exit\r" : $"cat '{file}'; exit\n";
        await Socket.SendInputAsync(socket, command, cancellationToken);

        var buffer = new byte[64 * 1024];
        long bytes = 0;
        long unacked = 0;
        var acks = 0;
        var exited = false;
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    // The bridge's exit message, sent once the shell has exited.
                    exited |= Encoding.UTF8.GetString(buffer, 0, result.Count).Contains("\"exit\"", StringComparison.Ordinal);
                    continue;
                }

                bytes += result.Count;
                unacked += result.Count;
                if (unacked >= AckChunk)
                {
                    // The late acknowledgement: meanwhile the bridge reaches its high watermark and stops delivering.
                    await Task.Delay(ackDelay, cancellationToken);
                    await Socket.AckAsync(socket, unacked, cancellationToken);
                    unacked = 0;
                    acks++;
                }
            }
        }
        catch (WebSocketException) when (exited)
        {
            // The bridge closed the socket after the shell exited, while an acknowledgement was on its way.
        }

        return (bytes, acks, started.Elapsed, exited);
    }
}

/// <summary>Lines of text to flood with, written once per run to a temporary file the shell prints.</summary>
static class FloodFile
{
    public static string Create(int bytes)
    {
        // One name for every run: a run killed before its cleanup leaves this one file, which the next run overwrites.
        var path = Path.Combine(Path.GetTempPath(), "minipty-aspire-load.txt");
        using var writer = new StreamWriter(path);
        var line = 0;
        for (long written = 0; written < bytes; line++)
        {
            var text = $"load line {line,8} " + new string('x', 60);
            writer.WriteLine(text);
            written += text.Length + Environment.NewLine.Length;
        }

        return path;
    }
}
