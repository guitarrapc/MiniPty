namespace MiniPty.Console;

/// <summary>
/// Observes host keyboard bytes forwarded to the PTY during an active
/// <see cref="PtyConsoleInput.Attach(MiniPty.PtySession, PtyConsoleAttachOptions)"/> session.
/// </summary>
/// <remarks>
/// Implementations receive the same raw bytes written to <see cref="PtySession.Input"/>.
/// Observers may see passwords and other sensitive keystrokes; embedders must handle data accordingly.
/// </remarks>
public interface IPtyConsoleInputObserver
{
    /// <summary>
    /// Called immediately before bytes are written to the PTY input stream.
    /// </summary>
    /// <param name="elapsed">Elapsed time since attach started.</param>
    /// <param name="data">Raw host input bytes about to be forwarded.</param>
    void OnForwardedInput(TimeSpan elapsed, ReadOnlySpan<byte> data);
}
