using System.Buffers;
using System.Text;

namespace MiniPty.Capture;

/// <summary>
/// Display helpers for <see cref="PtyCaptureResult"/>.
/// </summary>
public static class PtyCaptureResultExtensions
{
    /// <summary>
    /// Transforms decoded capture output for host display.
    /// </summary>
    /// <param name="result">Capture result whose decoded text is transformed.</param>
    /// <param name="mode">Display transformation to apply.</param>
    /// <returns>Displayable text for the chosen mode.</returns>
    public static string ToDisplayText(this PtyCaptureResult result, PtyOutputDisplayMode mode)
    {
        ArgumentNullException.ThrowIfNull(result);
        return PtyOutput.ToDisplayText(result.GetText(), mode);
    }

    /// <summary>
    /// Transforms raw capture bytes for host display without requiring a separate <see cref="PtyCaptureResult.GetText"/> call when only display output is needed.
    /// </summary>
    /// <param name="result">Capture result whose <see cref="PtyCaptureResult.Output"/> is transformed.</param>
    /// <param name="mode">Display transformation to apply.</param>
    /// <param name="encoding">Encoding used by the child process terminal stream. Default is UTF-8.</param>
    /// <returns>Displayable text for the chosen mode.</returns>
    public static string ToDisplayTextFromOutput(
        this PtyCaptureResult result,
        PtyOutputDisplayMode mode,
        Encoding? encoding = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        return PtyOutput.ToDisplayText(result.Output, encoding ?? Encoding.UTF8, mode);
    }

    /// <summary>
    /// Concatenates per-read text chunks and transforms them for host display.
    /// </summary>
    /// <param name="chunks">Timestamped text chunks from <see cref="PtyCaptureResult.GetTextChunks"/>.</param>
    /// <param name="mode">Display transformation to apply.</param>
    /// <returns>Displayable text for the chosen mode.</returns>
    public static string ToDisplayText(this IReadOnlyList<PtyCaptureTextChunk> chunks, PtyOutputDisplayMode mode)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        if (chunks.Count == 0)
            return string.Empty;

        if (chunks.Count == 1)
            return PtyOutput.ToDisplayText(chunks[0].Text, mode);

        var total = 0;
        for (var i = 0; i < chunks.Count; i++)
            total += chunks[i].Text.Length;

        var pool = ArrayPool<char>.Shared;
        var buffer = pool.Rent(total);
        try
        {
            var offset = 0;
            for (var i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i].Text.Span;
                chunk.CopyTo(buffer.AsSpan(offset, chunk.Length));
                offset += chunk.Length;
            }

            return PtyOutput.ToDisplayText(buffer.AsSpan(0, total), mode);
        }
        finally
        {
            pool.Return(buffer);
        }
    }
}
