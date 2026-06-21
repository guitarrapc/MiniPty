namespace MiniPty;

/// <summary>
/// Merged PTY output and child exit code returned by <see cref="PtySession.CompleteAsync"/>.
/// </summary>
/// <param name="Output">Merged stdout and stderr text decoded from the PTY byte stream.</param>
/// <param name="ExitCode">Operating-system exit code of the child process.</param>
public sealed record PtyResult(string Output, int ExitCode);
