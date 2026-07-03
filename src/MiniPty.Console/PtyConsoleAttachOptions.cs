namespace MiniPty.Console;

/// <summary>
/// Options for <see cref="PtyConsoleInput.Attach(MiniPty.PtySession, PtyConsoleAttachOptions)"/>.
/// </summary>
public sealed class PtyConsoleAttachOptions
{
    /// <summary>
    /// When <see langword="true"/> (default), the host terminal size is applied to the PTY on attach
    /// and kept in sync while attached. When <see langword="false"/>, the PTY keeps the size from
    /// <see cref="Pty.Start"/> — useful when the embedder records at a fixed cast geometry.
    /// </summary>
    public bool SyncHostSize { get; init; } = true;

    /// <summary>
    /// Optional observer for host keyboard bytes forwarded to the PTY.
    /// When <see langword="null"/> (default), the input pump adds no observation overhead beyond a null check.
    /// </summary>
    public IPtyConsoleInputObserver? InputObserver { get; init; }

    /// <summary>
    /// Clock used for <see cref="IPtyConsoleInputObserver.OnForwardedInput"/> elapsed times.
    /// </summary>
    /// <value>Default is <see cref="TimeProvider.System"/>.</value>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}
