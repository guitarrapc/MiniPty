#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property AllowUnsafeBlocks=true
#:property PublishAot=false

// Measures how the in-box ConPTY of the running Windows build closes, holds handles, and resizes, through kernel32.dll
// directly and without MiniPty, so that a behavior seen in MiniPty's tests can be told apart from the operating
// system's.
//
//     dotnet run scripts/conpty-close-probe.cs
//
// Each close scenario prints one line per run:
//
//     close scenario=<name> run=<n> returned=<bool> close_ms=<ms> pipe_end_ms=<ms> hosts=<n>/<n> handle_delta=<n> ...
//
// returned: whether ClosePseudoConsole returned within 5 s (it runs on a thread of its own, so a close that never
// returns does not stop the probe). close_ms: how long it took, or how long the probe waited. pipe_end_ms: when the
// output pipe broke, counted from the start of the close, or -1 when it did not within 3 s (or nothing read it).
// hosts: the conhost.exe children this scenario started that were still running right after the close, then 2 s later.
// A host still running then is stuck: the line adds the CPU time it used over the next second (host_cpu_ms_per_s),
// and the probe then terminates it, which shows whether a close that had not returned returns then
// (returned_after_host_kill). handle_delta: this process's handle count at the end against before the scenario, with
// everything the probe itself opened closed; a close that has not returned still holds what it closes.
//
// Scenarios: no-client (no process attached), exited (child exited, input pipe still open during the close),
// exited-input-closed (MiniPty's post-exit drain: input closed, output still read), exited-output-closed (MiniPty's
// disposal: input and output closed before the close), exited-undrained (output open but not read). The cursor-
// scenarios use PSEUDOCONSOLE_INHERIT_CURSOR, wait for the host's cursor query, then kill the child:
// cursor-unanswered then closes input, and cursor-unanswered-input-open leaves it open. cursor-answered-at-close and
// cursor-answered-input-open do the same after writing the report a terminal answers the query with (ESC [1;1R).
// cursor-answered-silent writes the report while the child, which writes nothing, still runs, then kills it. Each
// answered scenario adds after_report_bytes: what the host wrote in the 500 ms after the report. The named- scenarios repeat three of them over the pipes MiniPty creates: named pipes whose server end is the probe's,
// 4 KiB buffers, and the console host's end opened by name. MiniPty also opens its end overlapped, which the probe
// does not, so that it can read and write with blocking calls.
//
// A census runs one pattern 3 times to warm up, then 20 times, and prints what this process's handle table gained:
//
//     census mode=<name> runs=20 handle_delta=<n> added=<n> incomplete=<n> seconds=<s>
//     census-added mode=<name> <type>(<detail>) x<count>; ...
//
// Every census run closes on the probe's main thread, so the probe opens no thread of its own meanwhile. Modes:
// no-client and exited-drained (child exited, input closed, output read after the close) over anonymous pipes, and
// the mini- modes, which use MiniPty's named pipes and start the child as MiniPty does (standard handles set to
// INVALID_HANDLE_VALUE, the attribute list kept until the pseudo-console is closed): mini-no-client is MiniPty's
// failed start, mini-exited-drained its post-exit drain, and mini-exited-disposed its disposal.
//
// Each resize run starts Windows PowerShell on a 40x8 pseudo-console, resizes it to 100x30, then lets it print the
// size it sees, as MiniPty's resize tests do. before-spawn resizes before CreateProcess, after-spawn right after it.
// resent resizes right after it and again once the probe's 10 ms poll finds the first output; resent-now sends the
// second from the reading thread right after the first read. after-ready resizes once the child has printed READY, so
// certainly after it has connected. title_ms: when the host sent the window title, counted from CreateProcess; a host
// from Windows 11 24H2 sends nothing before a resize or the child's first output. mini- runs use MiniPty's pipes and
// start, and the parallel- runs start eight at once to load the machine.

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("The probe runs on Windows only.");
    return 2;
}

var kernel32 = NativeLibrary.Load("kernel32.dll");
Console.WriteLine(
    $"os={Environment.OSVersion.Version} release_export={NativeLibrary.TryGetExport(kernel32, "ReleasePseudoConsole", out _)} " +
    $"cpus={Environment.ProcessorCount}");
Watchdog.Start();

string[] closeScenarios =
[
    "no-client",
    "exited",
    "exited-input-closed",
    "exited-output-closed",
    "exited-undrained",
    "cursor-unanswered",
    "cursor-unanswered-input-open",
    "cursor-answered-at-close",
    "cursor-answered-input-open",
    "cursor-answered-silent",
    "named-no-client",
    "named-exited-input-closed",
    "named-exited-output-closed",
];

foreach (var scenario in closeScenarios)
{
    var runs = scenario.StartsWith("cursor-", StringComparison.Ordinal) ? 2 : 1;
    for (var run = 1; run <= runs; run++)
        Probe.Close(scenario, run);
}

foreach (var mode in new[] { "no-client", "exited-drained", "mini-no-client", "mini-exited-drained", "mini-exited-disposed" })
    Census.Run(mode);

Resizes.Run();

var left = Native.ConhostChildren();
Console.WriteLine($"end conhost_children={left.Count} late_close_returns={Probe.LateReturns}");
foreach (var pid in left)
    Native.Terminate(pid);

return 0;

static class Probe
{
    private const int CloseTimeoutMs = 5_000;
    private static int s_lateReturns;

    /// <summary>Closes that returned after the probe stopped waiting for them.</summary>
    public static int LateReturns => Volatile.Read(ref s_lateReturns);

    public static string ComSpec => Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";

    public readonly record struct CloseAttempt(Task Done, bool Returned, double Milliseconds);

    public static void Close(string scenario, int run)
    {
        var hostsBefore = Native.ConhostChildren();
        var handlesBefore = Native.HandleCount();
        var named = scenario.StartsWith("named-", StringComparison.Ordinal);
        var shape = named ? scenario["named-".Length..] : scenario;
        var cursor = shape.StartsWith("cursor-", StringComparison.Ordinal);
        var answered = shape.StartsWith("cursor-answered", StringComparison.Ordinal);
        var inputOpen = shape is "exited" or "cursor-unanswered-input-open" or "cursor-answered-input-open";
        var pty = PseudoConsole.Create(cursor ? Native.InheritCursor : 0, 80, 25, named);
        var drained = shape is not ("exited-output-closed" or "exited-undrained");
        var reader = drained ? new PipeReader(pty.OutputRead) : null;
        var note = "";

        Native.ProcessInformation? child = null;
        if (shape != "no-client")
        {
            var command = shape == "cursor-answered-silent" ? "set /p X=" : cursor ? "echo NEVER_SHOWN" : "exit 0";
            child = pty.Spawn($"\"{ComSpec}\" /c {command}");
            if (cursor)
            {
                note = reader!.WaitFor("\e[6n", 5_000) ? " query=seen" : " query=missing";
                if (shape == "cursor-answered-silent")
                    note += Answer(pty, reader);
                else
                    Thread.Sleep(500);

                Native.TerminateProcess(child.Value.hProcess, 1);
            }

            note += Native.WaitForSingleObject(child.Value.hProcess, 10_000) == 0 ? " child=exited" : " child=running";
        }

        Thread.Sleep(200);
        if (answered && shape != "cursor-answered-silent")
            note += Answer(pty, reader!);
        if (!inputOpen)
            pty.CloseInput();
        if (shape == "exited-output-closed")
            pty.CloseOutput();

        var started = Stopwatch.GetTimestamp();
        var close = CloseTimed(pty);
        var pipeEndMs = reader is not null && reader.WaitEnd(3_000)
            ? Stopwatch.GetElapsedTime(started, reader.EndedAt).TotalMilliseconds
            : -1;
        var hostsRightAfter = NewHosts(hostsBefore).Count;
        Thread.Sleep(2_000);
        var stuck = NewHosts(hostsBefore);

        // A stuck host left running would load the machine for whatever runs after the probe.
        if (stuck.Count > 0)
        {
            note += $" host_cpu_ms_per_s={Native.CpuMilliseconds(stuck, 1_000):F0}";
            foreach (var pid in stuck)
                Native.Terminate(pid);
            if (!close.Returned)
                note += $" returned_after_host_kill={close.Done.Wait(3_000)}";
            reader?.WaitEnd(2_000);
        }

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
            $"close scenario={scenario} run={run} returned={close.Returned} close_ms={close.Milliseconds:F1} " +
            $"pipe_end_ms={pipeEndMs:F1} hosts={hostsRightAfter}/{stuck.Count} handle_delta={handleDelta}{note}");
    }

    /// <summary>Writes the cursor position report, then says how many bytes the host wrote in the next 500 ms.</summary>
    private static string Answer(PseudoConsole pty, PipeReader reader)
    {
        var before = reader.ByteCount;
        pty.Write("\e[1;1R");
        Thread.Sleep(500);
        return $" after_report_bytes={reader.ByteCount - before}";
    }

    /// <summary>Runs ClosePseudoConsole on a thread of its own and waits for it up to <see cref="CloseTimeoutMs"/>.</summary>
    public static CloseAttempt CloseTimed(PseudoConsole pty)
    {
        var handle = pty.TakeHandle();
        var started = Stopwatch.GetTimestamp();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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

        return new CloseAttempt(done.Task, returned, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private static HashSet<uint> NewHosts(HashSet<uint> before)
    {
        var now = Native.ConhostChildren();
        now.ExceptWith(before);
        return now;
    }
}

/// <summary>Counts what one pattern of pseudo-console use leaves in this process's handle table.</summary>
static class Census
{
    private const int WarmUps = 3;
    private const int Runs = 20;

    public static void Run(string mode)
    {
        for (var i = 0; i < WarmUps; i++)
            Once(mode);

        Settle();
        var before = HandleTable.Snapshot();
        var countBefore = Native.HandleCount();
        var started = Stopwatch.GetTimestamp();
        var incomplete = 0;
        for (var i = 0; i < Runs; i++)
        {
            if (!Once(mode))
                incomplete++;
        }

        var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        Settle();
        var countAfter = Native.HandleCount();
        var added = HandleTable.AddedSince(before);
        Console.WriteLine(
            $"census mode={mode} runs={Runs} handle_delta={countAfter - countBefore} added={added.Count} " +
            $"incomplete={incomplete} seconds={seconds:F1}");
        Console.WriteLine($"census-added mode={mode} {HandleTable.Describe(added)}");
    }

    // Finalizers and the hosts of the last runs get time to let go, so that neither counts as growth.
    private static void Settle()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Thread.Sleep(2_000);
    }

    /// <summary>One pseudo-console from creation to close; false when the child or the output did not end in time.</summary>
    private static bool Once(string mode)
    {
        var mini = mode.StartsWith("mini-", StringComparison.Ordinal);
        var shape = mini ? mode["mini-".Length..] : mode;
        var pty = PseudoConsole.Create(0, 80, 25, named: mini);
        var complete = true;
        Native.ProcessInformation? child = null;
        if (shape != "no-client")
        {
            child = pty.Spawn($"\"{Probe.ComSpec}\" /c exit 0", mini);
            complete &= Native.WaitForSingleObject(child.Value.hProcess, 10_000) == 0;
        }

        pty.CloseInput();
        if (shape == "exited-disposed")
            pty.CloseOutput();

        Watchdog.Arm("census " + mode);
        Native.ClosePseudoConsole(pty.TakeHandle());
        Watchdog.Disarm();
        pty.ReleaseAttributes();

        if (pty.OutputRead != IntPtr.Zero)
        {
            complete &= Native.DrainUntilEnd(pty.OutputRead, 3_000);
            pty.CloseOutput();
        }

        if (child is { } process)
        {
            Native.CloseHandle(process.hProcess);
            Native.CloseHandle(process.hThread);
        }

        return complete;
    }
}

/// <summary>Whether a resize reaches the child, depending on when it is sent.</summary>
static class Resizes
{
    private const string SizeScript =
        "[Console]::In.ReadLine() | Out-Null; $s = $Host.UI.RawUI.WindowSize; Write-Output ('{0} {1}' -f $s.Width, $s.Height); exit 0";

    private static readonly string s_powerShell =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

    private readonly record struct RunResult(bool Applied, string Line);

    public static void Run()
    {
        // The first start of Windows PowerShell on a fresh machine can take tens of seconds, which no run should include.
        var warmUp = Once("warm-up");
        Console.WriteLine($"resize variant=warm-up {warmUp.Line}");

        Series("before-spawn", 2);
        Series("after-spawn", 6);
        Series("mini-after-spawn", 6);
        Series("after-ready", 2);
        Series("mini-resent", 2);
        Series("mini-resent-now", 10);
        Parallel(["mini-after-spawn", "mini-resent-now"], perVariant: 4, rounds: 2);
    }

    private static void Series(string variant, int runs)
    {
        var applied = 0;
        for (var run = 1; run <= runs; run++)
        {
            var result = Once(variant);
            Console.WriteLine($"resize variant={variant} run={run} {result.Line}");
            if (result.Applied)
                applied++;
        }

        Console.WriteLine($"resize-summary variant={variant} applied={applied}/{runs}");
    }

    private static void Parallel(string[] variants, int perVariant, int rounds)
    {
        var applied = new int[variants.Length];
        for (var round = 1; round <= rounds; round++)
        {
            var results = new RunResult[variants.Length * perVariant];
            var threads = new Thread[results.Length];
            for (var i = 0; i < threads.Length; i++)
            {
                var slot = i;
                threads[i] = new Thread(() => results[slot] = Once(variants[slot % variants.Length])) { IsBackground = true };
                threads[i].Start();
            }

            foreach (var thread in threads)
                thread.Join();

            for (var i = 0; i < results.Length; i++)
            {
                Console.WriteLine($"resize variant=parallel-{variants[i % variants.Length]} round={round} slot={i} {results[i].Line}");
                if (results[i].Applied)
                    applied[i % variants.Length]++;
            }
        }

        for (var v = 0; v < variants.Length; v++)
            Console.WriteLine($"resize-summary variant=parallel-{variants[v]} applied={applied[v]}/{perVariant * rounds}");
    }

    private static RunResult Once(string variant)
    {
        var mini = variant.StartsWith("mini-", StringComparison.Ordinal);
        var shape = mini ? variant["mini-".Length..] : variant;
        var pty = PseudoConsole.Create(0, 40, 8, named: mini);
        var reader = new PipeReader(pty.OutputRead, shape == "resent-now" ? () => pty.Resize(100, 30) : null);
        var hr = 0;
        if (shape == "before-spawn")
            hr = pty.Resize(100, 30);

        var script = shape == "after-ready" ? "Write-Output READY; " + SizeScript : SizeScript;
        var child = pty.Spawn($"\"{s_powerShell}\" -NoLogo -NoProfile -Command \"{script}\"", mini);
        var spawned = Stopwatch.GetTimestamp();
        if (shape is "after-spawn" or "resent" or "resent-now")
            hr = pty.Resize(100, 30);
        if (shape is "after-ready" or "resent")
        {
            if (shape == "after-ready")
                reader.WaitFor("READY", 10_000);
            else
                reader.WaitFor("", 10_000, anyOutput: true);

            var again = pty.Resize(100, 30);
            hr = hr != 0 ? hr : again;
        }

        pty.Write("go\r\n");
        var exited = Native.WaitForSingleObject(child.hProcess, 30_000) == 0;
        pty.CloseInput();
        var close = Probe.CloseTimed(pty);
        pty.ReleaseAttributes();
        reader.WaitEnd(3_000);

        var text = reader.Text;
        var titleAt = reader.FirstAt("\e]0;");
        var titleMs = titleAt == 0 ? -1 : Stopwatch.GetElapsedTime(spawned, titleAt).TotalMilliseconds;
        var sizes = Regex.Matches(text, @"(?<![\d;\[?])\d{1,4} \d{1,4}(?![\d;])");
        var size = sizes.Count == 0 ? "none" : sizes[^1].Value;

        Native.CloseHandle(child.hProcess);
        Native.CloseHandle(child.hThread);
        if (reader.Ended)
            pty.CloseOutput();

        return new RunResult(
            size == "100 30",
            $"result=\"{size}\" hr=0x{hr:X8} exited={exited} title_ms={titleMs:F0} close_returned={close.Returned}");
    }
}

/// <summary>
/// Terminates this process's console hosts when a step that cannot time out by itself overruns, so that a stuck host
/// cannot hang the probe.
/// </summary>
static class Watchdog
{
    private static long s_deadline;
    private static string s_step = "";

    public static void Start() => new Thread(Run) { IsBackground = true }.Start();

    public static void Arm(string step, int timeoutMs = 15_000)
    {
        s_step = step;
        Volatile.Write(ref s_deadline, Stopwatch.GetTimestamp() + timeoutMs * Stopwatch.Frequency / 1_000);
    }

    public static void Disarm() => Volatile.Write(ref s_deadline, 0);

    private static void Run()
    {
        while (true)
        {
            Thread.Sleep(250);
            var deadline = Volatile.Read(ref s_deadline);
            if (deadline == 0 || Stopwatch.GetTimestamp() < deadline)
                continue;

            var hosts = Native.ConhostChildren();
            foreach (var pid in hosts)
                Native.Terminate(pid);
            Console.WriteLine($"watchdog step=\"{s_step}\" terminated_hosts={hosts.Count}");
            Volatile.Write(ref s_deadline, 0);
        }
    }
}

/// <summary>This process's handle table, read with <c>NtQueryInformationProcess(ProcessHandleInformation)</c>.</summary>
static class HandleTable
{
    /// <summary>Each handle's value and object type index.</summary>
    public static Dictionary<long, uint> Snapshot()
    {
        const int ProcessHandleInformation = 51;
        const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

        // PROCESS_HANDLE_SNAPSHOT_INFORMATION: a count and a reserved word, then PROCESS_HANDLE_TABLE_ENTRY_INFO
        // entries of three pointer-sized fields (handle, handle count, pointer count) and four ULONGs (granted access,
        // object type index, attributes, reserved).
        var entrySize = 3 * IntPtr.Size + 16;
        var size = 256 * 1024;
        while (true)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = Native.NtQueryInformationProcess(new IntPtr(-1), ProcessHandleInformation, buffer, size, out var needed);
                if (status == StatusInfoLengthMismatch)
                {
                    size = Math.Max(size * 2, needed + 4096);
                    continue;
                }

                if (status < 0)
                    throw new InvalidOperationException($"NtQueryInformationProcess failed: 0x{status:X8}");

                var count = (long)Marshal.ReadIntPtr(buffer);
                var table = new Dictionary<long, uint>((int)count);
                for (var i = 0; i < count; i++)
                {
                    var entry = buffer + 2 * IntPtr.Size + i * entrySize;
                    table[(long)Marshal.ReadIntPtr(entry)] = (uint)Marshal.ReadInt32(entry, 3 * IntPtr.Size + 4);
                }

                return table;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    /// <summary>The handles open now that were not in <paramref name="before"/>, or held an object of another type.</summary>
    public static List<IntPtr> AddedSince(Dictionary<long, uint> before)
    {
        var added = new List<IntPtr>();
        foreach (var (value, type) in Snapshot())
        {
            if (!before.TryGetValue(value, out var old) || old != type)
                added.Add(new IntPtr(value));
        }

        return added;
    }

    public static string Describe(List<IntPtr> handles)
    {
        if (handles.Count == 0)
            return "none";

        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var handle in handles)
        {
            var key = DescribeOne(handle);
            counts.TryGetValue(key, out var count);
            counts[key] = count + 1;
        }

        return string.Join("; ", counts.Select(pair => $"{pair.Key} x{pair.Value}"));
    }

    private static string DescribeOne(IntPtr handle) => Native.ObjectType(handle) switch
    {
        null => "?",
        "Process" => $"Process({Native.ProcessDetail(handle)})",
        "Thread" => $"Thread({Native.ThreadOwner(handle)})",
        "File" => $"File({Native.FileName(handle)})",
        var type => type,
    };
}

/// <summary>A pseudo-console with MiniPty's end of each pipe; the host's ends are closed once it has them.</summary>
sealed class PseudoConsole
{
    private IntPtr _handle;
    private IntPtr _attributes;

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

    /// <summary>
    /// Starts <paramref name="commandLine"/> attached to the pseudo-console. With <paramref name="mini"/>, as MiniPty
    /// does: standard handles set to INVALID_HANDLE_VALUE, and the attribute list kept until
    /// <see cref="ReleaseAttributes"/>. Otherwise with null standard handles, and the list deleted at once.
    /// </summary>
    public Native.ProcessInformation Spawn(string commandLine, bool mini = false)
    {
        var size = IntPtr.Zero;
        Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        _attributes = Marshal.AllocHGlobal(size);
        try
        {
            if (!Native.InitializeProcThreadAttributeList(_attributes, 1, 0, ref size))
                throw new Win32Exception();
            if (!Native.UpdateProcThreadAttribute(_attributes, 0, Native.ProcThreadAttributePseudoConsole, _handle, IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception();

            var startup = new Native.StartupInfoEx();
            startup.StartupInfo.cb = Marshal.SizeOf<Native.StartupInfoEx>();
            startup.StartupInfo.dwFlags = Native.StartfUseStdHandles;
            if (mini)
            {
                startup.StartupInfo.hStdInput = new IntPtr(-1);
                startup.StartupInfo.hStdOutput = new IntPtr(-1);
                startup.StartupInfo.hStdError = new IntPtr(-1);
            }

            startup.lpAttributeList = _attributes;
            if (!Native.CreateProcessW(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false,
                    Native.ExtendedStartupInfoPresent, IntPtr.Zero, null, ref startup, out var info))
                throw new Win32Exception();

            return info;
        }
        finally
        {
            if (!mini)
                ReleaseAttributes();
        }
    }

    public void ReleaseAttributes()
    {
        if (_attributes == IntPtr.Zero)
            return;

        Native.DeleteProcThreadAttributeList(_attributes);
        Marshal.FreeHGlobal(_attributes);
        _attributes = IntPtr.Zero;
    }
}

/// <summary>Reads a pipe on a thread of its own until it fails, and records when it did.</summary>
sealed class PipeReader
{
    private readonly IntPtr _handle;
    private readonly StringBuilder _text = new();
    private readonly List<(long At, string Chunk)> _chunks = [];
    private readonly ManualResetEventSlim _ended = new();
    private readonly Action? _onFirstOutput;
    private int _bytes;

    /// <summary>Starts reading; <paramref name="onFirstOutput"/> runs on the reading thread right after the first read.</summary>
    public PipeReader(IntPtr handle, Action? onFirstOutput = null)
    {
        _handle = handle;
        _onFirstOutput = onFirstOutput;
        new Thread(Run) { IsBackground = true }.Start();
    }

    public long EndedAt { get; private set; }

    public int ByteCount => Volatile.Read(ref _bytes);

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

    /// <summary>When the read that completed <paramref name="value"/> returned, or 0 if it never arrived.</summary>
    public long FirstAt(string value)
    {
        lock (_text)
        {
            var seen = new StringBuilder();
            foreach (var (at, chunk) in _chunks)
            {
                seen.Append(chunk);
                if (seen.ToString().Contains(value, StringComparison.Ordinal))
                    return at;
            }
        }

        return 0;
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

            var chunk = Encoding.UTF8.GetString(buffer, 0, read);
            lock (_text)
            {
                _text.Append(chunk);
                _chunks.Add((Stopwatch.GetTimestamp(), chunk));
            }

            if (Interlocked.Add(ref _bytes, read) == read)
                _onFirstOutput?.Invoke();
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
    private const uint ProcessTerminate = 0x0001;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorBrokenPipe = 109;

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
    private static extern unsafe bool PeekNamedPipe(IntPtr pipe, byte* buffer, int size, out int read, out int available, IntPtr left);

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
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll")]
    private static extern uint GetProcessIdOfThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessHandleCount(IntPtr process, out uint count);

    [DllImport("ntdll.dll")]
    public static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr buffer, int length, out int returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryObject(IntPtr handle, int infoClass, IntPtr buffer, int length, out int returnLength);

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

    /// <summary>
    /// Reads and discards until the pipe breaks; false when it has not within <paramref name="timeoutMs"/>. Peeking
    /// first keeps a pipe that never breaks from blocking the caller in ReadFile.
    /// </summary>
    public static unsafe bool DrainUntilEnd(IntPtr pipe, int timeoutMs)
    {
        var buffer = stackalloc byte[4096];
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started).TotalMilliseconds < timeoutMs)
        {
            if (!PeekNamedPipe(pipe, null, 0, out _, out var available, IntPtr.Zero))
                return Marshal.GetLastPInvokeError() == ErrorBrokenPipe;

            if (available == 0)
            {
                Thread.Sleep(5);
                continue;
            }

            if (!ReadFile(pipe, buffer, Math.Min(available, 4096), out _, IntPtr.Zero))
                return Marshal.GetLastPInvokeError() == ErrorBrokenPipe;
        }

        return false;
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

    public static void Terminate(uint processId)
    {
        var process = OpenProcess(ProcessTerminate, false, processId);
        if (process == IntPtr.Zero)
            return;

        TerminateProcess(process, 1);
        CloseHandle(process);
    }

    /// <summary>The CPU time, in milliseconds, that the processes used together over the next <paramref name="windowMs"/>.</summary>
    public static double CpuMilliseconds(IEnumerable<uint> processIds, int windowMs)
    {
        var processes = new List<IntPtr>();
        foreach (var pid in processIds)
        {
            var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (process != IntPtr.Zero)
                processes.Add(process);
        }

        var before = CpuTime(processes);
        Thread.Sleep(windowMs);
        var after = CpuTime(processes);
        foreach (var process in processes)
            CloseHandle(process);

        return (after - before) / 10_000.0;

        static long CpuTime(List<IntPtr> processes)
        {
            long total = 0;
            foreach (var process in processes)
            {
                if (GetProcessTimes(process, out _, out _, out var kernel, out var user))
                    total += kernel + user;
            }

            return total;
        }
    }

    public static string? ObjectType(IntPtr handle) => QueryObjectString(handle, infoClass: 2);

    /// <summary>
    /// The object's name with long hexadecimal runs folded, so that per-pipe names group together. The query runs on a
    /// thread of its own: on a synchronous file handle it waits for any read pending on it.
    /// </summary>
    public static string FileName(IntPtr handle)
    {
        string? name = null;
        var query = new Thread(() => name = QueryObjectString(handle, infoClass: 1)) { IsBackground = true };
        query.Start();
        if (!query.Join(500))
            return "name-query-blocked";

        return string.IsNullOrEmpty(name) ? "unnamed" : Regex.Replace(name, "[0-9a-fA-F]{8,}", "*");
    }

    public static string ProcessDetail(IntPtr process)
    {
        var state = GetExitCodeProcess(process, out var code) ? code == 259 ? "running" : "exited" : "?";
        return $"{ImageName(process)},{state}";
    }

    public static string ThreadOwner(IntPtr thread)
    {
        var pid = GetProcessIdOfThread(thread);
        if (pid == (uint)Environment.ProcessId)
            return "own";

        var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == IntPtr.Zero)
            return "gone";

        try
        {
            return ImageName(process);
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>The image's file name, from its native path: the Win32 one fails once the process has exited.</summary>
    private static string ImageName(IntPtr process)
    {
        const uint ProcessNameNative = 1;
        var name = new StringBuilder(1024);
        var size = name.Capacity;
        return QueryFullProcessImageNameW(process, ProcessNameNative, name, ref size) ? Path.GetFileName(name.ToString()) : "?";
    }

    /// <summary>A UNICODE_STRING result of NtQueryObject: the type name (class 2) or the object name (class 1).</summary>
    private static string? QueryObjectString(IntPtr handle, int infoClass)
    {
        const int Size = 8192;
        var buffer = Marshal.AllocHGlobal(Size);
        try
        {
            if (NtQueryObject(handle, infoClass, buffer, Size, out _) < 0)
                return null;

            var length = (ushort)Marshal.ReadInt16(buffer);
            var text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
            return length == 0 || text == IntPtr.Zero ? "" : Marshal.PtrToStringUni(text, length / 2);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
