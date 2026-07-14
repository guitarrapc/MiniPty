namespace MiniPty;

/// <summary>
/// Optional terminal dimensions measured in pixels for Unix <c>winsize</c> reporting.
/// </summary>
/// <param name="Width">Terminal width in pixels. Must not be negative.</param>
/// <param name="Height">Terminal height in pixels. Must not be negative.</param>
/// <remarks>ConPTY ignores pixel dimensions.</remarks>
public readonly record struct PtyPixelSize(int Width, int Height);
