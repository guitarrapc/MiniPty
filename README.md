[![Build](https://github.com/guitarrapc/MiniPty/actions/workflows/build.yaml/badge.svg)](https://github.com/guitarrapc/MiniPty/actions/workflows/build.yaml)
[![release](https://github.com/guitarrapc/MiniPty/actions/workflows/release.yaml/badge.svg)](https://github.com/guitarrapc/MiniPty/actions/workflows/release.yaml)

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![NuGet](https://img.shields.io/nuget/v/MiniPty.svg?label=MiniPty%20nuget)](https://www.nuget.org/packages/MiniPty)

# MiniPty

A minimal, cross-platform pseudo-terminal (PTY) library for .NET with NativeAOT support.

**Motivation**

I needed a PTY library for high-performance NativeAOT projects. MiniPty provides a simple API, no third-party dependencies, and in-process backends built for NativeAOT publishing.

## Features

- NativeAOT ready: no winpty, and no helper process running beside a session ([what ships per platform](#native-files))
- Cross-platform: Windows, Linux, and macOS (FreeBSD: [experimental](#platforms))
- Scaling: Scales to thousands of sessions. An idle session holds no thread and no output buffer.
- Bounded output memory: each session uses a 16 KiB buffer. A slow consumer pauses the child instead of increasing memory use.
- Allocation-free steady state: reading chunks, writing input, and waiting for exit allocate nothing after the first call.
- Safe signal delivery: on Linux 5.2 and later, `clone` returns a pidfd with the child. Signals use this handle, avoiding PIDs that another process can reuse ([details](.github/docs/references/pty_crossplatform.md#child-exit-observation)).
- Metrics: the `MiniPty` meter reports open sessions, start time, output bytes, and stalls. Without listeners, it takes no timestamps and allocates nothing ([metrics](#metrics)).

**Not supported**

- Remote shells (`ssh`) or tunneling a PTY over the network
- Full terminal emulation, TUI replay, or faithfully preserving `\r` overwrite lines
- Pipe redirection when PTY creation fails

## Platforms

MiniPty creates a real PTY on each supported OS. See [Backends](#backends) for how each OS creates the PTY.

| OS | Architectures | Status |
|----|---------------|--------|
| Windows 10 1809+ / Windows 11 / Windows Server 2019+ | x64, arm64 | Supported |
| Linux, glibc 2.27+ | x64, arm64 | Supported |
| Linux, musl 1.2.3+ (Alpine) | x64, arm64 | Supported |
| macOS 12.0+ | x64, arm64 | Supported |
| FreeBSD 14.4+ | x64 (tested), arm64 (compiled only) | Experimental |

The Linux and macOS minimums apply to the native files. They cover at least the OS versions that .NET 10 supports. Windows requires ConPTY, introduced in Windows 10 1809 and Windows Server 2019. .NET 10 also supports earlier builds, such as Windows 10 1607 and Windows Server 2016. On those builds, `Pty.IsSupported` is `false` and `Pty.Start` throws `PlatformNotSupportedException`. The FreeBSD support is experimental, it compiles & tests on Container & CI for 14.4 and 15.1.

### Native files

Your application needs these files in addition to the MiniPty assembly. The package and `dotnet publish` place them for you.

| OS | Files beside the application | Under NativeAOT |
|----|------------------------------|-----------------|
| Windows | None: ConPTY comes with Windows. The [out-of-band ConPTY](#out-of-band-conpty-for-windows) is optional and supplied by your application | None |
| Linux (x64, arm64, glibc and musl) | `libminipty_unix.so` | None. The shim links into the executable. |
| macOS (x64, arm64) | `libminipty_unix.dylib` and `minipty_spawn_helper` | `minipty_spawn_helper` only. The shim links into the executable. The launcher must remain beside the executable, where MiniPty searches for it. |
| FreeBSD (experimental) | `libminipty_unix.so`. The package does not include it. Building MiniPty from source on FreeBSD compiles it with `cc`. | Not supported by the package |

## Performance

<!-- benchmark:run:start -->

The numbers below are from [run 42335813387](https://github.com/guitarrapc/MiniPty/actions/runs/42335813387) on 2026-11-07, with .NET 10.0.14 on GitHub Actions hosted runners:

- Linux (`ubuntu-24.04`): AMD EPYC 7763 2.61GHz, 1 CPU, 4 logical and 2 physical cores
- macOS (`macos-26`): Apple M1 (Virtual), 1 CPU, 3 logical and 3 physical cores
- Windows (`windows-2025`): AMD EPYC 9V45 2.60GHz, 1 CPU, 4 logical and 2 physical cores

<!-- benchmark:run:end -->

`PtyLatencyBenchmarks` benchmarks the keystroke round trip, time from writing one byte to reading the child's echo. On Windows, the `ConPty` parameter selects either ConPTY implementation for each library. The in-box ConPTY repaints on its own frame schedule, which determines round-trip latency for any library. The [out-of-band ConPTY](#out-of-band-conpty-for-windows) removes that wait.

<!-- benchmark:latency:start -->

| Runner | `ConPty` | `MiniPty` P50 / P95 | `PortaPty` P50 / P95 | Allocated (`MiniPty` / `PortaPty`) |
|--------|----------|---------------------|----------------------|------------------------------------|
| Linux | none | 95.34 μs / 121.92 μs | 105.71 μs / 131.84 μs | 0 B / 680 B |
| macOS | none | 34.17 μs / 61.25 μs | 38.73 μs / 132.57 μs | 0 B / 680 B |
| Windows | out-of-band | 110.05 μs / 131.0 μs | 109.50 μs / 134.4 μs | 0 B / 376 B |
| Windows | in-box | 11,933.70 μs / 22,455.0 μs | 10,998.50 μs / 22,009.2 μs | 0 B / 160 B |

<!-- benchmark:latency:end -->

`PtyThroughputBenchmarks` measures output throughput, the child writes 1 MiB of 77-character lines, read until its end marker. On Windows, the `ConPty` renders the child's output and sends its own VT stream. On Windows, this benchmark mainly measures ConPTY rendering speed rather than byte-pipe throughput.

<!-- benchmark:throughput:start -->

| Runner | `ConPty` | `MiniPty` MiB/s | `PortaPty` MiB/s | Allocated (`MiniPty` / `PortaPty`) |
|--------|----------|-----------------|------------------|------------------------------------|
| Linux | none | 216.3 | 192.2 | 0 B / 72889 B |
| macOS | none | 58.7 | 83.9 | 0 B / 492200 B |
| Windows | out-of-band | 7.1 | 7.7 | 12 B / 884802 B |
| Windows | in-box | 6.3 | 6.5 | 12 B / 120 B |

<!-- benchmark:throughput:end -->

`PtyStartupBenchmarks` measures session start time, one session from `Pty.Start` to exit. It uses the OS shell (`cmd.exe /c` on Windows, `sh -c` elsewhere), so process creation accounts for most of the time. `Session_Exit0_WaitForExit` runs `exit 0` and waits for exit. `Session_Echo_Bytes` runs `echo` and reads all output.

<!-- benchmark:startup:start -->

| Runner | `Session_Exit0_WaitForExit` Mean / Allocated | `Session_Echo_Bytes` Mean / Allocated |
|--------|----------------------------------------------|---------------------------------------|
| Linux | 858.0 μs / 1.27 KB | 879.1 μs / 1.78 KB |
| macOS | 5.606 ms / 1.27 KB | 8.376 ms / 1.79 KB |
| Windows | 11.88 ms / 1.25 KB | 16.61 ms / 1.86 KB |

<!-- benchmark:startup:end -->

PtyStartupHostMemoryBenchmarks measures session start in a large host with the same `Session_Exit0_WaitForExit` operation with extra host memory. The host holds either 0 or 2 GiB of resident memory beyond the runtime's own, as an IDE backend or web terminal may.

<!-- benchmark:startup-host-memory:start -->

| Runner | `HostResidentMiB` 0: Mean / Allocated | `HostResidentMiB` 2048: Mean / Allocated |
|--------|---------------------------------------|------------------------------------------|
| Linux | 852.0 μs / 1.27 KB | 852.1 μs / 1.27 KB |
| macOS | 5.472 ms / 1.27 KB | 5.523 ms / 1.27 KB |
| Windows | 13.41 ms / 1.25 KB | 12.95 ms / 1.25 KB |

<!-- benchmark:startup-host-memory:end -->

## Quick start

```bash
dotnet add package MiniPty            # PTY sessions
dotnet add package MiniPty.Capture    # timestamped output capture
dotnet add package MiniPty.Console    # attach the host terminal (vim, etc.)
dotnet add package MiniPty.Terminal   # backend for xterm.js and editor terminals
```

### MiniPty

Start a child with `Pty.Start`. Use `CompleteAsync` to collect its result, or use `WriteInputAsync` and `ReadOutputAsync` to interact with it. Run this example with `dotnet samples/SingleFiles/README_MiniPty.cs`. For more examples, [run a command with stdin and EOF](samples/SingleFiles/README.md#run-a-command-for-its-result). Or [control an interactive program](samples/SingleFiles/README.md#drive-an-interactive-program) by waiting for output before sending input.

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

using var stdout = Console.OpenStandardOutput();
await foreach (var chunk in session.ReadOutputAsync())
    await stdout.WriteAsync(chunk.Data);

Console.WriteLine($"exit={(await session.WaitForExitAsync()).ExitCode}");
```

### MiniPty.Capture

Run a child and collect its output, exit code, and each read's time since `Pty.Start`. Run this example with `dotnet samples/SingleFiles/README_MiniPty.Capture.cs`. For an example with stdin and a printed timeline, see [Capture output with timing](samples/SingleFiles/README.md#capture-output-with-timing).

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

Attach the host terminal to a session to send keyboard input and window size changes to interactive programs such as vim. Your application must read the output. Run `dotnet samples/SingleFiles/README_MiniPty.Console.cs` from an interactive terminal. See [Hand your terminal to a program](samples/SingleFiles/README.md#hand-your-terminal-to-a-program) for status messages and shell prompt alignment after exit. See the [TUI demo](samples/TuiDemo) for full-screen programs.

> [!TIP]
> On Windows, the host console reads input as UTF-8 while attached and restores the previous input code page on detach. Non-ASCII input, such as Japanese text, reaches the PTY as UTF-8. A legacy console output code page misdecodes the PTY's UTF-8 bytes. Set `Console.OutputEncoding = Encoding.UTF8` while copying output.

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
    using var stdout = Console.OpenStandardOutput();
    await foreach (var chunk in session.ReadOutputAsync())
    {
        await stdout.WriteAsync(chunk.Data);
        await stdout.FlushAsync();
    }
}
```

### MiniPty.Terminal

Connect a PTY to xterm.js or an editor terminal, as node-pty does for VS Code. Run the complete server with `dotnet samples/SingleFiles/WebTerminal.cs`. Open the address it prints. This integration snippet requires an existing `webSocket` and cancellation token `ct`. It starts Bash on Unix.

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

See the [web terminal guide](samples/SingleFiles/README.md#web-terminal) for authentication, browser code, and flow control. See also the [VS Code terminal backend](samples/VsCodeExtension).

Other entry points:

- `PtyWebSocketSessionManager`: reconnectable browser sessions with authentication and replay.
- `PtyStdioBridge`: length-prefixed frames over stdio, for a helper process such as [VsCodeTerminalHelper.cs](samples/SingleFiles/VsCodeTerminalHelper.cs) behind the [VS Code extension sample](samples/VsCodeExtension).
- `PtyTerminal`: a node-pty-style push API with pause/resume flow control, for your own transport.

Protocol and bridging details: [terminal.md](.github/docs/specs/terminal.md).

## Backends

Each OS has its own backend behind the same API. No backend uses winpty or keeps a helper process running beside a session.

### Windows: ConPTY

MiniPty calls Win32 ConPTY (`CreatePseudoConsole`) directly through P/Invoke. It attaches the child with `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE` and resizes with `ResizePseudoConsole`. One background loop waits on MiniPty's I/O completion port and reads output from all sessions. An idle session holds no thread and no output buffer. Applications can supply the out-of-band ConPTY (`conpty.dll` + `OpenConsole.exe`) per session through `PtyStartInfo.ConPty`. See [Out-of-band ConPTY](#out-of-band-conpty-for-windows).

### Linux: `clone`

The small `libminipty_unix` native shim opens the PTY. It starts the child with `clone(CLONE_VM | CLONE_VFORK)`, as `posix_spawn` does, then calls `execve` inside the PTY. Unlike `fork`, this does not copy the host, so startup costs remain the same with a large host heap. Resize uses `TIOCSWINSZ`.

One background loop uses epoll on the exit watcher's thread to read output and write queued input for all sessions. An idle session holds no thread and no output buffer.

### macOS: `posix_spawn` + launcher

MiniPty uses the Unix backend through `libminipty_unix.dylib`. It starts each child through `minipty_spawn_helper`, using `posix_spawn` rather than `forkpty`. This small launcher takes the terminal as its controlling terminal, then replaces itself with the child through `exec`. No helper process remains. Resize uses `TIOCSWINSZ`. Input and output use the same single loop as on Linux, with kqueue instead of epoll.

### FreeBSD (experimental): `fork`

MiniPty uses the Unix backend through `libminipty_unix.so`. Building MiniPty from source on FreeBSD compiles this shim with `cc`. It opens the terminal with close-on-exec and starts each child with `fork`. The child takes the terminal as its controlling terminal and calls `exec`, as on Linux. Input and output use the same single kqueue loop as on macOS.

## Advanced

### Good to know

- **Keep reading output.** An unread PTY fills its buffer and blocks the child. `ReadOutputAsync`, `CompleteAsync`, and the `Output` stream all drain it.
- **Disposal ends the child.** Windows terminates it. Unix sends `SIGHUP` so a shell can save its history, then kills the child if it does not exit within one second ([Session contract](.github/docs/specs/core_session.md#session-contract)).
- **`Environment` overlays the parent's.** A null value removes a variable. On Unix, `TerminalName` sets `TERM`, which defaults to `xterm-256color` ([Spawn environment](.github/docs/specs/core_session.md#spawn-environment)). To keep your process's variables out of the child, see [Environment for shared hosts](#environment-for-shared-hosts).
- **MiniPty is not a sandbox.** The child runs with your permissions. If a program name, path, or argument comes from untrusted input, read [Untrusted program names and paths](.github/docs/specs/core_session.md#untrusted-program-names-and-paths) and [Batch files](.github/docs/specs/core_session.md#batch-files-windows) first.

### Out-of-band ConPTY for Windows

Use the out-of-band ConPTY library for terminal emulators on Windows.

> [!TIP]
> In-box means the ConPTY that comes with Windows itself.

The in-box ConPTY drops graphics protocols (Sixel, Kitty) and some terminal queries. It forwards mouse tracking only to programs that read VT input. Its behavior changes with Windows updates. Windows Terminal ships the ConPTY that Microsoft publishes as `Microsoft.Windows.Console.ConPTY`. This version forwards everything and behaves the same on every Windows build.

Before Windows 11 24H2, the in-box host also has the following differences. Only Windows Server 2022 (build 20348) was measured among those builds ([verification](.github/docs/specs/platform_support.md#verification)):

- It does not forward alternate-screen switches or cursor hiding. Full-screen programs therefore draw on the main screen with the cursor visible.
- After a full-width row, it assumes that pending wrap survives `ESC[K`. xterm clears pending wrap, so the next row starts one column to the left.

MiniPty cannot change either behavior. Terminal emulators on these builds should use the out-of-band ConPTY. Also await `DisposeAsync` before your process exits on these builds. Disposal keeps the in-box host's output open until the host exits, for at most one second. If the output closes too early, the host can run indefinitely.

```shell
# Use the out-of-band ConPTY library, 1.25 or later (older ones can stall three seconds at startup)
dotnet add package Microsoft.Windows.Console.ConPTY --version 1.25.260930003
```

MiniPty's build targets copy `OpenConsole.exe` beside `conpty.dll`. Both run native code with your application's permissions. Store them with the same protection as your application binaries. Update the package to receive security fixes ([details and measurements](.github/docs/specs/core_session.md#out-of-band-conpty)).

```csharp
using MiniPty;

// Null on Unix, or when conpty.dll is not beside your application: the session then uses the in-box ConPTY.
var conpty = ConPtyLibrary.TryLoadFromApplicationDirectory();
if (conpty is { HasConsoleHost: false })
    Console.Error.WriteLine("OpenConsole.exe is missing; sessions fall back to conhost.exe");

await using var session = Pty.Start(new PtyStartInfo { FileName = "cmd.exe", ConPty = conpty });
```

### Windows options

`KillProcessTree = true` ends all processes the child started, including programs with their own console. This applies on disposal, on kill, and when your process crashes. Enable it for commands whose results you collect, such as CI steps, test runners, and agent tool calls. Leave it disabled for interactive terminals where a user-opened window (`code .`) should outlive the tab ([process tree](.github/docs/specs/core_session.md#process-tree)).

`InheritCursor = true` preserves the frontend's screen contents instead of letting ConPTY clear them. The frontend must answer ConPTY's cursor position query, or the child waits ([inherited cursor](.github/docs/specs/core_session.md#inherited-cursor)). Unix ignores both options.

```csharp
await using var session = Pty.Start(new PtyStartInfo
{
    FileName = "cmd.exe",
    Arguments = ["/c", "npm test"],
    KillProcessTree = true,
});
```

### Environment for shared hosts

A web host's environment contains connection strings and cloud credentials that its shells should not receive. `InheritEnvironment = false` gives the child only the required environment: `SystemRoot` on Windows, and `TERM` on Unix. Additional variables reach the child only through `Environment`. Pass the variables your shells need explicitly ([spawn environment](.github/docs/specs/core_session.md#spawn-environment)).

This does not isolate the child. A child running as the same user can still read your process's environment through the OS, such as `/proc/<pid>/environ` on Linux. Run shells for untrusted users as another user or in a container.

```csharp
await using var session = Pty.Start(new PtyStartInfo
{
    FileName = "bash",
    InheritEnvironment = false,
    Environment = new Dictionary<string, string?>
    {
        ["PATH"] = Environment.GetEnvironmentVariable("PATH"),
        ["HOME"] = "/home/tenant",
        ["LANG"] = "C.UTF-8",
    },
});
```

### Metrics

The `MiniPty` meter reports open sessions, start time and failures, output bytes, and stalls caused by a full output buffer. On Unix, it also reports the consumer's native waits for output. Without listeners, MiniPty only tracks the open session count. It takes no timestamps and allocates nothing ([metrics](.github/docs/specs/core_session.md#metrics)).

With OpenTelemetry, add the meter as shown below. [AspireTerminal](samples/AspireTerminal) uses this configuration to export metrics to the Aspire dashboard.

```csharp
builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddMeter("MiniPty"));
```

Use `dotnet-counters` to read the meter from a running process without application configuration:

```shell
dotnet-counters monitor --counters MiniPty --process-id <pid>
```

Use a `MeterListener` to read metrics without OpenTelemetry. [WebTerminalScale](samples/SingleFiles/WebTerminalScale.cs) displays them beside host thread and memory usage. [WinUiTerminal](samples/WinUiTerminal) and [AvaloniaTerminal](samples/AvaloniaTerminal) display them in their status lines.

### GUI hosts

WinUI, WPF, WinForms, and Avalonia can call every MiniPty API on the UI thread without blocking it or requiring marshaling. For full terminal emulators, see [WinUiTerminal](samples/WinUiTerminal) (Windows) and [AvaloniaTerminal](samples/AvaloniaTerminal) (Windows, Linux, macOS). See also [GUI hosts](.github/docs/specs/core_session.md#gui-hosts).

This integration snippet uses your application's `shell`, dimensions, view, and window.


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

## Samples

Each guide explains how to build that kind of application with MiniPty.

| Building | Guide | Package |
|----------|-------|---------|
| A tool that runs commands, drives programs, or records their output | [Single-file samples](samples/SingleFiles/README.md) | MiniPty, MiniPty.Capture |
| A program that connects your terminal to a shell or editor | [ConsoleAttach.cs](samples/SingleFiles/README.md#hand-your-terminal-to-a-program) | MiniPty.Console |
| A full-screen TUI in your current terminal | [TuiDemo](samples/TuiDemo) | MiniPty.Console |
| A terminal window on Windows | [WinUiTerminal](samples/WinUiTerminal) | MiniPty |
| A terminal window on Windows, Linux, and macOS | [AvaloniaTerminal](samples/AvaloniaTerminal) | MiniPty |
| A terminal in the browser (xterm.js) | [Web terminal](samples/SingleFiles/README.md#web-terminal) | MiniPty.Terminal |
| A browser terminal with metrics in the Aspire dashboard | [AspireTerminal](samples/AspireTerminal) | MiniPty.Terminal |
| A VS Code terminal backend | [VsCodeExtension](samples/VsCodeExtension) | MiniPty.Terminal |

Run them locally (JIT):

```bash
dotnet samples/SingleFiles/Session.cs
# open the http://localhost:5170/#token=... address it prints
dotnet samples/SingleFiles/WebTerminal.cs
# interactive terminal
dotnet run --project samples/TuiDemo -c Release
# Windows only
dotnet run --project samples/WinUiTerminal
# Windows, Linux, macOS GUI terminal
dotnet run --project samples/AvaloniaTerminal
# browser terminal and the Aspire dashboard with MiniPty's metrics
dotnet run samples/AspireTerminal/apphost.cs
```

Publish with NativeAOT using the same flags as CI. On Linux and macOS, first run `bash scripts/build-native.sh <rid>` for the target runtime identifier.

```bash
dotnet publish samples/SingleFiles/Session.cs -c Release -r linux-x64 --self-contained true -p:PublishAot=true -p:StripSymbols=true -p:DebugType=None
```

## Development

Use `dotnet` for local development, debugging, or publishing.

### Documentation

- [Specification](.github/docs/spec.md) — API contracts, scope, document map, lessons learned
- [Lifecycle](.github/docs/specs/lifecycle.md) — responsibility split, session flow diagram, cancellation, EOF, drain, disposal
- [Implementation reference](.github/docs/references/pty_crossplatform.md) — ConPTY, Unix process start, EOF staging

### Build

```bash
dotnet build
dotnet test
dotnet pack
```
