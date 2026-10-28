#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property AllowUnsafeBlocks=true
#:property PublishAot=true
#:package MiniPty@1.4.0
#:package MiniPty.Console@1.4.0

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
    var stdout = Console.OpenStandardOutput();
    await foreach (var chunk in session.ReadOutputAsync())
    {
        await stdout.WriteAsync(chunk.Data);
        await stdout.FlushAsync();
    }
}
