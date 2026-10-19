using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using MiniPty.Internal;

namespace MiniPty.Tests;

/// <summary>
/// A session whose child other code reaped, and whose pid then went to another child of the host, must report its own
/// child's exit and must not reap that other child. Without privileges, only a host in its own pid namespace can choose
/// the next pid, so the host is this test executable started again in new user and pid namespaces (see
/// <see cref="RunAsPidReuseHostWhenAsked"/>). Where those namespaces or a reap through a pidfd are not available, the
/// scene cannot be set and the test returns. The test fails when the pidfd is opened after the start hook, as one opened
/// when the session is built would be, or when the reap goes by pid. It does not check the session's signals: the
/// watcher usually reports the exit before <see cref="Pty.Start"/> returns, after which the session sends none.
/// </summary>
public sealed class PtyPidReuseTests
{
    private const string HostVariable = "MINIPTY_TEST_PID_REUSE_HOST";
    private const string NextPidPath = "/proc/sys/kernel/ns_last_pid";
    // The host's exit code when it cannot set the scene.
    private const int SceneUnavailable = 77;
    private static readonly string[] s_unshare = ["--user", "--map-root-user", "--pid", "--fork", "--mount-proc"];

    [Test]
    public async Task ASessionNeverReapsAChildThatGotItsPid()
    {
        if (!OperatingSystem.IsLinux() || !await CanUnshareAsync())
            return;

        // The host starts the way this run did, as the apphost or as the assembly under dotnet, so it finds the same
        // runtime.
        var self = Environment.ProcessPath!;
        string[] command = Path.GetFileNameWithoutExtension(self) == "dotnet"
            ? [self, Path.Combine(AppContext.BaseDirectory, "MiniPty.Tests.dll")]
            : [self];

        using var host = Process.Start(Unshare(command, redirectOutput: true))!;
        var output = host.StandardOutput.ReadToEndAsync();
        var error = host.StandardError.ReadToEndAsync();
        await WaitOrKillAsync(host, TimeSpan.FromSeconds(60));
        if (host.ExitCode == SceneUnavailable)
            return;

        await Assert.That(host.ExitCode).IsEqualTo(0).Because($"the host printed: {await output}{await error}");
    }

    private static async Task<bool> CanUnshareAsync()
    {
        Process probe;
        try
        {
            probe = Process.Start(Unshare(["true"], redirectOutput: false))!;
        }
        catch (Win32Exception)
        {
            // No unshare on this machine.
            return false;
        }

        using (probe)
        {
            try
            {
                await WaitOrKillAsync(probe, TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException)
            {
                return false;
            }

            return probe.ExitCode == 0;
        }
    }

    private static ProcessStartInfo Unshare(string[] command, bool redirectOutput)
    {
        var start = new ProcessStartInfo("unshare")
        {
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = true,
            UseShellExecute = false,
            Environment = { [HostVariable] = "1" },
        };
        foreach (var argument in s_unshare)
            start.ArgumentList.Add(argument);
        foreach (var argument in command)
            start.ArgumentList.Add(argument);
        return start;
    }

    /// <summary>
    /// Waits for <paramref name="process"/>, and kills it with everything it started if it outlasts
    /// <paramref name="timeout"/>, so a hung host leaves no namespace behind.
    /// </summary>
    private static async Task WaitOrKillAsync(Process process, TimeSpan timeout)
    {
        try
        {
            await process.WaitForExitAsync().WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
    }

    /// <summary>
    /// Makes this test executable the host of <see cref="ASessionNeverReapsAChildThatGotItsPid"/> when
    /// <see cref="HostVariable"/> is set. It runs before the test framework starts and exits the process, so no test runs
    /// in the host. In a test run the variable is unset and this returns at once.
    /// </summary>
    [ModuleInitializer]
    internal static void RunAsPidReuseHostWhenAsked()
    {
        if (Environment.GetEnvironmentVariable(HostVariable) is not null)
            Environment.Exit(RunPidReuseHost());
    }

    /// <summary>
    /// Runs in the new namespaces, where root of the new user namespace may set the next pid. The session's child exits
    /// at once, the start hook reaps it as other code would, and the next pid goes to a child the host starts itself.
    /// Returns 0 when the session reported its own child's exit and left that other child running and unreaped,
    /// <see cref="SceneUnavailable"/> when the scene cannot be set, and otherwise the number of the check that failed.
    /// </summary>
    private static int RunPidReuseHost()
    {
        if (!File.Exists(NextPidPath) || !PtyUnixChildSignalTests.PidfdsWork)
            return SceneUnavailable;

        // The runtime's own threads take pids too, so the reuse can miss. Each attempt starts over.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var reuser = -1;
            var nextPidFailed = false;
            PtyStartTestHook.Fault = (stage, pid) =>
            {
                if (stage != PtyStartStage.Spawned)
                    return;

                _ = UnixInterop.minipty_reap_child(-1, pid, 1, out _, out _);
                try
                {
                    File.WriteAllText(NextPidPath, (pid - 1).ToString(CultureInfo.InvariantCulture));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    nextPidFailed = true;
                    throw;
                }

                reuser = PtyUnixChildSignalTests.SpawnUnwatched("exec sleep 30");
            };
            PtySession session;
            try
            {
                session = Pty.Start(new PtyStartInfo { FileName = "true" });
            }
            catch when (nextPidFailed)
            {
                return SceneUnavailable;
            }
            finally
            {
                PtyStartTestHook.Fault = null;
            }

            if (reuser != session.ProcessId)
            {
                session.Dispose();
                PtyUnixChildSignalTests.KillAndReap(reuser);
                continue;
            }

            try
            {
                // The watcher sees the exit through the pidfd. A reap by pid would see the child that got the pid running,
                // so the session would never report its own exit.
                if (!SpinWait.SpinUntil(() => session.HasExited, TimeSpan.FromSeconds(10)))
                    return 1;

                session.Dispose();
                // 0 means the child that got the pid still runs, so the session did not reap it.
                var reaped = UnixInterop.minipty_reap_child(-1, reuser, 0, out _, out _);
                if (reaped == 1)
                    reuser = -1;
                return reaped == 0 ? 0 : 2;
            }
            finally
            {
                PtyUnixChildSignalTests.KillAndReap(reuser);
            }
        }

        // The pid never went to the child the host started.
        return 3;
    }
}
