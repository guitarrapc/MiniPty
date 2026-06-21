using System.Text;
using MiniPty.Internal;

namespace MiniPty;

/// <summary>
/// A running pseudo-terminal child session.
/// Disposing kills the child if it is still running, then releases handles.
/// </summary>
public sealed class PtySession : IAsyncDisposable, IDisposable
{
    private readonly IPtyBackend _backend;
    private bool _disposed;

    internal PtySession(IPtyBackend backend) => _backend = backend;

    /// <summary>Operating-system process identifier of the child.</summary>
    public int ProcessId => _backend.ProcessId;

    /// <summary>Current terminal dimensions.</summary>
    public PtySize Size => _backend.Size;

    /// <summary>Write-only stream to the child stdin (PTY input).</summary>
    public Stream Input => _backend.Input;

    /// <summary>Read-only stream from the child stdout/stderr (PTY output).</summary>
    public Stream Output => _backend.Output;

    /// <summary>Polls the OS for child exit.</summary>
    public bool HasExited => _backend.HasExited;

    /// <summary>Exit code when <see cref="HasExited"/> is <see langword="true"/>; otherwise <see langword="null"/>.</summary>
    public int? ExitCode => _backend.HasExited ? _backend.ExitCode : null;

    /// <summary>Writes bytes to the PTY stdin.</summary>
    public async ValueTask WriteInputAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        if (!bytes.IsEmpty)
            await Input.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes text to the PTY stdin using the specified encoding (UTF-8 by default).</summary>
    public ValueTask WriteInputAsync(
        string text,
        Encoding? encoding = null,
        CancellationToken cancellationToken = default) =>
        new(PtyIo.WriteTextAsync(Input, text, encoding ?? Encoding.UTF8, cancellationToken));

    /// <summary>
    /// Signals end of stdin. Windows defers pipe close until wait; Unix writes EOT (staged).
    /// </summary>
    public void SendEof() => _backend.SendEof();

    /// <summary>Resizes the pseudo-terminal.</summary>
    public void Resize(PtySize size) => _backend.Resize(size.Columns, size.Rows);

    /// <summary>Terminates the child process.</summary>
    public void Kill() => _backend.Kill();

    /// <summary>Waits for the child to exit. Cancellation stops waiting only; the child keeps running.</summary>
    public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default) =>
        WaitForExitInternalAsync(cancellationToken, killOnCancellation: false);

    /// <summary>
    /// Drains <see cref="Output"/>, optionally writes stdin, waits for exit, and returns merged output.
    /// </summary>
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

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _backend.Dispose();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    internal Task<int> WaitForExitInternalAsync(CancellationToken cancellationToken, bool killOnCancellation) =>
        _backend.WaitForExitAsync(cancellationToken, killOnCancellation);

    internal void CloseOutputTransport() => _backend.CloseOutputTransport();
}
