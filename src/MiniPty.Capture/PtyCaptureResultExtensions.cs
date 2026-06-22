using System.Buffers;
using System.Text;

namespace MiniPty.Capture;

/// <summary>
/// Display helpers for <see cref="PtyCaptureResult"/>.
/// </summary>
public static class PtyCaptureResultExtensions
{
    /// <summary>
    /// Transforms merged decoded capture output for host display.
    /// </summary>
    /// <param name="result">Capture result whose <see cref="PtyCaptureResult.Output"/> is transformed.</param>
    /// <param name="mode">Display transformation to apply.</param>
    /// <returns>Displayable text for the chosen mode.</returns>
    public static string ToDisplayText(this PtyCaptureResult result, PtyOutputDisplayMode mode)
    {
        ArgumentNullException.ThrowIfNull(result);
        return PtyOutput.ToDisplayText(result.Output.Span, mode);
    }

    /// <summary>
    /// Decodes merged raw capture bytes and transforms them for host display.
    /// </summary>
    /// <param name="result">Capture result whose <see cref="PtyCaptureResult.OutputBytes"/> is transformed.</param>
    /// <param name="mode">Display transformation to apply.</param>
    /// <param name="encoding">Encoding used by the child process terminal stream. Default is UTF-8.</param>
    /// <returns>Displayable text for the chosen mode.</returns>
    public static string ToDisplayTextFromBytes(
        this PtyCaptureResult result,
        PtyOutputDisplayMode mode,
        Encoding? encoding = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        return PtyOutput.ToDisplayText(result.OutputBytes.Span, encoding ?? Encoding.UTF8, mode);
    }

    /// <summary>
    /// Concatenates chunk text and transforms it for host display.
    /// </summary>
    /// <param name="chunks">Timestamped output chunks from a capture run.</param>
    /// <param name="mode">Display transformation to apply.</param>
    /// <returns>Displayable text for the chosen mode.</returns>
    public static string ToDisplayText(this IReadOnlyList<PtyCaptureChunk> chunks, PtyOutputDisplayMode mode)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        if (chunks.Count == 0)
            return string.Empty;

        if (chunks.Count == 1)
            return PtyOutput.ToDisplayText(chunks[0].Text.Span, mode);

        var texts = new List<ReadOnlyMemory<char>>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
            texts.Add(chunks[i].Text);

        if (PtyMemory.TryGetContiguousText(texts, out var contiguous))
            return PtyOutput.ToDisplayText(contiguous, mode);

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
