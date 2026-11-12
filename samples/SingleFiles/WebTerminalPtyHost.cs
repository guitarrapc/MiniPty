#:sdk Microsoft.NET.Sdk.Web
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property PublishAot=true
#:project ../../src/MiniPty/MiniPty.csproj
#:project ../../src/MiniPty.Terminal/MiniPty.Terminal.csproj

// WebTerminalPtyHost: a browser terminal whose shells live in a PTY host process of their own, so the web server can
// restart (a deploy, a crash) without ending them, and can run under another account than the shells.
//
//   browser ──WebSocket──▶ web server ("web")           ──Unix domain socket──▶ PTY host ("pty-host")
//                           signs the user in,                                  owns the shells
//                           relays frames, keeps no state                       (PtyWebSocketSessionManager)
//
// Run the two roles in two terminals:
//   dotnet run samples/SingleFiles/WebTerminalPtyHost.cs -- pty-host
//   dotnet run samples/SingleFiles/WebTerminalPtyHost.cs -- web        # then open the address it prints
// Start something that keeps printing (`top`, or `ping -t localhost` on Windows), stop the web server with Ctrl+C, and
// start it again: the page reconnects, and the output continues with nothing lost or shown twice. Ctrl+C on the PTY
// host ends every shell.
//
// Smoke (CI): --smoke starts a PTY host process, opens a session through a web server, stops the web server, starts
// another, and checks that the shell kept its state. Exits 0 on success.

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using MiniPty;
using MiniPty.Terminal;

try
{
    if (args.Contains("--smoke"))
        return await Smoke.RunAsync();

    var role = args.Length > 0 ? args[0] : null;
    var groupAccess = args.Contains("--group-access");
    var stateDirectory = StateDirectory.Resolve(Arguments.Get(args, "--state-dir"), groupAccess);
    switch (role)
    {
        case "pty-host":
            return await PtyHost.RunAsync(stateDirectory, groupAccess, exitOnStdinEnd: args.Contains("--exit-on-stdin-end"));
        case "web":
            var port = int.TryParse(Arguments.Get(args, "--port"), out var value) ? value : 5173;
            // The token is the web server's own secret, so it stays in this account's directory, never in a shared one.
            var token = WebAccess.LoadOrCreateToken(StateDirectory.Resolve(null, groupAccess: false));
            await using (var web = await WebServer.StartAsync(stateDirectory, port, token))
            {
                // The address carries the token, so it is printed only to this console, for the user who started the server.
                Console.Error.WriteLine($"Web server listening on http://localhost:{web.Port}/#token={web.Token} (Ctrl+C to stop; the shells keep running)");
                await web.WaitForShutdownAsync();
            }
            return 0;
        default:
            Console.Error.WriteLine("usage: WebTerminalPtyHost (pty-host | web [--port N]) [--state-dir DIR [--group-access]] | --smoke");
            return 2;
    }
}
catch (Exception exception)
{
    Console.Error.WriteLine($"{exception.GetType().Name}: {exception.Message}");
    return 1;
}

/// <summary>
/// The PTY host: the only process that starts shells. It listens on a Unix domain socket, never on a network port, so who
/// may start a shell is decided by who may open the socket file; the web server is the one client it expects.
/// </summary>
/// <remarks>
/// Its endpoints are VsCodePersistentBridge.cs's, without the service token: the socket's directory is the access check.
/// <list type="bullet">
/// <item><description><c>POST /sessions</c> starts a shell and returns its id and token. The token is returned only here.</description></item>
/// <item><description>
/// <c>GET /sessions/{id}/connect?offset=N</c> is a WebSocket that offers the token as the subprotocol
/// <c>minipty-token.&lt;hex&gt;</c>. It replays the output after the client's last acknowledged byte, then streams live
/// output. A client that goes away detaches the session, which keeps running for <c>DetachedSessionTimeout</c>.
/// </description></item>
/// </list>
/// </remarks>
static class PtyHost
{
    public const string SocketFileName = "pty.sock";

    /// <summary>Asks the client to connect again later: the session still has the connection of a client that is going away.</summary>
    public const WebSocketCloseStatus TryAgainLater = (WebSocketCloseStatus)1013;

    public static async Task<int> RunAsync(string stateDirectory, bool groupAccess, bool exitOnStdinEnd)
    {
        if (!Pty.IsSupported)
        {
            Console.Error.WriteLine("PTY is not supported on this operating system.");
            return 1;
        }

        var socketPath = Path.Combine(stateDirectory, SocketFileName);
        if (await UnixSocket.AcceptsConnectionsAsync(socketPath, CancellationToken.None))
        {
            Console.Error.WriteLine($"A PTY host is already listening on {socketPath}.");
            return 1;
        }

        // The file a host left behind when it did not exit cleanly; binding fails while it exists.
        File.Delete(socketPath);

        // One manager owns every shell, independently of the connections that come and go; disposing it ends them all.
        // DetachedSessionTimeout is how long a shell waits for its page to come back: long enough for a web server restart
        // and a dropped network, and it also ends the shells of tabs that were closed.
        await using var manager = new PtyWebSocketSessionManager(new PtyWebSocketSessionManagerOptions
        {
            DetachedSessionTimeout = TimeSpan.FromMinutes(5),
        });

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenUnixSocket(socketPath));

        await using var app = builder.Build();
        var stopping = app.Lifetime.ApplicationStopping;
        app.UseWebSockets();
        app.MapPost("/sessions", context => CreateSessionAsync(context, manager));
        app.Map("/sessions/{id}/connect", context => ConnectAsync(context, manager, stopping));
        await app.StartAsync();

        // The socket gets the directory's access: its owner only, or with --group-access also the directory's group, which
        // is where the web server's account is in a deployment with two accounts. Connecting needs write access.
        if (!OperatingSystem.IsWindows())
        {
            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            if (groupAccess)
                mode |= UnixFileMode.GroupRead | UnixFileMode.GroupWrite;
            File.SetUnixFileMode(socketPath, mode);
        }

        if (exitOnStdinEnd)
            _ = StopAtEndOfInputAsync(app.Lifetime);

        Console.Error.WriteLine($"PTY host listening on {socketPath} (Ctrl+C ends every shell)");
        await app.WaitForShutdownAsync();
        File.Delete(socketPath);
        return 0;
    }

    /// <summary>The smoke test's PTY host ends with the smoke process, even one that crashes: its stdin closes then.</summary>
    private static async Task StopAtEndOfInputAsync(IHostApplicationLifetime lifetime)
    {
        using var input = Console.OpenStandardInput();
        await input.CopyToAsync(Stream.Null);
        lifetime.StopApplication();
    }

    private static async Task CreateSessionAsync(HttpContext context, PtyWebSocketSessionManager manager)
    {
        PtyBridgeSessionCredentials credentials;
        try
        {
            // Starts the shell now; it runs whether or not a client is connected.
            credentials = manager.CreateSession(Shell.CreateStartInfo());
        }
        catch (InvalidOperationException)
        {
            // MaxSessions (64 by default) shells are running.
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        context.Response.StatusCode = StatusCodes.Status201Created;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(
            $$"""{"sessionId":"{{credentials.SessionId:D}}","authenticationToken":"{{credentials.AuthenticationToken}}"}""",
            context.RequestAborted);
    }

    private static async Task ConnectAsync(HttpContext context, PtyWebSocketSessionManager manager, CancellationToken stopping)
    {
        var sessionToken = WebAccess.FindProtocolValue(context.WebSockets.WebSocketRequestedProtocols, WebAccess.SessionTokenPrefix);
        if (!context.WebSockets.IsWebSocketRequest
            || sessionToken is null
            || !Guid.TryParse(context.Request.RouteValues["id"] as string, out var sessionId)
            || !long.TryParse(context.Request.Query["offset"], out var offset)
            || offset < 0)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var webSocket = await context.WebSockets.AcceptWebSocketAsync(WebAccess.Protocol);
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping);
        try
        {
            // Returns null when the web server goes away (the shell keeps running), or the exit status once the shell has
            // exited and its last output and the exit message are sent.
            var status = await manager.ConnectAsync(sessionId, sessionToken, offset, webSocket, connection.Token);
            if (status is not null)
                await WebSocketClose.AwaitPeerAsync(webSocket);
        }
        catch (UnauthorizedAccessException)
        {
            // An unknown or expired session, or a wrong token: the page starts over.
            await WebSocketClose.SendAsync(webSocket, WebSocketCloseStatus.PolicyViolation, "invalid session credentials");
        }
        catch (ArgumentOutOfRangeException)
        {
            // The page asked for output the replay buffer no longer holds: the page starts over.
            await WebSocketClose.SendAsync(webSocket, WebSocketCloseStatus.InvalidPayloadData, "replay offset unavailable");
        }
        catch (InvalidOperationException)
        {
            // The connection of the web server that just went away is still attached, for a moment.
            await WebSocketClose.SendAsync(webSocket, TryAgainLater, "session already connected");
        }
        catch (OperationCanceledException)
        {
            // The web server's connection or this host ended; the session stays for a reconnect until the manager ends.
        }
    }
}

/// <summary>
/// The web server: everything that faces the browser. It signs the user in, starts sessions through the PTY host, and
/// relays each page's WebSocket to the PTY host frame by frame. It keeps no session state, so it can restart at any time:
/// the page holds its session's id, token, and output offset, and connects again.
/// </summary>
/// <remarks>
/// It needs no MiniPty package: in a real deployment it is any web server that can relay a WebSocket.
/// </remarks>
sealed class WebServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly SocketsHttpHandler _ptyHostHandler;
    private readonly HttpClient _ptyHost;
    private readonly HttpMessageInvoker _ptyHostWebSockets;
    private readonly CancellationToken _stopping;

    private WebServer(WebApplication app, string socketPath, string token)
    {
        _app = app;
        Token = token;
        _stopping = app.Lifetime.ApplicationStopping;
        _ptyHostHandler = UnixSocket.CreateHandler(socketPath);
        _ptyHost = new HttpClient(_ptyHostHandler, disposeHandler: false) { BaseAddress = new Uri("http://localhost/") };
        _ptyHostWebSockets = new HttpMessageInvoker(_ptyHostHandler, disposeHandler: false);
    }

    public int Port { get; private set; }

    public string Token { get; }

    /// <summary>Starts the web server on <paramref name="port"/> of localhost, or on an ephemeral port of 127.0.0.1 for 0.</summary>
    public static async Task<WebServer> StartAsync(string stateDirectory, int port, string token)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            if (port == 0)
                kestrel.Listen(IPAddress.Loopback, 0);
            else
                kestrel.ListenLocalhost(port);
        });

        var app = builder.Build();
        var server = new WebServer(app, Path.Combine(stateDirectory, PtyHost.SocketFileName), token);
        app.UseWebSockets();
        app.MapGet("/", static context =>
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            return context.Response.WriteAsync(Page.Html);
        });
        app.MapPost("/api/sessions", server.CreateSessionAsync);
        app.Map("/api/sessions/{id}/connect", server.ConnectAsync);
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        server.Port = new Uri(address).Port;
        return server;
    }

    public Task WaitForShutdownAsync() => _app.WaitForShutdownAsync();

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _ptyHostWebSockets.Dispose();
        _ptyHost.Dispose();
        _ptyHostHandler.Dispose();
    }

    /// <summary><c>POST /api/sessions</c>: starts a shell in the PTY host and gives the page its id and token.</summary>
    private async Task CreateSessionAsync(HttpContext context)
    {
        if (Refuse(context, WebAccess.ReadBearer(context.Request.Headers.Authorization)))
            return;

        HttpResponseMessage response;
        try
        {
            response = await _ptyHost.PostAsync("sessions", content: null, context.RequestAborted);
        }
        catch (HttpRequestException)
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }

        using (response)
        {
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = "application/json";
            context.Response.Headers.CacheControl = "no-store";   // the body holds the session token
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
    }

    /// <summary>
    /// <c>GET /api/sessions/{id}/connect?offset=N</c>: connects to the PTY host first, so a page whose PTY host is down
    /// gets a failed handshake it retries, then relays the page's WebSocket to it until either side closes.
    /// </summary>
    private async Task ConnectAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var protocols = context.WebSockets.WebSocketRequestedProtocols;
        if (Refuse(context, WebAccess.FindProtocolValue(protocols, WebAccess.AccessTokenPrefix)))
            return;

        var sessionToken = WebAccess.FindProtocolValue(protocols, WebAccess.SessionTokenPrefix);
        if (!WebAccess.IsToken(sessionToken)
            || !Guid.TryParse(context.Request.RouteValues["id"] as string, out var sessionId)
            || !long.TryParse(context.Request.Query["offset"], out var offset)
            || offset < 0)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // Ends the relay when the page goes away or this server stops; either way the PTY host only detaches the session.
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _stopping);
        using var ptyHost = new ClientWebSocket();
        ptyHost.Options.AddSubProtocol(WebAccess.Protocol);
        ptyHost.Options.AddSubProtocol(WebAccess.SessionTokenPrefix + sessionToken);
        try
        {
            var uri = new Uri($"ws://localhost/sessions/{sessionId:D}/connect?offset={offset}");
            await ptyHost.ConnectAsync(uri, _ptyHostWebSockets, connection.Token);
        }
        catch (Exception exception) when (exception is WebSocketException or HttpRequestException)
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }

        using var browser = await context.WebSockets.AcceptWebSocketAsync(WebAccess.Protocol);
        await Relay.RunAsync(browser, ptyHost, connection.Token);
    }

    private bool Refuse(HttpContext context, string? offeredToken)
    {
        var refusal = WebAccess.Check(context.Request.Headers.Host, context.Request.Headers.Origin, offeredToken, Port, Token);
        if (refusal is null)
            return false;

        Console.Error.WriteLine($"refused a request: {refusal}");
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return true;
    }
}

/// <summary>
/// Relays one WebSocket to another, message fragment by message fragment, in order and unchanged. Nothing is buffered
/// and nothing is answered here: the page's acknowledgements reach the PTY host, so its replay starts exactly after what
/// the page has, and a page that reads slowly holds the shell back, as it would connected directly.
/// </summary>
static class Relay
{
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

    public static async Task RunAsync(WebSocket browser, WebSocket ptyHost, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var toPtyHost = PumpAsync(browser, ptyHost, stop.Token);
        var toBrowser = PumpAsync(ptyHost, browser, stop.Token);

        // A side that closed passes its close on, and the other side's reply follows; a side that failed ends both at once.
        var first = await Task.WhenAny(toPtyHost, toBrowser);
        if (first.IsCompletedSuccessfully)
            stop.CancelAfter(CloseTimeout);
        else
            stop.Cancel();

        try
        {
            await Task.WhenAll(toPtyHost, toBrowser);
        }
        catch (Exception)
        {
            // A dropped connection on either side, or this server stopping. Aborting both makes the PTY host detach the
            // session at once, and the page sees a dropped connection, which it retries.
            browser.Abort();
            ptyHost.Abort();
        }
    }

    private static async Task PumpAsync(WebSocket source, WebSocket destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var received = await source.ReceiveAsync(buffer.AsMemory(), cancellationToken);
            if (received.MessageType == WebSocketMessageType.Close)
            {
                // The close code tells the page whether to reconnect (see the page's onclose), so it is passed on as is.
                var status = source.CloseStatus is { } code && code != WebSocketCloseStatus.Empty ? code : WebSocketCloseStatus.NormalClosure;
                if (destination.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await destination.CloseOutputAsync(status, source.CloseStatusDescription, cancellationToken);
                return;
            }

            await destination.SendAsync(buffer.AsMemory(0, received.Count), received.MessageType, received.EndOfMessage, cancellationToken);
        }
    }
}

/// <summary>
/// The directory that holds the PTY host's socket. Whoever can open the socket can start shells as the PTY host's user, so
/// by default only the directory's owner may enter it. The user's own directory, the default, also holds the web server's
/// access token.
/// </summary>
/// <remarks>
/// The default is per user: <c>$XDG_RUNTIME_DIR</c> on Linux when set, otherwise the temporary directory. To run the two
/// roles under two accounts, an administrator creates a directory that the PTY host's account owns, in a group that the
/// web server's account is in, with mode 2750: the group may enter but not replace the socket, and the setgid bit gives
/// the socket the directory's group on Linux. Both roles then take <c>--state-dir</c> with <c>--group-access</c>, and the
/// PTY host makes its socket 0660. The access token stays in the web server's own directory.
/// </remarks>
static class StateDirectory
{
    private const UnixFileMode OthersAccess =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    /// <summary>What <c>--group-access</c> allows beyond the owner: the group may list and enter the directory.</summary>
    private const UnixFileMode GroupEnter = UnixFileMode.GroupRead | UnixFileMode.GroupExecute;

    public static string Resolve(string? path, bool groupAccess)
    {
        if (groupAccess)
            return ResolveShared(path);

        path ??= Path.Combine(DefaultRoot(), "minipty-webterminal-" + Environment.UserName);
        if (OperatingSystem.IsWindows())
        {
            // Under the user's own %TEMP% by default, which only the user and administrators can open.
            Directory.CreateDirectory(path);
            return path;
        }

        if (!Directory.Exists(path))
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        // /tmp is shared: a directory another user created first, open to others, would hand them the socket.
        if ((File.GetUnixFileMode(path) & OthersAccess) != 0)
            throw new InvalidOperationException($"{path} must be open to its owner only (chmod 700). Pass another --state-dir, or add --group-access to share it with the web server's group (chmod 2750).");
        return path;
    }

    /// <summary>
    /// A directory shared with the web server's group. It must be named and created beforehand: the per-user default stays
    /// the owner's alone, and an account that is not the owner could not give the directory its group.
    /// </summary>
    private static string ResolveShared(string? path)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("--group-access sets Unix file modes. On Windows, grant the web server's account access to the --state-dir directory instead.");
        if (path is null)
            throw new ArgumentException("--group-access needs --state-dir: a directory the PTY host's account owns, shared with the web server's group (chmod 2750).");
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"{path} does not exist. Create it owned by the PTY host's account and the web server's group, with chmod 2750.");

        // The group may enter, to open the socket, but not write: a member could otherwise replace the socket with its own.
        if ((File.GetUnixFileMode(path) & OthersAccess & ~GroupEnter) != 0)
            throw new InvalidOperationException($"{path} must be closed to others, and its group must not write to it (chmod 2750).");
        return path;
    }

    private static string DefaultRoot() =>
        !OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtime
            ? runtime
            : Path.GetTempPath();
}

static class UnixSocket
{
    /// <summary>An HTTP handler whose every connection goes to the PTY host's socket, whatever the URL's host.</summary>
    public static SocketsHttpHandler CreateHandler(string socketPath) => new()
    {
        ConnectCallback = async (_, cancellationToken) =>
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    public static async Task<bool> AcceptsConnectionsAsync(string socketPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(socketPath))
            return false;

        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}

/// <summary>
/// Decides whether a request may use the web server. As in WebTerminal.cs, listening on localhost is not enough: any page
/// the user visits can send requests to localhost, so a request must come from this server's own page, for this
/// server's host name, with its access token.
/// </summary>
/// <remarks>
/// The token is kept in the state directory, so that a restarted web server accepts the page it served before. A real web
/// server signs users in with cookies or tokens that outlive its process in the same way.
/// </remarks>
static class WebAccess
{
    /// <summary>The subprotocol both servers answer with; a browser fails a handshake that answers none it offered.</summary>
    public const string Protocol = "minipty";

    /// <summary>The page offers the web server's access token as this subprotocol, and the session token as the next.</summary>
    public const string AccessTokenPrefix = "minipty-access.";

    public const string SessionTokenPrefix = "minipty-token.";

    private const string TokenFileName = "web-access-token";

    public static string CreateToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    public static bool IsToken(string? value) =>
        value is { Length: 64 } && value.AsSpan().IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;

    public static string LoadOrCreateToken(string stateDirectory)
    {
        var path = Path.Combine(stateDirectory, TokenFileName);
        if (File.Exists(path) && File.ReadAllText(path).Trim() is var saved && IsToken(saved))
            return saved;

        var token = CreateToken();
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(path, options))
            stream.Write(Encoding.ASCII.GetBytes(token));
        return token;
    }

    /// <summary>Returns why the request is refused, or null when it may proceed. The reason never holds the token.</summary>
    public static string? Check(string? host, string? origin, string? offeredToken, int port, string token)
    {
        if (!IsLoopbackHost(host, port))
            return "Host is not this server";

        // Browsers send Origin on every WebSocket request and on a fetch POST; a client that is not a browser sends none
        // and is judged by the token alone.
        if (!string.IsNullOrEmpty(origin) && !string.Equals(origin, "http://" + host, StringComparison.OrdinalIgnoreCase))
            return "Origin is another site";

        // Compared in fixed time, so the response time does not reveal how much of a guess matched.
        return offeredToken is not null
            && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(offeredToken), Encoding.ASCII.GetBytes(token))
            ? null
            : "missing or wrong access token";
    }

    public static string? FindProtocolValue(IList<string> protocols, string prefix)
    {
        foreach (var protocol in protocols)
        {
            if (protocol.StartsWith(prefix, StringComparison.Ordinal))
                return protocol[prefix.Length..];
        }

        return null;
    }

    public static string? ReadBearer(string? authorization)
    {
        const string Prefix = "Bearer ";
        return authorization is not null && authorization.StartsWith(Prefix, StringComparison.Ordinal) ? authorization[Prefix.Length..] : null;
    }

    private static bool IsLoopbackHost(string? host, int port)
    {
        var suffix = ":" + port;
        if (host is null || !host.EndsWith(suffix, StringComparison.Ordinal))
            return false;

        var name = host[..^suffix.Length];
        return name.Equals("localhost", StringComparison.OrdinalIgnoreCase) || name == "127.0.0.1" || name == "[::1]";
    }
}

static class Shell
{
    /// <summary>
    /// The shell for a new session. It runs as the PTY host's user, in the PTY host's environment, not the web server's.
    /// The size is a placeholder: the page sends its own when it connects.
    /// </summary>
    /// <remarks>
    /// Windows: like WebTerminal.cs, this sample stays on the in-box ConPTY so that it publishes as one file; a production
    /// terminal should supply the out-of-band ConPTY (see the root README).
    /// </remarks>
    public static PtyStartInfo CreateStartInfo()
    {
        // xterm.js draws 24-bit color, and programs look at COLORTERM to decide whether to use it.
        var environment = new Dictionary<string, string?> { ["COLORTERM"] = "truecolor" };
        if (OperatingSystem.IsWindows())
        {
            return new PtyStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe",
                Environment = environment,
            };
        }

        return new PtyStartInfo
        {
            FileName = File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh",
            Arguments = ["-i"],
            Environment = environment,
            // TERM describes the frontend, xterm.js, not the terminal the PTY host was started from.
            TerminalName = "xterm-256color",
        };
    }
}

static class WebSocketClose
{
    public static async Task SendAsync(WebSocket webSocket, WebSocketCloseStatus status, string description)
    {
        if (webSocket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
            return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await webSocket.CloseAsync(status, description, timeout.Token);
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException)
        {
        }
    }

    /// <summary>Waits for the reply to a close this side sent, so the connection ends with a complete close handshake.</summary>
    public static async Task AwaitPeerAsync(WebSocket webSocket)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var buffer = new byte[256];
            while (webSocket.State == WebSocketState.CloseSent)
            {
                var result = await webSocket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException)
        {
        }
    }
}

static class Arguments
{
    public static string? Get(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}

/// <summary>
/// Runs the restart the sample is about, with a client that speaks the page's protocol: a PTY host process, a session
/// through one web server, that web server stopped mid-session, and the same session through a second one.
/// </summary>
/// <remarks>
/// The smoke reports each step, and both servers log their requests and failures, so a run that stops shows in its log
/// which step and which request did not finish.
/// </remarks>
static class Smoke
{
    private const string ReadyMarker = "MINIPTY_PTYHOST_READY_5D1E";
    private const string StateMarker = "MINIPTY_PTYHOST_STATE_C83A";

    public static async Task<int> RunAsync()
    {
        if (!Pty.IsSupported)
        {
            Console.Error.WriteLine("PTY is not supported on this operating system.");
            return 1;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var stateDirectory = StateDirectory.Resolve(Path.Combine(Path.GetTempPath(), "minipty-wt-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))), groupAccess: false);
        using var ptyHost = StartPtyHost(stateDirectory);
        try
        {
            await WaitForPtyHostAsync(ptyHost, Path.Combine(stateDirectory, PtyHost.SocketFileName), timeout.Token);
            Step("the PTY host accepts connections");
            var token = WebAccess.CreateToken();
            Guid sessionId;
            string sessionToken;
            long offset = 0;

            await using (var web = await WebServer.StartAsync(stateDirectory, port: 0, token))
            {
                using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{web.Port}/") };
                using (var refused = await http.PostAsync("api/sessions", content: null, timeout.Token))
                {
                    if (refused.StatusCode != HttpStatusCode.Forbidden)
                        throw new InvalidDataException("The web server started a session without the access token.");
                }

                using var request = new HttpRequestMessage(HttpMethod.Post, "api/sessions");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var created = await http.SendAsync(request, timeout.Token);
                created.EnsureSuccessStatusCode();
                using var credentials = JsonDocument.Parse(await created.Content.ReadAsStreamAsync(timeout.Token));
                sessionId = credentials.RootElement.GetProperty("sessionId").GetGuid();
                sessionToken = credentials.RootElement.GetProperty("authenticationToken").GetString()!;
                Step("a session started through the web server");

                using var first = await ConnectAsync(web.Port, token, sessionId, sessionToken, offset, timeout.Token);
                Step("connected to the session");
                // Only the command's output holds the marker whole: the echo of the typed line has it in two parts. The
                // echo is no evidence: an echoed line that reaches the terminal's width can be split anywhere, and a
                // marker split there was never found (bash 3.2 on macOS, whose 10-character prompt put the wrap in it).
                var (head, tail) = (ReadyMarker[..15], ReadyMarker[16..]);
                var command = OperatingSystem.IsWindows()
                    ? $"set MINIPTY_PTYHOST_STATE={StateMarker}&&echo {head}^_{tail}\r\n"
                    : $"export MINIPTY_PTYHOST_STATE={StateMarker}; printf '%s_%s\\n' {head} {tail}\n";
                await first.SendAsync(Encoding.UTF8.GetBytes(command), WebSocketMessageType.Binary, true, timeout.Token);
                offset = await ReadUntilAsync(first, offset, ReadyMarker, timeout.Token);
                Step($"the shell answered (offset {offset})");

                // The web server goes away mid-session, as a deploy or a crash takes it; the page sees a dropped connection.
                await web.DisposeAsync();
                await WaitForDropAsync(first, timeout.Token);
                Step("the web server stopped and the connection dropped");
            }

            await using (var web = await WebServer.StartAsync(stateDirectory, port: 0, token))
            {
                var query = OperatingSystem.IsWindows() ? "echo %MINIPTY_PTYHOST_STATE%\r\n" : "printf '%s\\n' \"$MINIPTY_PTYHOST_STATE\"\n";
                ClientWebSocket second;
                while (true)
                {
                    second = await ConnectAsync(web.Port, token, sessionId, sessionToken, offset, timeout.Token);
                    await second.SendAsync(Encoding.UTF8.GetBytes(query), WebSocketMessageType.Binary, true, timeout.Token);
                    try
                    {
                        // Only the output holds the marker: the echo shows the variable's name.
                        offset = await ReadUntilAsync(second, offset, StateMarker, timeout.Token);
                        break;
                    }
                    catch (TryAgainLaterException)
                    {
                        // The PTY host had not yet seen the first web server's connection end.
                        Step("the PTY host still holds the old connection; retrying");
                        second.Dispose();
                        await Task.Delay(100, timeout.Token);
                    }
                }

                Step("a second web server reached the same shell, with its state");

                using (second)
                {
                    var exit = OperatingSystem.IsWindows() ? "exit\r\n" : "exit\n";
                    await second.SendAsync(Encoding.UTF8.GetBytes(exit), WebSocketMessageType.Binary, true, timeout.Token);
                    await WaitForExitAsync(second, offset, timeout.Token);
                }
            }

            Console.WriteLine("WebTerminalPtyHost smoke OK: the shell kept its state across a web server restart.");
            return 0;
        }
        finally
        {
            // Closing its stdin ends the PTY host the way Ctrl+C does: every shell ends and the socket file is removed.
            ptyHost.StandardInput.Close();
            if (!ptyHost.WaitForExit(10_000))
                ptyHost.Kill();
            try
            {
                Directory.Delete(stateDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Reports each step, so that a run that stops can be read from its log.</summary>
    private static void Step(string text) => Console.Error.WriteLine($"smoke: {text}");

    private static Process StartPtyHost(string stateDirectory)
    {
        // This program again: the published executable, or `dotnet <app>.dll` under `dotnet run`.
        var path = Environment.ProcessPath ?? throw new InvalidOperationException("The process path is unknown.");
        var startInfo = new ProcessStartInfo(path) { UseShellExecute = false, RedirectStandardInput = true };
        if (Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, AppDomain.CurrentDomain.FriendlyName + ".dll"));
        foreach (var argument in (string[])["pty-host", "--state-dir", stateDirectory, "--exit-on-stdin-end"])
            startInfo.ArgumentList.Add(argument);
        return Process.Start(startInfo) ?? throw new InvalidOperationException("The PTY host did not start.");
    }

    private static async Task WaitForPtyHostAsync(Process ptyHost, string socketPath, CancellationToken cancellationToken)
    {
        while (!await UnixSocket.AcceptsConnectionsAsync(socketPath, cancellationToken))
        {
            if (ptyHost.HasExited)
                throw new InvalidOperationException($"The PTY host exited with code {ptyHost.ExitCode}.");
            await Task.Delay(100, cancellationToken);
        }
    }

    private static async Task<ClientWebSocket> ConnectAsync(int port, string token, Guid sessionId, string sessionToken, long offset, CancellationToken cancellationToken)
    {
        var client = new ClientWebSocket();
        client.Options.AddSubProtocol(WebAccess.Protocol);
        client.Options.AddSubProtocol(WebAccess.AccessTokenPrefix + token);
        client.Options.AddSubProtocol(WebAccess.SessionTokenPrefix + sessionToken);
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/api/sessions/{sessionId:D}/connect?offset={offset}"), cancellationToken);
        return client;
    }

    /// <summary>Reads output until <paramref name="marker"/> appears, acknowledging each message as the page does.</summary>
    private static async Task<long> ReadUntilAsync(ClientWebSocket client, long offset, string marker, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        while (output.ToString().IndexOf(marker, StringComparison.Ordinal) < 0)
        {
            (WebSocketMessageType Type, byte[] Payload) message;
            try
            {
                message = await ReceiveMessageAsync(client, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Says what was missing and what came instead, so a run that times out can be read from its log.
                var text = output.ToString();
                throw new TimeoutException($"'{marker}' did not arrive; the output was: {(text.Length > 600 ? text[^600..] : text)}");
            }

            var (type, payload) = message;
            if (type == WebSocketMessageType.Close)
            {
                if (client.CloseStatus == PtyHost.TryAgainLater)
                    throw new TryAgainLaterException();
                throw new InvalidDataException($"The session closed before the marker arrived: {client.CloseStatus} {client.CloseStatusDescription}");
            }

            offset = await AcceptAsync(client, type, payload, offset, output, cancellationToken);
        }

        return offset;
    }

    private static async Task WaitForExitAsync(ClientWebSocket client, long offset, CancellationToken cancellationToken)
    {
        while (true)
        {
            var (type, payload) = await ReceiveMessageAsync(client, cancellationToken);
            if (type == WebSocketMessageType.Close)
                throw new InvalidDataException("The session closed without an exit message.");
            if (type == WebSocketMessageType.Text)
            {
                using var json = JsonDocument.Parse(payload);
                if (json.RootElement.GetProperty("type").GetString() == "exit")
                    return;
            }

            offset = await AcceptAsync(client, type, payload, offset, output: null, cancellationToken);
        }
    }

    // The header of the next binary message. Every binary message follows its own header, on a new connection too.
    private static long s_pendingOffset = -1;
    private static int s_pendingBytes;

    /// <summary>The page's rule: an output header, then exactly that many bytes at that offset, then an acknowledgement.</summary>
    private static async Task<long> AcceptAsync(ClientWebSocket client, WebSocketMessageType type, byte[] payload, long offset, StringBuilder? output, CancellationToken cancellationToken)
    {
        if (type == WebSocketMessageType.Text)
        {
            using var json = JsonDocument.Parse(payload);
            if (json.RootElement.GetProperty("type").GetString() == "output")
            {
                s_pendingOffset = json.RootElement.GetProperty("offset").GetInt64();
                s_pendingBytes = json.RootElement.GetProperty("bytes").GetInt32();
            }

            return offset;
        }

        if (s_pendingOffset != offset || s_pendingBytes != payload.Length)
            throw new InvalidDataException($"Output at {s_pendingOffset} ({s_pendingBytes} bytes) does not follow offset {offset} ({payload.Length} bytes).");

        s_pendingOffset = -1;
        output?.Append(Encoding.UTF8.GetString(payload));
        offset += payload.Length;
        await client.SendAsync(Encoding.UTF8.GetBytes($$"""{"type":"ack","offset":{{offset}}}"""), WebSocketMessageType.Text, true, cancellationToken);
        return offset;
    }

    private static async Task WaitForDropAsync(ClientWebSocket client, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var (type, _) = await ReceiveMessageAsync(client, cancellationToken);
                if (type == WebSocketMessageType.Close)
                    return;
            }
        }
        catch (WebSocketException)
        {
        }
    }

    private static async Task<(WebSocketMessageType Type, byte[] Payload)> ReceiveMessageAsync(ClientWebSocket client, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        ValueWebSocketReceiveResult result;
        do
        {
            result = await client.ReceiveAsync(buffer.AsMemory(), cancellationToken);
            message.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);
        return (result.MessageType, message.ToArray());
    }

    private sealed class TryAgainLaterException : Exception;
}

// The page is embedded so the NativeAOT publish stays one executable. It holds the session's id, token, and output
// offset in memory, so it survives a web server restart, not a reload: a reloaded page starts a new shell, because it
// could not show the old one's screen (MiniPty replays bytes it has not acknowledged, not a screen).
static class Page
{
    public const string Html = """
<!doctype html>
<html>
<head>
<meta charset="utf-8">
<title>MiniPty WebTerminal (PTY host)</title>
<link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/@xterm/xterm@5.5.0/css/xterm.min.css">
<style>html,body{height:100%;margin:0;background:#000}#terminal{height:100%}</style>
</head>
<body>
<div id="terminal"></div>
<script src="https://cdn.jsdelivr.net/npm/@xterm/xterm@5.5.0/lib/xterm.min.js"></script>
<script src="https://cdn.jsdelivr.net/npm/@xterm/addon-fit@0.10.0/lib/addon-fit.min.js"></script>
<script>
const term = new Terminal();
const fit = new FitAddon.FitAddon();
term.loadAddon(fit);
term.open(document.getElementById('terminal'));
fit.fit();
term.focus();
window.addEventListener('resize', () => fit.fit());

const encoder = new TextEncoder();
const note = (text, color = 33) => term.write(`\r\n\x1b[${color}m[${text}]\x1b[0m\r\n`);
// The access token comes from this page's URL fragment, which the browser never sends to a server.
const token = new URLSearchParams(location.hash.slice(1)).get('token');
let session;       // { sessionId, authenticationToken } from POST /api/sessions
let offset = 0;    // the absolute offset of the next output byte: where a reconnect resumes
let header;        // the output header the next binary message belongs to
let socket;
let ended = false;
let reconnecting = false;

term.onData(data => { if (socket?.readyState === WebSocket.OPEN) socket.send(encoder.encode(data)); });
term.onResize(() => sendResize());

if (token) start();
else note('no access token: open the address the web server printed, including its #token=...', 31);

async function start() {
  const response = await fetch('/api/sessions', { method: 'POST', headers: { Authorization: `Bearer ${token}` } });
  if (!response.ok) { note(`could not start a shell: HTTP ${response.status}`, 31); return; }
  session = await response.json();
  connect();
}

function connect() {
  const url = `ws://${location.host}/api/sessions/${session.sessionId}/connect?offset=${offset}`;
  const ws = new WebSocket(url, ['minipty', `minipty-access.${token}`, `minipty-token.${session.authenticationToken}`]);
  ws.binaryType = 'arraybuffer';
  socket = ws;
  ws.onopen = () => {
    if (reconnecting) note('reconnected', 32);
    reconnecting = false;
    sendResize();
  };
  ws.onmessage = e => {
    if (typeof e.data === 'string') {
      const message = JSON.parse(e.data);
      if (message.type === 'output') header = message;
      else if (message.type === 'exit') { ended = true; note(`process exited with code ${message.exitCode}`); }
      return;
    }
    const bytes = new Uint8Array(e.data);
    if (!header || header.offset !== offset || header.bytes !== bytes.length) { ended = true; ws.close(); note('output out of order', 31); return; }
    header = undefined;
    offset += bytes.length;
    const ack = offset;
    // Acknowledged once drawn, so the PTY host frees the bytes and a slow page holds the shell back.
    term.write(bytes, () => { if (socket === ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify({ type: 'ack', offset: ack })); });
  };
  ws.onclose = e => {
    if (socket !== ws) return;
    socket = undefined;
    header = undefined;
    if (ended) return;
    // 1008: the session is unknown or expired; 1007: its replay no longer reaches back to this page's offset.
    if (e.code === 1008 || e.code === 1007) { ended = true; note(`session is gone (${e.reason || e.code}); reload for a new shell`, 31); return; }
    // Anything else, such as the web server restarting (1006, or a failed handshake) or 1013 from the PTY host while it
    // still holds the old connection, is retried with the same offset.
    if (!reconnecting) { reconnecting = true; note('connection lost; reconnecting'); }
    setTimeout(connect, 1000);
  };
}

function sendResize() {
  if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ type: 'resize', cols: term.cols, rows: term.rows }));
}
</script>
</body>
</html>
""";
}
