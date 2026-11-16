#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property PublishAot=true
#:project ../src/MiniPty/MiniPty.csproj
#:package Microsoft.Windows.Console.ConPTY

// Record: plays each playground scene against a real interactive shell in a PTY, as a person at a terminal would, and
// writes what crossed MiniPty's API, event by event, for the static viewer in playground/site.
//
//   dotnet playground/Record.cs [--out <dir>] [--env-id <id>] [--env-label <label>] [--conpty inbox|oob|both] [--scene <id>]
//   dotnet playground/Record.cs --index-only [--out <dir>]
//
// A scene is what a terminal user does: type a command, resize the window, press Ctrl+C, type a password, close the
// tab. The recorder types each key through WriteInputAsync with a person's pace, resizes through Resize, closes
// through DisposeAsync, and records every output chunk from ReadOutputAsync and every change of InputMode. The viewer
// only replays these recordings. The shell is bash on Unix and Windows PowerShell on Windows, started in a
// throwaway folder with a fixed prompt, so recordings hold no path or name of the machine that made them.
//
// Output (default playground/site/recordings):
//   <env>/environment.json   the machine, the runtime, MiniPty's version, and the ConPTY in use
//   <env>/<scene>.json       the events of one scene
//   index.json               the scenes and every environment found under the output directory, rebuilt on each run
//                            (--index-only rebuilds just this, after CI collects the runners' output)
//
// Times are milliseconds since just before Pty.Start. An output event's time is when the recorder received the chunk
// from ReadOutputAsync. MiniPty defines no recording format (.github/docs/specs/capture.md); this one belongs to the
// playground only.

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MiniPty;

string? outArg = null, envId = null, envLabel = null, onlyScene = null;
var conPtyChoice = "both";
var indexOnly = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--out" when i + 1 < args.Length: outArg = args[++i]; break;
        case "--env-id" when i + 1 < args.Length: envId = args[++i]; break;
        case "--env-label" when i + 1 < args.Length: envLabel = args[++i]; break;
        case "--conpty" when i + 1 < args.Length: conPtyChoice = args[++i]; break;
        case "--scene" when i + 1 < args.Length: onlyScene = args[++i]; break;
        case "--index-only": indexOnly = true; break;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            Console.Error.WriteLine("Usage: dotnet playground/Record.cs [--out <dir>] [--env-id <id>] [--env-label <label>] [--conpty inbox|oob|both] [--scene <id>] [--index-only]");
            return 2;
    }
}

// A file-based app knows the directory of its .cs file; fall back to the repository root layout.
var playgroundDir = AppContext.GetData("EntryPointFileDirectoryPath") as string
    ?? Path.Combine(Directory.GetCurrentDirectory(), "playground");
var outDir = Path.GetFullPath(outArg ?? Path.Combine(playgroundDir, "site", "recordings"));
Directory.CreateDirectory(outDir);

if (!indexOnly)
{
    if (!Pty.IsSupported)
    {
        Console.Error.WriteLine("PTY is not supported on this operating system.");
        return 1;
    }

    List<Target> targets;
    try
    {
        targets = Target.Resolve(envId, envLabel, conPtyChoice);
    }
    catch (ArgumentException e)
    {
        Console.Error.WriteLine(e.Message);
        return 2;
    }

    using var demo = Demo.Create();
    var scenes = Scenes.Define(demo.Shell);
    if (onlyScene is not null && !Array.Exists(scenes, s => s.Id == onlyScene))
    {
        Console.Error.WriteLine($"Unknown scene \"{onlyScene}\".");
        return 2;
    }

    foreach (var target in targets)
    {
        var envDir = Directory.CreateDirectory(Path.Combine(outDir, target.Id)).FullName;
        Json.WriteFile(Path.Combine(envDir, "environment.json"), target.Write);

        // One unsaved run first, so the first saved scene's start is not the JIT compiling MiniPty's start path.
        try
        {
            _ = await Recorder.RecordAsync(target, demo, scenes[0]);
        }
        catch (TimeoutException e)
        {
            Console.Error.WriteLine($"FAIL warm-up run on {target.Id}: {e.Message}");
            return 1;
        }

        foreach (var scene in scenes)
        {
            if (onlyScene is not null && scene.Id != onlyScene)
                continue;
            Console.WriteLine($"{target.Id} / {scene.Id}");
            EventLog log;
            try
            {
                log = await Recorder.RecordAsync(target, demo, scene);
            }
            catch (TimeoutException e)
            {
                Console.Error.WriteLine($"  FAIL {e.Message}");
                return 1;
            }

            Json.WriteFile(Path.Combine(envDir, scene.Id + ".json"), w => log.Write(w, scene, target, demo.Shell));
            Console.WriteLine($"  {log.Summary()}");
        }
    }
}

PlaygroundIndex.Write(outDir, Scenes.Define(Shell.ForThisOs("")));
Console.WriteLine($"Wrote {Path.Combine(outDir, "index.json")}");
return 0;

/// <summary>
/// What a user does in one step. Waits and pauses are the recorder's; the viewer pauses at user steps and describes
/// each one from its fields.
/// </summary>
enum StepKind { Type, Key, Resize, Close, WaitFor, WaitForNoEcho, Pause }

sealed record Step(
    StepKind Kind,
    string? Text = null,
    byte[]? Bytes = null,
    string? KeyName = null,
    bool Enter = false,
    bool Secret = false,
    int Cols = 0,
    int Rows = 0,
    int Ms = 0)
{
    public static Step Type(string text) => new(StepKind.Type, Text: text, Enter: true);

    public static Step TypeSecret(string text) => new(StepKind.Type, Text: text, Enter: true, Secret: true);

    public static Step Key(string keyName, params byte[] bytes) => new(StepKind.Key, Bytes: bytes, KeyName: keyName);

    public static Step Resize(int cols, int rows) => new(StepKind.Resize, Cols: cols, Rows: rows);

    public static Step Close() => new(StepKind.Close);

    public static Step WaitFor(string text, int ms = 15000) => new(StepKind.WaitFor, Text: text, Ms: ms);

    public static Step WaitForNoEcho(int ms = 15000) => new(StepKind.WaitForNoEcho, Ms: ms);

    public static Step Pause(int ms) => new(StepKind.Pause, Ms: ms);
}

/// <summary>
/// One playground scene: what the user does, and what the viewer says to notice on Unix and on Windows. The notes live
/// in index.json, so fixing one needs no new recording.
/// </summary>
sealed record Scene(string Id, string Title, string Summary, string UnixNotice, string WindowsNotice, Step[] Steps);

/// <summary>
/// The scenes. Each runs in a fresh shell. Commands differ by OS only where the shells differ; the user's actions are
/// the same everywhere.
/// </summary>
static class Scenes
{
    public static Scene[] Define(Shell shell)
    {
        var windows = shell.IsWindows;
        var prompt = shell.PromptMarker;
        return
        [
            new("command", "Run a command",
                "The user types a command and presses Enter. WriteInputAsync sends each key to the PTY. ReadOutputAsync returns everything on the screen, including the echo of each key.",
                "At the prompt, the bash line editor disables the PTY echo and draws each key itself. So InputMode shows Echo off and line input off. While a command runs, InputMode shows both on. Japanese text is UTF-8 in both directions.",
                "The PowerShell line editor redraws the command line in color after each key. ConPTY converts that drawing to VT output. InputMode is null because ConPTY cannot report it.",
                [Step.WaitFor(prompt), Step.Pause(600), Step.Type("ls"), Step.WaitFor(prompt), Step.Pause(800),
                 Step.Type("echo \"こんにちは MiniPty\""), Step.WaitFor(prompt), Step.Pause(900)]),

            new("resize", "Resize the window",
                "The user starts a full-screen program and resizes the window twice. Resize sends the new size to the PTY. Then the program redraws to fill the window.",
                "The PTY sends SIGWINCH to the program. The program reads the new size and redraws. While the program waits for a key, it reads raw input, so InputMode shows line input off.",
                "Windows has no SIGWINCH, so the program polls its window size. ConPTY also repaints the whole screen after each resize.",
                [Step.WaitFor(prompt), Step.Pause(500), Step.Type("fullscreen"), Step.WaitFor("80 x 22"), Step.Pause(1000),
                 Step.Resize(100, 28), Step.WaitFor("100 x 28"), Step.Pause(1000),
                 Step.Resize(60, 16), Step.WaitFor("60 x 16"), Step.Pause(1000),
                 Step.Key("q", (byte)'q'), Step.WaitFor(prompt), Step.Pause(800)]),

            new("interrupt", "Stop a command",
                "The user runs a command that does not stop, then presses Ctrl+C. Ctrl+C is one byte, 0x03. The PTY converts it to an interrupt for the running command, and the shell continues to run.",
                "The PTY line discipline sends SIGINT to the foreground job. ping prints its statistics and exits. Then bash shows the prompt again.",
                "ConPTY raises CTRL_C_EVENT. ping prints its statistics and exits. Then PowerShell shows the prompt again.",
                [Step.WaitFor(prompt), Step.Pause(500), Step.Type(windows ? "ping -t 127.0.0.1" : "ping 127.0.0.1"),
                 Step.WaitFor("ttl="), Step.WaitFor("ttl="), Step.WaitFor("ttl="), Step.Pause(300),
                 Step.Key("Ctrl+C", 0x03), Step.WaitFor(prompt), Step.Pause(1000)]),

            new("password", "Type a password",
                "A command asks for a password, and the user types it. The screen does not show the password. An app that logs the keys, or types for the user, needs to know when the user types a password.",
                "read -s disables echo but keeps line input on. So InputMode shows Echo off and line input on, which marks a password prompt. The bash prompt also has echo off, but it has line input off too, because the bash line editor draws each key itself.",
                "InputMode is null on Windows because ConPTY cannot report the input mode. So the app cannot tell when the user types a password. PowerShell draws an asterisk for each key itself.",
                windows
                    ? [Step.WaitFor(prompt), Step.Pause(500), Step.Type("$pw = Read-Host 'Password' -AsSecureString"), Step.WaitFor("Password:"), Step.Pause(600),
                       Step.TypeSecret("opensesame"), Step.WaitFor(prompt), Step.Pause(500), Step.Type("$pw.Length"), Step.WaitFor(prompt), Step.Pause(900)]
                    : [Step.WaitFor(prompt), Step.Pause(500), Step.Type("read -s -p 'Password: ' pw"), Step.WaitForNoEcho(), Step.Pause(600),
                       Step.TypeSecret("opensesame"), Step.WaitFor(prompt), Step.Pause(500), Step.Type("echo ${#pw} characters"), Step.WaitFor(prompt), Step.Pause(900)]),

            new("flood", "Print a large file",
                "The user prints a 30,000-line file. MiniPty reads the output as fast as it arrives. Each chunk holds the output that was waiting when MiniPty read it, not one line or one write.",
                "cat writes the file in large blocks. The PTY processes each line and delivers the output in pieces.",
                "cat -Raw writes the whole file in one call. ConPTY renders it and sends its own VT stream, one frame at a time.",
                [Step.WaitFor(prompt), Step.Pause(500), Step.Type(windows ? "cat numbers.txt -Raw" : "cat numbers.txt"), Step.WaitFor(prompt), Step.Pause(1000)]),

            new("close", "Close the tab",
                "The user starts a long command and closes the tab. DisposeAsync ends the shell and the programs that run in it. ExitStatus shows how the shell ended.",
                "bash receives SIGHUP, as when a terminal window closes. bash sends the hang-up to its jobs. Then the signal ends bash.",
                "Windows ends the shell. When the app disposes a session while its child runs, the session never reports the exit.",
                [Step.WaitFor(prompt), Step.Pause(500), Step.Type(windows ? "Start-Sleep 100" : "sleep 100"), Step.Pause(1500), Step.Close()]),
        ];
    }
}

/// <summary>
/// The shell a scene runs in: bash on Unix and Windows PowerShell on Windows, each with a fixed prompt, a
/// <c>fullscreen</c> command that draws a frame filling the terminal, and nothing from the user's own profile.
/// </summary>
sealed record Shell(bool IsWindows, string FileName, string[] Arguments, IReadOnlyDictionary<string, string?>? Environment, string PromptMarker, string Display)
{
    public static Shell ForThisOs(string home)
    {
        if (OperatingSystem.IsWindows())
        {
            var script = Encoding.Unicode.GetBytes(PowerShellSetup);
            return new(
                true,
                Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
                ["-NoLogo", "-NoProfile", "-NoExit", "-EncodedCommand", Convert.ToBase64String(script)],
                null,
                "project>",
                "Windows PowerShell 5.1, interactive, with a fixed prompt and a fullscreen command");
        }

        return new(
            false,
            "/bin/bash",
            ["--noprofile", "--rcfile", Path.Combine(home, ".bashrc"), "-i"],
            new Dictionary<string, string?>
            {
                ["HOME"] = home,
                ["LANG"] = OperatingSystem.IsMacOS() ? "en_US.UTF-8" : "C.UTF-8",
                ["BASH_SILENCE_DEPRECATION_WARNING"] = "1",
                ["PROMPT_COMMAND"] = null,
            },
            "project$",
            "bash, interactive, with a fixed prompt and a fullscreen command");
    }

    public const string BashSetup = """
        PS1='\[\e[1;32m\]demo\[\e[0m\]:\[\e[1;34m\]~/project\[\e[0m\]$ '
        HISTFILE="$HOME/.bash_history"
        if [ "$(uname)" = Darwin ]; then alias ls='ls -G'; else alias ls='ls --color=auto'; fi
        fullscreen() {
          printf '\e[?1049h\e[?25l'
          trap __fullscreen_draw WINCH
          __fullscreen_draw
          local key
          while :; do
            if read -rsn1 -t 1 key && [ "$key" = q ]; then break; fi
          done
          trap - WINCH
          printf '\e[?25h\e[?1049l'
        }
        __fullscreen_draw() {
          local size rows cols line i msg
          size=$(stty size); rows=${size% *}; cols=${size#* }
          line=''
          for ((i = 2; i < cols; i++)); do line+='─'; done
          printf '\e[2J\e[H\e[36m┌%s┐' "$line"
          for ((i = 2; i < rows; i++)); do printf '\e[%d;1H│\e[%d;%dH│' "$i" "$i" "$cols"; done
          printf '\e[%d;1H└%s┘\e[0m' "$rows" "$line"
          msg="$cols x $rows"
          printf '\e[%d;%dH\e[1;33m%s\e[0m' $((rows / 2)) $(((cols - ${#msg}) / 2 + 1)) "$msg"
          msg='Resize the window, or press q to quit'
          printf '\e[%d;%dH\e[2m%s\e[0m' $((rows / 2 + 2)) $(((cols - ${#msg}) / 2 + 1)) "$msg"
        }
        """;

    // PSReadLine would load the user's history, offer it as predictions on screen, and save the typed commands to it:
    // keep both the user's history out of the recording and the recording out of the user's history.
    const string PowerShellSetup = """
        if (Get-Module -ListAvailable -Name PSReadLine) {
          Set-PSReadLineOption -HistorySaveStyle SaveNothing
          if ((Get-Command Set-PSReadLineOption).Parameters.ContainsKey('PredictionSource')) { Set-PSReadLineOption -PredictionSource None }
          [Microsoft.PowerShell.PSConsoleReadLine]::ClearHistory()
        }
        function prompt { "$([char]27)[1;32mPS$([char]27)[0m $([char]27)[1;34mproject$([char]27)[0m> " }
        function Draw-Frame([int]$cols, [int]$rows) {
          $e = [char]27
          $line = [string]::new([char]0x2500, $cols - 2)
          $sb = [System.Text.StringBuilder]::new()
          [void]$sb.Append("$e[2J$e[H$e[36m" + [char]0x250C + $line + [char]0x2510)
          for ($i = 2; $i -lt $rows; $i++) { [void]$sb.Append("$e[$i;1H" + [char]0x2502 + "$e[$i;$($cols)H" + [char]0x2502) }
          [void]$sb.Append("$e[$rows;1H" + [char]0x2514 + $line + [char]0x2518 + "$e[0m")
          $msg = "$cols x $rows"
          [void]$sb.Append("$e[$([math]::Floor($rows / 2));$([math]::Floor(($cols - $msg.Length) / 2) + 1)H$e[1;33m$msg$e[0m")
          $msg = 'Resize the window, or press q to quit'
          [void]$sb.Append("$e[$([math]::Floor($rows / 2) + 2);$([math]::Floor(($cols - $msg.Length) / 2) + 1)H$e[2m$msg$e[0m")
          [Console]::Write($sb.ToString())
        }
        function fullscreen {
          $e = [char]27
          [Console]::Write("$e[?1049h$e[?25l")
          $last = $null
          try {
            while ($true) {
              $s = $Host.UI.RawUI.WindowSize
              if ($null -eq $last -or $s.Width -ne $last.Width -or $s.Height -ne $last.Height) { $last = $s; Draw-Frame $s.Width $s.Height }
              if ([Console]::KeyAvailable -and [Console]::ReadKey($true).KeyChar -eq 'q') { break }
              Start-Sleep -Milliseconds 50
            }
          } finally { [Console]::Write("$e[?25h$e[?1049l") }
        }
        """;
}

/// <summary>
/// A throwaway home for the scenes: a small project folder for <c>ls</c>, the bash setup, and nothing else, so the
/// recordings show no path or file of the machine. Deleted when the recorder ends.
/// </summary>
sealed class Demo : IDisposable
{
    Demo(string root, string project, Shell shell)
    {
        Root = root;
        Project = project;
        Shell = shell;
    }

    public string Root { get; }

    public string Project { get; }

    public Shell Shell { get; }

    public static Demo Create()
    {
        // PowerShell's ls prints the folder's full path, and the temp folder's path holds the user's name on Windows.
        var root = OperatingSystem.IsWindows()
            ? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData), "MiniPtyPlayground")
            : Path.Combine(Path.GetTempPath(), "minipty-playground-" + Guid.NewGuid().ToString("N")[..8]);
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(Path.Combine(project, "src"));
        Directory.CreateDirectory(Path.Combine(project, "docs"));
        File.WriteAllText(Path.Combine(project, "README.md"), "# Demo project\n");
        File.WriteAllText(Path.Combine(project, "notes.txt"), "MiniPty playground\n");
        File.WriteAllText(Path.Combine(project, "src", "Program.cs"), "Console.WriteLine(\"hello\");\n");
        var numbers = new StringBuilder();
        for (var n = 1; n <= 30000; n++)
            numbers.Append(n).Append('\n');
        File.WriteAllText(Path.Combine(project, "numbers.txt"), numbers.ToString());
        var build = Path.Combine(project, "build.sh");
        File.WriteAllText(build, "#!/bin/sh\necho build\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(build, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            File.WriteAllText(Path.Combine(root, ".bashrc"), Shell.BashSetup + "\n");
            File.WriteAllText(Path.Combine(root, ".hushlogin"), ""); // keeps Ubuntu's sudo hint out of the recording
        }

        return new Demo(root, project, Shell.ForThisOs(root));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A shell that is still ending can hold the folder for a moment; the temp folder is cleaned up later.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

static class Recorder
{
    public static async Task<EventLog> RecordAsync(Target target, Demo demo, Scene scene)
    {
        var shell = demo.Shell;
        var log = new EventLog();
        var session = Pty.Start(new PtyStartInfo
        {
            FileName = shell.FileName,
            Arguments = shell.Arguments,
            WorkingDirectory = demo.Project,
            Environment = shell.Environment,
            Size = new PtySize(80, 22),
            ConPty = target.ConPty,
        });
        log.Add("started");
        log.Mode(session.InputMode);

        // The exit watcher completes this on its own thread: recording the time there keeps it the moment of the exit.
        var exited = session.WaitForExitAsync();
        _ = exited.ContinueWith(
            static (task, state) =>
            {
                if (task.IsCompletedSuccessfully)
                    ((EventLog)state!).SetExit(task.Result, "WaitForExitAsync");
                else
                    _ = task.Exception; // a disposed session fails this wait; the close scene reads ExitStatus instead
            },
            log,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        var reader = ReadAsync(session, log);
        using var stopPolling = new CancellationTokenSource();
        var poller = PollInputModeAsync(session, log, stopPolling.Token);
        var keys = 0;
        try
        {
            foreach (var step in scene.Steps)
            {
                switch (step.Kind)
                {
                    case StepKind.WaitFor:
                        await log.WaitForTextAsync(step.Text!, TimeSpan.FromMilliseconds(step.Ms));
                        break;
                    case StepKind.WaitForNoEcho:
                        await log.WaitForEchoOffAsync(TimeSpan.FromMilliseconds(step.Ms));
                        break;
                    case StepKind.Pause:
                        await Task.Delay(step.Ms);
                        break;
                    case StepKind.Type:
                        log.Step(step);
                        foreach (var rune in step.Text!.EnumerateRunes())
                        {
                            await Task.Delay(KeyDelay(keys++));
                            var bytes = new byte[rune.Utf8SequenceLength];
                            rune.EncodeToUtf8(bytes);
                            log.Input(bytes, rune.Value == ' ' ? "Space" : rune.ToString());
                            await session.WriteInputAsync(bytes);
                        }

                        if (step.Enter)
                        {
                            await Task.Delay(KeyDelay(keys++) + 150);
                            log.Input([(byte)'\r'], "Enter");
                            await session.WriteInputAsync("\r");
                        }

                        break;
                    case StepKind.Key:
                        log.Step(step);
                        log.Input(step.Bytes!, step.KeyName!);
                        await session.WriteInputAsync(step.Bytes!);
                        break;
                    case StepKind.Resize:
                        log.Step(step);
                        log.Resize(step.Cols, step.Rows);
                        session.Resize(new PtySize(step.Cols, step.Rows));
                        break;
                    case StepKind.Close:
                        log.Step(step);
                        log.Add("dispose");
                        await session.DisposeAsync();
                        log.Add("disposed");

                        // Unix keeps watching after disposal and reports the hang-up or the kill; Windows never reports.
                        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 3;
                        while (session.ExitStatus is null && Stopwatch.GetTimestamp() < deadline)
                            await Task.Delay(1);
                        if (session.ExitStatus is { } status)
                            log.SetExit(status, "ExitStatus after DisposeAsync");
                        else
                            log.Add("note", label: "No exit status in the 3 s after DisposeAsync");
                        await Task.WhenAny(reader, Task.Delay(1000));
                        break;
                }
            }
        }
        finally
        {
            // What follows the scene's last step is cleanup, not part of the scene.
            log.Stop();
            stopPolling.Cancel();
            await session.DisposeAsync();
            await Task.WhenAny(reader, Task.Delay(3000));
            await poller;
        }

        return log;
    }

    // A person's typing: 45-100 ms between keys, the same pattern in every recording.
    static int KeyDelay(int key) => 45 + (key * 37 % 56);

    static async Task ReadAsync(PtySession session, EventLog log)
    {
        try
        {
            await foreach (var chunk in session.ReadOutputAsync())
            {
                log.Output(chunk.Data.Span); // copied at once: chunk memory is not ours after the next read
                log.Mode(session.InputMode);
            }

            log.Add("eof");
        }
        catch (ObjectDisposedException)
        {
            log.Add("eof", label: "DisposeAsync stopped the output");
        }
        catch (IOException e)
        {
            log.Add("eof", label: "ReadOutputAsync failed: " + e.Message);
        }
    }

    // A program can change the input mode without writing anything; reading it often catches that too.
    static async Task PollInputModeAsync(PtySession session, EventLog log, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                log.Mode(session.InputMode);
                await Task.Delay(10, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }
}

/// <summary>
/// The events of one scene, timed from just before <c>Pty.Start</c>. Output also feeds a decoded copy that the recorder
/// waits on; the viewer gets the raw bytes.
/// </summary>
sealed class EventLog
{
    readonly record struct Event(double Ms, string Type, string? Label = null, byte[]? Data = null, int Cols = 0, int Rows = 0, PtyInputMode? Mode = null, Step? Step = null);

    readonly long _origin = Stopwatch.GetTimestamp();
    readonly Lock _gate = new();
    readonly List<Event> _events = [];
    readonly StringBuilder _text = new();
    readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    bool _stopped;
    bool _hasMode;
    PtyInputMode? _mode;
    int _searchFrom;
    (double Ms, PtyExitStatus Status, string Source)? _exit;

    double Now => Stopwatch.GetElapsedTime(_origin).TotalMilliseconds;

    void Append(Event e)
    {
        if (!_stopped)
            _events.Add(e);
    }

    public void Add(string type, string? label = null)
    {
        var ms = Now;
        lock (_gate)
            Append(new(ms, type, label));
    }

    public void Step(Step step)
    {
        var ms = Now;
        lock (_gate)
            Append(new(ms, "step", Step: step));
    }

    public void Input(byte[] bytes, string label)
    {
        var ms = Now;
        lock (_gate)
            Append(new(ms, "input", label, bytes));
    }

    public void Resize(int cols, int rows)
    {
        var ms = Now;
        lock (_gate)
            Append(new(ms, "resize", Cols: cols, Rows: rows));
    }

    public void Output(ReadOnlySpan<byte> bytes)
    {
        var ms = Now;
        var copy = bytes.ToArray();
        lock (_gate)
        {
            Append(new(ms, "output", Data: copy));
            var chars = new char[_decoder.GetCharCount(copy, flush: false)];
            _decoder.GetChars(copy, chars, flush: false);
            _text.Append(chars);
        }
    }

    public void Mode(PtyInputMode? mode)
    {
        var ms = Now;
        lock (_gate)
        {
            if (_hasMode && _mode == mode)
                return;
            _hasMode = true;
            _mode = mode;
            Append(new(ms, "inputMode", Mode: mode));
        }
    }

    public void SetExit(PtyExitStatus status, string source)
    {
        var ms = Now;
        lock (_gate)
        {
            if (_exit is not null || _stopped)
                return;
            _exit = (ms, status, source);
            Append(new(ms, "exit", source));
        }
    }

    /// <summary>Ends the scene: later events are cleanup and are not written.</summary>
    public void Stop()
    {
        lock (_gate)
            _stopped = true;
    }

    /// <summary>
    /// Waits until output that arrived after the previous wait's match contains <paramref name="marker"/>, compared
    /// without case on the text with control sequences removed.
    /// </summary>
    public async Task WaitForTextAsync(string marker, TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            string text;
            int from;
            lock (_gate)
            {
                text = _text.ToString();
                from = _searchFrom;
            }

            var plain = PtyOutput.ToDisplayText(text, PtyOutputDisplayMode.PlainText);
            var index = plain.IndexOf(marker, Math.Min(from, plain.Length), StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                lock (_gate)
                    _searchFrom = index + marker.Length;
                return;
            }

            await Task.Delay(5);
        }

        throw new TimeoutException($"The shell did not print \"{marker}\" within {timeout.TotalSeconds:0} s.");
    }

    public async Task WaitForEchoOffAsync(TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            lock (_gate)
            {
                if (_mode is { Echo: false })
                    return;
            }

            await Task.Delay(5);
        }

        throw new TimeoutException($"The terminal's echo did not turn off within {timeout.TotalSeconds:0} s.");
    }

    public string Summary()
    {
        lock (_gate)
        {
            int chunks = 0, inputs = 0;
            long bytes = 0;
            foreach (var e in _events)
            {
                if (e.Type == "output")
                {
                    chunks++;
                    bytes += e.Data!.Length;
                }
                else if (e.Type == "input")
                {
                    inputs++;
                }
            }

            var exit = _exit is { } x ? $", exit {x.Status.ExitCode} signal {x.Status.Signal?.ToString() ?? "none"}" : "";
            return $"{inputs} inputs, {chunks} chunks, {bytes} bytes{exit}";
        }
    }

    public void Write(Utf8JsonWriter w, Scene scene, Target target, Shell shell)
    {
        lock (_gate)
        {
            w.WriteStartObject();
            w.WriteString("format", PlaygroundIndex.Format);
            w.WriteString("scene", scene.Id);
            w.WriteString("environment", target.Id);
            w.WriteString("shell", shell.Display);
            w.WriteStartObject("size");
            w.WriteNumber("cols", 80);
            w.WriteNumber("rows", 22);
            w.WriteEndObject();

            if (_exit is { } exit)
            {
                w.WriteStartObject("exit");
                w.WriteNumber("t", Round(exit.Ms));
                w.WriteNumber("exitCode", exit.Status.ExitCode);
                if (exit.Status.Signal is { } signal)
                    w.WriteNumber("signal", signal);
                else
                    w.WriteNull("signal");
                w.WriteNumber("nodePtyExitCode", exit.Status.NodePtyExitCode);
                w.WriteString("source", exit.Source);
                w.WriteEndObject();
            }
            else
            {
                w.WriteNull("exit");
            }

            w.WriteStartArray("events");
            foreach (var e in _events)
            {
                w.WriteStartObject();
                w.WriteNumber("t", Round(e.Ms));
                w.WriteString("type", e.Type);
                if (e.Label is not null)
                    w.WriteString("label", e.Label);
                if (e.Data is not null)
                    w.WriteBase64String("data", e.Data);
                switch (e.Type)
                {
                    case "resize":
                        w.WriteNumber("cols", e.Cols);
                        w.WriteNumber("rows", e.Rows);
                        break;
                    case "inputMode" when e.Mode is { } mode:
                        w.WriteBoolean("echo", mode.Echo);
                        w.WriteBoolean("canonical", mode.Canonical);
                        break;
                    case "inputMode":
                        w.WriteBoolean("unavailable", true);
                        break;
                    case "step" when e.Step is { } step:
                        w.WriteString("action", step.Kind.ToString().ToLowerInvariant());
                        if (step.Kind == StepKind.Type)
                        {
                            w.WriteString("text", step.Secret ? new string('•', step.Text!.Length) : step.Text);
                            w.WriteBoolean("enter", step.Enter);
                        }

                        if (step.Secret)
                            w.WriteBoolean("secret", true);
                        if (step.KeyName is not null)
                            w.WriteString("key", step.KeyName);
                        if (step.Kind == StepKind.Resize)
                        {
                            w.WriteNumber("cols", step.Cols);
                            w.WriteNumber("rows", step.Rows);
                        }

                        break;
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }
    }

    static double Round(double ms) => Math.Round(ms, 3);
}

/// <summary>
/// Where a run happens: the machine and, on Windows, which ConPTY. CI passes <c>--env-id</c> and <c>--env-label</c>
/// with the runner's name; the defaults describe this machine.
/// </summary>
sealed record Target(string Id, string Label, string? ConPtyKind, ConPtyLibrary? ConPty)
{
    public static List<Target> Resolve(string? idPrefix, string? label, string conPtyChoice)
    {
        var os = OsName();
        var arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        idPrefix ??= $"{os.ToLowerInvariant()}-{arch}";
        label ??= $"{os} {arch}";

        if (!OperatingSystem.IsWindows())
            return [new(idPrefix, label, null, null)];

        if (conPtyChoice is not ("inbox" or "oob" or "both"))
            throw new ArgumentException($"--conpty must be inbox, oob, or both, not \"{conPtyChoice}\".");

        var targets = new List<Target>();
        if (conPtyChoice is "inbox" or "both")
            targets.Add(new($"{idPrefix}-inbox", $"{label} · in-box ConPTY", "in-box", null));
        if (conPtyChoice is "oob" or "both")
        {
            var library = ConPtyLibrary.TryLoadFromApplicationDirectory();
            if (library is { HasConsoleHost: true })
                targets.Add(new($"{idPrefix}-oob", $"{label} · out-of-band ConPTY", "out-of-band", library));
            else if (conPtyChoice == "oob")
                throw new ArgumentException("The out-of-band ConPTY (conpty.dll and OpenConsole.exe) is not beside this program.");
            else
                Console.Error.WriteLine("Skipping the out-of-band ConPTY: conpty.dll or OpenConsole.exe is not beside this program.");
        }

        return targets;
    }

    static string OsName() =>
        OperatingSystem.IsWindows() ? "Windows"
        : OperatingSystem.IsLinux() ? "Linux"
        : OperatingSystem.IsMacOS() ? "macOS"
        : OperatingSystem.IsFreeBSD() ? "FreeBSD"
        : "Unknown";

    public void Write(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("id", Id);
        w.WriteString("label", Label);
        w.WriteString("os", OsName());
        w.WriteString("osDescription", RuntimeInformation.OSDescription);
        w.WriteString("architecture", RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant());
        w.WriteString("framework", RuntimeInformation.FrameworkDescription);
        w.WriteString("miniPty", MiniPtyVersion());
        if (ConPtyKind is null)
        {
            w.WriteNull("conPty");
        }
        else
        {
            w.WriteStartObject("conPty");
            w.WriteString("kind", ConPtyKind);
            w.WriteString("version", ConPty?.FileVersion.ToString() ?? System.Environment.OSVersion.Version.ToString());
            w.WriteEndObject();
        }

        w.WriteString("recordedAt", DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        var server = System.Environment.GetEnvironmentVariable("GITHUB_SERVER_URL");
        var repository = System.Environment.GetEnvironmentVariable("GITHUB_REPOSITORY");
        var runId = System.Environment.GetEnvironmentVariable("GITHUB_RUN_ID");
        if (server is not null && repository is not null && runId is not null)
            w.WriteString("run", $"{server}/{repository}/actions/runs/{runId}");
        else
            w.WriteNull("run");
        w.WriteEndObject();
    }

    // "1.7.0+<commit>": keep a short commit so a page can name the source it was recorded from.
    static string MiniPtyVersion()
    {
        var version = typeof(Pty).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var plus = version.IndexOf('+');
        return plus >= 0 && version.Length > plus + 8 ? version[..(plus + 8)] : version;
    }
}

/// <summary>Builds <c>index.json</c>: the scenes and every environment found on disk.</summary>
static class PlaygroundIndex
{
    public const string Format = "minipty-playground/2";

    public static void Write(string outDir, Scene[] scenes)
    {
        var environments = new List<(int Order, string Id, string Dir, JsonDocument Doc)>();
        foreach (var dir in Directory.GetDirectories(outDir))
        {
            var file = Path.Combine(dir, "environment.json");
            if (!File.Exists(file))
                continue;
            var doc = JsonDocument.Parse(File.ReadAllBytes(file));
            var root = doc.RootElement;
            environments.Add((Order(root), root.GetProperty("id").GetString() ?? "", dir, doc));
        }

        environments.Sort(static (a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : string.CompareOrdinal(a.Id, b.Id));

        Json.WriteFile(Path.Combine(outDir, "index.json"), w =>
        {
            w.WriteStartObject();
            w.WriteString("format", Format);
            w.WriteStartArray("scenes");
            foreach (var scene in scenes)
            {
                w.WriteStartObject();
                w.WriteString("id", scene.Id);
                w.WriteString("title", scene.Title);
                w.WriteString("summary", scene.Summary);
                w.WriteString("unixNotice", scene.UnixNotice);
                w.WriteString("windowsNotice", scene.WindowsNotice);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteStartArray("environments");
            foreach (var (_, _, dir, doc) in environments)
            {
                w.WriteStartObject();
                foreach (var property in doc.RootElement.EnumerateObject())
                    property.WriteTo(w);

                // Which scenes exist, so the viewer never asks for a file that is not there.
                w.WriteStartArray("scenes");
                foreach (var scene in scenes)
                {
                    if (File.Exists(Path.Combine(dir, scene.Id + ".json")))
                        w.WriteStringValue(scene.Id);
                }

                w.WriteEndArray();
                w.WriteEndObject();
                doc.Dispose();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }, indented: true);
    }

    static int Order(JsonElement environment) => environment.GetProperty("os").GetString() switch
    {
        "Linux" => 0,
        "macOS" => 1,
        "Windows" => 2,
        _ => 3,
    };
}

static class Json
{
    public static void WriteFile(string path, Action<Utf8JsonWriter> write) => WriteFile(path, write, indented: false);

    public static void WriteFile(string path, Action<Utf8JsonWriter> write, bool indented)
    {
        using var stream = File.Create(path);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = indented });
        write(writer);
    }
}
