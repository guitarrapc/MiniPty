namespace MiniPty.Capture;

/// <summary>Result of <see cref="PtyCapture.RunAsync"/>.</summary>
/// <param name="Output">Merged stdout/stderr text from the PTY.</param>
/// <param name="ExitCode">Child process exit code.</param>
/// <param name="Chunks">Timestamped output slices recorded during the run.</param>
public sealed record PtyCaptureResult(
    string Output,
    int ExitCode,
    IReadOnlyList<PtyCaptureChunk> Chunks);
