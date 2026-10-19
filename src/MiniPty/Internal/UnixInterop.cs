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

    // Output reader wake descriptors and PTY master readiness (minipty_unix_output.c).
    internal const int PtyReadable = 1;
    internal const int PtyWoken = 2;
    internal const int PtyWritable = 4;

    [LibraryImport("minipty_unix", SetLastError = true)]
    internal static unsafe partial int minipty_wake_open(int* readFd, int* writeFd);

    [LibraryImport("minipty_unix", SetLastError = true)]
    internal static partial int minipty_wake_signal(int writeFd);

    [LibraryImport("minipty_unix")]
    internal static partial void minipty_wake_drain(int readFd);

    [LibraryImport("minipty_unix")]
    internal static partial void minipty_wake_close(int readFd, int writeFd);

    [LibraryImport("minipty_unix", SetLastError = true)]
    internal static partial int minipty_set_nonblocking(int fd);

    // Child signaling (minipty_unix_exit.c).

    /// <summary>Opens a pidfd for <paramref name="pid"/> on Linux 5.3+. Returns -1 elsewhere or when the kernel refuses.</summary>
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
    /// Waits until <paramref name="master"/> is ready for the requested <paramref name="events"/>
    /// (<see cref="PtyReadable"/>, <see cref="PtyWritable"/>; hang-up and errors count as ready),
    /// <paramref name="wakeFd"/> is signaled, or the timeout elapses. Returns a mask of <see cref="PtyReadable"/>,
    /// <see cref="PtyWoken"/>, and <see cref="PtyWritable"/>, 0 on timeout or interruption, and -1 on failure. A
    /// negative descriptor is left out of the wait.
    /// </summary>
    [LibraryImport("minipty_unix", SetLastError = true)]
    internal static partial int minipty_pty_wait(int master, int events, int wakeFd, int timeoutMs);
}
