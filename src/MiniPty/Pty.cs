using System.Runtime.InteropServices;
using MiniPty.Internal;

namespace MiniPty;

/// <summary>
/// Entry point for spawning cross-platform pseudo-terminal sessions.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Start"/> validates the request, selects the platform backend (ConPTY on Windows, Unix PTY
/// elsewhere), spawns the child, and returns a <see cref="PtySession"/>. It does not wait for exit.
/// Launch options are declarative <see cref="PtyStartInfo"/> data; live I/O and teardown belong to
/// <see cref="PtySession"/>.
/// </para>
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
    /// <remarks>
    /// FreeBSD support is experimental: it is not tested, and the package ships no FreeBSD native library, so
    /// <see cref="Start"/> works there only when MiniPty is built from source. This property reports whether the
    /// operating system has pseudo-terminals MiniPty can drive, not whether the native library is present.
    /// </remarks>
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
    /// <exception cref="ArgumentException">
    /// Both <see cref="PtyStartInfo.CommandLine"/> and <see cref="PtyStartInfo.Arguments"/> are set. On every platform,
    /// <see cref="PtyStartInfo.FileName"/>, an element of <see cref="PtyStartInfo.Arguments"/>,
    /// <see cref="PtyStartInfo.WorkingDirectory"/>, or <see cref="PtyStartInfo.CommandLine"/> contains a NUL. On Windows,
    /// also for a batch file started without a raw <see cref="PtyStartInfo.CommandLine"/>: when its name contains a quote,
    /// a carriage return, a line feed, or a NUL, when one of its arguments contains a carriage return, a line feed, or
    /// a NUL, or when its <c>cmd.exe</c> command line would exceed 8191 characters.
    /// </exception>
    /// <exception cref="PlatformNotSupportedException">The current operating system is not supported.</exception>
    /// <exception cref="System.ComponentModel.Win32Exception">
    /// Windows: process or ConPTY creation failed. For a batch file this includes a script that MiniPty's lookup does
    /// not find, or cannot open, before anything is created, with the error <c>CreateProcess</c> would report; the
    /// same holds for a program MiniPty looks up itself, when <see cref="PtyStartInfo.Environment"/> sets or removes
    /// <c>PATH</c>.
    /// Unix: the child could not enter <see cref="PtyStartInfo.WorkingDirectory"/> or execute the program, for example
    /// because it does not exist (<c>ENOENT</c>) or may not be executed (<c>EACCES</c>).
    /// <see cref="System.ComponentModel.Win32Exception.NativeErrorCode"/> is the <c>errno</c>. The child has already
    /// been reaped, and nothing the start created remains.
    /// </exception>
    /// <exception cref="IOException">Unix: opening the pseudo-terminal or forking failed.</exception>
    public static PtySession Start(PtyStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (startInfo.CommandLine is not null && startInfo.Arguments.Count != 0)
            throw new ArgumentException("CommandLine and Arguments cannot both be specified.", nameof(startInfo));
        if (startInfo.CommandLine is not null && !OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("PtyStartInfo.CommandLine is supported only on Windows.");
        if (!IsSupported)
            throw new PlatformNotSupportedException("PTY is not supported on this operating system.");

        ThrowIfContainsNul(startInfo);

        IPtyBackend backend;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            backend = WindowsPtyBackend.Start(startInfo);
        else
            backend = UnixPtyBackend.Start(startInfo);

        return new PtySession(backend);
    }

    /// <summary>
    /// Rejects a NUL in the strings that say what to start and where. The operating system receives each of them as a
    /// NUL-terminated string, so a NUL ends it early. The program started, its arguments, or its working directory would
    /// then not be the ones the string names, and a caller that checked the string would have checked something else.
    /// Measured before this check: on Windows <c>FileName = "...\cmd.exe\0C:\allowed\safe.exe"</c> started
    /// <c>cmd.exe</c> without the arguments that followed, and on Linux <c>"/bin/sh\0/usr/bin/safe"</c> started
    /// <c>/bin/sh</c>, while <see cref="Path.GetFileName(string)"/> read <c>safe.exe</c> and <c>safe</c>.
    /// </summary>
    /// <remarks>
    /// The environment and <see cref="PtyStartInfo.TerminalName"/> are checked where the environment block is built.
    /// </remarks>
    private static void ThrowIfContainsNul(PtyStartInfo startInfo)
    {
        if (startInfo.FileName.Contains('\0'))
            throw NulInStartInfo(nameof(PtyStartInfo.FileName));

        var arguments = startInfo.Arguments;
        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i] is { } argument && argument.Contains('\0'))
                throw NulInStartInfo(nameof(PtyStartInfo.Arguments));
        }

        if (startInfo.WorkingDirectory is { } workingDirectory && workingDirectory.Contains('\0'))
            throw NulInStartInfo(nameof(PtyStartInfo.WorkingDirectory));
        if (startInfo.CommandLine is { } commandLine && commandLine.Contains('\0'))
            throw NulInStartInfo(nameof(PtyStartInfo.CommandLine));
    }

    private static ArgumentException NulInStartInfo(string member) =>
        new($"PtyStartInfo.{member} cannot contain a NUL character: the operating system reads it as the end of the string, so what started would not be what the string names.", "startInfo");
}
