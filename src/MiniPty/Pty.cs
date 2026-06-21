using System.Runtime.InteropServices;
using MiniPty.Internal;

namespace MiniPty;

/// <summary>Cross-platform pseudo-terminal factory.</summary>
public static class Pty
{
    /// <summary>Whether the current operating system supports pseudo-terminals.</summary>
    public static bool IsSupported =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ||
        RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD);

    /// <summary>Spawns a child in a new pseudo-terminal. Does not wait for exit.</summary>
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
