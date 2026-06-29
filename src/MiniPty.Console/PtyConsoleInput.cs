using MiniPty.Console.Internal;

namespace MiniPty.Console;

/// <summary>
/// Attaches the host terminal to a <see cref="PtySession"/> for interactive input and resize sync.
/// </summary>
/// <remarks>
/// Does not read PTY output. Embedders remain the sole output consumer via <see cref="PtySession.ReadOutputAsync"/>.
/// On Windows, the embedder must call <see cref="PtyConsoleInputHandle.PumpInputOnce"/> from the attach thread
/// (typically in a loop until the session exits).
/// </remarks>
public static class PtyConsoleInput
{
    /// <summary>
    /// Configures the host terminal, forwards raw stdin bytes to the PTY, and syncs host resize events.
    /// </summary>
    /// <param name="session">A running session from <see cref="Pty.Start"/>.</param>
    /// <returns>A handle that stops the input pump and restores the host terminal on <see cref="IDisposable.Dispose"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Host stdin or stdout is not a terminal, or a console attach is already active.</exception>
    /// <exception cref="ObjectDisposedException"><paramref name="session"/> is disposed.</exception>
    public static PtyConsoleInputHandle Attach(PtySession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (!HostTerminal.IsInteractiveHost())
        {
            throw new InvalidOperationException(
                "Host stdin and stdout must be an interactive terminal.");
        }

        PtyConsoleAttach.Register(session);
        try
        {
            return new PtyConsoleInputHandle(new PtyConsoleAttach(session));
        }
        catch
        {
            PtyConsoleAttach.Unregister(session);
            throw;
        }
    }
}
