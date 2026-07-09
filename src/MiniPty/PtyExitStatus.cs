namespace MiniPty;

/// <summary>
/// Exit status of the PTY child: the exit code plus, on Unix, the terminating signal.
/// </summary>
/// <param name="ExitCode">
/// Same value as <see cref="PtySession.ExitCode"/>: the child's exit code, or
/// <c>128 + signal</c> when a Unix child was terminated by a signal (for example 143 for SIGTERM).
/// </param>
/// <param name="Signal">
/// Raw OS signal number that terminated the child (waitpid <c>WTERMSIG</c>), or
/// <see langword="null"/> when the child exited normally or the signal is unknown.
/// Always <see langword="null"/> on Windows. The number is the OS's own value and can differ
/// per platform for uncommon signals.
/// </param>
public readonly record struct PtyExitStatus(int ExitCode, int? Signal);
