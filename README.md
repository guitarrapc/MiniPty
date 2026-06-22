[![Build](https://github.com/guitarrapc/MiniPty/actions/workflows/build.yaml/badge.svg)](https://github.com/guitarrapc/MiniPty/actions/workflows/build.yaml)

# MiniPty

NativeAOT-friendly minimal cross-platform pseudo-terminal library for .NET.

**Motivation**

I need a PTY library for NativeAOT projects, but existing .NET PTY libraries are not guranteed to work in NativeAOT. MiniPty is a minimal PTY library designed for NativeAOT compatibility, with a simple API and no dependencies.

## Features


**Not supported**



## Packages

| Package | Role |
|---------|------|
| **MiniPty** | PTY session: streams, lifecycle, `CompleteAsync` |
| **MiniPty.Capture** | Timestamped PTY output observation (per-read chunks) |

## MiniPty (core)

```csharp
using MiniPty;

await using var session = Pty.Start(new PtyStartInfo
{
    FileName = "/bin/bash",
    Arguments = ["-lc", "stty size && echo hello"],
    Size = new PtySize(120, 30),
});

await session.WriteInputAsync("echo ok\n");
session.SendEof();
var exitCode = await session.WaitForExitAsync();
```

Or use `CompleteAsync` to drain output without timestamps:

```csharp
var result = await session.CompleteAsync(new PtyCompleteOptions
{
    Input = "echo ok\n",
});
// result.Output, result.ExitCode
```

**Backpressure:** If the child writes output and nobody reads `PtySession.Output`, the child may block once the PTY buffer fills. Use `CompleteAsync`, `MiniPty.Capture`, or read `Output` yourself.

**Dispose:** Disposing a `PtySession` kills the child if it is still running.

## MiniPty.Capture

Observe PTY execution from outside: each read from the output stream is recorded with elapsed time since session start. MiniPty does not define a recording format; consumers build timelines from `Chunks`.

```csharp
using MiniPty;
using MiniPty.Capture;

var result = await PtyCapture.RunAsync(new PtyStartInfo
{
    FileName = "/bin/bash",
    Arguments = ["-lc", "printf '\\e[31mred\\e[0m\\n'"],
    Size = new PtySize(120, 30),
});

foreach (var chunk in result.Chunks)
    Console.WriteLine($"{chunk.Time.TotalSeconds:F3}: {chunk.Data}");
```

Chunk timestamps are measured from session start (immediately after `Pty.Start`).

## Documentation

- [Specification](.github/docs/spec.md) — API contracts, scope, lessons learned
- [Implementation reference](.github/docs/references/pty_crossplatform.md) — ConPTY, `openpty`, EOF staging
- [Document index](.github/docs/spec_index.md)

## Samples

NativeAOT smoke (same as CI `run` job):

```bash
dotnet samples/Capture.cs -c Release --self-contained true -p:PublishAot=true -p:StripSymbols=true -p:DebugType=None
```

JIT run for local development:

```bash
dotnet samples/Capture.cs
```

## Build

```bash
dotnet build src/MiniPty/MiniPty.csproj
dotnet build src/MiniPty.Capture/MiniPty.Capture.csproj
dotnet pack src/MiniPty/MiniPty.csproj -o artifacts -c Release
dotnet pack src/MiniPty.Capture/MiniPty.Capture.csproj -o artifacts -c Release
```
