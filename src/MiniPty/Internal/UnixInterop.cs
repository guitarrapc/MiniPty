using System.Runtime.InteropServices;

namespace MiniPty.Internal;

internal static partial class UnixInterop
{
    internal const int EINTR = 4;
    internal const int EIO = 5;
    internal const int EBADF = 9;
    internal const int ECHILD = 10;
    internal const int EPIPE = 32;
    // EAGAIN is 11 on Linux and 35 on macOS and FreeBSD.
    internal static readonly int EAGAIN = OperatingSystem.IsLinux() ? 11 : 35;
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

    // PTY master setup (minipty_unix_output.c).
    [LibraryImport("minipty_unix", SetLastError = true)]
    internal static partial int minipty_set_nonblocking(int fd);

    // Child signaling (minipty_unix_exit.c).

    /// <summary>
    /// Opens a pidfd for <paramref name="pid"/> on Linux 5.3+. Returns -1 elsewhere or when the kernel refuses, and -1
    /// with ECHILD when the pidfd names no child of this process, which Linux 5.4 and later can check.
    /// </summary>
    [LibraryImport("minipty_unix", SetLastError = true)]
    internal static partial int minipty_pidfd_open(int pid);

    /// <summary>
    /// Sends <paramref name="signal"/> to a child of this process: through <paramref name="pidfd"/> when it is not
    /// negative, and otherwise to <paramref name="pid"/> only while that pid is still this process's unreaped child.
    /// Returns 0 when the signal was sent, and -1 when the child is gone or the pid is no longer this process's child.
    /// </summary>
    [LibraryImport("minipty_unix", SetLastError = true)]
    internal static partial int minipty_signal_child(int pidfd, int pid, int signal);

    /// <summary>
    /// Reaps a child of this process once it has exited: through <paramref name="pidfd"/> when it is not negative and
    /// the kernel can wait on a pidfd (Linux 5.4 and later), and otherwise by <paramref name="pid"/>. A nonzero
    /// <paramref name="block"/> waits for the exit. Returns 1 with the exit code (128 plus the signal for a signaled
    /// child) and the terminating signal (0 for none), 0 while the child runs, and -1 on failure, with ECHILD when code
    /// outside MiniPty already reaped it.
    /// </summary>
    [LibraryImport("minipty_unix", SetLastError = true)]
    internal static partial int minipty_reap_child(int pidfd, int pid, int block, out int exitCode, out int termSignal);

}
