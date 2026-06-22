namespace MiniPty.Capture;

/// <summary>
/// Result of a full <see cref="PtyCapture.RunAsync"/> run.
/// </summary>
/// <param name="OutputBytes">
/// Merged stdout and stderr bytes, equivalent to concatenating all <see cref="ByteChunks"/> in order.
/// </param>
/// <param name="Output">
/// Text decoded from <paramref name="OutputBytes"/>.
/// Empty when <see cref="PtyCompleteOptions.DecodeOutput"/> is <see langword="false"/>.
/// </param>
/// <param name="ExitCode">Operating-system exit code of the child process.</param>
/// <param name="ByteChunks">Timestamped raw byte slices observed during the session.</param>
/// <param name="Chunks">
/// Timestamped decoded text slices observed during the session. Empty when output decoding is disabled.
/// </param>
public sealed record PtyCaptureResult(
    ReadOnlyMemory<byte> OutputBytes,
    ReadOnlyMemory<char> Output,
    int ExitCode,
    IReadOnlyList<PtyCaptureByteChunk> ByteChunks,
    IReadOnlyList<PtyCaptureChunk> Chunks);
