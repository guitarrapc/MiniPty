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
/// <see cref="PtySession"/> coordinates the <em>runtime</em> PTY lifecycle only: output consumption,
/// stdin writes, resize, exit waiting, kill, and disposal. Launch options come from
/// <see cref="PtyStartInfo"/>; <see cref="Pty.Start"/> selects the platform backend and constructs this
/// object after spawn. Platform spawn and handle ownership live in <c>WindowsPtyBackend</c> /
/// <c>UnixPtyBackend</c> behind <see cref="IPtyBackend"/>.
/// </para>
/// <para>
/// PTY output is a byte stream with terminal semantics. If the child writes output and nothing reads
/// <see cref="Output"/>, the child may block when the terminal buffer fills. Use <see cref="CompleteAsync"/>,
/// continuously read <see cref="Output"/>, or the <c>MiniPty.Capture</c> package for timestamped observation.
/// </para>
/// <para>
/// Disposing or awaiting <see cref="DisposeAsync"/> kills the child if it is still running, then releases handles.
/// </para>
/// </remarks>
public sealed class PtySession : IAsyncDisposable, IDisposable, IPtyExitListener
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
        backend.SetExitListener(this);
    }

    /// <summary>
    /// Runs on the thread that observed child exit (usually a process-wide watcher), so it only hands the
    /// event to the output buffer, which queues its post-exit drain instead of running it here.
    /// </summary>
    void IPtyExitListener.OnChildExited() => Volatile.Read(ref _outputBuffer)?.OnChildExited();

    /// <summary>
    /// Gets the operating-system process identifier of the child.
    /// </summary>
    public int ProcessId => _backend.ProcessId;

    /// <summary>
    /// Gets the current terminal dimensions in character cells.
    /// </summary>
    public PtySize Size => _backend.Size;

    /// <summary>
    /// Gets the foreground process name associated with the PTY, or <see langword="null"/> when
    /// unavailable. On Unix this is intended for inexpensive editor title polling; Windows
    /// currently returns <see langword="null"/>.
    /// </summary>
    public string? ActiveProcessName => _backend.ActiveProcessName;

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
    /// Gets the exit status (exit code plus Unix termination signal) when <see cref="HasExited"/>
    /// is <see langword="true"/>; otherwise <see langword="null"/>.
    /// </summary>
    /// <value>
    /// <see cref="PtyExitStatus.ExitCode"/> always equals <see cref="ExitCode"/>. The signal is
    /// reported on Unix when the child was terminated by a signal; Windows never reports a signal.
    /// </value>
    public PtyExitStatus? ExitStatus =>
        _backend.HasExited ? new PtyExitStatus(_backend.ExitCode, _backend.ExitSignal) : null;

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
    /// <remarks>
    /// Frontend size changes race child exit, so exit never makes this call fail. While the PTY
    /// transport is open the size is applied, even if the child has already exited. Once the transport
    /// has been closed after exit, the call does nothing and <see cref="Size"/> keeps its last applied
    /// value. Hosts can therefore forward every layout change without checking <see cref="HasExited"/>.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
    /// <exception cref="System.ComponentModel.Win32Exception">Windows: <c>ResizePseudoConsole</c> failed while the child was running.</exception>
    /// <exception cref="IOException">Unix: <c>TIOCSWINSZ</c> failed while the child was running.</exception>
    public void Resize(PtySize size)
        => Resize(size, null);

    /// <summary>
    /// Resizes the pseudo-terminal and optionally reports its pixel dimensions on Unix.
    /// </summary>
    /// <param name="size">New width and height in character cells.</param>
    /// <param name="pixelSize">Optional width and height in pixels. Ignored on Windows.</param>
    /// <remarks>
    /// Arguments are validated before the exit state is considered. See <see cref="Resize(PtySize)"/>
    /// for behavior after the child exits.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">A pixel dimension is negative.</exception>
    /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
    /// <exception cref="System.ComponentModel.Win32Exception">Windows: <c>ResizePseudoConsole</c> failed while the child was running.</exception>
    /// <exception cref="IOException">Unix: <c>TIOCSWINSZ</c> failed while the child was running.</exception>
    public void Resize(PtySize size, PtyPixelSize? pixelSize)
    {
        ThrowIfDisposed();
        if (pixelSize is { Width: < 0 })
            throw new ArgumentOutOfRangeException(nameof(pixelSize), "Pixel width must not be negative.");
        if (pixelSize is { Height: < 0 })
            throw new ArgumentOutOfRangeException(nameof(pixelSize), "Pixel height must not be negative.");

        _backend.Resize(
            size.Columns,
            size.Rows,
            Math.Min(pixelSize?.Width ?? 0, ushort.MaxValue),
            Math.Min(pixelSize?.Height ?? 0, ushort.MaxValue));
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
    /// Sends a signal to the child process without releasing PTY handles.
    /// </summary>
    /// <remarks>
    /// On Unix the mapped native signal is delivered via <c>kill(2)</c>; the child may handle or
    /// ignore catchable signals, so termination is not guaranteed. On Windows the signal is
    /// advisory and the child is terminated unconditionally, matching node-pty semantics.
    /// Call <see cref="Dispose"/> afterward to release resources.
    /// </remarks>
    /// <param name="signal">Signal to deliver.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="signal"/> is not a defined <see cref="PtySignal"/> value.</exception>
    public void Kill(PtySignal signal)
    {
        ThrowIfDisposed();
        _backend.Kill(signal);
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
        return WaitForExitCodeAsync(cancellationToken);
    }

    private async Task<int> WaitForExitCodeAsync(CancellationToken cancellationToken)
    {
        var status = await WaitForExitStatusCoreAsync(cancellationToken).ConfigureAwait(false);
        return status.ExitCode;
    }

    /// <summary>
    /// Asynchronously waits until the child process exits and returns its <see cref="PtyExitStatus"/>.
    /// </summary>
    /// <param name="cancellationToken">
    /// When canceled, waiting stops and <see cref="OperationCanceledException"/> is thrown.
    /// The child process continues running, identical to <see cref="WaitForExitAsync"/>.
    /// </param>
    /// <returns>A task that completes with the child exit code and Unix termination signal.</returns>
    /// <exception cref="OperationCanceledException">Waiting was canceled.</exception>
    public Task<PtyExitStatus> WaitForExitStatusAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return WaitForExitStatusCoreAsync(cancellationToken);
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

    /// <summary>
    /// Public exit wait. Awaits the backend's OS-driven exit task: nothing here polls or blocks, so awaiting
    /// it from a UI <see cref="SynchronizationContext"/> leaves that thread free; only the caller's own
    /// continuation resumes there.
    /// </summary>
    private async Task<PtyExitStatus> WaitForExitStatusCoreAsync(CancellationToken cancellationToken)
    {
        // Counted synchronously, before the first await, so a concurrent output drain sees the waiter.
        Interlocked.Increment(ref _exitWaitDepth);
        try
        {
            var status = await _backend.Exited.WaitAsync(cancellationToken).ConfigureAwait(false);
            _backend.CloseTransportAfterExit();
            ThrowIfDisposed();
            return status;
        }
        finally
        {
            Interlocked.Decrement(ref _exitWaitDepth);
        }
    }

    /// <summary>
    /// Library-internal exit wait that can also close the ConPTY transport once exit is observed.
    /// </summary>
    internal Task<int> WaitForExitInternalAsync(CancellationToken cancellationToken, bool killOnCancellation, bool closeTransportOnExit = true) =>
        WaitForExitCodeCoreAsync(cancellationToken, killOnCancellation, closeTransportOnExit);

    /// <summary>
    /// Exit wait for one-shot completion, which drains the transport itself. Without a cancelable token it is
    /// the backend's allocation-free waiter slot. Each result must be awaited exactly once.
    /// </summary>
    internal ValueTask<int> WaitForExitForCompletionAsync(CancellationToken cancellationToken, bool killOnCancellation)
    {
        // A session disposed before exit faults the wait with ObjectDisposedException.
        if (!cancellationToken.CanBeCanceled)
            return _backend.WaitForExitCodeAsync();

        return new ValueTask<int>(WaitForExitCodeCoreAsync(cancellationToken, killOnCancellation, closeTransportOnExit: false));
    }

    /// <summary>
    /// Internal exit wait with work around it. Never polls or blocks.
    /// </summary>
    private async Task<int> WaitForExitCodeCoreAsync(
        CancellationToken cancellationToken,
        bool killOnCancellation,
        bool closeTransportOnExit)
    {
        try
        {
            var exitCode = cancellationToken.CanBeCanceled
                ? (await _backend.Exited.WaitAsync(cancellationToken).ConfigureAwait(false)).ExitCode
                : await _backend.WaitForExitCodeAsync().ConfigureAwait(false);
            if (closeTransportOnExit)
                _backend.CloseTransportAfterExit();

            ThrowIfDisposed();
            return exitCode;
        }
        catch (OperationCanceledException) when (killOnCancellation && cancellationToken.IsCancellationRequested)
        {
            // Killed here rather than from a token registration: CancellationTokenSource runs callbacks in
            // reverse registration order, so WaitAsync's own callback would resume this method first and its
            // cleanup would unregister a kill callback before it ever ran.
            _backend.Kill();
            throw;
        }
    }

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

    internal int TryReadOutputTransportIfReady(Span<byte> buffer, out bool eof) =>
        outputTransport switch
        {
            PtyHandleReadStream windowsOutput => windowsOutput.TryReadTransportIfReady(buffer, out eof),
            PtyFdReadStream unixOutput => unixOutput.TryReadTransportIfReady(buffer, out eof),
            _ => throw new InvalidOperationException("Unsupported PTY output transport.")
        };

    internal bool TryGetAvailableOutputBytes(out int available) =>
        outputTransport switch
        {
            PtyHandleReadStream windowsOutput => windowsOutput.TryGetAvailableBytes(out available),
            PtyFdReadStream unixOutput => unixOutput.TryGetAvailableBytes(out available),
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
        BoundedOutputBuffer buffer;
        lock (outputReaderLock)
            buffer = _outputBuffer ??= new BoundedOutputBuffer(this);

        // The exit listener may have fired before the buffer was published; repeating it is harmless.
        if (_backend.HasExited)
            buffer.OnChildExited();

        return buffer;
    }

    private sealed class BoundedOutputBuffer : IDisposable, IValueTaskSource, IThreadPoolWorkItem
    {
        /// <summary>ConPTY may deliver the next micro-slice shortly after the previous read; peek is unreliable there.</summary>
        private const int CoalesceMicroWindowMs = 1;

        // Post-exit drain state flags. The drain is queued once, when both the producer has started and
        // child exit has been observed, in either order.
        private const int DrainProducerStarted = 1;
        private const int DrainExitObserved = 2;
        private const int DrainQueued = 4;

        private readonly PtySession _session;
        private int _drainState;
        private bool _concurrentExitWait;
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

            // One-shot completion drives its own drain. Otherwise arm the post-exit drain: it runs once the
            // child exit is observed, holding no thread while the child runs.
            if (!_session.IsCompletionOrchestrated)
            {
                _concurrentExitWait = _session.IsExitWaitActive;
                AdvanceDrainState(DrainProducerStarted);
            }

            void MarkProduceProgress() => _lastProduceProgressTicks = Environment.TickCount64;
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
                                var coalesceDeadline = Environment.TickCount64 + CoalesceMicroWindowMs;
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
            }
        }

        /// <summary>
        /// Called on the thread that observed child exit. Never blocks: the drain, when due, is queued.
        /// </summary>
        internal void OnChildExited() => AdvanceDrainState(DrainExitObserved);

        /// <summary>
        /// Sets <paramref name="flag"/> and queues the post-exit drain exactly once, when both the producer
        /// has started and child exit has been observed. Queuing <see langword="this"/> as the work item keeps
        /// the hand-off allocation-free.
        /// </summary>
        private void AdvanceDrainState(int flag)
        {
            while (true)
            {
                var current = Volatile.Read(ref _drainState);
                var next = current | flag;
                var queue = (next & (DrainProducerStarted | DrainExitObserved)) == (DrainProducerStarted | DrainExitObserved)
                    && (next & DrainQueued) == 0;
                if (queue)
                    next |= DrainQueued;

                if (Interlocked.CompareExchange(ref _drainState, next, current) != current)
                    continue;

                if (queue)
                    ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);

                return;
            }
        }

        void IThreadPoolWorkItem.Execute() => DrainAfterExit();

        /// <summary>
        /// On Windows, ConPTY output may not EOF until the transport is closed after the child exits, and on
        /// Unix a grandchild can keep the slave open. Once output has been quiet long enough after exit, close
        /// the transport so the producer reads EOF. Runs on a pool thread, only after exit.
        /// </summary>
        private void DrainAfterExit()
        {
            const int PostExitStallBeforeCloseMs = 100;

            // A concurrent WaitForExitAsync closes the ConPTY transport itself once exit is observed.
            if (_concurrentExitWait)
                return;

            var exitObservedAt = Environment.TickCount64;
            while (!_producerCancellation.IsCancellationRequested)
            {
                var canCloseAfterQuiet = false;
                lock (_sync)
                {
                    if (_completed || _disposed)
                        return;

                    // A fast child exit can beat the terminal pump's first ReadOutputAsync call.
                    // Closing ConPTY before a consumer attaches would discard output still in the transport.
                    if (_handoff.IsEmpty && _consumerWaiting)
                        canCloseAfterQuiet = true;
                }

                if (canCloseAfterQuiet)
                {
                    var now = Environment.TickCount64;
                    var lastProgress = Math.Max(_lastProduceProgressTicks, exitObservedAt);
                    if (now - lastProgress >= PostExitStallBeforeCloseMs
                        && now - exitObservedAt >= PostExitStallBeforeCloseMs)
                    {
                        // A thread-pool work item must never throw: that would terminate the host process.
                        try
                        {
                            _session.CloseOutputTransport();
                        }
                        catch (Exception)
                        {
                            // Disposal raced the close, or the transport already failed; either way the
                            // producer reaches EOF and disposal finishes the teardown.
                        }

                        return;
                    }
                }

                var pollDeadline = Environment.TickCount64 + 10;
                while (Environment.TickCount64 < pollDeadline)
                {
                    // IsCancellationRequested stays valid after the producer disposes its source; Token does not.
                    if (_producerCancellation.IsCancellationRequested)
                        return;

                    var remaining = (int)Math.Min(10, pollDeadline - Environment.TickCount64);
                    if (remaining > 0)
                        Thread.Sleep(remaining);
                }
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
            bool signal;
            lock (_sync)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(PtySession));

                _handoff = data;
                _consumerWaiting = false;
                signal = TakeDataWaitLocked();
                Monitor.PulseAll(_sync);
            }

            if (signal)
                _dataWaitState.SetResult(true);

            WaitForHandoffCleared(cancellationToken);
        }

        /// <summary>
        /// Claims the right to complete the armed consumer wait. Exactly one caller wins, and it must
        /// complete the wait only after releasing <see cref="_sync"/>.
        /// </summary>
        /// <remarks>
        /// Completing under the lock deadlocks: the consumer's continuation runs inline, and its
        /// <see cref="CancellationTokenRegistration.Dispose"/> blocks until a concurrently running
        /// <see cref="CancelDataWait"/> callback returns, while that callback blocks on the lock.
        /// </remarks>
        private bool TakeDataWaitLocked()
        {
            if (!_dataWaitArmed)
                return false;

            _dataWaitArmed = false;
            return true;
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
            bool signal;
            lock (_sync)
            {
                _handoff = default;
                signal = TakeDataWaitLocked();
                Monitor.PulseAll(_sync);
            }

            if (signal)
                _dataWaitState.SetResult(true);
        }

        private void CancelDataWait()
        {
            bool signal;
            lock (_sync)
                signal = TakeDataWaitLocked();

            if (signal)
                _dataWaitState.SetException(new OperationCanceledException());
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
