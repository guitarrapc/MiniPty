using System.Buffers.Text;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using MiniPty.Terminal.Internal;

namespace MiniPty.Terminal;

/// <summary>
/// Owns authenticated, expiring PTY sessions that can detach from and reconnect to WebSockets.
/// </summary>
/// <remarks>
/// A session has one active connection at a time. Unacknowledged output is retained in a fixed
/// ring buffer and addressed by an absolute byte offset. Reconnecting callers provide their last
/// processed offset, preventing both gaps and duplicate terminal output.
/// </remarks>
public sealed class PtyWebSocketSessionManager : IAsyncDisposable
{
    private const int TokenBytes = 32;
    private static readonly byte[] DummyToken = new byte[TokenBytes];

    private readonly PtyWebSocketSessionManagerOptions _options;
    private readonly Dictionary<Guid, ManagedSession> _sessions = [];
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _expirationCts = new();
    private readonly Task _expirationTask;
    private bool _disposed;

    /// <summary>Creates a persistent-session manager.</summary>
    /// <param name="options">Session limits and bridge behavior, or null for defaults.</param>
    public PtyWebSocketSessionManager(PtyWebSocketSessionManagerOptions? options = null)
    {
        _options = options ?? new PtyWebSocketSessionManagerOptions();
        _options.Validate();
        _expirationTask = ExpireSessionsAsync(_expirationCts.Token);
    }

    /// <summary>Gets the number of live sessions currently owned by the manager.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
                return _sessions.Count;
        }
    }

    /// <summary>
    /// Spawns a persistent PTY session and returns the credentials needed to connect to it.
    /// </summary>
    /// <param name="startInfo">Child process launch configuration.</param>
    /// <returns>Opaque session id and bearer token. The token is returned only once.</returns>
    /// <exception cref="InvalidOperationException">The manager reached <see cref="PtyWebSocketSessionManagerOptions.MaxSessions"/>.</exception>
    public PtyBridgeSessionCredentials CreateSession(PtyStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        lock (_lock)
        {
            ThrowIfDisposed();
            if (_sessions.Count >= _options.MaxSessions)
                throw new InvalidOperationException("The persistent terminal session limit has been reached.");
        }

        Span<byte> tokenBytes = stackalloc byte[TokenBytes];
        RandomNumberGenerator.Fill(tokenBytes);
        var token = Convert.ToHexString(tokenBytes);
        ManagedSession session;
        try
        {
            session = new ManagedSession(startInfo, tokenBytes, _options);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tokenBytes);
        }
        Guid sessionId;
        var rejection = 0;
        lock (_lock)
        {
            if (_disposed)
            {
                rejection = 1;
            }
            else if (_sessions.Count >= _options.MaxSessions)
            {
                rejection = 2;
            }
            else
            {
                do
                {
                    sessionId = Guid.NewGuid();
                }
                while (_sessions.ContainsKey(sessionId));

                _sessions.Add(sessionId, session);
                return new PtyBridgeSessionCredentials(sessionId, token);
            }
        }

        session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (rejection == 1)
            throw new ObjectDisposedException(nameof(PtyWebSocketSessionManager));
        throw new InvalidOperationException("The persistent terminal session limit has been reached.");
    }

    /// <summary>
    /// Authenticates and attaches one WebSocket to a persistent session.
    /// </summary>
    /// <param name="sessionId">Session id returned by <see cref="CreateSession"/>.</param>
    /// <param name="authenticationToken">Secret token returned by <see cref="CreateSession"/>.</param>
    /// <param name="acknowledgedOffset">
    /// Absolute output byte offset already processed by the frontend. Use zero for the first
    /// connection and persist the latest acknowledged offset across reconnects.
    /// </param>
    /// <param name="webSocket">Open server-side WebSocket.</param>
    /// <param name="cancellationToken">Stops this connection without killing the persistent session.</param>
    /// <returns>
    /// The child exit status when the child exits and buffered output is sent; null when the
    /// client detaches while the child remains available for reconnect.
    /// </returns>
    /// <exception cref="UnauthorizedAccessException">The session id or token is invalid or expired.</exception>
    /// <exception cref="InvalidOperationException">Another WebSocket is already connected.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The acknowledged offset is outside the retained replay range.</exception>
    public async Task<PtyExitStatus?> ConnectAsync(
        Guid sessionId,
        string authenticationToken,
        long acknowledgedOffset,
        WebSocket webSocket,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authenticationToken);
        ArgumentNullException.ThrowIfNull(webSocket);
        if (webSocket.State != WebSocketState.Open)
            throw new ArgumentException("The WebSocket must be open.", nameof(webSocket));

        ManagedSession session;
        SessionConnection connection;
        lock (_lock)
        {
            ThrowIfDisposed();
            session = GetAuthenticatedSession(sessionId, authenticationToken);
            connection = new SessionConnection(session, webSocket, _options);
            try
            {
                session.Attach(connection, acknowledgedOffset);
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        PtyExitStatus? result;
        try
        {
            result = await connection.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            session.Detach(connection);
            connection.Dispose();
        }

        if (result is not null)
        {
            lock (_lock)
            {
                if (_sessions.TryGetValue(sessionId, out var current) && ReferenceEquals(current, session))
                    _sessions.Remove(sessionId);
            }

            await session.DisposeAsync().ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>Authenticates, kills, and removes a persistent session.</summary>
    /// <param name="sessionId">Session id returned by <see cref="CreateSession"/>.</param>
    /// <param name="authenticationToken">Secret session token.</param>
    /// <returns>A task that completes after PTY resources are released.</returns>
    /// <exception cref="UnauthorizedAccessException">The session id or token is invalid or expired.</exception>
    public async ValueTask TerminateAsync(Guid sessionId, string authenticationToken)
    {
        ArgumentNullException.ThrowIfNull(authenticationToken);
        ManagedSession session;
        lock (_lock)
        {
            ThrowIfDisposed();
            session = GetAuthenticatedSession(sessionId, authenticationToken);
            _sessions.Remove(sessionId);
        }

        await session.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Kills all sessions and stops expiration processing.</summary>
    public async ValueTask DisposeAsync()
    {
        List<ManagedSession> sessions;
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            sessions = [.. _sessions.Values];
            _sessions.Clear();
        }

        _expirationCts.Cancel();
        try
        {
            await _expirationTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_expirationCts.IsCancellationRequested)
        {
        }

        foreach (var session in sessions)
            await session.DisposeAsync().ConfigureAwait(false);

        _expirationCts.Dispose();
    }

    private ManagedSession GetAuthenticatedSession(Guid sessionId, string authenticationToken)
    {
        _sessions.TryGetValue(sessionId, out var session);
        Span<byte> candidate = stackalloc byte[TokenBytes];
        var decoded = TryDecodeToken(authenticationToken, candidate);
        if (!decoded)
            candidate.Clear();

        var expected = session?.AuthenticationToken ?? DummyToken;
        var authenticated = CryptographicOperations.FixedTimeEquals(candidate, expected);
        if (session is null || !decoded || !authenticated)
            throw new UnauthorizedAccessException("The persistent terminal session credentials are invalid or expired.");

        return session!;
    }

    private static bool TryDecodeToken(ReadOnlySpan<char> token, Span<byte> destination)
    {
        if (token.Length != destination.Length * 2)
        {
            destination.Clear();
            return false;
        }

        var valid = true;
        for (var i = 0; i < destination.Length; i++)
        {
            var high = HexValue(token[i * 2]);
            var low = HexValue(token[i * 2 + 1]);
            valid &= high >= 0 && low >= 0;
            destination[i] = (byte)((Math.Max(high, 0) << 4) | Math.Max(low, 0));
        }

        return valid;
    }

    private static int HexValue(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'A' and <= 'F' => value - 'A' + 10,
        >= 'a' and <= 'f' => value - 'a' + 10,
        _ => -1,
    };

    private async Task ExpireSessionsAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.ExpirationScanInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            List<KeyValuePair<Guid, ManagedSession>>? expired = null;
            var now = DateTimeOffset.UtcNow;
            lock (_lock)
            {
                foreach (var pair in _sessions)
                {
                    if (!pair.Value.IsExpired(now, _options.DetachedSessionTimeout))
                        continue;
                    expired ??= [];
                    expired.Add(pair);
                }

                if (expired is not null)
                {
                    foreach (var pair in expired)
                        _sessions.Remove(pair.Key);
                }
            }

            if (expired is not null)
            {
                foreach (var pair in expired)
                    await pair.Value.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class ManagedSession : IAsyncDisposable
    {
        private readonly Lock _lock = new();
        private readonly PersistentOutputBuffer _output;
        private readonly PtyTerminal _terminal;
        private readonly Task _completionObserver;
        private SessionConnection? _connection;
        private DateTimeOffset _detachedAt = DateTimeOffset.UtcNow;
        private int _disposed;

        public ManagedSession(
            PtyStartInfo startInfo,
            ReadOnlySpan<byte> authenticationToken,
            PtyWebSocketSessionManagerOptions options)
        {
            AuthenticationToken = authenticationToken.ToArray();
            _output = new PersistentOutputBuffer(options.ReplayBufferSize);
            _terminal = PtyTerminal.Start(startInfo, new PtyTerminalOptions { Output = BufferOutputAsync });
            _completionObserver = ObserveCompletionAsync();
        }

        public byte[] AuthenticationToken { get; }
        public PersistentOutputBuffer Output => _output;
        public PtyTerminal Terminal => _terminal;

        public void Attach(SessionConnection connection, long acknowledgedOffset)
        {
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                if (_connection is not null)
                    throw new InvalidOperationException("The persistent terminal session already has an active connection.");
                _output.ResumeFrom(acknowledgedOffset);
                _connection = connection;
            }
        }

        public void Detach(SessionConnection connection)
        {
            lock (_lock)
            {
                if (!ReferenceEquals(_connection, connection))
                    return;
                _connection = null;
                _detachedAt = DateTimeOffset.UtcNow;
            }
        }

        public bool IsExpired(DateTimeOffset now, TimeSpan timeout)
        {
            lock (_lock)
                return _connection is null && now - _detachedAt >= timeout;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            SessionConnection? connection;
            lock (_lock)
            {
                connection = _connection;
                _connection = null;
            }

            connection?.Abort();
            // Force child exit and make a producer blocked on replay capacity unwind. Await the
            // pump itself before PtyTerminal disposes the core session/output buffer; canceling
            // and disposing an active ReadOutputAsync in parallel can double-signal its waiter.
            try
            {
                _terminal.Kill(PtySignal.Kill);
            }
            catch (ObjectDisposedException)
            {
            }
            _output.Dispose();
            try
            {
                await _terminal.Completion.ConfigureAwait(false);
            }
            catch
            {
            }
            await _terminal.DisposeAsync().ConfigureAwait(false);
            try
            {
                await _completionObserver.ConfigureAwait(false);
            }
            catch
            {
            }
            CryptographicOperations.ZeroMemory(AuthenticationToken);
        }

        private ValueTask BufferOutputAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
            _output.WriteAsync(data, cancellationToken);

        private async Task ObserveCompletionAsync()
        {
            try
            {
                _output.Complete(await _terminal.Completion.ConfigureAwait(false));
            }
            catch (Exception exception)
            {
                _output.Fault(exception);
            }
        }
    }

    private sealed class SessionConnection : IDisposable
    {
        private static ReadOnlySpan<byte> OutputPrefix => "{\"type\":\"output\",\"offset\":"u8;
        private static ReadOnlySpan<byte> OutputBytes => ",\"bytes\":"u8;

        private readonly ManagedSession _session;
        private readonly WebSocket _webSocket;
        private readonly PtyWebSocketSessionManagerOptions _options;
        private readonly CancellationTokenSource _abortCts = new();
        private readonly byte[] _outputHeader = new byte[96];
        private readonly byte[] _sendBuffer;
        private long _sentOffset;

        public SessionConnection(
            ManagedSession session,
            WebSocket webSocket,
            PtyWebSocketSessionManagerOptions options)
        {
            _session = session;
            _webSocket = webSocket;
            _options = options;
            _sendBuffer = new byte[options.MaxOutputFrameSize];
            _sentOffset = session.Output.OffsetRange.Start;
        }

        public async Task<PtyExitStatus?> RunAsync(CancellationToken cancellationToken)
        {
            using var lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _abortCts.Token);
            var token = lifetimeCts.Token;
            var sendTask = SendLoopAsync(token);
            var receiveTask = ReceiveLoopAsync(token);
            var first = await Task.WhenAny(sendTask, receiveTask).ConfigureAwait(false);

            if (first == sendTask)
            {
                try
                {
                    var status = await sendTask.ConfigureAwait(false);
                    await SendExitAndCloseAsync(status, cancellationToken).ConfigureAwait(false);
                    lifetimeCts.Cancel();
                    await IgnoreConnectionEndAsync(receiveTask).ConfigureAwait(false);
                    return status;
                }
                catch
                {
                    lifetimeCts.Cancel();
                    await IgnoreConnectionEndAsync(receiveTask).ConfigureAwait(false);
                    throw;
                }
            }

            lifetimeCts.Cancel();
            try
            {
                await receiveTask.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await IgnoreConnectionEndAsync(sendTask).ConfigureAwait(false);
                if (exception is InvalidDataException)
                    await ClosePolicyViolationAsync().ConfigureAwait(false);
                throw;
            }
            await IgnoreConnectionEndAsync(sendTask).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            await RespondToClientCloseAsync().ConfigureAwait(false);
            return null;
        }

        public void Abort()
        {
            _abortCts.Cancel();
            try
            {
                _webSocket.Abort();
            }
            catch
            {
            }
        }

        public void Dispose() => _abortCts.Dispose();

        private async Task<PtyExitStatus> SendLoopAsync(CancellationToken cancellationToken)
        {
            var cursor = _session.Output.OffsetRange.Start;
            while (true)
            {
                var read = await _session.Output.ReadAsync(
                    cursor,
                    _options.MaxOutputFrameSize,
                    cancellationToken).ConfigureAwait(false);
                if (read.Completion is { } status)
                    return status;

                var headerLength = FormatOutputHeader(cursor, read.Data.Length);
                await _webSocket.SendAsync(
                    _outputHeader.AsMemory(0, headerLength),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellationToken).ConfigureAwait(false);
                read.Data.CopyTo(_sendBuffer);
                cursor += read.Data.Length;
                Interlocked.Exchange(ref _sentOffset, cursor);
                await _webSocket.SendAsync(
                    _sendBuffer.AsMemory(0, read.Data.Length),
                    WebSocketMessageType.Binary,
                    endOfMessage: true,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[_options.BridgeOptions.ReceiveBufferSize];
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
                    return;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                    return;
                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    if (result.Count > 0)
                        await WriteInputAsync(buffer.AsMemory(0, result.Count), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (controlLength + result.Count > _options.BridgeOptions.MaxControlMessageSize)
                    throw new InvalidDataException("The persistent bridge control message is too large.");
                if (result.EndOfMessage && controlLength == 0)
                {
                    HandleControl(buffer.AsMemory(0, result.Count));
                    continue;
                }

                controlBuffer ??= new byte[_options.BridgeOptions.MaxControlMessageSize];
                buffer.AsSpan(0, result.Count).CopyTo(controlBuffer.AsSpan(controlLength));
                controlLength += result.Count;
                if (!result.EndOfMessage)
                    continue;
                HandleControl(controlBuffer.AsMemory(0, controlLength));
                controlLength = 0;
            }
        }

        private void HandleControl(ReadOnlyMemory<byte> utf8Json)
        {
            if (!BridgeJson.TryParse(utf8Json.Span, out var message))
                throw new InvalidDataException("The persistent bridge received malformed control JSON.");

            switch (message!.Type)
            {
                case BridgeJson.TypeAck:
                    if (message.Offset is not { } offset
                        || !_session.Output.TryAcknowledge(offset, Interlocked.Read(ref _sentOffset)))
                    {
                        throw new InvalidDataException("The persistent bridge received an invalid acknowledgement offset.");
                    }
                    break;
                case BridgeJson.TypeResize when message.Cols is > 0 && message.Rows is > 0:
                    PtyPixelSize? pixelSize = message.PixelWidth is >= 0 && message.PixelHeight is >= 0
                        ? new PtyPixelSize(message.PixelWidth.Value, message.PixelHeight.Value)
                        : null;
                    _session.Terminal.Resize(new PtySize(message.Cols.Value, message.Rows.Value), pixelSize);
                    break;
                default:
                    break;
            }
        }

        private async ValueTask WriteInputAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken)
        {
            try
            {
                await _session.Terminal.WriteInputAsync(input, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
            {
            }
        }

        private int FormatOutputHeader(long offset, int bytes)
        {
            var destination = _outputHeader.AsSpan();
            var written = 0;
            OutputPrefix.CopyTo(destination);
            written += OutputPrefix.Length;
            if (!Utf8Formatter.TryFormat(offset, destination[written..], out var offsetLength))
                throw new InvalidOperationException("The output offset did not fit the protocol header.");
            written += offsetLength;
            OutputBytes.CopyTo(destination[written..]);
            written += OutputBytes.Length;
            if (!Utf8Formatter.TryFormat(bytes, destination[written..], out var bytesLength))
                throw new InvalidOperationException("The output length did not fit the protocol header.");
            written += bytesLength;
            destination[written++] = (byte)'}';
            return written;
        }

        private async Task SendExitAndCloseAsync(PtyExitStatus status, CancellationToken cancellationToken)
        {
            try
            {
                using var closeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                closeCts.CancelAfter(_options.BridgeOptions.CloseTimeout);
                if (_options.BridgeOptions.SendExitMessage)
                {
                    await _webSocket.SendAsync(
                        BridgeJson.SerializeExit(status),
                        WebSocketMessageType.Text,
                        endOfMessage: true,
                        closeCts.Token).ConfigureAwait(false);
                }
                if (_webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await _webSocket.CloseOutputAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "child exited",
                        closeCts.Token).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or ObjectDisposedException)
            {
            }
        }

        private async Task ClosePolicyViolationAsync()
        {
            try
            {
                using var cts = new CancellationTokenSource(_options.BridgeOptions.CloseTimeout);
                await _webSocket.CloseOutputAsync(
                    WebSocketCloseStatus.PolicyViolation,
                    "invalid persistent bridge control message",
                    cts.Token).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        private async Task RespondToClientCloseAsync()
        {
            if (_webSocket.State != WebSocketState.CloseReceived)
                return;
            try
            {
                using var cts = new CancellationTokenSource(_options.BridgeOptions.CloseTimeout);
                await _webSocket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "session detached",
                    cts.Token).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        private async Task IgnoreConnectionEndAsync(Task task)
        {
            try
            {
                await task.WaitAsync(_options.BridgeOptions.CloseTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                try
                {
                    _webSocket.Abort();
                }
                catch
                {
                }

                try
                {
                    await task.WaitAsync(_options.BridgeOptions.CloseTimeout).ConfigureAwait(false);
                }
                catch
                {
                }
            }
            catch
            {
            }
        }
    }
}
