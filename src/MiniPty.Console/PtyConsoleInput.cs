using MiniPty.Console.Internal;

namespace MiniPty.Console;

/// <summary>
/// Attaches the host terminal to a <see cref="PtySession"/> for interactive input and resize sync.
/// </summary>
/// <remarks>
/// Does not read PTY output. Embedders remain the sole output consumer via <see cref="PtySession.ReadOutputAsync"/>.
/// Call <see cref="PtyConsoleInputHandle.PumpInputUntil"/> linked to session exit while attached.
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
    public static PtyConsoleInputHandle Attach(PtySession session) =>
        Attach(session, new PtyConsoleAttachOptions());

    /// <summary>
    /// Configures the host terminal, forwards raw stdin bytes to the PTY, and optionally syncs host resize events.
    /// </summary>
    /// <param name="session">A running session from <see cref="Pty.Start"/>.</param>
    /// <param name="options">Attach behavior. Set <see cref="PtyConsoleAttachOptions.SyncHostSize"/> to <see langword="false"/> when the PTY must keep a fixed recording geometry.</param>
    /// <returns>A handle that stops the input pump and restores the host terminal on <see cref="IDisposable.Dispose"/>.</returns>
    public static PtyConsoleInputHandle Attach(PtySession session, PtyConsoleAttachOptions options)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);

        if (!HostTerminal.IsInteractiveHost())
        {
            throw new InvalidOperationException(
                "Host stdin and stdout must be an interactive terminal.");
        }

        PtyConsoleAttach.Register(session);
        try
        {
            return new PtyConsoleInputHandle(new PtyConsoleAttach(session, options));
        }
        catch
        {
            PtyConsoleAttach.Unregister(session);
            throw;
        }
    }
}
