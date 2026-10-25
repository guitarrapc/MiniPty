using System.Runtime.InteropServices;
using MiniPty.Internal;

namespace MiniPty.Tests;

/// <summary>
/// The kqueue registration of a PTY master that the Unix loop reads on macOS and FreeBSD, driven through the native
/// watcher calls directly on a PTY pair the test owns (<c>.github/docs/plans/plan_thread_free_io.md</c>, Phases 4 and 6).
/// These are the kqueue behaviors the loop relies on that no session can show: a session never opens its slave again,
/// and its read and write filters are only ever seen through the loop.
/// </summary>
/// <remarks>
/// Each test opens its own watch descriptors, so it never touches the loop's kqueue. epoll differs on purpose (a hung-up
/// master reports once with interest 0, and a slave whose every descriptor closed cannot open again), so Linux is not
/// covered here. Both ends are opened close-on-exec: a child another test starts meanwhile would otherwise keep the slave
/// open, and closing it would not end output. A child forked in the instant before its exec still holds a copy, so a
/// hang-up is waited for rather than expected on the first wait.
/// </remarks>
public sealed partial class PtyKqueueWatchTests
{
    private const int InterestRead = 1;
    private const int InterestWrite = 2;
    private const int ReportReadable = 1;
    private const int ReportWritable = 2;
    private const int ReportHangup = 4;
    private const ulong Token = (1UL << 63) | (7UL << 32) | 3;

    /// <summary>
    /// A master's read and write filters are separate kqueue events, and the wait joins them into one report per master, as
    /// epoll gives, so the loop serves a session once per wait.
    /// </summary>
    [Test]
    public async Task ReadAndWriteReadinessJoinIntoOneReport()
    {
        if (!IsKqueue)
            return;

        using var pty = new PtyPair();
        using var watch = new Watch();
        await Assert.That(minipty_watch_add(watch.Fd, pty.Master, Token, InterestRead)).IsEqualTo(0);
        await Assert.That(watch.Report()).IsEqualTo(0);

        pty.WriteSlave("hi");
        await Assert.That(watch.Report()).IsEqualTo(ReportReadable);
        pty.ReadMaster();

        await Assert.That(minipty_watch_modify(watch.Fd, pty.Master, Token, InterestRead | InterestWrite)).IsEqualTo(0);
        await Assert.That(watch.Report()).IsEqualTo(ReportWritable);

        pty.WriteSlave("x");
        await Assert.That(watch.Report(out var reportsForToken)).IsEqualTo(ReportReadable | ReportWritable);
        await Assert.That(reportsForToken).IsEqualTo(1);
    }

    /// <summary>
    /// Interest 0 disables both filters, so a master the loop parked reports nothing, neither its unread output nor its
    /// hang-up, and enabling read interest again reports at once what arrived meanwhile.
    /// </summary>
    [Test]
    public async Task InterestZeroReportsNothingUntilEnabledAgain()
    {
        if (!IsKqueue)
            return;

        using var pty = new PtyPair();
        using var watch = new Watch();
        await Assert.That(minipty_watch_add(watch.Fd, pty.Master, Token, InterestRead)).IsEqualTo(0);

        pty.WriteSlave("unread");
        await Assert.That(minipty_watch_modify(watch.Fd, pty.Master, Token, 0)).IsEqualTo(0);
        await Assert.That(watch.Report()).IsEqualTo(0);
        await Assert.That(minipty_watch_modify(watch.Fd, pty.Master, Token, InterestRead)).IsEqualTo(0);
        await Assert.That(watch.Report()).IsEqualTo(ReportReadable);
        pty.ReadMaster();

        pty.CloseSlave();
        await Assert.That(watch.WaitForHangup()).IsTrue();
        await Assert.That(minipty_watch_modify(watch.Fd, pty.Master, Token, 0)).IsEqualTo(0);
        await Assert.That(watch.Report()).IsEqualTo(0);
        await Assert.That(minipty_watch_modify(watch.Fd, pty.Master, Token, InterestRead)).IsEqualTo(0);
        await Assert.That(watch.Report() & ReportHangup).IsEqualTo(ReportHangup);
        await Assert.That(pty.ReadMaster()).IsEqualTo(0);
    }

    /// <summary>
    /// kqueue keeps reporting end of file on a registration after the slave closed and opened again, while reads find
    /// nothing; the loop renews such a registration when a hang-up report found nothing to read, which clears it and
    /// keeps reporting output. Without the renewal the loop would spin on that master.
    /// </summary>
    [Test]
    public async Task RenewClearsAnEndOfFileLeftByASlaveOpenedAgain()
    {
        if (!IsKqueue)
            return;

        using var pty = new PtyPair();
        using var watch = new Watch();
        await Assert.That(minipty_watch_add(watch.Fd, pty.Master, Token, InterestRead)).IsEqualTo(0);

        pty.CloseSlave();
        await Assert.That(watch.WaitForHangup()).IsTrue();
        await Assert.That(pty.ReadMaster()).IsEqualTo(0);

        pty.OpenSlave();
        // Stale: the report repeats on every wait while a read finds nothing.
        await Assert.That(watch.Report() & ReportHangup).IsEqualTo(ReportHangup);
        await Assert.That(pty.ReadMaster()).IsEqualTo(-1);
        await Assert.That(watch.Report() & ReportHangup).IsEqualTo(ReportHangup);

        await Assert.That(minipty_watch_renew(watch.Fd, pty.Master, Token, InterestRead)).IsEqualTo(0);
        await Assert.That(watch.Report()).IsEqualTo(0);
        pty.WriteSlave("after");
        await Assert.That(watch.Report()).IsEqualTo(ReportReadable);

        minipty_watch_remove(watch.Fd, pty.Master);
        await Assert.That(watch.Report()).IsEqualTo(0);
    }

    private static bool IsKqueue => OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD();

    /// <summary>A watch of the test's own, opened the way the loop opens its own.</summary>
    private sealed class Watch : IDisposable
    {
        private readonly int _wakeRead;
        private readonly int _wakeWrite;

        public Watch()
        {
            if (minipty_child_watch_open(out var fd, out _wakeRead, out _wakeWrite) != 0)
                throw new IOException($"minipty_child_watch_open failed (errno {Marshal.GetLastPInvokeError()})");

            Fd = fd;
        }

        public int Fd { get; }

        /// <summary>Waits up to 50 ms and returns the report for the test's token, or 0 when there was none.</summary>
        public int Report() => Report(out _);

        /// <summary>Waits up to 2 s for a hang-up report for the test's token.</summary>
        public bool WaitForHangup()
        {
            var deadline = Environment.TickCount64 + 2000;
            while (Environment.TickCount64 < deadline)
            {
                if ((Report() & ReportHangup) != 0)
                    return true;
            }

            return false;
        }

        public unsafe int Report(out int reportsForToken)
        {
            var tokens = stackalloc ulong[16];
            var reports = stackalloc int[16];
            var count = minipty_watch_wait(Fd, _wakeRead, tokens, reports, 16, 50);
            if (count < 0)
                throw new IOException($"minipty_watch_wait failed (errno {Marshal.GetLastPInvokeError()})");

            var report = 0;
            reportsForToken = 0;
            for (var i = 0; i < count; i++)
            {
                if (tokens[i] != Token)
                    continue;

                report |= reports[i];
                reportsForToken++;
            }

            return report;
        }

        public void Dispose()
        {
            UnixInterop.close(Fd);
            if (_wakeRead >= 0)
                UnixInterop.close(_wakeRead);
            if (_wakeWrite >= 0)
                UnixInterop.close(_wakeWrite);
        }
    }

    /// <summary>A PTY pair with a non-blocking master, whose slave the test closes and opens at will.</summary>
    private sealed class PtyPair : IDisposable
    {
        private const int ORdWr = 2;
        private static readonly int s_openFlags = ORdWr | (OperatingSystem.IsMacOS()
            ? 0x20000 | 0x1000000 // O_NOCTTY | O_CLOEXEC
            : 0x8000 | 0x100000);
        private readonly string _slaveName;
        private int _slave;

        public PtyPair()
        {
            // Concurrent PTY opens and closes elsewhere in the process can fail posix_openpt now and then on macOS, as
            // the spawn's own retry allows for.
            for (var attempt = 0; ; attempt++)
            {
                Master = posix_openpt(s_openFlags);
                if (Master >= 0 || attempt == 3)
                    break;

                Thread.Sleep(25 * (attempt + 1));
            }

            if (Master < 0 || grantpt(Master) != 0 || unlockpt(Master) != 0)
                throw new IOException($"Opening a PTY failed (errno {Marshal.GetLastPInvokeError()})");

            _slaveName = Marshal.PtrToStringUTF8(ptsname(Master))!;
            OpenSlave();
            if (UnixInterop.minipty_set_nonblocking(Master) != 0)
                throw new IOException($"Setting the master non-blocking failed (errno {Marshal.GetLastPInvokeError()})");
        }

        public int Master { get; }

        public void OpenSlave()
        {
            _slave = open(_slaveName, s_openFlags);
            if (_slave < 0)
                throw new IOException($"Opening the slave failed (errno {Marshal.GetLastPInvokeError()})");
        }

        public void CloseSlave()
        {
            UnixInterop.close(_slave);
            _slave = -1;
        }

        public unsafe void WriteSlave(string text)
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(text);
            fixed (byte* ptr = bytes)
            {
                if (UnixInterop.Write(_slave, ptr, (nuint)bytes.Length) != bytes.Length)
                    throw new IOException($"Writing the slave failed (errno {Marshal.GetLastPInvokeError()})");
            }
        }

        /// <summary>One read of the master: the byte count, 0 at end of output, or -1 when nothing is there.</summary>
        public unsafe int ReadMaster()
        {
            var buffer = stackalloc byte[256];
            var read = UnixInterop.Read(Master, buffer, 256);
            if (read >= 0)
                return read;

            var errno = Marshal.GetLastPInvokeError();
            return errno == UnixInterop.EAGAIN ? -1 : errno == UnixInterop.EIO ? 0 : throw new IOException($"read failed (errno {errno})");
        }

        public void Dispose()
        {
            if (_slave >= 0)
                UnixInterop.close(_slave);

            UnixInterop.close(Master);
        }
    }

    [LibraryImport("minipty_unix", SetLastError = true)]
    private static partial int minipty_child_watch_open(out int watchFd, out int wakeRead, out int wakeWrite);

    [LibraryImport("minipty_unix", SetLastError = true)]
    private static partial int minipty_watch_add(int watchFd, int fd, ulong token, int interest);

    [LibraryImport("minipty_unix", SetLastError = true)]
    private static partial int minipty_watch_modify(int watchFd, int fd, ulong token, int interest);

    [LibraryImport("minipty_unix", SetLastError = true)]
    private static partial int minipty_watch_renew(int watchFd, int fd, ulong token, int interest);

    [LibraryImport("minipty_unix")]
    private static partial void minipty_watch_remove(int watchFd, int fd);

    [LibraryImport("minipty_unix", SetLastError = true)]
    private static unsafe partial int minipty_watch_wait(int watchFd, int wakeRead, ulong* tokens, int* reports, int capacity, int timeoutMs);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int posix_openpt(int flags);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int grantpt(int fd);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int unlockpt(int fd);

    [LibraryImport("libc", SetLastError = true)]
    private static partial IntPtr ptsname(int fd);

    // Without O_CREAT, open takes no mode, so its variadic part is never used.
    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int open(string path, int flags);
}
