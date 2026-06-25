using System.Runtime.CompilerServices;
using System.Buffers;
using System.Text;
using MiniPty.Internal;

namespace MiniPty;

/// <summary>
/// A running pseudo-terminal session wrapping a child process.
/// </summary>
/// <remarks>
/// <para>
/// PTY output is a byte stream with terminal semantics. If the child writes output and nothing reads
/// <see cref="Output"/>, the child may block when the terminal buffer fills. Use <see cref="CompleteAsync"/>,
/// continuously read <see cref="Output"/>, or the <c>MiniPty.Capture</c> package for timestamped observation.
/// </para>
/// <para>
/// Disposing or awaiting <see cref="DisposeAsync"/> kills the child if it is still running, then releases handles.
/// </para>
/// </remarks>
public sealed class PtySession : IAsyncDisposable, IDisposable
{
    internal const int OutputConsumerNone = 0;
    internal const int OutputConsumerReadOutputAsync = 1;
    internal const int OutputConsumerRawOutput = 2;
    internal const int OutputConsumerComplete = 3;
    internal const string OutputConsumerConflictMessage = "Only one PTY output reader can be active at a time.";

    private static readonly PtyCompleteOptions DefaultCompleteOptions = new();
    private const int OutputBufferCapacity = 32 * 1024;
    private const int OutputStreamChunkSize = 16 * 1024;

    private readonly IPtyBackend _backend;
    private readonly Stream outputTransport;
    private readonly Lock outputReaderLock = new();
    private BoundedOutputBuffer? _outputBuffer;
    private int _outputReaderActive;
    private bool _disposed;

    internal PtySession(IPtyBackend backend)
    {
        _backend = backend;
        outputTransport = backend.Output;
        BindOutputGate(outputTransport);
    }

    /// <summary>
    /// Gets the operating-system process identifier of the child.
    /// </summary>
    public int ProcessId => _backend.ProcessId;

    /// <summary>
    /// Gets the current terminal dimensions in character cells.
    /// </summary>
    public PtySize Size => _backend.Size;

    /// <summary>
    /// Gets the write-only stream connected to the child's standard input (PTY master input).
    /// </summary>
    /// <remarks>Writes are raw bytes; no line-ending translation is performed.</remarks>
    public Stream Input => _backend.Input;

    /// <summary>
    /// Gets the read-only stream of merged stdout and stderr from the child (PTY master output).
    /// </summary>
    /// <remarks>Reads are raw bytes; decode with <see cref="PtyCompleteOptions.OutputEncoding"/> or your own decoder.</remarks>
    public Stream Output => outputTransport;

    internal Stream OutputTransport => outputTransport;

    /// <summary>
    /// Reads persistent PTY output as raw byte chunks.
    /// </summary>
    /// <param name="cancellationToken">
    /// Token used to stop this output reader. Canceling the reader does not kill the child process.
    /// </param>
    /// <returns>An async sequence of raw output chunks.</returns>
    /// <remarks>
    /// <para>Only one active output reader is allowed. Concurrent readers throw <see cref="InvalidOperationException"/>.</para>
    /// <para>
    /// Each chunk's memory is valid only until the next successful <c>MoveNextAsync</c> call on the same enumeration.
    /// Copy the bytes if they must be retained.
    /// </para>
    /// <para>Normal PTY EOF completes the sequence after all available output is drained.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">Another output reader is already active.</exception>
    /// <exception cref="ObjectDisposedException">The session was disposed while reading.</exception>
    /// <exception cref="IOException">The PTY output transport failed unexpectedly.</exception>
    /// <exception cref="OperationCanceledException">The reader was canceled.</exception>
    public async IAsyncEnumerable<PtyOutputChunk> ReadOutputAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (Interlocked.CompareExchange(ref _outputReaderActive, OutputConsumerReadOutputAsync, OutputConsumerNone) != OutputConsumerNone)
            throw new InvalidOperationException(OutputConsumerConflictMessage);

        BoundedOutputBuffer? outputBuffer = null;
        var pendingAdvance = 0;
        try
        {
            outputBuffer = GetOrCreateOutputBuffer();
            while (true)
            {
                if (pendingAdvance > 0)
                {
                    outputBuffer.Advance(pendingAdvance);
                    pendingAdvance = 0;
                }

                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfDisposed();

                var chunk = await outputBuffer.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (chunk.IsEmpty)
                    yield break;

                ThrowIfDisposed();
                pendingAdvance = chunk.Length;
                yield return new PtyOutputChunk(chunk);
            }
        }
        finally
        {
            if (pendingAdvance > 0)
                outputBuffer?.Advance(pendingAdvance);

            Volatile.Write(ref _outputReaderActive, OutputConsumerNone);
        }
    }

    /// <summary>
    /// Gets a value indicating whether the child process has exited.
    /// </summary>
    /// <value>
    /// <see langword="true"/> after the OS reports exit; on Unix the zombie is reaped on the first successful poll.
    /// </value>
    public bool HasExited => _backend.HasExited;

    /// <summary>
    /// Gets the child exit code when <see cref="HasExited"/> is <see langword="true"/>; otherwise <see langword="null"/>.
    /// </summary>
    public int? ExitCode => _backend.HasExited ? _backend.ExitCode : null;

    /// <summary>
    /// Writes raw bytes to the PTY stdin.
    /// </summary>
    /// <param name="bytes">Bytes to write. Empty buffers are ignored.</param>
    /// <param name="cancellationToken">Token used to cancel the write operation.</param>
    /// <returns>A task that completes when the bytes are written.</returns>
    public async ValueTask WriteInputAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!bytes.IsEmpty)
            await Input.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes text to the PTY stdin using the specified character encoding.
    /// </summary>
    /// <param name="text">Text to encode and write. Empty strings are ignored.</param>
    /// <param name="encoding">
    /// Encoding used to convert <paramref name="text"/> to bytes. Default is <see cref="Encoding.UTF8"/>.
    /// </param>
    /// <param name="cancellationToken">Token used to cancel the write operation.</param>
    /// <returns>A task that completes when the text is written.</returns>
    public ValueTask WriteInputAsync(
        string text,
        Encoding? encoding = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return new(PtyIo.WriteTextAsync(Input, text, encoding ?? Encoding.UTF8, cancellationToken));
    }

    /// <summary>
    /// Signals end of stdin to the child.
    /// </summary>
    /// <remarks>
    /// <para>Windows: after bytes were written, writes Ctrl+Z + CR to the ConPTY input stream and leaves the pipe open until the child exits; with no prior bytes, closes the input pipe (deferred when staged).</para>
    /// <para>Unix: writes EOT (<c>0x04</c>, Ctrl-D) to the PTY master; does not close the master fd.</para>
    /// </remarks>
    public void SendEof()
    {
        ThrowIfDisposed();
        _backend.SendEof();
    }

    /// <summary>
    /// Resizes the pseudo-terminal to the given dimensions.
    /// </summary>
    /// <param name="size">New width and height in character cells.</param>
    /// <exception cref="InvalidOperationException">The PTY transport has already been closed.</exception>
    /// <exception cref="System.ComponentModel.Win32Exception">Windows: <c>ResizePseudoConsole</c> failed.</exception>
    /// <exception cref="IOException">Unix: <c>TIOCSWINSZ</c> failed.</exception>
    public void Resize(PtySize size)
    {
        ThrowIfDisposed();
        _backend.Resize(size.Columns, size.Rows);
    }

    /// <summary>
    /// Terminates the child process without releasing PTY handles.
    /// </summary>
    /// <remarks>
    /// Uses <c>TerminateProcess</c> on Windows and <c>SIGKILL</c> on Unix.
    /// Call <see cref="Dispose"/> afterward to release resources.
    /// </remarks>
    public void Kill()
    {
        ThrowIfDisposed();
        _backend.Kill();
    }

    /// <summary>
    /// Asynchronously waits until the child process exits.
    /// </summary>
    /// <param name="cancellationToken">
    /// When canceled, waiting stops and <see cref="OperationCanceledException"/> is thrown.
    /// The child process continues running.
    /// </param>
    /// <returns>A task that completes with the child exit code.</returns>
    /// <exception cref="OperationCanceledException">Waiting was canceled.</exception>
    public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return WaitForExitInternalAsync(cancellationToken, killOnCancellation: false);
    }

    /// <summary>
    /// Pumps and drains the PTY output stream, optionally writes stdin, waits for exit, and returns captured bytes.
    /// </summary>
    /// <param name="options">Completion behavior, or <see langword="null"/> for defaults.</param>
    /// <param name="cancellationToken">
    /// When canceled, behavior depends on <see cref="PtyCompleteOptions.KillOnCancellation"/> (default: kill child).
    /// </param>
    /// <returns>A <see cref="PtyResult"/> with merged <see cref="PtyResult.Output"/> bytes and exit code.</returns>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    /// <exception cref="TimeoutException">
    /// <see cref="PtyCompleteOptions.ExitTimeout"/> or output drain timeout was exceeded.
    /// </exception>
    public async Task<PtyResult> CompleteAsync(
        PtyCompleteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= DefaultCompleteOptions;
        ThrowIfDisposed();
        if (Interlocked.CompareExchange(ref _outputReaderActive, OutputConsumerComplete, OutputConsumerNone) != OutputConsumerNone)
            throw new InvalidOperationException(OutputConsumerConflictMessage);

        try
        {
            var encoding = options.OutputEncoding;
            var (output, exitCode) = await PtyCompletion.RunAsync(
                this,
                options,
                (stream, ct) => PtyBytePump.ReadAllAsync(stream, encoding, options.DecodeOutput, ct),
                cancellationToken).ConfigureAwait(false);

            return new PtyResult(output.ToPayload(), exitCode);
        }
        finally
        {
            Volatile.Write(ref _outputReaderActive, OutputConsumerNone);
        }
    }

    /// <summary>
    /// Synchronously releases PTY and process handles, killing the child if it is still running.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Volatile.Write(ref _outputReaderActive, OutputConsumerNone);
        _outputBuffer?.Dispose();
        _backend.Dispose();
    }

    /// <summary>
    /// Asynchronously releases PTY and process handles, killing the child if it is still running.
    /// </summary>
    /// <returns>A completed value task; disposal is synchronous under the hood.</returns>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    internal Task<int> WaitForExitInternalAsync(CancellationToken cancellationToken, bool killOnCancellation) =>
        _backend.WaitForExitAsync(cancellationToken, killOnCancellation);

    internal void CloseOutputTransport() => _backend.CloseOutputTransport();

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PtySession));
    }

    private void ThrowIfOutputConsumerActive()
    {
        if (Volatile.Read(ref _outputReaderActive) != OutputConsumerNone)
            throw new InvalidOperationException(OutputConsumerConflictMessage);
    }

    internal void EnsureRawOutputReadAllowed() => ThrowIfDisposed();

    internal void BeforeRawOutputRead(ref int rawHoldActive)
    {
        EnsureRawOutputReadAllowed();
        if (Volatile.Read(ref rawHoldActive) != 0)
            return;

        AcquireRawOutputConsumer();
        Volatile.Write(ref rawHoldActive, 1);
    }

    internal void AfterRawOutputRead(ref int rawHoldActive)
    {
        if (Interlocked.Exchange(ref rawHoldActive, 0) != 0)
            ReleaseRawOutputConsumer();
    }

    internal void AcquireRawOutputConsumer()
    {
        ThrowIfDisposed();
        if (Interlocked.CompareExchange(ref _outputReaderActive, OutputConsumerRawOutput, OutputConsumerNone) != OutputConsumerNone)
            throw new InvalidOperationException(OutputConsumerConflictMessage);
    }

    internal void ReleaseRawOutputConsumer() =>
        Interlocked.CompareExchange(ref _outputReaderActive, OutputConsumerNone, OutputConsumerRawOutput);

    internal int ReadOutputTransport(Span<byte> buffer) =>
        outputTransport switch
        {
            PtyHandleReadStream windowsOutput => windowsOutput.ReadTransport(buffer),
            PtyFdReadStream unixOutput => unixOutput.ReadTransport(buffer),
            _ => throw new InvalidOperationException("Unsupported PTY output transport.")
        };

    private void BindOutputGate(Stream output)
    {
        switch (output)
        {
            case PtyHandleReadStream windowsOutput:
                windowsOutput.BindOutputGate(this);
                break;
            case PtyFdReadStream unixOutput:
                unixOutput.BindOutputGate(this);
                break;
        }
    }

    private BoundedOutputBuffer GetOrCreateOutputBuffer()
    {
        lock (outputReaderLock)
            return _outputBuffer ??= new BoundedOutputBuffer(this);
    }

    private sealed class BoundedOutputBuffer : IDisposable
    {
        private readonly PtySession _session;
        private readonly Lock _gate = new();
        private readonly byte[] _buffer;
        private readonly CancellationTokenSource _producerCancellation = new();
        private readonly Task _producer;
        private bool _bufferReturned;
        private int _readOffset;
        private int _writeOffset;
        private int _count;
        private bool _completed;
        private bool _disposed;
        private Exception? _error;
        private TaskCompletionSource? _dataAvailable;
        private TaskCompletionSource? _spaceAvailable;

        internal BoundedOutputBuffer(PtySession session)
        {
            _session = session;
            _buffer = ArrayPool<byte>.Shared.Rent(OutputBufferCapacity);
            _producer = ProduceAsync();
        }

        internal async ValueTask<ReadOnlyMemory<byte>> ReadAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                Task? wait;
                lock (_gate)
                {
                    if (_disposed)
                        throw new ObjectDisposedException(nameof(PtySession));

                    if (_count > 0)
                    {
                        var length = Math.Min(_count, OutputBufferCapacity - _readOffset);
                        length = Math.Min(length, OutputStreamChunkSize);
                        return _buffer.AsMemory(_readOffset, length);
                    }

                    if (_error is not null)
                        throw _error;

                    if (_completed)
                        return ReadOnlyMemory<byte>.Empty;

                    wait = (_dataAvailable ??= CreateSignal()).Task;
                }

                await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        internal void Advance(int length)
        {
            if (length <= 0)
                return;

            TaskCompletionSource? signal = null;
            lock (_gate)
            {
                _readOffset = (_readOffset + length) % OutputBufferCapacity;
                _count -= length;
                signal = _spaceAvailable;
                _spaceAvailable = null;
            }

            signal?.TrySetResult();
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;

                _disposed = true;
            }

            _producerCancellation.Cancel();
            SignalAll();
            if (_producer.IsCompleted)
            {
                ReturnBuffer();
                return;
            }

            _ = _producer.ContinueWith(
                static (task, state) => ((BoundedOutputBuffer)state!).ReturnBuffer(),
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private async Task ProduceAsync()
        {
            // ReadOutputTransport is synchronous and can block on an empty pipe. Yield before the
            // first read so ReadOutputAsync returns promptly and the caller can write stdin or start
            // WaitForExitAsync concurrently (PtyReadOutputAsyncSupportsPersistentCommandLoop).
            await Task.Yield();

            Task<int>? exitTask = null;
            try
            {
                exitTask = _session.WaitForExitInternalAsync(CancellationToken.None, killOnCancellation: false);
                using var bytes = PtyReadBuffer.RentBytes(OutputStreamChunkSize);
                while (true)
                {
                    _producerCancellation.Token.ThrowIfCancellationRequested();
                    var read = _session.ReadOutputTransport(bytes.Span);
                    if (read <= 0)
                        break;

                    var consumed = 0;
                    while (consumed < read)
                    {
                        var written = await WriteAsync(bytes.Memory.Slice(consumed, read - consumed), _producerCancellation.Token).ConfigureAwait(false);
                        consumed += written;
                    }
                }

                Complete(null);
            }
            catch (OperationCanceledException) when (_producerCancellation.IsCancellationRequested)
            {
                Complete(null);
            }
            catch (Exception ex) when (_session._disposed && ex is IOException or ObjectDisposedException or System.ComponentModel.Win32Exception)
            {
                Complete(new ObjectDisposedException(nameof(PtySession), ex));
            }
            catch (Exception ex)
            {
                Complete(ex);
            }
            finally
            {
                if (exitTask is { IsCompleted: true, IsFaulted: true })
                    _ = exitTask.Exception;
            }
        }

        private async ValueTask<int> WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
        {
            while (true)
            {
                Task? wait;
                lock (_gate)
                {
                    if (_disposed)
                        throw new ObjectDisposedException(nameof(PtySession));

                    var free = OutputBufferCapacity - _count;
                    if (free > 0)
                    {
                        var length = Math.Min(source.Length, free);
                        length = Math.Min(length, OutputBufferCapacity - _writeOffset);
                        source[..length].CopyTo(_buffer.AsMemory(_writeOffset, length));
                        _writeOffset = (_writeOffset + length) % OutputBufferCapacity;
                        _count += length;
                        var signal = _dataAvailable;
                        _dataAvailable = null;
                        signal?.TrySetResult();
                        return length;
                    }

                    wait = (_spaceAvailable ??= CreateSignal()).Task;
                }

                await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private void Complete(Exception? error)
        {
            lock (_gate)
            {
                _error = error;
                _completed = true;
            }

            SignalAll();
        }

        private void SignalAll()
        {
            TaskCompletionSource? data;
            TaskCompletionSource? space;
            lock (_gate)
            {
                data = _dataAvailable;
                space = _spaceAvailable;
                _dataAvailable = null;
                _spaceAvailable = null;
            }

            data?.TrySetResult();
            space?.TrySetResult();
        }

        private static TaskCompletionSource CreateSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private void ReturnBuffer()
        {
            _producerCancellation.Dispose();
            lock (_gate)
            {
                if (_bufferReturned)
                    return;

                _bufferReturned = true;
            }

            ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
        }
    }
}
