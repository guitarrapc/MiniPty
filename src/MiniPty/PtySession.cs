using System.Buffers;
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
/// <see cref="SynchronizationContext"/>, so they can be awaited from a UI thread. Synchronous members that block by
/// contract, such as <see cref="Stream.Write(ReadOnlySpan{byte})"/> on <see cref="Input"/> and <see cref="Stream.Read(Span{byte})"/>
/// on <see cref="Output"/>, still block.
/// </para>
/// <para>
/// Disposing or awaiting <see cref="DisposeAsync"/> ends a child that is still running and releases handles without
/// waiting for the child to exit. On Unix the child is hung up first (<c>SIGHUP</c>), so a shell can save its history.
/// It is killed after a short grace period if it is still running, or at once if the host process exits normally
/// first. A host ended within the grace by a crash or by a signal it does not cancel sends no kill, as
/// <see cref="Dispose"/> describes. On Windows the child is terminated.
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
    // Nonzero once disposal has started. Taken with Interlocked.Exchange so exactly one Dispose tears down: the backend
    // and the ring reference this session holds are each released once.
    private int _disposed;

    internal PtySession(IPtyBackend backend)
    {
        _backend = backend;
        _pump = new PtyOutputPump(backend);
        backend.SetOutputPump(_pump);
        try
        {
            PtyStartTestHook.Run(PtyStartStage.BackendBuilt, backend.ProcessId);
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
    /// Runs on the thread that observed child exit (usually the background exit watcher), so it only hands the
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
    /// <remarks>
    /// Writes are raw bytes; no line-ending translation is performed. <see cref="Stream.WriteAsync(ReadOnlyMemory{byte}, CancellationToken)"/>
    /// behaves as <see cref="WriteInputAsync(ReadOnlyMemory{byte}, CancellationToken)"/> and never blocks the calling thread.
    /// <see cref="Stream.Write(ReadOnlySpan{byte})"/> blocks until the bytes are written, as the <see cref="Stream"/>
    /// contract requires.
    /// </remarks>
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

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

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
    /// <see langword="true"/> after the background exit watcher has observed the exit. Reading it never blocks.
    /// </value>
    /// <remarks>
    /// On Unix the watcher keeps observing after <see cref="Dispose"/>, so this can become <see langword="true"/>
    /// afterwards. A child disposed while running then reports what ended it: its own exit, the hang-up (129,
    /// <c>SIGHUP</c>), or the kill after the grace period (137, <c>SIGKILL</c>). <see cref="WaitForExitAsync"/> fails
    /// once the session is disposed, so read this property, <see cref="ExitCode"/>, or <see cref="ExitStatus"/> instead.
    /// On Windows a session disposed while its child was running never reports the exit.
    /// </remarks>
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
    /// <param name="bytes">Bytes to write. Empty buffers are ignored. Keep them unchanged until the task completes.</param>
    /// <param name="cancellationToken">
    /// Token used to cancel a write that is still waiting. Bytes not yet handed to the terminal are discarded, and the
    /// child still receives the ones already handed over. On Windows those include a pending part of up to 4 KiB that
    /// ConPTY has not read yet.
    /// </param>
    /// <returns>A task that completes when the bytes are written.</returns>
    /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
    /// <remarks>
    /// <para>
    /// Never blocks the calling thread. The bytes the terminal accepts right away are written synchronously, and the
    /// task is then already complete. This is the usual case, and after a session's first write it allocates nothing.
    /// Otherwise the rest is written in order in the background, and the task completes then. That happens when the
    /// terminal's input buffer is full: on Unix the tty input queue, which drains as the child reads, and on Windows
    /// the 4 KiB input pipe, which drains as ConPTY takes input. ConPTY stops taking input while output nobody reads
    /// has backed up.
    /// </para>
    /// <para>
    /// A canceled token, a closed input, and a failed write are reported through the returned task, not thrown from the
    /// call. Writes reach the child in the order they were issued. <see cref="SendEof"/> takes effect after every write
    /// issued before it, and a write issued after it may reach the child before end of input.
    /// </para>
    /// </remarks>
    public ValueTask WriteInputAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return bytes.IsEmpty ? default : PtyInputWrites.WriteAsync(_backend, bytes, ownedBuffer: null, cancellationToken);
    }

    /// <summary>
    /// Writes text to the PTY stdin using the specified character encoding.
    /// </summary>
    /// <param name="text">Text to encode and write. Empty strings are ignored.</param>
    /// <param name="encoding">
    /// Encoding used to convert <paramref name="text"/> to bytes. Default is <see cref="Encoding.UTF8"/>.
    /// </param>
    /// <param name="cancellationToken">
    /// Token used to cancel a write that is still waiting. Bytes not yet handed to the terminal are discarded, and the
    /// child still receives the ones already handed over. On Windows those include a pending part of up to 4 KiB that
    /// ConPTY has not read yet.
    /// </param>
    /// <returns>A task that completes when the text is written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
    /// <remarks>
    /// Never blocks the calling thread; see <see cref="WriteInputAsync(ReadOnlyMemory{byte}, CancellationToken)"/>.
    /// Bytes that must wait are kept in a pooled buffer until they are written.
    /// </remarks>
    public ValueTask WriteInputAsync(
        string text,
        Encoding? encoding = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ThrowIfDisposed();
        encoding ??= Encoding.UTF8;
        var byteCount = encoding.GetByteCount(text);
        if (byteCount == 0)
            return default;

        if (byteCount <= PtyIo.Utf8StackThreshold)
        {
            Span<byte> buffer = stackalloc byte[byteCount];
            encoding.GetBytes(text, buffer);
            return PtyInputWrites.WriteAsync(_backend, buffer, cancellationToken);
        }

        var rented = ArrayPool<byte>.Shared.Rent(byteCount);
        int length;
        try
        {
            length = encoding.GetBytes(text, rented);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(rented);
            throw;
        }

        // The backend owns the rental from here and returns it once the bytes are written.
        return PtyInputWrites.WriteAsync(_backend, rented.AsMemory(0, length), rented, cancellationToken);
    }

    /// <summary>
    /// Signals end of stdin to the child.
    /// </summary>
    /// <remarks>
    /// <para>
    /// End of input is staged on a timer that gives the child time to attach, and it takes effect after every write
    /// issued before it, waiting while earlier input is still queued. A write issued after it may reach the child
    /// first. Later calls do nothing.
    /// </para>
    /// <para>
    /// Windows: writes Ctrl+Z + CR to the ConPTY input stream and leaves the pipe open until the child exits, with or
    /// without prior bytes. The input pipe is never closed to signal EOF.
    /// </para>
    /// <para>Unix: writes EOT (<c>0x04</c>, Ctrl-D) to the PTY master. It does not close the master fd.</para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
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
    /// Clears the pseudo-console's screen buffer, for a frontend that has just cleared its own display.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Effective on Windows with an out-of-band ConPTY (<see cref="PtyStartInfo.ConPty"/>): the console host clears
    /// its buffer and repaints the frontend, so the two stay in step and the host does not later reprint content the
    /// frontend has discarded. A frontend calls it when the user clears the terminal's scrollback.
    /// </para>
    /// <para>
    /// A no-op on the in-box ConPTY, which exposes no clear operation, and on Unix, where the frontend owns the display.
    /// Also a no-op once the child has exited and the post-exit drain has closed the transport, like
    /// <see cref="Resize(PtySize)"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
    /// <exception cref="System.ComponentModel.Win32Exception">Windows: the clear failed while the child was running.</exception>
    public void Clear()
    {
        ThrowIfDisposed();
        _backend.Clear();
    }

    /// <summary>
    /// Terminates the child process without releasing PTY handles.
    /// </summary>
    /// <remarks>
    /// Uses <c>TerminateProcess</c> on Windows and <c>SIGKILL</c> on Unix.
    /// Call <see cref="Dispose"/> afterward to release resources. A kill that races disposal on another thread ends
    /// the child, throws <see cref="ObjectDisposedException"/>, or returns without signaling because disposal has
    /// begun. In the last two cases disposal ends the child as <see cref="Dispose"/> describes.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
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
    /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
    public void Kill(PtySignal signal)
    {
        ThrowIfDisposed();
        _backend.Kill(signal);
    }

    /// <summary>
    /// Asynchronously waits until the child process exits and returns its <see cref="PtyExitStatus"/>.
    /// </summary>
    /// <param name="cancellationToken">
    /// When canceled, waiting stops and <see cref="OperationCanceledException"/> is thrown.
    /// The child process continues running.
    /// </param>
    /// <returns>
    /// A task that completes with the child's exit code and, on Unix, the signal that terminated it.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Without a cancelable token every call returns the same task, so waiting allocates nothing beyond the first
    /// call. The wait holds no thread: the task completes when the background exit watcher observes the exit.
    /// </para>
    /// <para>
    /// Waiting does not affect output: a concurrent output reader keeps receiving output until end of output,
    /// which follows shortly after exit once output has gone quiet. Disposing the session before the child exits
    /// fails the wait with <see cref="ObjectDisposedException"/>. A wait started earlier still reports the status of
    /// a child whose exit was observed before the session was disposed. After disposal, read <see cref="ExitStatus"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
    /// <exception cref="OperationCanceledException">Waiting was canceled.</exception>
    public Task<PtyExitStatus> WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        // WaitAsync returns the exit task itself when the token cannot be canceled.
        return _backend.Exited.WaitAsync(cancellationToken);
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
    /// Output is collected on the session's reader thread while the child runs, so awaiting this method does not
    /// block the calling thread. <see cref="PtyCompleteOptions.Input"/> is written as by
    /// <see cref="WriteInputAsync(string, Encoding?, CancellationToken)"/>, without blocking either.
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
    /// Releases PTY and process handles, ending the child if it is still running. Does not block.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On Unix a running child is hung up with <c>SIGHUP</c>, the signal a shell receives when its terminal window
    /// closes. The signal is sent without blocking. If the child has not exited one second later, a background thread
    /// kills it with <c>SIGKILL</c>. If the process exits normally before then, by returning from <c>Main</c> or calling
    /// <see cref="Environment.Exit(int)"/>, the kill is sent at exit. A host ended within that second by a crash or by
    /// a signal it does not cancel, such as <c>SIGTERM</c> to a console app, never sends the kill, so a child that
    /// ignores the hang-up survives. Calling <see cref="Kill()"/> first ends the child at once but skips the hang-up.
    /// An interactive shell killed this way cannot resend the hang-up to its jobs, so a running background job keeps
    /// running. A host can instead cancel such a signal, hang up each session with <see cref="Kill(PtySignal)"/>, wait
    /// a bounded time for the child to exit, dispose the session even when the wait times out, and exit normally. The
    /// wait gives a shell time to resend the hang-up to its jobs, and the kills are sent at exit. MiniPty signals only
    /// the child. When the child exits, the system hangs up the terminal's foreground process group, so a descendant in
    /// that group ends too unless it ignores <c>SIGHUP</c>. On Windows the child is terminated.
    /// </para>
    /// <para>
    /// In-flight output reads and exit waits fail with <see cref="ObjectDisposedException"/>. The background
    /// reader stops on its own shortly afterwards; output chunk memory must not be used after this call. Use
    /// <see cref="DisposeAsync"/> to also wait until the reader has stopped.
    /// </para>
    /// <para>
    /// Safe to call more than once, also from several threads at once, such as a host's exit handler racing its main
    /// thread. The first call tears the session down; a call that finds disposal already started returns at once.
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Volatile.Write(ref _outputReaderActive, OutputConsumerNone);

        // Discard mode first: closing the pseudo-console waits for ConPTY to flush, which needs a reader.
        _pump.OnSessionDisposing();
        _backend.Dispose();
        _pump.OnSessionDisposed();
    }

    /// <summary>
    /// Releases PTY and process handles, ending the child if it is still running as <see cref="Dispose"/> does, and
    /// completes once the background output reader has stopped.
    /// </summary>
    /// <returns>A value task that completes when the reader has released the transport.</returns>
    /// <remarks>
    /// Does not wait for the reader on the calling thread, and does not wait for the child to exit. Every call waits
    /// for the same shutdown, including calls after <see cref="Dispose"/> and concurrent ones.
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        // Idempotent teardown; disposal having started does not mean the reader has stopped.
        Dispose();
        return _pump.WaitForStopAsync();
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
        if (Volatile.Read(ref _disposed) != 0)
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
