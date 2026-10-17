using System.Diagnostics;
using System.Runtime.InteropServices;
using MiniPty.Internal;

namespace MiniPty.Tests;

/// <summary>
/// On macOS a child starts as <c>minipty_spawn_helper</c>, which re-opens its slave by name to make it the
/// controlling terminal before it execs the target. The host can go away before that open, for example when it
/// crashes right after the spawn. The helper must then end as the hang-up would have ended it, instead of
/// waiting forever for a carrier that a closed master never brings back.
/// </summary>
public sealed partial class PtyMacSpawnHelperTests
{
    private const int O_RDWR = 0x2;
    private const int O_NOCTTY = 0x20000;
    private const short POSIX_SPAWN_SETSIGDEF = 0x4;
    private const short POSIX_SPAWN_SETSIGMASK = 0x8;
    private const short POSIX_SPAWN_SETSID = 0x400;
    private const int SigHup = 1;

    [Test]
    public async Task HelperEndsByHangupWhenTheMasterClosedBeforeItStarted()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var helper = Path.Combine(AppContext.BaseDirectory, "minipty_spawn_helper");
        await Assert.That(File.Exists(helper)).IsTrue();

        var master = posix_openpt(O_RDWR | O_NOCTTY);
        await Assert.That(master).IsGreaterThanOrEqualTo(0);
        await Assert.That(grantpt(master) == 0 && unlockpt(master) == 0).IsTrue();
        var slaveName = Marshal.PtrToStringUTF8(ptsname(master));
        await Assert.That(slaveName).IsNotNull();
        var slave = open(slaveName!, O_RDWR | O_NOCTTY);
        await Assert.That(slave).IsGreaterThanOrEqualTo(0);

        // The master closes before the helper even starts, so its own open of the slave always comes after the
        // hang-up. Without the fix that open never returns, and the helper outlives the test.
        UnixInterop.close(master);
        var pid = SpawnHelper(helper, slave);
        UnixInterop.close(slave);

        var status = await WaitForExitAsync(pid);
        await Assert.That(status).IsNotNull();
        // A signaled wait status keeps the signal in its low seven bits.
        await Assert.That(status!.Value & 0x7f).IsEqualTo(SigHup);
    }

    /// <summary>
    /// Spawns the helper the way the darwin spawn path does: a new session, the slave as stdio, default signal
    /// dispositions, and an empty signal mask, so a test host that ignores or blocks SIGHUP does not hide it.
    /// </summary>
    private static int SpawnHelper(string helper, int slave)
    {
        string[] args = [helper, "sleep", "30"];
        var argv = new nint[args.Length + 1];
        var envp = new nint[] { Marshal.StringToCoTaskMemUTF8("PATH=/usr/bin:/bin"), 0 };
        nint actions = 0;
        nint attrs = 0;
        try
        {
            for (var i = 0; i < args.Length; i++)
                argv[i] = Marshal.StringToCoTaskMemUTF8(args[i]);

            Check(posix_spawn_file_actions_init(ref actions));
            Check(posix_spawn_file_actions_adddup2(ref actions, slave, 0));
            Check(posix_spawn_file_actions_adddup2(ref actions, slave, 1));
            Check(posix_spawn_file_actions_adddup2(ref actions, slave, 2));
            Check(posix_spawnattr_init(ref attrs));
            Check(posix_spawnattr_setflags(ref attrs, POSIX_SPAWN_SETSID | POSIX_SPAWN_SETSIGDEF | POSIX_SPAWN_SETSIGMASK));
            var all = uint.MaxValue;
            Check(posix_spawnattr_setsigdefault(ref attrs, ref all));
            var none = 0u;
            Check(posix_spawnattr_setsigmask(ref attrs, ref none));
            Check(posix_spawn(out var pid, helper, ref actions, ref attrs, argv, envp));
            return pid;
        }
        finally
        {
            if (actions != 0)
                posix_spawn_file_actions_destroy(ref actions);
            if (attrs != 0)
                posix_spawnattr_destroy(ref attrs);
            foreach (var p in argv)
                Marshal.FreeCoTaskMem(p);
            Marshal.FreeCoTaskMem(envp[0]);
        }
    }

    /// <summary>The helper's wait status, or null after killing a helper that is still running past the bound.</summary>
    private static async Task<int?> WaitForExitAsync(int pid)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 10_000)
        {
            if (UnixInterop.waitpid(pid, out var status, UnixInterop.WaitNoHang) == pid)
                return status;

            await Task.Delay(20);
        }

        UnixInterop.kill(pid, UnixInterop.SigKill);
        UnixInterop.waitpid(pid, out _, 0);
        return null;
    }

    private static void Check(int result)
    {
        if (result != 0)
            throw new InvalidOperationException($"posix_spawn setup failed ({result})");
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int posix_openpt(int flags);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int grantpt(int fd);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int unlockpt(int fd);

    [LibraryImport("libc")]
    private static partial nint ptsname(int fd);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int open(string path, int flags);

    [LibraryImport("libc")]
    private static partial int posix_spawn_file_actions_init(ref nint actions);

    [LibraryImport("libc")]
    private static partial int posix_spawn_file_actions_adddup2(ref nint actions, int fd, int newFd);

    [LibraryImport("libc")]
    private static partial int posix_spawn_file_actions_destroy(ref nint actions);

    [LibraryImport("libc")]
    private static partial int posix_spawnattr_init(ref nint attrs);

    [LibraryImport("libc")]
    private static partial int posix_spawnattr_setflags(ref nint attrs, short flags);

    [LibraryImport("libc")]
    private static partial int posix_spawnattr_setsigdefault(ref nint attrs, ref uint signals);

    [LibraryImport("libc")]
    private static partial int posix_spawnattr_setsigmask(ref nint attrs, ref uint signals);

    [LibraryImport("libc")]
    private static partial int posix_spawnattr_destroy(ref nint attrs);

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int posix_spawn(out int pid, string path, ref nint actions, ref nint attrs, nint[] argv, nint[] envp);
}
