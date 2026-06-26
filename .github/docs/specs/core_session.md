# Core Session Specification

Implemented user-facing contract for the **MiniPty** core session API.

## Entry Point

`Pty.Start(PtyStartInfo)` spawns a child process attached to a new pseudo-terminal and returns a `PtySession`. It does **not** wait for the child to exit.

`PtyStartInfo` supplies:

| Member | Behavior |
|---|---|
| `FileName` | Executable path or name. Required. |
| `Arguments` | Arguments after the executable. Default is empty. |
| `WorkingDirectory` | Optional child working directory. Null inherits the parent working directory. |
| `Size` | Initial terminal columns x rows. Default is 80x24; values are clamped to 1-512 per dimension at spawn. |
| `Environment` | Optional child environment overlay. Null inherits the parent environment. Non-null entries override or remove inherited variables. |
| `TerminalName` | Optional Unix terminal name for `TERM`. Null or empty uses default behavior. Currently ignored on Windows. |

## Spawn Environment

`PtyStartInfo.Environment` is an overlay on top of the parent process environment, not a replacement block.

| Value | Behavior |
|---|---|
| `Environment = null` | Inherit the parent environment. |
| `Environment["KEY"] = "value"` | Set or override `KEY` for the child. |
| `Environment["KEY"] = null` | Remove `KEY` from the child environment. Removing a missing key succeeds. |
| `Environment["KEY"] = ""` | Set an empty value on platforms that preserve empty environment variables. Windows children observe empty entries as missing. |

Environment keys are case-insensitive on Windows and case-sensitive on Unix. Invalid names are rejected at spawn time: empty names, names containing `=`, and names or values containing NUL are invalid. Duplicate-equivalent overlay keys are resolved by enumeration order; the last value wins.

On Unix, MiniPty removes inherited terminal-container and size variables before applying the explicit overlay: `TMUX`, `TMUX_PANE`, `STY`, `WINDOW`, `WINDOWID`, `TERMCAP`, `COLUMNS`, and `LINES`. This prevents a fresh PTY from inheriting stale parent terminal state. Because sanitize runs before the overlay, callers can still explicitly pass any of these variables.

On Unix, `TerminalName` sets `TERM` after the environment overlay. If `TerminalName` is null or empty and no `TERM` remains, MiniPty sets `TERM=xterm-256color` unless `TERM` was explicitly removed by the overlay. `Environment["TERM"] = ""` is respected as an explicit empty value on Unix. On Windows, `TerminalName` does not set `TERM`; use `Environment["TERM"]` to pass it explicitly.

MiniPty environment inheritance follows normal process-spawn behavior and is not a sandbox. Callers exposing PTYs to untrusted users must isolate the child process outside MiniPty, for example with OS users, containers, sandboxing, or explicit environment removal.

## Session Contract

| Member | Behavior |
|---|---|
| `Input` / `Output` | Raw byte streams. No line-ending translation. stdout and stderr are merged on `Output`. |
| `ReadOutputAsync` | Persistent bytes-only output streaming. Returns `IAsyncEnumerable<PtyOutputChunk>`. |
| `WriteInputAsync` | Writes UTF-8 text by default, or raw bytes. Does not close stdin. |
| `SendEof()` | Signals end of stdin using platform-specific behavior. See [Lifecycle](lifecycle.md). |
| `Resize(PtySize)` | Resizes the terminal after spawn. |
| `WaitForExitAsync` | Waits for child exit. Cancellation stops waiting only; the child keeps running. |
| `CompleteAsync` | Convenience API for one-shot input, wait, drain, and result materialization. See [Completion](completion.md). |
| `Kill()` | Terminates the child process without releasing handles. |
| `HasExited` / `ExitCode?` | Polls exit state. `ExitCode` is null until the child has exited. |
| `Dispose` / `DisposeAsync` | Kills the child if still running, then releases handles. |

## Backpressure

A PTY has backpressure. If the child writes output and nothing reads PTY output, the child may block when the terminal buffer fills. Callers must use `ReadOutputAsync`, `CompleteAsync`, `PtyCapture.RunAsync`, or continuously read `Output` themselves.

`ReadOutputAsync` is the supported high-level persistent output API. It is bytes-only and performs no text decoding.

`PtyOutputChunk.Data` is ephemeral: it is valid only until the next successful `MoveNextAsync` call on the same enumeration. Callers that need to retain bytes must copy them before advancing the reader.

Only one active `ReadOutputAsync` reader is allowed per session. A concurrent reader attempt throws `InvalidOperationException`. The existing `Output` stream remains available as the low-level stream API, but callers should not read `Output` concurrently with `ReadOutputAsync`.

`ReadOutputAsync` uses strict consumer handoff and does not drop data. The producer reads only when the consumer is waiting, may coalesce multiple transport reads into one handoff slice (up to the producer read buffer size), and blocks until `Advance`. Coalescing stops before blocking for bytes that are not yet available so interactive output is not delayed to fill a buffer. Backpressure therefore appears as handoff wait and, when the consumer stops reading, OS PTY pipe fill. Chunk delivery is bounded by the producer read buffer; the maximum per-chunk size is an implementation detail and may change.

## Lessons Learned

- Environment overlay is safer for MiniPty than node-pty-style replacement because Windows child startup is fragile when inherited variables such as `SystemRoot` are accidentally omitted.
- Unix terminal-size variables such as `COLUMNS` and `LINES` can make a fresh PTY behave like the parent terminal. Sanitizing them before overlay avoids stale child-visible terminal state.
- Windows does not preserve empty environment variables as child-visible empty values. MiniPty keeps the API distinction so Unix can express empty values, but Windows children observe them like missing variables.
- A fixed public buffer capacity is a poor contract for `ReadOutputAsync`: it turns an allocation/backpressure tuning knob into observable API surface. The stable contract is bounded no-drop streaming with producer wait; capacity should remain internal unless a future options API exposes it deliberately.
- On Windows ConPTY, `PeekNamedPipe` often reports zero pending bytes even when more output is in flight. `ReadOutputAsync` coalescing therefore uses non-blocking continuation reads (`PIPE_NOWAIT`) and a short micro-window before handing off a partial buffer, so bulk output batches without delaying the first byte of interactive output.
