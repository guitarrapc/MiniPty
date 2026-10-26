#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property PublishAot=true
#:project ../../src/MiniPty/MiniPty.csproj
#:project ../../src/MiniPty.Terminal/MiniPty.Terminal.csproj

// WebTerminal: xterm.js in a browser, MiniPty.Terminal as the backend PTY.
//
// Interactive mode:  dotnet samples/SingleFiles/WebTerminal.cs
//   Serves http://localhost:5170/ with an xterm.js page; each WebSocket connection
//   at /ws spawns the platform shell through PtyWebSocketBridge. Open the address the
//   server prints: its #token=... fragment is this run's access token (see LocalAccess).
//
// Smoke mode (CI):   dotnet samples/SingleFiles/WebTerminal.cs --smoke   (or redirected stdin)
//   Starts the server on an ephemeral port, checks that connections without the token or
//   from another site's page are refused, connects a ClientWebSocket to itself, drives
//   resize + a marker command + exit, and asserts the marker output and the exit control
//   message round-trip. Exits 0 on success.

using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
    var token = LocalAccess.CreateToken();
    // The address carries the token, so it is printed only to this console, for the user who started the server.
    Console.Error.WriteLine($"WebTerminal listening on http://localhost:{port}/#token={token} (Ctrl+C to stop)");
    await ServeAsync(listener, port, token, CancellationToken.None);
    return 0;
}

static async Task<int> RunSmokeAsync()
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    using var listener = StartEphemeralListener(out var port);
    var token = LocalAccess.CreateToken();
    var serveTask = ServeAsync(listener, port, token, timeout.Token);
    var endpoint = new Uri($"ws://localhost:{port}/ws");

    // Each of these would otherwise get a shell: no token, a wrong token, and the right token from another site's page.
    if (!await IsRefusedAsync(endpoint, [LocalAccess.Protocol], origin: null, timeout.Token)
        || !await IsRefusedAsync(endpoint, LocalAccess.Protocols(LocalAccess.CreateToken()), origin: null, timeout.Token)
        || !await IsRefusedAsync(endpoint, LocalAccess.Protocols(token), origin: "https://attacker.example", timeout.Token))
    {
        Console.Error.WriteLine("Smoke failed: an unauthorized connection was accepted.");
        return 1;
    }

    using var client = new ClientWebSocket();
    foreach (var protocol in LocalAccess.Protocols(token))
        client.Options.AddSubProtocol(protocol);
    await client.ConnectAsync(endpoint, timeout.Token);

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

static async Task ServeAsync(HttpListener listener, int port, string token, CancellationToken cancellationToken)
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

        _ = HandleRequestAsync(context, port, token, cancellationToken);
    }
}

static async Task HandleRequestAsync(HttpListenerContext context, int port, string token, CancellationToken cancellationToken)
{
    try
    {
        var path = context.Request.Url?.AbsolutePath ?? "/";
        if (path == "/ws" && context.Request.IsWebSocketRequest)
        {
            // PtyWebSocketBridge authenticates nothing: whoever holds the socket types into the shell.
            var refusal = LocalAccess.Check(
                context.Request.Headers["Host"],
                context.Request.Headers["Origin"],
                context.Request.Headers["Sec-WebSocket-Protocol"],
                port,
                token);
            if (refusal is not null)
            {
                Console.Error.WriteLine($"refused a terminal connection: {refusal}");
                context.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                context.Response.Close();
                return;
            }

            var wsContext = await context.AcceptWebSocketAsync(LocalAccess.Protocol);
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

static async Task<bool> IsRefusedAsync(Uri endpoint, string[] protocols, string? origin, CancellationToken cancellationToken)
{
    using var socket = new ClientWebSocket();
    foreach (var protocol in protocols)
        socket.Options.AddSubProtocol(protocol);
    if (origin is not null)
        socket.Options.SetRequestHeader("Origin", origin);

    try
    {
        await socket.ConnectAsync(endpoint, cancellationToken);
    }
    catch (WebSocketException)
    {
        return true;
    }

    // Accepted: aborting the socket ends the bridge, and with it the shell it started.
    socket.Abort();
    return false;
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

/// <summary>
/// Decides whether a WebSocket request may open a shell. <see cref="PtyWebSocketBridge"/> authenticates nothing, so the
/// host must. Listening on localhost is not enough: browsers let any page open a WebSocket to any host, loopback
/// included, and leave the Origin check to the server, so without these checks every site the user visits while the
/// server runs could type into a shell.
/// </summary>
/// <remarks>
/// A request must pass all three checks:
/// <list type="bullet">
/// <item><description>
/// <c>Host</c> is this server's loopback name and port. A page that points its own domain at 127.0.0.1 (DNS
/// rebinding) is same-origin with itself and sends that domain as Host, so it is refused here.
/// </description></item>
/// <item><description>
/// <c>Origin</c>, when present, is this server's own origin. Browsers always send it on a WebSocket request, so a page
/// from another site is refused. A client that is not a browser sends none and is judged by the token alone.
/// </description></item>
/// <item><description>
/// The request offers this run's token as the subprotocol <c>minipty-token.&lt;hex&gt;</c>. Other local users can
/// reach a loopback port and forge any header, but not the token. The browser gets it from the URL fragment the
/// server prints, which browsers never send to a server, and sends it in a header rather than the URL, so request logs
/// do not record it.
/// </description></item>
/// </list>
/// </remarks>
static class LocalAccess
{
    /// <summary>The subprotocol the server answers with; a browser fails a handshake that answers none it offered.</summary>
    public const string Protocol = "minipty";

    private const string TokenPrefix = "minipty-token.";

    /// <summary>A random 256-bit token as 64 lowercase hex characters, created once per run.</summary>
    public static string CreateToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    /// <summary>The subprotocols a client offers to present <paramref name="token"/>.</summary>
    public static string[] Protocols(string token) => [Protocol, TokenPrefix + token];

    /// <summary>Returns why the request is refused, or null when it may open a shell. The reason never holds the token.</summary>
    public static string? Check(string? host, string? origin, string? protocols, int port, string token)
    {
        if (!IsLoopbackHost(host, port))
            return "Host is not this server";

        if (!string.IsNullOrEmpty(origin) && !string.Equals(origin, "http://" + host, StringComparison.OrdinalIgnoreCase))
            return "Origin is another site";

        return OffersToken(protocols, token) ? null : "missing or wrong access token";
    }

    private static bool IsLoopbackHost(string? host, int port)
    {
        if (host is null)
            return false;

        var suffix = ":" + port;
        if (!host.EndsWith(suffix, StringComparison.Ordinal))
            return false;

        var name = host[..^suffix.Length];
        return name.Equals("localhost", StringComparison.OrdinalIgnoreCase) || name == "127.0.0.1" || name == "[::1]";
    }

    private static bool OffersToken(string? protocols, string token)
    {
        if (protocols is null)
            return false;

        foreach (var protocol in protocols.Split(',', StringSplitOptions.TrimEntries))
        {
            if (protocol.StartsWith(TokenPrefix, StringComparison.Ordinal))
            {
                // Compared in fixed time so the response time does not reveal how much of a guess matched.
                return CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(protocol[TokenPrefix.Length..]),
                    Encoding.ASCII.GetBytes(token));
            }
        }

        return false;
    }
}

// The page is embedded so the NativeAOT publish stays a single asset-free executable.
// Client protocol: binary frames = terminal data; text frames = JSON control messages
// (resize / ack from the client, exit from the server). The ack chunk size matches the
// server's default LowWatermark for self-clocking flow control. The page itself holds no
// secret: it reads the access token from its own URL fragment (see LocalAccess).
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
term.attachCustomKeyEventHandler(clipboardKeys);
term.open(document.getElementById('terminal'));
fit.fit();
term.focus();
window.addEventListener('resize', () => fit.fit());

// xterm.js sends Ctrl+C and Ctrl+V to the program as ^C and ^V and cancels the browser's own handling of them, so
// neither copies nor pastes. Returning false skips xterm.js for that key and leaves the browser's default in place:
// Ctrl+V pastes into the terminal (wrapped for bracketed paste when the program asked for it), and Ctrl+C copies when
// text is selected and still interrupts when nothing is. Cmd+C and Cmd+V on macOS already work this way.
function clipboardKeys(e) {
  if (e.type !== 'keydown' || !e.ctrlKey || e.altKey || e.metaKey) return true;
  const key = e.key.toLowerCase();
  if (key === 'v') return false;
  if (key === 'c' && !e.shiftKey && term.hasSelection()) {
    // The browser's copy runs after this handler and takes the selection; clear it afterwards, as a terminal does.
    setTimeout(() => term.clearSelection());
    return false;
  }
  return true;
}

// The access token comes from this page's URL fragment, which the browser never sends to a server.
const token = new URLSearchParams(location.hash.slice(1)).get('token');
if (token) connect(token);
else term.write('\x1b[31m[no access token: open the address the server printed, including its #token=...]\x1b[0m\r\n');

function connect(token) {
  const ws = new WebSocket(`ws://${location.host}/ws`, ['minipty', `minipty-token.${token}`]);
  ws.binaryType = 'arraybuffer';
  const encoder = new TextEncoder();
  let unacked = 0;
  const sendResize = () => {
    if (ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify({ type: 'resize', cols: term.cols, rows: term.rows }));
  };

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
}
</script>
</body>
</html>
""";
}
