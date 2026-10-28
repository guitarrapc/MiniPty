#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property AllowUnsafeBlocks=true
#:property PublishAot=true
#:package MiniPty@1.4.0

using MiniPty;

var shell = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";

// Run a command for its result: CompleteAsync drains the output and waits for exit.
await using (var run = Pty.Start(new PtyStartInfo { FileName = shell, Arguments = [OperatingSystem.IsWindows() ? "/c" : "-c", "echo hello"] }))
{
    var result = await run.CompleteAsync();
    Console.WriteLine($"exit={result.ExitCode}: {PtyOutput.ToDisplayText(result.GetText(), PtyOutputDisplayMode.PlainText).Trim()}");
}

// Drive a session: write input, resize at any time, and stream output until the child exits.
await using var session = Pty.Start(new PtyStartInfo { FileName = shell, Size = new PtySize(120, 30) });
await session.WriteInputAsync("echo hello\r");
await session.WriteInputAsync("exit\r");
session.Resize(new PtySize(100, 40));

var stdout = Console.OpenStandardOutput();
await foreach (var chunk in session.ReadOutputAsync())
    await stdout.WriteAsync(chunk.Data);

Console.WriteLine($"exit={(await session.WaitForExitAsync()).ExitCode}");
