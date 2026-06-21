namespace MiniPty;

/// <summary>Terminal dimensions in character cells.</summary>
/// <param name="Columns">Width in character cells.</param>
/// <param name="Rows">Height in character cells.</param>
public readonly record struct PtySize(int Columns, int Rows);
