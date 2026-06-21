using System.Diagnostics;
using System.Text;
using MiniPty;
using MiniPty.Internal;

namespace MiniPty.Capture;

/// <summary>Timestamped PTY output capture built on <see cref="Pty"/>.</summary>
public static class PtyCapture
{
    /// <summary>
    /// Spawns a child, captures timestamped output, and waits for exit.
    /// Chunk timestamps are measured from session start.
    /// </summary>
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
