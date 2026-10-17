#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property PublishAot=true
#:project ../src/MiniPty/MiniPty.csproj
#:project ../src/MiniPty.Terminal/MiniPty.Terminal.csproj

// WebTerminal: xterm.js in a browser, MiniPty.Terminal as the backend PTY.
//
// Interactive mode:  dotnet samples/WebTerminal.cs
//   Serves http://localhost:5170/ with an xterm.js page; each WebSocket connection
//   at /ws spawns the platform shell through PtyWebSocketBridge.
//
// Smoke mode (CI):   dotnet samples/WebTerminal.cs --smoke   (or redirected stdin)
//   Starts the server on an ephemeral port, connects a ClientWebSocket to itself,
//   drives resize + a marker command + exit, and asserts the marker output and the
//   exit control message round-trip. Exits 0 on success.

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

// --serve forces interactive mode (e.g. when launched from a script with redirected stdin).
var smoke = !args.Contains("--serve") && (args.Contains("--smoke") || Console.IsInputRedirected);

try
{
    return smoke ? await RunSmokeAsync() : await RunInteractiveAsync(ParsePort(args) ?? 5170);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
    return 1;
}

static async Task<int> RunInteractiveAsync(int port)
{
    using var listener = StartListener(port);
    Console.Error.WriteLine($"WebTerminal listening on http://localhost:{port}/ (Ctrl+C to stop)");
    await ServeAsync(listener, CancellationToken.None);
    return 0;
}

static async Task<int> RunSmokeAsync()
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    using var listener = StartEphemeralListener(out var port);
    var serveTask = ServeAsync(listener, timeout.Token);

    using var client = new ClientWebSocket();
    await client.ConnectAsync(new Uri($"ws://localhost:{port}/ws"), timeout.Token);

    var enter = OperatingSystem.IsWindows() ? "\r\n" : "\n";
    await SendTextAsync(client, "{\"type\":\"resize\",\"cols\":100,\"rows\":30}", timeout.Token);
    await SendBinaryAsync(client, "echo WEBTERM_SMOKE_OK" + enter, timeout.Token);
    await SendBinaryAsync(client, "exit" + enter, timeout.Token);

    var output = new StringBuilder();
    string? exitMessage = null;
    var buffer = new byte[64 * 1024];
    var message = new MemoryStream();
    while (true)
    {
        var result = await client.ReceiveAsync(buffer.AsMemory(), timeout.Token);
        if (result.MessageType == WebSocketMessageType.Close)
        {
            if (client.State == WebSocketState.CloseReceived)
                await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
            break;
        }

        message.Write(buffer, 0, result.Count);
        if (!result.EndOfMessage)
            continue;

        var payload = message.ToArray();
        message.SetLength(0);
        if (result.MessageType == WebSocketMessageType.Binary)
            output.Append(Encoding.UTF8.GetString(payload));
        else
            exitMessage = Encoding.UTF8.GetString(payload);
    }

    listener.Stop();
    try
    {
        await serveTask;
    }
    catch (OperationCanceledException)
    {
    }

    if (!output.ToString().Contains("WEBTERM_SMOKE_OK"))
    {
        Console.Error.WriteLine("Smoke failed: marker output not observed.");
        Console.Error.WriteLine(output.ToString());
        return 1;
    }

    if (exitMessage is null)
    {
        Console.Error.WriteLine("Smoke failed: exit control message not received.");
        return 1;
    }

    using var exitJson = JsonDocument.Parse(exitMessage);
    if (exitJson.RootElement.GetProperty("type").GetString() != "exit")
    {
        Console.Error.WriteLine($"Smoke failed: unexpected control message: {exitMessage}");
        return 1;
    }

    Console.WriteLine($"WebTerminal smoke OK (exit message: {exitMessage})");
    return 0;
}

static int? ParsePort(string[] args)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == "--port" && int.TryParse(args[i + 1], out var port))
            return port;
    }

    return null;
}

static HttpListener StartListener(int port)
{
    var listener = new HttpListener();
    listener.Prefixes.Add($"http://localhost:{port}/");
    listener.Start();
    return listener;
}

static HttpListener StartEphemeralListener(out int port)
{
    for (var attempt = 0; attempt < 20; attempt++)
    {
        port = Random.Shared.Next(20000, 60000);
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        try
        {
            listener.Start();
            return listener;
        }
        catch (HttpListenerException)
        {
        }
        catch (SocketException)
        {
        }
    }

    throw new InvalidOperationException("Could not bind an ephemeral port for the smoke server.");
}

static async Task ServeAsync(HttpListener listener, CancellationToken cancellationToken)
{
    while (!cancellationToken.IsCancellationRequested)
    {
        HttpListenerContext context;
        try
        {
            context = await listener.GetContextAsync().WaitAsync(cancellationToken);
        }
        catch (Exception e) when (e is OperationCanceledException or HttpListenerException or ObjectDisposedException)
        {
            return;
        }

        _ = HandleRequestAsync(context, cancellationToken);
    }
}

static async Task HandleRequestAsync(HttpListenerContext context, CancellationToken cancellationToken)
{
    try
    {
        var path = context.Request.Url?.AbsolutePath ?? "/";
        if (path == "/ws" && context.Request.IsWebSocketRequest)
        {
            var wsContext = await context.AcceptWebSocketAsync(subProtocol: null);
            Console.Error.WriteLine("terminal session started");
            var status = await PtyWebSocketBridge.RunAsync(
                CreateShellStartInfo(),
                wsContext.WebSocket,
                cancellationToken: cancellationToken);
            Console.Error.WriteLine($"terminal session ended: exit {status.ExitCode}" +
                (status.Signal is { } signal ? $" (signal {signal})" : ""));
            return;
        }

        if (path == "/")
        {
            var body = Encoding.UTF8.GetBytes(IndexHtml);
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body, cancellationToken);
            context.Response.Close();
            return;
        }

        context.Response.StatusCode = 404;
        context.Response.Close();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"request failed: {ex.GetType().Name}: {ex.Message}");
        try
        {
            context.Response.Abort();
        }
        catch
        {
        }
    }
}

static async Task SendBinaryAsync(ClientWebSocket socket, string text, CancellationToken cancellationToken) =>
    await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Binary, endOfMessage: true, cancellationToken);

static async Task SendTextAsync(ClientWebSocket socket, string json, CancellationToken cancellationToken) =>
    await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);

static PtyStartInfo CreateShellStartInfo()
{
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
        return new PtyStartInfo { FileName = cmd, Size = new PtySize(80, 24) };
    }

    var shell = File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";
    return new PtyStartInfo { FileName = shell, Arguments = ["-i"], Size = new PtySize(80, 24) };
}

// The page is embedded so the NativeAOT publish stays a single asset-free executable.
// Client protocol: binary frames = terminal data; text frames = JSON control messages
// (resize / ack from the client, exit from the server). The ack chunk size matches the
// server's default LowWatermark for self-clocking flow control.
partial class Program
{
    const string IndexHtml = """
<!doctype html>
<html>
<head>
<meta charset="utf-8">
<title>MiniPty WebTerminal</title>
<link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/@xterm/xterm@5.5.0/css/xterm.min.css">
<style>html,body{height:100%;margin:0;background:#000}#terminal{height:100%}</style>
</head>
<body>
<div id="terminal"></div>
<script src="https://cdn.jsdelivr.net/npm/@xterm/xterm@5.5.0/lib/xterm.min.js"></script>
<script src="https://cdn.jsdelivr.net/npm/@xterm/addon-fit@0.10.0/lib/addon-fit.min.js"></script>
<script>
const ACK_CHUNK = 131072; // flow-control credit granularity; matches server LowWatermark
const term = new Terminal();
const fit = new FitAddon.FitAddon();
term.loadAddon(fit);
term.open(document.getElementById('terminal'));
fit.fit();
term.focus();

const ws = new WebSocket(`ws://${location.host}/ws`);
ws.binaryType = 'arraybuffer';
const encoder = new TextEncoder();
let unacked = 0;

ws.onopen = () => {
  sendResize();
  term.onData(data => { if (ws.readyState === WebSocket.OPEN) ws.send(encoder.encode(data)); });
  term.onResize(() => sendResize());
};
ws.onmessage = e => {
  if (typeof e.data === 'string') {
    const msg = JSON.parse(e.data);
    if (msg.type === 'exit') {
      const signal = msg.signal ? `, signal ${msg.signal}` : '';
      term.write(`\r\n\x1b[33m[process exited with code ${msg.exitCode}${signal}]\x1b[0m\r\n`);
    }
    return;
  }
  const bytes = new Uint8Array(e.data);
  term.write(bytes, () => {
    unacked += bytes.length;
    if (unacked >= ACK_CHUNK && ws.readyState === WebSocket.OPEN) {
      ws.send(JSON.stringify({ type: 'ack', bytes: unacked }));
      unacked = 0;
    }
  });
};
ws.onclose = () => term.write('\r\n\x1b[31m[connection closed]\x1b[0m\r\n');
window.addEventListener('resize', () => fit.fit());
function sendResize() {
  if (ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify({ type: 'resize', cols: term.cols, rows: term.rows }));
}
</script>
</body>
</html>
""";
}
