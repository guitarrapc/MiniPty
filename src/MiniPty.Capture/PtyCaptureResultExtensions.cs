using System.Buffers;
using System.Runtime.InteropServices;

namespace MiniPty.Capture;

/// <summary>
/// Display helpers for <see cref="PtyCaptureResult"/>.
/// </summary>
public static class PtyCaptureResultExtensions
{
    /// <summary>
    /// Transforms merged capture output for host display using <see cref="PtyOutput.ToDisplayText"/>.
    /// </summary>
    /// <param name="result">Capture result whose <see cref="PtyCaptureResult.Output"/> is transformed.</param>
    /// <param name="mode">Display transformation to apply.</param>
    /// <returns>Displayable text for the chosen mode.</returns>
    public static string ToDisplayText(this PtyCaptureResult result, PtyOutputDisplayMode mode)
    {
        ArgumentNullException.ThrowIfNull(result);
        return PtyOutput.ToDisplayText(result.Output, mode);
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

        if (TryGetContiguousChunkText(chunks, out var contiguous))
            return PtyOutput.ToDisplayText(contiguous, mode);

        return PtyOutput.ToDisplayText(MergeChunkText(chunks), mode);
    }

    private static bool TryGetContiguousChunkText(IReadOnlyList<PtyCaptureChunk> chunks, out ReadOnlySpan<char> text)
    {
        if (!MemoryMarshal.TryGetString(chunks[0].Text, out var str, out var start, out var length))
        {
            text = default;
            return false;
        }

        var end = start + length;
        for (var i = 1; i < chunks.Count; i++)
        {
            if (!MemoryMarshal.TryGetString(chunks[i].Text, out var other, out var otherStart, out var otherLength)
                || !ReferenceEquals(str, other))
            {
                text = default;
                return false;
            }

            if (otherStart != end)
            {
                text = default;
                return false;
            }

            end += otherLength;
        }

        text = str.AsSpan(start, end - start);
        return true;
    }

    private static string MergeChunkText(IReadOnlyList<PtyCaptureChunk> chunks)
    {
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

            return new string(buffer, 0, total);
        }
        finally
        {
            pool.Return(buffer);
        }
    }
}
