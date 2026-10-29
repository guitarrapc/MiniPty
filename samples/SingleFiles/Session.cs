#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property AllowUnsafeBlocks=true
#:property PublishAot=true
#:project ../../src/MiniPty/MiniPty.csproj

// Session: run commands in a PTY for their result.
//
//   dotnet samples/SingleFiles/Session.cs
//
// Three uses of one PtySession, each printing the child's output raw (escaped) and as plain text:
//   1. Read the Output stream yourself, write stdin, and end it with SendEof.
//   2. CompleteAsync: read all output and wait for the exit in one call.
//   3. Resize a running session.

using System.Runtime.InteropServices;
using System.Text;
using MiniPty;

// A terminal sends CR for the Enter key, and a PTY treats it as Enter on every platform: ConPTY on Windows, and on Unix
// the line discipline, which MiniPty configures to turn CR into a newline (ICRNL). So one string works everywhere.
const string Enter = "\r";

if (!Pty.IsSupported)
{
    Console.Error.WriteLine("PTY is not supported on this operating system.");
    return 1;
}

try
{
    await RunStdinPipelineAsync();
    await RunEchoWithCompleteAsync();
    await RunResizeAsync();
    Console.Error.WriteLine("ok Session sample: Output stream with SendEof, CompleteAsync, Resize");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
    return 1;
}

static async Task RunStdinPipelineAsync()
{
    Console.Error.WriteLine("=== Output stream, stdin, and SendEof ===");

    await using var session = Pty.Start(CreateStdinPipelineStartInfo());
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

    // Start reading before writing, and keep reading until the end: a PTY whose output nobody reads fills up and blocks
    // the child. The first read makes this stream the session's only output consumer.
    using var output = new MemoryStream();
    var pump = session.Output.CopyToAsync(output, cts.Token);

    // Three lines, then end of input: Ctrl+D on Unix, Ctrl+Z and Enter on Windows. Writes arrive in order, and SendEof
    // takes effect after them.
    await session.WriteInputAsync($"line 3{Enter}line 1{Enter}line 2{Enter}", cancellationToken: cts.Token);
    session.SendEof();

    var status = await session.WaitForExitAsync(cts.Token);
    await pump; // End of output follows the exit, once the child's last output has been read.

    Console.Error.WriteLine($"pid={session.ProcessId} size={session.Size.Columns}x{session.Size.Rows} exit={status.ExitCode}");
    // The output also holds the three input lines: like any terminal, the PTY echoes what is typed.
    var text = Decode(output);
    PrintOutput(text);
    ValidateStdinPipelineOutput(text, status.ExitCode);
}

static async Task RunEchoWithCompleteAsync()
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("=== One-shot completion (CompleteAsync) ===");

    await using var session = Pty.Start(CreateEchoStartInfo());

    // CompleteAsync is the session's output consumer for the whole run: it collects every byte, waits for the exit,
    // and returns both. ExitTimeout makes it throw TimeoutException if the command runs longer.
    var result = await session.CompleteAsync(new PtyCompleteOptions
    {
        ExitTimeout = TimeSpan.FromSeconds(15),
    });

    Console.Error.WriteLine($"exit={result.ExitCode}");
    PrintOutput(result.GetTextString());

    if (result.ExitCode != 0)
        throw new InvalidOperationException($"echo exited with {result.ExitCode}");

    if (!result.Contains("minipty-session-sample"))
        throw new InvalidOperationException("expected marker missing from output");
}

static async Task RunResizeAsync()
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("=== Terminal resize ===");

    await using var session = Pty.Start(CreateResizeStartInfo());
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    using var output = new MemoryStream();
    var pump = session.Output.CopyToAsync(output, cts.Token);

    // Resize works at any time while the session is open, and never fails because the child has exited.
    // On Unix the child gets SIGWINCH, and `stty size` below reports the new size.
    session.Resize(new PtySize(100, 28));
    Console.Error.WriteLine($"parent reports {session.Size.Columns}x{session.Size.Rows}");

    // The Unix child waits for one line before it prints its size.
    await session.WriteInputAsync(Enter, cancellationToken: cts.Token);
    session.SendEof();

    var status = await session.WaitForExitAsync(cts.Token);
    await pump;

    Console.Error.WriteLine($"exit={status.ExitCode}");
    var text = Decode(output);
    PrintOutput(text);

    if (status.ExitCode != 0)
        throw new InvalidOperationException($"resize probe exited with {status.ExitCode}");

    // The Windows child only echoes; a Windows console program sees the new size through its console.
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        return;

    if (!text.Contains("100", StringComparison.Ordinal) || !text.Contains("28", StringComparison.Ordinal))
        throw new InvalidOperationException("child did not report resized dimensions");
}

/// <summary>
/// Decodes the collected bytes once, after the last read. Decoding each read on its own could split a UTF-8 character
/// that spans two reads.
/// </summary>
static string Decode(MemoryStream output) => Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);

static void PrintOutput(string text)
{
    Console.Error.WriteLine("child output (raw, escaped):");
    Console.Error.WriteLine(EscapeForDisplay(text.TrimEnd()));
    Console.Error.WriteLine("child output (plain):");
    // PTY output is terminal output: colors, cursor moves, and CRLF line ends. ToDisplayText strips the sequences for
    // logs and assertions; a terminal frontend needs the raw bytes instead.
    Console.Error.WriteLine(PtyOutput.ToDisplayText(text, PtyOutputDisplayMode.PlainText).TrimEnd());
}

static string EscapeForDisplay(string text)
{
    var builder = new StringBuilder(text.Length);
    foreach (var ch in text)
    {
        builder.Append(ch switch
        {
            '\r' => "\\r",
            '\n' => "\\n",
            '\t' => "\\t",
            < ' ' or > '~' => $"\\u{(int)ch:X4}",
            _ => ch.ToString(),
        });
    }

    return builder.ToString();
}

static void ValidateStdinPipelineOutput(string output, int exitCode)
{
    if (exitCode != 0)
        throw new InvalidOperationException($"stdin pipeline exited with {exitCode}");

    if (!output.Contains("minipty-stdin-pipeline", StringComparison.Ordinal))
        throw new InvalidOperationException("expected pipeline marker missing from output");

    // The echo shows the input as typed (3, 1, 2); the sorted lines (1, 2, 3) follow it once sort has read to the end.
    var sorted = output.LastIndexOf("line 1", StringComparison.Ordinal);
    var two = sorted < 0 ? -1 : output.IndexOf("line 2", sorted, StringComparison.Ordinal);
    if (two < 0 || output.IndexOf("line 3", two, StringComparison.Ordinal) < 0)
        throw new InvalidOperationException("sorted input missing from output: stdin did not reach the child");
}

/// <summary>A child that sorts its stdin, which ends only at end of input, then prints a marker.</summary>
static PtyStartInfo CreateStdinPipelineStartInfo()
{
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
        return new PtyStartInfo
        {
            FileName = cmd,
            // What follows /c is a command line for cmd.exe to parse: never put user input there. sort.exe is named by
            // its full path because a bare `sort` can find another program first on PATH, such as Git's GNU tools.
            Arguments = ["/c", @"%SystemRoot%\System32\sort.exe & echo minipty-stdin-pipeline"],
            Size = new PtySize(80, 24),
        };
    }

    return new PtyStartInfo
    {
        FileName = "/bin/sh",
        Arguments = ["-c", "sort; printf 'minipty-stdin-pipeline\\n'"],
        Size = new PtySize(80, 24),
    };
}

static PtyStartInfo CreateEchoStartInfo()
{
    var startInfo = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? new PtyStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe",
            Arguments = ["/c", "echo minipty-session-sample"],
        }
        : new PtyStartInfo
        {
            FileName = "/bin/sh",
            Arguments = ["-c", "printf 'minipty-session-sample\\n'"],
        };

    return startInfo with
    {
        WorkingDirectory = Environment.CurrentDirectory,
        Size = new PtySize(100, 30),
        // Windows: when the session ends, end everything the command started too, even programs in a console of their
        // own. Use it for commands run for their result (build steps, test runners, tool calls); leave it off for
        // interactive terminals, where a window the user opened should outlive the tab. Ignored on Unix.
        KillProcessTree = true,
    };
}

/// <summary>A child that waits for one line, then prints its terminal size as "columns rows".</summary>
static PtyStartInfo CreateResizeStartInfo()
{
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
        return new PtyStartInfo
        {
            FileName = cmd,
            Arguments = ["/c", "echo resized"],
            Size = new PtySize(80, 24),
        };
    }

    return new PtyStartInfo
    {
        FileName = "/bin/sh",
        Arguments = ["-c", "read _; set -- $(stty size); echo $2 $1"],
        Size = new PtySize(80, 24),
    };
}
