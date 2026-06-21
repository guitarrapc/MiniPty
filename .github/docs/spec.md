# MiniPty Specification

Status: **Implemented** (MiniPty 0.3.x, MiniPty.Capture 0.3.x)

User-facing API contracts for the **MiniPty** and **MiniPty.Capture** NuGet packages. OS-level implementation notes live in [references/pty_crossplatform.md](references/pty_crossplatform.md).

## Motivation

Many CLI tools and terminal UIs behave differently when stdout is not a TTY: they disable color, skip animations, or refuse to run. A pseudo-terminal gives the child real terminal semantics while the parent reads and writes a byte stream.

MiniPty exists as a **standalone, NativeAOT-friendly** library so any .NET program—not only scenetake—can spawn PTY children without bundling winpty or external helpers. Recording semantics (per-read timestamps) are split into **MiniPty.Capture** so core callers that only need streams and exit codes are not forced to depend on capture types.

## Scope

### Packages

| Package | Responsibility |
|---|---|
| **MiniPty** | Spawn child in PTY; `Input` / `Output` streams; lifecycle (`WaitForExitAsync`, `CompleteAsync`, `Dispose`) |
| **MiniPty.Capture** | One-shot `PtyCapture.RunAsync` → merged output, exit code, and timestamped `Chunks` |

Timestamped chunks are **not** part of the core API. Consumers that need cast timelines (e.g. [scenetake](https://github.com/guitarrapc/scenetake)) take a dependency on both packages.

### In scope (current)

| Goal | Examples |
|---|---|
| Child sees a TTY; TUI output is readable | Short `matrix` / `cmatrix` runs |
| Spawn with executable, arguments, cwd, terminal size | Shell one-liners, `echo`, `Write-Output` |
| Optional one-shot stdin with EOF | `cat`, `sort`, pipelines through a shell |
| Terminal resize after spawn | `PtySession.Resize` |
| Cooperative vs forced shutdown | `WaitForExitAsync` (cancel = stop waiting) vs `CompleteAsync` (cancel may kill) |

### Out of scope (for now)

- Long-lived interactive sessions (vim, less, REPLs)
- Bidirectional input beyond optional initial stdin text
- Remote shells (`ssh`)
- Spilling capture to disk when memory is exhausted
- Capture tuning (`TimeProvider`, max chunk size, chunk timestamp modes)

## Platform Support

| OS | Backend | Minimum |
|---|---|---|
| Windows | ConPTY (`CreatePseudoConsole`) | Windows 10 1809+, Windows 11 |
| Linux | `openpty` + `fork` + `execvp` | Common glibc/musl targets |
| macOS | `openpty` + `fork` + `execvp` | Supported runners (BSD `TIOCSCTTY`, `libutil`) |
| FreeBSD | `openpty` + `fork` + `execvp` | `libutil` + BSD `TIOCSCTTY` |

Pipe redirect **without** ConPTY is not a PTY. On Windows, TUI tools require ConPTY-backed spawn.

MiniPty does **not** use winpty or third-party PTY binaries. Backends are in-process `[LibraryImport]` only.

## Core API (`MiniPty`)

### Entry: `Pty.Start(PtyStartInfo)` → `PtySession`

Spawns a child attached to a new pseudo-terminal. Does **not** wait for exit.

`PtyStartInfo` supplies:

- `FileName` — executable path or name (required)
- `Arguments` — argv after the executable (default empty)
- `WorkingDirectory` — optional; inherits parent when null
- `Size` — initial columns × rows in character cells (default 80×24; clamped to 1–512 per dimension at spawn)

### Session contract

| Member | Behavior |
|---|---|
| `Input` / `Output` | Byte streams; no line-ending translation; stdout and stderr merged on `Output` |
| `WriteInputAsync` | Write UTF-8 (default) or raw bytes; does not close stdin |
| `SendEof()` | End stdin (platform-specific; see reference doc) |
| `Resize(PtySize)` | Resize terminal after spawn |
| `WaitForExitAsync` | Wait for child exit; **cancellation stops waiting only—the child keeps running** |
| `CompleteAsync` | Pump output, optional stdin, wait, drain, return `PtyResult` (no timestamps) |
| `Kill()` | `TerminateProcess` / `SIGKILL`; does not release handles |
| `HasExited` / `ExitCode?` | Poll exit; `ExitCode` is null until exited |
| `Dispose` / `DisposeAsync` | **Kill child if still running**, then release handles |

### `PtyCompleteOptions`

Used by `CompleteAsync`. `MiniPty.Capture` composes the same type via `PtyCaptureOptions.Completion`.

| Option | Default | Purpose |
|---|---|---|
| `OutputEncoding` | UTF-8 | Decode PTY bytes to text |
| `Input` | null | Stdin text; null = leave open (TUI); `""` = EOF with no bytes |
| `SendEofAfterInput` | true | Call `SendEof` after writing `Input` |
| `ExitTimeout` | null | Max wait for child exit; null = wait until exit or cancel |
| `OutputDrainGrace` | 1s | Drain after exit before closing transport |
| `OutputReaderCloseTimeout` | 5s | Wait for reader after transport close |
| `KillOnCancellation` | true | **CompleteAsync only** — cancel kills child |

### Backpressure

A PTY has backpressure. If the child writes output and nothing reads `PtySession.Output`, the child may block when the terminal buffer fills. Callers must use `CompleteAsync`, `PtyCapture.RunAsync`, or continuously read `Output`.

## Capture API (`MiniPty.Capture`)

```csharp
PtyCaptureResult result = await PtyCapture.RunAsync(startInfo, options);
// result.Output   — merged text (concatenation of chunk data)
// result.ExitCode
// result.Chunks   — PtyCaptureChunk(TimeSpan Time, string Data)
```

- `PtyCaptureOptions.Completion` wraps `PtyCompleteOptions`.
- Each chunk's `Time` is elapsed since **session start** (immediately after `Pty.Start`).
- The session is disposed when `RunAsync` completes (child killed on dispose if still running).

PTY output is a **raw byte stream**. MiniPty does not normalize newlines or parse ANSI; sequences may span chunk boundaries.

## Cancellation

| API | On cancel |
|---|---|
| `WaitForExitAsync` | Waiting stops (`OperationCanceledException`); child **continues** |
| `CompleteAsync` | When `KillOnCancellation` is true (default), child is killed, then `OperationCanceledException` |
| `PtyCapture.RunAsync` | Same as `CompleteAsync` (uses completion options) |

This split lets embedders observe cancellation without tearing down long-running children, while one-shot capture runs default to killing on cancel.

## Failure Behavior

| Condition | Behavior |
|---|---|
| Unsupported OS | `PlatformNotSupportedException` from `Pty.Start` |
| Spawn / ConPTY / `openpty` failure | OS exception with error code (run aborts) |
| Child non-zero exit | Returned as `ExitCode`; not thrown by MiniPty |
| `ExitTimeout` exceeded | `TimeoutException` |
| Output drain / reader close timeout | `TimeoutException` |
| `Dispose` while child running | Child killed |

MiniPty does not fall back to pipe redirect when PTY creation fails.

## Verification

CI runs `tests/MiniPty.Tests` on Linux, macOS, and Windows:

| Check | Why |
|---|---|
| TTY detection (`redirected=False`, `isatty`) | Confirms real PTY semantics |
| Simple command output | Confirms spawn + decode |
| Stdin + EOF (`cat`, `sort`) | Confirms staged EOF |
| Multiple capture chunks + ANSI | Confirms concurrent read + timestamps |
| `WaitForExitAsync` cancel vs `CompleteAsync` cancel | Confirms cancellation contract |
| Resize | Confirms `TIOCSWINSZ` / `ResizePseudoConsole` |

Tests use property assertions, not golden byte captures, to reduce OS/timing flake.

[scenetake](https://github.com/guitarrapc/scenetake) adds end-to-end fixture scenarios (`tests/fixtures/pty-*.yaml`) via `SCENETAKE_BIN`; that layer is documented in scenetake's [spec_pty.md](https://github.com/guitarrapc/scenetake/blob/main/.github/docs/spec_pty.md).

## Lessons Learned

These constraints shaped the API and backends; details are in the reference doc.

- **Pipe redirect is not a PTY.** Redirected stdin/stdout captures bytes but children report "not a TTY". Tools like `matrix` skip rendering on Windows without ConPTY.
- **ConPTY pipes are transport, not the terminal.** `HPCON` is the pseudo-console; mishandling pipe ends after `CreatePseudoConsole` causes missing output, hangs, or leaks to the parent console.
- **winpty is a poor fit for NativeAOT single-binary goals.** Bundled helpers add environment dependency; in-process ConPTY avoids that.
- **Child stdin must not be closed before launch completes (Windows).** Early ConPTY input pipe close yields `STATUS_CONTROL_C_EXIT` (0xC000013A). EOF is always staged to the first wait poll or transport close.
- **Unix PTY master fds cannot be half-closed.** `SendEof()` writes EOT (`0x04`); closing the master ends both read and write. EOT is reliable only in canonical line discipline—not for raw-mode TUIs or REPLs.
- **Fork before exec must stay async-signal-safe.** Build `argv` / `cwd` in the parent with `NativeMemory`; the child only calls libc before `execvp`.
- **Capture timing requires concurrent reads.** Reading only after exit loses TUI animation timing; failing to attach ConPTY loses content entirely.
- **Output drain after child exit needs bounded waits.** `OutputDrainGrace` then transport close, then `OutputReaderCloseTimeout`, prevents hung readers without dropping slow flushes.
- **Session vs capture split reduced API awkwardness.** A blocking `Run` that mixed streams, timestamps, and cancellation forced `GetAwaiter().GetResult()` in tests; `PtySession` + optional Capture package keeps async paths natural.
- **Cancel semantics differ by use case.** Library callers needed `WaitForExitAsync` that does not kill on cancel; one-shot capture defaults to killing so hung children do not leak.

## Related Documents

- [spec_index.md](spec_index.md) — document map
- [references/pty_crossplatform.md](references/pty_crossplatform.md) — ConPTY, `openpty`, EOF staging, interop
- [README.md](../../README.md) — quick-start examples
- [scenetake spec_pty.md](https://github.com/guitarrapc/scenetake/blob/main/.github/docs/spec_pty.md) — `pty: true` YAML and cast integration
