using System.Net.WebSockets;
using MiniPty.Terminal.Internal;

namespace MiniPty.Terminal;

/// <summary>
/// Runs a full terminal session over one WebSocket: spawn, output pump with watermark/ACK flow
/// control, input and resize handling, exit notification, and close. Designed for xterm.js
/// frontends; the protocol is transport-shape only (binary frames = raw PTY data, text frames =
/// JSON control messages) so any client can implement it.
/// </summary>
/// <remarks>
/// <para>Protocol:</para>
/// <list type="bullet">
/// <item><description>server → client binary: raw PTY output bytes.</description></item>
/// <item><description>client → server binary: raw input bytes, written to the PTY verbatim.</description></item>
/// <item><description>client → server text: <c>{"type":"resize","cols":120,"rows":30}</c> or <c>{"type":"ack","bytes":131072}</c>.</description></item>
/// <item><description>server → client text: <c>{"type":"exit","exitCode":0,"signal":15}</c>, sent after the final output frame.</description></item>
/// </list>
/// <para>
/// Unknown control <c>type</c> values are ignored (forward compatibility). Malformed JSON or an
/// oversize control message closes the socket with <c>PolicyViolation</c> and kills the child.
/// </para>
/// </remarks>
public static class PtyWebSocketBridge
{
    /// <summary>
    /// Spawns the child and bridges it to <paramref name="webSocket"/> until the child exits or
    /// the socket closes. The bridge owns the terminal: on child exit it sends the exit message
    /// and closes the socket; on socket close, error, or cancellation it kills the child. The
    /// session is disposed before this method returns.
    /// </summary>
    /// <param name="startInfo">Child process launch configuration.</param>
    /// <param name="webSocket">
    /// An open <see cref="WebSocket"/> (Kestrel/HttpListener accept, <see cref="ClientWebSocket"/>,
    /// or <see cref="WebSocket.CreateFromStream(Stream, WebSocketCreationOptions)"/>).
    /// </param>
    /// <param name="options">Bridge behavior, or <see langword="null"/> for defaults.</param>
    /// <param name="cancellationToken">When canceled, the child is killed and the session torn down.</param>
    /// <returns>The child exit status.</returns>
    /// <exception cref="InvalidDataException">The client sent a malformed or oversize control message.</exception>
    /// <exception cref="OperationCanceledException">The bridge was canceled.</exception>
    public static Task<PtyExitStatus> RunAsync(
        PtyStartInfo startInfo,
        WebSocket webSocket,
        PtyBridgeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(webSocket);
        var effectiveOptions = options ?? new PtyBridgeOptions();
        effectiveOptions.Validate();

        return new BridgeSession(webSocket, effectiveOptions).RunAsync(startInfo, cancellationToken);
    }

    private sealed class BridgeSession
    {
        private readonly WebSocket _webSocket;
        private readonly PtyBridgeOptions _options;
        private readonly BridgeFlowControl _flowControl;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private volatile bool _discardOutput;
        // Bridge-lifetime token used for output sends instead of the pump token: a client that
        // stops reading can wedge SendAsync via transport backpressure, and the pump token is not
        // canceled until disposal. Canceling this token in every teardown path unwedges the send.
        private CancellationToken _sendCancellation;

        public BridgeSession(WebSocket webSocket, PtyBridgeOptions options)
        {
            _webSocket = webSocket;
            _options = options;
            _flowControl = new BridgeFlowControl(options.HighWatermark, options.LowWatermark);
        }

        public async Task<PtyExitStatus> RunAsync(PtyStartInfo startInfo, CancellationToken cancellationToken)
        {
            using var teardownCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _sendCancellation = teardownCts.Token;
            var terminal = PtyTerminal.Start(startInfo, new PtyTerminalOptions { Output = SendOutputAsync });
            _flowControl.Attach(terminal);

            Task? receiveTask = null;
            try
            {
                receiveTask = ReceiveLoopAsync(terminal, teardownCts.Token);
                var cancellationTask = WaitForCancellationAsync(cancellationToken);
                var first = await Task.WhenAny(terminal.Completion, receiveTask, cancellationTask).ConfigureAwait(false);

                if (first == terminal.Completion)
                {
                    // Child exited (or the output pump faulted; the await rethrows in that case).
                    // Completion resolves only after the final output frame was sent, so the exit
                    // message is guaranteed to follow all data.
                    var status = await terminal.Completion.ConfigureAwait(false);
                    await SendExitAndCloseAsync(status, cancellationToken).ConfigureAwait(false);
                    await AwaitReceiveShutdownAsync(receiveTask).ConfigureAwait(false);
                    return status;
                }

                // The receive side ended first: client close, protocol violation, socket fault, or
                // cancellation. Stop sending (canceling the teardown token unwedges a SendAsync
                // blocked on a client that stopped reading), release any flow-control pause so the
                // post-kill drain can complete, and kill the child.
                _discardOutput = true;
                _flowControl.Disable();
                teardownCts.Cancel();
                AbortWebSocket();
                terminal.Kill();

                PtyExitStatus killedStatus;
                try
                {
                    killedStatus = await terminal.Completion.WaitAsync(_options.CloseTimeout, CancellationToken.None).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    AbortWebSocket();
                    killedStatus = await WaitForKilledStatusAsync(terminal, _options.CloseTimeout).ConfigureAwait(false);
                }
                catch
                {
                    // The pump faulted because teardown canceled its in-flight send (or the socket
                    // died mid-send). The child was killed either way; read the status directly.
                    killedStatus = await WaitForKilledStatusAsync(terminal, _options.CloseTimeout).ConfigureAwait(false);
                }

                // Rethrows InvalidDataException (protocol violation) or OperationCanceledException.
                try
                {
                    await receiveTask.WaitAsync(_options.CloseTimeout, CancellationToken.None).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    AbortWebSocket();
                }
                await RespondToClientCloseAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return killedStatus;
            }
            finally
            {
                _discardOutput = true;
                _flowControl.Disable();
                teardownCts.Cancel();
                AbortWebSocket();
                if (receiveTask is not null)
                {
                    try
                    {
                        await receiveTask.WaitAsync(_options.CloseTimeout, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        AbortWebSocket();
                    }
                    catch
                    {
                        // The primary outcome (return value or exception) is already decided.
                    }
                }

                await terminal.DisposeAsync().ConfigureAwait(false);
            }
        }

        private async ValueTask SendOutputAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            if (_discardOutput)
                return;

            // The bridge-lifetime token, not the pump token: it cancels on every teardown path, so
            // a send wedged by a client that stopped reading cannot park the pump forever.
            var sendCancellation = _sendCancellation;
            await _sendLock.WaitAsync(sendCancellation).ConfigureAwait(false);
            try
            {
                if (_discardOutput)
                    return;

                await _webSocket.SendAsync(data, WebSocketMessageType.Binary, endOfMessage: true, sendCancellation).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }

            _flowControl.OnSent(data.Length);
        }

        /// <summary>
        /// Reads the exit status directly from the session after a kill whose drain faulted.
        /// SIGKILL / TerminateProcess complete quickly; the bounded poll covers scheduler lag.
        /// </summary>
        private static async Task<PtyExitStatus> WaitForKilledStatusAsync(PtyTerminal terminal, TimeSpan timeout)
        {
            var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
            while (true)
            {
                if (terminal.ExitStatus is { } status)
                    return status;

                if (Environment.TickCount64 >= deadline)
                    throw new TimeoutException("The child process did not report exit after being killed.");

                await Task.Delay(10).ConfigureAwait(false);
            }
        }

        private async Task ReceiveLoopAsync(PtyTerminal terminal, CancellationToken cancellationToken)
        {
            var buffer = new byte[_options.ReceiveBufferSize];
            byte[]? controlBuffer = null;
            var controlLength = 0;

            while (true)
            {
                ValueWebSocketReceiveResult result;
                try
                {
                    result = await _webSocket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                }
                catch (WebSocketException)
                {
                    // Abrupt client disconnect: same outcome as a clean close.
                    return;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                    return;

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    // Fragments arrive in order; each one is raw input and can be written directly.
                    if (result.Count > 0)
                        await WriteInputAsync(terminal, buffer.AsMemory(0, result.Count), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // Text frame: control message. Accumulate fragments up to the configured cap.
                if (controlLength + result.Count > _options.MaxControlMessageSize)
                    await ViolateProtocolAsync().ConfigureAwait(false);

                if (result.EndOfMessage && controlLength == 0)
                {
                    await HandleControlAsync(buffer.AsMemory(0, result.Count), terminal).ConfigureAwait(false);
                    continue;
                }

                controlBuffer ??= new byte[_options.MaxControlMessageSize];
                buffer.AsSpan(0, result.Count).CopyTo(controlBuffer.AsSpan(controlLength));
                controlLength += result.Count;
                if (!result.EndOfMessage)
                    continue;

                var message = controlBuffer.AsMemory(0, controlLength);
                controlLength = 0;
                await HandleControlAsync(message, terminal).ConfigureAwait(false);
            }
        }

        private static async ValueTask WriteInputAsync(PtyTerminal terminal, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            try
            {
                await terminal.WriteInputAsync(bytes, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException)
            {
                // Input racing child exit / teardown is benign; the session outcome is decided elsewhere.
            }
        }

        private async ValueTask HandleControlAsync(ReadOnlyMemory<byte> utf8Json, PtyTerminal terminal)
        {
            if (!BridgeJson.TryParse(utf8Json.Span, out var message))
            {
                await ViolateProtocolAsync().ConfigureAwait(false);
                return;
            }

            switch (message!.Type)
            {
                case BridgeJson.TypeResize:
                    if (message.Cols is > 0 && message.Rows is > 0)
                    {
                        try
                        {
                            terminal.Resize(new PtySize(message.Cols.Value, message.Rows.Value));
                        }
                        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
                        {
                            // Resize racing child exit / teardown is benign.
                        }
                    }

                    break;

                case BridgeJson.TypeAck:
                    _flowControl.OnAcknowledged(message.Bytes ?? 0);
                    break;

                default:
                    // Unknown control types are ignored for forward compatibility.
                    break;
            }
        }

        /// <summary>Closes with <c>PolicyViolation</c> and throws; a broken client must not hold a shell open.</summary>
        private async Task ViolateProtocolAsync()
        {
            try
            {
                // CloseOutputAsync is a send-type operation and this runs on the receive loop while
                // the output pump may be mid-SendAsync; a WebSocket allows one outstanding send, so
                // the close frame must take the same send lock (bounded by the close timeout).
                using var closeCts = new CancellationTokenSource(_options.CloseTimeout);
                await _sendLock.WaitAsync(closeCts.Token).ConfigureAwait(false);
                try
                {
                    await _webSocket.CloseOutputAsync(
                        WebSocketCloseStatus.PolicyViolation,
                        "malformed control message",
                        closeCts.Token).ConfigureAwait(false);
                }
                finally
                {
                    _sendLock.Release();
                }
            }
            catch
            {
                // Best-effort close; the violation exception below is the outcome that matters.
            }

            throw new InvalidDataException("Malformed or oversize control message received on the terminal WebSocket.");
        }

        private async Task SendExitAndCloseAsync(PtyExitStatus status, CancellationToken cancellationToken)
        {
            try
            {
                using var closeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                closeCts.CancelAfter(_options.CloseTimeout);

                if (_options.SendExitMessage)
                {
                    var payload = BridgeJson.SerializeExit(status);
                    await _sendLock.WaitAsync(closeCts.Token).ConfigureAwait(false);
                    try
                    {
                        await _webSocket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, closeCts.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        _sendLock.Release();
                    }
                }

                if (_webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await _webSocket.CloseOutputAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "child exited",
                        closeCts.Token).ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or WebSocketException or ObjectDisposedException)
            {
                // Best-effort exit notification against a slow or dead client; the exit status is
                // still returned to the caller.
            }
        }

        /// <summary>Completes the close handshake after a client-initiated close.</summary>
        private async Task RespondToClientCloseAsync()
        {
            if (_webSocket.State != WebSocketState.CloseReceived)
                return;

            try
            {
                using var closeCts = new CancellationTokenSource(_options.CloseTimeout);
                await _webSocket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "session closed",
                    closeCts.Token).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort close response.
            }
        }

        private async Task AwaitReceiveShutdownAsync(Task receiveTask)
        {
            try
            {
                await receiveTask.WaitAsync(_options.CloseTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                AbortWebSocket();
            }
            catch
            {
                // Close-handshake races are benign once the exit path owns the outcome.
            }
        }

        /// <summary>
        /// Breaks a send or receive blocked on transport backpressure. ManagedWebSocket may not
        /// observe cancellation while waiting on the underlying stream.
        /// </summary>
        private void AbortWebSocket()
        {
            try
            {
                if (_webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived or WebSocketState.CloseSent)
                    _webSocket.Abort();
            }
            catch
            {
                // Best-effort abort; teardown continues via canceled tokens and disposal.
            }
        }

        private static Task WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.CompletedTask;

            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), completed);
            return completed.Task;
        }
    }
}
