#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property PublishAot=true
#:project ../src/MiniPty/MiniPty.csproj
#:project ../src/MiniPty.Terminal/MiniPty.Terminal.csproj

// Long-lived loopback service for the VS Code persistent-terminal sample.
//
// Interactive:
//   MINIPTY_BRIDGE_ACCESS_TOKEN=<64 hex characters> dotnet samples/VsCodePersistentBridge.cs
// Smoke:
//   dotnet samples/VsCodePersistentBridge.cs --smoke

using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MiniPty;
using MiniPty.Terminal;

const string AccessTokenEnvironmentVariable = "MINIPTY_BRIDGE_ACCESS_TOKEN";

if (!Pty.IsSupported)
{
    Console.Error.WriteLine("PTY is not supported on this operating system.");
    return 1;
}

try
{
    if (args.Contains("--smoke"))
        return await RunSmokeAsync();

    var accessToken = Environment.GetEnvironmentVariable(AccessTokenEnvironmentVariable);
    if (!TokenAuthentication.IsValidToken(accessToken))
    {
        Console.Error.WriteLine($"{AccessTokenEnvironmentVariable} must contain exactly 64 hexadecimal characters.");
        return 2;
    }

    var port = ParsePort(args) ?? 5171;
    using var shutdown = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        shutdown.Cancel();
    };

    await using var manager = new PtyWebSocketSessionManager();
    using var listener = StartListener(port);
    Console.Error.WriteLine($"MiniPty persistent bridge listening on http://127.0.0.1:{port}/ (Ctrl+C to stop)");
    await ServeAsync(listener, manager, accessToken!, shutdown.Token);
    return 0;
}
catch (OperationCanceledException)
{
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}

static int? ParsePort(string[] arguments)
{
    for (var i = 0; i < arguments.Length - 1; i++)
    {
        if (arguments[i] == "--port" && int.TryParse(arguments[i + 1], out var port) && port is > 0 and <= 65535)
            return port;
    }
    return null;
}

static HttpListener StartListener(int port)
{
    var listener = new HttpListener();
    listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    listener.Start();
    return listener;
}

static HttpListener StartEphemeralListener(out int port)
{
    for (var attempt = 0; attempt < 20; attempt++)
    {
        port = Random.Shared.Next(20000, 60000);
        try
        {
            return StartListener(port);
        }
        catch (Exception exception) when (exception is HttpListenerException or SocketException)
        {
        }
    }
    throw new InvalidOperationException("Could not bind an ephemeral port for the persistent bridge smoke test.");
}

static async Task ServeAsync(
    HttpListener listener,
    PtyWebSocketSessionManager manager,
    string accessToken,
    CancellationToken cancellationToken)
{
    var requests = new HashSet<Task>();
    var requestsLock = new Lock();
    try
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is OperationCanceledException or HttpListenerException or ObjectDisposedException)
            {
                break;
            }

            var request = HandleRequestAsync(context, manager, accessToken, cancellationToken);
            lock (requestsLock)
                requests.Add(request);
            _ = request.ContinueWith(
                completed =>
                {
                    lock (requestsLock)
                        requests.Remove(completed);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
    finally
    {
        listener.Stop();
        Task[] remaining;
        lock (requestsLock)
            remaining = [.. requests];
        await Task.WhenAll(remaining).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
    }
}

static async Task HandleRequestAsync(
    HttpListenerContext context,
    PtyWebSocketSessionManager manager,
    string accessToken,
    CancellationToken cancellationToken)
{
    try
    {
        var path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? string.Empty;
        if (context.Request.HttpMethod == "GET" && path == "/health")
        {
            await WriteTextAsync(context.Response, HttpStatusCode.OK, "ok", "text/plain", cancellationToken);
            return;
        }

        if (context.Request.HttpMethod == "POST" && path == "/sessions")
        {
            if (!TokenAuthentication.AuthenticatesBearer(context.Request.Headers["Authorization"], accessToken))
            {
                await WriteStatusAsync(context.Response, HttpStatusCode.Unauthorized);
                return;
            }

            var credentials = manager.CreateSession(CreateShellStartInfo());
            var json = $$"""{"sessionId":"{{credentials.SessionId:D}}","authenticationToken":"{{credentials.AuthenticationToken}}"}""";
            await WriteTextAsync(context.Response, HttpStatusCode.Created, json, "application/json", cancellationToken);
            return;
        }

        if (!TryReadSessionPath(path, out var sessionId, out var connect))
        {
            await WriteStatusAsync(context.Response, HttpStatusCode.NotFound);
            return;
        }

        if (context.Request.HttpMethod == "DELETE" && !connect)
        {
            var token = TokenAuthentication.ReadBearer(context.Request.Headers["Authorization"]);
            if (token is null)
            {
                await WriteStatusAsync(context.Response, HttpStatusCode.Unauthorized);
                return;
            }

            try
            {
                await manager.TerminateAsync(sessionId, token);
                await WriteStatusAsync(context.Response, HttpStatusCode.NoContent);
            }
            catch (UnauthorizedAccessException)
            {
                await WriteStatusAsync(context.Response, HttpStatusCode.Unauthorized);
            }
            return;
        }

        if (context.Request.HttpMethod != "GET" || !connect || !context.Request.IsWebSocketRequest)
        {
            await WriteStatusAsync(context.Response, HttpStatusCode.BadRequest);
            return;
        }

        var sessionToken = ReadSessionTokenProtocol(context.Request.Headers["Sec-WebSocket-Protocol"]);
        if (sessionToken is null
            || !long.TryParse(context.Request.QueryString["offset"], out var offset)
            || offset < 0)
        {
            await WriteStatusAsync(context.Response, HttpStatusCode.Unauthorized);
            return;
        }

        var webSocketContext = await context.AcceptWebSocketAsync("minipty");
        using var webSocket = webSocketContext.WebSocket;
        try
        {
            var status = await manager.ConnectAsync(sessionId, sessionToken, offset, webSocket, cancellationToken);
            if (status is not null)
                await AwaitPeerCloseAsync(webSocket);
        }
        catch (UnauthorizedAccessException)
        {
            await CloseWebSocketAsync(webSocket, WebSocketCloseStatus.PolicyViolation, "invalid session credentials");
        }
        catch (ArgumentOutOfRangeException)
        {
            await CloseWebSocketAsync(webSocket, WebSocketCloseStatus.InvalidPayloadData, "replay offset unavailable");
        }
        catch (InvalidOperationException)
        {
            await CloseWebSocketAsync(webSocket, WebSocketCloseStatus.PolicyViolation, "session already connected");
        }
        return;
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Bridge request failed: {exception.GetType().Name}: {exception.Message}");
        if (!context.Response.OutputStream.CanWrite)
            return;
        try
        {
            await WriteStatusAsync(context.Response, HttpStatusCode.InternalServerError);
        }
        catch
        {
        }
    }
}

static PtyStartInfo CreateShellStartInfo()
{
    if (OperatingSystem.IsWindows())
    {
        return new PtyStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe",
            TerminalName = "xterm-256color",
        };
    }

    return new PtyStartInfo
    {
        FileName = Environment.GetEnvironmentVariable("SHELL") ?? "/bin/sh",
        Arguments = ["-i"],
        TerminalName = "xterm-256color",
    };
}

static bool TryReadSessionPath(string path, out Guid sessionId, out bool connect)
{
    sessionId = default;
    connect = false;
    var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (segments.Length is < 2 or > 3 || segments[0] != "sessions" || !Guid.TryParse(segments[1], out sessionId))
        return false;
    connect = segments.Length == 3 && segments[2] == "connect";
    return segments.Length == 2 || connect;
}

static string? ReadSessionTokenProtocol(string? protocols)
{
    const string prefix = "minipty-token.";
    if (protocols is null)
        return null;
    foreach (var protocol in protocols.Split(','))
    {
        var candidate = protocol.Trim();
        if (candidate.StartsWith(prefix, StringComparison.Ordinal))
            return candidate[prefix.Length..];
    }
    return null;
}

static async Task WriteTextAsync(
    HttpListenerResponse response,
    HttpStatusCode statusCode,
    string content,
    string contentType,
    CancellationToken cancellationToken)
{
    var bytes = Encoding.UTF8.GetBytes(content);
    response.StatusCode = (int)statusCode;
    response.ContentType = contentType;
    response.ContentEncoding = Encoding.UTF8;
    response.ContentLength64 = bytes.Length;
    await response.OutputStream.WriteAsync(bytes, cancellationToken);
    response.Close();
}

static Task WriteStatusAsync(HttpListenerResponse response, HttpStatusCode statusCode)
{
    response.StatusCode = (int)statusCode;
    response.ContentLength64 = 0;
    response.Close();
    return Task.CompletedTask;
}

static async Task CloseWebSocketAsync(WebSocket webSocket, WebSocketCloseStatus status, string description)
{
    if (webSocket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        return;
    try
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await webSocket.CloseAsync(status, description, timeout.Token);
    }
    catch
    {
    }
}

static async Task AwaitPeerCloseAsync(WebSocket webSocket)
{
    if (webSocket.State != WebSocketState.CloseSent)
        return;
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
    catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or ObjectDisposedException)
    {
    }
}

static async Task<int> RunSmokeAsync()
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    var accessToken = TokenAuthentication.CreateToken();
    await using var manager = new PtyWebSocketSessionManager(new PtyWebSocketSessionManagerOptions
    {
        DetachedSessionTimeout = TimeSpan.FromSeconds(10),
        ExpirationScanInterval = TimeSpan.FromMilliseconds(100),
    });
    using var listener = StartEphemeralListener(out var port);
    var serveTask = ServeAsync(listener, manager, accessToken, timeout.Token);

    using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
    using (var unauthorizedRequest = new HttpRequestMessage(HttpMethod.Post, "sessions"))
    {
        unauthorizedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", new string('0', 64));
        using var unauthorizedResponse = await http.SendAsync(unauthorizedRequest, timeout.Token);
        if (unauthorizedResponse.StatusCode != HttpStatusCode.Unauthorized)
            throw new InvalidDataException("Persistent bridge accepted an invalid service access token.");
    }
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    using var createResponse = await http.PostAsync("sessions", null, timeout.Token);
    createResponse.EnsureSuccessStatusCode();
    using var credentialsJson = JsonDocument.Parse(await createResponse.Content.ReadAsStreamAsync(timeout.Token));
    var sessionId = credentialsJson.RootElement.GetProperty("sessionId").GetGuid();
    var sessionToken = credentialsJson.RootElement.GetProperty("authenticationToken").GetString()!;

    var wrongSessionToken = (sessionToken[0] == '0' ? "1" : "0") + sessionToken[1..];
    using (var unauthorizedClient = await ConnectClientAsync(port, sessionId, wrongSessionToken, 0, timeout.Token))
        await WaitForRejectionAsync(unauthorizedClient, timeout.Token);

    var readyMarker = "MINIPTY_RECONNECT_READY_7A2C";
    var stateMarker = "MINIPTY_RECONNECT_STATE_91B4";
    long acknowledgedOffset = 0;
    using (var first = await ConnectClientAsync(port, sessionId, sessionToken, acknowledgedOffset, timeout.Token))
    {
        var command = OperatingSystem.IsWindows()
            ? $"set MINIPTY_RECONNECT_STATE={stateMarker}&&echo {readyMarker}\r\n"
            : $"export MINIPTY_RECONNECT_STATE={stateMarker}; printf '{readyMarker}\\n'\n";
        await first.SendAsync(Encoding.UTF8.GetBytes(command), WebSocketMessageType.Binary, true, timeout.Token);
        acknowledgedOffset = await ReadUntilAsync(first, acknowledgedOffset, readyMarker, expectedOccurrences: 2, timeout.Token);
        await first.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "smoke detach", timeout.Token);
        await WaitForCloseAsync(first, timeout.Token);
    }

    await Task.Delay(100, timeout.Token);
    using (var second = await ConnectClientAsync(port, sessionId, sessionToken, acknowledgedOffset, timeout.Token))
    {
        var command = OperatingSystem.IsWindows()
            ? "echo %MINIPTY_RECONNECT_STATE%\r\n"
            : "printf '%s\\n' \"$MINIPTY_RECONNECT_STATE\"\n";
        await second.SendAsync(Encoding.UTF8.GetBytes(command), WebSocketMessageType.Binary, true, timeout.Token);
        acknowledgedOffset = await ReadUntilAsync(second, acknowledgedOffset, stateMarker, expectedOccurrences: 1, timeout.Token);
        var exit = OperatingSystem.IsWindows() ? "exit\r\n" : "exit\n";
        await second.SendAsync(Encoding.UTF8.GetBytes(exit), WebSocketMessageType.Binary, true, timeout.Token);
        await WaitForExitAsync(second, acknowledgedOffset, timeout.Token);
    }

    timeout.Cancel();
    try
    {
        await serveTask;
    }
    catch (OperationCanceledException)
    {
    }
    Console.Error.WriteLine("VsCodePersistentBridge reconnect smoke passed.");
    return 0;
}

static async Task<ClientWebSocket> ConnectClientAsync(
    int port,
    Guid sessionId,
    string sessionToken,
    long offset,
    CancellationToken cancellationToken)
{
    var client = new ClientWebSocket();
    client.Options.AddSubProtocol("minipty");
    client.Options.AddSubProtocol($"minipty-token.{sessionToken}");
    await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/sessions/{sessionId:D}/connect?offset={offset}"), cancellationToken);
    return client;
}

static async Task<long> ReadUntilAsync(
    ClientWebSocket client,
    long acknowledgedOffset,
    string marker,
    int expectedOccurrences,
    CancellationToken cancellationToken)
{
    var output = new StringBuilder();
    while (CountOccurrences(output, marker) < expectedOccurrences)
    {
        var message = await ReceiveMessageAsync(client, cancellationToken);
        if (message.Type == WebSocketMessageType.Text)
            continue;
        if (message.Type == WebSocketMessageType.Close)
            throw new InvalidDataException("Persistent bridge closed before the smoke marker was received.");
        output.Append(Encoding.UTF8.GetString(message.Payload));
        acknowledgedOffset += message.Payload.Length;
        await SendAckAsync(client, acknowledgedOffset, cancellationToken);
    }
    return acknowledgedOffset;
}

static int CountOccurrences(StringBuilder value, string marker)
{
    var text = value.ToString();
    var count = 0;
    var offset = 0;
    while ((offset = text.IndexOf(marker, offset, StringComparison.Ordinal)) >= 0)
    {
        count++;
        offset += marker.Length;
    }
    return count;
}

static async Task WaitForExitAsync(ClientWebSocket client, long acknowledgedOffset, CancellationToken cancellationToken)
{
    var sawExit = false;
    while (true)
    {
        (WebSocketMessageType Type, byte[] Payload) message;
        try
        {
            message = await ReceiveMessageAsync(client, cancellationToken);
        }
        catch (WebSocketException) when (sawExit)
        {
            return;
        }
        if (message.Type == WebSocketMessageType.Close)
        {
            if (!sawExit)
                throw new InvalidDataException("Persistent bridge closed without an exit control message.");
            return;
        }
        if (message.Type == WebSocketMessageType.Binary)
        {
            acknowledgedOffset += message.Payload.Length;
            await SendAckAsync(client, acknowledgedOffset, cancellationToken);
            continue;
        }
        using var json = JsonDocument.Parse(message.Payload);
        if (json.RootElement.TryGetProperty("type", out var type) && type.GetString() == "exit")
            sawExit = true;
    }
}

static async Task WaitForCloseAsync(ClientWebSocket client, CancellationToken cancellationToken)
{
    try
    {
        while (client.State is WebSocketState.Open or WebSocketState.CloseSent)
        {
            var message = await ReceiveMessageAsync(client, cancellationToken);
            if (message.Type == WebSocketMessageType.Close)
                return;
        }
    }
    catch (WebSocketException)
    {
        // HttpListener can dispose its server socket before this client writes its close reply.
    }
}

static async Task WaitForRejectionAsync(ClientWebSocket client, CancellationToken cancellationToken)
{
    try
    {
        await WaitForCloseAsync(client, cancellationToken);
    }
    catch (WebSocketException)
    {
        // Authentication failed after the HTTP upgrade. Disposing the server socket after its
        // policy close can race the client's close reply, which is still a rejected connection.
    }
}

static Task SendAckAsync(ClientWebSocket client, long offset, CancellationToken cancellationToken) =>
    client.SendAsync(
        Encoding.UTF8.GetBytes($$"""{"type":"ack","offset":{{offset}}}"""),
        WebSocketMessageType.Text,
        true,
        cancellationToken);

static async Task<(WebSocketMessageType Type, byte[] Payload)> ReceiveMessageAsync(
    ClientWebSocket client,
    CancellationToken cancellationToken)
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

static class TokenAuthentication
{
    public static string CreateToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        try
        {
            return Convert.ToHexString(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static bool IsValidToken(string? token) =>
        token is { Length: 64 } && token.AsSpan().IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;

    public static string? ReadBearer(string? authorization)
    {
        const string prefix = "Bearer ";
        return authorization is not null && authorization.StartsWith(prefix, StringComparison.Ordinal)
            ? authorization[prefix.Length..]
            : null;
    }

    public static bool AuthenticatesBearer(string? authorization, string expectedToken)
    {
        var candidate = ReadBearer(authorization);
        if (!IsValidToken(candidate))
            return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(candidate!),
            Encoding.ASCII.GetBytes(expectedToken));
    }
}
