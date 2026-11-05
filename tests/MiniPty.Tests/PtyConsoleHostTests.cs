using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace MiniPty.Tests;

/// <summary>
/// The console host behind a Windows session ends with the session, and disposal does not wait for a host that does
/// not. Before Windows 11 24H2 the in-box ConPTY closes a pseudo-console only once its host exits, and a host whose
/// write finds MiniPty's end of the output closed can wait for good. From build 26100 the host exits with its child.
/// </summary>
/// <remarks>
/// A session's in-box console host is a conhost.exe child of the process that creates the pseudo-console. The tests
/// that look for one run alone, so the one that appears is the session's.
/// </remarks>
public sealed class PtyConsoleHostTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A session disposed while its child writes leaves no console host behind. The test stops reading until the output
    /// ring is full, so the host still has output to write when disposal ends the child and closes the pseudo-console.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task ConsoleHostExitsWhenASessionIsDisposedWhileItsChildWrites()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var before = ConsoleHostChildren();
        var output = new StringBuilder();
        var host = IntPtr.Zero;
        try
        {
            await using (var session = Pty.Start(WindowsCommand("for /L %i in (1,1,100000000) do @echo WRITING %i")))
            {
                using var cts = new CancellationTokenSource(Timeout);
                await using var reader = session.ReadOutputAsync(cts.Token).GetAsyncEnumerator();
                while (output.ToString().IndexOf("WRITING", StringComparison.Ordinal) < 0)
                {
                    await Assert.That(await reader.MoveNextAsync()).IsTrue();
                    output.Append(Encoding.UTF8.GetString(reader.Current.Data.Span));
                }

                await Assert.That(await PtyBaselineTests.WaitUntilAsync(() => session.OutputPump.IsWaitingForFreeSpace, Timeout)).IsTrue();
                host = OpenConsoleHostStartedSince(before, Synchronize | QueryLimitedInformation);
            }

            await AssertExitsAsync(host, output.ToString());
        }
        finally
        {
            Close(host);
        }
    }

    /// <summary>
    /// <see cref="PtySession.DisposeAsync"/> completes while the console host keeps the output pipe open. Before
    /// Windows 11 24H2 disposal leaves that pipe open until the host has ended, so only its bound ends the wait for a
    /// host that never does. A suspended host neither writes nor ends.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task DisposeAsyncCompletesWhileTheConsoleHostIsSuspended()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var before = ConsoleHostChildren();
        var host = IntPtr.Zero;
        var session = Pty.Start(WindowsCommand("echo READY& set /p LINE="));
        try
        {
            var output = new StringBuilder();
            using (var cts = new CancellationTokenSource(Timeout))
            await using (var reader = session.ReadOutputAsync(cts.Token).GetAsyncEnumerator())
            {
                while (output.ToString().IndexOf("READY", StringComparison.Ordinal) < 0)
                {
                    await Assert.That(await reader.MoveNextAsync()).IsTrue();
                    output.Append(Encoding.UTF8.GetString(reader.Current.Data.Span));
                }
            }

            host = OpenConsoleHostStartedSince(before, Synchronize | QueryLimitedInformation | SuspendResume | Terminate);
            await Assert.That(NtSuspendProcess(host)).IsEqualTo(0);

            // The bound is 1 s. The rest is room for a loaded runner.
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            // The host's end keeps the pipe open until it is gone, which also lets the close that waits for it return.
            if (host != IntPtr.Zero)
                TerminateProcess(host, 1);

            await session.DisposeAsync();
            Close(host);
        }
    }

    /// <summary>
    /// The conhost.exe processes whose parent is this process: the in-box ConPTY starts a session's console host from
    /// the process that creates the pseudo-console.
    /// </summary>
    internal static HashSet<uint> ConsoleHostChildren()
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

    /// <summary>
    /// Opens the one console host that is a child of this process and was not in <paramref name="before"/>. Opened
    /// while the host runs, so that a later wait cannot reach a process that reused its id.
    /// </summary>
    internal static IntPtr OpenConsoleHostStartedSince(HashSet<uint> before, uint access = Synchronize | QueryLimitedInformation)
    {
        var started = ConsoleHostChildren();
        started.ExceptWith(before);
        if (started.Count != 1)
            throw new InvalidOperationException($"Expected one new console host, found {started.Count}.");

        var host = OpenProcess(access, false, started.Single());
        return host != IntPtr.Zero ? host : throw new Win32Exception();
    }

    /// <summary>
    /// Waits up to 10 s for <paramref name="host"/> to exit, and otherwise fails with the CPU time it used over a second
    /// and the session's output. A host still waiting for a cursor report spins on a core once its input is closed, so
    /// a busy host never got one, and an idle one is held by something else.
    /// </summary>
    internal static async Task AssertExitsAsync(IntPtr host, string output)
    {
        var deadline = Environment.TickCount64 + 10_000;
        while (WaitForSingleObject(host, 0) != WaitObject0 && Environment.TickCount64 < deadline)
            await Task.Delay(50);

        var cpu = WaitForSingleObject(host, 0) == WaitObject0 ? 0 : await CpuMillisecondsOverASecondAsync(host);
        await Assert.That(WaitForSingleObject(host, 0)).IsEqualTo(WaitObject0)
            .Because($"the host used {cpu} ms of CPU over 1 s; the session's output was {PtyBaselineTests.Escape(output)}");
    }

    internal static void Close(IntPtr host)
    {
        if (host != IntPtr.Zero)
            CloseHandle(host);
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

    private static PtyStartInfo WindowsCommand(string command) => new()
    {
        FileName = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe",
        Arguments = ["/c", command],
        Size = new(80, 24),
    };

    private const uint Terminate = 0x0001;
    private const uint SuspendResume = 0x0800;
    private const uint QueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;
    private const uint WaitObject0 = 0;

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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(IntPtr hProcess, out long lpCreationTime, out long lpExitTime, out long lpKernelTime, out long lpUserTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr processHandle);
}
