using System.Buffers;
using System.Text;
using MiniPty.Internal;

namespace MiniPty;

/// <summary>
/// Optional helpers for turning decoded PTY text into host-displayable strings.
/// </summary>
/// <remarks>
/// <para>
/// PTY backends and capture APIs return a <strong>raw</strong> terminal byte stream.
/// Writing that stream directly to the parent console can trigger screen clears and other
/// control effects. Use <see cref="ToDisplayText"/> when the goal is logging or readable output.
/// </para>
/// <para>
/// Processing is best-effort and not a full terminal emulator. Recording and replay tools
/// should keep raw output instead.
/// </para>
/// </remarks>
public static class PtyOutput
{
    /// <summary>
    /// Transforms decoded PTY text for display on the host using the given mode.
    /// </summary>
    /// <param name="text">Decoded PTY output text.</param>
    /// <param name="mode">Display transformation to apply.</param>
    /// <returns>Displayable text for the chosen mode.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public static string ToDisplayText(string text, PtyOutputDisplayMode mode)
    {
        ArgumentNullException.ThrowIfNull(text);
        return mode == PtyOutputDisplayMode.Raw
            ? text
            : text.Length == 0
                ? text
                : PtyDisplayTextStripper.Strip(text, mode);
    }

    /// <summary>
    /// Transforms decoded PTY text for display on the host using the given mode.
    /// </summary>
    /// <param name="text">Decoded PTY output text.</param>
    /// <param name="mode">Display transformation to apply.</param>
    /// <returns>Displayable text for the chosen mode.</returns>
    public static string ToDisplayText(ReadOnlySpan<char> text, PtyOutputDisplayMode mode)
    {
        if (text.IsEmpty)
            return string.Empty;

        return mode == PtyOutputDisplayMode.Raw
            ? text.ToString()
            : PtyDisplayTextStripper.Strip(text, mode);
    }

    /// <summary>
    /// Transforms decoded PTY text for display on the host using the given mode.
    /// </summary>
    /// <param name="text">Decoded PTY output text.</param>
    /// <param name="mode">Display transformation to apply.</param>
    /// <returns>Displayable text for the chosen mode.</returns>
    public static string ToDisplayText(ReadOnlyMemory<char> text, PtyOutputDisplayMode mode) =>
        ToDisplayText(text.Span, mode);

    /// <summary>
    /// Decodes raw PTY output bytes and transforms them for display on the host.
    /// </summary>
    /// <param name="bytes">Raw PTY output bytes.</param>
    /// <param name="encoding">Encoding used by the child process terminal stream.</param>
    /// <param name="mode">Display transformation to apply.</param>
    /// <returns>Displayable text for the chosen mode.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="encoding"/> is <see langword="null"/>.</exception>
    public static string ToDisplayText(ReadOnlySpan<byte> bytes, Encoding encoding, PtyOutputDisplayMode mode)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        if (bytes.IsEmpty)
            return string.Empty;

        if (mode == PtyOutputDisplayMode.Raw)
            return encoding.GetString(bytes);

        var charCount = encoding.GetCharCount(bytes);
        if (charCount == 0)
            return string.Empty;

        var pool = ArrayPool<char>.Shared;
        var chars = pool.Rent(charCount);
        try
        {
            var written = encoding.GetChars(bytes, chars);
            return PtyDisplayTextStripper.Strip(chars.AsSpan(0, written), mode);
        }
        finally
        {
            pool.Return(chars);
        }
    }

    /// <summary>
    /// Decodes raw PTY output bytes and transforms them for display on the host.
    /// </summary>
    /// <param name="bytes">Raw PTY output bytes.</param>
    /// <param name="encoding">Encoding used by the child process terminal stream.</param>
    /// <param name="mode">Display transformation to apply.</param>
    /// <returns>Displayable text for the chosen mode.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="encoding"/> is <see langword="null"/>.</exception>
    public static string ToDisplayText(ReadOnlyMemory<byte> bytes, Encoding encoding, PtyOutputDisplayMode mode) =>
        ToDisplayText(bytes.Span, encoding, mode);
}
