using System.Text;
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
/// PTY output is read from spawn on a dedicated background thread into a small bounded buffer, and exactly one
/// consumer drains it: <see cref="ReadOutputAsync"/>, <see cref="Output"/>, <see cref="CompleteAsync"/>, or the
/// <c>MiniPty.Capture</c> package. When nothing drains it the buffer fills, reading stops, and the child may block
/// on its next write, as it would on a terminal nobody reads.
/// </para>
/// <para>
/// Asynchronous members never block the calling thread and never run library code on the caller's
/// <see cref="SynchronizationContext"/>, so they can be awaited from a UI thread.
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
    private readonly PtyOutputPump _pump;
    private PtyOutputReader? _outputReader;
    private PtyOutputStream? _outputStream;
    private int _outputReaderActive;
    private bool _disposed;

    internal PtySession(IPtyBackend backend)
    {
        _backend = backend;
        _pump = new PtyOutputPump(backend);
        try
        {
            _pump.Start();
        }
        catch
        {
            backend.Dispose();
            throw;
        }

        backend.SetExitListener(this);

        // The child may have exited before the listener was set; repeating the notification is harmless.
        if (backend.HasExited)
            _pump.OnChildExited();
    }

    /// <summary>
    /// Runs on the thread that observed child exit (usually a process-wide watcher), so it only hands the
    /// event to the output pump, which schedules its post-exit drain instead of running it here.
    /// </summary>
    void IPtyExitListener.OnChildExited() => _pump.OnChildExited();

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
    /// <remarks>
    /// <para>Reads are raw bytes; decode with <see cref="PtyCompleteOptions.OutputEncoding"/> or your own decoder.</para>
    /// <para>
    /// <see cref="Stream.ReadAsync(Memory{byte}, CancellationToken)"/> never blocks the calling thread: it completes
    /// at once when output is buffered and otherwise when output arrives. <see cref="Stream.Read(Span{byte})"/>
    /// blocks until output arrives, as the <see cref="Stream"/> contract requires.
    /// </para>
    /// <para>
    /// The first read makes this stream the session's output consumer until a read returns 0 (end of output) or
    /// fails. Meanwhile <see cref="ReadOutputAsync"/> and <see cref="CompleteAsync"/> throw
    /// <see cref="InvalidOperationException"/>.
    /// </para>
    /// </remarks>
    public Stream Output => _outputStream ?? CreateOutputStream();

    internal PtyOutputPump OutputPump => _pump;

    internal bool IsDisposed => Volatile.Read(ref _disposed);

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
    /// Each chunk's memory is valid only until the next successful <c>MoveNextAsync</c> call on the same enumeration,
    /// or until the session is disposed. Copy the bytes if they must be retained.
    /// </para>
    /// <para>
    /// <c>MoveNextAsync</c> completes synchronously while output is buffered and never blocks the calling thread.
    /// When it has to wait, the caller's continuation resumes on its captured context, so <c>await foreach</c> on a
    /// UI thread processes chunks in place on that thread. Enumerating allocates nothing per chunk.
    /// </para>
    /// <para>Normal PTY EOF completes the sequence after all available output is drained.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">Another output reader is already active.</exception>
    /// <exception cref="ObjectDisposedException">The session was disposed while reading.</exception>
    /// <exception cref="IOException">The PTY output transport failed unexpectedly.</exception>
    /// <exception cref="OperationCanceledException">The reader was canceled.</exception>
    public IAsyncEnumerable<PtyOutputChunk> ReadOutputAsync(CancellationToken cancellationToken = default)
    {
        var reader = _outputReader ?? CreateOutputReader();
        return cancellationToken.CanBeCanceled
            ? new PtyOutputReader.WithToken(reader, cancellationToken)
            : reader;
    }

    /// <summary>
    /// Gets a value indicating whether the child process has exited.
    /// </summary>
    /// <value>
    /// <see langword="true"/> after the process-wide exit watcher has observed the exit. Reading it never blocks.
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
    /// <remarks>
    /// Waiting does not affect output: a concurrent output reader keeps receiving output until end of output,
    /// which follows shortly after exit once output has gone quiet.
    /// </remarks>
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
    /// Collects the PTY output, optionally writes stdin, waits for exit, and returns captured bytes.
    /// </summary>
    /// <param name="options">Completion behavior, or <see langword="null"/> for defaults.</param>
    /// <param name="cancellationToken">
    /// When canceled, behavior depends on <see cref="PtyCompleteOptions.KillOnCancellation"/> (default: kill child).
    /// </param>
    /// <returns>A <see cref="PtyResult"/> with merged <see cref="PtyResult.Output"/> bytes and exit code.</returns>
    /// <remarks>
    /// Output is collected on the session's reader thread while the child runs, so awaiting this method never
    /// blocks the calling thread.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    /// <exception cref="TimeoutException">
    /// <see cref="PtyCompleteOptions.ExitTimeout"/> or output drain timeout was exceeded.
    /// </exception>
    public async Task<PtyResult> CompleteAsync(
        PtyCompleteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= DefaultCompleteOptions;
        AcquireOutputConsumer(OutputConsumerComplete);
        try
        {
            var sink = new PtyCompletionSink(options.OutputEncoding, options.DecodeOutput);
            var exitCode = await PtyCompletion.RunAsync(this, options, sink, cancellationToken).ConfigureAwait(false);
            return new PtyResult(sink.ToPayload(), exitCode);
        }
        finally
        {
            ReleaseOutputConsumer(OutputConsumerComplete);
        }
    }

    /// <summary>
    /// Releases PTY and process handles, killing the child if it is still running. Does not block.
    /// </summary>
    /// <remarks>
    /// In-flight output reads and exit waits fail with <see cref="ObjectDisposedException"/>. The background
    /// reader stops on its own shortly afterwards; output chunk memory must not be used after this call. Use
    /// <see cref="DisposeAsync"/> to also wait until the reader has stopped.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
            return;

        Volatile.Write(ref _disposed, true);
        Volatile.Write(ref _outputReaderActive, OutputConsumerNone);

        // Discard mode first: closing the pseudo-console waits for ConPTY to flush, which needs a reader.
        _pump.OnSessionDisposing();
        _backend.Dispose();
        _pump.OnSessionDisposed();
    }

    /// <summary>
    /// Releases PTY and process handles, killing the child if it is still running, and completes once the
    /// background output reader has stopped.
    /// </summary>
    /// <returns>A value task that completes when the reader has released the transport.</returns>
    /// <remarks>Never blocks the calling thread.</remarks>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return default;

        Dispose();
        return _pump.WaitForStopAsync();
    }

    /// <summary>
    /// Public exit wait. Awaits the backend's OS-driven exit task: nothing here polls or blocks, so awaiting
    /// it from a UI <see cref="SynchronizationContext"/> leaves that thread free; only the caller's own
    /// continuation resumes there.
    /// </summary>
    private async Task<PtyExitStatus> WaitForExitStatusCoreAsync(CancellationToken cancellationToken)
    {
        var status = await _backend.Exited.WaitAsync(cancellationToken).ConfigureAwait(false);
        ThrowIfDisposed();
        return status;
    }

    /// <summary>
    /// Exit wait for one-shot completion. Without a cancelable token it is the backend's allocation-free waiter
    /// slot. Each result must be awaited exactly once.
    /// </summary>
    internal ValueTask<int> WaitForExitForCompletionAsync(CancellationToken cancellationToken, bool killOnCancellation)
    {
        // A session disposed before exit faults the wait with ObjectDisposedException.
        if (!cancellationToken.CanBeCanceled)
            return _backend.WaitForExitCodeAsync();

        return new ValueTask<int>(WaitForExitCodeCoreAsync(cancellationToken, killOnCancellation));
    }

    /// <summary>
    /// Cancelable internal exit wait that can kill the child on cancellation. Never polls or blocks.
    /// </summary>
    private async Task<int> WaitForExitCodeCoreAsync(CancellationToken cancellationToken, bool killOnCancellation)
    {
        try
        {
            var exitCode = (await _backend.Exited.WaitAsync(cancellationToken).ConfigureAwait(false)).ExitCode;
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

    /// <summary>Closes the output transport as the post-exit drain does. For tests of the closed-transport state.</summary>
    internal void CloseOutputTransport() => _pump.RequestClose();

    internal void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed))
            throw new ObjectDisposedException(nameof(PtySession));
    }

    /// <summary>Takes the session's single-consumer ownership for <paramref name="consumer"/>.</summary>
    /// <exception cref="ObjectDisposedException">The session was disposed.</exception>
    /// <exception cref="InvalidOperationException">Another consumer is active.</exception>
    internal void AcquireOutputConsumer(int consumer)
    {
        ThrowIfDisposed();
        if (!TryAcquireOutputConsumer(consumer))
            throw new InvalidOperationException(OutputConsumerConflictMessage);
    }

    internal bool TryAcquireOutputConsumer(int consumer) =>
        Interlocked.CompareExchange(ref _outputReaderActive, consumer, OutputConsumerNone) == OutputConsumerNone;

    internal void ReleaseOutputConsumer(int consumer) =>
        Interlocked.CompareExchange(ref _outputReaderActive, OutputConsumerNone, consumer);

    private PtyOutputReader CreateOutputReader()
    {
        var created = new PtyOutputReader(this, _pump);
        return Interlocked.CompareExchange(ref _outputReader, created, null) ?? created;
    }

    private PtyOutputStream CreateOutputStream()
    {
        var created = new PtyOutputStream(this, _pump);
        return Interlocked.CompareExchange(ref _outputStream, created, null) ?? created;
    }
}
