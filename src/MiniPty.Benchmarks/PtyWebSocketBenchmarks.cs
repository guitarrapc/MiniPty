using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using MiniPty.Terminal;

namespace MiniPty.Benchmarks;

/// <summary>
/// A session bridged to a WebSocket client by <see cref="PtyWebSocketBridge"/>, end to end over loopback TCP: what a
/// web host pays per session, including the WebSocket frames the bridge sends.
/// </summary>
/// <remarks>
/// <para>
/// The client acknowledges output the way the xterm.js flow-control guide describes, and returns the number of output
/// bytes it received. Allocations include the child's spawn, the loopback connection, and both WebSockets.
/// </para>
/// <para>
/// Session counts, thread counts, echo latency, and frames per 32 KiB are measured by the
/// <c>samples/SingleFiles/WebTerminalScale.cs</c> bench mode instead, against a real ASP.NET Core host.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[SimpleJob(RuntimeMoniker.Net10_0, warmupCount: 2, iterationCount: 5, launchCount: 1)]
public class PtyWebSocketBenchmarks
{
    private const int AckChunk = 131072;

    private readonly byte[] receiveBuffer = new byte[64 * 1024];
    private PtyStartInfo smallStdout = null!;
    private Socket listener = null!;
    private bool supported;

    [GlobalSetup]
    public void Setup()
    {
        supported = BenchmarkPtyCommands.IsSupported;
        if (!supported)
            return;

        smallStdout = BenchmarkPtyCommands.SmallStdout(32_768);
        listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen();
    }

    [GlobalCleanup]
    public void Cleanup() => listener?.Dispose();

    [BenchmarkCategory("WebSocket", "Binary")]
    [Benchmark]
    public async Task<long> Bridge_32KiB_Bytes()
    {
        if (!supported)
            return 0;

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        var connect = client.ConnectAsync(listener.LocalEndPoint!);
        var server = await listener.AcceptAsync().ConfigureAwait(false);
        try
        {
            await connect.ConfigureAwait(false);
            server.NoDelay = true;

            using var serverSocket = WebSocket.CreateFromStream(new NetworkStream(server), new WebSocketCreationOptions { IsServer = true });
            using var clientSocket = WebSocket.CreateFromStream(new NetworkStream(client), new WebSocketCreationOptions { IsServer = false });
            var bridge = PtyWebSocketBridge.RunAsync(smallStdout, serverSocket);

            var receive = ReceiveUntilCloseAsync(clientSocket);
            if (await Task.WhenAny(receive, bridge).ConfigureAwait(false) != receive)
            {
                // The bridge ended before it closed the socket, so it failed, for example because the child could not
                // start. Its exception says why; the client would otherwise wait for frames that never come.
                clientSocket.Abort();
                await bridge.ConfigureAwait(false);
            }

            var received = await receive.ConfigureAwait(false);

            // Reply to the close, let the bridge finish, then close the connection as a web server does once its
            // handler returns. A .NET client waits up to a second for the server to close the connection after the
            // close handshake, which would otherwise dominate the measurement.
            var reply = clientSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            await bridge.ConfigureAwait(false);
            server.Dispose();
            await reply.ConfigureAwait(false);
            return received;
        }
        finally
        {
            server.Dispose();
        }
    }

    /// <summary>Receives output until the server's close frame, acknowledging it; the caller replies to the close.</summary>
    private async Task<long> ReceiveUntilCloseAsync(WebSocket socket)
    {
        long received = 0;
        long unacked = 0;
        while (true)
        {
            var result = await socket.ReceiveAsync(receiveBuffer.AsMemory(), CancellationToken.None).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                return received;

            if (result.MessageType != WebSocketMessageType.Binary)
                continue;

            received += result.Count;
            unacked += result.Count;
            if (unacked >= AckChunk)
            {
                var ack = Encoding.UTF8.GetBytes($"{{\"type\":\"ack\",\"bytes\":{unacked}}}");
                unacked = 0;
                await socket.SendAsync(ack, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }
}
