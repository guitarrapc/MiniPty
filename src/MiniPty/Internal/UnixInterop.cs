using System.Runtime.InteropServices;

namespace MiniPty.Internal;

internal static partial class UnixInterop
{
    internal const int EINTR = 4;
    internal const int ECHILD = 10;
    internal const int EIO = 5;
    internal const int WaitNoHang = 1;
    internal const int SigKill = 9;

    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    internal static unsafe partial int Read(int fd, byte* buf, nuint count);

    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    internal static unsafe partial int Write(int fd, byte* buf, nuint count);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int waitpid(int pid, out int status, int options);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int kill(int pid, int sig);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int close(int fd);
}
