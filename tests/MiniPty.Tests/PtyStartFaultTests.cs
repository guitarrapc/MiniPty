using System.Diagnostics;
using System.Runtime.InteropServices;
using MiniPty.Internal;

namespace MiniPty.Tests;

/// <summary>
/// A start that fails after its child is running ends the child and leaks nothing (baseline guarantee 1, the part
/// <see cref="PtyBaselineTests"/> cannot reach through the public API). The steps that can fail there do so only on
/// resource exhaustion, so the failure is injected through <see cref="PtyStartTestHook"/>, which is process-wide:
/// these tests run alone.
/// </summary>
public sealed class PtyStartFaultTests
{
    private const int ESRCH = 3;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// <c>Spawned</c>: no backend owns the child yet, so the start itself ends it; on Unix it also reaps it, since no
    /// exit watcher knows the child. <c>BackendBuilt</c>: the backend owns the child, and the session's failure path
    /// disposes the backend, which ends it; on Unix the watcher reaps it.
    /// </summary>
    [Test]
    [NotInParallel]
    [Arguments(nameof(PtyStartStage.Spawned))]
    [Arguments(nameof(PtyStartStage.BackendBuilt))]
    public async Task AStartThatFailsWithItsChildRunningEndsTheChildAndLeaksNothing(string stage)
    {
        var injected = Enum.Parse<PtyStartStage>(stage);
        var children = new List<int>();
        PtyStartTestHook.Fault = (at, processId) =>
        {
            if (at != injected)
                return;

            children.Add(processId);
            throw new InjectedStartFault();
        };

        try
        {
            await PtyBaselineTests.AssertNoLeakAsync(async () =>
            {
                StartExpectingTheFault();
                // Off this thread between starts, so the synchronous loop does not hold a thread-pool worker throughout.
                await Task.Yield();
            });

            await Assert.That(children.Count).IsGreaterThan(0);
            await WaitForChildrenToEndAsync(children);
        }
        finally
        {
            PtyStartTestHook.Fault = null;
            foreach (var pid in children)
                KillIfRunning(pid);
        }
    }

    private static void StartExpectingTheFault()
    {
        try
        {
            Pty.Start(Blocks()).Dispose();
        }
        catch (InjectedStartFault)
        {
            return;
        }

        throw new InvalidOperationException("The start did not fail.");
    }

    private static async Task WaitForChildrenToEndAsync(List<int> children)
    {
        foreach (var pid in children)
            await Assert.That(await WaitUntilAsync(() => !Exists(pid))).IsTrue().Because($"child {pid} still exists");
    }

    /// <summary>
    /// Whether the child still exists: on Windows whether it runs, on Unix whether it runs or is a zombie, since an
    /// unreaped child is a leak too.
    /// </summary>
    private static bool Exists(int pid)
    {
        if (!OperatingSystem.IsWindows())
            return kill(pid, 0) == 0 || Marshal.GetLastPInvokeError() != ESRCH;

        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void KillIfRunning(int pid)
    {
        if (!Exists(pid))
            return;

        if (!OperatingSystem.IsWindows())
        {
            kill(pid, 9);
            return;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(Timeout.TotalSeconds * Stopwatch.Frequency);
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() >= deadline)
                return false;

            await Task.Delay(20);
        }

        return true;
    }

    /// <summary>Waits on stdin, which nobody writes, so only the failed start can end it.</summary>
    private static PtyStartInfo Blocks() =>
        OperatingSystem.IsWindows()
            ? new PtyStartInfo { FileName = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe", Arguments = ["/c", "set /p DUMMY="] }
            : new PtyStartInfo { FileName = "sh", Arguments = ["-c", "IFS= read -r _"] };

    private sealed class InjectedStartFault : Exception;

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);
}
