namespace MiniPty;

/// <summary>
/// Signals that can be sent to the PTY child with <see cref="PtySession.Kill(PtySignal)"/>.
/// </summary>
/// <remarks>
/// Members are logical identifiers, not raw OS numbers: on Unix each member is mapped to the
/// platform's native signal number internally (SIGUSR1/SIGUSR2 numbering differs between Linux
/// and macOS/FreeBSD). Only defined members are accepted. On Windows the selected signal is
/// advisory; every defined member terminates the child, matching node-pty semantics.
/// </remarks>
public enum PtySignal
{
    /// <summary>SIGHUP: terminal hangup. node-pty's default kill signal.</summary>
    Hangup = 1,

    /// <summary>SIGINT: interrupt, Ctrl+C semantics.</summary>
    Interrupt = 2,

    /// <summary>SIGQUIT: quit.</summary>
    Quit = 3,

    /// <summary>SIGKILL: forced, unhandleable termination. Same effect as <see cref="PtySession.Kill()"/>.</summary>
    Kill = 9,

    /// <summary>SIGUSR1: user-defined signal 1. Native number differs per OS.</summary>
    User1 = 10,

    /// <summary>SIGUSR2: user-defined signal 2. Native number differs per OS.</summary>
    User2 = 12,

    /// <summary>SIGTERM: graceful termination request.</summary>
    Terminate = 15,
}
