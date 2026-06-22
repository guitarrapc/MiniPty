using System.Diagnostics;
using MiniPty;
using MiniPty.Internal;

namespace MiniPty.Capture;

/// <summary>
/// High-level API for spawning a PTY child and observing timestamped output in one call.
/// </summary>
/// <remarks>
/// Built on <see cref="Pty.Start"/> and <see cref="PtySession.CompleteAsync"/>.
/// Each <see cref="PtyCaptureChunk"/> records one read from the PTY output stream with elapsed time since session start (immediately after spawn).
/// </remarks>
public static class PtyCapture
{
    private static readonly PtyCaptureOptions DefaultOptions = new();

    /// <summary>
    /// Spawns a child in a pseudo-terminal, observes timestamped byte output, waits for exit, and disposes the session.
    /// </summary>
    /// <param name="startInfo">Executable, arguments, working directory, and initial terminal size.</param>
    /// <param name="options">Capture and completion options, or <see langword="null"/> for defaults.</param>
    /// <param name="cancellationToken">
    /// When canceled, the child is killed when <see cref="PtyCompleteOptions.KillOnCancellation"/> is
    /// <see langword="true"/> (default).
    /// </param>
    /// <returns>
    /// A <see cref="PtyCaptureResult"/> containing merged bytes, per-read byte chunks, exit code, and optional pump-decoded text.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="startInfo"/> is <see langword="null"/>.</exception>
    /// <exception cref="PlatformNotSupportedException">The current operating system is not supported.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    /// <exception cref="TimeoutException">Exit or output drain timeout was exceeded.</exception>
    public static async Task<PtyCaptureResult> RunAsync(
        PtyStartInfo startInfo,
        PtyCaptureOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        options ??= DefaultOptions;
        var completion = options.Completion;
        await using var session = Pty.Start(startInfo);
        var origin = Stopwatch.StartNew();
        var (capture, exitCode) = await PtyCompletion.RunAsync(
            session,
            completion,
            (stream, ct) => PtyCapturePump.ReadAsync(
                stream,
                origin,
                completion.OutputEncoding,
                completion.DecodeOutput,
                ct),
            cancellationToken).ConfigureAwait(false);

        return new PtyCaptureResult(capture.ToPayload(), exitCode, capture.Chunks, capture.TextChunks);
    }
}
