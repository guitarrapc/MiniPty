namespace MiniPty;

/// <summary>
/// Terminal dimensions measured in character cells (not pixels).
/// </summary>
/// <param name="Columns">Width in character columns.</param>
/// <param name="Rows">Height in character rows.</param>
/// <remarks>
/// <see cref="PtyStartInfo.Size"/> and <see cref="PtySession.Resize(PtySize)"/> clamp each dimension to the
/// platform's limit, 1–32767 on Windows and 1–65535 on Unix, so a zero-sized window before layout or an
/// oversized grid never faults; <see cref="PtySession.Size"/> reports the value applied.
/// </remarks>
public readonly record struct PtySize(int Columns, int Rows);
