using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using MiniPty.Internal;

namespace MiniPty.Tests;

/// <summary>
/// The native calls every Unix session signals and reaps its child through. With a pidfd they must act on its process
/// alone, whatever the pid argument names. Without one they must refuse a pid that is no longer an unreaped child of
/// this process. Code outside MiniPty can reap a child, and its pid can then be reused, which without privileges only a
/// pid namespace can force, as <see cref="PtyPidReuseTests"/> does. The tests here use stand-ins instead. One is a
/// live process that is not the child, signaled with signal 0, which only checks whether a signal could be delivered.
/// Others are a child reaped elsewhere and a zombie that must stay waitable.
/// </summary>
public sealed partial class PtyUnixChildSignalTests
{
    private const int CheckOnly = 0;
    private const int Esrch = 3;
    // waitid's id type and flags differ by platform. P_PID is 0 on FreeBSD and 1 elsewhere, WEXITED is 16 on FreeBSD
    // and 4 elsewhere, and WNOWAIT differs on each.
    private static readonly int s_idTypePid = OperatingSystem.IsFreeBSD() ? 0 : 1;
    private static readonly int s_waitExited = OperatingSystem.IsFreeBSD() ? 16 : 4;
    private static readonly int s_waitNoWait = OperatingSystem.IsLinux() ? 0x01000000 : OperatingSystem.IsFreeBSD() ? 8 : 0x20;
    private const long SysPidfdOpen = 434;
    private const int IdTypePidfd = 3;
    private static readonly Lazy<bool> s_pidfdsWork = new(ProbePidfds);

    /// <summary>
    /// Whether the kernel gives pidfds and can reap through one: Linux 5.4 and later, with no filter that blocks them.
    /// It is probed with the system calls themselves rather than MiniPty's, so a MiniPty regression fails the pidfd tests
    /// instead of skipping them.
    /// </summary>
    internal static bool PidfdsWork => OperatingSystem.IsLinux() && s_pidfdsWork.Value;

    [Test]
    public async Task APidfdForAReapedChildReachesNoOtherProcess()
    {
        if (!PidfdsWork)
            return;

        var child = StartSleep();
        var pidfd = UnixInterop.minipty_pidfd_open(child.Id);
        try
        {
            await Assert.That(pidfd).IsGreaterThanOrEqualTo(0);
            child.Kill();
            await child.WaitForExitAsync();

            var result = UnixInterop.minipty_signal_child(pidfd, Environment.ProcessId, CheckOnly);
            var error = Marshal.GetLastPInvokeError();

            // This test process is alive, so a signal by pid would reach it.
            await Assert.That(UnixInterop.kill(Environment.ProcessId, CheckOnly)).IsEqualTo(0);
            await Assert.That(result).IsEqualTo(-1);
            await Assert.That(error).IsEqualTo(Esrch);
        }
        finally
        {
            UnixInterop.close(pidfd);
            KillAndDispose(child);
        }
    }

    [Test]
    public async Task APidfdForALiveChildReachesItAndNoOther()
    {
        if (!PidfdsWork)
            return;

        var child = StartSleep();
        var decoy = StartSleep();
        var pidfd = UnixInterop.minipty_pidfd_open(child.Id);
        try
        {
            await Assert.That(pidfd).IsGreaterThanOrEqualTo(0);

            // The pid argument names the decoy, so only the pidfd can deliver the signal to the child.
            await Assert.That(UnixInterop.minipty_signal_child(pidfd, decoy.Id, UnixInterop.SigTerm)).IsEqualTo(0);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(decoy.HasExited).IsFalse();
        }
        finally
        {
            UnixInterop.close(pidfd);
            KillAndDispose(child);
            KillAndDispose(decoy);
        }
    }

    [Test]
    public async Task APidfdForAProcessThatIsNotAChildIsRefused()
    {
        if (!PidfdsWork)
            return;

        // This test process is alive but is not its own child, like a process this one never started that got a reaped
        // child's pid.
        var pidfd = UnixInterop.minipty_pidfd_open(Environment.ProcessId);
        var error = Marshal.GetLastPInvokeError();
        if (pidfd >= 0)
            UnixInterop.close(pidfd);

        await Assert.That(pidfd).IsEqualTo(-1);
        await Assert.That(error).IsEqualTo(UnixInterop.ECHILD);
    }

    [Test]
    public async Task WithoutAPidfdAProcessThatIsNotAChildIsNotSignaled()
    {
        if (OperatingSystem.IsWindows())
            return;

        var result = UnixInterop.minipty_signal_child(-1, Environment.ProcessId, CheckOnly);
        var error = Marshal.GetLastPInvokeError();

        // This test process is alive and is not its own child.
        await Assert.That(UnixInterop.kill(Environment.ProcessId, CheckOnly)).IsEqualTo(0);
        await Assert.That(result).IsEqualTo(-1);
        await Assert.That(error).IsEqualTo(UnixInterop.ECHILD);
    }

    [Test]
    public async Task WithoutAPidfdAChildReapedElsewhereIsNotSignaledOrReaped()
    {
        if (OperatingSystem.IsWindows())
            return;

        var child = StartSleep();
        var pid = child.Id;
        child.Kill();
        // .NET reaps the child, so it is no longer this process's child. ECHILD rather than ESRCH shows the signal was
        // refused at the child check and never reached kill.
        await child.WaitForExitAsync();
        child.Dispose();

        var signaled = UnixInterop.minipty_signal_child(-1, pid, CheckOnly);
        var signalError = Marshal.GetLastPInvokeError();
        var reaped = UnixInterop.minipty_reap_child(-1, pid, 0, out _, out _);
        var reapError = Marshal.GetLastPInvokeError();

        await Assert.That(signaled).IsEqualTo(-1);
        await Assert.That(signalError).IsEqualTo(UnixInterop.ECHILD);
        await Assert.That(reaped).IsEqualTo(-1);
        await Assert.That(reapError).IsEqualTo(UnixInterop.ECHILD);
    }

    [Test]
    public async Task WithoutAPidfdALiveChildIsSignaled()
    {
        if (OperatingSystem.IsWindows())
            return;

        var child = StartSleep();
        try
        {
            await Assert.That(UnixInterop.minipty_signal_child(-1, child.Id, UnixInterop.SigTerm)).IsEqualTo(0);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            KillAndDispose(child);
        }
    }

    /// <summary>
    /// The check before a signal must not reap: a zombie is still the session's child, and the watcher must read its
    /// status. The child is spawned without .NET's knowledge, so nothing else reaps it either.
    /// </summary>
    [Test]
    public async Task WithoutAPidfdAZombieChildIsLeftWaitable()
    {
        if (OperatingSystem.IsWindows())
            return;

        var pid = SpawnUnwatched("exit 7");
        try
        {
            WaitUntilZombie(pid);

            await Assert.That(UnixInterop.minipty_signal_child(-1, pid, CheckOnly)).IsEqualTo(0);
            var reaped = UnixInterop.minipty_reap_child(-1, pid, 0, out var exitCode, out var termSignal);
            if (reaped == 1)
                pid = -1;
            await Assert.That(reaped).IsEqualTo(1);
            await Assert.That(exitCode).IsEqualTo(7);
            await Assert.That(termSignal).IsEqualTo(0);
        }
        finally
        {
            KillAndReap(pid);
        }
    }

    /// <summary>
    /// A reap must report nothing while the child runs. The watcher reaps a polled child this way every few
    /// milliseconds, so a running child reported as exited would lose its watch and its pidfd.
    /// </summary>
    [Test]
    public async Task AReapOfARunningChildReportsNoExit()
    {
        if (OperatingSystem.IsWindows())
            return;

        var pid = SpawnUnwatched("exec sleep 30");
        var pidfd = PidfdsWork ? UnixInterop.minipty_pidfd_open(pid) : -1;
        try
        {
            if (PidfdsWork)
            {
                await Assert.That(pidfd).IsGreaterThanOrEqualTo(0);
                await Assert.That(UnixInterop.minipty_reap_child(pidfd, pid, 0, out _, out _)).IsEqualTo(0);
            }

            await Assert.That(UnixInterop.minipty_reap_child(-1, pid, 0, out _, out _)).IsEqualTo(0);
        }
        finally
        {
            if (pidfd >= 0)
                UnixInterop.close(pidfd);
            KillAndReap(pid);
        }
    }

    /// <summary>
    /// A reap through a pidfd whose child is gone must not take the exit of another child that the pid argument names:
    /// after an outside reap, a reused pid can name exactly such a child.
    /// </summary>
    [Test]
    public async Task APidfdReapNeverReapsAnotherChild()
    {
        if (!PidfdsWork)
            return;

        var gone = StartSleep();
        var pidfd = -1;
        var other = -1;
        try
        {
            pidfd = UnixInterop.minipty_pidfd_open(gone.Id);
            other = SpawnUnwatched("exit 5");
            await Assert.That(pidfd).IsGreaterThanOrEqualTo(0);
            gone.Kill();
            await gone.WaitForExitAsync();
            WaitUntilZombie(other);

            var result = UnixInterop.minipty_reap_child(pidfd, other, 0, out _, out _);
            var error = Marshal.GetLastPInvokeError();

            await Assert.That(result).IsEqualTo(-1);
            await Assert.That(error).IsEqualTo(UnixInterop.ECHILD);
            // The other child is still waitable, with its own status.
            var reaped = UnixInterop.minipty_reap_child(-1, other, 0, out var exitCode, out _);
            if (reaped == 1)
                other = -1;
            await Assert.That(reaped).IsEqualTo(1);
            await Assert.That(exitCode).IsEqualTo(5);
        }
        finally
        {
            if (pidfd >= 0)
                UnixInterop.close(pidfd);
            KillAndDispose(gone);
            KillAndReap(other);
        }
    }

    [Test]
    public async Task APidfdReapReportsItsChildsExit()
    {
        if (!PidfdsWork)
            return;

        var child = SpawnUnwatched("exit 6");
        var pidfd = -1;
        try
        {
            WaitUntilZombie(child);
            pidfd = UnixInterop.minipty_pidfd_open(child);
            await Assert.That(pidfd).IsGreaterThanOrEqualTo(0);

            // The pid argument is this test process, which is no child, so only the pidfd can reach the zombie.
            var reaped = UnixInterop.minipty_reap_child(pidfd, Environment.ProcessId, 0, out var exitCode, out var termSignal);
            if (reaped == 1)
                child = -1;
            await Assert.That(reaped).IsEqualTo(1);
            await Assert.That(exitCode).IsEqualTo(6);
            await Assert.That(termSignal).IsEqualTo(0);
        }
        finally
        {
            if (pidfd >= 0)
                UnixInterop.close(pidfd);
            KillAndReap(child);
        }
    }

    /// <summary>
    /// A signal through a pidfd to a zombie must not reap it or change the status the watcher will read. The pid argument
    /// names a live decoy child, so a call that ignored the pidfd would kill the decoy instead.
    /// </summary>
    [Test]
    public async Task APidfdSignalToAZombieLeavesItWaitable()
    {
        if (!PidfdsWork)
            return;

        var child = SpawnUnwatched("exit 4");
        var decoy = StartSleep();
        var pidfd = -1;
        try
        {
            WaitUntilZombie(child);
            pidfd = UnixInterop.minipty_pidfd_open(child);
            await Assert.That(pidfd).IsGreaterThanOrEqualTo(0);

            await Assert.That(UnixInterop.minipty_signal_child(pidfd, decoy.Id, UnixInterop.SigKill)).IsEqualTo(0);
            await Assert.That(decoy.WaitForExit(200)).IsFalse();

            var reaped = UnixInterop.minipty_reap_child(pidfd, child, 0, out var exitCode, out var termSignal);
            if (reaped == 1)
                child = -1;
            await Assert.That(reaped).IsEqualTo(1);
            await Assert.That(exitCode).IsEqualTo(4);
            await Assert.That(termSignal).IsEqualTo(0);
        }
        finally
        {
            if (pidfd >= 0)
                UnixInterop.close(pidfd);
            KillAndReap(child);
            KillAndDispose(decoy);
        }
    }

    /// <summary>
    /// The spawn opens the child's pidfd before it returns, so the pidfd already exists when the start hook first sees the
    /// pid. A pidfd opened after the hook, when the session is built, could name another process if the child had exited,
    /// other code had reaped it, and its pid had been reused. The hook reaps the child as such code would, and takes its
    /// status 7, so the session must still report the exit, with the status lost.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task ThePidfdIsOpenBeforeTheStartHookRuns()
    {
        // The check reads the process a pidfd names from fdinfo, which older kernels do not show.
        if (!PidfdsWork || !FdinfoShowsPidfdProcess())
            return;

        var pidfdBeforeTheHook = false;
        var hookReaped = 0;
        var hookExitCode = -1;
        PtyStartTestHook.Fault = (stage, pid) =>
        {
            if (stage != PtyStartStage.Spawned)
                return;

            pidfdBeforeTheHook = HasPidfdFor(pid);
            hookReaped = UnixInterop.minipty_reap_child(-1, pid, 1, out hookExitCode, out _);
        };
        PtySession session;
        try
        {
            session = Pty.Start(new PtyStartInfo { FileName = "sh", Arguments = ["-c", "exit 7"] });
        }
        finally
        {
            PtyStartTestHook.Fault = null;
        }

        await using (session)
        {
            await Assert.That(pidfdBeforeTheHook).IsTrue();
            await Assert.That(hookReaped).IsEqualTo(1);
            await Assert.That(hookExitCode).IsEqualTo(7);
            var status = await session.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(status).IsEqualTo(new PtyExitStatus(0, null));
        }
    }

    private static Process StartSleep() =>
        Process.Start(new ProcessStartInfo("sleep", "30") { UseShellExecute = false })
            ?? throw new InvalidOperationException("sleep did not start");

    private static void KillAndDispose(Process process)
    {
        using (process)
            process.Kill();
    }

    /// <summary>
    /// Kills and reaps a child that <see cref="SpawnUnwatched"/> started and the caller has not reaped. Once reaped, its
    /// pid is free and may name another child of this process, so a caller that reaped it passes -1.
    /// </summary>
    internal static void KillAndReap(int pid)
    {
        if (pid <= 0)
            return;

        _ = UnixInterop.minipty_signal_child(-1, pid, UnixInterop.SigKill);
        _ = UnixInterop.minipty_reap_child(-1, pid, 1, out _, out _);
    }

    private static unsafe bool ProbePidfds()
    {
        var child = SpawnUnwatched("exit 0");
        var pidfd = -1;
        try
        {
            pidfd = (int)syscall(SysPidfdOpen, child, 0);
            if (pidfd < 0)
                return false;

            // Linux 5.3 rejects a wait by pidfd with EINVAL.
            var info = stackalloc byte[256];
            if (waitid(IdTypePidfd, pidfd, info, s_waitExited) != 0)
                return false;

            child = -1;
            return true;
        }
        finally
        {
            if (pidfd >= 0)
                UnixInterop.close(pidfd);
            KillAndReap(child);
        }
    }

    private static bool FdinfoShowsPidfdProcess()
    {
        var child = SpawnUnwatched("exec sleep 30");
        var pidfd = (int)syscall(SysPidfdOpen, child, 0);
        try
        {
            return pidfd >= 0 && HasPidfdFor(child);
        }
        finally
        {
            if (pidfd >= 0)
                UnixInterop.close(pidfd);
            KillAndReap(child);
        }
    }

    /// <summary>Whether this process holds a pidfd whose process has <paramref name="pid"/>.</summary>
    private static bool HasPidfdFor(int pid)
    {
        var expected = pid.ToString(CultureInfo.InvariantCulture);
        foreach (var path in Directory.EnumerateFiles("/proc/self/fdinfo"))
        {
            try
            {
                foreach (var line in File.ReadLines(path))
                {
                    if (line.StartsWith("Pid:", StringComparison.Ordinal) && line[4..].Trim() == expected)
                        return true;
                }
            }
            catch (IOException)
            {
                // The descriptor closed meanwhile.
            }
        }

        return false;
    }

    /// <summary>
    /// Starts <c>/bin/sh -c <paramref name="script"/></c> with posix_spawn, so neither .NET nor MiniPty knows the
    /// child and nothing but the caller reaps it.
    /// </summary>
    internal static unsafe int SpawnUnwatched(string script)
    {
        var strings = new[] { "sh", "-c", script };
        var argv = new IntPtr[strings.Length + 1];
        try
        {
            for (var i = 0; i < strings.Length; i++)
                argv[i] = Marshal.StringToCoTaskMemUTF8(strings[i]);

            var envp = stackalloc IntPtr[1];
            envp[0] = IntPtr.Zero;
            int pid;
            int error;
            fixed (IntPtr* argvPtr = argv)
                error = posix_spawn(&pid, "/bin/sh", IntPtr.Zero, IntPtr.Zero, argvPtr, envp);

            if (error != 0)
                throw new InvalidOperationException($"posix_spawn failed with {error}");

            return pid;
        }
        finally
        {
            foreach (var arg in argv)
                Marshal.FreeCoTaskMem(arg);
        }
    }

    /// <summary>Blocks until <paramref name="pid"/> exits, leaving it a zombie (WNOWAIT).</summary>
    private static unsafe void WaitUntilZombie(int pid)
    {
        // Larger than siginfo_t on every Unix the suite runs on.
        var info = stackalloc byte[256];
        while (waitid(s_idTypePid, pid, info, s_waitExited | s_waitNoWait) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != UnixInterop.EINTR)
                throw new InvalidOperationException($"waitid failed with {error}");
        }
    }

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int posix_spawn(int* pid, string path, IntPtr fileActions, IntPtr attributes, IntPtr* argv, IntPtr* envp);

    [LibraryImport("libc", SetLastError = true)]
    // id_t is 64-bit on FreeBSD and 32-bit elsewhere. A long passes either way on the 64-bit platforms the suite runs on.
    private static unsafe partial int waitid(int idType, long id, byte* info, int options);

    [LibraryImport("libc", SetLastError = true)]
    private static partial long syscall(long number, int pid, int flags);
}
