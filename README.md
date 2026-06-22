[![Build](https://github.com/guitarrapc/MiniPty/actions/workflows/build.yaml/badge.svg)](https://github.com/guitarrapc/MiniPty/actions/workflows/build.yaml)

# MiniPty

NativeAOT-friendly minimal cross-platform pseudo-terminal library for .NET.

**Motivation**

I need a PTY library for NativeAOT projects, but existing .NET PTY libraries are not guranteed to work in NativeAOT. MiniPty is a minimal PTY library designed for NativeAOT compatibility, with a simple API and no dependencies.

## Features

MiniPty provides a minimal set of features for running a child process in a PTY and observing its output.


**Not supported**



## Quick start

Install NuGet packages by running the following commands.

```bash
# PTY session management and lifecycle
dotnet add package MiniPty

# Timestamped PTY output observation (per-read chunks)
dotnet add package MiniPty.Capture
```

**MiniPty** supports running a child process in a PTY and observing its output through `PtySession.Output` or `PtySession.CompleteAsync`. The child process is killed when the session is disposed. If the child writes output and nobody reads `PtySession.Output`, the child may block once the PTY buffer fills. Use `CompleteAsync`, `MiniPty.Capture`, or read `Output` yourself.

```csharp
using MiniPty;

// Disposing a pty session kills the child process if it is still running. Use `WaitForExitAsync` to wait for the child to exit without killing it.
await using var session = Pty.Start(new PtyStartInfo
{
    FileName = "/bin/bash",
    Arguments = ["-lc", "stty size && echo hello"],
    Size = new PtySize(120, 30),
});

await session.WriteInputAsync("echo ok\n");
session.SendEof();
var exitCode = await session.WaitForExitAsync();
Console.WriteLine(session.Output);
Console.WriteLine($"Exit code: {exitCode}");

// Use `CompleteAsync` to drain output without timestamps:
var result = await session.CompleteAsync(new PtyCompleteOptions
{
    Input = "echo ok\n",
});
Console.WriteLine(result.Output);
Console.WriteLine(result.ExitCode);
```

**MiniPty.Capture** provides a higher-level API for observing PTY output with timestamps. Observe PTY execution from outside, each read from the output stream is recorded with elapsed time since session start.

```csharp
using MiniPty;
using MiniPty.Capture;

var result = await PtyCapture.RunAsync(new PtyStartInfo
{
    FileName = "/bin/bash",
    Arguments = ["-lc", "printf '\\e[31mred\\e[0m\\n'"],
    Size = new PtySize(120, 30),
});

// Chunk timestamps are measured from session start (immediately after `Pty.Start`).
foreach (var chunk in result.Chunks)
    Console.WriteLine($"{chunk.Time.TotalSeconds:F3}: {chunk.Data}");
```

## Samples

- [Capture](samples/Capture.cs) — capture PTY output with timestamps using `MiniPty.Capture`

To run sample as NativeAOT, use the following command:

```bash
dotnet samples/Capture.cs -c Release --self-contained true -p:PublishAot=true -p:StripSymbols=true -p:DebugType=None
```

## Development

Use `dotnet` for local development, debugging, or publishing.

### Documentation

- [Document index](.github/docs/spec_index.md)
- [Specification](.github/docs/spec.md) — API contracts, scope, lessons learned
- [Implementation reference](.github/docs/references/pty_crossplatform.md) — ConPTY, `openpty`, EOF staging

### Build

```bash
dotnet build
dotnet test
```
