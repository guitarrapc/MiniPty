using System.Text;
using MiniPty.Internal;

namespace MiniPty.Capture;

/// <summary>
/// Result of a full <see cref="PtyCapture.RunAsync"/> run.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Output"/> and <see cref="Chunks"/> are the canonical byte-oriented capture products.
/// Text views are available through <see cref="GetText"/> and <see cref="GetTextChunks"/>.
/// </para>
/// <para>
/// See <see cref="PtyResult"/> for the caching contract when <see cref="PtyCompleteOptions.DecodeOutput"/>
/// is <see langword="false"/>.
/// </para>
/// </remarks>
public sealed class PtyCaptureResult
{
    private readonly byte[] outputBytes;
    private readonly char[]? pumpDecodedChars;
    private readonly PtyCaptureChunk[] chunks;
    private readonly PtyCaptureTextChunk[]? pumpTextChunks;
    private readonly Encoding outputEncoding;

    internal PtyCaptureResult(
        PtyPumpPayload payload,
        int exitCode,
        PtyCaptureChunk[] chunks,
        PtyCaptureTextChunk[]? textChunks)
    {
        ArgumentNullException.ThrowIfNull(payload);
        outputBytes = payload.Bytes;
        pumpDecodedChars = payload.Chars;
        outputEncoding = payload.Encoding;
        ExitCode = exitCode;
        this.chunks = chunks;
        pumpTextChunks = textChunks;
    }

    /// <summary>
    /// Gets the merged stdout and stderr bytes from the PTY master output stream.
    /// </summary>
    public ReadOnlyMemory<byte> Output => outputBytes;

    /// <summary>
    /// Gets the operating-system exit code of the child process.
    /// </summary>
    public int ExitCode { get; }

    /// <summary>
    /// Gets timestamped raw byte slices observed during the session (one per read from the PTY output stream).
    /// </summary>
    public IReadOnlyList<PtyCaptureChunk> Chunks => chunks;

    /// <inheritdoc cref="PtyResult.GetText"/>
    public ReadOnlyMemory<char> GetText(Encoding? encoding = null)
    {
        if (pumpDecodedChars is not null)
            return pumpDecodedChars.AsMemory();

        return PtyOutputHelpers.DecodeToNewChars(Output.Span, encoding ?? outputEncoding);
    }

    /// <inheritdoc cref="PtyResult.GetTextString"/>
    public string GetTextString(Encoding? encoding = null)
    {
        if (pumpDecodedChars is not null)
            return pumpDecodedChars.Length == 0 ? string.Empty : new string(pumpDecodedChars);

        encoding ??= outputEncoding;
        return encoding.GetString(Output.Span);
    }

    /// <inheritdoc cref="PtyResult.Contains(string, StringComparison)"/>
    public bool Contains(string value, StringComparison comparison = StringComparison.Ordinal)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        return GetText().Span.Contains(value, comparison);
    }

    /// <inheritdoc cref="PtyResult.Contains(ReadOnlySpan{byte})"/>
    public bool Contains(ReadOnlySpan<byte> pattern) =>
        PtyOutputHelpers.ContainsBytes(Output.Span, pattern);

    /// <inheritdoc cref="PtyResult.ContainsUtf8"/>
    public bool ContainsUtf8(string utf8Text, StringComparison comparison = StringComparison.Ordinal) =>
        PtyOutputHelpers.ContainsUtf8(Output.Span, utf8Text, comparison);

    /// <summary>
    /// Gets timestamped decoded text slices aligned to PTY read boundaries.
    /// </summary>
    /// <param name="encoding">Encoding used when on-demand decoding is required.</param>
    /// <returns>
    /// Per-read text chunks. When pump decoding was enabled, returns pre-built slices without allocating.
    /// Otherwise rebuilds chunks on every call.
    /// </returns>
    public IReadOnlyList<PtyCaptureTextChunk> GetTextChunks(Encoding? encoding = null)
    {
        if (pumpTextChunks is not null)
            return pumpTextChunks;

        return PtyCaptureTextDecode.DecodeFromByteChunks(Chunks, encoding ?? outputEncoding);
    }
}
