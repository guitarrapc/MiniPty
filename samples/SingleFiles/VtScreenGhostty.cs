#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property PublishAot=true
#:project ../../src/MiniPty/MiniPty.csproj
#:package DeBlasis.GhosttyVt@1.4.1

// VtScreenGhostty: keep a VT engine beside a session, so the host knows what the screen shows.
//
//   dotnet samples/SingleFiles/VtScreenGhostty.cs                                      # a demo that redraws its lines
//   dotnet samples/SingleFiles/VtScreenGhostty.cs -- [--after <seconds>] <program> [args...]   # e.g. -- top
//
// The "--" keeps dotnet from reading the program's options, such as "-c", as its own.
//
// MiniPty carries bytes, and a terminal draws them. A host that needs the screen itself (an agent that reads a TUI, a
// log that keeps only the last state of a progress line, a server that redraws a reloaded page) gives the same bytes
// to a terminal emulator without a window. This sample uses Ghostty's VT engine (libghostty-vt) through the
// DeBlasis.GhosttyVt package, which bundles it for win-x64, linux-x64, and osx-arm64. VtScreenTerminalEmulation.cs
// does the same with a VT engine written in C#.
//
// The demo prints a progress line that it redraws with '\r', and a status line that it replaces with cursor movement.
// With a program, the sample takes the screen after --after seconds (default 2) or when the program exits, whichever
// comes first, then ends the program.

using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Ghostty.Vt;
using Ghostty.Vt.Enums;
using MiniPty;

const int Columns = 80;
const int Rows = 24;

if (!Pty.IsSupported)
{
    Console.Error.WriteLine("PTY is not supported on this operating system.");
    return 1;
}

var (startInfo, snapshotAfter) = ParseArguments(args);
await using var session = Pty.Start(startInfo);

// A program can ask its terminal a question, such as the cursor position (ESC[6n) or the terminal type (ESC[c), and
// wait for the answer. Here the engine is the only terminal, so its answers go back to the program. When a frontend
// such as xterm.js is attached too, the frontend answers: drop these replies then, or the program reads two answers.
var replies = new ArrayBufferWriter<byte>();
using var terminal = new Terminal(Columns, Rows, options => options.OnWritePty = reply => replies.Write(reply));

var bytesAsText = new StringBuilder();
var decoder = Encoding.UTF8.GetDecoder();
var exited = true;

using (var deadline = new CancellationTokenSource(snapshotAfter))
{
    try
    {
        await foreach (var chunk in session.ReadOutputAsync(deadline.Token))
        {
            // chunk.Data is valid only until the next iteration, so both consumers take it now.
            terminal.VTWrite(chunk.Data.Span);
            AppendText(bytesAsText, decoder, chunk.Data.Span);

            if (replies.WrittenCount > 0)
            {
                await session.WriteInputAsync(replies.WrittenMemory, deadline.Token);
                replies.ResetWrittenCount();
            }
        }
    }
    catch (OperationCanceledException) when (deadline.IsCancellationRequested)
    {
        exited = false; // The program still runs. The screen below is the screen at this moment.
    }
}

Console.WriteLine($"== Output as text: {nameof(PtyOutput.ToDisplayText)} keeps every redraw ==");
Console.WriteLine(PtyOutput.ToDisplayText(bytesAsText.ToString(), PtyOutputDisplayMode.PlainText).TrimEnd());
Console.WriteLine();

// The text holds the scrollback and the screen below it, as a terminal shows them scrolled to the top. A full-screen
// program draws on the alternate screen, which has no scrollback.
var screen = FormatScreen(terminal, FormatterFormat.PlainText);
Console.WriteLine($"== Screen and scrollback: what a terminal shows {(exited ? "after the exit" : $"after {snapshotAfter.TotalSeconds:0.#} s")} ==");
Console.WriteLine(screen.TrimEnd());
Console.WriteLine();

// A page that connects later needs the screen, not all the bytes since the start. The VT format writes the screen as
// escape sequences, so the page draws the same screen from a few bytes. A second engine stands in for the page here.
var redraw = Encoding.UTF8.GetBytes(FormatScreen(terminal, FormatterFormat.Vt));
using var page = new Terminal(Columns, Rows, configure: null);
page.VTWrite(redraw);
var same = FormatScreen(page, FormatterFormat.PlainText) == screen;
Console.WriteLine($"== Redraw: {redraw.Length} bytes of VT draw this screen in a new terminal (same text: {same}) ==");
return same ? 0 : 1;

static string FormatScreen(Terminal terminal, FormatterFormat format)
{
    // Trim drops the blank cells at the end of each line and the blank lines below the last text.
    using var formatter = terminal.CreateFormatter(format, options =>
    {
        options.Trim = true;
        options.IncludeStyle = format == FormatterFormat.Vt; // colors and bold, for the redraw only
    });
    return formatter.ToString();
}

static void AppendText(StringBuilder text, Decoder decoder, ReadOnlySpan<byte> bytes)
{
    // A chunk can end inside a UTF-8 character. The decoder keeps those bytes until the next chunk.
    var chars = ArrayPool<char>.Shared.Rent(decoder.GetCharCount(bytes, flush: false));
    var count = decoder.GetChars(bytes, chars, flush: false);
    text.Append(chars, 0, count);
    ArrayPool<char>.Shared.Return(chars);
}

static (PtyStartInfo StartInfo, TimeSpan SnapshotAfter) ParseArguments(string[] args)
{
    var after = TimeSpan.FromSeconds(2);
    var rest = args.AsSpan();
    if (rest.Length >= 2 && rest[0] == "--after")
    {
        after = TimeSpan.FromSeconds(double.Parse(rest[1], CultureInfo.InvariantCulture));
        rest = rest[2..];
    }

    // The demo ends by itself. The limit only stops a demo that hangs.
    if (rest.IsEmpty)
        return (CreateDemoStartInfo(), TimeSpan.FromSeconds(30));

    return (new PtyStartInfo
    {
        FileName = rest[0],
        Arguments = rest[1..].ToArray(),
        Size = new PtySize(Columns, Rows),
        KillProcessTree = true, // Windows: also end what the program started, when the session ends
    }, after);
}

/// <summary>
/// A program that redraws a progress line with '\r', then prints a status line and replaces it with cursor movement
/// (cursor up, erase the line). A terminal shows only the last state of each line.
/// </summary>
static PtyStartInfo CreateDemoStartInfo()
{
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        return new PtyStartInfo
        {
            FileName = powershell,
            Arguments =
            [
                "-NoLogo",
                "-NoProfile",
                "-Command",
                "$e = [char]27; [Console]::Out.Write(\"build started`n\"); " +
                "foreach ($i in 20, 40, 60, 80, 100) { [Console]::Out.Write(\"`rprogress $i%\"); Start-Sleep -Milliseconds 100 }; " +
                "[Console]::Out.Write(\"`nstatus: running`n\"); Start-Sleep -Milliseconds 100; " +
                "[Console]::Out.Write(\"$e[1A$e[2Kstatus: done`n\")",
            ],
            Size = new PtySize(Columns, Rows),
        };
    }

    return new PtyStartInfo
    {
        FileName = "/bin/sh",
        Arguments =
        [
            "-c",
            "printf 'build started\\n'; " +
            "for i in 20 40 60 80 100; do printf '\\rprogress %s%%' \"$i\"; sleep 0.1; done; " +
            "printf '\\nstatus: running\\n'; sleep 0.1; " +
            "printf '\\033[1A\\033[2Kstatus: done\\n'",
        ],
        Size = new PtySize(Columns, Rows),
    };
}
