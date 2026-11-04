#:sdk Microsoft.NET.Sdk.Web
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property PublishAot=false
#:project ../../src/MiniPty/MiniPty.csproj
#:project ../../src/MiniPty.Terminal/MiniPty.Terminal.csproj
#:package OpenTelemetry.Extensions.Hosting@1.20.1
#:package OpenTelemetry.Exporter.OpenTelemetryProtocol@1.20.1

// AspireTerminal's web app: a shell in the browser through PtyWebSocketBridge, with MiniPty's meter exported over OTLP.
//
// Under the Aspire AppHost (apphost.cs beside this file), Aspire sets OTEL_EXPORTER_OTLP_ENDPOINT to its dashboard and
// MINIPTY_TERMINAL_TOKEN to the access token in the dashboard's link to this app, and the dashboard charts the MiniPty
// meter beside ASP.NET Core's and the runtime's. Run alone (dotnet run samples/AspireTerminal/terminal.cs), it serves
// http://localhost:5172/, prints its address with a fresh token, and exports only when OTEL_EXPORTER_OTLP_ENDPOINT is
// set.

using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using MiniPty;
using MiniPty.Terminal;
using OpenTelemetry;
using OpenTelemetry.Metrics;

if (!Pty.IsSupported)
{
    Console.Error.WriteLine("PTY is not supported on this operating system.");
    return 1;
}

// The AppHost passes a 64-character token.
var givenToken = Environment.GetEnvironmentVariable("MINIPTY_TERMINAL_TOKEN");
var underAspire = givenToken is { Length: 64 };
var token = underAspire ? givenToken! : LocalAccess.CreateToken();

var builder = WebApplication.CreateSlimBuilder(args);
if (!underAspire)
    builder.WebHost.UseUrls("http://localhost:5172");

// The one line a host adds for MiniPty: AddMeter("MiniPty"). The other two meters are built into ASP.NET Core and the
// runtime, so the dashboard can set MiniPty's figures against requests, threads, and GC.
var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
if (!string.IsNullOrWhiteSpace(otlpEndpoint))
{
    builder.Services.AddOpenTelemetry()
        .WithMetrics(metrics => metrics.AddMeter("MiniPty", "Microsoft.AspNetCore.Hosting", "System.Runtime"))
        .UseOtlpExporter();
}

var app = builder.Build();
app.UseWebSockets();
app.MapGet("/", static context =>
{
    context.Response.ContentType = "text/html; charset=utf-8";
    return context.Response.WriteAsync(Page.Html);
});
app.Map("/ws", context => HandleTerminalAsync(context, token));

if (!underAspire)
    Console.Error.WriteLine($"AspireTerminal's web app listening on http://localhost:5172/#token={token} (Ctrl+C to stop)");

await app.RunAsync();
return 0;

// One shell per WebSocket. The request's abort token stops the bridge when the browser goes away.
static async Task HandleTerminalAsync(HttpContext context, string token)
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    // PtyWebSocketBridge authenticates nothing: whoever holds the socket types into the shell.
    var refusal = LocalAccess.Check(
        context.Request.Headers.Host,
        context.Request.Headers.Origin,
        context.Request.Headers.SecWebSocketProtocol,
        context.Connection.LocalPort,
        token);
    if (refusal is not null)
    {
        Console.Error.WriteLine($"refused a terminal connection: {refusal}");
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync(LocalAccess.Protocol);
    try
    {
        await PtyWebSocketBridge.RunAsync(Shell.StartInfo(), socket, cancellationToken: context.RequestAborted);
    }
    catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or TimeoutException)
    {
        // The browser went away, or the shell outlived the hang-up. The bridge has already ended the shell.
    }
    catch (Exception ex)
    {
        // Anything else, such as a terminal that failed to start, is reported here rather than as an unhandled request.
        // An IOException comes from the terminal, not from the socket.
        Console.Error.WriteLine($"session failed: {ex}");
    }
}

static class Shell
{
    // Under the AppHost the variables AppVariables removes configure this app. Left in, an OpenTelemetry app started in
    // the shell would export to the dashboard as this resource, and a web app started there would try to take this
    // app's address.
    private static readonly Dictionary<string, string?>? s_appVariables = AppVariables();

    public static PtyStartInfo StartInfo()
    {
        if (OperatingSystem.IsWindows())
        {
            return new PtyStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe",
                Environment = s_appVariables,
            };
        }

        // TERM describes the frontend, xterm.js, not the terminal this server was started from.
        var shell = Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } user ? user : "/bin/sh";
        return new PtyStartInfo { FileName = shell, Arguments = ["-i"], TerminalName = "xterm-256color", Environment = s_appVariables };
    }

    /// <summary>
    /// An overlay that removes every <c>OTEL_*</c> variable, <c>ASPNETCORE_URLS</c>, and <c>MINIPTY_TERMINAL_TOKEN</c>,
    /// or null when none is set. Names match ignoring case: .NET configuration reads <c>OTEL_*</c> and
    /// <c>ASPNETCORE_URLS</c> that way on every platform.
    /// </summary>
    private static Dictionary<string, string?>? AppVariables()
    {
        var removed = new Dictionary<string, string?>();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key
                && (key.StartsWith("OTEL_", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("ASPNETCORE_URLS", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("MINIPTY_TERMINAL_TOKEN", StringComparison.OrdinalIgnoreCase)))
            {
                removed[key] = null;
            }
        }

        return removed.Count == 0 ? null : removed;
    }
}

/// <summary>
/// Who may open a shell: a request to this server's own loopback address, from this server's own page or from a
/// client that is not a browser, that offers this run's token as the subprotocol <c>minipty-token.&lt;hex&gt;</c>. The
/// page reads the token from its URL fragment, which browsers never send to a server. WebTerminalScale.cs explains
/// each check.
/// </summary>
static class LocalAccess
{
    public const string Protocol = "minipty";

    private const string TokenPrefix = "minipty-token.";

    public static string CreateToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// Returns why the request is refused, or null when it may open a shell. The reason never holds the token.
    /// </summary>
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

/// <summary>
/// The terminal page: xterm.js speaking the <c>PtyWebSocketBridge</c> protocol (binary frames for data, JSON text
/// frames for resize, ack, and exit). It acknowledges output as xterm.js renders it, so a flood the browser cannot draw
/// fast enough fills the session's output buffer, and the dashboard shows the stalls.
/// </summary>
static class Page
{
    public const string Html = """
<!doctype html>
<html>
<head>
<meta charset="utf-8">
<title>MiniPty AspireTerminal</title>
<link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/@xterm/xterm@5.5.0/css/xterm.min.css">
<style>html,body{height:100%;margin:0;background:#000}#terminal{height:100%}</style>
</head>
<body>
<div id="terminal"></div>
<script src="https://cdn.jsdelivr.net/npm/@xterm/xterm@5.5.0/lib/xterm.min.js"></script>
<script src="https://cdn.jsdelivr.net/npm/@xterm/addon-fit@0.10.0/lib/addon-fit.min.js"></script>
<script>
const ACK_CHUNK = 131072;
const token = new URLSearchParams(location.hash.slice(1)).get('token');
const term = new Terminal();
const fit = new FitAddon.FitAddon();
term.loadAddon(fit);
term.open(document.getElementById('terminal'));
fit.fit();
term.focus();
window.addEventListener('resize', () => fit.fit());
if (!token) {
  term.write('\x1b[31m[no access token: open this page from the Aspire dashboard, or from the address the app printed]\x1b[0m\r\n');
} else {
  const ws = new WebSocket(`ws://${location.host}/ws`, ['minipty', `minipty-token.${token}`]);
  ws.binaryType = 'arraybuffer';
  const encoder = new TextEncoder();
  let unacked = 0;
  const sendResize = () => { if (ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify({ type: 'resize', cols: term.cols, rows: term.rows })); };
  ws.onopen = () => {
    sendResize();
    term.onData(data => { if (ws.readyState === WebSocket.OPEN) ws.send(encoder.encode(data)); });
    term.onResize(sendResize);
  };
  ws.onmessage = e => {
    if (typeof e.data === 'string') {
      const msg = JSON.parse(e.data);
      if (msg.type === 'exit') term.write(`\r\n\x1b[33m[process exited with code ${msg.exitCode}]\x1b[0m\r\n`);
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
