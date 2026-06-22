using System.Diagnostics;
using System.Text;
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
    /// <summary>
    /// Spawns a child in a pseudo-terminal, observes timestamped output, waits for exit, and disposes the session.
    /// </summary>
    /// <param name="startInfo">Executable, arguments, working directory, and initial terminal size.</param>
    /// <param name="options">Capture and completion options, or <see langword="null"/> for defaults.</param>
    /// <param name="cancellationToken">
    /// When canceled, the child is killed when <see cref="PtyCompleteOptions.KillOnCancellation"/> is
    /// <see langword="true"/> (default).
    /// </param>
    /// <returns>
    /// A <see cref="PtyCaptureResult"/> containing merged output, exit code, and per-read chunks with timestamps.
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
        options ??= new PtyCaptureOptions();
        var completion = options.Completion;
        await using var session = Pty.Start(startInfo);
        var origin = Stopwatch.StartNew();
        var (chunks, exitCode) = await PtyCompletion.RunAsync(
            session,
            completion,
            (stream, ct) => PtyCapturePump.ReadAsync(stream, origin, completion.OutputEncoding, ct),
            cancellationToken).ConfigureAwait(false);

        var output = string.Concat(chunks.Select(static chunk => chunk.Data));
        return new PtyCaptureResult(output, exitCode, chunks);
    }
}
