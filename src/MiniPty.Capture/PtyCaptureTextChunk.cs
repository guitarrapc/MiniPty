namespace MiniPty.Capture;

/// <summary>
/// A single timestamped slice of decoded PTY text aligned to one read from the master output stream.
/// </summary>
/// <param name="Time">
/// Elapsed time since the capture session started (immediately after <see cref="Pty.Start"/>).
/// </param>
/// <param name="Text">Text decoded from the bytes read in this slice.</param>
/// <remarks>
/// Produced by <see cref="PtyCaptureResult.GetTextChunks"/> when output was decoded during capture,
/// or by on-demand decoding when <see cref="PtyCompleteOptions.DecodeOutput"/> was <see langword="false"/>.
/// </remarks>
public readonly record struct PtyCaptureTextChunk(TimeSpan Time, ReadOnlyMemory<char> Text);
