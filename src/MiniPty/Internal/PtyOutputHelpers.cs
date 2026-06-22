using System.Buffers;
using System.Text;

namespace MiniPty.Internal;

/// <summary>
/// UTF-8/byte pattern search and on-demand text decoding helpers shared by result types.
/// </summary>
internal static class PtyOutputHelpers
{
    private const int Utf8PatternStackThreshold = 256;

    /// <summary>
    /// Locates a UTF-8 byte pattern in <paramref name="output"/> without decoding the full stream.
    /// </summary>
    internal static bool ContainsUtf8(ReadOnlySpan<byte> output, string utf8Text, StringComparison comparison)
    {
        if (utf8Text.Length == 0)
            return false;

        if (comparison is not StringComparison.Ordinal)
        {
            // Non-ordinal comparisons require decoded text; callers use GetText() + Contains on chars instead.
            return Encoding.UTF8.GetString(output).Contains(utf8Text, comparison);
        }

        var byteCount = Encoding.UTF8.GetByteCount(utf8Text);
        if (byteCount == 0)
            return false;

        if (byteCount <= Utf8PatternStackThreshold)
        {
            Span<byte> pattern = stackalloc byte[byteCount];
            Encoding.UTF8.GetBytes(utf8Text, pattern);
            return output.IndexOf(pattern) >= 0;
        }

        var rented = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            var written = Encoding.UTF8.GetBytes(utf8Text, rented.AsSpan(0, byteCount));
            return output.IndexOf(rented.AsSpan(0, written)) >= 0;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Locates raw bytes in PTY output.
    /// </summary>
    internal static bool ContainsBytes(ReadOnlySpan<byte> output, ReadOnlySpan<byte> pattern) =>
        !pattern.IsEmpty && output.IndexOf(pattern) >= 0;

    /// <summary>
    /// Decodes <paramref name="output"/> to a new char array (allocates every call).
    /// </summary>
    internal static ReadOnlyMemory<char> DecodeToNewChars(ReadOnlySpan<byte> output, Encoding encoding)
    {
        if (output.IsEmpty)
            return ReadOnlyMemory<char>.Empty;

        var charCount = encoding.GetCharCount(output);
        if (charCount == 0)
            return ReadOnlyMemory<char>.Empty;

        var chars = new char[charCount];
        encoding.GetChars(output, chars);
        return chars.AsMemory();
    }
}
