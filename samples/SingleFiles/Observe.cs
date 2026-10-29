#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property AllowUnsafeBlocks=true
#:property PublishAot=true
#:project ../../src/MiniPty/MiniPty.csproj
#:project ../../src/MiniPty.Capture/MiniPty.Capture.csproj

// Observe: record what a command prints and when, with MiniPty.Capture.
//
//   dotnet samples/SingleFiles/Observe.cs
//
// PtyCapture.RunAsync starts the child, writes its input, waits for the exit, and returns every read with its time since
// Pty.Start. The first part prints a timeline of output that arrives in steps; the second feeds stdin and shows where
// each read begins and ends.

using System.Runtime.InteropServices;
using System.Text;
using MiniPty;
using MiniPty.Capture;

// The Enter key: a PTY treats CR as Enter on every platform (see Session.cs).
const string Enter = "\r";

if (!Pty.IsSupported)
{
    Console.Error.WriteLine("PTY is not supported on this operating system.");
    return 1;
}

try
{
    await ObserveStaggeredOutputAsync();
    await ObserveStdinPipelineAsync();
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
    return 1;
}

static async Task ObserveStaggeredOutputAsync()
{
    Console.WriteLine("=== Observe output over time ===");

    // Completion takes the same options as PtySession.CompleteAsync. ExitTimeout makes the capture throw
    // TimeoutException if the command runs longer.
    var result = await PtyCapture.RunAsync(
        CreateStaggeredStartInfo(),
        new PtyCaptureOptions
        {
            Completion = new PtyCompleteOptions
            {
                ExitTimeout = TimeSpan.FromSeconds(30),
            },
        });

    var text = result.GetText();
    Console.WriteLine($"exit={result.ExitCode} chunks={result.Chunks.Count} bytes={result.Output.Length} chars={text.Length}");
    Console.WriteLine("timeline (elapsed since session start):");

    // One entry per read from the PTY, not per write by the child: the OS can join or split writes. GetTextChunks gives
    // the same reads as text, with each read's time.
    var textChunks = result.GetTextChunks();
    for (var i = 0; i < textChunks.Count; i++)
    {
        var chunk = textChunks[i];
        var preview = EscapeForDisplay(chunk.Text.Span);
        Console.WriteLine($"  +{chunk.Time.TotalSeconds,7:F3}s  {chunk.Text.Length,4} chars  {preview}");
    }

    if (result.Chunks.Count > 0)
    {
        var last = result.Chunks[^1];
        Console.WriteLine($"session span: {last.Time.TotalSeconds:F3}s");
    }

    // result.Output holds every byte; result.Chunks splits the same bytes into reads, in order and with no gaps.
    var mergedOffset = 0;
    foreach (var chunk in result.Chunks)
    {
        var slice = result.Output.Span.Slice(mergedOffset, chunk.Data.Length);
        if (!chunk.Data.Span.SequenceEqual(slice))
            throw new InvalidOperationException("merged byte chunks do not match result.Output");

        mergedOffset += chunk.Data.Length;
    }

    if (mergedOffset != result.Output.Length)
        throw new InvalidOperationException("merged byte chunks do not cover result.Output");

    if (result.ExitCode != 0)
        throw new InvalidOperationException($"child exited with {result.ExitCode}");

    if (result.Chunks.Count < 2)
        throw new InvalidOperationException("expected multiple chunks from staggered output");

    foreach (var label in new[] { "alpha", "beta", "gamma" })
    {
        if (!result.ContainsUtf8(label))
            throw new InvalidOperationException($"expected label '{label}' missing from output");
    }
}

static async Task ObserveStdinPipelineAsync()
{
    Console.WriteLine();
    Console.WriteLine("=== Observe stdin + PTY pipeline ===");

    var result = await PtyCapture.RunAsync(
        CreateStdinPipelineStartInfo(),
        new PtyCaptureOptions
        {
            Completion = new PtyCompleteOptions
            {
                // Written as typed input once the child starts, then followed by end of input (SendEofAfterInput
                // defaults to true), so the child's read of stdin ends.
                Input = $"line 3{Enter}line 1{Enter}line 2{Enter}",
                ExitTimeout = TimeSpan.FromSeconds(15),
            },
        });

    Console.WriteLine($"exit={result.ExitCode}");
    // Plain text: written raw, the PTY's terminal sequences (such as ConPTY's screen clear) would act on this console.
    Console.WriteLine("merged output (plain):");
    Console.WriteLine(result.ToDisplayText(PtyOutputDisplayMode.PlainText).TrimEnd());

    Console.WriteLine("chunk boundaries (useful when rebuilding a consumer timeline):");
    var cursor = TimeSpan.Zero;
    foreach (var chunk in result.Chunks)
    {
        Console.WriteLine($"  [{cursor.TotalSeconds:F3}s -> {chunk.Time.TotalSeconds:F3}s)  {chunk.Data.Length} bytes");
        cursor = chunk.Time;
    }

    ValidateStdinPipelineOutput(result.GetTextString(), result.ExitCode);
}

static void ValidateStdinPipelineOutput(string output, int exitCode)
{
    if (exitCode != 0)
        throw new InvalidOperationException($"stdin pipeline exited with {exitCode}");

    if (!output.Contains("minipty-stdin-pipeline", StringComparison.Ordinal))
        throw new InvalidOperationException("expected pipeline marker missing from output");
}

static string EscapeForDisplay(ReadOnlySpan<char> text)
{
    var builder = new StringBuilder(text.Length);
    foreach (var ch in text)
    {
        builder.Append(ch switch
        {
            '\r' => "\\r",
            '\n' => "\\n\n       ",
            '\t' => "\\t",
            < ' ' or > '~' => $"\\u{(int)ch:X4}",
            _ => ch.ToString(),
        });
    }

    return builder.ToString().TrimEnd();
}

static PtyStartInfo CreateStaggeredStartInfo()
{
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
        return new PtyStartInfo
        {
            FileName = cmd,
            Arguments =
            [
                "/c",
                // Full path: a bare `timeout` can find another program first on PATH, such as Git's GNU tools.
                @"echo alpha& %SystemRoot%\System32\timeout.exe /t 1 /nobreak >nul & echo beta& %SystemRoot%\System32\timeout.exe /t 1 /nobreak >nul & echo gamma",
            ],
            Size = new PtySize(80, 24),
        };
    }

    return new PtyStartInfo
    {
        FileName = "/bin/sh",
        Arguments =
        [
            "-c",
            "printf 'alpha\\n'; sleep 0.15; printf 'beta\\n'; sleep 0.15; printf 'gamma\\n'",
        ],
        Size = new PtySize(80, 24),
    };
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
            // Full path: a bare `sort` can find another program first on PATH, such as Git's GNU tools.
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
