using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using MiniPty.Internal;

namespace MiniPty.Tests;

/// <summary>
/// A Ctrl+C written to a session interrupts its child, as typing it into a terminal does: on Unix the line discipline
/// sends <c>SIGINT</c> to the terminal's foreground process group, and on Windows ConPTY raises <c>CTRL_C_EVENT</c> for
/// the processes attached to the pseudo-console.
/// </summary>
/// <remarks>
/// On Windows a child inherits whether its creator ignores Ctrl+C, and a host started from Git Bash, or with
/// <c>CREATE_NEW_PROCESS_GROUP</c>, does. MiniPty creates the child from a host that processes it
/// (<see cref="WindowsHostCtrlC"/>). The tests that set this process's attribute and check it afterwards run alone: every
/// session another test starts meanwhile joins the window in which the attribute is cleared, so it is set again only
/// once that start is done too (it failed in the parallel suite before), and a test that injects input into this
/// process's console could deliver a Ctrl+C to it while the attribute is cleared.
/// </remarks>
public sealed partial class PtyCtrlCTests
{
    // What the child writes once it runs: the Unix shell echoes a marker, and ping names the address it pings on its
    // first line in every language.
    private static readonly string Ready = OperatingSystem.IsWindows() ? "127.0.0.1" : "minipty-ctrl-c-ready";
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(30);
    // The child would run on for about two minutes; a Ctrl+C ends it in milliseconds, and a loaded runner takes longer.
    private static readonly TimeSpan InterruptTimeout = TimeSpan.FromSeconds(15);

    [Test]
    public async Task CtrlCInterruptsAChildThatWaits()
    {
        var status = await InterruptAsync();
        if (status is null)
            return;

        // Windows reports no signal; the child's own Ctrl+C handling decides its exit code there.
        if (!OperatingSystem.IsWindows())
        {
            await Assert.That(status.Value.Signal).IsEqualTo(UnixInterop.SigInt);
            await Assert.That(status.Value.ExitCode).IsEqualTo(128 + UnixInterop.SigInt);
        }
    }

    /// <summary>
    /// Before the fix, the child of a host that ignored Ctrl+C ignored it too, and a flooding child ran on for 30 s after
    /// the keystroke, on the in-box and the out-of-band ConPTY alike.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task OnWindowsCtrlCInterruptsTheChildOfAHostThatIgnoresIt()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var hostIgnored = WindowsHostCtrlC.HostIgnoresCtrlC;
        await Assert.That(SetConsoleCtrlHandler(IntPtr.Zero, true)).IsTrue();
        try
        {
            await Assert.That(WindowsHostCtrlC.HostIgnoresCtrlC).IsTrue();
            var status = await InterruptAsync();
            await Assert.That(status).IsNotNull();

            // The start cleared the attribute only while it created the child.
            await Assert.That(WindowsHostCtrlC.HostIgnoresCtrlC).IsTrue();
        }
        finally
        {
            _ = SetConsoleCtrlHandler(IntPtr.Zero, hostIgnored);
        }
    }

    /// <summary>
    /// Starts share one window in which the host processes Ctrl+C: a start that leaves while another is still in it does
    /// not set the attribute again under it, and the last one out does, also after many starts at once.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task OnWindowsStartsShareOneWindowAndTheLastOneOutRestoresTheHost()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var hostIgnored = WindowsHostCtrlC.HostIgnoresCtrlC;
        await Assert.That(SetConsoleCtrlHandler(IntPtr.Zero, true)).IsTrue();
        try
        {
            WindowsHostCtrlC.EnterStart();
            await Assert.That(WindowsHostCtrlC.HostIgnoresCtrlC).IsFalse();
            WindowsHostCtrlC.EnterStart();
            WindowsHostCtrlC.ExitStart();
            await Assert.That(WindowsHostCtrlC.HostIgnoresCtrlC).IsFalse();
            WindowsHostCtrlC.ExitStart();
            await Assert.That(WindowsHostCtrlC.HostIgnoresCtrlC).IsTrue();

            // Starts that enter and leave on many threads at once: whoever is in the window finds the attribute cleared,
            // and once every one has left it is set again.
            await Assert.That(EnterAndLeaveOnManyThreads()).IsEqualTo(0);
            await Assert.That(WindowsHostCtrlC.HostIgnoresCtrlC).IsTrue();

            // A host that processes Ctrl+C is left as it is.
            await Assert.That(SetConsoleCtrlHandler(IntPtr.Zero, false)).IsTrue();
            WindowsHostCtrlC.EnterStart();
            WindowsHostCtrlC.ExitStart();
            await Assert.That(WindowsHostCtrlC.HostIgnoresCtrlC).IsFalse();
        }
        finally
        {
            _ = SetConsoleCtrlHandler(IntPtr.Zero, hostIgnored);
        }
    }

    /// <summary>
    /// Starts a child that writes <see cref="Ready"/> and then waits about two minutes, writes Ctrl+C once
    /// <see cref="Ready"/> has arrived, and returns the child's exit status; null where a PTY is not supported.
    /// </summary>
    /// <remarks>
    /// The Ctrl+C is written only after the child has written: before that a Unix child may not yet lead the terminal's
    /// foreground process group, and a ConPTY may not yet have its client attached. Output is read to its end meanwhile, so
    /// the child never blocks on a full terminal.
    /// </remarks>
    private static async Task<PtyExitStatus?> InterruptAsync()
    {
        if (!Pty.IsSupported)
            return null;

        await using var session = Pty.Start(WaitingChild());
        var output = new StringBuilder();
        var reader = session.ReadOutputAsync().GetAsyncEnumerator();
        try
        {
            await ReadUntilAsync(reader, output, Ready).WaitAsync(ReadyTimeout);
            var drain = DrainAsync(reader, output);
            await session.WriteInputAsync(new byte[] { 0x03 });
            PtyExitStatus status;
            try
            {
                status = await session.WaitForExitAsync().WaitAsync(InterruptTimeout);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(
                    $"The child was still running {InterruptTimeout.TotalSeconds} s after Ctrl+C. Output: {PtyBaselineTests.Escape(output.ToString())}");
            }

            await drain.WaitAsync(InterruptTimeout);
            return status;
        }
        finally
        {
            await reader.DisposeAsync();
        }
    }

    /// <summary>
    /// A child that writes <see cref="Ready"/> and waits about two minutes without reading its input: <c>sleep</c> on
    /// Unix, replacing the shell so the interrupt reaches it directly, and <c>ping</c> of the loopback address on Windows.
    /// </summary>
    private static PtyStartInfo WaitingChild() =>
        OperatingSystem.IsWindows()
            ? new PtyStartInfo { FileName = Path.Combine(Environment.SystemDirectory, "PING.EXE"), Arguments = ["-n", "120", Ready] }
            : new PtyStartInfo { FileName = "sh", Arguments = ["-c", $"echo {Ready}; exec sleep 120"] };

    private static async Task ReadUntilAsync(IAsyncEnumerator<PtyOutputChunk> reader, StringBuilder output, string text)
    {
        while (output.ToString().IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0)
        {
            if (!await reader.MoveNextAsync())
                throw new InvalidOperationException($"Output ended before '{text}': {PtyBaselineTests.Escape(output.ToString())}");

            output.Append(Encoding.UTF8.GetString(reader.Current.Data.Span));
        }
    }

    private static async Task DrainAsync(IAsyncEnumerator<PtyOutputChunk> reader, StringBuilder output)
    {
        while (await reader.MoveNextAsync())
            output.Append(Encoding.UTF8.GetString(reader.Current.Data.Span));
    }

    /// <summary>Enters and leaves the window 10,000 times on 8 threads; how often a start inside it found the attribute set.</summary>
    [SupportedOSPlatform("windows")]
    private static int EnterAndLeaveOnManyThreads()
    {
        var setInside = 0;
        Parallel.For(0, 10_000, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ =>
        {
            WindowsHostCtrlC.EnterStart();
            if (WindowsHostCtrlC.HostIgnoresCtrlC)
                Interlocked.Increment(ref setInside);

            WindowsHostCtrlC.ExitStart();
        });
        return setInside;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleCtrlHandler(IntPtr handlerRoutine, [MarshalAs(UnmanagedType.Bool)] bool add);
}
