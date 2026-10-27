[![Build](https://github.com/guitarrapc/MiniPty/actions/workflows/build.yaml/badge.svg)](https://github.com/guitarrapc/MiniPty/actions/workflows/build.yaml)
[![release](https://github.com/guitarrapc/MiniPty/actions/workflows/release.yaml/badge.svg)](https://github.com/guitarrapc/MiniPty/actions/workflows/release.yaml)

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![NuGet](https://img.shields.io/nuget/v/MiniPty.svg?label=MiniPty%20nuget)](https://www.nuget.org/packages/MiniPty)

# MiniPty

NativeAOT-friendly minimal cross-platform pseudo-terminal library for .NET.

**Motivation**

I needed a PTY library for NativeAOT projects, but existing .NET PTY libraries don't reliably work with NativeAOT. MiniPty is a minimal PTY library with a simple API, no third-party dependencies, and in-process backends built for AOT publish.

## Features

- NativeAOT ready: no winpty, and no helper process running beside a session ([what ships per platform](#native-files))
- Cross-platform: Windows, Linux, and macOS (FreeBSD: [experimental](#platforms))
- Scales to thousands of sessions: an idle session holds no thread and no output buffer (I/O completions on Windows, one epoll/kqueue loop on Unix)
- Idle sessions cost nothing: an idle session holds no thread and no output buffer ([how each OS reads output](#backends))
- Bounded memory with backpressure: output goes into a 16 KiB buffer per session, and a slow consumer pauses the child instead of growing memory
- Allocation-free steady state: reading chunks, writing input, and waiting for exit allocate nothing after the first call
- Signals the right process: on Linux, signals go through a pidfd, so a reused PID is never hit

**Not supported**

- Remote shells (`ssh`) or tunneling a PTY over the network
- Full terminal emulation, TUI replay, or faithfully preserving `\r` overwrite lines
- Falling back to pipe redirect when PTY creation fails

## Platforms

MiniPty creates a real PTY on each supported OS. See [Backends](#backends) for how each OS creates the PTY.

| OS | Architectures | Status |
|----|---------------|--------|
| Windows 10 1809+ / Windows 11 | x64, arm64 | Supported |
| Linux | x64, arm64 | Supported |
| macOS | x64, arm64 | Supported |
| FreeBSD | — | Experimental: tested neither locally nor in CI, and the package ships no FreeBSD binary, so MiniPty works there only when built from source |

### Native files

Your application carries the following besides the MiniPty assembly. The package and `dotnet publish` place these files for you.

| OS | Files beside the application | Under NativeAOT |
|----|------------------------------|-----------------|
| Windows | None: ConPTY comes with Windows. The [out-of-band ConPTY](#out-of-band-conpty) is optional and supplied by your application | None |
| Linux (x64, arm64) | `libminipty_unix.so` | None: the shim is linked into the executable |
| macOS (x64, arm64) | `libminipty_unix.dylib` and `minipty_spawn_helper` | `minipty_spawn_helper` only: the shim is linked into the executable, and the launcher must stay in the same directory, where MiniPty looks for it |
| FreeBSD (experimental) | `libminipty_unix.so`, which the package does not include: it is built with `cc` when MiniPty is built from source on FreeBSD | Not supported by the package |


## Quick start

```bash
dotnet add package MiniPty            # PTY sessions
dotnet add package MiniPty.Capture    # timestamped output capture
dotnet add package MiniPty.Console    # attach the host terminal (vim, etc.)
dotnet add package MiniPty.Terminal   # backend for xterm.js and editor terminals
```

### MiniPty

Start a child with `Pty.Start`, then either run it for its result with `CompleteAsync` or drive it with `WriteInputAsync` and `ReadOutputAsync`. Try `dotnet samples/SingleFiles/README_MiniPty.cs`.

```csharp
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
```

### MiniPty.Capture

Run a child and get its output, exit code, and every read with its time since `Pty.Start`. Try `dotnet samples/SingleFiles/README_MiniPty.Capture.cs`.

```csharp
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
```

### MiniPty.Console

Attach the host terminal to a session, so interactive programs such as vim get your keyboard and window size. Reading output stays your job. Try `dotnet samples/SingleFiles/README_MiniPty.Console.cs`.

```csharp
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
```

[ConsoleAttach.cs](samples/SingleFiles/ConsoleAttach.cs) adds status messages and keeps the shell prompt aligned afterwards.

### MiniPty.Terminal

Serve a PTY to xterm.js or an editor terminal, the role node-pty plays behind VS Code. Try `dotnet samples/SingleFiles/WebTerminal.cs` for a shell in your browser.

```csharp
using MiniPty;
using MiniPty.Terminal;

// webSocket: any System.Net.WebSockets.WebSocket (Kestrel, HttpListener, ...)
// ct: cancel it when the connection ends (for example HttpContext.RequestAborted)
var status = await PtyWebSocketBridge.RunAsync(
    new PtyStartInfo { FileName = "/bin/bash", Size = new PtySize(120, 30) },
    webSocket,
    cancellationToken: ct);
Console.WriteLine($"shell exited: {status.ExitCode}");
```

Other entry points:

- `PtyWebSocketSessionManager`: reconnectable browser sessions with authentication and replay.
- `PtyStdioBridge`: length-prefixed frames over stdio, for a helper process such as [VsCodeTerminalHelper.cs](samples/SingleFiles/VsCodeTerminalHelper.cs) behind the [VS Code extension sample](samples/VsCodeExtension).
- `PtyTerminal`: a node-pty-style push API with pause/resume flow control, for your own transport.

Protocol and bridging details: [terminal.md](.github/docs/specs/terminal.md).

## Backends

Each OS has its own backend behind the same API. None of them uses winpty, and none keeps a helper process running beside a session.

### Windows: ConPTY

MiniPty calls Win32 ConPTY (`CreatePseudoConsole`) directly through P/Invoke, attaches the child with `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE`, and resizes with `ResizePseudoConsole`. Output is read through I/O completions on the runtime's thread pool, so an idle session holds no thread and no output buffer. An application can supply the out-of-band ConPTY (`conpty.dll` + `OpenConsole.exe`) per session with `PtyStartInfo.ConPty`; see [Out-of-band ConPTY](#out-of-band-conpty).

### Linux: `forkpty`

MiniPty uses the small `libminipty_unix` native shim to call the platform PTY API, then `execve` the child inside the PTY. Resize uses `TIOCSWINSZ`. One background loop (the exit watcher's thread, on epoll) reads every session's output and writes its queued input, so an idle session holds no thread and no output buffer.

### macOS: `posix_spawn` + launcher

MiniPty uses the Unix backend through `libminipty_unix.dylib`, and starts each child through `minipty_spawn_helper` (not `forkpty`): a small launcher that `posix_spawn` starts, which takes the terminal as its controlling terminal and then replaces itself with the child by `exec`, so no helper process stays running. Resize uses `TIOCSWINSZ`. Output and input go through the same one loop as on Linux, on kqueue.

### FreeBSD (experimental): `forkpty`

MiniPty uses the Unix backend through libutil, matching the Linux/macOS PTY lifecycle.

## Advanced Usage

### Good to know

To use MiniPty effectively, keep the following points in mind.

- **Keep reading output.** A PTY nobody reads fills up and blocks the child. `ReadOutputAsync`, `CompleteAsync`, and the `Output` stream all drain it.
- **Disposing ends the child.** Windows terminates it. Unix hangs it up (`SIGHUP`, so a shell saves its history) and kills it if it has not exited a second later ([Session contract](.github/docs/specs/core_session.md#session-contract)).
- **`Environment` overlays the parent's.** A null value removes a variable. On Unix, `TerminalName` sets `TERM`, which defaults to `xterm-256color` ([Spawn environment](.github/docs/specs/core_session.md#spawn-environment)).
- **MiniPty is not a sandbox.** The child runs with your permissions. If a program name, path, or argument comes from untrusted input, read [Untrusted program names and paths](.github/docs/specs/core_session.md#untrusted-program-names-and-paths) and [Batch files](.github/docs/specs/core_session.md#batch-files-windows) first.

### Out-of-band ConPTY for Windows

Building a terminal emulator on Windows? Use the out-of-band ConPTY library instead of the in-box ConPTY.

> [!TIP]
> In-box means the ConPTY that comes with Windows itself.

The in-box ConPTY drops graphics protocols (Sixel, Kitty) and some terminal queries, forwards mouse tracking only to programs that read VT input, and changes behavior with Windows updates. The ConPTY that Windows Terminal ships, published by Microsoft as `Microsoft.Windows.Console.ConPTY`, forwards everything and behaves the same on every Windows build.

```shell
# Use the out-of-band ConPTY library, 1.25 or later (older ones can stall three seconds at startup)
dotnet add package Microsoft.Windows.Console.ConPTY --version 1.25.260930003
```

MiniPty's build targets copy `OpenConsole.exe` beside `conpty.dll` for you. Both are native code that runs as your application, so ship them where your own binaries are protected, and take security fixes through package updates ([details and measurements](.github/docs/specs/core_session.md#out-of-band-conpty)).

```csharp
using MiniPty;

// Null on Unix, or when conpty.dll is not beside your application: the session then uses the in-box ConPTY.
var conpty = ConPtyLibrary.TryLoadFromApplicationDirectory();
if (conpty is { HasConsoleHost: false })
    Console.Error.WriteLine("OpenConsole.exe is missing; sessions fall back to conhost.exe");

await using var session = Pty.Start(new PtyStartInfo { FileName = "cmd.exe", ConPty = conpty });
```

### Windows options

`KillProcessTree = true` ends everything the child started, even programs in a console of their own, on dispose, on kill, and when your process crashes. Turn it on for commands run for their result (CI steps, test runners, agent tool calls). Leave it off for interactive terminals, where a window the user opened (`code .`) should outlive the tab ([Process tree](.github/docs/specs/core_session.md#process-tree)).

```csharp
await using var session = Pty.Start(new PtyStartInfo
{
    FileName = "cmd.exe",
    Arguments = ["/c", "npm test"],
    KillProcessTree = true,
});
```

`InheritCursor = true` keeps what the frontend already shows instead of letting ConPTY clear the screen. The frontend must then answer ConPTY's cursor position query, or the child waits ([Inherited cursor](.github/docs/specs/core_session.md#inherited-cursor)).

Both options are ignored on Unix.

### GUI hosts

WinUI, WPF, WinForms, and Avalonia can call every MiniPty API on the UI thread. Nothing blocks it, and no marshaling is needed.

```csharp
// All on the UI thread.
var session = Pty.Start(new PtyStartInfo
{
    FileName = shell,
    Size = new PtySize(columns, rows),
    ConPty = ConPtyLibrary.TryLoadFromApplicationDirectory(),   // null on Unix, so the same line works everywhere
});

view.SizeChanged += (_, _) => session.Resize(new PtySize(view.Columns, view.Rows));
view.TextInput   += async (_, text) => await session.WriteInputAsync(text);
window.Closed    += async (_, _) => await session.DisposeAsync();

await foreach (var chunk in session.ReadOutputAsync())
    view.Feed(chunk.Data.Span);   // your VT parser and renderer
```

Full terminal emulators built this way: [samples/WinUiTerminal](samples/WinUiTerminal) (Windows) and [samples/AvaloniaTerminal](samples/AvaloniaTerminal) (Windows, Linux, macOS). See also [GUI hosts](.github/docs/specs/core_session.md#gui-hosts).


## Samples

See the [VS Code extension sample instructions](samples/VsCodeExtension) for complete Windows and macOS reproduction steps, including NativeAOT helper publishing and authenticated reconnect.

| Sample | Shows |
|--------|-------|
| [Capture.cs](samples/SingleFiles/Capture.cs) | Minimal `MiniPty.Capture` smoke |
| [Session.cs](samples/SingleFiles/Session.cs) | `Pty.Start`, background `Output` reads, `WriteInputAsync` / `SendEof`, `CompleteAsync`, `Resize` |
| [Interactive.cs](samples/SingleFiles/Interactive.cs) | `ReadOutputAsync` persistent loop, marker-driven writes, mid-session `Resize`, natural child exit |
| [ConsoleAttach.cs](samples/SingleFiles/ConsoleAttach.cs) | **MiniPty.Console** host attach: keyboard → PTY, `ReadOutputAsync` → host display (requires interactive TTY) |
| [Observe.cs](samples/SingleFiles/Observe.cs) | `PtyCapture.RunAsync`, per-read chunk timelines, stdin via `PtyCaptureOptions.Completion` |
| [WebTerminal.cs](samples/SingleFiles/WebTerminal.cs) | **MiniPty.Terminal** xterm.js in the browser: WebSocket bridge, ACK flow control, resize, exit banner, Ctrl+V paste and Ctrl+C copy of a selection |
| [WebTerminalScale.cs](samples/SingleFiles/WebTerminalScale.cs) | **MiniPty.Terminal** in an ASP.NET Core host serving many sessions: a dashboard of what each session costs the host (threads, memory, handles), and a `bench` mode that measures it with keystroke echo latency, and a flood's WebSocket frames per 32 KiB and host CPU per MiB |
| [VsCodeTerminalHelper.cs](samples/SingleFiles/VsCodeTerminalHelper.cs) | **MiniPty.Terminal** stdio-framed helper for a VS Code `Pseudoterminal` |
| [VsCodeExtension](samples/VsCodeExtension) | Dependency-free VS Code extension sample connecting `Pseudoterminal` to the NativeAOT helper |
| [VsCodePersistentBridge.cs](samples/SingleFiles/VsCodePersistentBridge.cs) | Authenticated loopback service for real VS Code detach/reconnect testing |
| [AvaloniaTerminal](samples/AvaloniaTerminal) | The same terminal emulator as an Avalonia desktop app (Windows, Linux, macOS): `DrawingContext` grid, the same key, mouse, and clipboard handling, the same UI-thread MiniPty calls |
| [WinUiTerminal](samples/WinUiTerminal) | Code-only WinUI 3 terminal emulator (VT parser, screen buffer, Win2D grid, mouse copy/paste) driving `Pty.Start`, `ReadOutputAsync`, `WriteInputAsync`, `Resize`, and `DisposeAsync` from the UI thread (Windows only) |

Run a sample locally (JIT):

```bash
dotnet samples/SingleFiles/Session.cs
dotnet samples/SingleFiles/Interactive.cs
dotnet samples/SingleFiles/ConsoleAttach.cs
dotnet samples/SingleFiles/Observe.cs
dotnet samples/SingleFiles/Capture.cs
dotnet samples/SingleFiles/WebTerminal.cs   # then open the http://localhost:5170/#token=... address it prints
dotnet run -c Release samples/SingleFiles/WebTerminalScale.cs   # then open the http://localhost:5171/#token=... address it prints
dotnet run -c Release samples/SingleFiles/WebTerminalScale.cs -- bench --sessions 16,64,256
dotnet run --project samples/WinUiTerminal   # Windows only
dotnet run --project samples/AvaloniaTerminal   # Windows, Linux, macOS
```

NativeAOT publish (same flags as CI):

```bash
dotnet samples/SingleFiles/Session.cs -c Release --self-contained true -p:PublishAot=true -p:StripSymbols=true -p:DebugType=None
```

## Development

Use `dotnet` for local development, debugging, or publishing.

### Documentation

- [Specification](.github/docs/spec.md) — API contracts, scope, document map, lessons learned
- [Lifecycle](.github/docs/specs/lifecycle.md) — responsibility split, session flow diagram, cancellation, EOF, drain, disposal
- [Implementation reference](.github/docs/references/pty_crossplatform.md) — ConPTY, `forkpty`, EOF staging

### Build

```bash
dotnet build
dotnet test
dotnet pack
```
