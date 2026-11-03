using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace MiniPty.Benchmarks;

/// <summary>
/// Session start in a host with a large resident heap, the case of IDE backends and web terminals. A start that forks
/// copies the host's page tables and marks every resident page copy-on-write before the child can exec, so its cost
/// grows with the host's resident memory. Linux starts children with <c>clone(CLONE_VM | CLONE_VFORK)</c>, macOS with
/// <c>posix_spawn</c>, and Windows with <c>CreateProcess</c>, so a start should cost the same at every
/// <see cref="HostResidentMiB"/>.
/// </summary>
/// <remarks>
/// The memory is native, so the GC neither moves nor scans it, and the global setup writes every 4 KiB of it, so it is
/// resident. Each parameter runs in a process of its own. With transparent huge pages set to <c>always</c> the kernel
/// may back it with 2 MiB pages, which a fork copies far faster. Compare forks with 4 KiB pages (Ubuntu's default,
/// <c>madvise</c>).
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0, warmupCount: 2, iterationCount: 5, launchCount: 1)]
public class PtyStartupHostMemoryBenchmarks
{
    private const int PageBytes = 4096;
    private IntPtr memory;
    private PtyStartInfo exit0 = null!;
    private bool supported;

    /// <summary>Resident memory the host holds beside the runtime's own while it starts sessions.</summary>
    [Params(0, 2048)]
    public int HostResidentMiB { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        supported = BenchmarkPtyCommands.IsSupported;
        if (!supported)
            return;

        exit0 = BenchmarkPtyCommands.Exit0();
        var bytes = (nuint)HostResidentMiB * 1024 * 1024;
        if (bytes == 0)
            return;

        unsafe
        {
            var pages = (byte*)NativeMemory.Alloc(bytes);
            for (nuint offset = 0; offset < bytes; offset += PageBytes)
                pages[offset] = 1;

            memory = (IntPtr)pages;
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        unsafe
        {
            if (memory != IntPtr.Zero)
                NativeMemory.Free((void*)memory);
        }

        memory = IntPtr.Zero;
    }

    /// <summary>One session from <see cref="Pty.Start"/> to its exit, as <see cref="PtyStartupBenchmarks.Session_Exit0_WaitForExit"/>.</summary>
    [Benchmark]
    public async Task<int> Session_Exit0_WaitForExit()
    {
        if (!supported)
            return 0;

        await using var session = Pty.Start(exit0);
        return (await session.WaitForExitAsync().ConfigureAwait(false)).ExitCode;
    }
}
