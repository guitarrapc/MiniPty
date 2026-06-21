namespace MiniPty;

/// <summary>
/// Terminal dimensions measured in character cells (not pixels).
/// </summary>
/// <param name="Columns">Width in character columns. Must be positive when passed to <see cref="PtySession.Resize"/>.</param>
/// <param name="Rows">Height in character rows. Must be positive when passed to <see cref="PtySession.Resize"/>.</param>
public readonly record struct PtySize(int Columns, int Rows);
