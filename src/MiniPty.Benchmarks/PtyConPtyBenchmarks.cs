using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using MiniPty.Capture;

namespace MiniPty.Benchmarks;

/// <summary>
/// The in-box ConPTY against the out-of-band one an application supplies (<see cref="PtyStartInfo.ConPty"/>), on the
/// same scenarios as <see cref="PtyStartupBenchmarks"/>. Windows only: elsewhere, and when <c>conpty.dll</c> is not
/// staged in the benchmark output, the methods return 0 without spawning.
/// </summary>
/// <remarks>
/// Both paths release MiniPty's reference to the console server after spawn, so the host exits with the child and the
/// pipe breaks at once, except the in-box path on Windows builds before 11 24H2 (26100), whose <c>kernel32.dll</c> has no
/// <c>ReleasePseudoConsole</c>: there output ends only after the pseudo-console is closed, which the post-exit drain does
/// once reads have been quiet for 100 ms, and every scenario that reads to the end pays that interval in-box.
/// Allocations include OS process spawn; the console host process (<c>conhost.exe</c> or <c>OpenConsole.exe</c>) is
/// part of each spawn.
/// </remarks>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[SimpleJob(RuntimeMoniker.Net10_0, warmupCount: 2, iterationCount: 5, launchCount: 1)]
public class PtyConPtyBenchmarks
{
    private PtyStartInfo echo = null!;
    private PtyStartInfo exit0 = null!;
    private PtyStartInfo smallStdout = null!;
    private bool supported;

    /// <summary><see langword="true"/> runs on the out-of-band ConPTY, <see langword="false"/> on the one Windows ships.</summary>
    [Params(false, true)]
    public bool OutOfBand { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        supported = OperatingSystem.IsWindows() && BenchmarkPtyCommands.IsSupported;
        if (!supported)
            return;

        ConPtyLibrary? library = null;
        if (OutOfBand)
        {
            library = ConPtyLibrary.TryLoadFromApplicationDirectory();
            // Without the DLL there is nothing to compare; with the DLL but no host the session would silently run
            // on conhost and compare the in-box implementation with itself.
            supported = library is { HasConsoleHost: true };
            if (!supported)
                return;
        }

        echo = BenchmarkPtyCommands.Echo() with { ConPty = library };
        exit0 = BenchmarkPtyCommands.Exit0() with { ConPty = library };
        smallStdout = BenchmarkPtyCommands.SmallStdout(32_768) with { ConPty = library };
    }

    /// <summary>Spawn and exit observation only: the cost of starting each implementation's console host.</summary>
    [BenchmarkCategory("ConPty", "Exit")]
    [Benchmark]
    public async Task<int> Session_Exit0_WaitForExit()
    {
        if (!supported)
            return 0;

        await using var session = Pty.Start(exit0);
        return (await session.WaitForExitAsync().ConfigureAwait(false)).ExitCode;
    }

    [BenchmarkCategory("ConPty", "Binary")]
    [Benchmark]
    public async Task<int> Session_Echo_Bytes()
    {
        if (!supported)
            return 0;

        await using var session = Pty.Start(echo);
        var result = await session.CompleteAsync(BenchmarkOptions.BytesOnly).ConfigureAwait(false);
        return result.Output.Length;
    }

    [BenchmarkCategory("ConPty", "Streaming", "Binary")]
    [Benchmark]
    public async Task<int> Session_32KiB_StreamBytes()
    {
        if (!supported)
            return 0;

        await using var session = Pty.Start(smallStdout);
        var length = 0;
        await foreach (var chunk in session.ReadOutputAsync())
            length += chunk.Data.Length;

        return length;
    }

    [BenchmarkCategory("ConPty", "Binary")]
    [Benchmark]
    public async Task<int> Capture_32KiB_Bytes()
    {
        if (!supported)
            return 0;

        var result = await PtyCapture.RunAsync(smallStdout, BenchmarkOptions.CaptureBytesOnly).ConfigureAwait(false);
        return result.Output.Length;
    }
}
