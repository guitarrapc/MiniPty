using System.Diagnostics;
using System.Globalization;

namespace MiniPty.Benchmarks;

/// <summary>
/// The whole of a session's start, many times over: an opt-in probe, not a BenchmarkDotNet benchmark.
/// <c>dotnet run -c Release --project src/MiniPty.Benchmarks -- --start-probe [rounds] [program [arguments...]]</c>.
/// </summary>
/// <remarks>
/// Each round runs what <see cref="PtyStartupBenchmarks.Session_Exit0_WaitForExit"/> runs: <see cref="Pty.Start"/>, the
/// wait for the exit, and the disposal. It prints the minimum, median and mean, in microseconds, in the format of
/// <c>scripts/macos-start-probe/start-probe.c</c>, which times the native parts of a macOS start on the same runner:
/// together they split a start into process creation, the terminal, the spawn helper, and MiniPty's own work. Without
/// a program it runs the benchmark's <c>sh -c 'exit 0'</c> (<c>cmd.exe /c exit /b 0</c> on Windows).
/// </remarks>
internal static class StartProbe
{
    private const int WarmUpRounds = 10;

    internal static async Task<int> RunAsync(string[] args)
    {
        var rounds = args.Length > 0 && int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : 200;
        if (!BenchmarkPtyCommands.IsSupported)
        {
            System.Console.Error.WriteLine("PTY is not supported on this platform.");
            return 1;
        }

        var startInfo = args.Length > 1
            ? new PtyStartInfo { FileName = args[1], Arguments = args[2..] }
            : BenchmarkPtyCommands.Exit0();
        for (var i = 0; i < WarmUpRounds; i++)
            await StartOnceAsync(startInfo);

        var times = new double[rounds];
        double sum = 0;
        for (var i = 0; i < rounds; i++)
        {
            times[i] = await StartOnceAsync(startInfo);
            sum += times[i];
        }

        Array.Sort(times);
        var name = args.Length > 1 ? args[1] : $"{startInfo.FileName} {string.Join(' ', startInfo.Arguments)}";
        System.Console.WriteLine(
            $"start-probe: {name}, {rounds} rounds after {WarmUpRounds} warm-up rounds, times in microseconds");
        System.Console.WriteLine(
            string.Create(CultureInfo.InvariantCulture, $"  {"minipty",-6} min {times[0],8:F1}  median {times[rounds / 2],8:F1}  mean {sum / rounds,8:F1}"));
        return 0;
    }

    /// <summary>One session from <see cref="Pty.Start"/> through its disposal; its time in microseconds.</summary>
    private static async Task<double> StartOnceAsync(PtyStartInfo startInfo)
    {
        var started = Stopwatch.GetTimestamp();
        await using (var session = Pty.Start(startInfo))
        {
            var exit = await session.WaitForExitAsync().ConfigureAwait(false);
            if (exit.ExitCode != 0)
                throw new InvalidOperationException($"The probe's child exited with {exit.ExitCode}.");
        }

        return Stopwatch.GetElapsedTime(started).TotalMicroseconds;
    }
}
