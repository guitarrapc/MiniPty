#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property PublishAot=false
#:project ../src/MiniPty/MiniPty.csproj

// Temporary (improvement plan I4, 2026-11-08): measures whether a one-shot completion that closes an in-box ConPTY
// before its first output read loses output, on a build without ReleasePseudoConsole (windows-2022) and on one with it
// (windows-2025). A completion now waits for the first read before it closes, so a child that writes nothing costs the
// whole OutputDrainGrace there. Remove this probe and its CI job once the result is recorded.
//
//     dotnet run scripts/legacy-close-probe.cs
//
// OutputDrainGrace 0 closes the pseudo-console at the drain's first tick after the exit (within 10 ms), before the
// host's first output unless it came already. 100 ms closes it when the quiet interval would, which is what a
// completion without the first-read rule would do for a session with no output. Each run prints one line:
//
//     probe scenario=<name> run=<n> exit_ms=<ms> end_ms=<ms> bytes=<n> marker=<bool> [out=<escaped>]
//
// exit_ms and end_ms count from Pty.Start to the exit and to the completion's return. marker: whether the output holds
// the text the child printed last. out: the output, escaped, for the first runs of each scenario. Each scenario ends
// with a summary line: runs, runs with the marker, and the median and largest end_ms.

using System.Diagnostics;
using System.Text;
using MiniPty;

const int Runs = 30;
const int ParallelRounds = 10;
const int ParallelSessions = 8;

Console.WriteLine($"probe os={Environment.OSVersion.Version} cpus={Environment.ProcessorCount}");

var exit0 = Cmd("exit 0");
var echo = Cmd("echo minipty-probe");
var lines = Cmd("for /l %i in (1,1,40) do @echo line-%i");

var failed = false;
failed |= !await ScenarioAsync("exit0-default", exit0, null, null);
failed |= !await ScenarioAsync("exit0-grace0", exit0, TimeSpan.Zero, null);
failed |= !await ScenarioAsync("exit0-grace100", exit0, TimeSpan.FromMilliseconds(100), null);
failed |= !await ScenarioAsync("echo-default", echo, null, "minipty-probe");
failed |= !await ScenarioAsync("echo-grace0", echo, TimeSpan.Zero, "minipty-probe");
failed |= !await ScenarioAsync("echo-grace100", echo, TimeSpan.FromMilliseconds(100), "minipty-probe");
failed |= !await ScenarioAsync("lines-grace0", lines, TimeSpan.Zero, "line-40");
failed |= !await ScenarioAsync("lines-grace100", lines, TimeSpan.FromMilliseconds(100), "line-40");
failed |= !await ParallelScenarioAsync("echo-grace0-parallel8", echo, TimeSpan.Zero, "minipty-probe");
failed |= !await ParallelScenarioAsync("echo-grace100-parallel8", echo, TimeSpan.FromMilliseconds(100), "minipty-probe");

// A lost marker is the finding, not a failure of the probe; only a run that threw fails it.
return failed ? 1 : 0;

static PtyStartInfo Cmd(string command) => new() { FileName = "cmd.exe", Arguments = ["/c", command] };

static async Task<bool> ScenarioAsync(string name, PtyStartInfo info, TimeSpan? grace, string? marker)
{
    var ends = new List<long>();
    var withMarker = 0;
    for (var i = 0; i < Runs; i++)
    {
        try
        {
            var run = await RunOnceAsync(info, grace, marker);
            ends.Add(run.EndMs);
            withMarker += run.Marker ? 1 : 0;
            Print(name, i, run);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"probe scenario={name} run={i} error={ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    Summarize(name, ends, withMarker, marker);
    return true;
}

static async Task<bool> ParallelScenarioAsync(string name, PtyStartInfo info, TimeSpan? grace, string? marker)
{
    var ends = new List<long>();
    var withMarker = 0;
    var tasks = new Task<Run>[ParallelSessions];
    for (var round = 0; round < ParallelRounds; round++)
    {
        for (var j = 0; j < ParallelSessions; j++)
            tasks[j] = Task.Run(() => RunOnceAsync(info, grace, marker));

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"probe scenario={name} round={round} error={ex.GetType().Name}: {ex.Message}");
            return false;
        }

        for (var j = 0; j < ParallelSessions; j++)
        {
            var run = tasks[j].Result;
            ends.Add(run.EndMs);
            withMarker += run.Marker ? 1 : 0;
            Print(name, round * ParallelSessions + j, run);
        }
    }

    Summarize(name, ends, withMarker, marker);
    return true;
}

static async Task<Run> RunOnceAsync(PtyStartInfo info, TimeSpan? grace, string? marker)
{
    var clock = Stopwatch.StartNew();
    await using var session = Pty.Start(info);
    var exitMs = new long[] { -1 };
    var exited = session.WaitForExitAsync().ContinueWith(
        _ => exitMs[0] = clock.ElapsedMilliseconds,
        CancellationToken.None,
        TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);
    var options = grace is { } g
        ? new PtyCompleteOptions { DecodeOutput = false, OutputDrainGrace = g }
        : new PtyCompleteOptions { DecodeOutput = false };
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var result = await session.CompleteAsync(options, cts.Token);
    var endMs = clock.ElapsedMilliseconds;
    await exited;
    return new Run(exitMs[0], endMs, result.Output.ToArray(), marker is null || result.Contains(marker));
}

static void Print(string name, int index, Run run)
{
    var line = $"probe scenario={name} run={index} exit_ms={run.ExitMs} end_ms={run.EndMs} bytes={run.Output.Length} marker={run.Marker}";
    if (index < 3 || !run.Marker)
        line += $" out={Escape(run.Output)}";

    Console.WriteLine(line);
}

static void Summarize(string name, List<long> ends, int withMarker, string? marker)
{
    ends.Sort();
    var markerText = marker is null ? "n/a" : $"{withMarker}/{ends.Count}";
    Console.WriteLine($"summary scenario={name} runs={ends.Count} marker={markerText} end_ms_median={ends[ends.Count / 2]} end_ms_max={ends[^1]}");
}

static string Escape(byte[] bytes)
{
    var text = new StringBuilder();
    foreach (var b in bytes.AsSpan(0, Math.Min(bytes.Length, 400)))
    {
        if (b == 0x1B)
            text.Append("\\e");
        else if (b is >= 0x20 and < 0x7F)
            text.Append((char)b);
        else
            text.Append($"\\x{b:X2}");
    }

    if (bytes.Length > 400)
        text.Append("...");

    return text.ToString();
}

sealed record Run(long ExitMs, long EndMs, byte[] Output, bool Marker);
