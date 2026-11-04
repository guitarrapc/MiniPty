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
    /// The first Windows version with ConPTY: Windows 10 version 1809 and Windows Server 2019 (build 17763).
    /// </summary>
    private static readonly Version MinimumWindowsVersion = new(10, 0, 17763);

    /// <summary>
    /// Gets a value indicating whether the current operating system supports pseudo-terminals.
    /// </summary>
    /// <value>
    /// <see langword="true"/> on Windows 10 version 1809 (build 17763) or later, Windows Server 2019 or later, Linux,
    /// macOS, and FreeBSD. Otherwise <see langword="false"/>.
    /// </value>
    /// <remarks>
    /// <para>
    /// An out-of-band ConPTY (<see cref="PtyStartInfo.ConPty"/>) does not change the answer.
    /// </para>
    /// <para>
    /// FreeBSD support is experimental: it is not tested, and the package ships no FreeBSD native library, so
    /// <see cref="Start"/> works there only when MiniPty is built from source. This property reports whether the
    /// operating system has pseudo-terminals MiniPty can drive, not whether the native library is present.
    /// </para>
    /// </remarks>
    public static bool IsSupported =>
        OperatingSystem.IsWindows()
            ? IsSupportedWindowsVersion(Environment.OSVersion.Version)
            : OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD();

    /// <summary>
    /// Whether a Windows version has ConPTY. Separate from <see cref="IsSupported"/> so tests can pin the build.
    /// </summary>
    internal static bool IsSupportedWindowsVersion(Version version) => version >= MinimumWindowsVersion;

    /// <summary>
    /// Spawns a child process attached to a new pseudo-terminal.
    /// </summary>
    /// <param name="startInfo">Executable, arguments, working directory, and initial terminal size.</param>
    /// <returns>
    /// A <see cref="PtySession"/> that owns the child process and PTY handles. Does not wait for exit.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="startInfo"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Both <see cref="PtyStartInfo.CommandLine"/> and <see cref="PtyStartInfo.Arguments"/> are set.
    /// <see cref="PtyStartInfo.FileName"/>, an element of <see cref="PtyStartInfo.Arguments"/>, or
    /// <see cref="PtyStartInfo.WorkingDirectory"/> contains a NUL, or on Windows <see cref="PtyStartInfo.CommandLine"/>
    /// does. On Windows, a batch file started without a raw <see cref="PtyStartInfo.CommandLine"/> has a carriage
    /// return or a line feed in its name or an argument, a quote in its name, or a <c>cmd.exe</c> command line over
    /// 8191 characters. See Batch Files (Windows), <c>.github/docs/specs/core_session.md</c>.
    /// </exception>
    /// <exception cref="PlatformNotSupportedException">
    /// <see cref="IsSupported"/> is <see langword="false"/>, as on Windows before Windows 10 version 1809. Also when
    /// <see cref="PtyStartInfo.CommandLine"/> is not <see langword="null"/> on an operating system other than Windows,
    /// even when it is empty or contains a NUL.
    /// </exception>
    /// <exception cref="System.ComponentModel.Win32Exception">
    /// The program cannot be executed, or <see cref="PtyStartInfo.WorkingDirectory"/> cannot be entered. On Windows,
    /// also when ConPTY or the process cannot be created. When MiniPty looks a name up itself and finds no file, or a
    /// directory first, the start fails before anything is created, with <c>ERROR_FILE_NOT_FOUND</c> or
    /// <c>ERROR_ACCESS_DENIED</c>. A batch file that MiniPty cannot open, or an empty one, fails there too, with the
    /// error <c>CreateProcess</c> would report for it. Program Lookup (Windows) and Batch Files (Windows) in
    /// <c>.github/docs/specs/core_session.md</c> say which names MiniPty looks up. On Unix,
    /// <see cref="System.ComponentModel.Win32Exception.NativeErrorCode"/> is the <c>errno</c>, such as <c>ENOENT</c> or
    /// <c>EACCES</c>, and the child has already been reaped, so nothing the start created remains.
    /// </exception>
    /// <exception cref="IOException">
    /// Unix: the pseudo-terminal could not be opened or set up, or the child could not be started for a reason other
    /// than those above.
    /// </exception>
    public static PtySession Start(PtyStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (startInfo.CommandLine is not null && startInfo.Arguments.Count != 0)
            throw new ArgumentException("CommandLine and Arguments cannot both be specified.", nameof(startInfo));
        if (startInfo.CommandLine is not null && !OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("PtyStartInfo.CommandLine is supported only on Windows.");
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException(OperatingSystem.IsWindows()
                ? $"PTY on Windows needs ConPTY, which Windows provides from Windows 10 version 1809 (build {MinimumWindowsVersion.Build}); this is build {Environment.OSVersion.Version.Build}."
                : "PTY is not supported on this operating system.");
        }

        ThrowIfContainsNul(startInfo);

        var started = PtyMetrics.StartTimestamp();
        PtySession session;
        try
        {
            IPtyBackend backend;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                backend = WindowsPtyBackend.Start(startInfo);
            else
                backend = UnixPtyBackend.Start(startInfo);

            session = new PtySession(backend);
        }
        catch (Exception error) when (PtyMetrics.RecordStartFailure(error))
        {
            // Not reached: the filter only counts the failure and lets the exception propagate untouched.
            throw;
        }

        try
        {
            PtyMetrics.RecordStart(started);
        }
        catch
        {
            // A listener that throws here would otherwise leave a running child that no caller holds.
            session.Dispose();
            throw;
        }

        return session;
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
