using System.Runtime.InteropServices;

namespace MiniPty.Internal;

internal static partial class UnixInterop
{
    internal const int EINTR = 4;
    internal const int EIO = 5;
    internal const int EBADF = 9;
    internal const int ECHILD = 10;
    internal const int EPIPE = 32;
    internal const int WaitNoHang = 1;
    internal const int SigHup = 1;
    internal const int SigInt = 2;
    internal const int SigQuit = 3;
    internal const int SigKill = 9;
    internal const int SigTerm = 15;
    // SIGUSR1/SIGUSR2 numbering differs: Linux uses 10/12, macOS and FreeBSD use BSD 30/31.
    internal const int SigUsr1Linux = 10;
    internal const int SigUsr2Linux = 12;
    internal const int SigUsr1Bsd = 30;
    internal const int SigUsr2Bsd = 31;

    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    internal static unsafe partial int Read(int fd, byte* buf, nuint count);

    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    internal static unsafe partial int Write(int fd, byte* buf, nuint count);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int waitpid(int pid, out int status, int options);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int kill(int pid, int sig);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int tcdrain(int fd);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int close(int fd);

    [LibraryImport("minipty_unix", SetLastError = true)]
    internal static unsafe partial int minipty_peek_readable_bytes(int fd, int* bytes_available);

    [LibraryImport("minipty_unix", SetLastError = true)]
    internal static unsafe partial int minipty_try_read(int fd, byte* buf, uint count, int* bytes_read, int* is_eof);
}
