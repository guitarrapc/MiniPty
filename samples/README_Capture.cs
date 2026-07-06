#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property AllowUnsafeBlocks=true
#:property PublishAot=true
#:package MiniPty@1.2.0
#:package MiniPty.Capture@1.2.0

using System.Runtime.InteropServices;
using MiniPty;
using MiniPty.Capture;

var result = await PtyCapture.RunAsync(CreateCaptureStartInfo());

// Chunk timestamps are measured from session start (immediately after `Pty.Start`).
foreach (var chunk in result.Chunks)
    Console.WriteLine($"{chunk.Time.TotalSeconds:F3}: {chunk.Data.Length} bytes");

foreach (var textChunk in result.GetTextChunks())
    Console.WriteLine($"{textChunk.Time.TotalSeconds:F3}: {textChunk.Text.Span}");

// Or plain text for logging:
Console.WriteLine(result.ToDisplayText(PtyOutputDisplayMode.PlainText));

static PtyStartInfo CreateCaptureStartInfo()
{
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        return new PtyStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe",
            Arguments = ["/c", "echo capture-sample"],
            Size = new PtySize(120, 30),
        };
    }

    return new PtyStartInfo
    {
        FileName = "/bin/sh",
        Arguments = ["-c", "printf '\\e[31mcapture-sample\\e[0m\\n'"],
        Size = new PtySize(120, 30),
    };
}
