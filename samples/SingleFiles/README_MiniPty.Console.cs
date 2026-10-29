#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property AllowUnsafeBlocks=true
#:property PublishAot=true
#:package MiniPty@1.4.0
#:package MiniPty.Console@1.4.0

// The MiniPty.Console section of README.md's Quick start, as a runnable file (from an interactive terminal):
//   dotnet samples/SingleFiles/README_MiniPty.Console.cs
// It uses the published NuGet packages, as an application that installs them does. Keep it in step with the README.

using MiniPty;
using MiniPty.Console;

await using var session = Pty.Start(new PtyStartInfo { FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash" });

// Keyboard input and window resizes now go to the PTY.
using var input = PtyConsoleInput.Attach(session);

// MiniPty.Console does not read output: copy it to stdout yourself.
var output = CopyOutputAsync(session);

// Forward keys until the child exits.
using var exited = new CancellationTokenSource();
_ = session.WaitForExitAsync().ContinueWith(_ => exited.Cancel());
input.PumpInputUntil(exited.Token);
await output;

static async Task CopyOutputAsync(PtySession session)
{
    using var stdout = Console.OpenStandardOutput();
    await foreach (var chunk in session.ReadOutputAsync())
    {
        await stdout.WriteAsync(chunk.Data);
        await stdout.FlushAsync();
    }
}
