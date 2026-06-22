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
    private readonly IPtyBackend _backend;
    private bool _disposed;

    internal PtySession(IPtyBackend backend) => _backend = backend;

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
    public Stream Output => _backend.Output;

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
        CancellationToken cancellationToken = default) =>
        new(PtyIo.WriteTextAsync(Input, text, encoding ?? Encoding.UTF8, cancellationToken));

    /// <summary>
    /// Signals end of stdin to the child.
    /// </summary>
    /// <remarks>
    /// <para>Windows: closes the ConPTY input pipe (deferred until the first wait poll when staged).</para>
    /// <para>Unix: writes EOT (<c>0x04</c>, Ctrl-D) to the PTY master; does not close the master fd.</para>
    /// </remarks>
    public void SendEof() => _backend.SendEof();

    /// <summary>
    /// Resizes the pseudo-terminal to the given dimensions.
    /// </summary>
    /// <param name="size">New width and height in character cells.</param>
    /// <exception cref="InvalidOperationException">The PTY transport has already been closed.</exception>
    /// <exception cref="System.ComponentModel.Win32Exception">Windows: <c>ResizePseudoConsole</c> failed.</exception>
    /// <exception cref="IOException">Unix: <c>TIOCSWINSZ</c> failed.</exception>
    public void Resize(PtySize size) => _backend.Resize(size.Columns, size.Rows);

    /// <summary>
    /// Terminates the child process without releasing PTY handles.
    /// </summary>
    /// <remarks>
    /// Uses <c>TerminateProcess</c> on Windows and <c>SIGKILL</c> on Unix.
    /// Call <see cref="Dispose"/> afterward to release resources.
    /// </remarks>
    public void Kill() => _backend.Kill();

    /// <summary>
    /// Asynchronously waits until the child process exits.
    /// </summary>
    /// <param name="cancellationToken">
    /// When canceled, waiting stops and <see cref="OperationCanceledException"/> is thrown.
    /// The child process continues running.
    /// </param>
    /// <returns>A task that completes with the child exit code.</returns>
    /// <exception cref="OperationCanceledException">Waiting was canceled.</exception>
    public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default) =>
        WaitForExitInternalAsync(cancellationToken, killOnCancellation: false);

    /// <summary>
    /// Pumps and drains <see cref="Output"/>, optionally writes stdin, waits for exit, and returns merged text.
    /// </summary>
    /// <param name="options">Completion behavior, or <see langword="null"/> for defaults.</param>
    /// <param name="cancellationToken">
    /// When canceled, behavior depends on <see cref="PtyCompleteOptions.KillOnCancellation"/> (default: kill child).
    /// </param>
    /// <returns>A <see cref="PtyResult"/> with decoded output and exit code.</returns>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    /// <exception cref="TimeoutException">
    /// <see cref="PtyCompleteOptions.ExitTimeout"/> or output drain timeout was exceeded.
    /// </exception>
    public async Task<PtyResult> CompleteAsync(
        PtyCompleteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new PtyCompleteOptions();
        var encoding = options.OutputEncoding;
        var (output, exitCode) = await PtyCompletion.RunAsync(
            this,
            options,
            (stream, ct) => PtyTextPump.ReadAllAsync(stream, encoding, ct),
            cancellationToken).ConfigureAwait(false);

        return new PtyResult(output, exitCode);
    }

    /// <summary>
    /// Synchronously releases PTY and process handles, killing the child if it is still running.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
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
}
