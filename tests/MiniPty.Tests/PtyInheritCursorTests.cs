using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using MiniPty.Internal;

namespace MiniPty.Tests;

/// <summary>
/// <see cref="PtyStartInfo.InheritCursor"/>. On Windows, ConPTY either clears the screen as its first output (off) or
/// asks the frontend for the cursor position and continues from the reported position (on). Unix ignores the option.
/// </summary>
/// <remarks>
/// The tests act as the frontend: they answer ConPTY's cursor position query (<c>ESC[6n</c>) by writing a report
/// (<c>ESC[row;columnR</c>) to the session input, as xterm.js does.
/// </remarks>
public sealed class PtyInheritCursorTests
{
    private const string CursorPositionQuery = "\e[6n";
    private const string ClearScreen = "\e[2J";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Test]
    public async Task DefaultStartClearsTheScreenWithoutQueryingTheCursor()
    {
        if (!OperatingSystem.IsWindows())
            return;

        await using var session = Pty.Start(WindowsCommand("echo DEFAULT_MARKER"));
        var result = await session.CompleteAsync().WaitAsync(Timeout);

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Contains("DEFAULT_MARKER")).IsTrue();
        await Assert.That(result.Contains(CursorPositionQuery)).IsFalse();
        // The clear is what InheritCursor avoids; asserting it here keeps the no-clear assertion below meaningful.
        await Assert.That(result.Contains(ClearScreen)).IsTrue();
    }

    [Test]
    public async Task InheritCursorQueriesFirstThenContinuesWithoutClearing()
    {
        if (!OperatingSystem.IsWindows())
            return;

        await using var session = Pty.Start(WindowsCommand("echo INHERIT_MARKER") with { InheritCursor = true });
        var output = await ReadAnsweringCursorQueryAsync(session, input: null);
        var status = await session.WaitForExitAsync().WaitAsync(Timeout);

        await Assert.That(status.ExitCode).IsEqualTo(0);
        await Assert.That(output.StartsWith(CursorPositionQuery, StringComparison.Ordinal)).IsTrue();
        await Assert.That(output).Contains("INHERIT_MARKER");
        await Assert.That(output).DoesNotContain(ClearScreen);
    }

    [Test]
    public async Task InheritCursorReportDoesNotReachTheChild()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // Delayed expansion, because %LINE% would be expanded before set /p runs; CR submits a ConPTY line.
        await using var session = Pty.Start(
            Spawn(WindowsComSpec(), ["/v:on", "/c", "set /p LINE= & echo GOT:!LINE!"]) with { InheritCursor = true });
        var output = await ReadAnsweringCursorQueryAsync(session, input: "abc\r");
        var status = await session.WaitForExitAsync().WaitAsync(Timeout);

        await Assert.That(status.ExitCode).IsEqualTo(0);
        await Assert.That(output).Contains("GOT:abc");
    }

    [Test]
    public async Task UnansweredInheritCursorStallsTheChildButStillTearsDown()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var session = Pty.Start(WindowsCommand("echo NEVER_SHOWN") with { InheritCursor = true });
        try
        {
            using var cts = new CancellationTokenSource(Timeout);
            await using var reader = session.ReadOutputAsync(cts.Token).GetAsyncEnumerator();
            var output = new StringBuilder();
            while (output.ToString().IndexOf(CursorPositionQuery, StringComparison.Ordinal) < 0)
            {
                await Assert.That(await reader.MoveNextAsync()).IsTrue();
                output.Append(Encoding.UTF8.GetString(reader.Current.Data.Span));
            }

            // Without the stall, cmd /c echo exits within tens of milliseconds.
            await Task.Delay(TimeSpan.FromSeconds(1));
            await Assert.That(session.HasExited).IsFalse();

            session.Kill();
            await session.WaitForExitAsync().WaitAsync(Timeout);
            while (await reader.MoveNextAsync().AsTask().WaitAsync(Timeout))
                output.Append(Encoding.UTF8.GetString(reader.Current.Data.Span));

            await Assert.That(output.ToString()).DoesNotContain("NEVER_SHOWN");

            // Before Windows 11 24H2, output ends only after the post-exit drain, which every session shares, has closed
            // the pseudo-console, and the close waits until the console host exits. This host was still waiting for its
            // answer when the child was killed, and another session's output must end all the same.
            await using var other = Pty.Start(WindowsCommand("echo OTHER_MARKER"));
            var result = await other.CompleteAsync().WaitAsync(Timeout);
            await Assert.That(result.Contains("OTHER_MARKER")).IsTrue();
        }
        finally
        {
            await session.DisposeAsync().AsTask().WaitAsync(Timeout);
        }
    }

    /// <summary>
    /// A console host still waiting for its cursor report when its session ends exits all the same. Before Windows 11
    /// 24H2 such a host otherwise waits for the report for good: with its input closed it spins on a CPU core, and it
    /// outlives the process that started it.
    /// </summary>
    [Test]
    [NotInParallel] // The session's console host is the one child conhost.exe that appears, while no other test starts one.
    public async Task UnansweredInheritCursorHostExitsOnceTheSessionEnds()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var trace = new ConcurrentQueue<string>();
        var clock = Stopwatch.StartNew();
        WindowsPtyBackend.TeardownTraceForTests = step => trace.Enqueue($"{clock.Elapsed.TotalMilliseconds:F1} ms {step}");
        try
        {
            var before = ConsoleHostChildren();
            var output = new StringBuilder();
            IntPtr host;
            await using (var session = Pty.Start(WindowsCommand("echo NEVER_SHOWN") with { InheritCursor = true }))
            {
                using var cts = new CancellationTokenSource(Timeout);
                await using var reader = session.ReadOutputAsync(cts.Token).GetAsyncEnumerator();
                while (output.ToString().IndexOf(CursorPositionQuery, StringComparison.Ordinal) < 0)
                {
                    await Assert.That(await reader.MoveNextAsync()).IsTrue();
                    output.Append(Encoding.UTF8.GetString(reader.Current.Data.Span));
                }

                var started = ConsoleHostChildren();
                started.ExceptWith(before);
                await Assert.That(started.Count).IsEqualTo(1);

                // Opened while the host runs, so that the wait below cannot reach a process that reused its id.
                host = OpenProcess(Synchronize | QueryLimitedInformation, false, started.Single());
                if (host == IntPtr.Zero)
                    throw new Win32Exception();

                trace.Enqueue($"{clock.Elapsed.TotalMilliseconds:F1} ms dispose");
            }

            try
            {
                var deadline = Environment.TickCount64 + 10_000;
                while (WaitForSingleObject(host, 0) != WaitObject0 && Environment.TickCount64 < deadline)
                    await Task.Delay(50);

                // A host still waiting for its report spins on a core once its input is closed, so a busy host never
                // got one, and an idle one is held by something else.
                var cpu = WaitForSingleObject(host, 0) == WaitObject0 ? 0 : await CpuMillisecondsOverASecondAsync(host);
                var shown = PtyBaselineTests.Escape(output.ToString());
                await Assert.That(WaitForSingleObject(host, 0)).IsEqualTo(WaitObject0)
                    .Because($"the host used {cpu} ms of CPU over 1 s; the output before teardown was {shown}; " +
                        $"teardown: {string.Join(", ", trace)}");
            }
            finally
            {
                CloseHandle(host);
            }
        }
        finally
        {
            WindowsPtyBackend.TeardownTraceForTests = null;
        }
    }

    [Test]
    public async Task InheritCursorIsIgnoredOnUnix()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var session = Pty.Start(Spawn("sh", ["-c", "printf 'UNIX_MARKER\\n'"]) with { InheritCursor = true });
        var result = await session.CompleteAsync().WaitAsync(Timeout);

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Contains("UNIX_MARKER")).IsTrue();
        await Assert.That(result.Contains(CursorPositionQuery)).IsFalse();
    }

    /// <summary>
    /// Reads output to its end, answering the first cursor position query with a report for row 5, column 1, then
    /// writing <paramref name="input"/> when given.
    /// </summary>
    private static async Task<string> ReadAnsweringCursorQueryAsync(PtySession session, string? input)
    {
        using var cts = new CancellationTokenSource(Timeout);
        var output = new StringBuilder();
        var answered = false;
        await foreach (var chunk in session.ReadOutputAsync(cts.Token))
        {
            output.Append(Encoding.UTF8.GetString(chunk.Data.Span));
            if (answered || output.ToString().IndexOf(CursorPositionQuery, StringComparison.Ordinal) < 0)
                continue;

            answered = true;
            await session.WriteInputAsync("\e[5;1R"u8.ToArray(), cts.Token);
            if (input is not null)
                await session.WriteInputAsync(input, cancellationToken: cts.Token);
        }

        return output.ToString();
    }

    private static PtyStartInfo Spawn(string fileName, IReadOnlyList<string> arguments) =>
        new() { FileName = fileName, Arguments = arguments, Size = new(80, 24) };

    private static PtyStartInfo WindowsCommand(string command) => Spawn(WindowsComSpec(), ["/c", command]);

    /// <summary>
    /// The conhost.exe processes whose parent is this process: the in-box ConPTY starts a session's console host from
    /// the process that creates the pseudo-console.
    /// </summary>
    private static HashSet<uint> ConsoleHostChildren()
    {
        const uint SnapProcess = 0x00000002;

        var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == new IntPtr(-1))
            throw new Win32Exception();

        try
        {
            var self = (uint)Environment.ProcessId;
            var children = new HashSet<uint>();
            var entry = new ProcessEntry { dwSize = (uint)Marshal.SizeOf<ProcessEntry>() };
            for (var more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
            {
                if (entry.th32ParentProcessID == self && string.Equals(entry.szExeFile, "conhost.exe", StringComparison.OrdinalIgnoreCase))
                    children.Add(entry.th32ProcessID);
            }

            return children;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    /// <summary>The CPU time, in milliseconds, that <paramref name="process"/> uses over the next second, or -1.</summary>
    private static async Task<long> CpuMillisecondsOverASecondAsync(IntPtr process)
    {
        if (!GetProcessTimes(process, out _, out _, out var kernelBefore, out var userBefore))
            return -1;

        await Task.Delay(1_000);
        if (!GetProcessTimes(process, out _, out _, out var kernelAfter, out var userAfter))
            return -1;

        return (kernelAfter + userAfter - kernelBefore - userBefore) / 10_000;
    }

    private const uint Synchronize = 0x00100000;
    private const uint QueryLimitedInformation = 0x1000;
    private const uint WaitObject0 = 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(IntPtr hProcess, out long lpCreationTime, out long lpExitTime, out long lpKernelTime, out long lpUserTime);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref ProcessEntry lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref ProcessEntry lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    private static string WindowsComSpec() =>
        Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
}
