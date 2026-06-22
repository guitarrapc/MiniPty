namespace MiniPty;

/// <summary>
/// Merged PTY output and child exit code returned by <see cref="PtySession.CompleteAsync"/>.
/// </summary>
/// <param name="OutputBytes">Merged stdout and stderr bytes from the PTY master output stream.</param>
/// <param name="Output">
/// Text decoded from <paramref name="OutputBytes"/> using <see cref="PtyCompleteOptions.OutputEncoding"/>.
/// Empty when <see cref="PtyCompleteOptions.DecodeOutput"/> is <see langword="false"/>.
/// </param>
/// <param name="ExitCode">Operating-system exit code of the child process.</param>
public sealed record PtyResult(ReadOnlyMemory<byte> OutputBytes, ReadOnlyMemory<char> Output, int ExitCode);
