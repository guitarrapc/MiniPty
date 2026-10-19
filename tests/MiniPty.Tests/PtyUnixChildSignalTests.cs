using System.Diagnostics;
using System.Runtime.InteropServices;
using MiniPty.Internal;

namespace MiniPty.Tests;

/// <summary>
/// The native call every Unix session signals its child through. It must never deliver a signal to a process that is
/// not the session's child, even when the pid number now names one: code outside MiniPty can reap the child, and its
/// pid can then be reused. Reuse cannot be forced without privileges, so the negative tests pass the pid of a live
/// process that is not the child, and signal 0, which only checks whether a signal could be delivered.
/// </summary>
public sealed class PtyUnixChildSignalTests
{
    private const int CheckOnly = 0;
    private const int Esrch = 3;

    [Test]
    public async Task APidfdForAReapedChildReachesNoOtherProcess()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var child = StartSleep();
        var pidfd = UnixInterop.minipty_pidfd_open(child.Id);
        await Assert.That(pidfd).IsGreaterThanOrEqualTo(0);
        try
        {
            child.Kill();
            await child.WaitForExitAsync();

            // This test process is alive, so a signal by pid would reach it.
            await Assert.That(UnixInterop.kill(Environment.ProcessId, CheckOnly)).IsEqualTo(0);
            await Assert.That(UnixInterop.minipty_signal_child(pidfd, Environment.ProcessId, CheckOnly)).IsEqualTo(-1);
            await Assert.That(Marshal.GetLastPInvokeError()).IsEqualTo(Esrch);
        }
        finally
        {
            UnixInterop.close(pidfd);
        }
    }

    [Test]
    public async Task APidfdForALiveChildReachesIt()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var child = StartSleep();
        var pidfd = UnixInterop.minipty_pidfd_open(child.Id);
        await Assert.That(pidfd).IsGreaterThanOrEqualTo(0);
        try
        {
            // The pid argument is not the child's, so only the pidfd can deliver the signal.
            await Assert.That(UnixInterop.minipty_signal_child(pidfd, Environment.ProcessId, UnixInterop.SigTerm)).IsEqualTo(0);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            UnixInterop.close(pidfd);
        }
    }

    [Test]
    public async Task WithoutAPidfdAProcessThatIsNotAChildIsNotSignaled()
    {
        if (OperatingSystem.IsWindows())
            return;

        // This test process is alive and is not its own child.
        await Assert.That(UnixInterop.kill(Environment.ProcessId, CheckOnly)).IsEqualTo(0);
        await Assert.That(UnixInterop.minipty_signal_child(-1, Environment.ProcessId, CheckOnly)).IsEqualTo(-1);
        await Assert.That(Marshal.GetLastPInvokeError()).IsEqualTo(UnixInterop.ECHILD);
    }

    [Test]
    public async Task WithoutAPidfdAChildReapedElsewhereIsNotSignaled()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var child = StartSleep();
        var pid = child.Id;
        child.Kill();
        // .NET reaps the child, so it is no longer this process's child. ECHILD rather than ESRCH shows the call
        // refused at the child check and never reached kill.
        await child.WaitForExitAsync();

        await Assert.That(UnixInterop.minipty_signal_child(-1, pid, CheckOnly)).IsEqualTo(-1);
        await Assert.That(Marshal.GetLastPInvokeError()).IsEqualTo(UnixInterop.ECHILD);
    }

    [Test]
    public async Task WithoutAPidfdALiveChildIsSignaled()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var child = StartSleep();
        await Assert.That(UnixInterop.minipty_signal_child(-1, child.Id, UnixInterop.SigTerm)).IsEqualTo(0);
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static Process StartSleep() =>
        Process.Start(new ProcessStartInfo("sleep", "30") { UseShellExecute = false })
            ?? throw new InvalidOperationException("sleep did not start");
}
