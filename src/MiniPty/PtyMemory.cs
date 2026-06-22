using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;

namespace MiniPty;

/// <summary>
/// Convenience helpers for working with <see cref="ReadOnlyMemory{T}"/> PTY output buffers.
/// </summary>
public static class PtyMemory
{
    /// <summary>
    /// Materializes decoded PTY text as a <see cref="string"/>.
    /// </summary>
    /// <param name="text">Decoded PTY output text.</param>
    /// <returns>A string containing the same characters as <paramref name="text"/>.</returns>
    public static string ToString(ReadOnlyMemory<char> text) =>
        text.IsEmpty ? string.Empty : text.ToString();

    /// <summary>
    /// Materializes raw PTY output bytes as a <see cref="string"/> using the given encoding.
    /// </summary>
    /// <param name="bytes">Raw PTY output bytes.</param>
    /// <param name="encoding">Encoding used by the child process terminal stream.</param>
    /// <returns>Decoded text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="encoding"/> is <see langword="null"/>.</exception>
    public static string ToString(ReadOnlyMemory<byte> bytes, Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        return bytes.IsEmpty ? string.Empty : encoding.GetString(bytes.Span);
    }

    /// <summary>
    /// Determines whether decoded PTY text contains the specified value.
    /// </summary>
    /// <param name="text">Decoded PTY output text.</param>
    /// <param name="value">Substring to locate.</param>
    /// <param name="comparison">String comparison rules.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> occurs in <paramref name="text"/>.</returns>
    public static bool Contains(ReadOnlyMemory<char> text, string value, StringComparison comparison = StringComparison.Ordinal) =>
        !string.IsNullOrEmpty(value) && text.Span.Contains(value, comparison);

    /// <summary>
    /// Determines whether raw PTY output bytes decode to text containing the specified value.
    /// </summary>
    /// <param name="bytes">Raw PTY output bytes.</param>
    /// <param name="value">Substring to locate after decoding.</param>
    /// <param name="encoding">Encoding used by the child process terminal stream.</param>
    /// <param name="comparison">String comparison rules.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> occurs in decoded text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="encoding"/> is <see langword="null"/>.</exception>
    public static bool Contains(ReadOnlyMemory<byte> bytes, string value, Encoding encoding, StringComparison comparison = StringComparison.Ordinal)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        if (string.IsNullOrEmpty(value))
            return false;

        return encoding.GetString(bytes.Span).Contains(value, comparison);
    }

    /// <summary>
    /// Attempts to treat chunk spans as contiguous slices of one backing string.
    /// </summary>
    /// <param name="chunks">Text chunk spans to inspect.</param>
    /// <param name="text">Contiguous text span when all chunks share one backing string.</param>
    /// <returns><see langword="true"/> when the chunks form one contiguous span.</returns>
    public static bool TryGetContiguousText(IReadOnlyList<ReadOnlyMemory<char>> chunks, out ReadOnlySpan<char> text)
    {
        text = default;
        if (chunks.Count == 0)
            return false;

        if (!MemoryMarshal.TryGetString(chunks[0], out var str, out var start, out var length))
            return false;

        var end = start + length;
        for (var i = 1; i < chunks.Count; i++)
        {
            if (!MemoryMarshal.TryGetString(chunks[i], out var other, out var otherStart, out var otherLength)
                || !ReferenceEquals(str, other))
                return false;

            if (otherStart != end)
                return false;

            end += otherLength;
        }

        text = str.AsSpan(start, end - start);
        return true;
    }
}
