using System.Diagnostics;
using System.Runtime.InteropServices;
using MiniPty.Internal;

namespace MiniPty.Tests;

/// <summary>
/// On Unix, disposing a session with a running child hangs the child up first and kills it only after a grace
/// period, so a shell gets to save its history, and a child that ignores the hang-up still does not outlive
/// the session for long. Neither step runs on the disposing thread.
/// </summary>
public sealed class PtyUnixDisposeTests
{
    private const int ESRCH = 3;

    [Test]
    public async Task DisposeHangsUpAChildThatExitsOnHangup()
    {
        if (OperatingSystem.IsWindows())
            return;

        var marker = NewMarkerPath();
        try
        {
            // Exiting from the trap is what a shell does with SIGHUP; the file proves the trap ran, which it cannot
            // have done if the child had been killed outright.
            var session = Pty.Start(HangupScript($"trap 'echo hup > \"{marker}\"; exit 0' HUP"));
            var pid = session.ProcessId;
            await WaitForReadyAsync(session);

            var sw = Stopwatch.StartNew();
            session.Dispose();
            await Assert.That(sw.ElapsedMilliseconds).IsLessThan(UnixPtyBackend.DisposeHangupGraceMs);

            await Assert.That(await WaitForFileContentAsync(marker)).Contains("hup");
            await Assert.That(await WaitForProcessGoneAsync(pid)).IsTrue();
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
            var session = Pty.Start(HangupScript($"trap 'echo hup > \"{marker}\"' HUP"));
            var pid = session.ProcessId;
            await WaitForReadyAsync(session);

            var sw = Stopwatch.StartNew();
            session.Dispose();
            await Assert.That(sw.ElapsedMilliseconds).IsLessThan(UnixPtyBackend.DisposeHangupGraceMs);

            await Assert.That(await WaitForFileContentAsync(marker)).Contains("hup");
            await Assert.That(await WaitForProcessGoneAsync(pid)).IsTrue();
        }
        finally
        {
            File.Delete(marker);
        }
    }

    private static PtyStartInfo HangupScript(string trap) => new()
    {
        FileName = "sh",
        // A short sleep keeps the trap prompt: a shell runs a trap only once the foreground command returns.
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
}
