using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MiniPty.Internal;
using static MiniPty.Tests.PtyBaselineTests;

namespace MiniPty.Tests;

/// <summary>
/// <see cref="PtyStartInfo.KillProcessTree"/>: disposal, <see cref="PtySession.Kill()"/>, and the end of the host process
/// end every process the child started. On Windows the child runs in a kill-on-close job, which also reaches a descendant
/// in a console of its own. On Unix the signals go to the child's process group, which also reaches a descendant started
/// with <c>nohup</c>, and the child's zombie is kept until the group is killed. Without the option such a descendant
/// outlives the session (<see cref="PtyBaselineTests.TeardownEndsOnlyTheDescendantsOnTheTerminal"/>).
/// </summary>
public sealed class PtyKillProcessTreeTests
{
    // Set on this test executable to make it the host of HostEndEndsEveryDescendant. The value names how the host ends.
    private const string TreeHostVariable = "MINIPTY_TESTS_TREE_HOST";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly Descendant[] OnTerminalAndDetached = [Descendant.OnTerminal, Descendant.Detached];

    /// <summary>Disposing the session ends the child and both descendants.</summary>
    [Test]
    [NotInParallel(DescendantsGroup)]
    public async Task DisposeEndsEveryDescendant()
    {
        int[] descendants = [];
        try
        {
            var session = StartWithDescendants(OnTerminalAndDetached, out var pids, killProcessTree: true);
            await using (session)
            {
                descendants = await pids;
                await session.DisposeAsync().AsTask().WaitAsync(Timeout);
            }

            await AssertAllEndedAsync(descendants);
        }
        finally
        {
            KillAll(descendants);
        }
    }

    /// <summary><see cref="PtySession.Kill()"/> ends the child and both descendants, as disposal does.</summary>
    [Test]
    [NotInParallel(DescendantsGroup)]
    public async Task KillEndsEveryDescendant()
    {
        int[] descendants = [];
        try
        {
            await using var session = StartWithDescendants(OnTerminalAndDetached, out var pids, killProcessTree: true);
            descendants = await pids;

            session.Kill();

            // TerminateJobObject's code on Windows, 128 + SIGKILL on Unix.
            var killed = OperatingSystem.IsWindows() ? 1 : 137;
            await Assert.That((await session.WaitForExitAsync().WaitAsync(Timeout)).ExitCode).IsEqualTo(killed);
            await AssertAllEndedAsync(descendants);
        }
        finally
        {
            KillAll(descendants);
        }
    }

    /// <summary>
    /// The session owns the tree until it is disposed: after the child exits by itself, a descendant in a console of its
    /// own (Windows) or started with <c>nohup</c> (Unix) keeps running, and disposal or <see cref="PtySession.Kill()"/>
    /// then ends it. On Unix the child's exit is reported at once, while its zombie is kept until the group's kill and
    /// then reaped.
    /// </summary>
    [Test]
    [NotInParallel(DescendantsGroup)]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ADescendantOfAChildThatExitedRunsUntilTeardown(bool dispose)
    {
        int[] descendants = [];
        try
        {
            var session = StartWithDescendants([Descendant.Detached], out var pids, killProcessTree: true);
            await using (session)
            {
                descendants = await pids;
                var child = session.ProcessId;
                await EndChildAsync(session);
                await ReadAllTextAsync(session).WaitAsync(Timeout);
                await Assert.That((await session.WaitForExitAsync().WaitAsync(Timeout)).ExitCode).IsEqualTo(0);

                // Past the post-exit drain, which closes the pseudo-console (Windows) or the master (Unix); the job or
                // the group stays.
                await Task.Delay(1500);
                await Assert.That(IsRunning(descendants[0])).IsTrue();
                if (!OperatingSystem.IsWindows())
                    await Assert.That(Exists(child)).IsTrue().Because("the child's zombie keeps its process group's id");

                if (dispose)
                    await session.DisposeAsync().AsTask().WaitAsync(Timeout);
                else
                    session.Kill();

                await AssertAllEndedAsync(descendants);
                if (!OperatingSystem.IsWindows())
                    await Assert.That(await WaitUntilAsync(() => !Exists(child), Timeout)).IsTrue().Because("the kept zombie is reaped");
            }
        }
        finally
        {
            KillAll(descendants);
        }
    }

    /// <summary>
    /// The end of the host process ends the tree. On Windows the host may exit without disposing the session or be
    /// killed: the kernel closes the job's only handle either way. The host also runs inside a job of its own, so the
    /// session's job is nested, as it is under CI runners and editors. On Unix only a host that exits does, since a killed
    /// process runs no code: MiniPty kills the group from the process's exit event.
    /// </summary>
    [Test]
    [NotInParallel(DescendantsGroup)]
    [Arguments("exit")]
    [Arguments("killed")]
    public async Task HostEndEndsEveryDescendant(string mode)
    {
        if (!OperatingSystem.IsWindows() && mode != "exit")
            return;

        int[] processes = [];
        try
        {
            var hostPath = OperatingSystem.IsWindows() ? TestExecutable : Path.Combine(AppContext.BaseDirectory, "MiniPty.Tests");
            using var host = Process.Start(new ProcessStartInfo(hostPath)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                Environment = { [TreeHostVariable] = mode },
            })!;
            // Two starts of this executable in a row (the host, then its descendants child), so the longer bound.
            var line = await host.StandardOutput.ReadLineAsync().WaitAsync(DescendantsStartTimeout);
            await host.WaitForExitAsync().WaitAsync(Timeout);

            await Assert.That(line).StartsWith("PIDS ").Because($"the host wrote '{line}' and exited with {host.ExitCode}");
            processes = Array.ConvertAll(line!["PIDS ".Length..].Split(' '), int.Parse);
            await AssertAllEndedAsync(processes);
        }
        finally
        {
            KillAll(processes);
        }
    }

    /// <summary>Starts and teardowns with the option leak no handle, the job's included.</summary>
    [Test]
    [NotInParallel]
    public async Task StartAndTeardownLeakNothing()
    {
        if (!OperatingSystem.IsWindows())
            return;

        await AssertNoLeakAsync(async () =>
        {
            await using (var session = Pty.Start(Exits(0) with { KillProcessTree = true }))
            {
                var output = ReadAllTextAsync(session);
                await session.WaitForExitAsync().WaitAsync(Timeout);
                await output.WaitAsync(Timeout);
            }

            try
            {
                Pty.Start(MissingProgram() with { KillProcessTree = true }).Dispose();
                throw new InvalidOperationException("The start did not fail.");
            }
            catch (Win32Exception)
            {
                // The start failed after creating the job, which it must release.
            }
        });
    }

    /// <summary>
    /// Unix: <see cref="PtySession.Kill(PtySignal)"/> signals the child's process group, so <c>SIGTERM</c> ends a
    /// descendant started with <c>nohup</c> together with the child.
    /// </summary>
    [Test]
    [NotInParallel(DescendantsGroup)]
    public async Task KillWithASignalReachesTheGroup()
    {
        if (OperatingSystem.IsWindows())
            return;

        int[] descendants = [];
        try
        {
            await using var session = StartWithDescendants([Descendant.Detached], out var pids, killProcessTree: true);
            descendants = await pids;

            session.Kill(PtySignal.Terminate);

            await Assert.That((await session.WaitForExitAsync().WaitAsync(Timeout)).Signal).IsEqualTo(15);
            await AssertAllEndedAsync(descendants);
        }
        finally
        {
            KillAll(descendants);
        }
    }

    /// <summary>Unix: whether <paramref name="pid"/> exists, a zombie included.</summary>
    private static bool Exists(int pid) => UnixInterop.kill(pid, 0) == 0 || Marshal.GetLastPInvokeError() != Esrch;

    private const int Esrch = 3;

    private static async Task AssertAllEndedAsync(int[] processes)
    {
        foreach (var pid in processes)
            await Assert.That(await WaitUntilAsync(() => !IsRunning(pid), Timeout)).IsTrue().Because($"process {pid} still runs");
    }

    /// <summary>
    /// Makes this test executable the host of <see cref="HostEndEndsEveryDescendant"/> when <see cref="TreeHostVariable"/>
    /// is set. Runs before the test framework starts and exits the process; in a test run the variable is unset and this
    /// returns at once.
    /// </summary>
    /// <remarks>
    /// Synchronous on this thread only: a module initializer runs inside the module's type initializer, so code from this
    /// assembly on another thread could wait for it and deadlock. The session's own threads run MiniPty code only.
    /// </remarks>
    [ModuleInitializer]
    internal static void RunAsTreeHostWhenAsked()
    {
        if (Environment.GetEnvironmentVariable(TreeHostVariable) is { } mode)
            Environment.Exit(RunTreeHost(mode));
    }

    /// <summary>
    /// Joins a job of its own (Windows), starts a session with the option whose child starts both descendants, writes
    /// <c>PIDS child onTerminal detached</c>, and then ends as <paramref name="mode"/> says without disposing the session:
    /// <c>exit</c> returns normally, <c>killed</c> terminates this process (Windows).
    /// </summary>
    private static int RunTreeHost(string mode)
    {
        if (OperatingSystem.IsWindows())
        {
            var outer = CreateJobObjectW(IntPtr.Zero, IntPtr.Zero);
            if (outer == IntPtr.Zero || !AssignProcessToJobObject(outer, new IntPtr(-1)))
                return 2;
        }

        var pidFile = NewPidFilePath();
        var session = Pty.Start(DescendantsChild(OnTerminalAndDetached, pidFile) with { KillProcessTree = true });
        var pids = new int[OnTerminalAndDetached.Length];
        var deadline = Environment.TickCount64 + (long)DescendantsStartTimeout.TotalMilliseconds;
        while (!TryReadPids(pidFile, OnTerminalAndDetached, pids))
        {
            if (Environment.TickCount64 >= deadline)
                return 3;

            Thread.Sleep(20);
        }

        DeletePidFile(pidFile);
        System.Console.WriteLine($"PIDS {session.ProcessId} {pids[0]} {pids[1]}");
        System.Console.Out.Flush();
        GC.KeepAlive(session);

        if (mode == "killed" && OperatingSystem.IsWindows())
            TerminateProcess(new IntPtr(-1), 9);

        return 0;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, IntPtr lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);
}
