using System.Text;
using MiniPty.Internal;

namespace MiniPty;

/// <summary>
/// Merged PTY output and child exit code returned by <see cref="PtySession.CompleteAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Output"/> is the canonical raw byte stream from the PTY. Text is not stored as a property;
/// call <see cref="GetText"/> or <see cref="GetTextString"/> when decoded text is needed.
/// </para>
/// <para>
/// When completion used <see cref="PtyCompleteOptions.DecodeOutput"/> = <see langword="true"/> (default),
/// <see cref="GetText"/> returns a slice of memory decoded during the pump without extra allocation.
/// When decoding was skipped, <see cref="GetText"/> decodes on every call — cache the returned
/// <see cref="ReadOnlyMemory{Char}"/> locally if you need text more than once.
/// </para>
/// </remarks>
public sealed class PtyResult
{
    private readonly byte[] outputBytes;
    private readonly char[]? pumpDecodedChars;
    private readonly Encoding outputEncoding;

    internal PtyResult(PtyPumpPayload payload, int exitCode)
    {
        ArgumentNullException.ThrowIfNull(payload);
        outputBytes = payload.Bytes;
        pumpDecodedChars = payload.Chars;
        outputEncoding = payload.Encoding;
        ExitCode = exitCode;
    }

    /// <summary>
    /// Gets the merged stdout and stderr bytes from the PTY master output stream.
    /// </summary>
    /// <remarks>This is the canonical PTY output. Prefer this for recording and binary inspection.</remarks>
    public ReadOnlyMemory<byte> Output => outputBytes;

    /// <summary>
    /// Gets the operating-system exit code of the child process.
    /// </summary>
    public int ExitCode { get; }

    /// <summary>
    /// Gets decoded text from <see cref="Output"/>.
    /// </summary>
    /// <param name="encoding">
    /// Encoding used to decode bytes. Defaults to the <see cref="PtyCompleteOptions.OutputEncoding"/> from completion.
    /// </param>
    /// <returns>
    /// Decoded characters. When pump decoding was enabled, returns a slice without allocating.
    /// Otherwise allocates a new buffer on every call.
    /// </returns>
    public ReadOnlyMemory<char> GetText(Encoding? encoding = null)
    {
        if (pumpDecodedChars is not null)
            return pumpDecodedChars.AsMemory();

        return PtyOutputHelpers.DecodeToNewChars(Output.Span, encoding ?? outputEncoding);
    }

    /// <summary>
    /// Materializes decoded text as a <see cref="string"/> (always allocates when pump decoding was skipped).
    /// </summary>
    /// <param name="encoding">Encoding used to decode bytes.</param>
    /// <returns>A string containing the decoded PTY output.</returns>
    public string GetTextString(Encoding? encoding = null)
    {
        if (pumpDecodedChars is not null)
            return pumpDecodedChars.Length == 0 ? string.Empty : new string(pumpDecodedChars);

        encoding ??= outputEncoding;
        return encoding.GetString(Output.Span);
    }

    /// <summary>
    /// Determines whether decoded output contains <paramref name="value"/>.
    /// </summary>
    /// <param name="value">Substring to locate after decoding.</param>
    /// <param name="comparison">String comparison rules.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> occurs in decoded text.</returns>
    /// <remarks>Uses <see cref="GetText"/> internally. Cache <see cref="GetText"/> if you perform many checks.</remarks>
    public bool Contains(string value, StringComparison comparison = StringComparison.Ordinal)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        return GetText().Span.Contains(value, comparison);
    }

    /// <summary>
    /// Determines whether raw <see cref="Output"/> contains the given byte pattern (zero decode, zero allocation beyond the call).
    /// </summary>
    /// <param name="pattern">Raw bytes to locate.</param>
    /// <returns><see langword="true"/> when <paramref name="pattern"/> occurs in <see cref="Output"/>.</returns>
    public bool Contains(ReadOnlySpan<byte> pattern) =>
        PtyOutputHelpers.ContainsBytes(Output.Span, pattern);

    /// <summary>
    /// Determines whether raw <see cref="Output"/> contains a UTF-8 string without full stream decoding.
    /// </summary>
    /// <param name="utf8Text">UTF-8 text to locate.</param>
    /// <param name="comparison">
    /// String comparison rules. Only <see cref="StringComparison.Ordinal"/> avoids full decoding.
    /// </param>
    /// <returns><see langword="true"/> when <paramref name="utf8Text"/> occurs in <see cref="Output"/>.</returns>
    public bool ContainsUtf8(string utf8Text, StringComparison comparison = StringComparison.Ordinal) =>
        PtyOutputHelpers.ContainsUtf8(Output.Span, utf8Text, comparison);
}
