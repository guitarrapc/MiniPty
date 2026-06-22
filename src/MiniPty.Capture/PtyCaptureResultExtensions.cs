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
    /// Concatenates chunk data and transforms it for host display.
    /// </summary>
    /// <param name="chunks">Timestamped output chunks from a capture run.</param>
    /// <param name="mode">Display transformation to apply.</param>
    /// <returns>Displayable text for the chosen mode.</returns>
    public static string ToDisplayText(this IReadOnlyList<PtyCaptureChunk> chunks, PtyOutputDisplayMode mode)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        return PtyOutput.ToDisplayText(string.Concat(chunks.Select(static chunk => chunk.Data)), mode);
    }
}
