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
            : PtyDisplayTextStripper.Strip(text, mode);
    }
}
