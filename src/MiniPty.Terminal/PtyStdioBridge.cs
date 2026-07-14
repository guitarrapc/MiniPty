using System.Buffers.Binary;
using MiniPty.Terminal.Internal;

namespace MiniPty.Terminal;

/// <summary>
/// Bridges a terminal over length-prefixed stdin/stdout-style streams for editor helper processes.
/// </summary>
/// <remarks>
/// Every frame starts with a five-byte header: one <see cref="PtyStdioFrameType"/> byte followed
/// by a little-endian unsigned 32-bit payload length. Output and input payloads are raw PTY bytes;
/// control payloads use the same UTF-8 JSON messages as <see cref="PtyWebSocketBridge"/>.
/// </remarks>
public static class PtyStdioBridge
{
    private const int HeaderLength = 5;

    /// <summary>
    /// Spawns a child and bridges it until the child exits, the input stream ends, or cancellation
    /// is requested. The bridge owns and disposes the terminal.
    /// </summary>
    /// <param name="startInfo">Child process launch configuration.</param>
    /// <param name="input">Framed frontend-to-helper stream, normally standard input.</param>
    /// <param name="output">Framed helper-to-frontend stream, normally standard output.</param>
    /// <param name="options">Bridge flow-control and framing limits, or null for defaults.</param>
    /// <param name="cancellationToken">Token that stops the bridge and child.</param>
    /// <returns>The full MiniPty exit status; the emitted exit control uses node-pty exit shape.</returns>
    public static Task<PtyExitStatus> RunAsync(
        PtyStartInfo startInfo,
        Stream input,
        Stream output,
        PtyBridgeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        if (!input.CanRead)
            throw new ArgumentException("The bridge input stream must be readable.", nameof(input));
        if (!output.CanWrite)
            throw new ArgumentException("The bridge output stream must be writable.", nameof(output));

        var effectiveOptions = options ?? new PtyBridgeOptions();
        effectiveOptions.Validate();
        return new StdioSession(input, output, effectiveOptions).RunAsync(startInfo, cancellationToken);
    }

    private sealed class StdioSession
    {
        private readonly Stream _input;
        private readonly Stream _output;
        private readonly PtyBridgeOptions _options;
        private readonly BridgeFlowControl _flowControl;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly byte[] _writeHeader = new byte[HeaderLength];
        private CancellationToken _teardownToken;
        private volatile bool _discardOutput;

        public StdioSession(Stream input, Stream output, PtyBridgeOptions options)
        {
            _input = input;
            _output = output;
            _options = options;
            _flowControl = new BridgeFlowControl(options.HighWatermark, options.LowWatermark);
        }

        public async Task<PtyExitStatus> RunAsync(PtyStartInfo startInfo, CancellationToken cancellationToken)
        {
            using var teardownCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _teardownToken = teardownCts.Token;
            await using var terminal = PtyTerminal.Start(startInfo, new PtyTerminalOptions { Output = SendOutputAsync });
            _flowControl.Attach(terminal);
            var receiveTask = ReceiveLoopAsync(terminal, teardownCts.Token);

            try
            {
                var cancellationSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = cancellationToken.UnsafeRegister(
                    static state => ((TaskCompletionSource)state!).TrySetResult(),
                    cancellationSignal);
                var first = await Task.WhenAny(terminal.Completion, receiveTask, cancellationSignal.Task).ConfigureAwait(false);

                if (first == terminal.Completion && !cancellationToken.IsCancellationRequested)
                {
                    var status = await terminal.Completion.ConfigureAwait(false);
                    if (_options.SendExitMessage)
                        await WriteFrameAsync(PtyStdioFrameType.Control, BridgeJson.SerializeExit(status), cancellationToken).ConfigureAwait(false);
                    await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    teardownCts.Cancel();
                    return status;
                }

                _discardOutput = true;
                _flowControl.Disable();
                teardownCts.Cancel();
                // Input EOF is bridge teardown, not a user graceful hangup; force-kill so Completion
                // cannot wedge on children that ignore SIGHUP.
                terminal.Kill(PtySignal.Kill);
                cancellationToken.ThrowIfCancellationRequested();
                await receiveTask.ConfigureAwait(false);
                return await terminal.Completion.WaitAsync(_options.CloseTimeout, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _discardOutput = true;
                _flowControl.Disable();
                teardownCts.Cancel();
                try
                {
                    await receiveTask.ConfigureAwait(false);
                }
                catch when (receiveTask.IsCanceled || receiveTask.IsFaulted)
                {
                    // The primary bridge outcome has already been selected.
                }
            }
        }

        private async ValueTask SendOutputAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            if (_discardOutput)
                return;

            await WriteFrameAsync(PtyStdioFrameType.Output, data, _teardownToken).ConfigureAwait(false);
            _flowControl.OnSent(data.Length);
        }

        private async Task ReceiveLoopAsync(PtyTerminal terminal, CancellationToken cancellationToken)
        {
            var header = new byte[HeaderLength];
            var buffer = new byte[_options.ReceiveBufferSize];
            var control = new byte[_options.MaxControlMessageSize];
            while (await TryReadHeaderAsync(header, cancellationToken).ConfigureAwait(false))
            {
                var type = (PtyStdioFrameType)header[0];
                var remaining = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(1));
                if (type == PtyStdioFrameType.Control && remaining > _options.MaxControlMessageSize)
                    throw new InvalidDataException("The stdio control frame exceeds MaxControlMessageSize.");

                if (type == PtyStdioFrameType.Control)
                {
                    await ReadExactlyAsync(control.AsMemory(0, (int)remaining), cancellationToken).ConfigureAwait(false);
                    HandleControl(control.AsMemory(0, (int)remaining), terminal);
                    continue;
                }

                while (remaining > 0)
                {
                    var count = (int)Math.Min(remaining, (uint)buffer.Length);
                    await ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    if (type == PtyStdioFrameType.Input)
                        await WriteInputAsync(terminal, buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    remaining -= (uint)count;
                }
            }
        }

        private void HandleControl(ReadOnlyMemory<byte> utf8Json, PtyTerminal terminal)
        {
            if (!BridgeJson.TryParse(utf8Json.Span, out var message))
                throw new InvalidDataException("The stdio bridge received malformed control JSON.");

            switch (message!.Type)
            {
                case BridgeJson.TypeResize when message.Cols is > 0 && message.Rows is > 0:
                    PtyPixelSize? pixelSize = message.PixelWidth is >= 0 && message.PixelHeight is >= 0
                        ? new PtyPixelSize(message.PixelWidth.Value, message.PixelHeight.Value)
                        : null;
                    terminal.Resize(new PtySize(message.Cols.Value, message.Rows.Value), pixelSize);
                    break;
                case BridgeJson.TypeAck:
                    _flowControl.OnAcknowledged(message.Bytes ?? 0);
                    break;
            }
        }

        private static async ValueTask WriteInputAsync(
            PtyTerminal terminal,
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken)
        {
            try
            {
                await terminal.WriteInputAsync(data, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
            {
                // Input can race normal child exit.
            }
        }

        private async ValueTask WriteFrameAsync(
            PtyStdioFrameType type,
            ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken)
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _writeHeader[0] = (byte)type;
                BinaryPrimitives.WriteUInt32LittleEndian(_writeHeader.AsSpan(1), (uint)payload.Length);
                await _output.WriteAsync(_writeHeader, cancellationToken).ConfigureAwait(false);
                if (!payload.IsEmpty)
                    await _output.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private async ValueTask<bool> TryReadHeaderAsync(byte[] header, CancellationToken cancellationToken)
        {
            var first = await _input.ReadAsync(header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (first == 0)
                return false;

            await ReadExactlyAsync(header.AsMemory(1), cancellationToken).ConfigureAwait(false);
            return true;
        }

        private async ValueTask ReadExactlyAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = await _input.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    throw new EndOfStreamException("The stdio bridge frame ended before its declared payload length.");
                offset += read;
            }
        }
    }
}
