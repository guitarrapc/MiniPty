namespace MiniPty;

/// <summary>Result of <see cref="PtySession.CompleteAsync"/>.</summary>
/// <param name="Output">Merged stdout/stderr text from the PTY.</param>
/// <param name="ExitCode">Child process exit code.</param>
public sealed record PtyResult(string Output, int ExitCode);
