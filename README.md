[![Build](https://github.com/guitarrapc/MiniPty/actions/workflows/build.yaml/badge.svg)](https://github.com/guitarrapc/MiniPty/actions/workflows/build.yaml)
[![release](https://github.com/guitarrapc/MiniPty/actions/workflows/release.yaml/badge.svg)](https://github.com/guitarrapc/MiniPty/actions/workflows/release.yaml)

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![NuGet](https://img.shields.io/nuget/v/MiniPty.svg?label=MiniPty%20nuget)](https://www.nuget.org/packages/MiniPty)

# MiniPty

NativeAOT-friendly minimal cross-platform pseudo-terminal library for .NET.

**Motivation**

I needed a PTY library for NativeAOT projects, but existing .NET PTY libraries don't reliably work with NativeAOT. MiniPty is a minimal PTY library with a simple API, no third-party dependencies, and in-process backends built for AOT publish.

**Benchmarks**

You can check various benchmark patterns at [GitHub Actions/Benchmark](https://github.com/guitarrapc/MiniPty/actions/runs/27960737962).

Ubuntu 24.04, .NET 10


![](https://raw.githubusercontent.com/guitarrapc/MiniPty/refs/heads/main/images/benchmark.png)

## Features

- NativeAOT ready, in-process backends only, no winpty or bundled helpers
- Multi-platform ready, Windows, Linux, macOS, and FreeBSD
- Spawn a child in a pseudo-terminal (`Pty.Start`)
- Overlay child environment variables and set Unix `TERM`
- `Input` / `Output` byte streams; stdout and stderr merged on `Output`
- Persistent bytes-only output streaming (`ReadOutputAsync`)
- One-shot run with optional stdin and drained output (`CompleteAsync`)
- Resize the terminal after spawn (`PtySession.Resize`)
- Per-read timestamps for observation or recording (**MiniPty.Capture**, `PtyCapture.RunAsync`)
- Plain or colored host output from PTY bytes (`PtyOutput.ToDisplayText`)

**Not supported**

- Full local-console attachment for programs such as vim, less, and htop
- Remote shells (`ssh`) or tunneling a PTY over the network
- Full terminal emulation, TUI replay, or faithfully preserving `\r` overwrite lines
- Falling back to pipe redirect when PTY creation fails—if you need a PTY, MiniPty either gives you one or throws

## Platform backends

MiniPty creates a real PTY on each supported OS; it does not fall back to redirected pipes when PTY creation fails.

| OS | Backend | Notes |
|----|---------|-------|
| Windows | ConPTY (`CreatePseudoConsole`) | Uses Win32 ConPTY directly through P/Invoke, attaches the child with `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE`, and resizes with `ResizePseudoConsole`. Requires Windows 10 1809+ / Windows 11. No winpty or helper process is used. |
| Ubuntu / Linux | `forkpty` | Uses the small `libminipty_unix` native shim to call the platform PTY API, then `execve` the child inside the PTY. Resize uses `TIOCSWINSZ`. |
| macOS | `forkpty` | Uses the same Unix backend shape through `libminipty_unix.dylib`, backed by macOS `forkpty` / libutil. Resize uses `TIOCSWINSZ`. |
| FreeBSD | `forkpty` | Uses the Unix backend through libutil, matching the Linux/macOS PTY lifecycle. |

## Quick start

Install NuGet packages by running the following commands.

```bash
# PTY session management and lifecycle
dotnet add package MiniPty

# Timestamped PTY output observation (per-read chunks)
dotnet add package MiniPty.Capture
```

**MiniPty** start a session with `Pty.Start`, then either call `ReadOutputAsync` for persistent bytes-only output streaming or call `CompleteAsync` for a one-shot run. Disposing the session kills the child if it is still running. If nobody reads output while the child writes, the PTY buffer can fill and the child will block; `ReadOutputAsync`, `CompleteAsync`, and continuous `Output` stream reads avoid that.

```csharp
using System.Text;
using MiniPty;

// Disposing a pty session kills the child process if it is still running. Use `WaitForExitAsync` to wait for the child to exit without killing it.
await using var session = Pty.Start(new PtyStartInfo
{
    FileName = "/bin/bash",
    Arguments = ["-lc", "stty size && echo hello"],
    Size = new PtySize(120, 30),
    TerminalName = "xterm-256color",
    Environment = new Dictionary<string, string?>
    {
        ["NO_COLOR"] = null,
        ["MINIPTY_SAMPLE"] = "true",
    },
});

var outputTask = Task.Run(async () =>
{
    await foreach (var chunk in session.ReadOutputAsync())
        Console.Write(Encoding.UTF8.GetString(chunk.Data.Span));
});

await session.WriteInputAsync("echo ok\n");
session.SendEof();
var exitCode = await session.WaitForExitAsync();
await outputTask;
Console.WriteLine($"Exit code: {exitCode}");

// Use `CompleteAsync` to drain output without timestamps:
var result = await session.CompleteAsync(new PtyCompleteOptions
{
    Input = "echo ok\n",
});
Console.WriteLine(result.GetTextString());
Console.WriteLine(result.ExitCode);

// For host-readable logs, transform control sequences first:
Console.WriteLine(PtyOutput.ToDisplayText(result.GetText(), PtyOutputDisplayMode.PlainText));

// Raw bytes: result.Output, or skip pump decoding with DecodeOutput = false
Console.WriteLine(result.Output.Length);
```

`PtyStartInfo.Environment` overlays the parent environment. A null value removes a variable; an empty string sets an empty variable on platforms that preserve empty environment values. On Unix, `TerminalName` sets `TERM`; if no `TERM` remains, MiniPty defaults it to `xterm-256color`. On Windows, `TerminalName` is currently ignored and `TERM` is only passed when explicitly set in `Environment`.

MiniPty is not a sandbox. Processes run with the parent process permissions unless the host application isolates them with OS users, containers, or another security boundary.

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
foreach (var chunk in result.Chunks)
    Console.WriteLine($"{chunk.Time.TotalSeconds:F3}: {chunk.Data.Length} bytes");

foreach (var textChunk in result.GetTextChunks())
    Console.WriteLine($"{textChunk.Time.TotalSeconds:F3}: {textChunk.Text.Span}");

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

- [Specification](.github/docs/spec.md) — API contracts, scope, document map, lessons learned
- [Implementation reference](.github/docs/references/pty_crossplatform.md) — ConPTY, `forkpty`, EOF staging

### Build

```bash
dotnet build
dotnet test
dotnet pack
```
