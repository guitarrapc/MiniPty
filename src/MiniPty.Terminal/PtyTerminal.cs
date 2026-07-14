using System.Text;

namespace MiniPty.Terminal;

/// <summary>
/// Push-model terminal backend over a <see cref="PtySession"/> for frontend integrations
/// (xterm.js, editor terminals). Equivalent role to node-pty: output is pushed to a handler,
/// exit is observed after all output has been delivered, and <see cref="Pause"/> /
/// <see cref="Resume"/> provide the flow-control primitive frontends need.
/// </summary>
/// <remarks>
/// <para>
/// The terminal owns its session end-to-end: it is created by <see cref="Start"/> or transferred
/// by <see cref="Attach"/>, is the sole output consumer, and disposing the terminal kills the child
/// if still running. The underlying session is not exposed after ownership is transferred.
/// </para>
/// <para>
/// Output ordering contract: every output handler invocation completes before
/// <see cref="Completion"/> transitions to completed, so tail output is never lost
/// (drain-then-exit, matching node-pty's onData/onExit ordering).
/// </para>
/// </remarks>
public sealed class PtyTerminal : IAsyncDisposable
{
    private readonly PtySession _session;
    private readonly PtyTerminalOutputHandler _output;
    private readonly CancellationTokenSource _pumpCts = new();
    private readonly Task<PtyExitStatus> _pumpTask;
    private TaskCompletionSource? _pauseGate;
    private int _disposed;

    private PtyTerminal(PtySession session, PtyTerminalOutputHandler output)
    {
        _session = session;
        _output = output;
        _pumpTask = Task.Run(PumpAsync);
    }

    /// <summary>
    /// Spawns a child process attached to a new pseudo-terminal and starts pushing its output to
    /// <see cref="PtyTerminalOptions.Output"/>.
    /// </summary>
    /// <param name="startInfo">Child process launch configuration.</param>
    /// <param name="options">Terminal behavior; the output handler is required.</param>
    /// <returns>A running terminal. Dispose it to kill the child and release the PTY.</returns>
    public static PtyTerminal Start(PtyStartInfo startInfo, PtyTerminalOptions options)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Output);

        return new PtyTerminal(Pty.Start(startInfo), options.Output);
    }

    /// <summary>
    /// Takes ownership of an existing session and starts pushing its output to the configured
    /// handler.
    /// </summary>
    /// <param name="session">Session whose ownership is transferred to the terminal.</param>
    /// <param name="options">Terminal behavior; the output handler is required.</param>
    /// <returns>A terminal that disposes <paramref name="session"/> when disposed.</returns>
    /// <remarks>
    /// The session must not have another active output consumer. After a successful call, callers
    /// must use the returned terminal for lifecycle operations and must not dispose the session.
    /// </remarks>
    public static PtyTerminal Attach(PtySession session, PtyTerminalOptions options)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Output);

        return new PtyTerminal(session, options.Output);
    }

    /// <summary>Gets the OS process id of the child.</summary>
    public int ProcessId => _session.ProcessId;

    /// <summary>Gets the current terminal dimensions.</summary>
    public PtySize Size => _session.Size;

    /// <summary>
    /// Gets the foreground process name for editor title polling, or <see langword="null"/> when
    /// the current platform cannot report it.
    /// </summary>
    public string? ActiveProcessName => _session.ActiveProcessName;

    /// <summary>Gets a value indicating whether the child process has exited.</summary>
    public bool HasExited => _session.HasExited;

    /// <summary>
    /// Gets the exit status when <see cref="HasExited"/> is <see langword="true"/>; otherwise
    /// <see langword="null"/>. Unlike <see cref="Completion"/> this does not wait for output
    /// delivery, so it is readable even when the pump faulted before draining.
    /// </summary>
    public PtyExitStatus? ExitStatus => _session.ExitStatus;

    /// <summary>
    /// Gets a task that completes with the child's <see cref="PtyExitStatus"/> after the child has
    /// exited <em>and</em> every output handler invocation has completed. Faults with the handler
    /// exception when a handler throws (the child is killed first), and with
    /// <see cref="OperationCanceledException"/> when the terminal is disposed before exit.
    /// </summary>
    public Task<PtyExitStatus> Completion => _pumpTask;

    /// <summary>
    /// Writes raw bytes to the PTY stdin. Safe to call concurrently with output delivery.
    /// </summary>
    /// <param name="bytes">Bytes to write. Empty buffers are ignored.</param>
    /// <param name="cancellationToken">Token used to cancel the write operation.</param>
    public ValueTask WriteInputAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _session.WriteInputAsync(bytes, cancellationToken);
    }

    /// <summary>
    /// Writes text to the PTY stdin using the specified character encoding (UTF-8 by default).
    /// </summary>
    /// <param name="text">Text to encode and write. Empty strings are ignored.</param>
    /// <param name="encoding">Encoding used to convert <paramref name="text"/> to bytes.</param>
    /// <param name="cancellationToken">Token used to cancel the write operation.</param>
    public ValueTask WriteInputAsync(string text, Encoding? encoding = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _session.WriteInputAsync(text, encoding, cancellationToken);
    }

    /// <summary>Signals end of stdin to the child. See <see cref="PtySession.SendEof"/>.</summary>
    public void SendEof()
    {
        ThrowIfDisposed();
        _session.SendEof();
    }

    /// <summary>Resizes the pseudo-terminal to the given dimensions.</summary>
    /// <param name="size">New width and height in character cells.</param>
    public void Resize(PtySize size)
    {
        ThrowIfDisposed();
        _session.Resize(size);
    }

    /// <summary>Resizes the terminal with optional Unix pixel dimensions.</summary>
    /// <param name="size">New width and height in character cells.</param>
    /// <param name="pixelSize">Optional terminal width and height in pixels.</param>
    public void Resize(PtySize size, PtyPixelSize? pixelSize)
    {
        ThrowIfDisposed();
        _session.Resize(size, pixelSize);
    }

    /// <summary>
    /// Synchronizes a cleared frontend buffer when the platform supports it.
    /// </summary>
    /// <remarks>
    /// This is currently a documented no-op. In-box ConPTY exposes no public clear operation and
    /// Unix frontends own their scrollback, so calling this method is always safe.
    /// </remarks>
    public void Clear()
    {
        ThrowIfDisposed();
    }

    /// <summary>
    /// Pauses output delivery for flow control. No output handler runs while paused.
    /// </summary>
    /// <remarks>
    /// Pausing parks the pump: the session's strict-handoff producer stops reading the PTY
    /// transport, the OS PTY buffer fills, and the child eventually blocks on write. No data is
    /// dropped or buffered in managed memory beyond the single in-flight chunk. Equivalent to
    /// node-pty's <c>pause()</c>.
    /// </remarks>
    public void Pause()
    {
        ThrowIfDisposed();
        if (Volatile.Read(ref _pauseGate) is null)
        {
            Interlocked.CompareExchange(
                ref _pauseGate,
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                null);
        }
    }

    /// <summary>Resumes output delivery after <see cref="Pause"/>. Equivalent to node-pty's <c>resume()</c>.</summary>
    public void Resume()
    {
        ThrowIfDisposed();
        Interlocked.Exchange(ref _pauseGate, null)?.TrySetResult();
    }

    /// <summary>
    /// Gracefully hangs up the child process. Remaining output is drained and delivered before
    /// <see cref="Completion"/> completes.
    /// On Unix this sends SIGHUP, matching node-pty's parameterless <c>kill()</c>; on Windows it
    /// terminates the process because ConPTY has no signal delivery.
    /// </summary>
    public void Kill()
    {
        ThrowIfDisposed();
        _session.Kill(PtySignal.Hangup);
    }

    /// <summary>
    /// Sends a signal to the child process. See <see cref="PtySession.Kill(PtySignal)"/> for
    /// platform semantics (Windows terminates regardless of signal).
    /// </summary>
    /// <param name="signal">Signal to deliver.</param>
    public void Kill(PtySignal signal)
    {
        ThrowIfDisposed();
        _session.Kill(signal);
    }

    /// <summary>
    /// Stops output delivery and disposes the session, killing the child if still running.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // Cancel first so the enumerator, a parked pause gate, and in-flight handlers observe
        // cancellation, then wait for the pump to stop before disposing the session: disposing
        // while a canceled read is still unwinding races the session's internal buffer teardown.
        _pumpCts.Cancel();
        Interlocked.Exchange(ref _pauseGate, null)?.TrySetResult();

        try
        {
            await _pumpTask.ConfigureAwait(false);
        }
        catch
        {
            // Completion observers see the original fault; disposal must not rethrow it.
        }

        await _session.DisposeAsync().ConfigureAwait(false);
        _pumpCts.Dispose();
    }

    private async Task<PtyExitStatus> PumpAsync()
    {
        var cancellationToken = _pumpCts.Token;
        try
        {
            await foreach (var chunk in _session.ReadOutputAsync(cancellationToken).ConfigureAwait(false))
            {
                // Gate before delivery: while paused the in-flight chunk stays valid (the pump
                // does not advance the enumerator) and the producer stops reading the transport.
                // The inline null check keeps the unpaused per-chunk path free of async-method
                // overhead; the await runs only while actually paused.
                if (Volatile.Read(ref _pauseGate) is not null)
                    await WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);

                await _output(chunk.Data, cancellationToken).ConfigureAwait(false);
            }

            // Enumeration completes only after child exit and post-exit drain, so every handler
            // invocation has finished before the exit status is observed and published.
            return await _session.WaitForExitStatusAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Handler exception, transport failure, or disposal: deterministic teardown. Killing
            // an already-exited or disposed session is a no-op / benign here.
            if (Volatile.Read(ref _disposed) == 0)
            {
                try
                {
                    _session.Kill();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            throw;
        }
    }

    private async ValueTask WaitWhilePausedAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var gate = Volatile.Read(ref _pauseGate);
            if (gate is null)
                return;

            await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(PtyTerminal));
    }
}
