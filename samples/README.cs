#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property AllowUnsafeBlocks=true
#:property PublishAot=true
#:package MiniPty@1.2.0

using System.Diagnostics;
using System.Runtime.InteropServices;
using MiniPty;

Console.Error.WriteLine("=== 1) Stream mode: chunks arrive over time ===");
await RunStreamModeAsync();

Console.Error.WriteLine();
Console.Error.WriteLine("=== 2) One-shot mode: one completion result ===");
await RunCompleteModeAsync();

static async Task RunStreamModeAsync()
{
    await using var session = Pty.Start(CreateTimedOutputStartInfo());
    var sw = Stopwatch.StartNew();
    var chunks = 0;
    await foreach (var chunk in session.ReadOutputAsync())
        Console.WriteLine($"+{sw.Elapsed.TotalSeconds,4:F1}s chunk#{++chunks} ({chunk.Data.Length} bytes)");

    Console.WriteLine($"stream exit={await session.WaitForExitAsync()}, chunks={chunks}");
}

static async Task RunCompleteModeAsync()
{
    await using var session = Pty.Start(CreateTimedOutputStartInfo());
    var result = await session.CompleteAsync();
    Console.WriteLine($"complete exit={result.ExitCode}, bytes={result.Output.Length}");
    Console.WriteLine(PtyOutput.ToDisplayText(result.GetText(), PtyOutputDisplayMode.PlainText).TrimEnd());
}

static PtyStartInfo CreateTimedOutputStartInfo()
{
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        return new PtyStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe",
            Arguments = ["/c", "echo alpha & timeout /t 1 /nobreak >nul & echo beta & timeout /t 1 /nobreak >nul & echo gamma"],
            Size = new PtySize(120, 30),
        };
    }

    return new PtyStartInfo
    {
        FileName = "/bin/sh",
        Arguments = ["-c", "printf 'alpha\\n'; sleep 1; printf 'beta\\n'; sleep 1; printf 'gamma\\n'"],
        Size = new PtySize(120, 30),
        TerminalName = "xterm-256color",
        Environment = new Dictionary<string, string?>
        {
            ["NO_COLOR"] = null,
            ["MINIPTY_SAMPLE"] = "true",
        },
    };
}
