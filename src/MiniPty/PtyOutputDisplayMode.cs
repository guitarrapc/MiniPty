namespace MiniPty;

/// <summary>
/// Controls how decoded PTY text is transformed for display on the host.
/// </summary>
public enum PtyOutputDisplayMode
{
    /// <summary>
    /// Return the input unchanged.
    /// </summary>
    Raw,

    /// <summary>
    /// Remove terminal control sequences and normalize line endings for plain-text logs.
    /// </summary>
    PlainText,

    /// <summary>
    /// Remove layout and mode sequences but keep SGR color/style attributes.
    /// </summary>
    AnsiText,
}
