#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property PublishAot=true
#:project ../../src/MiniPty/MiniPty.csproj
#:package Microsoft.Windows.Console.ConPTY@1.25.260930003

// Reproduces what a session started with PtyStartInfo.InheritCursor = true looks like from the frontend, depending on
// whether the frontend answers ConPTY's cursor position query (ESC[6n), on the in-box ConPTY and on the out-of-band
// one. Each scenario prints a timeline of what the frontend receives and does, so the stall is visible as time.
//
//   dotnet samples/SingleFiles/InheritCursorWait.cs [--give-up <seconds>]
//
// The out-of-band scenario runs when the Microsoft.Windows.Console.ConPTY package referenced above is staged beside
// the app, which MiniPty's build targets do. Windows only; Unix ignores InheritCursor.

using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using MiniPty;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("InheritCursor applies to the Windows ConPTY only; a Unix PTY never clears the screen or asks.");
    return 0;
}

var giveUp = TimeSpan.FromSeconds(args is ["--give-up", var s] && int.TryParse(s, out var n) ? n : 5);
var outOfBand = ConPtyLibrary.TryLoadFromApplicationDirectory();

Console.WriteLine($"Windows build {Environment.OSVersion.Version.Build}");
Console.WriteLine(outOfBand is { HasConsoleHost: true }
    ? $"Out-of-band ConPTY {outOfBand.FileVersion} with OpenConsole.exe"
    : "Out-of-band ConPTY not staged; scenario 6 is skipped");
Console.WriteLine($"Child: cmd /c {Scenario.ChildCommand}");

await Scenario.RunAsync(
    "1. InheritCursor = false (the default)",
    "No question is asked, so nothing waits, but ConPTY clears the frontend's screen first (ESC[2J).",
    conPty: null, inheritCursor: false, answerAt: null, ctrlCAt: null, giveUp);

await Scenario.RunAsync(
    "2. InheritCursor = true, the frontend answers at once",
    "What xterm.js, VS Code, and Windows Terminal do. No clear, and no visible delay.",
    conPty: null, inheritCursor: true, answerAt: TimeSpan.Zero, ctrlCAt: null, giveUp);

await Scenario.RunAsync(
    "3. InheritCursor = true, the frontend never answers (in-box ConPTY)",
    "A read-only consumer, or a frontend that does not implement the query. The user presses Ctrl+C after 1 s; the app gives up and kills the session at --give-up.",
    conPty: null, inheritCursor: true, answerAt: null, ctrlCAt: TimeSpan.FromSeconds(1), giveUp);

await Scenario.RunAsync(
    "4. InheritCursor = true, the frontend answers 3 s late (in-box ConPTY)",
    "A slow frontend. Nobody touches the keyboard.",
    conPty: null, inheritCursor: true, answerAt: TimeSpan.FromSeconds(3), ctrlCAt: null, giveUp);

await Scenario.RunAsync(
    "5. Same as 4, and the user presses Ctrl+C after 1 s (in-box ConPTY)",
    "Input written while ConPTY waits is held, not dropped.",
    conPty: null, inheritCursor: true, answerAt: TimeSpan.FromSeconds(3), ctrlCAt: TimeSpan.FromSeconds(1), giveUp);

if (outOfBand is { HasConsoleHost: true })
{
    await Scenario.RunAsync(
        "6. InheritCursor = true, the frontend never answers (out-of-band ConPTY)",
        "The out-of-band host stops waiting after 1 s and runs the child.",
        conPty: outOfBand, inheritCursor: true, answerAt: null, ctrlCAt: null, giveUp);
}

return 0;

[SupportedOSPlatform("windows")]
static class Scenario
{
    // Prints "start", works for about a second, prints "end": a command whose progress is visible on a timeline.
    public const string ChildCommand = "echo start& ping -n 2 127.0.0.1 >nul& echo end";

    private static readonly object s_gate = new();

    public static async Task RunAsync(
        string title, string story, ConPtyLibrary? conPty, bool inheritCursor, TimeSpan? answerAt, TimeSpan? ctrlCAt, TimeSpan giveUp)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {title}");
        Console.WriteLine($"    {story}");

        var clock = Stopwatch.StartNew();
        using var session = Pty.Start(new PtyStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = ["/c", ChildCommand],
            Size = new PtySize(80, 24),
            InheritCursor = inheritCursor,
            ConPty = conPty,
        });

        TimeSpan? childText = null;
        var lastOutput = TimeSpan.Zero;
        var queryAnswered = false;
        var gaveUp = false;
        using var stop = new CancellationTokenSource();

        var reader = Task.Run(async () =>
        {
            try
            {
                await foreach (var chunk in session.ReadOutputAsync(stop.Token))
                {
                    var text = Encoding.UTF8.GetString(chunk.Data.Span);
                    lock (s_gate)
                    {
                        lastOutput = clock.Elapsed;
                        if (childText is null && text.Contains("start", StringComparison.Ordinal))
                            childText = clock.Elapsed;
                        Log(clock, "output", Escape(text) + Notes(text));
                    }

                    // A terminal emulator answers the query the moment it parses it.
                    if (answerAt == TimeSpan.Zero && !queryAnswered && text.Contains("\e[6n", StringComparison.Ordinal))
                    {
                        queryAnswered = true;
                        await AnswerAsync(session, clock);
                    }
                }

                lock (s_gate)
                    Log(clock, "end", "output ended");
            }
            catch (OperationCanceledException)
            {
            }
        });

        // What the user sees while nothing arrives: a blank screen and a child that is alive.
        var ticker = Task.Run(async () =>
        {
            while (!reader.IsCompleted)
            {
                await Task.WhenAny(reader, Task.Delay(1000));
                lock (s_gate)
                {
                    if (!reader.IsCompleted && !gaveUp && clock.Elapsed - lastOutput >= TimeSpan.FromSeconds(1))
                        Log(clock, "screen", $"nothing new for {(clock.Elapsed - lastOutput).TotalSeconds:F0} s; child alive: {!session.HasExited}");
                }
            }
        });

        var actions = new List<(TimeSpan At, Func<Task> Act)>();
        if (ctrlCAt is { } c)
        {
            actions.Add((c, async () =>
            {
                lock (s_gate)
                    Log(clock, "input", "^C (the user presses Ctrl+C)");
                await session.WriteInputAsync("\x03"u8.ToArray());
            }));
        }

        if (answerAt is { } a && a > TimeSpan.Zero)
            actions.Add((a, () => AnswerAsync(session, clock)));

        actions.Sort(static (x, y) => x.At.CompareTo(y.At));
        foreach (var (at, act) in actions)
        {
            var wait = at - clock.Elapsed;
            if (wait > TimeSpan.Zero)
                await Task.WhenAny(reader, Task.Delay(wait));
            if (!session.HasExited)
                await act();
        }

        var remaining = giveUp - clock.Elapsed;
        if (await Task.WhenAny(reader, Task.Delay(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero)) != reader)
        {
            lock (s_gate)
            {
                gaveUp = true;
                Log(clock, "give up", $"no end after {giveUp.TotalSeconds:F0} s; Kill ends the session");
            }

            session.Kill();
            await session.WaitForExitAsync();
            await reader.WaitAsync(TimeSpan.FromSeconds(5));
        }

        await ticker;
        var exit = session.ExitCode switch
        {
            null => "no exit code",
            >= 0 and < 256 and var code => $"exit code {code}",
            var code => $"exit code 0x{code:X8}",
        };
        var shown = childText is { } t ? $"the child's first text after {t.TotalMilliseconds:F0} ms" : "the child never printed anything";
        Console.WriteLine($"    => {shown}; {exit}");
    }

    private static async Task AnswerAsync(PtySession session, Stopwatch clock)
    {
        lock (s_gate)
            Log(clock, "input", "ESC[1;1R (the frontend answers: row 1, column 1)");
        await session.WriteInputAsync("\e[1;1R"u8.ToArray());
    }

    private static void Log(Stopwatch clock, string kind, string message) =>
        Console.WriteLine($"    +{clock.ElapsedMilliseconds,6} ms  {kind,-8} {message}");

    private static string Notes(string text)
    {
        var notes = new StringBuilder();
        if (text.Contains("\e[6n", StringComparison.Ordinal))
            notes.Append("  <- cursor position query");
        if (text.Contains("\e[c", StringComparison.Ordinal))
            notes.Append("  <- device attributes query");
        if (text.Contains("\e[2J", StringComparison.Ordinal))
            notes.Append("  <- clear screen");
        return notes.ToString();
    }

    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c == '\e')
                sb.Append("ESC");
            else if (c == '\r')
                sb.Append("\\r");
            else if (c == '\n')
                sb.Append("\\n");
            else if (c < ' ')
                sb.Append('^').Append((char)(c + '@'));
            else
                sb.Append(c);
        }

        return sb.ToString();
    }
}
