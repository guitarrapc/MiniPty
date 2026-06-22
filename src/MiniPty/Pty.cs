using System.Runtime.InteropServices;
using MiniPty.Internal;

namespace MiniPty;

/// <summary>
/// Entry point for spawning cross-platform pseudo-terminal sessions.
/// </summary>
/// <remarks>
/// <para>
/// Use <see cref="Start"/> to create a <see cref="PtySession"/>, then read <see cref="PtySession.Output"/>,
/// write <see cref="PtySession.Input"/>, and wait with <see cref="PtySession.WaitForExitAsync"/> or
/// <see cref="PtySession.CompleteAsync"/>.
/// </para>
/// <para>
/// For timestamped PTY output observation, use the <c>MiniPty.Capture</c> package and <c>PtyCapture.RunAsync</c>.
/// </para>
/// </remarks>
public static class Pty
{
    /// <summary>
    /// Gets a value indicating whether the current operating system supports pseudo-terminals.
    /// </summary>
    /// <value>
    /// <see langword="true"/> on Windows 10 1809+, Linux, macOS, and FreeBSD; otherwise <see langword="false"/>.
    /// </value>
    public static bool IsSupported =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ||
        RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD);

    /// <summary>
    /// Spawns a child process attached to a new pseudo-terminal.
    /// </summary>
    /// <param name="startInfo">Executable, arguments, working directory, and initial terminal size.</param>
    /// <returns>A <see cref="PtySession"/> that owns the child process and PTY handles. Does not wait for exit.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="startInfo"/> is <see langword="null"/>.</exception>
    /// <exception cref="PlatformNotSupportedException">The current operating system is not supported.</exception>
    /// <exception cref="System.ComponentModel.Win32Exception">Windows: process or ConPTY creation failed.</exception>
    /// <exception cref="IOException">Unix: <c>openpty</c>, <c>fork</c>, or <c>exec</c> failed.</exception>
    public static PtySession Start(PtyStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (!IsSupported)
            throw new PlatformNotSupportedException("PTY is not supported on this operating system.");

        IPtyBackend backend;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            backend = WindowsPtyBackend.Start(startInfo);
        else
            backend = UnixPtyBackend.Start(startInfo);

        return new PtySession(backend);
    }
}
