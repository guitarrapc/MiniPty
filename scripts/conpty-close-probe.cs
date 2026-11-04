#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property AllowUnsafeBlocks=true
#:property PublishAot=false

// Measures how the in-box ConPTY of the running Windows build closes and resizes, through kernel32.dll directly and
// without MiniPty, so that a behavior seen in MiniPty's tests can be told apart from the operating system's.
//
//     dotnet run scripts/conpty-close-probe.cs
//
// Each close scenario prints one line per run:
//
//     close scenario=<name> run=<n> returned=<bool> close_ms=<ms> pipe_end_ms=<ms> hosts=<n>/<n> handle_delta=<n>
//
// returned: whether ClosePseudoConsole returned within 10 s (it runs on a thread of its own, so a close that never
// returns does not stop the probe). close_ms: how long it took, or how long the probe waited. pipe_end_ms: when the
// output pipe broke, counted from the start of the close, or -1 when it did not within 3 s (or nothing read it).
// hosts: the conhost.exe children this scenario started that were still running right after the close, then 2 s later.
// handle_delta: this process's handle count 2 s after the close, against before the scenario, with everything the
// probe itself opened closed; a close that has not returned still holds what it closes.
//
// Scenarios: no-client (no process attached), exited (child exited, input pipe still open during the close),
// exited-input-closed (MiniPty's post-exit drain: input closed, output still read), exited-output-closed (MiniPty's
// disposal: input and output closed before the close), exited-undrained (output open but not read),
// cursor-unanswered (PSEUDOCONSOLE_INHERIT_CURSOR, the cursor query left unanswered, the child killed, input closed),
// cursor-unanswered-input-open (the same with the input pipe open during the close). The named- scenarios repeat
// three of them over the pipes MiniPty creates: named pipes whose server end is the probe's, 4 KiB buffers, and the
// console host's end opened by name. MiniPty also opens its end overlapped, which the probe does not, so that it can
// read and write with blocking calls.
//
// Each resize scenario starts Windows PowerShell on a 40x8 pseudo-console, resizes it to 100x30, then lets it print
// the size it sees: resize-before-attach (resize right after CreateProcess), resize-after-output (resize once the
// first output arrived), resize-then-wait (resize right after CreateProcess, then wait 1 s before the input).

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("The probe runs on Windows only.");
    return 2;
}

var kernel32 = NativeLibrary.Load("kernel32.dll");
Console.WriteLine($"os={Environment.OSVersion.Version} release_export={NativeLibrary.TryGetExport(kernel32, "ReleasePseudoConsole", out _)}");

string[] closeScenarios =
[
    "no-client",
    "exited",
    "exited-input-closed",
    "exited-output-closed",
    "exited-undrained",
    "cursor-unanswered",
    "cursor-unanswered-input-open",
    "named-no-client",
    "named-exited-input-closed",
    "named-exited-output-closed",
];

foreach (var scenario in closeScenarios)
{
    for (var run = 1; run <= 2; run++)
        Probe.Close(scenario, run);
}

foreach (var scenario in new[] { "resize-before-attach", "resize-after-output", "resize-then-wait" })
    Probe.Resize(scenario);

Console.WriteLine($"end conhost_children={Native.ConhostChildren().Count} late_close_returns={Probe.LateReturns}");
return 0;

static class Probe
{
    private const int CloseTimeoutMs = 10_000;
    private static int s_lateReturns;

    /// <summary>Closes that returned after the probe stopped waiting for them.</summary>
    public static int LateReturns => Volatile.Read(ref s_lateReturns);

    public static void Close(string scenario, int run)
    {
        var hostsBefore = Native.ConhostChildren();
        var handlesBefore = Native.HandleCount();
        var named = scenario.StartsWith("named-", StringComparison.Ordinal);
        var shape = named ? scenario["named-".Length..] : scenario;
        var cursor = shape.StartsWith("cursor", StringComparison.Ordinal);
        var pty = PseudoConsole.Create(cursor ? Native.InheritCursor : 0, 80, 25, named);
        var drained = shape is not ("exited-output-closed" or "exited-undrained");
        var reader = drained ? new PipeReader(pty.OutputRead) : null;
        var note = "";

        Native.ProcessInformation? child = null;
        if (shape != "no-client")
        {
            child = pty.Spawn(cursor ? $"\"{ComSpec}\" /c echo NEVER_SHOWN" : $"\"{ComSpec}\" /c exit 0");
            if (cursor)
            {
                note = reader!.WaitFor("\e[6n", 5_000) ? " query=seen" : " query=missing";
                Thread.Sleep(500);
                Native.TerminateProcess(child.Value.hProcess, 1);
            }

            note += Native.WaitForSingleObject(child.Value.hProcess, 10_000) == 0 ? " child=exited" : " child=running";
        }

        Thread.Sleep(200);
        if (shape is not ("exited" or "cursor-unanswered-input-open"))
            pty.CloseInput();
        if (shape == "exited-output-closed")
            pty.CloseOutput();

        var started = Stopwatch.GetTimestamp();
        var (returned, closeMs) = CloseTimed(pty);
        var pipeEndMs = reader is not null && reader.WaitEnd(3_000)
            ? Stopwatch.GetElapsedTime(started, reader.EndedAt).TotalMilliseconds
            : -1;
        var hostsRightAfter = NewHosts(hostsBefore);
        Thread.Sleep(2_000);
        var hostsLater = NewHosts(hostsBefore);

        pty.CloseInput();
        if (child is { } process)
        {
            Native.CloseHandle(process.hProcess);
            Native.CloseHandle(process.hThread);
        }

        // A reader still blocked in ReadFile keeps its handle: closing it under the read is not what is measured.
        if (reader is null || reader.Ended)
            pty.CloseOutput();
        else
            note += " reader=blocked";

        var handleDelta = Native.HandleCount() - handlesBefore;
        Console.WriteLine(
            $"close scenario={scenario} run={run} returned={returned} close_ms={closeMs:F1} pipe_end_ms={pipeEndMs:F1} " +
            $"hosts={hostsRightAfter}/{hostsLater} handle_delta={handleDelta}{note}");
    }

    public static void Resize(string scenario)
    {
        var pty = PseudoConsole.Create(0, 40, 8);
        var reader = new PipeReader(pty.OutputRead);
        var child = pty.Spawn(
            "powershell.exe -NoLogo -NoProfile -Command \"[Console]::In.ReadLine() | Out-Null; " +
            "$w = $Host.UI.RawUI.WindowSize; $b = $Host.UI.RawUI.BufferSize; " +
            "Write-Output ('SIZE W={0}x{1} B={2}x{3}' -f $w.Width, $w.Height, $b.Width, $b.Height)\"");
        if (scenario == "resize-after-output")
            reader.WaitFor("", 10_000, anyOutput: true);

        var hr = pty.Resize(100, 30);
        if (scenario == "resize-then-wait")
            Thread.Sleep(1_000);

        pty.Write("go\r\n");
        var exited = Native.WaitForSingleObject(child.hProcess, 20_000) == 0;
        Thread.Sleep(200);
        pty.CloseInput();
        var (returned, closeMs) = CloseTimed(pty);
        reader.WaitEnd(3_000);

        var text = reader.Text;
        var at = text.IndexOf("SIZE ", StringComparison.Ordinal);
        var size = at < 0 ? "none" : text[at..].Split('\r', '\n')[0];
        Console.WriteLine(
            $"resize scenario={scenario} hr=0x{hr:X8} exited={exited} result=\"{size}\" close_returned={returned} " +
            $"close_ms={closeMs:F1} output=\"{Escape(text)}\"");

        Native.CloseHandle(child.hProcess);
        Native.CloseHandle(child.hThread);
        if (reader.Ended)
            pty.CloseOutput();
    }

    private static string ComSpec => Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";

    /// <summary>Runs ClosePseudoConsole on a thread of its own and waits for it up to <see cref="CloseTimeoutMs"/>.</summary>
    private static (bool Returned, double Ms) CloseTimed(PseudoConsole pty)
    {
        var handle = pty.TakeHandle();
        var started = Stopwatch.GetTimestamp();
        var done = new TaskCompletionSource();
        var gaveUp = 0;
        var closer = new Thread(() =>
        {
            Native.ClosePseudoConsole(handle);
            done.SetResult();
            if (Volatile.Read(ref gaveUp) != 0)
                Interlocked.Increment(ref s_lateReturns);
        })
        { IsBackground = true };
        closer.Start();

        var returned = done.Task.Wait(CloseTimeoutMs);
        if (!returned)
            Volatile.Write(ref gaveUp, 1);

        return (returned, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private static int NewHosts(HashSet<uint> before)
    {
        var now = Native.ConhostChildren();
        now.ExceptWith(before);
        return now.Count;
    }

    private static string Escape(string text)
    {
        var escaped = text.Replace("\e", "\\e", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
        return escaped.Length <= 300 ? escaped : escaped[..300] + "...";
    }
}

/// <summary>A pseudo-console with MiniPty's end of each pipe; the host's ends are closed once it has them.</summary>
sealed class PseudoConsole
{
    private IntPtr _handle;

    public IntPtr InputWrite { get; private set; }
    public IntPtr OutputRead { get; private set; }

    public static PseudoConsole Create(uint flags, short columns, short rows, bool named = false)
    {
        IntPtr inputRead, inputWrite, outputRead, outputWrite;
        if (named)
        {
            (inputWrite, inputRead) = Native.CreateNamedPipePair(inbound: false);
            (outputRead, outputWrite) = Native.CreateNamedPipePair(inbound: true);
        }
        else if (!Native.CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0) ||
            !Native.CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0))
        {
            throw new Win32Exception();
        }

        var hr = Native.CreatePseudoConsole(new Native.Coord { X = columns, Y = rows }, inputRead, outputWrite, flags, out var handle);
        if (hr != 0)
            throw new Win32Exception(hr);

        Native.CloseHandle(inputRead);
        Native.CloseHandle(outputWrite);
        return new PseudoConsole { _handle = handle, InputWrite = inputWrite, OutputRead = outputRead };
    }

    public IntPtr TakeHandle()
    {
        var handle = _handle;
        _handle = IntPtr.Zero;
        return handle;
    }

    public int Resize(short columns, short rows) => Native.ResizePseudoConsole(_handle, new Native.Coord { X = columns, Y = rows });

    public unsafe void Write(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        fixed (byte* p = bytes)
            Native.WriteFile(InputWrite, p, bytes.Length, out _, IntPtr.Zero);
    }

    public void CloseInput()
    {
        if (InputWrite == IntPtr.Zero)
            return;

        Native.CloseHandle(InputWrite);
        InputWrite = IntPtr.Zero;
    }

    public void CloseOutput()
    {
        if (OutputRead == IntPtr.Zero)
            return;

        Native.CloseHandle(OutputRead);
        OutputRead = IntPtr.Zero;
    }

    /// <summary>Starts <paramref name="commandLine"/> attached to the pseudo-console, the way MiniPty does.</summary>
    public Native.ProcessInformation Spawn(string commandLine)
    {
        var size = IntPtr.Zero;
        Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        var list = Marshal.AllocHGlobal(size);
        try
        {
            if (!Native.InitializeProcThreadAttributeList(list, 1, 0, ref size))
                throw new Win32Exception();
            if (!Native.UpdateProcThreadAttribute(list, 0, Native.ProcThreadAttributePseudoConsole, _handle, IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception();

            var startup = new Native.StartupInfoEx();
            startup.StartupInfo.cb = Marshal.SizeOf<Native.StartupInfoEx>();
            startup.StartupInfo.dwFlags = Native.StartfUseStdHandles;
            startup.lpAttributeList = list;
            if (!Native.CreateProcessW(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false,
                    Native.ExtendedStartupInfoPresent, IntPtr.Zero, null, ref startup, out var info))
                throw new Win32Exception();

            return info;
        }
        finally
        {
            Native.DeleteProcThreadAttributeList(list);
            Marshal.FreeHGlobal(list);
        }
    }
}

/// <summary>Reads a pipe on a thread of its own until it fails, and records when it did.</summary>
sealed class PipeReader
{
    private readonly IntPtr _handle;
    private readonly StringBuilder _text = new();
    private readonly ManualResetEventSlim _ended = new();
    private int _bytes;

    public PipeReader(IntPtr handle)
    {
        _handle = handle;
        new Thread(Run) { IsBackground = true }.Start();
    }

    public long EndedAt { get; private set; }

    public bool Ended => _ended.IsSet;

    public string Text
    {
        get
        {
            lock (_text)
                return _text.ToString();
        }
    }

    public bool WaitEnd(int timeoutMs) => _ended.Wait(timeoutMs);

    public bool WaitFor(string value, int timeoutMs, bool anyOutput = false)
    {
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started).TotalMilliseconds < timeoutMs)
        {
            if (anyOutput ? Volatile.Read(ref _bytes) > 0 : Text.Contains(value, StringComparison.Ordinal))
                return true;
            Thread.Sleep(10);
        }

        return false;
    }

    private unsafe void Run()
    {
        var buffer = new byte[4096];
        while (true)
        {
            int read;
            bool ok;
            fixed (byte* p = buffer)
                ok = Native.ReadFile(_handle, p, buffer.Length, out read, IntPtr.Zero);
            if (!ok || read == 0)
                break;

            lock (_text)
                _text.Append(Encoding.UTF8.GetString(buffer, 0, read));
            Interlocked.Add(ref _bytes, read);
        }

        EndedAt = Stopwatch.GetTimestamp();
        _ended.Set();
    }
}

static class Native
{
    public const uint InheritCursor = 1;
    public const uint StartfUseStdHandles = 0x00000100;
    public const uint ExtendedStartupInfoPresent = 0x00080000;
    public static readonly IntPtr ProcThreadAttributePseudoConsole = 0x00020016;

    [StructLayout(LayoutKind.Sequential)]
    public struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct StartupInfo
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
        public uint dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
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

    [DllImport("kernel32.dll")]
    public static extern int CreatePseudoConsole(Coord size, IntPtr input, IntPtr output, uint flags, out IntPtr handle);

    [DllImport("kernel32.dll")]
    public static extern int ResizePseudoConsole(IntPtr handle, Coord size);

    [DllImport("kernel32.dll")]
    public static extern void ClosePseudoConsole(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CreatePipe(out IntPtr read, out IntPtr write, IntPtr attributes, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern unsafe bool ReadFile(IntPtr file, byte* buffer, int count, out int read, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern unsafe bool WriteFile(IntPtr file, byte* buffer, int count, out int written, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    public static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool CreateProcessW(string? application, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string? directory,
        ref StartupInfoEx startupInfo, out ProcessInformation information);

    [DllImport("kernel32.dll")]
    public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessHandleCount(IntPtr process, out uint count);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateNamedPipeW(string name, uint openMode, uint pipeMode, uint maxInstances,
        uint outBufferSize, uint inBufferSize, uint defaultTimeout, IntPtr attributes);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr attributes, uint disposition,
        uint flags, IntPtr template);

    /// <summary>
    /// A named pipe as MiniPty creates one, except that the server end is not overlapped: the server end is the probe's
    /// (input it writes when <paramref name="inbound"/> is false, output it reads otherwise), the client end the host's.
    /// </summary>
    public static (IntPtr Server, IntPtr Client) CreateNamedPipePair(bool inbound)
    {
        const uint PipeAccessInbound = 0x1, PipeAccessOutbound = 0x2, FirstPipeInstance = 0x00080000;
        const uint RejectRemoteClients = 0x8, BufferSize = 4096;
        const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, OpenExisting = 3;
        const uint SqosPresent = 0x00100000, Identification = 0x00010000;

        var name = $@"\\.\pipe\conpty-close-probe-{Guid.NewGuid():N}";
        var server = CreateNamedPipeW(name, (inbound ? PipeAccessInbound : PipeAccessOutbound) | FirstPipeInstance,
            RejectRemoteClients, 1, inbound ? 0 : BufferSize, inbound ? BufferSize : 0, 0, IntPtr.Zero);
        if (server == new IntPtr(-1))
            throw new Win32Exception();

        var client = CreateFileW(name, inbound ? GenericWrite : GenericRead, 0, IntPtr.Zero, OpenExisting,
            SqosPresent | Identification, IntPtr.Zero);
        if (client == new IntPtr(-1))
            throw new Win32Exception();

        return (server, client);
    }

    public static int HandleCount() => GetProcessHandleCount(new IntPtr(-1), out var count) ? (int)count : -1;

    /// <summary>The conhost.exe processes whose parent is this process.</summary>
    public static HashSet<uint> ConhostChildren()
    {
        var snapshot = CreateToolhelp32Snapshot(0x00000002, 0);
        var children = new HashSet<uint>();
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
            return children;

        try
        {
            var self = (uint)Environment.ProcessId;
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
}
