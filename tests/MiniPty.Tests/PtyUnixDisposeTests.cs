using System.Diagnostics;
using System.Runtime.InteropServices;
using MiniPty.Internal;

namespace MiniPty.Tests;

/// <summary>
/// On Unix, disposing a session with a running child hangs the child up first and kills it only after a grace
/// period, so a shell gets to save its history, and a child that ignores the hang-up still does not outlive
/// the session for long. Dispose sends the hang-up without waiting, and the kill runs later on a timer.
/// </summary>
/// <remarks>
/// The process-exit kill test kills every child in its grace across the test process. The class runs its tests one at a
/// time so that it never cuts short the grace another test here is measuring.
/// </remarks>
[NotInParallel(nameof(PtyUnixDisposeTests))]
public sealed class PtyUnixDisposeTests
{
    private const int ESRCH = 3;
    private const int SigHup = 1;
    private const int SigKill = 9;

    [Test]
    public async Task DisposeHangsUpAChildThatExitsOnHangup()
    {
        if (OperatingSystem.IsWindows())
            return;

        var marker = NewMarkerPath();
        try
        {
            // With CLOCAL set, closing the master does not hang the child up on macOS. Only the explicit signal can
            // run the trap there before the grace kill. The file proves the trap ran, which a kill would prevent.
            using var session = Pty.Start(HangupScript($"stty clocal; trap 'echo hup > \"{marker}\"; exit 0' HUP"));
            var pid = session.ProcessId;
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
            File.Delete(marker);
        }
    }

    [Test]
    public async Task DisposeKillsAChildThatIgnoresHangupAfterTheGrace()
    {
        if (OperatingSystem.IsWindows())
            return;

        var marker = NewMarkerPath();
        try
        {
            // The trap records the hang-up but does not exit, so only the grace kill can end this child.
            using var session = Pty.Start(HangupScript($"trap 'echo hup > \"{marker}\"' HUP"));
            var pid = session.ProcessId;
            await WaitForReadyAsync(session);

            var sw = Stopwatch.StartNew();
            session.Dispose();
            await Assert.That(sw.ElapsedMilliseconds).IsLessThan(UnixPtyBackend.DisposeHangupGraceMs);

            await Assert.That(await WaitForFileContentAsync(marker)).Contains("hup");
            // A timer never fires early, so the child is still there halfway through the grace unless the kill came
            // too soon. A slow run that is already past the grace proves nothing either way and skips the check.
            await Task.Delay(UnixPtyBackend.DisposeHangupGraceMs / 2);
            if (sw.ElapsedMilliseconds < UnixPtyBackend.DisposeHangupGraceMs)
                await Assert.That(IsRunning(pid)).IsTrue();

            await Assert.That(await WaitForProcessGoneAsync(pid)).IsTrue();
            await Assert.That((await WaitForExitStatusAsync(session))?.Signal).IsEqualTo(SigKill);
        }
        finally
        {
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

    [Test]
    public async Task KillChildrenInHangupGraceKillsOnlyDisposedChildrenAtOnce()
    {
        if (OperatingSystem.IsWindows())
            return;

        // This is the kill the process-exit hook runs, called directly. The host can exit before the grace timer
        // fires, and a pending timer never runs at exit, so the kill must not wait for the grace. It must also spare
        // a session that was never disposed, which the host may still be using.
        using var running = Pty.Start(HangupScript("trap '' HUP"));
        using var disposed = Pty.Start(HangupScript("trap '' HUP"));
        var disposedPid = disposed.ProcessId;
        await WaitForReadyAsync(running);
        await WaitForReadyAsync(disposed);

        var sw = Stopwatch.StartNew();
        disposed.Dispose();
        UnixPtyBackend.KillChildrenInHangupGrace();

        await Assert.That(await WaitForProcessGoneAsync(disposedPid)).IsTrue();
        await Assert.That(sw.ElapsedMilliseconds).IsLessThan(UnixPtyBackend.DisposeHangupGraceMs);
        await Assert.That((await WaitForExitStatusAsync(disposed))?.Signal).IsEqualTo(SigKill);
        await Assert.That(IsRunning(running.ProcessId)).IsTrue();
        await Assert.That(running.HasExited).IsFalse();
    }

    private static bool IsRunning(int pid) => UnixInterop.kill(pid, 0) == 0;

    private static PtyStartInfo HangupScript(string trap) => new()
    {
        FileName = "sh",
        // A shell runs a trap only once the foreground command returns, so a short sleep lets the trap run promptly.
        Arguments = ["-c", $"{trap}; printf ready; while :; do sleep 0.1; done"],
    };

    private static string NewMarkerPath() =>
        Path.Combine(Path.GetTempPath(), "minipty-hup-" + Guid.NewGuid().ToString("N"));

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
    private static async Task<bool> WaitForProcessGoneAsync(int pid)
    {
        var deadline = Environment.TickCount64 + UnixPtyBackend.DisposeHangupGraceMs + 15_000;
        while (true)
        {
            if (UnixInterop.kill(pid, 0) != 0 && Marshal.GetLastPInvokeError() == ESRCH)
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
}
