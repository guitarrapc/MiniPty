[![Build](https://github.com/guitarrapc/MiniPty/actions/workflows/build.yaml/badge.svg)](https://github.com/guitarrapc/MiniPty/actions/workflows/build.yaml)
[![release](https://github.com/guitarrapc/MiniPty/actions/workflows/release.yaml/badge.svg)](https://github.com/guitarrapc/MiniPty/actions/workflows/release.yaml)

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![NuGet](https://img.shields.io/nuget/v/MiniPty.svg?label=MiniPty%20nuget)](https://www.nuget.org/packages/MiniPty)

# MiniPty

NativeAOT-friendly minimal cross-platform pseudo-terminal library for .NET.

**Motivation**

I needed a PTY library for NativeAOT projects, but existing .NET PTY libraries don't reliably work with NativeAOT. MiniPty is a minimal PTY library with a simple API, no third-party dependencies, and in-process backends built for AOT publish.

**Benchmarks**

You can check various benchmark patterns at [GitHub Actions/Benchmark](https://github.com/guitarrapc/MiniPty/actions/runs/27328010495).

## Features

- NativeAOT ready, in-process backends only, no winpty or bundled helpers
- Multi-platform ready, Windows, Linux, macOS, and FreeBSD
- Spawn a child in a pseudo-terminal (`Pty.Start`)
- `Input` / `Output` byte streams; stdout and stderr merged on `Output`
- One-shot run with optional stdin and drained output (`CompleteAsync`)
- Resize the terminal after spawn (`PtySession.Resize`)
- Per-read timestamps for observation or recording (**MiniPty.Capture**, `PtyCapture.RunAsync`)
- Plain or colored host output from PTY bytes (`PtyOutput.ToDisplayText`)

**Not supported**

- Long-lived interactive sessions (vim, less, REPLs, a shell you type into for minutes)
- Ongoing bidirectional input beyond an optional initial stdin blob
- Remote shells (`ssh`) or tunneling a PTY over the network
- Full terminal emulation, TUI replay, or faithfully preserving `\r` overwrite lines
- Falling back to pipe redirect when PTY creation fails—if you need a PTY, MiniPty either gives you one or throws

## Quick start

Install NuGet packages by running the following commands.

```bash
# PTY session management and lifecycle
dotnet add package MiniPty

# Timestamped PTY output observation (per-read chunks)
dotnet add package MiniPty.Capture
```

**MiniPty** start a session with `Pty.Start`, then either pump `Output` yourself or call `CompleteAsync` for a one-shot run. Disposing the session kills the child if it is still running. If nobody reads `Output` while the child writes, the PTY buffer can fill and the child will block; `CompleteAsync` and continuous reads avoid that.

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
Console.WriteLine(PtyMemory.ToString(result.Output));
Console.WriteLine(result.ExitCode);

// For host-readable logs, transform control sequences first:
Console.WriteLine(PtyOutput.ToDisplayText(result.Output, PtyOutputDisplayMode.PlainText));

// Raw bytes: result.OutputBytes, or skip decoding with CompleteBytesAsync / PtyCapture.RunBytesAsync
Console.WriteLine(result.OutputBytes.Length);
```

**MiniPty.Capture** one call that runs the child, pumps output, and returns merged text, exit code, and per-read chunks. Each chunk's timestamp is elapsed time since `Pty.Start`.

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
foreach (var chunk in result.ByteChunks)
    Console.WriteLine($"{chunk.Time.TotalSeconds:F3}: {chunk.Data.Length} bytes");

foreach (var chunk in result.Chunks)
    Console.WriteLine($"{chunk.Time.TotalSeconds:F3}: {chunk.Text.Span}");

// Or plain text for logging:
Console.WriteLine(result.ToDisplayText(PtyOutputDisplayMode.PlainText));
```

## Samples

| Sample | Shows |
|--------|-------|
| [Capture.cs](samples/Capture.cs) | Minimal `MiniPty.Capture` smoke |
| [Session.cs](samples/Session.cs) | `Pty.Start`, background `Output` reads, `WriteInputAsync` / `SendEof`, `CompleteAsync`, `Resize` |
| [Observe.cs](samples/Observe.cs) | `PtyCapture.RunAsync`, per-read chunk timelines, stdin via `PtyCaptureOptions.Completion` |

Run a sample locally (JIT):

```bash
dotnet samples/Session.cs
dotnet samples/Observe.cs
dotnet samples/Capture.cs
```

NativeAOT publish (same flags as CI):

```bash
dotnet samples/Session.cs -c Release --self-contained true -p:PublishAot=true -p:StripSymbols=true -p:DebugType=None
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
dotnet pack
```
