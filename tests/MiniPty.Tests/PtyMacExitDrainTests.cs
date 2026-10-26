using System.Diagnostics;
using System.Runtime.InteropServices;
using MiniPty.Internal;

namespace MiniPty.Tests;

/// <summary>
/// On macOS a child's last output must survive a consumer that has stopped reading when the child exits. When the
/// exit closes the terminal's last descriptor, macOS waits at most 0.6 s for the master to read what the terminal
/// still holds (up to 1 KiB) and then discards it, so a session whose ring is full at that moment used to lose the
/// child's last output. MiniPty therefore holds a descriptor of the slave itself until the master closes: the exit is
/// never the last close, and macOS then waits for the master without a bound, so the exit waits for the consumer
/// instead of discarding.
/// </summary>
/// <remarks>
/// The child is zsh, whose exit is the terminal's last close. bash's exit was not, for a reason not identified, so it
/// waited for the read even without MiniPty's descriptor and cannot show the loss.
/// </remarks>
public sealed class PtyMacExitDrainTests
{
    private const int ESRCH = 3;
    private const int TailBytes = 500;
    // How long the consumer stays away while it waits for the child's exit: past the child's 0.3 s sleep plus the
    // terminal's 0.6 s window, so without the fix the child has exited and its tail is gone by then.
    private const int StalledMs = 1500;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The child fills the ring, then leaves <see cref="TailBytes"/> in the terminal and exits while nobody reads.
    /// Every byte must arrive once the consumer reads, and the exit must not be reported before that read: macOS keeps
    /// the exit waiting until the terminal is drained.
    /// </summary>
    [Test]
    public async Task OutputLeftInTheTerminalAtExitSurvivesAStalledConsumer()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        await using var session = Pty.Start(TailScript());
        var tty = await TerminalOfAsync(session.ProcessId);
        var node = NodeOf(tty);
        await Assert.That(node).IsNotEmpty();

        var exit = session.WaitForExitAsync();
        var exitedWhileStalled = await Task.WhenAny(exit, Task.Delay(StalledMs)) == exit;
        // Without the fix the child exited at about 0.9 s. Waiting out the terminal's window makes the loss certain.
        if (exitedWhileStalled)
            await Task.Delay(1000);

        var (filler, tail) = await CountOutputAsync(session);
        var status = await exit.WaitAsync(Timeout);

        await Assert.That(tail).IsEqualTo(TailBytes);
        await Assert.That(filler).IsEqualTo(PtyOutputPump.Capacity);
        await Assert.That(exitedWhileStalled).IsFalse().Because("the exit waits until the terminal is drained");
        await Assert.That(status).IsEqualTo(new PtyExitStatus(0, null));

        await session.DisposeAsync();
        await Assert.That(await WaitForTerminalGoneAsync(tty, node)).IsTrue().Because($"the session's descriptors of {tty} close with it");
    }

    /// <summary>
    /// A child held in its exit by a stalled consumer must not hold up disposal: disposal discards the output, the
    /// exit completes, the child is reaped, and the session's descriptors of the terminal close.
    /// </summary>
    [Test]
    public async Task DisposeEndsAChildHeldInItsExitByAStalledConsumer()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var session = Pty.Start(TailScript());
        var pid = session.ProcessId;
        var tty = await TerminalOfAsync(pid);
        var node = NodeOf(tty);
        try
        {
            await Assert.That(node).IsNotEmpty();
            await Assert.That(HostHoldsTerminal(tty)).IsTrue().Because($"the session holds a descriptor of {tty}");
            await Task.Delay(StalledMs);

            var sw = Stopwatch.StartNew();
            await session.DisposeAsync().AsTask().WaitAsync(Timeout);
            await Assert.That(sw.ElapsedMilliseconds).IsLessThan(UnixPtyBackend.DisposeHangupGraceMs + 5_000);

            await Assert.That(await WaitForProcessGoneAsync(pid)).IsTrue();
            await Assert.That(await WaitForTerminalGoneAsync(tty, node)).IsTrue().Because($"the session's descriptors of {tty} close with it");
        }
        finally
        {
            session.Dispose();
        }
    }

    /// <summary>
    /// The slave descriptor a session holds is close-on-exec like its master, so a child the host starts while the
    /// session is open does not get it. One that did would keep the terminal open after the session ended.
    /// </summary>
    [Test]
    public async Task AChildTheHostStartsDoesNotInheritTheSessionsSlave()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        await using var session = Pty.Start(new PtyStartInfo { FileName = "/bin/sleep", Arguments = ["30"] });
        var tty = await TerminalOfAsync(session.ProcessId);
        await Assert.That(HostHoldsTerminal(tty)).IsTrue().Because($"the session holds a descriptor of {tty}");

        using var other = Process.Start(new ProcessStartInfo("/bin/sleep", ["30"]) { UseShellExecute = false })!;
        try
        {
            await Assert.That(HoldsTerminal(other.Id, tty)).IsFalse().Because($"a child the host started inherited {tty}");
        }
        finally
        {
            other.Kill();
            await other.WaitForExitAsync();
        }
    }

    /// <summary>A zsh child that writes exactly a ring of filler, sleeps, then writes the tail and exits.</summary>
    private static PtyStartInfo TailScript() => new()
    {
        FileName = "/bin/zsh",
        Arguments =
        [
            "-c",
            $"head -c {PtyOutputPump.Capacity} /dev/zero | tr '\\0' x; sleep 0.3; head -c {TailBytes} /dev/zero | tr '\\0' Z",
        ],
    };

    private static async Task<(int Filler, int Tail)> CountOutputAsync(PtySession session)
    {
        var filler = 0;
        var tail = 0;
        using var cts = new CancellationTokenSource(Timeout);
        await foreach (var chunk in session.ReadOutputAsync(cts.Token))
        {
            foreach (var b in chunk.Data.Span)
            {
                if (b == (byte)'x')
                    filler++;
                else if (b == (byte)'Z')
                    tail++;
            }
        }

        return (filler, tail);
    }

    /// <summary>
    /// The child's terminal, such as <c>ttys012</c>, from <c>ps</c>. The spawn helper makes the slave the child's
    /// controlling terminal only once it runs, and <c>ps</c> reports <c>??</c> until then.
    /// </summary>
    private static async Task<string> TerminalOfAsync(int pid)
    {
        var tty = "";
        var deadline = Environment.TickCount64 + 10_000;
        while (Environment.TickCount64 < deadline)
        {
            using var ps = Process.Start(new ProcessStartInfo("/bin/ps", ["-o", "tty=", "-p", pid.ToString()])
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            })!;
            tty = (await ps.StandardOutput.ReadToEndAsync()).Trim();
            await ps.WaitForExitAsync();
            if (tty.StartsWith("ttys", StringComparison.Ordinal))
                return tty;

            await Task.Delay(20);
        }

        throw new InvalidOperationException($"ps reported terminal '{tty}' for pid {pid}");
    }

    private static bool HostHoldsTerminal(string tty) => HoldsTerminal(Environment.ProcessId, tty);

    /// <summary>Whether the process has the terminal open, from <c>lsof</c>, which prints nothing when it finds none.</summary>
    private static bool HoldsTerminal(int pid, string tty)
    {
        using var lsof = Process.Start(new ProcessStartInfo("/usr/sbin/lsof", ["-a", "-p", pid.ToString(), "--", $"/dev/{tty}"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        var output = lsof.StandardOutput.ReadToEnd();
        lsof.StandardError.ReadToEnd();
        lsof.WaitForExit();
        return output.Contains(tty, StringComparison.Ordinal);
    }

    /// <summary>
    /// The inode of the terminal's device node, or empty once the node is gone. Each terminal gets a node of its own, so
    /// the inode tells the session's terminal from a later one of the same name.
    /// </summary>
    private static string NodeOf(string tty)
    {
        using var stat = Process.Start(new ProcessStartInfo("/usr/bin/stat", ["-f", "%i", $"/dev/{tty}"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        var node = stat.StandardOutput.ReadToEnd().Trim();
        stat.StandardError.ReadToEnd();
        stat.WaitForExit();
        return node;
    }

    /// <summary>
    /// True once the session's terminal is gone. macOS removes a terminal's node only once every descriptor of it, master
    /// and slave, is closed, and the child is gone by then, so a descriptor the session kept would keep the node. The
    /// name is no proof: macOS gives a new terminal the lowest free number, so a test running alongside can open one
    /// under it as soon as the session lets go, and the host then holds the name again, under a node of its own. The
    /// watcher closes the session's descriptors just after it reaps the child, so this waits rather than checks once.
    /// </summary>
    /// <remarks>
    /// A slave descriptor left open alone would not show here: the session leader's exit revokes the terminal, which
    /// detaches that descriptor from it. A master left open keeps the node.
    /// </remarks>
    private static async Task<bool> WaitForTerminalGoneAsync(string tty, string node)
    {
        var deadline = Environment.TickCount64 + 5_000;
        while (NodeOf(tty) == node)
        {
            if (Environment.TickCount64 >= deadline)
                return false;

            await Task.Delay(50);
        }

        return true;
    }

    /// <summary>True once the pid is gone; <c>kill(pid, 0)</c> succeeds on a zombie, so this also covers the reap.</summary>
    private static async Task<bool> WaitForProcessGoneAsync(int pid)
    {
        var deadline = Environment.TickCount64 + UnixPtyBackend.DisposeHangupGraceMs + 15_000;
        while (Environment.TickCount64 < deadline)
        {
            if (UnixInterop.kill(pid, 0) != 0 && Marshal.GetLastPInvokeError() == ESRCH)
                return true;

            await Task.Delay(50);
        }

        return false;
    }
}
