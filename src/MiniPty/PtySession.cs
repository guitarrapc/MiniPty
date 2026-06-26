using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks.Sources;
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

    private readonly IPtyBackend _backend;
    private readonly Stream outputTransport;
    private readonly Lock outputReaderLock = new();
    private BoundedOutputBuffer? _outputBuffer;
    private int _outputReaderActive;
    private int _completionOrchestrationDepth;
    private int _exitWaitDepth;
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

    internal bool IsCompletionOrchestrated => Volatile.Read(ref _completionOrchestrationDepth) > 0;

    internal bool IsExitWaitActive => Volatile.Read(ref _exitWaitDepth) > 0;

    internal ExitWaitScope EnterExitWait() => new(this);

    internal readonly struct ExitWaitScope : IDisposable
    {
        private readonly PtySession _session;

        internal ExitWaitScope(PtySession session)
        {
            _session = session;
            Interlocked.Increment(ref session._exitWaitDepth);
        }

        public void Dispose() => Interlocked.Decrement(ref _session._exitWaitDepth);
    }

    internal CompletionOrchestrationScope EnterCompletionOrchestration() => new(this);

    internal readonly struct CompletionOrchestrationScope : IDisposable
    {
        private readonly PtySession _session;

        internal CompletionOrchestrationScope(PtySession session)
        {
            _session = session;
            Interlocked.Increment(ref session._completionOrchestrationDepth);
        }

        public void Dispose() => Interlocked.Decrement(ref _session._completionOrchestrationDepth);
    }

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
        return WaitForExitInternalScopedAsync(cancellationToken, killOnCancellation: false);
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

    internal Task<int> WaitForExitInternalAsync(CancellationToken cancellationToken, bool killOnCancellation, bool closeTransportOnExit = true) =>
        WaitForExitInternalCoreAsync(cancellationToken, killOnCancellation, closeTransportOnExit);

    private async Task<int> WaitForExitInternalScopedAsync(CancellationToken cancellationToken, bool killOnCancellation)
    {
        using var exitWait = EnterExitWait();
        await Task.Yield();
        return await AwaitExitAsync(_backend.WaitForExitAsync(cancellationToken, killOnCancellation)).ConfigureAwait(false);
    }

    private async Task<int> WaitForExitInternalCoreAsync(CancellationToken cancellationToken, bool killOnCancellation, bool closeTransportOnExit)
    {
        await Task.Yield();
        return await AwaitExitAsync(_backend.WaitForExitAsync(cancellationToken, killOnCancellation, closeTransportOnExit)).ConfigureAwait(false);
    }

    private async Task<int> AwaitExitAsync(Task<int> exitTask)
    {
        var exitCode = await exitTask.ConfigureAwait(false);
        ThrowIfDisposed();
        return exitCode;
    }

    internal void CloseOutputTransport() => _backend.CloseOutputTransport();

    internal void PollForChildExitUntilExited(CancellationToken cancellationToken, bool closeTransportOnExit) =>
        _backend.PollForChildExitUntilExited(cancellationToken, closeTransportOnExit);

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

    internal int TryReadOutputTransportIfReady(Span<byte> buffer, out bool eof) =>
        outputTransport switch
        {
            PtyHandleReadStream windowsOutput => windowsOutput.TryReadTransportIfReady(buffer, out eof),
            PtyFdReadStream unixOutput => unixOutput.TryReadTransportIfReady(buffer, out eof),
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

    private sealed class BoundedOutputBuffer : IDisposable, IValueTaskSource
    {
        private readonly PtySession _session;
        private readonly object _sync = new();
        private readonly CancellationTokenSource _producerCancellation = new();
        private readonly Task _producer;
        private ManualResetValueTaskSourceCore<bool> _dataWaitState;
        private short _dataWaitToken;
        private long _lastProduceProgressTicks;
        private ReadOnlyMemory<byte> _handoff;
        private bool _consumerWaiting;
        private bool _dataWaitArmed;
        private bool _completed;
        private bool _disposed;
        private Exception? _error;

        internal BoundedOutputBuffer(PtySession session)
        {
            _session = session;
            _producer = ProduceAsync();
        }

        internal async ValueTask<ReadOnlyMemory<byte>> ReadAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                lock (_sync)
                {
                    if (_disposed)
                        throw new ObjectDisposedException(nameof(PtySession));

                    if (!_handoff.IsEmpty)
                    {
                        _consumerWaiting = false;
                        return _handoff;
                    }

                    if (_error is not null)
                        throw _error;

                    if (_completed)
                        return ReadOnlyMemory<byte>.Empty;

                    _consumerWaiting = true;
                    _dataWaitState.Reset();
                    _dataWaitToken = _dataWaitState.Version;
                    _dataWaitArmed = true;
                    Monitor.PulseAll(_sync);
                }

                if (!cancellationToken.CanBeCanceled)
                {
                    await new ValueTask(this, _dataWaitToken).ConfigureAwait(false);
                }
                else
                {
                    var registration = cancellationToken.Register(
                        static state => ((BoundedOutputBuffer)state!).CancelDataWait(),
                        this);

                    try
                    {
                        await new ValueTask(this, _dataWaitToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        registration.Dispose();
                    }
                }

                lock (_sync)
                    _dataWaitArmed = false;
            }
        }

        internal void Advance(int length)
        {
            if (length <= 0)
                return;

            lock (_sync)
            {
                if (!_handoff.IsEmpty)
                    _handoff = default;

                Monitor.PulseAll(_sync);
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;

                _disposed = true;
            }

            _producerCancellation.Cancel();
            SignalAll();
            if (_producer.IsCompleted)
            {
                DisposeProducerCancellation();
                return;
            }

            _ = _producer.ContinueWith(
                static (_, state) => ((BoundedOutputBuffer)state!).DisposeProducerCancellation(),
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void DisposeProducerCancellation() => _producerCancellation.Dispose();

        private async Task ProduceAsync()
        {
            // ReadOutputTransport is synchronous and can block on an empty pipe. Yield before the
            // first read so ReadOutputAsync returns promptly and the caller can write stdin or start
            // WaitForExitAsync concurrently (PtyReadOutputAsyncSupportsPersistentCommandLoop).
            await Task.Yield();

            var exitObserveTask = _session.IsCompletionOrchestrated
                ? Task.CompletedTask
                : ObserveExitForOutputDrainAsync();

            void MarkProduceProgress() => _lastProduceProgressTicks = Environment.TickCount64;
            MarkProduceProgress();
            try
            {
                using var bytes = PtyReadBuffer.RentBytes();
                while (true)
                {
                    _producerCancellation.Token.ThrowIfCancellationRequested();
                    if (!WaitUntilReadyToRead(_producerCancellation.Token))
                        break;

                    var offset = 0;
                    var eof = false;
                    while (true)
                    {
                        int read;
                        if (offset == 0)
                        {
                            read = _session.ReadOutputTransport(bytes.Span);
                            if (read <= 0)
                            {
                                eof = true;
                                break;
                            }
                        }
                        else
                        {
                            read = _session.TryReadOutputTransportIfReady(bytes.Span.Slice(offset), out var readEof);
                            if (read <= 0 && !readEof)
                            {
                                var coalesceDeadline = Environment.TickCount64 + 1;
                                while (read <= 0 && Environment.TickCount64 < coalesceDeadline)
                                {
                                    Thread.Sleep(0);
                                    read = _session.TryReadOutputTransportIfReady(bytes.Span.Slice(offset), out readEof);
                                }
                            }

                            if (read <= 0)
                            {
                                eof = readEof;
                                break;
                            }
                        }

                        offset += read;
                        if (offset >= bytes.Span.Length)
                            break;
                    }

                    if (offset > 0)
                    {
                        MarkProduceProgress();
                        Handoff(bytes.Memory.Slice(0, offset), _producerCancellation.Token);
                        MarkProduceProgress();
                    }

                    if (eof)
                        break;
                }

                Complete(null);
            }
            catch (OperationCanceledException) when (_producerCancellation.IsCancellationRequested)
            {
                Complete(null);
            }
            catch (ObjectDisposedException)
            {
                Complete(null);
            }
            catch (Exception ex) when (_session._disposed && ex is System.ComponentModel.Win32Exception)
            {
                Complete(new ObjectDisposedException(nameof(PtySession), ex));
            }
            catch (Exception ex)
            {
                Complete(ex);
            }
            finally
            {
                _producerCancellation.Cancel();
                try
                {
                    await exitObserveTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        /// <summary>
        /// On Windows, ConPTY output may not EOF until the transport is closed after the child exits.
        /// </summary>
        private async Task ObserveExitForOutputDrainAsync()
        {
            // Must not run exit polling on the producer thread; ProduceAsync assigns this Task without awaiting.
            await Task.Yield();

            const int PostExitStallBeforeCloseMs = 100;

            try
            {
                var concurrentExitWait = _session.IsExitWaitActive;
                _session.PollForChildExitUntilExited(_producerCancellation.Token, closeTransportOnExit: false);

                if (concurrentExitWait)
                    return;

                var exitObservedAt = Environment.TickCount64;
                while (!_producerCancellation.IsCancellationRequested)
                {
                    lock (_sync)
                    {
                        if (_completed || _disposed)
                            return;
                    }

                    var now = Environment.TickCount64;
                    if (now - _lastProduceProgressTicks >= PostExitStallBeforeCloseMs
                        && now - exitObservedAt >= PostExitStallBeforeCloseMs)
                    {
                        _session.CloseOutputTransport();
                        return;
                    }

                    var pollDeadline = Environment.TickCount64 + 10;
                    while (Environment.TickCount64 < pollDeadline)
                    {
                        _producerCancellation.Token.ThrowIfCancellationRequested();
                        var remaining = (int)Math.Min(10, pollDeadline - Environment.TickCount64);
                        if (remaining > 0)
                            Thread.Sleep(remaining);
                    }
                }
            }
            catch (OperationCanceledException) when (_producerCancellation.IsCancellationRequested)
            {
            }
        }

        private bool WaitUntilReadyToRead(CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_disposed)
                        throw new ObjectDisposedException(nameof(PtySession));

                    if (_completed && _handoff.IsEmpty)
                        return false;

                    if (_handoff.IsEmpty && _consumerWaiting)
                        return true;

                    Monitor.Wait(_sync);
                }
            }
        }

        private void Handoff(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(PtySession));

                _handoff = data;
                _consumerWaiting = false;
                if (_dataWaitArmed)
                    _dataWaitState.SetResult(true);

                Monitor.PulseAll(_sync);
            }

            WaitForHandoffCleared(cancellationToken);
        }

        private void WaitForHandoffCleared(CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                while (!_handoff.IsEmpty)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_disposed || _completed)
                    {
                        _handoff = default;
                        return;
                    }

                    Monitor.Wait(_sync);
                }
            }
        }

        private void Complete(Exception? error)
        {
            lock (_sync)
            {
                _error = error;
                _completed = true;
            }

            SignalAll();
        }

        private void SignalAll()
        {
            lock (_sync)
            {
                _handoff = default;
                if (_dataWaitArmed)
                    _dataWaitState.SetResult(true);

                _dataWaitArmed = false;
                Monitor.PulseAll(_sync);
            }
        }

        private void CancelDataWait()
        {
            lock (_sync)
            {
                if (_dataWaitArmed)
                    _dataWaitState.SetException(new OperationCanceledException());
            }
        }

        void IValueTaskSource.OnCompleted(
            Action<object?> continuation,
            object? state,
            short token,
            ValueTaskSourceOnCompletedFlags flags) =>
            _dataWaitState.OnCompleted(continuation, state, token, flags);

        ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) =>
            _dataWaitState.GetStatus(token);

        void IValueTaskSource.GetResult(short token) =>
            _dataWaitState.GetResult(token);
    }
}
