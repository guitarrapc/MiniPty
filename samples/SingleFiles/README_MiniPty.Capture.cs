#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property AllowUnsafeBlocks=true
#:property PublishAot=true
#:package MiniPty@1.4.0
#:package MiniPty.Capture@1.4.0

// The MiniPty.Capture section of README.md's Quick start, as a runnable file:
//   dotnet samples/SingleFiles/README_MiniPty.Capture.cs
// It uses the published NuGet packages, as an application that installs them does. Keep it in step with the README.

using MiniPty;
using MiniPty.Capture;

var result = await PtyCapture.RunAsync(new PtyStartInfo
{
    FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
    Arguments = [OperatingSystem.IsWindows() ? "/c" : "-c", "echo hello"],
});

foreach (var chunk in result.Chunks)
    Console.WriteLine($"{chunk.Time.TotalSeconds:F3}s: {chunk.Data.Length} bytes");

Console.WriteLine(result.ToDisplayText(PtyOutputDisplayMode.PlainText));
