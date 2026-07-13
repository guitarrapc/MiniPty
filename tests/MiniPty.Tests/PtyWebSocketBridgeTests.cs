using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MiniPty.Terminal;

namespace MiniPty.Tests;

public sealed class PtyWebSocketBridgeTests
{
    [Test]
    public async Task BridgeDeliversChildOutputAsBinaryFrames()
    {
        var (serverSocket, clientSocket) = CreateSocketPair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var bridgeTask = PtyWebSocketBridge.RunAsync(EchoMarkerChild("BRIDGE_MARKER"), serverSocket, cancellationToken: cts.Token);
        var client = new BridgeTestClient(clientSocket);
        await client.RunToCloseAsync(ackEverything: true, cts.Token);

        var status = await bridgeTask.WaitAsync(cts.Token);

        await Assert.That(status.ExitCode).IsEqualTo(0);
        await Assert.That(client.BinaryText).Contains("BRIDGE_MARKER");
    }

    [Test]
    public async Task BridgeWritesClientBinaryFramesToChildInput()
    {
        var (serverSocket, clientSocket) = CreateSocketPair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var bridgeTask = PtyWebSocketBridge.RunAsync(EchoInputThenExitChild(), serverSocket, cancellationToken: cts.Token);
        var client = new BridgeTestClient(clientSocket);
        var clientTask = client.RunToCloseAsync(ackEverything: true, cts.Token);

        await client.SendBinaryAsync("ROUND_TRIP" + Enter, cts.Token);

        await clientTask;
        var status = await bridgeTask.WaitAsync(cts.Token);

        await Assert.That(status.ExitCode).IsEqualTo(0);
        await Assert.That(client.BinaryText).Contains("GOT:ROUND_TRIP");
    }

    [Test]
    public async Task BridgeResizeControlMessageResizesTerminal()
    {
        var (serverSocket, clientSocket) = CreateSocketPair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        PtyStartInfo startInfo;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Windows has no simple size-reporting console child; assert the resize message is
            // accepted and the session completes normally.
            startInfo = EchoInputThenExitChild();
        }
        else
        {
            startInfo = Spawn("sh", ["-c", "IFS= read -r _; stty size"]);
        }

        var bridgeTask = PtyWebSocketBridge.RunAsync(startInfo, serverSocket, cancellationToken: cts.Token);
        var client = new BridgeTestClient(clientSocket);
        var clientTask = client.RunToCloseAsync(ackEverything: true, cts.Token);

        await client.SendTextAsync("{\"type\":\"resize\",\"cols\":100,\"rows\":40}", cts.Token);
        // Give the resize a moment to apply before the child samples the size.
        await Task.Delay(200, cts.Token);
        await client.SendBinaryAsync("go" + Enter, cts.Token);

        await clientTask;
        var status = await bridgeTask.WaitAsync(cts.Token);

        await Assert.That(status.ExitCode).IsEqualTo(0);
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            await Assert.That(client.BinaryText).Contains("40 100");
    }

    [Test]
    public async Task BridgeFlowControlPausesWithoutAcksAndResumesOnAck()
    {
        var (serverSocket, clientSocket) = CreateSocketPair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var options = new PtyBridgeOptions { HighWatermark = 4096, LowWatermark = 1024 };

        var bridgeTask = PtyWebSocketBridge.RunAsync(BulkOutputChild(), serverSocket, options, cts.Token);
        var client = new BridgeTestClient(clientSocket);
        var clientTask = client.RunToCloseAsync(ackEverything: false, cts.Token);

        // Withhold acks: delivery must stall once unacknowledged bytes reach the high watermark.
        long stalledAt = 0;
        while (true)
        {
            cts.Token.ThrowIfCancellationRequested();
            var before = client.BinaryByteCount;
            await Task.Delay(400, cts.Token);
            if (client.BinaryByteCount == before && before >= options.HighWatermark)
            {
                stalledAt = before;
                break;
            }
        }

        // A stalled stream must stay stalled without acks.
        await Task.Delay(300, cts.Token);
        await Assert.That(client.BinaryByteCount).IsEqualTo(stalledAt);

        // Ack everything received so far: delivery must resume and the session must complete.
        client.AckEverythingFromNowOn();
        await client.SendAckAsync(stalledAt, cts.Token);

        await clientTask;
        var status = await bridgeTask.WaitAsync(cts.Token);

        await Assert.That(status.ExitCode).IsEqualTo(0);
        await Assert.That(client.BinaryByteCount).IsGreaterThan(stalledAt);
    }

    [Test]
    public async Task BridgeSendsExitMessageAfterFinalOutputThenClosesNormally()
    {
        var (serverSocket, clientSocket) = CreateSocketPair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var bridgeTask = PtyWebSocketBridge.RunAsync(EchoMarkerChild("ORDERED_MARKER"), serverSocket, cancellationToken: cts.Token);
        var client = new BridgeTestClient(clientSocket);
        await client.RunToCloseAsync(ackEverything: true, cts.Token);

        var status = await bridgeTask.WaitAsync(cts.Token);

        await Assert.That(status.ExitCode).IsEqualTo(0);
        await Assert.That(client.ExitMessage).IsNotNull();
        await Assert.That(client.ExitMessage!.Value.GetProperty("exitCode").GetInt32()).IsEqualTo(0);
        // The exit control message arrived after every binary frame.
        await Assert.That(client.ExitMessageIndex).IsEqualTo(client.MessageCount - 1);
        await Assert.That(client.CloseStatus).IsEqualTo(WebSocketCloseStatus.NormalClosure);
    }

    [Test]
    public async Task BridgeClientCloseKillsChildAndReturnsStatus()
    {
        var (serverSocket, clientSocket) = CreateSocketPair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var bridgeTask = PtyWebSocketBridge.RunAsync(StdinBlockingChild(), serverSocket, cancellationToken: cts.Token);

        // Wait for the session to be live (prompt bytes flowing is not guaranteed; a short delay
        // plus close exercises the mid-session teardown path deterministically enough).
        await Task.Delay(300, cts.Token);
        await clientSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);

        var status = await bridgeTask.WaitAsync(cts.Token);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            await Assert.That(status.Signal).IsEqualTo(9);
        }
        else
        {
            await Assert.That(status.ExitCode).IsEqualTo(1);
            await Assert.That(status.Signal).IsNull();
        }
    }

    [Test]
    public async Task BridgeMalformedControlMessageClosesWithPolicyViolation()
    {
        var (serverSocket, clientSocket) = CreateSocketPair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var bridgeTask = PtyWebSocketBridge.RunAsync(StdinBlockingChild(), serverSocket, cancellationToken: cts.Token);

        await clientSocket.SendAsync(
            Encoding.UTF8.GetBytes("this is not json"),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cts.Token);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await bridgeTask.WaitAsync(cts.Token));

        // The client observes the PolicyViolation close frame.
        var buffer = new byte[1024];
        while (true)
        {
            var result = await clientSocket.ReceiveAsync(buffer.AsMemory(), cts.Token);
            if (result.MessageType == WebSocketMessageType.Close)
                break;
        }

        await Assert.That(clientSocket.CloseStatus).IsEqualTo(WebSocketCloseStatus.PolicyViolation);
    }

    [Test]
    public async Task BridgeUnknownControlTypeIsIgnored()
    {
        var (serverSocket, clientSocket) = CreateSocketPair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var bridgeTask = PtyWebSocketBridge.RunAsync(EchoInputThenExitChild(), serverSocket, cancellationToken: cts.Token);
        var client = new BridgeTestClient(clientSocket);
        var clientTask = client.RunToCloseAsync(ackEverything: true, cts.Token);

        await client.SendTextAsync("{\"type\":\"future-extension\",\"payload\":123}", cts.Token);
        await client.SendBinaryAsync("STILL_ALIVE" + Enter, cts.Token);

        await clientTask;
        var status = await bridgeTask.WaitAsync(cts.Token);

        await Assert.That(status.ExitCode).IsEqualTo(0);
        await Assert.That(client.BinaryText).Contains("GOT:STILL_ALIVE");
    }

    [Test]
    public async Task BridgeOversizeControlMessageClosesWithPolicyViolation()
    {
        var (serverSocket, clientSocket) = CreateSocketPair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var options = new PtyBridgeOptions { MaxControlMessageSize = 64 };

        var bridgeTask = PtyWebSocketBridge.RunAsync(StdinBlockingChild(), serverSocket, options, cts.Token);

        // A valid-JSON control message over the cap must still violate: the bound is on size, not syntax.
        var oversize = "{\"type\":\"ack\",\"bytes\":1,\"padding\":\"" + new string('x', 128) + "\"}";
        await clientSocket.SendAsync(
            Encoding.UTF8.GetBytes(oversize),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cts.Token);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await bridgeTask.WaitAsync(cts.Token));
    }

    [Test]
    public async Task BridgeFragmentedControlMessageIsReassembled()
    {
        var (serverSocket, clientSocket) = CreateSocketPair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var bridgeTask = PtyWebSocketBridge.RunAsync(EchoInputThenExitChild(), serverSocket, cancellationToken: cts.Token);
        var client = new BridgeTestClient(clientSocket);
        var clientTask = client.RunToCloseAsync(ackEverything: true, cts.Token);

        // Split one resize message across two text frames; a broken accumulation path would parse
        // half a JSON document and fail the session with PolicyViolation.
        var resize = Encoding.UTF8.GetBytes("{\"type\":\"resize\",\"cols\":100,\"rows\":40}");
        await clientSocket.SendAsync(resize.AsMemory(0, 10), WebSocketMessageType.Text, endOfMessage: false, cts.Token);
        await clientSocket.SendAsync(resize.AsMemory(10), WebSocketMessageType.Text, endOfMessage: true, cts.Token);
        await client.SendBinaryAsync("FRAGMENTS_OK" + Enter, cts.Token);

        await clientTask;
        var status = await bridgeTask.WaitAsync(cts.Token);

        await Assert.That(status.ExitCode).IsEqualTo(0);
        await Assert.That(client.BinaryText).Contains("GOT:FRAGMENTS_OK");
    }

    [Test]
    public async Task BridgeCancellationUnwedgesSendBlockedByStalledClient()
    {
        // Server→client direction bounded like a full TCP send buffer; the client never reads.
        var (serverSocket, _) = CreateSocketPair(serverToClientCapacityChunks: 2);
        using var cts = new CancellationTokenSource();
        // Disable watermark pausing so the pump drives straight into the wedged send.
        var options = new PtyBridgeOptions { HighWatermark = long.MaxValue / 2, LowWatermark = 1 };

        var bridgeTask = PtyWebSocketBridge.RunAsync(BulkOutputChild(), serverSocket, options, cts.Token);

        // Give the pump time to fill the pipe and wedge inside SendAsync, then cancel.
        await Task.Delay(500);
        await Assert.That(bridgeTask.IsCompleted).IsFalse();
        await cts.CancelAsync();

        // Without teardown-token sends this hangs forever. WaitAsync(TimeSpan) is deliberate: a
        // hang surfaces as TimeoutException and fails the assertion, unlike WaitAsync(token)
        // whose timeout would also throw an OperationCanceledException and mask the hang.
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await bridgeTask.WaitAsync(TimeSpan.FromSeconds(15)));
        await Assert.That(bridgeTask.IsCompleted).IsTrue();
    }

    [Test]
    public async Task BridgeOptionValidationRejectsInvertedWatermarks()
    {
        var (serverSocket, _) = CreateSocketPair();
        var options = new PtyBridgeOptions { HighWatermark = 100, LowWatermark = 100 };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await PtyWebSocketBridge.RunAsync(StdinBlockingChild(), serverSocket, options));
    }

    // ---- test doubles and helpers ----

    /// <summary>
    /// Client-side driver: receives every message in order, tracks binary bytes, optionally acks
    /// each binary frame, records the exit control message, and completes the close handshake.
    /// </summary>
    private sealed class BridgeTestClient
    {
        private readonly WebSocket _socket;
        private readonly StringBuilder _binaryText = new();
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private long _binaryByteCount;
        private int _messageCount;
        private volatile bool _ackEverything;

        public BridgeTestClient(WebSocket socket) => _socket = socket;

        public string BinaryText
        {
            get
            {
                lock (_binaryText)
                {
                    return _binaryText.ToString();
                }
            }
        }

        public long BinaryByteCount => Interlocked.Read(ref _binaryByteCount);
        public int MessageCount => _messageCount;
        public JsonElement? ExitMessage { get; private set; }
        public int ExitMessageIndex { get; private set; } = -1;
        public WebSocketCloseStatus? CloseStatus { get; private set; }

        public void AckEverythingFromNowOn() => _ackEverything = true;

        public Task SendAckAsync(long bytes, CancellationToken cancellationToken) =>
            SendAsync(Encoding.UTF8.GetBytes($"{{\"type\":\"ack\",\"bytes\":{bytes}}}"), WebSocketMessageType.Text, cancellationToken);

        public Task SendBinaryAsync(string text, CancellationToken cancellationToken) =>
            SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Binary, cancellationToken);

        public Task SendTextAsync(string json, CancellationToken cancellationToken) =>
            SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, cancellationToken);

        /// <summary>WebSocket allows only one outstanding send; serialize acks and test-driven sends.</summary>
        private async Task SendAsync(byte[] payload, WebSocketMessageType messageType, CancellationToken cancellationToken)
        {
            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                await _socket.SendAsync(payload, messageType, endOfMessage: true, cancellationToken);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public async Task RunToCloseAsync(bool ackEverything, CancellationToken cancellationToken)
        {
            _ackEverything = ackEverything;
            var buffer = new byte[64 * 1024];
            var message = new MemoryStream();

            while (true)
            {
                var result = await _socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    CloseStatus = _socket.CloseStatus;
                    if (_socket.State == WebSocketState.CloseReceived)
                        await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken);
                    return;
                }

                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                    continue;

                var payload = message.ToArray();
                message.SetLength(0);
                _messageCount++;

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    Interlocked.Add(ref _binaryByteCount, payload.Length);
                    lock (_binaryText)
                    {
                        _binaryText.Append(Encoding.UTF8.GetString(payload));
                    }

                    if (_ackEverything && payload.Length > 0)
                        await SendAckAsync(payload.Length, cancellationToken);
                    continue;
                }

                var json = JsonDocument.Parse(payload).RootElement.Clone();
                if (json.TryGetProperty("type", out var type) && type.GetString() == "exit")
                {
                    ExitMessage = json;
                    ExitMessageIndex = _messageCount - 1;
                }
            }
        }
    }

    private static (WebSocket Server, WebSocket Client) CreateSocketPair(int serverToClientCapacityChunks = 0)
    {
        var (serverStream, clientStream) = InMemoryDuplexStream.CreatePair(serverToClientCapacityChunks);
        var server = WebSocket.CreateFromStream(serverStream, new WebSocketCreationOptions { IsServer = true });
        var client = WebSocket.CreateFromStream(clientStream, new WebSocketCreationOptions { IsServer = false });
        return (server, client);
    }

    /// <summary>
    /// In-memory duplex stream pair carrying the raw WebSocket protocol between
    /// <see cref="WebSocket.CreateFromStream(Stream, WebSocketCreationOptions)"/> endpoints.
    /// Unbounded, so transport buffering never interferes with flow-control assertions.
    /// </summary>
    private sealed class InMemoryDuplexStream : Stream
    {
        private readonly BytePipe _readPipe;
        private readonly BytePipe _writePipe;

        private InMemoryDuplexStream(BytePipe readPipe, BytePipe writePipe)
        {
            _readPipe = readPipe;
            _writePipe = writePipe;
        }

        /// <param name="aToBCapacityChunks">
        /// When positive, bounds the A→B direction to that many written chunks so writes block like
        /// a full TCP send buffer (for wedged-client tests). 0 keeps the direction unbounded.
        /// </param>
        public static (InMemoryDuplexStream A, InMemoryDuplexStream B) CreatePair(int aToBCapacityChunks = 0)
        {
            var aToB = new BytePipe(aToBCapacityChunks);
            var bToA = new BytePipe(0);
            return (new InMemoryDuplexStream(bToA, aToB), new InMemoryDuplexStream(aToB, bToA));
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) =>
            _readPipe.ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _readPipe.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _readPipe.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count) =>
            _writePipe.Write(buffer.AsSpan(offset, count));

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            _writePipe.WriteAsync(buffer, cancellationToken);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _writePipe.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _writePipe.Complete();
                _readPipe.Complete();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class BytePipe
    {
        private readonly Lock _lock = new();
        private readonly Queue<byte[]> _chunks = new();
        private readonly SemaphoreSlim _signal = new(0);
        private readonly SemaphoreSlim? _space;
        private readonly int _capacityChunks;
        private byte[]? _current;
        private int _currentOffset;
        private bool _completed;

        public BytePipe(int capacityChunks)
        {
            _capacityChunks = capacityChunks;
            _space = capacityChunks > 0 ? new SemaphoreSlim(capacityChunks) : null;
        }

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            if (data.IsEmpty)
                return;

            if (_space is not null)
            {
                await _space.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (_completed)
                    return;
            }

            Write(data.Span);
        }

        public void Write(ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty)
                return;

            lock (_lock)
            {
                if (_completed)
                    return;
                _chunks.Enqueue(data.ToArray());
            }

            _signal.Release();
        }

        public void Complete()
        {
            lock (_lock)
            {
                _completed = true;
            }

            _signal.Release();
            if (_space is not null)
            {
                for (var i = 0; i < _capacityChunks; i++)
                    _space.Release();
            }
        }

        public async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken)
        {
            while (true)
            {
                lock (_lock)
                {
                    if (_current is null && _chunks.Count > 0)
                    {
                        _current = _chunks.Dequeue();
                        _currentOffset = 0;
                    }

                    if (_current is not null)
                    {
                        var available = _current.Length - _currentOffset;
                        var count = Math.Min(available, destination.Length);
                        _current.AsSpan(_currentOffset, count).CopyTo(destination.Span);
                        _currentOffset += count;
                        if (_currentOffset == _current.Length)
                        {
                            _current = null;
                            _space?.Release();
                        }

                        return count;
                    }

                    if (_completed)
                        return 0;
                }

                await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static PtyStartInfo Spawn(string fileName, IReadOnlyList<string> arguments) =>
        new() { FileName = fileName, Arguments = arguments, Size = new(80, 24) };

    private static PtyStartInfo EchoMarkerChild(string marker) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Spawn(WindowsComSpec(), ["/c", $"echo {marker}"])
            : Spawn("sh", ["-c", $"printf '{marker}\\n'"]);

    /// <summary>Child that echoes one stdin line back to stdout, then exits 0. Windows uses delayed
    /// expansion because %LINE% would be expanded before set /p runs.</summary>
    private static PtyStartInfo EchoInputThenExitChild() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Spawn(WindowsComSpec(), ["/v:on", "/c", "set /p LINE= & echo GOT:!LINE!"])
            : Spawn("sh", ["-c", "IFS= read -r line; printf 'GOT:%s\\n' \"$line\""]);

    private static PtyStartInfo StdinBlockingChild() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Spawn(WindowsComSpec(), ["/c", "set /p DUMMY="])
            : Spawn("sh", ["-c", "IFS= read -r _"]);

    /// <summary>Child that emits sustained bulk output (~256 KiB) then exits 0.</summary>
    private static PtyStartInfo BulkOutputChild() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Spawn(WindowsComSpec(), ["/c", "for /l %i in (1,1,2048) do @echo 0123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567"])
            : Spawn("sh", ["-c", "i=0; while [ $i -lt 2048 ]; do printf '%0128d\\n' \"$i\"; i=$((i+1)); done"]);

    private static string WindowsComSpec() =>
        Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";

    /// <summary>ConPTY line submission needs CR; Unix canonical mode accepts LF.</summary>
    private static string Enter =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "\r\n" : "\n";
}
