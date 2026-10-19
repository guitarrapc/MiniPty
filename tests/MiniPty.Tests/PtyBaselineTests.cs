using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace MiniPty.Tests;

/// <summary>
/// The minimum a PTY session guarantees, one test per guarantee, on Windows (ConPTY) and Unix alike:
/// <list type="number">
/// <item>A start that fails leaks no handle or descriptor.</item>
/// <item>A child that exits by itself is reported, and disposing the session then releases everything.</item>
/// <item>Teardown can be repeated, in any order and from several threads.</item>
/// <item>Output ending before exit, or exit before output is read, breaks neither.</item>
/// <item>Output nobody reads blocks the child instead of growing a buffer.</item>
/// <item>A resize reaches the running child as a notification.</item>
/// <item>What teardown does to the child's descendants is fixed per platform.</item>
/// <item>Start failure, exit, and cancellation behave as documented on every platform.</item>
/// <item>Handles do not leak across the session: the child inherits none of the host's, and other children of the host
/// inherit none of the session's.</item>
/// </list>
/// </summary>
/// <remarks>
/// The tests use the public API and real children, because every guarantee concerns operating-system resources:
/// handles and descriptors, processes, signals, and the terminal. Other test classes cover the same areas in more
/// depth. Tests that count handles, descriptors, or allocations run alone (<c>[NotInParallel]</c> without a key),
/// since any other test changes those counts.
/// </remarks>
public sealed class PtyBaselineTests
{
    private const int ErrorFileNotFound = 2;
    private const int ErrorDirectory = 267;
    private const int ESRCH = 3;
    private const int SigKill = 9;
    // Set on this test executable to make it the child of ResizeReachesTheRunningChild on Windows.
    private const string ResizeChildVariable = "MINIPTY_TESTS_RESIZE_CHILD";
    // Set on this test executable to make it the child of StartWithDescendants on Windows: the descendants to start, and
    // the file their pids go to.
    private const string DescendantsChildVariable = "MINIPTY_TESTS_DESCENDANTS_CHILD";
    private const string PidFileVariable = "MINIPTY_TESTS_PID_FILE";

    /// <summary>
    /// Tests that start the descendants child run one at a time. The child is this executable with two descendants
    /// (on Windows a second .NET start-up, two <c>waitfor.exe</c>, and a console host), and nine such tests starting it
    /// together, at the moment the whole parallel suite also starts its children, left it unstarted within 30 s on the
    /// hosted runners (three at once on ARM64, one on windows-2025); under 3× CPU oversubscription on the desktop one
    /// took 32 s. One at a time, each gets the start-up window to itself.
    /// </summary>
    internal const string DescendantsGroup = "descendants";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long the descendants child may take to record its pids. A hang detector, not a performance assertion, so
    /// it is generous: starting the child is what a loaded hosted runner does slowest.
    /// </summary>
    internal static readonly TimeSpan DescendantsStartTimeout = TimeSpan.FromSeconds(60);

    // ---------------------------------------------------------------- 1. Start failure

    /// <summary>
    /// A missing program and a missing working directory fail the start. Windows reports them from
    /// <see cref="Pty.Start"/>, because <c>CreateProcess</c> fails. On Unix the fork has already succeeded when the exec
    /// or <c>chdir</c> fails, so the session starts and the child exits with the shell convention: 127 and 126.
    /// </summary>
    [Test]
    public async Task StartFailureIsReported()
    {
        if (OperatingSystem.IsWindows())
        {
            var missing = await Assert.ThrowsAsync<Win32Exception>(() => StartAndDispose(MissingProgram()));
            await Assert.That(missing!.NativeErrorCode).IsEqualTo(ErrorFileNotFound);
            var badDirectory = await Assert.ThrowsAsync<Win32Exception>(() => StartAndDispose(InMissingDirectory()));
            await Assert.That(badDirectory!.NativeErrorCode).IsEqualTo(ErrorDirectory);
            return;
        }

        await using (var missing = Pty.Start(MissingProgram()))
            await Assert.That((await missing.WaitForExitAsync().WaitAsync(Timeout)).ExitCode).IsEqualTo(127);

        await using (var badDirectory = Pty.Start(InMissingDirectory()))
            await Assert.That((await badDirectory.WaitForExitAsync().WaitAsync(Timeout)).ExitCode).IsEqualTo(126);

        static Task StartAndDispose(PtyStartInfo startInfo)
        {
            Pty.Start(startInfo).Dispose();
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A start that fails releases what it had already created. On Windows that is the pseudo-console, its pipes, and
    /// the attribute list; on Unix the session of a child whose exec failed.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task StartFailureLeaksNothing()
    {
        await AssertNoLeakAsync(async () =>
        {
            await FailToStartAsync(MissingProgram());
            await FailToStartAsync(InMissingDirectory());
        });

        static async Task FailToStartAsync(PtyStartInfo startInfo)
        {
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    Pty.Start(startInfo).Dispose();
                }
                catch (Win32Exception)
                {
                    return;
                }

                throw new InvalidOperationException("The start did not fail.");
            }

            await using var session = Pty.Start(startInfo);
            await session.WaitForExitAsync().WaitAsync(Timeout);
        }
    }

    // ---------------------------------------------------------------- 2. Natural exit

    /// <summary>
    /// A child that exits by itself reports its exit code, its output ends, and disposing the session releases every
    /// handle and descriptor. On Unix the child is also reaped, so no zombie remains.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task NaturalExitIsReportedAndDisposeReleasesEverything()
    {
        var pid = 0;
        await AssertNoLeakAsync(async () =>
        {
            await using var session = Pty.Start(Exits(7));
            var output = ReadAllTextAsync(session);
            var status = await session.WaitForExitAsync().WaitAsync(Timeout);
            await output.WaitAsync(Timeout);

            await Assert.That(status.ExitCode).IsEqualTo(7);
            pid = session.ProcessId;
        });

        if (!OperatingSystem.IsWindows())
            await Assert.That(UnixProcessExists(pid)).IsFalse();
    }

    // ---------------------------------------------------------------- 3. Repeated teardown

    /// <summary>
    /// Every teardown call can be repeated: kill before and after exit, dispose after kill, dispose and dispose-async
    /// in any order, and disposal from several threads at once. Only a kill after disposal is refused.
    /// </summary>
    [Test]
    public async Task TeardownCanBeRepeated()
    {
        var session = Pty.Start(Blocks());
        session.Kill();
        session.Kill();
        await session.WaitForExitAsync().WaitAsync(Timeout);
        session.Kill();
        session.Dispose();
        session.Dispose();
        await session.DisposeAsync().AsTask().WaitAsync(Timeout);
        await session.DisposeAsync().AsTask().WaitAsync(Timeout);
        session.Dispose();

        await Assert.That(session.HasExited).IsTrue();
        Assert.Throws<ObjectDisposedException>(() => session.Kill());

        var raced = Pty.Start(Blocks());
        using var start = new ManualResetEventSlim();
        var disposals = new Task[4];
        for (var i = 0; i < disposals.Length; i++)
        {
            disposals[i] = Task.Factory.StartNew(
                () =>
                {
                    start.Wait();
                    raced.Dispose();
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        start.Set();
        await Task.WhenAll(disposals).WaitAsync(Timeout);
        await raced.DisposeAsync().AsTask().WaitAsync(Timeout);
    }

    // ---------------------------------------------------------------- 4. Order of exit and end of output

    /// <summary>
    /// The child stops using the terminal and exits a second later, so output ends first. Ending output must not end
    /// the child: hanging the terminal up there would report 129 instead of its own 3. Windows has no such order, because
    /// ConPTY ends output only when the pseudo-console closes, and MiniPty closes it after exit.
    /// </summary>
    [Test]
    public async Task OutputEndingBeforeExitStillReportsTheExit()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var session = Pty.Start(Shell("printf ready; exec >/dev/null 2>&1 </dev/null; sleep 1; exit 3"));
        var text = await ReadAllTextAsync(session).WaitAsync(Timeout);
        var exitedWhenOutputEnded = session.HasExited;
        var status = await session.WaitForExitAsync().WaitAsync(Timeout);

        await Assert.That(text).Contains("ready");
        await Assert.That(status).IsEqualTo(new PtyExitStatus(3, null));
        // Linux ends output once no process holds the terminal, so the order is known there.
        if (OperatingSystem.IsLinux())
            await Assert.That(exitedWhenOutputEnded).IsFalse();
    }

    /// <summary>
    /// The child exits and its output ends before anyone reads; a reader started afterwards still gets all of it.
    /// </summary>
    [Test]
    public async Task ExitBeforeOutputIsReadStillDeliversTheOutput()
    {
        const string Marker = "baseline-exit-before-read";
        await using var session = Pty.Start(OperatingSystem.IsWindows() ? Cmd($"echo {Marker}") : Shell($"printf {Marker}"));
        await session.WaitForExitAsync().WaitAsync(Timeout);
        // Past the post-exit drain, which closes the transport after 100 ms of quiet.
        await Task.Delay(500);

        await Assert.That(await ReadAllTextAsync(session).WaitAsync(Timeout)).Contains(Marker);
    }

    /// <summary>
    /// A descendant that outlives the child and keeps the terminal open does not hold end of output: it ends shortly
    /// after the child exits, not when the descendant does. On Unix the descendant ignores the hang-up and holds the
    /// terminal as its output, so it is still running when output ends; on Windows it shares the pseudo-console.
    /// </summary>
    [Test]
    [NotInParallel(DescendantsGroup)]
    public async Task OutputEndsWithoutWaitingForADescendant()
    {
        int[] descendants = [];
        try
        {
            await using var session = StartWithDescendants([Descendant.HoldsTerminal], out var pids);
            var output = ReadAllTextAsync(session);
            descendants = await pids;

            var stopwatch = Stopwatch.StartNew();
            await EndChildAsync(session);
            var text = await output.WaitAsync(Timeout);
            var elapsed = stopwatch.Elapsed;

            await Assert.That(text).Contains("ready").Because("the output was: " + Escape(text));
            // The descendant lives 30 s; the bound is loose for a loaded runner.
            await Assert.That(elapsed).IsLessThan(TimeSpan.FromSeconds(15));
            if (!OperatingSystem.IsWindows())
                await Assert.That(IsRunning(descendants[0])).IsTrue();
        }
        finally
        {
            KillAll(descendants);
        }
    }

    // ---------------------------------------------------------------- 5. Bounded output buffer

    /// <summary>
    /// While nobody reads, a session holds a few kilobytes and the child blocks on its next write, as on a terminal
    /// nobody reads. The child here writes megabytes and then exits; it is still running after seconds of no reader, and
    /// the session allocated nothing meanwhile. Reading then delivers every byte.
    /// </summary>
    /// <remarks>
    /// <see cref="PtySession.CompleteAsync"/> and <c>PtyCapture</c> are the exception: a one-shot result keeps all
    /// output by design.
    /// </remarks>
    [Test]
    [NotInParallel]
    public async Task OutputNobodyReadsBlocksTheChild()
    {
        var size = OperatingSystem.IsWindows() ? 4_000_000 : 16_000_000;
        PtyStartInfo flood;
        if (OperatingSystem.IsWindows())
        {
            if (!TryGetWindowsPowerShell(out var powershell))
                return;

            flood = new PtyStartInfo { FileName = powershell, Arguments = ["-NoLogo", "-NoProfile", "-Command", $"[Console]::Out.Write(('x' * {size})); exit 0"] };
        }
        else
        {
            flood = Shell($"head -c {size} /dev/zero | tr '\\0' x; exit 0");
        }

        await using var session = Pty.Start(flood);
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        // Draining takes under a second on either platform, so a child still running after this has been blocked.
        await Task.Delay(TimeSpan.FromSeconds(OperatingSystem.IsWindows() ? 4 : 2));
        var allocatedWhileBlocked = GC.GetTotalAllocatedBytes(precise: true) - allocated;

        await Assert.That(session.HasExited).IsFalse();
        await Assert.That(allocatedWhileBlocked).IsLessThan(size / 16);

        long length = 0;
        using var cts = new CancellationTokenSource(Timeout);
        await foreach (var chunk in session.ReadOutputAsync(cts.Token))
            length += chunk.Data.Length;

        await Assert.That((await session.WaitForExitAsync().WaitAsync(Timeout)).ExitCode).IsEqualTo(0);
        await Assert.That(length).IsGreaterThanOrEqualTo(size);
    }

    // ---------------------------------------------------------------- 6. Resize

    /// <summary>
    /// A resize notifies the running child, which TUI programs redraw on: <c>SIGWINCH</c> on Unix, a
    /// <c>WINDOW_BUFFER_SIZE_EVENT</c> input record on Windows. The child reports the size it reads in response.
    /// </summary>
    [Test]
    public async Task ResizeReachesTheRunningChild()
    {
        var child = OperatingSystem.IsWindows()
            ? new PtyStartInfo
            {
                FileName = TestExecutable,
                Environment = new Dictionary<string, string?> { [ResizeChildVariable] = "1" },
            }
            : Shell("trap 'set -- $(stty size); printf \"SIZE:%s:%s;\" \"$1\" \"$2\"' WINCH; printf ready; " + UnixWaitLoop);
        var expected = OperatingSystem.IsWindows() ? "SIZE:100:30;" : "SIZE:30:100;";

        await using var session = Pty.Start(child);
        using var cts = new CancellationTokenSource(Timeout);
        await using var reader = session.ReadOutputAsync(cts.Token).GetAsyncEnumerator();
        var output = new StringBuilder();
        await ReadUntilAsync(reader, output, "ready");

        session.Resize(new PtySize(100, 30));

        await ReadUntilAsync(reader, output, expected);
    }

    /// <summary>
    /// Makes this test executable a Windows child of these tests when one of their variables is set: the resize child
    /// (<see cref="ResizeChildVariable"/>) or the descendants child (<see cref="DescendantsChildVariable"/>). Runs before
    /// the test framework starts and exits the process; in a test run the variables are unset and this returns at once.
    /// </summary>
    /// <remarks>
    /// This executable starts in a fraction of the time Windows PowerShell takes. A PowerShell child that started the
    /// descendants did not record them within the tests' 30 s on the hosted ARM64 runner under the parallel suite.
    /// </remarks>
    [ModuleInitializer]
    internal static void RunAsChildWhenAsked()
    {
        if (!OperatingSystem.IsWindows())
            return;

        if (Environment.GetEnvironmentVariable(ResizeChildVariable) is not null)
            Environment.Exit(RunWindowsResizeChild());

        if (Environment.GetEnvironmentVariable(DescendantsChildVariable) is { } descendants)
            Environment.Exit(RunWindowsDescendantsChild(descendants, Environment.GetEnvironmentVariable(PidFileVariable)!));
    }

    internal static string TestExecutable => Path.Combine(AppContext.BaseDirectory, "MiniPty.Tests.exe");

    /// <summary>
    /// Reports each <c>WINDOW_BUFFER_SIZE_EVENT</c> as <c>SIZE:columns:rows;</c>, as the child of
    /// <see cref="ResizeReachesTheRunningChild"/>. Ends by itself after 30 s.
    /// </summary>
    private static unsafe int RunWindowsResizeChild()
    {
        var input = CreateFileW("CONIN$", GenericRead | GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (input == new IntPtr(-1) || !GetConsoleMode(input, out var mode) || !SetConsoleMode(input, mode | EnableWindowInput))
            return 2;

        System.Console.Out.Write("ready");
        System.Console.Out.Flush();
        var records = stackalloc InputRecord[16];
        var deadline = Environment.TickCount64 + 30_000;
        while (Environment.TickCount64 < deadline)
        {
            if (WaitForSingleObject(input, 100) != 0)
                continue;

            if (!ReadConsoleInputW(input, records, 16, out var read))
                return 3;

            for (var i = 0; i < read; i++)
            {
                if (records[i].EventType == WindowBufferSizeEvent)
                {
                    System.Console.Out.Write($"SIZE:{records[i].SizeX}:{records[i].SizeY};");
                    System.Console.Out.Flush();
                }
            }
        }

        return 0;
    }

    // ---------------------------------------------------------------- 7. Descendants at teardown

    /// <summary>
    /// What disposal, or the child's own exit, does to the descendants it started. MiniPty signals only the child.
    /// On Unix the system hangs up the terminal's foreground process group when the child exits, so a descendant in
    /// that group ends, and one that ignores the hang-up (<c>nohup</c>) keeps running. On Windows closing the
    /// pseudo-console ends the descendants attached to it, and one in its own console keeps running. A descendant
    /// that survives belongs to the host from then on.
    /// </summary>
    [Test]
    [NotInParallel(DescendantsGroup)]
    [Arguments(true)]
    [Arguments(false)]
    public async Task TeardownEndsOnlyTheDescendantsOnTheTerminal(bool dispose)
    {
        int[] descendants = [];
        try
        {
            var session = StartWithDescendants([Descendant.OnTerminal, Descendant.Detached], out var pids);
            await using (session)
            {
                descendants = await pids;
                if (dispose)
                {
                    await session.DisposeAsync().AsTask().WaitAsync(Timeout);
                }
                else
                {
                    await EndChildAsync(session);
                    await ReadAllTextAsync(session).WaitAsync(Timeout);
                }
            }

            var onTerminal = descendants[0];
            var detached = descendants[1];
            await Assert.That(await WaitUntilAsync(() => !IsRunning(onTerminal), Timeout)).IsTrue();
            // Past the Unix disposal's one-second grace kill, which reaches only the child in any case.
            await Task.Delay(1500);
            await Assert.That(IsRunning(detached)).IsTrue();
        }
        finally
        {
            KillAll(descendants);
        }
    }

    // ---------------------------------------------------------------- 8. Cancellation

    /// <summary>
    /// Canceling a wait or a read stops only that operation, and the child keeps running. Canceling one-shot
    /// completion kills the child by default (<see cref="PtyCompleteOptions.KillOnCancellation"/>).
    /// </summary>
    [Test]
    public async Task CancellationStopsOnlyTheCanceledOperation()
    {
        await using var session = Pty.Start(Blocks());

        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
            await Assert.ThrowsAsync<OperationCanceledException>(() => session.WaitForExitAsync(cts.Token));

        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
        {
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                await foreach (var _ in session.ReadOutputAsync(cts.Token))
                {
                }
            });
        }

        await Assert.That(session.HasExited).IsFalse();

        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
            await Assert.ThrowsAsync<OperationCanceledException>(() => session.CompleteAsync(cancellationToken: cts.Token));

        await session.WaitForExitAsync().WaitAsync(Timeout);
        await Assert.That(session.HasExited).IsTrue();
    }

    // ---------------------------------------------------------------- 9. Handle inheritance

    /// <summary>
    /// The child inherits no handle or descriptor of the host. The host here holds one that any child would inherit:
    /// an inheritable pipe end on Windows, a descriptor without <c>FD_CLOEXEC</c> on Unix. On Windows the pipe reports
    /// broken as soon as the host closes its end, which it would not while the child or ConPTY's console host held a
    /// copy. On Unix the child looks for the descriptor's number.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task ChildInheritsNothingFromTheHost()
    {
        if (OperatingSystem.IsWindows())
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = 1 };
            if (!CreatePipe(out var read, out var write, ref attributes, 0))
                throw new Win32Exception();

            try
            {
                await using var session = Pty.Start(Blocks());
                CloseHandle(write);
                write = IntPtr.Zero;

                var peeked = PeekNamedPipe(read, IntPtr.Zero, 0, IntPtr.Zero, out _, IntPtr.Zero);
                await Assert.That(peeked ? 0 : Marshal.GetLastPInvokeError()).IsEqualTo(ErrorBrokenPipe);
            }
            finally
            {
                if (write != IntPtr.Zero)
                    CloseHandle(write);
                CloseHandle(read);
            }

            return;
        }

        using var file = File.OpenHandle("/dev/null");
        // dup clears FD_CLOEXEC, which .NET sets on every descriptor it opens.
        var fd = dup((int)file.DangerousGetHandle());
        try
        {
            await using var session = Pty.Start(Shell($"if [ -e /dev/fd/{fd} ]; then printf INHERITED; else printf CLEAN; fi"));
            await Assert.That(await ReadAllTextAsync(session).WaitAsync(Timeout)).Contains("CLEAN");
        }
        finally
        {
            close(fd);
        }
    }

    /// <summary>
    /// A session adds no handle or descriptor that the host's other children would inherit. Every one MiniPty keeps is
    /// non-inheritable on Windows and close-on-exec on Unix, also the PTY master, which glibc's <c>forkpty</c> opens
    /// without <c>O_CLOEXEC</c>. On Unix a child the host starts itself also finds no terminal descriptor of the session.
    /// Not covered: the moment inside <c>CreatePseudoConsole</c> when ConPTY briefly holds inheritable pipe ends, and on
    /// Linux the moment between <c>forkpty</c> returning and MiniPty marking the master, both too short to hit on purpose.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task HostChildrenInheritNothingFromTheSession()
    {
        var inheritableBefore = CountInheritableHandles();
        var terminalsBefore = OperatingSystem.IsWindows() ? "" : await TerminalDescriptorsOfAHostChildAsync();

        await using var session = Pty.Start(Blocks());

        await Assert.That(CountInheritableHandles()).IsLessThanOrEqualTo(inheritableBefore);
        if (!OperatingSystem.IsWindows())
            await Assert.That(await TerminalDescriptorsOfAHostChildAsync()).IsEqualTo(terminalsBefore);
    }

    /// <summary>
    /// Handles marked inheritable on Windows, descriptors above stdio without <c>FD_CLOEXEC</c> on Unix: what a child
    /// started with inheritance gets. Windows handle values are multiples of 4, scanned up to a bound far above what a
    /// test process uses.
    /// </summary>
    private static int CountInheritableHandles()
    {
        var count = 0;
        if (OperatingSystem.IsWindows())
        {
            for (var value = 4; value < 1 << 20; value += 4)
            {
                if (GetHandleInformation(value, out var flags) && (flags & HandleFlagInherit) != 0)
                    count++;
            }

            return count;
        }

        foreach (var entry in Directory.GetFileSystemEntries(OperatingSystem.IsLinux() ? "/proc/self/fd" : "/dev/fd"))
        {
            if (int.TryParse(Path.GetFileName(entry), out var fd) && fd > 2 && fcntl(fd, FGetFd, 0) is >= 0 and var flags && (flags & FdCloexec) == 0)
                count++;
        }

        return count;
    }

    /// <summary>The descriptors above stdio that are terminals in a child the host starts with <see cref="Process"/>.</summary>
    private static async Task<string> TerminalDescriptorsOfAHostChildAsync()
    {
        using var probe = Process.Start(new ProcessStartInfo("sh", ["-c", "i=3; while [ $i -lt 1024 ]; do if [ -t $i ]; then printf '%s ' $i; fi; i=$((i+1)); done"])
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        var terminals = await probe.StandardOutput.ReadToEndAsync().WaitAsync(Timeout);
        await probe.WaitForExitAsync().WaitAsync(Timeout);
        return terminals.Trim();
    }

    // ---------------------------------------------------------------- Children

    private static string ComSpec => Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";

    /// <summary>Waits about 30 s, in short sleeps so that a shell runs its traps promptly.</summary>
    private const string UnixWaitLoop = "i=0; while [ $i -lt 300 ]; do sleep 0.1; i=$((i+1)); done";

    private static PtyStartInfo Shell(string script) => new() { FileName = "sh", Arguments = ["-c", script] };

    private static PtyStartInfo Cmd(string command) => new() { FileName = ComSpec, Arguments = ["/c", command] };

    internal static PtyStartInfo MissingProgram() => new() { FileName = "minipty-baseline-no-such-program" };

    private static PtyStartInfo InMissingDirectory()
    {
        var startInfo = OperatingSystem.IsWindows() ? Cmd("exit 0") : Shell("exit 0");
        return startInfo with { WorkingDirectory = Path.Combine(Path.GetTempPath(), "minipty-baseline-missing-" + Guid.NewGuid().ToString("N")) };
    }

    internal static PtyStartInfo Exits(int code) => OperatingSystem.IsWindows() ? Cmd($"exit {code}") : Shell($"exit {code}");

    /// <summary>Waits on stdin, which nobody writes, so only teardown ends it.</summary>
    private static PtyStartInfo Blocks() => OperatingSystem.IsWindows() ? Cmd("set /p DUMMY=") : Shell("IFS= read -r _");

    /// <summary>A long-lived descendant the child starts; each lives about 30 s unless teardown ends it.</summary>
    internal enum Descendant
    {
        /// <summary>Unix: in the terminal's foreground process group. Windows: attached to the pseudo-console.</summary>
        OnTerminal,

        /// <summary>
        /// Unix: as <see cref="OnTerminal"/>, but ignores the hang-up, so it outlives the child with the terminal as its
        /// output. Windows: attached to the pseudo-console.
        /// </summary>
        HoldsTerminal,

        /// <summary>Unix: started with <c>nohup</c>. Windows: in a console of its own.</summary>
        Detached,
    }

    /// <summary>
    /// Starts a child that starts <paramref name="descendants"/>, prints <c>ready</c>, and waits for a line on stdin,
    /// on which it exits by itself (<see cref="EndChildAsync"/>). The pids complete in the order given, once each
    /// descendant is set up. They come through a file, so the output keeps a single consumer, the test.
    /// </summary>
    /// <remarks>
    /// A Unix descendant records its own pid after it has set up how it treats the hang-up. Recorded by the child
    /// instead, a <c>nohup</c> descendant could still be starting when the child exits, and the hang-up killed it.
    /// </remarks>
    internal static PtySession StartWithDescendants(Descendant[] descendants, out Task<int[]> pids, bool killProcessTree = false)
    {
        var pidFile = NewPidFilePath();
        var session = Pty.Start(DescendantsChild(descendants, pidFile) with { KillProcessTree = killProcessTree });
        pids = ReadPidFileAsync(pidFile, descendants);
        return session;
    }

    internal static string NewPidFilePath() => Path.Combine(Path.GetTempPath(), "minipty-baseline-" + Guid.NewGuid().ToString("N"));

    /// <summary>The child of <see cref="StartWithDescendants"/>, recording the descendants' pids in <paramref name="pidFile"/>.</summary>
    internal static PtyStartInfo DescendantsChild(Descendant[] descendants, string pidFile)
    {
        if (OperatingSystem.IsWindows())
        {
            return new PtyStartInfo
            {
                FileName = TestExecutable,
                Environment = new Dictionary<string, string?>
                {
                    [DescendantsChildVariable] = string.Join(',', descendants),
                    [PidFileVariable] = pidFile,
                },
            };
        }

        var script = new StringBuilder();
        foreach (var descendant in descendants)
        {
            var record = $"echo \"{descendant} $$\" >> \"{pidFile}\"; exec sleep 30";
            script.Append(descendant switch
            {
                Descendant.OnTerminal => $"sh -c '{record}' & ",
                Descendant.HoldsTerminal => $"sh -c 'trap \"\" HUP; {record}' & ",
                _ => $"nohup sh -c '{record}' >/dev/null 2>&1 & ",
            });
        }

        script.Append("printf ready; IFS= read -r _; exit 0");
        return Shell(script.ToString());
    }

    /// <summary>
    /// The Windows child of <see cref="StartWithDescendants"/>: starts each of <paramref name="descendants"/>, records
    /// their pids in <paramref name="pidFile"/>, prints <c>ready</c>, and exits once a line arrives on stdin.
    /// </summary>
    private static int RunWindowsDescendantsChild(string descendants, string pidFile)
    {
        var lines = new List<string>();
        foreach (var descendant in descendants.Split(','))
            lines.Add($"{descendant} {StartWindowsDescendant(detached: descendant == nameof(Descendant.Detached))}");

        File.WriteAllLines(pidFile, lines);
        System.Console.Out.Write("ready");
        System.Console.Out.Flush();
        System.Console.In.ReadLine();
        return 0;
    }

    /// <summary>
    /// Starts <c>waitfor</c>, which blocks silently for 30 s, attached to this process's console or in a hidden console
    /// of its own, and returns its pid. No handle is inherited, so an attached descendant holds nothing of this process
    /// but the console it joins.
    /// </summary>
    /// <remarks>
    /// Silent, so the child is the only process writing to the console. With a descendant that wrote at the same moment
    /// (<c>ping</c>), ConPTY's stream lacked the child's <c>ready</c> in 0.5–5 % of runs, depending on load, although the
    /// child wrote it and exited normally (see lifecycle.md, Lessons Learned).
    /// </remarks>
    private static int StartWindowsDescendant(bool detached)
    {
        var startupInfo = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        uint creationFlags = 0;
        if (detached)
        {
            creationFlags = CreateNewConsole;
            startupInfo.Flags = StartfUseShowWindow;
            startupInfo.ShowWindow = SwHide;
        }

        var commandLine = new StringBuilder($"\"{Path.Combine(Environment.SystemDirectory, "waitfor.exe")}\" /t 30 MiniPtyBaseline{Guid.NewGuid():N}");
        if (!CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, creationFlags, IntPtr.Zero, null, ref startupInfo, out var processInformation))
            throw new Win32Exception();

        CloseHandle(processInformation.Thread);
        CloseHandle(processInformation.Process);
        if (!detached)
            WaitUntilAttached(processInformation.ProcessId);

        return processInformation.ProcessId;
    }

    /// <summary>
    /// Waits until <paramref name="processId"/> is attached to this process's console. A process attaches during its
    /// own startup, after <c>CreateProcess</c> returns; one still starting when the pseudo-console closes never attaches
    /// and outlives it, so a descendant is reported only once it is attached.
    /// </summary>
    private static void WaitUntilAttached(int processId)
    {
        var attached = new uint[64];
        var deadline = Environment.TickCount64 + 30_000;
        while (Environment.TickCount64 < deadline)
        {
            var count = GetConsoleProcessList(attached, (uint)attached.Length);
            for (var i = 0; i < count && i < attached.Length; i++)
            {
                if (attached[i] == (uint)processId)
                    return;
            }

            Thread.Sleep(10);
        }

        throw new TimeoutException($"Process {processId} did not attach to the console.");
    }

    /// <summary>Sends the line on which a child from <see cref="StartWithDescendants"/> exits by itself.</summary>
    internal static Task EndChildAsync(PtySession session) =>
        session.WriteInputAsync(OperatingSystem.IsWindows() ? "\r\n" : "\n").AsTask().WaitAsync(Timeout);

    /// <summary>
    /// Waits until <paramref name="pidFile"/> has a <c>name pid</c> line for each descendant, then deletes it. Bounded by
    /// <see cref="DescendantsStartTimeout"/>, so callers await the task itself rather than wrapping it in the shorter
    /// general timeout.
    /// </summary>
    private static async Task<int[]> ReadPidFileAsync(string pidFile, Descendant[] descendants)
    {
        try
        {
            var pids = new int[descendants.Length];
            var found = await WaitUntilAsync(() => TryReadPids(pidFile, descendants, pids), DescendantsStartTimeout);
            return found ? pids : throw new TimeoutException("The descendants did not record their pids.");
        }
        finally
        {
            DeletePidFile(pidFile);
        }
    }

    /// <summary>Fills <paramref name="pids"/> from <paramref name="pidFile"/> once it lists every descendant.</summary>
    internal static bool TryReadPids(string pidFile, Descendant[] descendants, int[] pids)
    {
        if (!File.Exists(pidFile))
            return false;

        // The child may still be writing: only lines that already end in a newline count, so a pid cut short is never
        // read, and a read that collides with the writer tries again.
        string text;
        try
        {
            text = File.ReadAllText(pidFile);
        }
        catch (IOException)
        {
            return false;
        }

        var lines = text[..(text.LastIndexOf('\n') + 1)].Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < descendants.Length; i++)
        {
            pids[i] = 0;
            foreach (var line in lines)
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[0] == descendants[i].ToString())
                    pids[i] = int.Parse(parts[1]);
            }

            if (pids[i] == 0)
                return false;
        }

        return true;
    }

    internal static void DeletePidFile(string pidFile)
    {
        try
        {
            File.Delete(pidFile);
        }
        catch (IOException)
        {
            // The child still has it open; a temporary file is left behind.
        }
    }

    private static bool TryGetWindowsPowerShell(out string path)
    {
        path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(path);
    }

    // ---------------------------------------------------------------- Observation

    /// <summary>
    /// Runs <paramref name="iteration"/> often enough that a handle or descriptor leaked on each run stands out, after a
    /// warm-up that lasts until the count has held still for a while.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three kinds of growth are not leaks. The first starts in a process add handles that stay: on Windows a few dozen at
    /// a time, in a few steps over the first dozen or so starts, with the thread count all but unchanged. On Windows a
    /// closed pseudo-console lets go of its handles in the background within a second, so starts that fail quickly leave
    /// dozens still pending when the last one returns. So the warm-up lasts until the count holds still, and the count is
    /// read once it has settled, before and after.
    /// </para>
    /// <para>
    /// And anything else in the process can set itself up at any moment, once: the test platform's telemetry upload opens
    /// an HTTPS connection, and the certificate chain engine and HTTP stack it brings up add 28 to 49 handles at once
    /// (certificate store keys, a native thread pool, ETW registrations). A one-time step grows one batch of runs, while a
    /// leak grows every batch, so a batch that grew is followed by another, and the check fails only when both grew.
    /// </para>
    /// </remarks>
    internal static async Task AssertNoLeakAsync(Func<Task> iteration)
    {
        const int MaxWarmUp = 100;
        const int StillRuns = 10;

        var last = CountOpenHandles();
        for (int i = 0, still = 0; i < MaxWarmUp && still < StillRuns; i++)
        {
            await iteration();
            var count = CountOpenHandles();
            still = count <= last ? still + 1 : 0;
            last = count;
        }

        var grown = await GrowthOverLeakCheckRunsAsync(iteration);
        if (grown < LeakCheckSlack)
            return;

        var grownAgain = await GrowthOverLeakCheckRunsAsync(iteration);
        await Assert.That(grownAgain).IsLessThan(LeakCheckSlack)
            .Because($"{grown}, then {grownAgain} more handles or descriptors over two batches of {LeakCheckRuns} runs");
    }

    private const int LeakCheckRuns = 40;
    // Under one per two runs: a handle leaked on every run, or every other run, fails the check.
    private const int LeakCheckSlack = 20;

    /// <summary>How much the settled count grew over <see cref="LeakCheckRuns"/> runs, read once it falls under the slack or after 10 s.</summary>
    private static async Task<int> GrowthOverLeakCheckRunsAsync(Func<Task> iteration)
    {
        var before = await SettledHandleCountAsync();
        for (var i = 0; i < LeakCheckRuns; i++)
            await iteration();

        var grown = 0;
        await WaitUntilAsync(() => (grown = CountOpenHandles() - before) < LeakCheckSlack, TimeSpan.FromSeconds(10));
        return grown;
    }

    /// <summary>The handle or descriptor count once it has stopped falling, or after a few seconds.</summary>
    private static async Task<int> SettledHandleCountAsync()
    {
        var count = CountOpenHandles();
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(100);
            var next = CountOpenHandles();
            if (next >= count)
                return next;

            count = next;
        }

        return count;
    }

    /// <summary>Handles on Windows, descriptors on Unix, of this process.</summary>
    internal static int CountOpenHandles()
    {
        if (OperatingSystem.IsWindows())
            return GetProcessHandleCount(new IntPtr(-1), out var count) ? (int)count : throw new Win32Exception();

        return Directory.GetFileSystemEntries(OperatingSystem.IsLinux() ? "/proc/self/fd" : "/dev/fd").Length;
    }

    internal static async Task<string> ReadAllTextAsync(PtySession session)
    {
        using var output = new MemoryStream();
        await foreach (var chunk in session.ReadOutputAsync())
            output.Write(chunk.Data.Span);

        return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }

    /// <summary>Output with its control characters spelled out, for failure messages.</summary>
    private static string Escape(string text) =>
        text.Replace("\u001b", "\\e", StringComparison.Ordinal).Replace("\u0007", "\\a", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    private static async Task ReadUntilAsync(IAsyncEnumerator<PtyOutputChunk> reader, StringBuilder output, string text)
    {
        while (output.ToString().IndexOf(text, StringComparison.Ordinal) < 0)
        {
            if (!await reader.MoveNextAsync())
                throw new InvalidOperationException($"Output ended before '{text}': {output}");

            output.Append(Encoding.UTF8.GetString(reader.Current.Data.Span));
        }
    }

    internal static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() >= deadline)
                return false;

            await Task.Delay(50);
        }

        return true;
    }

    /// <summary>
    /// Whether the process is still running. A Unix descendant that ended stays a zombie until init reaps it, which
    /// counts as ended.
    /// </summary>
    internal static bool IsRunning(int pid)
    {
        if (OperatingSystem.IsWindows())
        {
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

        if (!UnixProcessExists(pid))
            return false;

        if (!OperatingSystem.IsLinux())
            return true;

        try
        {
            // The state follows the parenthesized command name: "pid (comm) S ...".
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            return !stat[(stat.LastIndexOf(')') + 2)..].StartsWith('Z');
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool UnixProcessExists(int pid) => kill(pid, 0) == 0 || Marshal.GetLastPInvokeError() != ESRCH;

    internal static void KillAll(int[] pids)
    {
        foreach (var pid in pids)
        {
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    using var process = Process.GetProcessById(pid);
                    process.Kill();
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
                {
                    // Already gone.
                }
            }
            else if (IsRunning(pid))
            {
                kill(pid, SigKill);
            }
        }
    }

    // ---------------------------------------------------------------- Interop

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint EnableWindowInput = 0x0008;
    private const ushort WindowBufferSizeEvent = 0x0004;

    /// <summary><c>INPUT_RECORD</c>, read only as a <c>WINDOW_BUFFER_SIZE_RECORD</c>.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 20)]
    private struct InputRecord
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public short SizeX;
        [FieldOffset(6)] public short SizeY;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessHandleCount(IntPtr hProcess, out uint pdwHandleCount);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool ReadConsoleInputW(IntPtr hConsoleInput, InputRecord* lpBuffer, uint nLength, out uint lpNumberOfEventsRead);

    private const uint CreateNewConsole = 0x00000010;
    private const int StartfUseShowWindow = 0x00000001;
    private const short SwHide = 0;

    /// <summary><c>STARTUPINFOW</c>; the string members are left null.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Size;
        public IntPtr Reserved2;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref StartupInfo lpStartupInfo,
        out ProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList([Out] uint[] lpdwProcessList, uint dwProcessCount);

    private const int ErrorBrokenPipe = 109;
    private const uint HandleFlagInherit = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, ref SecurityAttributes lpPipeAttributes, uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekNamedPipe(IntPtr hNamedPipe, IntPtr lpBuffer, uint nBufferSize, IntPtr lpBytesRead, out uint lpTotalBytesAvail, IntPtr lpBytesLeftThisMessage);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetHandleInformation(nint hObject, out uint lpdwFlags);

    private const int FGetFd = 1;
    private const int FdCloexec = 1;

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    [DllImport("libc", SetLastError = true)]
    private static extern int dup(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int fd, int cmd, int arg);
}
