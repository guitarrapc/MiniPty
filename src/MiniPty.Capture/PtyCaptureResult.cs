namespace MiniPty.Capture;

/// <summary>
/// Result of a full <see cref="PtyCapture.RunAsync"/> run.
/// </summary>
/// <param name="Output">
/// Merged stdout and stderr text, equivalent to concatenating all <see cref="Chunks"/> data in order.
/// </param>
/// <param name="ExitCode">Operating-system exit code of the child process.</param>
/// <param name="Chunks">
/// Timestamped output slices recorded during the session. Times are elapsed since spawn.
/// </param>
public sealed record PtyCaptureResult(
    string Output,
    int ExitCode,
    IReadOnlyList<PtyCaptureChunk> Chunks);
