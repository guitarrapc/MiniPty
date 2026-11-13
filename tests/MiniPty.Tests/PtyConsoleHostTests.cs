using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace MiniPty.Tests;

/// <summary>
/// The console host behind a Windows session ends with the session, and disposal neither waits for a host that does
/// not nor holds a thread-pool thread for it. Before Windows 11 24H2 the in-box ConPTY closes a pseudo-console only
/// once its host exits, and a host whose write finds MiniPty's end of the output closed can wait for good. From build
/// 26100 the host exits with its child.
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
    /// Disposal holds no thread-pool thread while console hosts are slow to exit, as the post-exit drain's ticks and
    /// callers' continuations need that pool. Before Windows 11 24H2 the in-box close waits until the host exits, and a
    /// suspended host does not exit, so a close on the pool would stay running or queued there until the test ends the
    /// hosts. From build 26100 the close does not wait, and the test shows nothing there.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task DisposalHoldsNoPoolThreadWhileConsoleHostsAreSuspended()
    {
        if (!OperatingSystem.IsWindows())
            return;

        const int Sessions = 8;
        var sessions = new List<PtySession>(Sessions);
        var hosts = new List<IntPtr>(Sessions);
        try
        {
            for (var i = 0; i < Sessions; i++)
            {
                var before = ConsoleHostChildren();
                var session = Pty.Start(WindowsCommand("echo READY& set /p LINE="));
                sessions.Add(session);
                await ReadUntilAsync(session, "READY");
                hosts.Add(OpenConsoleHostStartedSince(before, Synchronize | QueryLimitedInformation | SuspendResume | Terminate));
                await Assert.That(NtSuspendProcess(hosts[^1])).IsEqualTo(0);
            }

            var idle = await HeldPoolWorkAsync();
            foreach (var session in sessions)
                session.Dispose();

            // Each close on the pool would add one. Half leaves room for a work item or two of something else.
            await Assert.That(await HeldPoolWorkAsync() - idle).IsLessThan(Sessions / 2);
        }
        finally
        {
            // Ending the hosts lets the closes that wait for them return.
            foreach (var host in hosts)
                TerminateProcess(host, 1);

            foreach (var session in sessions)
                await session.DisposeAsync();

            foreach (var host in hosts)
                Close(host);
        }
    }

    /// <summary>
    /// Output ends soon after the console host exits while another process holds the output pipe, so the pipe does not
    /// break and the drain must end output itself. Before Windows 11 24H2 the in-box close waits until the host exits,
    /// and the drain counts the close as done when it returns, which the backend learns by watching the close thread
    /// (Post-exit drain in <c>.github/docs/references/pty_crossplatform.md</c>). Output then ends about 100 ms after the
    /// host. Without that report the close would count as done only 1 s after the request, about 1.1 s before the end.
    /// From build 26100 the host exits with its child and the close does not wait, so output ends about 200 ms after
    /// the host there.
    /// </summary>
    /// <remarks>
    /// This test holds the pipe itself. ConPTY starts its console host with the output pipe's write end as the host's
    /// standard output, so the test duplicates that handle out of the host, and a marker written through the duplicate
    /// shows that it is the session's output pipe. A process that inherited the write end while ConPTY started the host
    /// holds it the same way (<c>PtyTests.PtyWindowsOutputEndsWhileAnotherProcessHoldsTheOutputPipe</c>), but only when
    /// its start lands in that window.
    /// </remarks>
    [Test]
    [NotInParallel]
    public async Task OutputEndsSoonAfterTheConsoleHostExitsWhileTheOutputPipeIsHeld()
    {
        // The host's process parameters are read at their 64-bit offsets.
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            return;

        var before = ConsoleHostChildren();
        var host = IntPtr.Zero;
        var held = IntPtr.Zero;
        try
        {
            await using var session = Pty.Start(WindowsCommand("echo READY& set /p LINE="));
            var output = new StringBuilder();
            using var cts = new CancellationTokenSource(Timeout);
            await using var reader = session.ReadOutputAsync(cts.Token).GetAsyncEnumerator();
            await ReadUntilAsync(reader, output, "READY");

            host = OpenConsoleHostStartedSince(before, Synchronize | QueryLimitedInformation | QueryInformation | VmRead | DupHandle);
            held = DuplicateStandardOutput(host);
            await Assert.That(GetFileType(held)).IsEqualTo(FileTypePipe);
            var marker = "HELD_MARKER"u8.ToArray();
            await Assert.That(WriteFile(held, marker, (uint)marker.Length, out var written, IntPtr.Zero)).IsTrue();
            await Assert.That(written).IsEqualTo((uint)marker.Length);
            await ReadUntilAsync(reader, output, "HELD_MARKER");

            await session.WriteInputAsync("x\r");
            while (await reader.MoveNextAsync())
                output.Append(Encoding.UTF8.GetString(reader.Current.Data.Span));

            var endedAt = DateTime.UtcNow.ToFileTimeUtc();
            await Assert.That(session.OutputPump.IsEndOfOutputForced).IsTrue();
            await Assert.That(WaitForSingleObject(host, 5_000)).IsEqualTo(WaitObject0);
            await Assert.That(GetProcessTimes(host, out _, out var hostExitedAt, out _, out _)).IsTrue();

            // The bound sits between the two outcomes on a legacy host (about 0.1 s and 1.1 s less the close's time) and
            // above the 0.2 s from build 26100. A close that took 0.6 s would put the outcome without the report under it.
            var afterHostMs = (endedAt - hostExitedAt) / 10_000.0;
            await Assert.That(afterHostMs).IsLessThan(500)
                .Because($"output ended {afterHostMs:F0} ms after the console host exited " +
                    $"(ReleasePseudoConsole: {MiniPty.Internal.WindowsInterop.CanReleasePseudoConsole})");
        }
        finally
        {
            Close(held);
            Close(host);
        }
    }

    /// <summary>
    /// Duplicates <paramref name="host"/>'s standard output into this process. The handle value is read from the host's
    /// process parameters (<c>RTL_USER_PROCESS_PARAMETERS.StandardOutput</c>, through its PEB), at their offsets in a
    /// 64-bit process.
    /// </summary>
    private static IntPtr DuplicateStandardOutput(IntPtr host)
    {
        const int PebProcessParameters = 0x20;
        const int ParametersStandardOutput = 0x28;
        const int ProcessBasicInformationClass = 0;

        var status = NtQueryInformationProcess(
            host, ProcessBasicInformationClass, out var basic, Marshal.SizeOf<ProcessBasicInformation>(), out _);
        if (status != 0)
            throw new InvalidOperationException($"NtQueryInformationProcess failed with 0x{status:X8}.");

        var parameters = ReadPointer(host, basic.PebBaseAddress + PebProcessParameters);
        var standardOutput = ReadPointer(host, parameters + ParametersStandardOutput);
        if (!DuplicateHandle(host, standardOutput, GetCurrentProcess(), out var held, 0, false, DuplicateSameAccess))
            throw new Win32Exception();

        return held;
    }

    private static IntPtr ReadPointer(IntPtr process, IntPtr address) =>
        ReadProcessMemory(process, address, out var value, IntPtr.Size, out _) ? value : throw new Win32Exception();

    private static async Task ReadUntilAsync(IAsyncEnumerator<PtyOutputChunk> reader, StringBuilder output, string marker)
    {
        while (output.ToString().IndexOf(marker, StringComparison.Ordinal) < 0)
        {
            await Assert.That(await reader.MoveNextAsync()).IsTrue();
            output.Append(Encoding.UTF8.GetString(reader.Current.Data.Span));
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

    private static async Task ReadUntilAsync(PtySession session, string marker)
    {
        var output = new StringBuilder();
        using var cts = new CancellationTokenSource(Timeout);
        await using var reader = session.ReadOutputAsync(cts.Token).GetAsyncEnumerator();
        while (output.ToString().IndexOf(marker, StringComparison.Ordinal) < 0)
        {
            await Assert.That(await reader.MoveNextAsync()).IsTrue();
            output.Append(Encoding.UTF8.GetString(reader.Current.Data.Span));
        }
    }

    /// <summary>
    /// The thread-pool threads running work plus the work items waiting for one, at the fewest of ten samples 10 ms
    /// apart, so that work which only passes through is left out.
    /// </summary>
    private static async Task<long> HeldPoolWorkAsync()
    {
        ThreadPool.GetMaxThreads(out var max, out _);
        var fewest = long.MaxValue;
        for (var i = 0; i < 10; i++)
        {
            ThreadPool.GetAvailableThreads(out var available, out _);
            fewest = Math.Min(fewest, max - available + ThreadPool.PendingWorkItemCount);
            await Task.Delay(10);
        }

        return fewest;
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
    private const uint VmRead = 0x0010;
    private const uint DupHandle = 0x0040;
    private const uint QueryInformation = 0x0400;
    private const uint SuspendResume = 0x0800;
    private const uint QueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;
    private const uint WaitObject0 = 0;
    private const uint DuplicateSameAccess = 0x00000002;
    private const uint FileTypePipe = 0x0003;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

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

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle, int processInformationClass, out ProcessBasicInformation processInformation,
        int processInformationLength, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(
        IntPtr hProcess, IntPtr lpBaseAddress, out IntPtr lpBuffer, nint nSize, out nint lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(
        IntPtr hSourceProcessHandle, IntPtr hSourceHandle, IntPtr hTargetProcessHandle, out IntPtr lpTargetHandle,
        uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwOptions);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(IntPtr hFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(
        IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);
}
