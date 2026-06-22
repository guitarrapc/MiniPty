#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property AllowUnsafeBlocks=true
#:property PublishAot=true
#:project ../src/MiniPty/MiniPty.csproj
#:project ../src/MiniPty.Capture/MiniPty.Capture.csproj

using System.Runtime.InteropServices;
using System.Text;
using MiniPty;
using MiniPty.Capture;

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

    var result = await PtyCapture.RunAsync(
        CreateStaggeredStartInfo(),
        new PtyCaptureOptions
        {
            Completion = new PtyCompleteOptions
            {
                ExitTimeout = TimeSpan.FromSeconds(30),
            },
        });

    Console.WriteLine($"exit={result.ExitCode} chunks={result.Chunks.Count} chars={result.Output.Length}");
    Console.WriteLine("timeline (elapsed since session start):");

    foreach (var chunk in result.Chunks)
    {
        var preview = EscapeForDisplay(chunk.Data);
        Console.WriteLine($"  +{chunk.Time.TotalSeconds,7:F3}s  {chunk.Data.Length,4} chars  {preview}");
    }

    if (result.Chunks.Count > 0)
    {
        var last = result.Chunks[^1];
        Console.WriteLine($"session span: {last.Time.TotalSeconds:F3}s");
    }

    var merged = string.Concat(result.Chunks.Select(static chunk => chunk.Data));
    if (!string.Equals(merged, result.Output, StringComparison.Ordinal))
        throw new InvalidOperationException("merged chunks do not match result.Output");

    if (result.ExitCode != 0)
        throw new InvalidOperationException($"child exited with {result.ExitCode}");

    if (result.Chunks.Count < 2)
        throw new InvalidOperationException("expected multiple chunks from staggered output");

    foreach (var label in new[] { "alpha", "beta", "gamma" })
    {
        if (!result.Output.Contains(label, StringComparison.Ordinal))
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
                Input = CreateStdinPipelineInput(),
                ExitTimeout = TimeSpan.FromSeconds(15),
            },
        });

    Console.WriteLine($"exit={result.ExitCode}");
    Console.WriteLine("merged output:");
    Console.WriteLine(result.Output.TrimEnd());

    Console.WriteLine("chunk boundaries (useful when rebuilding a consumer timeline):");
    var cursor = TimeSpan.Zero;
    foreach (var chunk in result.Chunks)
    {
        Console.WriteLine($"  [{cursor.TotalSeconds:F3}s -> {chunk.Time.TotalSeconds:F3}s)  {chunk.Data.Length} chars");
        cursor = chunk.Time;
    }

    ValidateStdinPipelineOutput(result.Output, result.ExitCode);
}

static void ValidateStdinPipelineOutput(string output, int exitCode)
{
    if (exitCode != 0)
        throw new InvalidOperationException($"stdin pipeline exited with {exitCode}");

    if (!output.Contains("minipty-stdin-pipeline", StringComparison.Ordinal))
        throw new InvalidOperationException("expected pipeline marker missing from output");
}

static string EscapeForDisplay(string text)
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
                "echo alpha& timeout /t 1 /nobreak >nul & echo beta& timeout /t 1 /nobreak >nul & echo gamma",
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

static PtyStartInfo CreateStdinPipelineStartInfo()
{
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
        return new PtyStartInfo
        {
            FileName = cmd,
            Arguments = ["/c", "find /v \"\" >nul & echo minipty-stdin-pipeline"],
            Size = new PtySize(80, 24),
        };
    }

    return new PtyStartInfo
    {
        FileName = "/bin/sh",
        Arguments = ["-c", "cat >/dev/null; printf 'minipty-stdin-pipeline\\n'"],
        Size = new PtySize(80, 24),
    };
}

static string CreateStdinPipelineInput() =>
    RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? "line 3\r\nline 1\r\nline 2\r\n"
        : "line 3\nline 1\nline 2\n";
