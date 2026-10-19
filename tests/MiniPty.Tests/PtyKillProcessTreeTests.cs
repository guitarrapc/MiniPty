using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static MiniPty.Tests.PtyBaselineTests;

namespace MiniPty.Tests;

/// <summary>
/// <see cref="PtyStartInfo.KillProcessTree"/>: on Windows the child runs in a kill-on-close job, so disposal,
/// <see cref="PtySession.Kill()"/>, and the end of the host process end every process the child started, including one
/// in a console of its own. Without the option such a descendant outlives the session
/// (<see cref="PtyBaselineTests.TeardownEndsOnlyTheDescendantsOnTheTerminal"/>). Unix ignores the option.
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
        if (!OperatingSystem.IsWindows())
            return;

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
        if (!OperatingSystem.IsWindows())
            return;

        int[] descendants = [];
        try
        {
            await using var session = StartWithDescendants(OnTerminalAndDetached, out var pids, killProcessTree: true);
            descendants = await pids;

            session.Kill();

            await Assert.That((await session.WaitForExitAsync().WaitAsync(Timeout)).ExitCode).IsEqualTo(1);
            await AssertAllEndedAsync(descendants);
        }
        finally
        {
            KillAll(descendants);
        }
    }

    /// <summary>
    /// The session owns the tree until it is disposed: after the child exits by itself, a descendant in a console of its
    /// own keeps running, and disposal or <see cref="PtySession.Kill()"/> then ends it.
    /// </summary>
    [Test]
    [NotInParallel(DescendantsGroup)]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ADescendantOfAChildThatExitedRunsUntilTeardown(bool dispose)
    {
        if (!OperatingSystem.IsWindows())
            return;

        int[] descendants = [];
        try
        {
            var session = StartWithDescendants([Descendant.Detached], out var pids, killProcessTree: true);
            await using (session)
            {
                descendants = await pids;
                await EndChildAsync(session);
                await ReadAllTextAsync(session).WaitAsync(Timeout);
                await Assert.That((await session.WaitForExitAsync().WaitAsync(Timeout)).ExitCode).IsEqualTo(0);

                // Past the post-exit drain, which closes the pseudo-console; the job stays open.
                await Task.Delay(1500);
                await Assert.That(IsRunning(descendants[0])).IsTrue();

                if (dispose)
                    await session.DisposeAsync().AsTask().WaitAsync(Timeout);
                else
                    session.Kill();

                await AssertAllEndedAsync(descendants);
            }
        }
        finally
        {
            KillAll(descendants);
        }
    }

    /// <summary>
    /// The end of the host process ends the tree, whether the host exits without disposing the session or is killed:
    /// the kernel closes the job's only handle either way. The host also runs inside a job of its own, so the session's
    /// job is nested, as it is under CI runners and editors.
    /// </summary>
    [Test]
    [NotInParallel(DescendantsGroup)]
    [Arguments("exit")]
    [Arguments("killed")]
    public async Task HostEndEndsEveryDescendant(string mode)
    {
        if (!OperatingSystem.IsWindows())
            return;

        int[] processes = [];
        try
        {
            using var host = Process.Start(new ProcessStartInfo(TestExecutable)
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
    /// Unix ignores the option: disposal hangs the child up as without it, so a descendant in the terminal's foreground
    /// process group ends, and one started with <c>nohup</c> keeps running.
    /// </summary>
    [Test]
    [NotInParallel(DescendantsGroup)]
    public async Task UnixIgnoresIt()
    {
        if (OperatingSystem.IsWindows())
            return;

        int[] descendants = [];
        try
        {
            var session = StartWithDescendants(OnTerminalAndDetached, out var pids, killProcessTree: true);
            await using (session)
            {
                descendants = await pids;
                await session.DisposeAsync().AsTask().WaitAsync(Timeout);
            }

            await Assert.That(await WaitUntilAsync(() => !IsRunning(descendants[0]), Timeout)).IsTrue();
            // Past the disposal's one-second grace kill, which reaches only the child in any case.
            await Task.Delay(1500);
            await Assert.That(IsRunning(descendants[1])).IsTrue();
        }
        finally
        {
            KillAll(descendants);
        }
    }

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
        if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable(TreeHostVariable) is { } mode)
            Environment.Exit(RunTreeHost(mode));
    }

    /// <summary>
    /// Joins a job of its own, starts a session with the option whose child starts both descendants, writes
    /// <c>PIDS child onTerminal detached</c>, and then ends as <paramref name="mode"/> says without disposing the session:
    /// <c>exit</c> returns normally, <c>killed</c> terminates this process.
    /// </summary>
    private static int RunTreeHost(string mode)
    {
        var outer = CreateJobObjectW(IntPtr.Zero, IntPtr.Zero);
        if (outer == IntPtr.Zero || !AssignProcessToJobObject(outer, new IntPtr(-1)))
            return 2;

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

        if (mode == "killed")
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
