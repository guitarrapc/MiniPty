using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MiniPty.Internal;
using MiniPty.Terminal;

namespace MiniPty.Tests;

/// <summary>
/// On Unix, disposing a session with a running child hangs the child up first and kills it only after a grace
/// period, so a shell gets to save its history, and a child that ignores the hang-up still does not outlive the
/// session for long. The hang-up is sent on the disposing thread and does not block. The kill comes from the
/// child watcher, or at once when the process exits normally.
/// </summary>
public sealed class PtyUnixDisposeTests
{
    private const int ESRCH = 3;
    private const int SigHup = 1;
    private const int SigKill = 9;
    private const int SigTerm = 15;
    // Set on a copy of this test executable to make it the host that HostExitKillsAChildStillInItsHangupGrace starts.
    // The value names how the host exits.
    private const string DisposeHostVariable = "MINIPTY_TESTS_DISPOSE_HOST";

    [Test]
    public async Task DisposeHangsUpAChildThatExitsOnHangup()
    {
        if (OperatingSystem.IsWindows())
            return;

        var marker = NewMarkerPath();
        // With CLOCAL set, closing the master does not hang the child up on macOS. Only the explicit signal can run the
        // trap there before the grace kill. The file proves the trap ran, which a kill would prevent.
        var session = Pty.Start(HangupScript($"stty clocal; trap 'echo hup > \"{marker}\"; exit 0' HUP"));
        var pid = session.ProcessId;
        try
        {
            await WaitForReadyAsync(session);

            var sw = Stopwatch.StartNew();
            session.Dispose();
            await Assert.That(sw.ElapsedMilliseconds).IsLessThan(UnixPtyBackend.DisposeHangupGraceMs);

            await Assert.That(await WaitForFileContentAsync(marker)).Contains("hup");
            await Assert.That(await WaitForProcessGoneAsync(pid)).IsTrue();
            await Assert.That(await WaitForExitStatusAsync(session)).IsEqualTo(new PtyExitStatus(0, null));
        }
        finally
        {
            session.Dispose();
            File.Delete(marker);
        }
    }

    [Test]
    public async Task DisposeKillsAChildThatIgnoresHangupAfterTheGrace()
    {
        if (OperatingSystem.IsWindows())
            return;

        var marker = NewMarkerPath();
        // The trap records the hang-up but does not exit, so only the kill after the grace can end this child.
        var session = Pty.Start(HangupScript($"trap 'echo hup > \"{marker}\"' HUP"));
        var pid = session.ProcessId;
        try
        {
            await WaitForReadyAsync(session);

            var sw = Stopwatch.StartNew();
            session.Dispose();
            await Assert.That(sw.ElapsedMilliseconds).IsLessThan(UnixPtyBackend.DisposeHangupGraceMs);
            await Assert.That(await WaitForFileContentAsync(marker)).Contains("hup");

            // Checked late in the grace, so a kill sent too early fails here. A check that a loaded machine delays
            // past the deadline proves nothing and is skipped.
            var lateInGrace = UnixPtyBackend.DisposeHangupGraceMs * 7 / 10;
            if (sw.ElapsedMilliseconds < lateInGrace)
                await Task.Delay(TimeSpan.FromMilliseconds(lateInGrace - sw.ElapsedMilliseconds));
            var alive = IsRunning(pid);
            if (sw.ElapsedMilliseconds < UnixPtyBackend.DisposeHangupGraceMs)
                await Assert.That(alive).IsTrue();

            await Assert.That(await WaitForProcessGoneAsync(pid)).IsTrue();
            await Assert.That((await WaitForExitStatusAsync(session))?.Signal).IsEqualTo(SigKill);
        }
        finally
        {
            session.Dispose();
            File.Delete(marker);
        }
    }

    /// <summary>
    /// Closing the master hangs the tty up only when no other process holds a copy of it, and a process the host
    /// starts can inherit one. This test holds a duplicate itself, so the trap can run only because disposal sent
    /// the hang-up explicitly.
    /// </summary>
    [Test]
    public async Task DisposeHangsUpTheChildWhileAnotherHolderKeepsTheMasterOpen()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var marker = NewMarkerPath();
        var session = Pty.Start(HangupScript($"trap 'echo hup > \"{marker}\"; exit 0' HUP"));
        var pid = session.ProcessId;
        var duplicate = -1;
        try
        {
            // Once the child is running its terminal is the slave, which names the master to duplicate.
            await WaitForReadyAsync(session);
            var master = FindMasterOf(pid);
            await Assert.That(master).IsGreaterThanOrEqualTo(0);
            duplicate = dup(master);
            await Assert.That(duplicate).IsGreaterThanOrEqualTo(0);

            session.Dispose();

            await Assert.That(await WaitForFileContentAsync(marker)).Contains("hup");
            await Assert.That(await WaitForProcessGoneAsync(pid)).IsTrue();
            await Assert.That(await WaitForExitStatusAsync(session)).IsEqualTo(new PtyExitStatus(0, null));
        }
        finally
        {
            session.Dispose();
            if (duplicate >= 0)
                UnixInterop.close(duplicate);
            File.Delete(marker);
        }
    }

    /// <summary>
    /// The host disposes a session whose child ignores the hang-up and loops for about 30 s, then exits at once, either
    /// right after the disposal or by disposing from its own exit handler, which runs after MiniPty's. The child watcher
    /// dies with the host, so only the kill sent at exit can end the child within the wait.
    /// </summary>
    /// <remarks>
    /// Only a process that really exits can show that MiniPty subscribed to the exit, so the host is this test
    /// executable started again (see <see cref="RunAsDisposeHostWhenAsked"/>). Both modes exit through
    /// <see cref="Environment.Exit(int)"/>, which raises the same exit event as returning from <c>Main</c>.
    /// </remarks>
    [Test]
    [Arguments("exit")]
    [Arguments("exit-handler")]
    public async Task HostExitKillsAChildStillInItsHangupGrace(string mode)
    {
        if (OperatingSystem.IsWindows())
            return;

        var hostPath = Path.Combine(AppContext.BaseDirectory, "MiniPty.Tests");
        await Assert.That(File.Exists(hostPath)).IsTrue();

        using var host = Process.Start(new ProcessStartInfo(hostPath)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            Environment = { [DisposeHostVariable] = mode },
        })!;
        var pidLine = await host.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await Assert.That(host.ExitCode).IsEqualTo(0);
        await Assert.That(int.TryParse(pidLine, out var pid)).IsTrue();

        // A child left behind by a failure is not killed here: init reaps it, so its pid could already be reused. It ends
        // by itself once its loop finishes.
        var gone = await WaitForAsync(() => UnixInterop.kill(pid, 0) != 0 && Marshal.GetLastPInvokeError() == ESRCH, 5_000);
        await Assert.That(gone).IsTrue();
    }

    /// <summary>
    /// The host disposes a session whose child ignores the hang-up, and is then ended by a signal it does not handle,
    /// within the grace period. Such a host raises no <c>ProcessExit</c>, so MiniPty's own handler for the signal must
    /// send the kill. The handler does not cancel the signal: the host ends with the same status it would without it.
    /// </summary>
    /// <remarks>
    /// <c>SIGINT</c> and <c>SIGQUIT</c> share the handler and are left out: a process started from a background job
    /// ignores <c>SIGINT</c>, and <c>SIGQUIT</c> writes a core dump. A signal that a loaded machine delays past the grace
    /// lets the watcher's own kill pass this test, so it can pass without the handler then, but never fail with it.
    /// </remarks>
    [Test]
    [Arguments(SigTerm)]
    [Arguments(SigHup)]
    public async Task HostEndedBySignalKillsAChildStillInItsHangupGrace(int signal)
    {
        if (OperatingSystem.IsWindows())
            return;

        var hostPath = Path.Combine(AppContext.BaseDirectory, "MiniPty.Tests");
        using var host = Process.Start(new ProcessStartInfo(hostPath)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            Environment = { [DisposeHostVariable] = "dispose-then-wait" },
        })!;
        try
        {
            var pidLine = await host.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var disposed = await host.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await Assert.That(disposed).IsEqualTo("disposed");
            await Assert.That(UnixInterop.kill(host.Id, signal)).IsEqualTo(0);

            await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await Assert.That(host.ExitCode).IsEqualTo(128 + signal);
            await Assert.That(int.TryParse(pidLine, out var pid)).IsTrue();

            var gone = await WaitForAsync(() => UnixInterop.kill(pid, 0) != 0 && Marshal.GetLastPInvokeError() == ESRCH, 5_000);
            await Assert.That(gone).IsTrue();
        }
        finally
        {
            if (!host.HasExited)
                host.Kill();
        }
    }

    [Test]
    public async Task PtyTerminalDisposeHangsUpTheChild()
    {
        if (OperatingSystem.IsWindows())
            return;

        var marker = NewMarkerPath();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new MemoryStream();
        var terminal = PtyTerminal.Start(HangupScript($"trap 'echo hup > \"{marker}\"; exit 0' HUP"), new PtyTerminalOptions
        {
            Output = (data, _) =>
            {
                output.Write(data.Span);
                if (output.GetBuffer().AsSpan(0, checked((int)output.Length)).IndexOf("ready"u8) >= 0)
                    ready.TrySetResult();
                return ValueTask.CompletedTask;
            },
        });
        var pid = terminal.ProcessId;
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await terminal.DisposeAsync();

            await Assert.That(await WaitForFileContentAsync(marker)).Contains("hup");
            await Assert.That(await WaitForProcessGoneAsync(pid)).IsTrue();
        }
        finally
        {
            await terminal.DisposeAsync();
            File.Delete(marker);
        }
    }

    [Test]
    public async Task DisposeAsyncEndsAChildWithTheDefaultHangupAction()
    {
        if (OperatingSystem.IsWindows())
            return;

        // No trap, so the hang-up itself terminates the child. A grace kill would report SIGKILL instead.
        await using var session = Pty.Start(HangupScript(":"));
        await WaitForReadyAsync(session);

        await session.DisposeAsync();

        await Assert.That(await WaitForExitStatusAsync(session)).IsEqualTo(new PtyExitStatus(128 + SigHup, SigHup));
    }

    [Test]
    public async Task DisposeAfterExitKeepsTheChildsOwnStatus()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var session = Pty.Start(new PtyStartInfo { FileName = "sh", Arguments = ["-c", "exit 3"] });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await session.WaitForExitAsync(cts.Token);

        await session.DisposeAsync();

        await Assert.That(session.ExitStatus).IsEqualTo(new PtyExitStatus(3, null));
    }

    /// <summary>
    /// Makes this test executable the host of <see cref="HostExitKillsAChildStillInItsHangupGrace"/> and
    /// <see cref="HostEndedBySignalKillsAChildStillInItsHangupGrace"/> when
    /// <see cref="DisposeHostVariable"/> is set. It runs before the test framework starts and exits the process, so no
    /// test runs in the host. In a test run the variable is unset and this returns at once.
    /// </summary>
    /// <remarks>
    /// The host code is synchronous and runs on this thread only. A module initializer runs inside the module's type
    /// initializer, so code from this assembly on another thread, such as an <c>await</c> continuation, could wait for it
    /// to finish and deadlock.
    /// </remarks>
    [ModuleInitializer]
    internal static void RunAsDisposeHostWhenAsked()
    {
        var mode = Environment.GetEnvironmentVariable(DisposeHostVariable);
        if (mode is not null)
            Environment.Exit(RunDisposeHost(mode));
    }

    /// <summary>
    /// Starts a session whose child ignores the hang-up, prints the child's pid once the child is ready, and disposes
    /// the session now, from an exit handler, or now and then waits for the test to end the host with a signal.
    /// Returns the exit code for the host.
    /// </summary>
    private static int RunDisposeHost(string mode)
    {
        var session = Pty.Start(HangupScript("trap '' HUP"));
        var output = new MemoryStream();
        var buffer = new byte[256];
        while (output.GetBuffer().AsSpan(0, checked((int)output.Length)).IndexOf("ready"u8) < 0)
        {
            var read = session.Output.Read(buffer);
            if (read == 0)
                return 3;

            output.Write(buffer, 0, read);
        }

        System.Console.WriteLine(session.ProcessId);
        System.Console.Out.Flush();

        switch (mode)
        {
            case "exit":
                session.Dispose();
                return 0;
            case "exit-handler":
                // Subscribed after Pty.Start, so it runs after MiniPty's own exit handler. The disposal then has to kill
                // at once, because the deadline it would otherwise wait for never comes.
                AppDomain.CurrentDomain.ProcessExit += (_, _) => session.Dispose();
                return 0;
            case "dispose-then-wait":
                // The test ends this host with a signal while the child is still in its grace period.
                session.Dispose();
                System.Console.WriteLine("disposed");
                System.Console.Out.Flush();
                Thread.Sleep(30_000);
                return 4;
            default:
                System.Console.Error.WriteLine($"Unknown mode '{mode}'.");
                return 2;
        }
    }

    private static bool IsRunning(int pid) => UnixInterop.kill(pid, 0) == 0;

    private static PtyStartInfo HangupScript(string trap) => new()
    {
        FileName = "sh",
        // A shell runs a trap only once the foreground command returns, so short sleeps let the trap run promptly. The
        // loop ends by itself after about 30 s, so a child that a failed test leaves behind does not run forever.
        Arguments = ["-c", $"{trap}; printf ready; i=0; while [ $i -lt 300 ]; do sleep 0.1; i=$((i+1)); done"],
    };

    private static string NewMarkerPath() =>
        Path.Combine(Path.GetTempPath(), "minipty-hup-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// The descriptor of the master whose slave is <paramref name="pid"/>'s standard input, or -1 (Linux). Matched by
    /// the pts number in <c>tty-index</c> rather than by descriptor number, because a master that another session closes
    /// meanwhile can hand its number to the next one opened.
    /// </summary>
    private static int FindMasterOf(int pid)
    {
        var slave = new FileInfo($"/proc/{pid}/fd/0").LinkTarget;
        if (slave is null || !slave.StartsWith("/dev/pts/", StringComparison.Ordinal))
            return -1;

        var index = slave["/dev/pts/".Length..];
        foreach (var path in Directory.EnumerateFileSystemEntries("/proc/self/fd"))
        {
            try
            {
                if (new FileInfo(path).LinkTarget?.EndsWith("ptmx", StringComparison.Ordinal) != true)
                    continue;

                var name = Path.GetFileName(path);
                foreach (var line in File.ReadLines($"/proc/self/fdinfo/{name}"))
                {
                    if (line.StartsWith("tty-index:", StringComparison.Ordinal) && line["tty-index:".Length..].Trim() == index)
                        return int.Parse(name);
                }
            }
            catch (IOException)
            {
                // The descriptor closed while being listed.
            }
        }

        return -1;
    }

    /// <summary>Observes the child's own output so that the hang-up is never sent before the trap is installed.</summary>
    private static async Task WaitForReadyAsync(PtySession session)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var output = new MemoryStream();
        await using var reader = session.ReadOutputAsync(cts.Token).GetAsyncEnumerator();
        while (output.GetBuffer().AsSpan(0, checked((int)output.Length)).IndexOf("ready"u8) < 0)
        {
            await Assert.That(await reader.MoveNextAsync()).IsTrue();
            output.Write(reader.Current.Data.Span);
        }
    }

    private static async Task<string> WaitForFileContentAsync(string path)
    {
        var deadline = Environment.TickCount64 + 15_000;
        while (true)
        {
            if (File.Exists(path))
            {
                var content = await File.ReadAllTextAsync(path);
                if (content.Length > 0)
                    return content;
            }

            if (Environment.TickCount64 >= deadline)
                return string.Empty;

            await Task.Delay(50);
        }
    }

    /// <summary>
    /// True once the pid is gone. A killed child stays a zombie until the watcher reaps it, and <c>kill(pid, 0)</c>
    /// still succeeds on a zombie, so this also covers the reap.
    /// </summary>
    private static Task<bool> WaitForProcessGoneAsync(int pid) =>
        WaitForAsync(
            () => UnixInterop.kill(pid, 0) != 0 && Marshal.GetLastPInvokeError() == ESRCH,
            UnixPtyBackend.DisposeHangupGraceMs + 15_000);

    private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            if (condition())
                return true;

            if (Environment.TickCount64 >= deadline)
                return false;

            await Task.Delay(50);
        }
    }

    /// <summary>
    /// The status the watcher records after disposal. <c>WaitForExitAsync</c> fails once the session is disposed, so
    /// this polls the property, which keeps reporting the reap.
    /// </summary>
    private static async Task<PtyExitStatus?> WaitForExitStatusAsync(PtySession session)
    {
        var deadline = Environment.TickCount64 + UnixPtyBackend.DisposeHangupGraceMs + 15_000;
        while (session.ExitStatus is null && Environment.TickCount64 < deadline)
            await Task.Delay(20);

        return session.ExitStatus;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int dup(int fd);
}
