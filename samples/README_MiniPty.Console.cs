#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property AllowUnsafeBlocks=true
#:property PublishAot=true
#:package MiniPty@1.2.0
#:package MiniPty.Console@1.2.0

using System.Runtime.InteropServices;
using MiniPty;
using MiniPty.Console;

if (Console.IsInputRedirected || Console.IsOutputRedirected)
{
    Console.Error.WriteLine("Run this sample from an interactive terminal (TTY).");
    return;
}

await using var session = Pty.Start(CreateShellStartInfo());
Console.Error.WriteLine($"[MiniPty] child pid={session.ProcessId}, size={session.Size.Columns}x{session.Size.Rows}");
Console.Error.WriteLine("[MiniPty] Try: type `echo hello`, resize the window, then `exit`.");

using var attachCts = new CancellationTokenSource();
var outputStats = new OutputStats();
var pumpTask = PumpOutputAsync(session, outputStats);
using var consoleInput = PtyConsoleInput.Attach(session);

var exitTask = session.WaitForExitAsync(attachCts.Token);
_ = exitTask.ContinueWith(
    static (_, state) => ((CancellationTokenSource)state!).Cancel(),
    attachCts,
    CancellationToken.None,
    TaskContinuationOptions.ExecuteSynchronously,
    TaskScheduler.Default);

consoleInput.PumpInputUntil(attachCts.Token);
var exitCode = await exitTask;
await pumpTask;
ResetHostCursorColumnIfNeeded();
Console.Error.WriteLine($"[MiniPty] shell exit={exitCode}, chunks={outputStats.Chunks}, bytes={outputStats.Bytes}");

static async Task PumpOutputAsync(PtySession session, OutputStats stats)
{
#pragma warning disable CA2000 // Host standard output is process-owned and must not be disposed.
    var stdout = Console.OpenStandardOutput();
#pragma warning restore CA2000
    await foreach (var chunk in session.ReadOutputAsync())
    {
        stats.Chunks++;
        stats.Bytes += chunk.Data.Length;
        await stdout.WriteAsync(chunk.Data);
        await stdout.FlushAsync(CancellationToken.None);
    }
}

static void ResetHostCursorColumnIfNeeded()
{
    if (OperatingSystem.IsWindows())
        return;

#pragma warning disable CA2000 // Host standard output is process-owned and must not be disposed.
    var stdout = Console.OpenStandardOutput();
#pragma warning restore CA2000
    stdout.Write("\r\n"u8);
    stdout.Flush();
}

static PtyStartInfo CreateShellStartInfo()
{
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
        if (!File.Exists(cmd))
            throw new InvalidOperationException("cmd.exe is required.");

        return new PtyStartInfo
        {
            FileName = cmd,
            Size = new PtySize(80, 24),
        };
    }

    return new PtyStartInfo
    {
        FileName = File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh",
        Arguments = ["-i"],
        Size = new PtySize(80, 24),
    };
}

sealed class OutputStats
{
    public int Chunks;
    public int Bytes;
}
