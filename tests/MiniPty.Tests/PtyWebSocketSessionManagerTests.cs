using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MiniPty.Terminal;

namespace MiniPty.Tests;

public sealed class PtyWebSocketSessionManagerTests
{
    [Test]
    public async Task PersistentSessionReconnectsFromAcknowledgedOffset()
    {
        await using var manager = new PtyWebSocketSessionManager(TestOptions());
        var credentials = manager.CreateSession(ReadyThenEchoChild());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var (server1, socket1) = PtyWebSocketBridgeTests.CreateSocketPair();
        using (server1)
        using (socket1)
        {
            var connection1 = manager.ConnectAsync(
                credentials.SessionId,
                credentials.AuthenticationToken,
                acknowledgedOffset: 0,
                server1,
                timeout.Token);
            var client1 = new PersistentClient(socket1);
            await client1.ReceiveUntilAsync("READY", timeout.Token);
            var resumeOffset = client1.AcknowledgedOffset;

            await socket1.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "detach", timeout.Token);
            await client1.ReceiveToCloseAsync(timeout.Token);
            await Assert.That(await connection1.WaitAsync(timeout.Token)).IsNull();

            var (server2, socket2) = PtyWebSocketBridgeTests.CreateSocketPair();
            using (server2)
            using (socket2)
            {
                var connection2 = manager.ConnectAsync(
                    credentials.SessionId,
                    credentials.AuthenticationToken,
                    resumeOffset,
                    server2,
                    timeout.Token);
                var client2 = new PersistentClient(socket2, resumeOffset);
                await client2.SendInputAsync(OperatingSystem.IsWindows() ? "RECONNECTED\r" : "RECONNECTED\n", timeout.Token);
                await client2.ReceiveToCloseAsync(timeout.Token);
                var status = await connection2.WaitAsync(timeout.Token);

                await Assert.That(status).IsNotNull();
                await Assert.That(status!.Value.ExitCode).IsEqualTo(0);
                await Assert.That(client2.Output).Contains("RECONNECTED");
                await Assert.That(manager.Count).IsEqualTo(0);
            }
        }
    }

    [Test]
    public async Task PersistentSessionBuffersOutputBeforeFirstConnection()
    {
        await using var manager = new PtyWebSocketSessionManager(TestOptions());
        var credentials = manager.CreateSession(MarkerChild("DETACHED_MARKER"));
        await Task.Delay(100);
        var (server, socket) = PtyWebSocketBridgeTests.CreateSocketPair();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using (server)
        using (socket)
        {
            var connection = manager.ConnectAsync(
                credentials.SessionId,
                credentials.AuthenticationToken,
                0,
                server,
                timeout.Token);
            var client = new PersistentClient(socket);
            await client.ReceiveToCloseAsync(timeout.Token);
            var status = await connection.WaitAsync(timeout.Token);

            await Assert.That(status).IsNotNull();
            await Assert.That(client.Output).Contains("DETACHED_MARKER");
        }
    }

    [Test]
    public async Task PersistentSessionRejectsWrongMalformedAndUnknownCredentials()
    {
        await using var manager = new PtyWebSocketSessionManager(TestOptions());
        var credentials = manager.CreateSession(BlockingChild());

        await AssertUnauthorizedAsync(manager, credentials.SessionId, new string('0', 64));
        await AssertUnauthorizedAsync(manager, credentials.SessionId, "not-a-token");
        await AssertUnauthorizedAsync(manager, Guid.NewGuid(), credentials.AuthenticationToken);

        await manager.TerminateAsync(credentials.SessionId, credentials.AuthenticationToken);
    }

    [Test]
    public async Task PersistentSessionCredentialsDoNotExposeTokenInToString()
    {
        await using var manager = new PtyWebSocketSessionManager(TestOptions());
        var credentials = manager.CreateSession(BlockingChild());

        await Assert.That(credentials.ToString()).DoesNotContain(credentials.AuthenticationToken);

        await manager.TerminateAsync(credentials.SessionId, credentials.AuthenticationToken);
    }

    [Test]
    public async Task PersistentSessionRejectsConcurrentConnection()
    {
        await using var manager = new PtyWebSocketSessionManager(TestOptions());
        var credentials = manager.CreateSession(BlockingChild());
        var (server1, socket1) = PtyWebSocketBridgeTests.CreateSocketPair();
        var (server2, socket2) = PtyWebSocketBridgeTests.CreateSocketPair();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using (server1)
        using (socket1)
        using (server2)
        using (socket2)
        {
            var first = manager.ConnectAsync(
                credentials.SessionId,
                credentials.AuthenticationToken,
                0,
                server1,
                timeout.Token);

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await manager.ConnectAsync(
                    credentials.SessionId,
                    credentials.AuthenticationToken,
                    0,
                    server2,
                    timeout.Token));

            await socket1.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "detach", timeout.Token);
            var client = new PersistentClient(socket1);
            await client.ReceiveToCloseAsync(timeout.Token);
            await first.WaitAsync(timeout.Token);
        }

        await manager.TerminateAsync(credentials.SessionId, credentials.AuthenticationToken);
    }

    [Test]
    public async Task PersistentSessionExpiresWhileDetached()
    {
        var options = TestOptions() with
        {
            DetachedSessionTimeout = TimeSpan.FromMilliseconds(100),
            ExpirationScanInterval = TimeSpan.FromMilliseconds(20),
        };
        await using var manager = new PtyWebSocketSessionManager(options);
        var credentials = manager.CreateSession(BlockingChild());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (manager.Count != 0)
            await Task.Delay(20, timeout.Token);

        await AssertUnauthorizedAsync(manager, credentials.SessionId, credentials.AuthenticationToken);
    }

    [Test]
    public async Task PersistentSessionRejectsUnavailableResumeOffset()
    {
        await using var manager = new PtyWebSocketSessionManager(TestOptions());
        var credentials = manager.CreateSession(BlockingChild());
        var (server, socket) = PtyWebSocketBridgeTests.CreateSocketPair();
        using (server)
        using (socket)
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
                await manager.ConnectAsync(
                    credentials.SessionId,
                    credentials.AuthenticationToken,
                    1,
                    server));
        }

        await manager.TerminateAsync(credentials.SessionId, credentials.AuthenticationToken);
    }

    [Test]
    public async Task PersistentSessionTerminateRequiresAuthentication()
    {
        await using var manager = new PtyWebSocketSessionManager(TestOptions());
        var credentials = manager.CreateSession(BlockingChild());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await manager.TerminateAsync(credentials.SessionId, new string('F', 64)));
        await Assert.That(manager.Count).IsEqualTo(1);

        await manager.TerminateAsync(credentials.SessionId, credentials.AuthenticationToken);
        await Assert.That(manager.Count).IsEqualTo(0);
    }

    [Test]
    public async Task PersistentSessionEnforcesSessionLimit()
    {
        await using var manager = new PtyWebSocketSessionManager(TestOptions() with { MaxSessions = 1 });
        var credentials = manager.CreateSession(BlockingChild());

        Assert.Throws<InvalidOperationException>(() => manager.CreateSession(BlockingChild()));

        await manager.TerminateAsync(credentials.SessionId, credentials.AuthenticationToken);
    }

    [Test]
    public async Task PersistentSessionRejectsAcknowledgementBeyondSentOutput()
    {
        await using var manager = new PtyWebSocketSessionManager(TestOptions());
        var credentials = manager.CreateSession(BlockingChild());
        var (server, socket) = PtyWebSocketBridgeTests.CreateSocketPair();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using (server)
        using (socket)
        {
            var connection = manager.ConnectAsync(
                credentials.SessionId,
                credentials.AuthenticationToken,
                0,
                server,
                timeout.Token);
            await socket.SendAsync(
                "{\"type\":\"ack\",\"offset\":1}"u8.ToArray(),
                WebSocketMessageType.Text,
                endOfMessage: true,
                timeout.Token);

            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await connection.WaitAsync(timeout.Token));
        }

        await manager.TerminateAsync(credentials.SessionId, credentials.AuthenticationToken);
    }

    [Test]
    public async Task PersistentSessionCancellationUnblocksStalledClientSend()
    {
        await using var manager = new PtyWebSocketSessionManager(TestOptions());
        var credentials = manager.CreateSession(BulkOutputChild());
        var (server, socket) = PtyWebSocketBridgeTests.CreateSocketPair(serverToClientCapacityChunks: 2);
        using var connectionCts = new CancellationTokenSource();
        using (server)
        using (socket)
        {
            var connection = manager.ConnectAsync(
                credentials.SessionId,
                credentials.AuthenticationToken,
                0,
                server,
                connectionCts.Token);
            await Task.Delay(200);
            connectionCts.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await connection.WaitAsync(TimeSpan.FromSeconds(10)));
        }

        await manager.TerminateAsync(credentials.SessionId, credentials.AuthenticationToken);
    }

    private static async Task AssertUnauthorizedAsync(
        PtyWebSocketSessionManager manager,
        Guid sessionId,
        string token)
    {
        var (server, client) = PtyWebSocketBridgeTests.CreateSocketPair();
        using (server)
        using (client)
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
                await manager.ConnectAsync(sessionId, token, 0, server));
        }
    }

    private static PtyWebSocketSessionManagerOptions TestOptions() => new()
    {
        DetachedSessionTimeout = TimeSpan.FromSeconds(10),
        ExpirationScanInterval = TimeSpan.FromMilliseconds(50),
        ReplayBufferSize = 128 * 1024,
        MaxOutputFrameSize = 16 * 1024,
        BridgeOptions = new PtyBridgeOptions
        {
            CloseTimeout = TimeSpan.FromSeconds(2),
        },
    };

    private static PtyStartInfo Spawn(string fileName, IReadOnlyList<string> arguments) =>
        new() { FileName = fileName, Arguments = arguments };

    private static PtyStartInfo ReadyThenEchoChild() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Spawn(WindowsComSpec(), ["/v:on", "/c", "echo READY & set /p LINE= & echo GOT:!LINE!"])
            : Spawn("sh", ["-c", "printf 'READY\\n'; IFS= read -r line; printf 'GOT:%s\\n' \"$line\""]);

    private static PtyStartInfo MarkerChild(string marker) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Spawn(WindowsComSpec(), ["/c", $"echo {marker}"])
            : Spawn("sh", ["-c", $"printf '{marker}\\n'"]);

    private static PtyStartInfo BlockingChild() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Spawn(WindowsComSpec(), ["/c", "set /p DUMMY="])
            : Spawn("sh", ["-c", "IFS= read -r _"]);

    private static PtyStartInfo BulkOutputChild() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Spawn(WindowsComSpec(), ["/c", "for /l %i in (1,1,4096) do @echo 0123456789012345678901234567890123456789012345678901234567890123"])
            : Spawn("sh", ["-c", "i=0; while [ $i -lt 4096 ]; do printf '%064d\\n' \"$i\"; i=$((i+1)); done"]);

    private static string WindowsComSpec() =>
        Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";

    private sealed class PersistentClient(WebSocket socket, long acknowledgedOffset = 0)
    {
        private readonly StringBuilder _output = new();
        private long _pendingOffset = -1;
        private int _pendingBytes;

        public string Output => _output.ToString();
        public long AcknowledgedOffset { get; private set; } = acknowledgedOffset;

        public Task SendInputAsync(string input, CancellationToken cancellationToken) =>
            socket.SendAsync(
                Encoding.UTF8.GetBytes(input),
                WebSocketMessageType.Binary,
                endOfMessage: true,
                cancellationToken);

        public async Task ReceiveUntilAsync(string marker, CancellationToken cancellationToken)
        {
            while (!Output.Contains(marker, StringComparison.Ordinal))
                await ReceiveOneAsync(cancellationToken);
        }

        public async Task ReceiveToCloseAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                var closed = await ReceiveOneAsync(cancellationToken);
                if (closed)
                    return;
            }
        }

        private async Task<bool> ReceiveOneAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[128 * 1024];
            var result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (socket.State == WebSocketState.CloseReceived)
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken);
                return true;
            }

            if (!result.EndOfMessage)
                throw new InvalidDataException("The test client received an unexpected fragmented message.");
            if (result.MessageType == WebSocketMessageType.Text)
            {
                using var document = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
                var root = document.RootElement;
                if (root.GetProperty("type").GetString() == "output")
                {
                    _pendingOffset = root.GetProperty("offset").GetInt64();
                    _pendingBytes = root.GetProperty("bytes").GetInt32();
                }
                return false;
            }

            if (_pendingOffset < 0 || _pendingBytes != result.Count)
                throw new InvalidDataException("Binary output did not match its output envelope.");
            _output.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            AcknowledgedOffset = _pendingOffset + result.Count;
            _pendingOffset = -1;
            _pendingBytes = 0;
            await socket.SendAsync(
                Encoding.UTF8.GetBytes($"{{\"type\":\"ack\",\"offset\":{AcknowledgedOffset}}}"),
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken);
            return false;
        }
    }
}
