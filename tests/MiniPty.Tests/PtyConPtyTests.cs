using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using MiniPty.Internal;
using MiniPty.Terminal;
using static MiniPty.Tests.PtyBaselineTests;

namespace MiniPty.Tests;

/// <summary>
/// <see cref="PtyStartInfo.ConPty"/> and <see cref="ConPtyLibrary"/>: sessions on the out-of-band ConPTY an application
/// supplies, <c>conpty.dll</c> plus <c>OpenConsole.exe</c>. The test project references the
/// <c>Microsoft.Windows.Console.ConPTY</c> package, and <c>buildTransitive\MiniPty.ConPty.targets</c> stages the host
/// beside the DLL in this test's output, as it does for a package consumer. Windows only; elsewhere the type refuses to
/// load and <see cref="PtySession.Clear"/> is a no-op.
/// </summary>
public sealed class PtyConPtyTests
{
    // Set on this test executable to make it the VT probe child of ForwardsTheSequencesTheInBoxHostConsumes.
    private const string VtProbeChildVariable = "MINIPTY_TESTS_VT_PROBE_CHILD";
    private const string DeviceAttributesQuery = "\e[c";
    private const string CursorHome = "\e[1;1H";
    private const string OutOfBandHost = "OpenConsole.exe";
    private const string InBoxHost = "conhost.exe";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Modes and queries that the in-box console host of the measured Windows builds consumes instead of forwarding to
    /// the frontend, and the start of each as it reaches the frontend through the out-of-band host.
    /// </summary>
    private static readonly (string Name, string Sequence, string Needle)[] VtProbes =
    [
        ("mouse tracking 1000", "\e[?1000h", "\e[?1000h"),
        ("any-event mouse tracking 1003", "\e[?1003h", "\e[?1003h"),
        ("SGR mouse encoding 1006", "\e[?1006h", "\e[?1006h"),
        ("application cursor keys (DECCKM)", "\e[?1h", "\e[?1h"),
        ("OSC 11 background color query", "\e]11;?\e\\", "\e]11;?"),
        ("DECRQSS", "\eP$qm\e\\", "\eP$qm"),
        ("XTGETTCAP", "\eP+q544e\e\\", "\eP+q544e"),
    ];

    // ---------------------------------------------------------------- Loading

    [Test]
    public async Task LoadResolvesTheExportsAndFindsTheHost()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var library = Library();

        await Assert.That(Path.IsPathFullyQualified(library.Path)).IsTrue();
        await Assert.That(library.Path.EndsWith("conpty.dll", StringComparison.OrdinalIgnoreCase)).IsTrue();
        await Assert.That(library.FileVersion.Major).IsGreaterThanOrEqualTo(1);
        await Assert.That(library.HasConsoleHost).IsTrue().Because($"OpenConsole.exe is staged beside {library.Path}");
        await Assert.That(library.ConsoleHostPath).IsEqualTo(Path.Combine(Path.GetDirectoryName(library.Path)!, OutOfBandHost));

        // One instance per path, whatever the casing.
        await Assert.That(ReferenceEquals(ConPtyLibrary.Load(library.Path), library)).IsTrue();
        await Assert.That(ReferenceEquals(ConPtyLibrary.Load(library.Path.ToUpperInvariant()), library)).IsTrue();
    }

    [Test]
    public async Task LoadRejectsWhatItCannotUse()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // The platform guard above does not reach into the lambdas, which the analyzer reports.
#pragma warning disable CA1416
        // A bare name would be searched for along the loader's path and could bind a copy the operating system ships.
        Assert.Throws<ArgumentException>(() => ConPtyLibrary.Load("conpty.dll"));
        Assert.Throws<ArgumentException>(() => ConPtyLibrary.Load(""));
        Assert.Throws<FileNotFoundException>(() => ConPtyLibrary.Load(
            Path.Combine(Path.GetTempPath(), "minipty-conpty-" + Guid.NewGuid().ToString("N"), "conpty.dll")));
        // A DLL that is not an out-of-band ConPTY: kernel32 exports CreatePseudoConsole, but not the Conpty-prefixed set.
        Assert.Throws<EntryPointNotFoundException>(() => ConPtyLibrary.Load(Path.Combine(Environment.SystemDirectory, "kernel32.dll")));
#pragma warning restore CA1416
        await Task.CompletedTask;
    }

    [Test]
    public async Task LoadIsWindowsOnly()
    {
        if (OperatingSystem.IsWindows())
            return;

        // Calling the Windows-only method elsewhere is the point of this test.
#pragma warning disable CA1416
        Assert.Throws<PlatformNotSupportedException>(() => ConPtyLibrary.Load("/tmp/conpty.dll"));
#pragma warning restore CA1416
        await Assert.That(ConPtyLibrary.TryLoadFromApplicationDirectory()).IsNull();
    }

    // ---------------------------------------------------------------- Which host runs

    /// <summary>
    /// The process census is the only direct evidence of which implementation a session runs on: a session on the
    /// out-of-band ConPTY runs under an <c>OpenConsole.exe</c> child of this process, one on the in-box ConPTY under a
    /// <c>conhost.exe</c> child, and both go away with the session.
    /// </summary>
    [Test]
    [NotInParallel("conpty-census")]
    [Arguments(true)]
    [Arguments(false)]
    public async Task SessionRunsUnderTheHostOfItsImplementation(bool outOfBand)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var host = outOfBand ? OutOfBandHost : InBoxHost;
        var startInfo = outOfBand ? Shell() with { ConPty = Library() } : Shell();
        var hostsBefore = CountChildren(host);

        await using var session = Pty.Start(startInfo);
        var output = ReadAllTextAsync(session);
        await Assert.That(await WaitUntilAsync(() => CountChildren(host) > hostsBefore, Timeout)).IsTrue()
            .Because($"a {host} child of this process should host the session");

        await session.WriteInputAsync("exit\r");
        await session.WaitForExitAsync().WaitAsync(Timeout);
        await output.WaitAsync(Timeout);
        await Assert.That(await WaitUntilAsync(() => CountChildren(host) == hostsBefore, Timeout)).IsTrue()
            .Because($"the {host} should end with the session");
    }

    /// <summary>
    /// <c>conpty.dll</c> with no <c>OpenConsole.exe</c> where it looks launches the in-box <c>conhost.exe</c> instead,
    /// without an error. <see cref="ConPtyLibrary.HasConsoleHost"/> reports that before the first spawn.
    /// </summary>
    [Test]
    [NotInParallel("conpty-census")]
    public async Task WithoutItsHostTheLibraryReportsSoAndSessionsRunOnConhost()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var staged = Library();
        // A copy of the DLL alone, kept in the test output: once loaded, a module cannot be deleted, and it need not be.
        var directory = Path.Combine(AppContext.BaseDirectory, "conpty-without-host");
        Directory.CreateDirectory(directory);
        var copy = Path.Combine(directory, "conpty.dll");
        if (!File.Exists(copy))
            File.Copy(staged.Path, copy);

        var library = ConPtyLibrary.Load(copy);
        await Assert.That(library.HasConsoleHost).IsFalse();
        await Assert.That(library.ConsoleHostPath).IsNull();
        await Assert.That(library.FileVersion).IsEqualTo(staged.FileVersion);

        var conhostsBefore = CountChildren(InBoxHost);
        await using var session = Pty.Start(Shell() with { ConPty = library });
        var output = ReadAllTextAsync(session);
        await Assert.That(await WaitUntilAsync(() => CountChildren(InBoxHost) > conhostsBefore, Timeout)).IsTrue()
            .Because("conpty.dll without its host falls back to conhost.exe");

        await session.WriteInputAsync("exit\r");
        await session.WaitForExitAsync().WaitAsync(Timeout);
        await output.WaitAsync(Timeout);
    }

    // ---------------------------------------------------------------- Behavior of the out-of-band host

    /// <summary>
    /// The in-box console host of the measured Windows builds consumes mouse tracking modes, DECCKM, and terminal
    /// queries instead of forwarding them, so a terminal emulator never learns that a program wants mouse events. The
    /// out-of-band host forwards them.
    /// </summary>
    [Test]
    public async Task ForwardsTheSequencesTheInBoxHostConsumes()
    {
        if (!OperatingSystem.IsWindows())
            return;

        await using var session = Pty.Start(VtProbeChild() with { ConPty = Library() });
        var result = await session.CompleteAsync().WaitAsync(Timeout);

        await Assert.That(result.ExitCode).IsEqualTo(0);
        var output = result.GetText().ToString();
        foreach (var (name, _, needle) in VtProbes)
        {
            await Assert.That(output.Contains(needle, StringComparison.Ordinal)).IsTrue()
                .Because($"{name} should reach the frontend; output was {Escape(output)}");
        }
    }

    /// <summary>
    /// The out-of-band host asks the frontend what terminal it is when the session starts. A terminal emulator answers
    /// that on its own; a consumer that only reads does not, and with the verified package version the host does not
    /// wait for the answer, where earlier versions waited three seconds per session.
    /// </summary>
    [Test]
    [NotInParallel("conpty-timing")]
    public async Task ReadOnlyCompletionDoesNotWaitForTheDeviceAttributesAnswer()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var stopwatch = Stopwatch.StartNew();
        await using var session = Pty.Start(WindowsCommand("echo DA1_MARKER") with { ConPty = Library() });
        var result = await session.CompleteAsync().WaitAsync(Timeout);
        stopwatch.Stop();

        await Assert.That(result.ExitCode).IsEqualTo(0);
        var output = result.GetText().ToString();
        await Assert.That(output).Contains("DA1_MARKER");
        await Assert.That(output).Contains(DeviceAttributesQuery);
        await Assert.That(stopwatch.ElapsedMilliseconds).IsLessThan(2500)
            .Because("the host must not wait for a device attributes answer that never comes");
    }

    /// <summary>
    /// On the out-of-band path MiniPty releases its reference to the console server once the child is attached, so
    /// the host exits with its last client and the output pipe breaks: output ends on the pipe break, not after the
    /// post-exit quiet interval the in-box path needs.
    /// </summary>
    [Test]
    [NotInParallel("conpty-timing")]
    public async Task OutputEndsOnThePipeBreakAfterExit()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var outOfBand = await MedianEndOfOutputLatencyAsync(Library());
        var inBox = await MedianEndOfOutputLatencyAsync(null);

        await Assert.That(outOfBand).IsLessThan(inBox)
            .Because($"output ended {outOfBand:F1} ms after exit out-of-band and {inBox:F1} ms in-box, whose quiet interval is {PtyOutputPump.DefaultPostExitQuietMs} ms");
    }

    // ---------------------------------------------------------------- Clear

    /// <summary>
    /// <see cref="PtyTerminal.Clear"/> empties the out-of-band host's buffer: the host emits nothing for it, as the
    /// frontend has already cleared its own display, but the next output is placed at the top of the screen instead
    /// of below what the host would otherwise still hold.
    /// </summary>
    [Test]
    public async Task ClearEmptiesTheHostBufferSoTheNextOutputStartsAtTheTop()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var output = new StringBuilder();
        await using var terminal = PtyTerminal.Start(Shell() with { ConPty = Library() }, new PtyTerminalOptions
        {
            Output = (data, _) =>
            {
                lock (output)
                    output.Append(Encoding.UTF8.GetString(data.Span));

                return ValueTask.CompletedTask;
            },
        });

        await WaitForAsync(output, ">");
        await terminal.WriteInputAsync("echo AAAA\r");
        await WaitForAsync(output, "AAAA\r\n");
        int mark;
        lock (output)
            mark = output.Length;

        terminal.Clear();
        // The clear travels on the host's signal pipe and input on its input pipe, with no ordering between them; the
        // host applies the clear within milliseconds, and a user types the next command long after. Typing at once
        // raced the clear and lost in 1 of 1 runs.
        await Task.Delay(500);

        await terminal.WriteInputAsync("echo BBBB\r");
        await WaitForAsync(output, "BBBB\r\n");
        string afterClear;
        lock (output)
            afterClear = output.ToString(mark, output.Length - mark);
        await Assert.That(afterClear.Contains(CursorHome, StringComparison.Ordinal)).IsTrue()
            .Because($"the host's buffer is empty after the clear, so the next output starts at row 1; output was {Escape(afterClear)}");

        await terminal.WriteInputAsync("exit\r");
        await terminal.Completion.WaitAsync(Timeout);
    }

    /// <summary>The in-box ConPTY exposes no clear operation: the call is a no-op and output continues where it was.</summary>
    [Test]
    public async Task ClearIsANoOpOnTheInBoxConPty()
    {
        if (!OperatingSystem.IsWindows())
            return;

        await using var session = Pty.Start(Shell());
        using var cts = new CancellationTokenSource(Timeout);
        await using var reader = session.ReadOutputAsync(cts.Token).GetAsyncEnumerator();
        var output = new StringBuilder();
        await ReadUntilAsync(reader, output, ">");
        await session.WriteInputAsync("echo AAAA\r", cancellationToken: cts.Token);
        await ReadUntilAsync(reader, output, "AAAA\r\n");
        var mark = output.Length;

        session.Clear();
        await Task.Delay(500);

        await session.WriteInputAsync("echo BBBB\r", cancellationToken: cts.Token);
        await ReadUntilAsync(reader, output, "BBBB\r\n");
        await Assert.That(output.ToString(mark, output.Length - mark).Contains(CursorHome, StringComparison.Ordinal)).IsFalse();

        await session.WriteInputAsync("exit\r", cancellationToken: cts.Token);
        await session.WaitForExitAsync().WaitAsync(Timeout);
        while (await reader.MoveNextAsync())
        {
        }
    }

    [Test]
    public async Task ClearIsANoOpOnUnix()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var session = Pty.Start(new PtyStartInfo { FileName = "sh", Arguments = ["-c", "IFS= read -r _"] });
        session.Clear();
        await Assert.That(session.HasExited).IsFalse();
        session.Kill();
        await session.WaitForExitAsync().WaitAsync(Timeout);
    }

    [Test]
    public async Task ClearAfterDisposeThrows()
    {
        var session = Pty.Start(Exits(0));
        await session.WaitForExitAsync().WaitAsync(Timeout);
        await session.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => session.Clear());

        var terminal = PtyTerminal.Start(Exits(0), new PtyTerminalOptions { Output = static (_, _) => ValueTask.CompletedTask });
        await terminal.Completion.WaitAsync(Timeout);
        await terminal.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => terminal.Clear());
    }

    // ---------------------------------------------------------------- Teardown

    /// <summary>Starts and teardowns on the out-of-band ConPTY leak no handle.</summary>
    [Test]
    [NotInParallel]
    public async Task StartAndTeardownLeakNothing()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var library = Library();
        await AssertNoLeakAsync(async () =>
        {
            await using var session = Pty.Start(Exits(0) with { ConPty = library });
            var output = ReadAllTextAsync(session);
            await session.WaitForExitAsync().WaitAsync(Timeout);
            await output.WaitAsync(Timeout);
        });
    }

    // ---------------------------------------------------------------- Helpers

    private static ConPtyLibrary Library() =>
        ConPtyLibrary.TryLoadFromApplicationDirectory()
        ?? throw new InvalidOperationException(
            "conpty.dll is not staged in the test output; the Microsoft.Windows.Console.ConPTY package reference places it under runtimes\\win-<arch>\\native.");

    private static PtyStartInfo Shell() => new() { FileName = ComSpec(), Arguments = ["/k"], Size = new(120, 30) };

    private static PtyStartInfo WindowsCommand(string command) => new() { FileName = ComSpec(), Arguments = ["/c", command], Size = new(120, 30) };

    private static string ComSpec() => Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";

    private static PtyStartInfo VtProbeChild() => new()
    {
        FileName = TestExecutable,
        Environment = new Dictionary<string, string?> { [VtProbeChildVariable] = "1" },
        Size = new(120, 30),
    };

    private static string Escape(string text) => text.Replace("\e", "\\e").Replace("\r", "\\r").Replace("\n", "\\n");

    /// <summary>Median over three sessions of the time from the child's exit to the end of output, in milliseconds.</summary>
    private static async Task<double> MedianEndOfOutputLatencyAsync(ConPtyLibrary? library)
    {
        var latencies = new double[3];
        for (var i = 0; i < latencies.Length; i++)
        {
            await using var session = Pty.Start(WindowsCommand("echo hi") with { ConPty = library });
            long endOfOutputAt = 0;
            var read = Task.Run(async () =>
            {
                await foreach (var _ in session.ReadOutputAsync())
                {
                }

                endOfOutputAt = Stopwatch.GetTimestamp();
            });
            await session.WaitForExitAsync().WaitAsync(Timeout);
            var exitAt = Stopwatch.GetTimestamp();
            await read.WaitAsync(Timeout);
            latencies[i] = (endOfOutputAt - exitAt) * 1000.0 / Stopwatch.Frequency;
        }

        Array.Sort(latencies);
        return latencies[1];
    }

    private static async Task WaitForAsync(StringBuilder output, string text)
    {
        var deadline = Environment.TickCount64 + (long)Timeout.TotalMilliseconds;
        while (true)
        {
            lock (output)
            {
                if (output.ToString().IndexOf(text, StringComparison.Ordinal) >= 0)
                    return;
            }

            if (Environment.TickCount64 >= deadline)
                throw new TimeoutException($"'{Escape(text)}' did not arrive; output was {Escape(output.ToString())}");

            await Task.Delay(20);
        }
    }

    private static async Task ReadUntilAsync(IAsyncEnumerator<PtyOutputChunk> reader, StringBuilder output, string text)
    {
        while (output.ToString().IndexOf(text, StringComparison.Ordinal) < 0)
        {
            if (!await reader.MoveNextAsync())
                throw new InvalidOperationException($"Output ended before '{Escape(text)}': {Escape(output.ToString())}");

            output.Append(Encoding.UTF8.GetString(reader.Current.Data.Span));
        }
    }

    /// <summary>Children of this process named <paramref name="exeName"/>, from a Toolhelp snapshot.</summary>
    private static int CountChildren(string exeName)
    {
        var snapshot = CreateToolhelp32Snapshot(Th32CsSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
            throw new Win32Exception();

        try
        {
            var self = (uint)Environment.ProcessId;
            var count = 0;
            var entry = new ProcessEntry { dwSize = (uint)Marshal.SizeOf<ProcessEntry>() };
            for (var more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
            {
                if (entry.th32ParentProcessID == self && string.Equals(entry.szExeFile, exeName, StringComparison.OrdinalIgnoreCase))
                    count++;
            }

            return count;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    /// <summary>
    /// Makes this test executable the VT probe child when <see cref="VtProbeChildVariable"/> is set: it writes every
    /// probe sequence and exits. Runs before the test framework starts; in a test run the variable is unset and this
    /// returns at once.
    /// </summary>
    [ModuleInitializer]
    internal static void RunAsVtProbeChildWhenAsked()
    {
        if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable(VtProbeChildVariable) is not null)
            Environment.Exit(RunVtProbeChild());
    }

    private static int RunVtProbeChild()
    {
        var stdout = GetStdHandle(StdOutputHandle);
        if (GetConsoleMode(stdout, out var mode))
            SetConsoleMode(stdout, mode | EnableVirtualTerminalProcessing | DisableNewlineAutoReturn);

        using var output = System.Console.OpenStandardOutput();
        foreach (var (_, sequence, _) in VtProbes)
        {
            output.Write(Encoding.UTF8.GetBytes(sequence));
            output.Write("\r\n"u8);
        }

        output.Flush();
        // Lets the host render the last frame before its client count drops to zero.
        Thread.Sleep(100);
        return 0;
    }

    private const int StdOutputHandle = -11;
    private const uint EnableVirtualTerminalProcessing = 0x0004;
    private const uint DisableNewlineAutoReturn = 0x0008;
    private const uint Th32CsSnapProcess = 0x2;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref ProcessEntry lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref ProcessEntry lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary><c>PROCESSENTRY32W</c>.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nuint th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }
}
