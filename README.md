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
- Scales to thousands of sessions: an idle session holds no thread and no output buffer (one I/O completion port loop on Windows, one epoll/kqueue loop on Unix)
- Idle sessions cost nothing: an idle session holds no thread and no output buffer ([how each OS reads output](#backends))
- Bounded memory with backpressure: output goes into a 16 KiB buffer per session, and a slow consumer pauses the child instead of growing memory
- Allocation-free steady state: reading chunks, writing input, and waiting for exit allocate nothing after the first call
- Signals the right process: on Linux 5.2 and later, `clone` returns a pidfd with the child, and signals go through it rather than through a PID that another process can reuse ([details](.github/docs/references/pty_crossplatform.md#child-exit-observation))
- Observable: a `MiniPty` meter reports open sessions, start time, output bytes, and stalls, and takes no timestamps and allocates nothing while nobody listens ([Metrics](#metrics))

**Not supported**

- Remote shells (`ssh`) or tunneling a PTY over the network
- Full terminal emulation, TUI replay, or faithfully preserving `\r` overwrite lines
- Falling back to pipe redirect when PTY creation fails

## Platforms

MiniPty creates a real PTY on each supported OS. See [Backends](#backends) for how each OS creates the PTY.

| OS | Architectures | Status |
|----|---------------|--------|
| Windows 10 1809+ / Windows 11 / Windows Server 2019+ | x64, arm64 | Supported |
| Linux, glibc 2.27+ | x64, arm64 | Supported |
| Linux, musl 1.2.3+ (Alpine) | x64, arm64 | Supported |
| macOS 12.0+ | x64, arm64 | Supported |
| FreeBSD | — | Experimental: tested neither locally nor in CI, and the package ships no FreeBSD binary, so MiniPty works there only when built from source |

The Linux and macOS minimums are those of the native files. They reach at least as far back as .NET 10 itself runs. Windows needs ConPTY, which first shipped in Windows 10 1809 and Windows Server 2019. .NET 10 also runs on earlier builds, such as Windows 10 1607 and Windows Server 2016; there `Pty.IsSupported` is `false` and `Pty.Start` throws `PlatformNotSupportedException`.

### Native files

Your application carries the following besides the MiniPty assembly. The package and `dotnet publish` place these files for you.

| OS | Files beside the application | Under NativeAOT |
|----|------------------------------|-----------------|
| Windows | None: ConPTY comes with Windows. The [out-of-band ConPTY](#out-of-band-conpty-for-windows) is optional and supplied by your application | None |
| Linux (x64, arm64; glibc and musl) | `libminipty_unix.so` | None: the shim is linked into the executable |
| macOS (x64, arm64) | `libminipty_unix.dylib` and `minipty_spawn_helper` | `minipty_spawn_helper` only: the shim is linked into the executable, and the launcher must stay in the same directory, where MiniPty looks for it |
| FreeBSD (experimental) | `libminipty_unix.so`, which the package does not include: it is built with `cc` when MiniPty is built from source on FreeBSD | Not supported by the package |

## Performance

<!-- benchmark:run:start -->

The numbers below are from [run 42334732063](https://github.com/guitarrapc/MiniPty/actions/runs/42334732063) on 2026-11-07, with .NET 10.0.14 on GitHub Actions hosted runners:

- Linux (`ubuntu-24.04`): Intel Xeon 6973P-C 3.59GHz, 1 CPU, 4 logical and 2 physical cores
- macOS (`macos-26`): Apple M1 (Virtual), 1 CPU, 3 logical and 3 physical cores
- Windows (`windows-2025`): AMD EPYC 7763 2.44GHz, 1 CPU, 4 logical and 2 physical cores

<!-- benchmark:run:end -->

Each table names the report it comes from, the methods it compares, and the columns it quotes, and every value is copied from that report as written, units included. On the run's page, each runner's job summary lists the reports by those names. CI rewrites these tables after every full benchmark run.

Hosted runners get a different CPU from run to run, and the gap between the libraries, even their order, can change with it. Compare the libraries within one run only. The macOS runner swings even on the same CPU: over 20 runs, Porta.Pty's 1 MiB flood ranged from 32.6 to 81.3 MiB/s and MiniPty's from 38.9 to 64.4, so the macOS order follows the run ([platform lessons](.github/docs/specs/platform_support.md#lessons-learned)).

The keystroke and throughput benchmarks keep one child running with raw input, as a shell or an editor runs, so they measure the PTY path and not process startup. [Porta.Pty](https://www.nuget.org/packages/Porta.Pty) 2.2.3 runs the same child for comparison, in its default mode (method `PortaPty`).

**Keystroke round trip** (`PtyLatencyBenchmarks`, job `AllCpus`, columns `P50`, `P95` and `Allocated`): one byte written until the child's echo of it is read.

<!-- benchmark:latency:start -->

| Runner | `ConPty` | `MiniPty` P50 / P95 | `PortaPty` P50 / P95 | Allocated (`MiniPty` / `PortaPty`) |
|--------|----------|---------------------|----------------------|------------------------------------|
| Linux | none | 72.72 μs / 98.06 μs | 93.17 μs / 120.17 μs | 0 B / 680 B |
| macOS | none | 51.67 μs / 93.94 μs | 35.50 μs / 42.39 μs | 0 B / 680 B |
| Windows | out-of-band | 220.7 μs / 262.7 μs | 227.0 μs / 268.3 μs | 0 B / 376 B |
| Windows | in-box | 13,426.9 μs / 20,506.2 μs | 14,426.0 μs / 20,485.1 μs | 0 B / 0 B |

<!-- benchmark:latency:end -->

On Windows each library runs on both ConPTY implementations, the `ConPty` parameter. The in-box ConPTY repaints on its own frame schedule, which sets the round trip for any library; the [out-of-band ConPTY](#out-of-band-conpty-for-windows) removes that wait.

**Output throughput** (`PtyThroughputBenchmarks`, job `AllCpus`, `PayloadBytes` 1048576, columns `MiB/s` and `Allocated`): the child writes 1 MiB of 77-character lines, read until its end marker.

<!-- benchmark:throughput:start -->

| Runner | `ConPty` | `MiniPty` MiB/s | `PortaPty` MiB/s | Allocated (`MiniPty` / `PortaPty`) |
|--------|----------|-----------------|------------------|------------------------------------|
| Linux | none | 295.8 | 256.5 | 0 B / 91565 B |
| macOS | none | 60.8 | 81.4 | 0 B / 492200 B |
| Windows | out-of-band | 3.4 | 3.8 | 12 B / 884948 B |
| Windows | in-box | 2.9 | 2.9 | 0 B / 32 B |

<!-- benchmark:throughput:end -->

ConPTY renders the child's output and sends its own VT stream, so on Windows this is mostly the speed of ConPTY's renderer, not of a byte pipe.

A few bytes in the `Allocated` column are one-time costs of the benchmark process, not of each flood. BenchmarkDotNet counts every thread's allocations over a few operations of a quarter of a second each, so a one-time cost that falls there shows as bytes per operation. Such costs include a thread-pool thread added during the run (about 1 KB), the runtime's tiered compilation, or its first use of a type. Run outside BenchmarkDotNet, the flood allocated nothing per operation on either ConPTY ([platform lessons](.github/docs/specs/platform_support.md#lessons-learned)).

**Session start** (`PtyStartupBenchmarks`, columns `Mean` and `Allocated`): one session from `Pty.Start` to exit, through the OS shell (`cmd.exe /c` on Windows, `sh -c` elsewhere), so the time is mostly process creation. `Session_Exit0_WaitForExit` runs `exit 0` and waits for the exit; `Session_Echo_Bytes` runs `echo` and reads its output to the end.

<!-- benchmark:startup:start -->

| Runner | `Session_Exit0_WaitForExit` Mean / Allocated | `Session_Echo_Bytes` Mean / Allocated |
|--------|----------------------------------------------|---------------------------------------|
| Linux | 522.1 μs / 1.27 KB | 531.6 μs / 1.78 KB |
| macOS | 9.762 ms / 1.27 KB | 7.831 ms / 1.79 KB |
| Windows | 17.29 ms / 1.26 KB | 23.47 ms / 1.86 KB |

<!-- benchmark:startup:end -->

**Session start in a large host** (`PtyStartupHostMemoryBenchmarks`, `HostResidentMiB` 0 and 2048, columns `Mean` and `Allocated`): `Session_Exit0_WaitForExit` while the host holds nothing or 2 GiB of resident memory beside the runtime's own, as an IDE backend or a web terminal may. No platform copies the host to start the child (Linux uses `clone(CLONE_VM | CLONE_VFORK)`, macOS `posix_spawn`, Windows `CreateProcess`), so the two columns should match. This benchmark runs fewer iterations than the one above, so its figure without the host memory can differ from that table's.

<!-- benchmark:startup-host-memory:start -->

| Runner | `HostResidentMiB` 0: Mean / Allocated | `HostResidentMiB` 2048: Mean / Allocated |
|--------|---------------------------------------|------------------------------------------|
| Linux | 517.7 μs / 1.27 KB | 532.3 μs / 1.27 KB |
| macOS | 7.990 ms / 1.27 KB | 6.805 ms / 1.27 KB |
| Windows | 17.10 ms / 1.26 KB | 16.93 ms / 1.26 KB |

<!-- benchmark:startup-host-memory:end -->

The run's reports also include every other benchmark, Porta.Pty's `UseAsyncIo` mode (`PortaPtyAsyncIo`), and 64 KiB floods. On Linux and Windows, each keystroke and throughput case also runs on two of the runner's four logical CPUs (job `TwoCpus`), and those reports add the thread-pool work items (`Completed Work Items`) and, for throughput, the benchmark process's CPU time (`Process CPU`) per operation. Run them locally with `dotnet run -c Release --project src/MiniPty.Benchmarks -- --filter "*PtyLatencyBenchmarks*"`.


## Quick start

```bash
dotnet add package MiniPty            # PTY sessions
dotnet add package MiniPty.Capture    # timestamped output capture
dotnet add package MiniPty.Console    # attach the host terminal (vim, etc.)
dotnet add package MiniPty.Terminal   # backend for xterm.js and editor terminals
```

### MiniPty

Start a child with `Pty.Start`, then either run it for its result with `CompleteAsync` or drive it with `WriteInputAsync` and `ReadOutputAsync`. To run this code: `dotnet samples/SingleFiles/README_MiniPty.cs`.

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

Advanced: [run a command for its result](samples/SingleFiles/README.md#run-a-command-for-its-result) with stdin and EOF, and [drive an interactive program](samples/SingleFiles/README.md#drive-an-interactive-program) that waits for output before it answers.

### MiniPty.Capture

Run a child and get its output, exit code, and every read with its time since `Pty.Start`. To run this code: `dotnet samples/SingleFiles/README_MiniPty.Capture.cs`.

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

Advanced: [capture output with timing](samples/SingleFiles/README.md#capture-output-with-timing), with stdin and a printed timeline.

### MiniPty.Console

Attach the host terminal to a session, so interactive programs such as vim get your keyboard and window size. Reading output stays your job. To run this code: `dotnet samples/SingleFiles/README_MiniPty.Console.cs`, from an interactive terminal.

On Windows, the host console reads input as UTF-8 while attached (the previous input code page is restored on detach), so non-ASCII keys such as Japanese text reach the PTY as UTF-8. Output is still yours: a console with a legacy output code page misdecodes the PTY's UTF-8 bytes, so set `Console.OutputEncoding = Encoding.UTF8` while copying output.

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

Advanced: [hand your terminal to a program](samples/SingleFiles/README.md#hand-your-terminal-to-a-program), which adds status messages and keeps the shell prompt aligned afterwards, and the [TUI demo](samples/TuiDemo) for full-screen programs.

### MiniPty.Terminal

Serve a PTY to xterm.js or an editor terminal, the role node-pty plays behind VS Code. For a complete server, run `dotnet samples/SingleFiles/WebTerminal.cs` and open the address it prints.

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

Advanced: the [web terminal guide](samples/SingleFiles/README.md#web-terminal) (who may connect, the browser side, flow control) and the [VS Code terminal backend](samples/VsCodeExtension).

Other entry points:

- `PtyWebSocketSessionManager`: reconnectable browser sessions with authentication and replay.
- `PtyStdioBridge`: length-prefixed frames over stdio, for a helper process such as [VsCodeTerminalHelper.cs](samples/SingleFiles/VsCodeTerminalHelper.cs) behind the [VS Code extension sample](samples/VsCodeExtension).
- `PtyTerminal`: a node-pty-style push API with pause/resume flow control, for your own transport.

Protocol and bridging details: [terminal.md](.github/docs/specs/terminal.md).

## Backends

Each OS has its own backend behind the same API. None of them uses winpty, and none keeps a helper process running beside a session.

### Windows: ConPTY

MiniPty calls Win32 ConPTY (`CreatePseudoConsole`) directly through P/Invoke, attaches the child with `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE`, and resizes with `ResizePseudoConsole`. One background loop waits on an I/O completion port of MiniPty's own and reads every session's output, so an idle session holds no thread and no output buffer. An application can supply the out-of-band ConPTY (`conpty.dll` + `OpenConsole.exe`) per session with `PtyStartInfo.ConPty`; see [Out-of-band ConPTY](#out-of-band-conpty-for-windows).

### Linux: `clone`

MiniPty uses the small `libminipty_unix` native shim to open the PTY, start the child with `clone(CLONE_VM | CLONE_VFORK)` as `posix_spawn` does, and `execve` it inside the PTY. The host is not copied as a `fork` would copy it, so a start costs the same in a host with a large heap. Resize uses `TIOCSWINSZ`. One background loop (the exit watcher's thread, on epoll) reads every session's output and writes its queued input, so an idle session holds no thread and no output buffer.

### macOS: `posix_spawn` + launcher

MiniPty uses the Unix backend through `libminipty_unix.dylib`, and starts each child through `minipty_spawn_helper` (not `forkpty`): a small launcher that `posix_spawn` starts, which takes the terminal as its controlling terminal and then replaces itself with the child by `exec`, so no helper process stays running. Resize uses `TIOCSWINSZ`. Output and input go through the same one loop as on Linux, on kqueue.

### FreeBSD (experimental): `forkpty`

MiniPty uses the Unix backend through libutil, matching the Linux/macOS PTY lifecycle.

## Advanced Usage

### Good to know

To use MiniPty effectively, keep the following points in mind.

- **Keep reading output.** A PTY nobody reads fills up and blocks the child. `ReadOutputAsync`, `CompleteAsync`, and the `Output` stream all drain it.
- **Disposing ends the child.** Windows terminates it. Unix hangs it up (`SIGHUP`, so a shell saves its history) and kills it if it has not exited a second later ([Session contract](.github/docs/specs/core_session.md#session-contract)).
- **`Environment` overlays the parent's.** A null value removes a variable. On Unix, `TerminalName` sets `TERM`, which defaults to `xterm-256color` ([Spawn environment](.github/docs/specs/core_session.md#spawn-environment)). To keep your process's variables out of the child, see [Environment for shared hosts](#environment-for-shared-hosts).
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

`InheritCursor = true` keeps what the frontend already shows instead of letting ConPTY clear the screen. The frontend must then answer ConPTY's cursor position query, or the child waits ([Inherited cursor](.github/docs/specs/core_session.md#inherited-cursor)). Both options are ignored on Unix.

```csharp
await using var session = Pty.Start(new PtyStartInfo
{
    FileName = "cmd.exe",
    Arguments = ["/c", "npm test"],
    KillProcessTree = true,
});
```

### Environment for shared hosts

A web host's environment holds connection strings and cloud credentials that its shells should not be handed. `InheritEnvironment = false` starts the child from the minimum it needs (`SystemRoot` on Windows, nothing on Unix besides `TERM`), so a variable reaches the child's environment only when `Environment` names it. Pass what your shells need yourself ([Spawn environment](.github/docs/specs/core_session.md#spawn-environment)).

It is not isolation. A child that runs as the same user can still read your process's environment from the operating system, for example from `/proc/<pid>/environ` on Linux. Run shells for untrusted users as another user or in a container.

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

MiniPty reports what a host pays on the output path on a meter named `MiniPty`: open sessions, start time and failures, output bytes, stalls on a full output buffer, and (Unix) a consumer's native waits for output. While nobody listens, MiniPty only keeps the count of open sessions: it takes no timestamps and allocates nothing ([Metrics](.github/docs/specs/core_session.md#metrics)).

With OpenTelemetry, add the meter; [AspireTerminal](samples/AspireTerminal) exports it this way to the Aspire dashboard:

```csharp
builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddMeter("MiniPty"));
```

Without any setup, `dotnet-counters` reads it from a running process:

```shell
dotnet-counters monitor --counters MiniPty --process-id <pid>
```

In code without OpenTelemetry, a `MeterListener` reads it: [WebTerminalScale](samples/SingleFiles/WebTerminalScale.cs) shows them beside its host's threads and memory, and [WinUiTerminal](samples/WinUiTerminal) and [AvaloniaTerminal](samples/AvaloniaTerminal) on their status line.

### GUI hosts

WinUI, WPF, WinForms, and Avalonia can call every MiniPty API on the UI thread. Nothing blocks it, and no marshaling is needed. Full terminal emulators built this way: [samples/WinUiTerminal](samples/WinUiTerminal) (Windows) and [samples/AvaloniaTerminal](samples/AvaloniaTerminal) (Windows, Linux, macOS). See also [GUI hosts](.github/docs/specs/core_session.md#gui-hosts).


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

Each guide shows how to build that kind of application with MiniPty.

| Building | Guide | Package |
|----------|-------|---------|
| A tool that runs commands, drives programs, or records their output | [Single-file samples](samples/SingleFiles/README.md) | MiniPty, MiniPty.Capture |
| A program that hands your terminal to a shell or an editor | [ConsoleAttach.cs](samples/SingleFiles/README.md#hand-your-terminal-to-a-program) | MiniPty.Console |
| A full-screen TUI shown in the terminal you are using | [TuiDemo](samples/TuiDemo) | MiniPty.Console |
| A terminal window on Windows | [WinUiTerminal](samples/WinUiTerminal) | MiniPty |
| A terminal window on Windows, Linux, and macOS | [AvaloniaTerminal](samples/AvaloniaTerminal) | MiniPty |
| A terminal in the browser (xterm.js) | [Web terminal](samples/SingleFiles/README.md#web-terminal) | MiniPty.Terminal |
| A browser terminal whose metrics the Aspire dashboard charts | [AspireTerminal](samples/AspireTerminal) | MiniPty.Terminal |
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

NativeAOT publish, with the same flags as CI (on Linux and macOS, run `bash scripts/build-native.sh <rid>` first):

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
