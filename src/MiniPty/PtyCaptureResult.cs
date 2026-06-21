namespace MiniPty;

/// <summary>Result of <see cref="Pty.Run"/> — merged PTY output and timestamped chunks.</summary>
/// <param name="Output">Merged stdout/stderr text from the PTY.</param>
/// <param name="ExitCode">Child process exit code.</param>
/// <param name="Chunks">Timestamped output slices recorded during the run.</param>
public readonly record struct PtyCaptureResult(
    string Output,
    int ExitCode,
    IReadOnlyList<PtyOutputChunk> Chunks);
