using System.Runtime.InteropServices;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace MiniPty.Benchmarks;

/// <summary>
/// Cost of one stdin write into a live session whose child reads and discards its input.
/// </summary>
/// <remarks>
/// Spawn is outside the measurement: one session lives for the whole run, and a background reader drains the
/// terminal's echo so output never backs up into the child. Each operation writes one 64-byte line. The writes come
/// faster than the terminal takes input, so some of them wait for room, as on Windows for ConPTY to drain the 4 KiB
/// input pipe. The mean time is therefore bounded by how fast the terminal takes input. The allocation column measures
/// the write path, waits included.
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0, warmupCount: 2, iterationCount: 5, launchCount: 1)]
public class PtyInputBenchmarks
{
    private PtySession? session;
    private Task? drain;
    private byte[] line = [];
    private string text = "";

    [GlobalSetup]
    public void Setup()
    {
        if (!BenchmarkPtyCommands.IsSupported)
            return;

        // Cooked console input ends a line on CR under ConPTY and on LF on Unix.
        var newline = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "\r" : "\n";
        text = new string('x', 63) + newline;
        line = Encoding.ASCII.GetBytes(text);
        session = Pty.Start(BenchmarkPtyCommands.StdinSink());
        drain = Task.Run(async () =>
        {
            await foreach (var _ in session.ReadOutputAsync())
            {
            }
        });
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        session?.Dispose();
        try
        {
            drain?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }
    }

    [Benchmark]
    public ValueTask WriteInputBytes() => session is null ? default : session.WriteInputAsync(line);

    [Benchmark]
    public ValueTask WriteInputText() => session is null ? default : session.WriteInputAsync(text);

    [Benchmark]
    public ValueTask InputStreamWriteAsync() => session is null ? default : session.Input.WriteAsync(line);
}
