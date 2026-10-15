# Core Session Specification

Implemented user-facing contract for the **MiniPty** core session API.

## Entry Point

`Pty.Start(PtyStartInfo)` spawns a child process attached to a new pseudo-terminal and returns a `PtySession`. It does **not** wait for the child to exit.

`PtyStartInfo` supplies:

| Member | Behavior |
|---|---|
| `FileName` | Executable path or name. Required. |
| `Arguments` | Arguments after the executable. Default is empty. |
| `CommandLine` | Optional pre-escaped Windows argument command line. Mutually exclusive with `Arguments`; unsupported on Unix. |
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
| `Input` / `Output` | Raw byte streams. No line-ending translation. stdout and stderr are merged on `Output`. `Output` is a view over the session's output buffer: `ReadAsync` never blocks the calling thread; `Read` blocks, per the `Stream` contract. |
| `ReadOutputAsync` | Persistent bytes-only output streaming. Returns `IAsyncEnumerable<PtyOutputChunk>`. |
| `WriteInputAsync` | Writes UTF-8 text by default, or raw bytes. Does not close stdin. |
| `SendEof()` | Signals end of stdin using platform-specific behavior. See [Lifecycle](lifecycle.md). |
| `Resize(PtySize)` / `Resize(PtySize, PtyPixelSize?)` | Resizes after spawn; Unix also reports optional pixel dimensions, while Windows ignores pixels. Child exit never makes it fail: while the transport is open the size is applied, and once the transport has been closed after exit the call is a no-op that keeps `Size`. Only disposal (`ObjectDisposedException`), invalid pixel dimensions, and an OS failure while the child is running throw. |
| `WaitForExitAsync` | Waits for child exit. Cancellation stops waiting only; the child keeps running. |
| `WaitForExitStatusAsync` | Same wait/cancellation semantics as `WaitForExitAsync`; returns `PtyExitStatus` (exit code plus Unix termination signal). |
| `CompleteAsync` | Convenience API for one-shot input, wait, drain, and result materialization. See [Completion](completion.md). |
| `Kill()` | Terminates the child process without releasing handles. |
| `Kill(PtySignal)` | Sends a signal on Unix (`kill(2)`; the child may handle or ignore catchable signals). On Windows the signal is advisory and the child is terminated (node-pty parity). Undefined enum values throw `ArgumentOutOfRangeException` on both platforms. |
| `HasExited` / `ExitCode?` | Reports the exit observed by the process-wide exit watcher; reading it never blocks or reaps. `ExitCode` is null until the child has exited. |
| `ExitStatus?` | `PtyExitStatus` after exit; null before. |
| `ActiveProcessName` | Foreground process name for editor title polling on Unix; null when unavailable and on Windows. |
| `Dispose` / `DisposeAsync` | Kills the child if still running and releases handles without blocking; in-flight output reads and exit waits fail with `ObjectDisposedException`. `DisposeAsync` also completes once the background output reader has stopped. Output chunk memory is invalid after either. |

## Exit Status

`PtyExitStatus` reports `ExitCode` and `Signal`:

- `ExitCode` always equals `PtySession.ExitCode`. On Unix a signal-terminated child keeps MiniPty's existing `128 + signal` mapping (for example 143 for SIGTERM) — deliberately not node-pty's 0-on-signal, so a killed child never reads as success and there is one exit-code story across the library.
- `NodePtyExitCode` projects that status into node-pty's frontend shape: zero when `Signal` is present, otherwise `ExitCode`. Terminal bridges use this projection without changing core completion semantics.
- `Signal` is the raw OS signal number (`waitpid` `WTERMSIG`), null on normal exit and when the wait status was lost (ECHILD reap-lost path). Windows never reports a signal.
- `PtySignal` members are logical identifiers mapped to native numbers per platform (SIGUSR1/SIGUSR2 numbering differs between Linux and macOS/FreeBSD); reporting uses raw OS numbers.

## Embedder Patterns

MiniPty core is the PTY transport for multiple embedder shapes ([spec.md](../spec.md)). The core API is unchanged; patterns differ by who reads output and who writes input.

| Use case | Output consumer (exactly one) | Input source | Packages |
|---|---|---|---|
| **1 — General transport** | Embedder (`ReadOutputAsync` or raw `Output`) | Embedder (`WriteInputAsync`) | **MiniPty** |
| **2 — One-shot record** | `PtyCapture.RunAsync` (completion sink on the reader thread) | `PtyCompleteOptions` / capture options | **MiniPty.Capture** |
| **3 — Interactive host** | Embedder `ReadOutputAsync` (record + write host `stdout`) | **MiniPty.Console** host keyboard + optional embedder writes | **MiniPty** + **MiniPty.Console** |
| **4 — Editor terminal** | **MiniPty.Terminal** `PtyTerminal` (owns `ReadOutputAsync`) | Frontend key events → bridge → `WriteInputAsync` | **MiniPty** + **MiniPty.Terminal** |

**Use case 3** does not add a second PTY output reader. [Console](console.md) configures the host terminal and forwards host stdin bytes; the embedder keeps the single `ReadOutputAsync` enumeration for PTY → host display and recording.

**Use case 4** does not use **MiniPty.Console** (no host console attach). The push facade, flow control, and WebSocket bridge live in [Terminal](terminal.md).

## Backpressure

A PTY has backpressure. Each session reads its output from spawn on a dedicated background thread into a bounded buffer, and the session's single output consumer drains it. When the buffer is full, reading stops and the OS PTY buffer fills, so a child whose output nobody reads still blocks eventually. Callers must use `ReadOutputAsync`, `CompleteAsync`, `PtyCapture.RunAsync`, or continuously read `Output` themselves.

Reading from spawn means early output is never stranded: a child does not stall on its first writes before a consumer attaches, and output produced before the consumer starts is delivered first.

`ReadOutputAsync` is the supported high-level persistent output API. It is bytes-only and performs no text decoding. It does not drop data.

`PtyOutputChunk.Data` is ephemeral: it is valid only until the next successful `MoveNextAsync` call on the same enumeration, or until the session is disposed. Callers that need to retain bytes must copy them before advancing the reader. Chunks are handed out in place from the buffer and the enumerator is reused per session, so enumeration allocates nothing per chunk.

Only one output consumer is active per session (see [Lifecycle](lifecycle.md#output-consumer-exclusivity)). A concurrent reader attempt throws `InvalidOperationException`.

Managed buffering is bounded by the buffer's slot count times its slot size (currently 4 × 4 KiB). Each chunk holds one transport read plus the output that arrives within a short micro-window after it, so bulk output arrives in few full chunks without delaying the first byte of interactive output. The slot geometry and the maximum per-chunk size are implementation details and may change.

## Caller Context

Asynchronous members never block the calling thread and never run library code on the caller's `SynchronizationContext`, so they can be awaited from a UI thread. Only the caller's own continuation resumes on its captured context, and only when the operation had to wait; `ConfigureAwait(false)` opts out as usual.

| Member | Behavior on the caller's thread |
|---|---|
| `ReadOutputAsync`, `Output.ReadAsync` | Complete synchronously while output is buffered; otherwise resume when output arrives. |
| `WaitForExitAsync`, `WaitForExitStatusAsync` | Await the exit watcher's notification. |
| `CompleteAsync`, `PtyCapture.RunAsync` | Output is collected on the session's reader thread; the caller only awaits exit and end of output. |
| `DisposeAsync` | Returns at once and completes when the reader has stopped. |
| `Output.Read`, `Input.Write` | Block, per the `Stream` contract. |
| `WriteInputAsync` | Still writes synchronously: a full Unix tty input queue can block it (planned in [GUI host readiness](../plans/plan_gui_host_readiness.md), G1 step 4). |

## Lessons Learned

- The textbook glibc `WIFSIGNALED` macro relies on a signed-char cast that a direct C# transcription loses, misclassifying the stopped marker `0x7f` as signaled. Unreachable under WNOHANG-only polling, but load-bearing once the termination signal became public API; the managed decode uses the explicit `(status & 0x7f) != 0 && (status & 0x7f) != 0x7f` form. The exited/signaled wait-status layout is identical on Linux, macOS, and FreeBSD, so decoding stays managed with no native shim involvement.
- Environment overlay is safer for MiniPty than node-pty-style replacement because Windows child startup is fragile when inherited variables such as `SystemRoot` are accidentally omitted.
- Unix terminal-size variables such as `COLUMNS` and `LINES` can make a fresh PTY behave like the parent terminal. Sanitizing them before overlay avoids stale child-visible terminal state.
- Windows does not preserve empty environment variables as child-visible empty values. MiniPty keeps the API distinction so Unix can express empty values, but Windows children observe them like missing variables.
- A fixed public buffer capacity is a poor contract for `ReadOutputAsync`: it turns an allocation/backpressure tuning knob into observable API surface. The stable contract is bounded no-drop streaming with producer wait; capacity should remain internal unless a future options API exposes it deliberately.
- On Windows ConPTY, `PeekNamedPipe` often reports zero pending bytes even when more output is in flight. The output reader therefore appends to a slot with non-blocking continuation reads (`PIPE_NOWAIT`) within a short micro-window before publishing it, so bulk output batches without delaying the first byte of interactive output.
- Kill-on-cancellation must not rely on a token registration made alongside `WaitAsync(token)`. `CancellationTokenSource` runs callbacks in reverse registration order, so `WaitAsync`'s callback resumes the waiter first, and the waiter's cleanup unregisters the kill callback before it runs. The waiter kills in its own cancellation handler instead.
- A consumer wait must be completed outside the lock that guards its state. Completing it under the lock runs the consumer's continuation inline, and that continuation's `CancellationTokenRegistration.Dispose` blocks until a concurrently running cancellation callback returns, while the callback blocks on the lock. The output pump claims a wait with a compare-and-swap and completes it without holding any lock.
- Resize must tolerate child exit. GUI hosts forward layout changes asynchronously to child exit, so a size change can arrive after post-exit drain has closed the transport; throwing there terminated WinUI hosts from a size-changed handler. Defining the no-op by transport state rather than by `HasExited` keeps resize-after-fast-exit working while the transport is still open, and on Windows a reference on the pseudo-console handle keeps a concurrent drain close from invalidating it mid-call.
- Pixel dimensions fit Unix's 16-bit `winsize` fields. MiniPty clamps larger caller values to `ushort.MaxValue`; zero remains a valid "unknown" pixel dimension.
- Foreground process lookup belongs behind the existing Unix native boundary: `tcgetpgrp` supplies the foreground process group without spawning `ps`, preserving NativeAOT and avoiding a helper process on each editor poll. A process-group id is not always a live process id: after a pipeline leader exits, lookup must fall back to a remaining group member rather than reporting no active process.
- Output must be read on a thread the library owns. The former producer started on the caller's captured context (through `Task.Yield`) and then blocked inside `ReadFile` / `read(2)`, which deadlocked a UI thread before the first chunk and froze it on every idle read; every idle session also parked a thread-pool worker. A per-session reader thread that completes allocation-free waits, whose continuations run asynchronously, removes all three.
- Reuse reader threads across sessions. A bare thread start allocates 137 bytes, and one pooled-array return on a fresh thread raised that to about 4.9 KB: the array pool allocates a per-thread cache on the first return, and the returned array is stranded in that cache when the thread exits. One-shot completion grows its result buffers on the reader thread, so a thread per session would pay this on every spawn. Parked reader threads serve later sessions and exit after 30 seconds idle.
- One-shot completion writes into a sink on the reader thread instead of draining the buffer from a consumer task. With a bounded buffer, a consumer that only started draining after exit would block a bulk-output child before it could exit. Writing on the reader thread needs no consumer thread and removed the pump task, the polling drain, and the timeout tasks: about 0.7 KB less per `CompleteAsync`.
- Coalescing belongs to the reader, so every consumer gets it. Capture used to record one chunk per transport read; slots that keep appending within the micro-window cut a 32 KiB capture's chunks and the metadata they cost (about 5 KB less on Windows).
- A Unix reader cannot be stopped by closing the master: `close(2)` does not interrupt a `read` or `poll` blocked on the descriptor in another thread, and the number can be reused while that thread still uses it. The reader waits on the master and a wake descriptor together, and the master is reference-counted so it closes only after the reader, input writes, resize, and EOT writes release it. This also resolves the resize race in which a concurrent drain closed the master during the `ioctl`.
- Let go of pooled resources before publishing completion. The reader first signaled end of output and only then dropped its reference to the buffer, so a consumer that disposed at once sometimes left the final release to the reader thread. The array then went back to the pool from that thread and stayed in its per-thread cache, and the next session's rent allocated a fresh 16 KiB array: the streaming benchmark intermittently averaged 7.2 KB instead of 1.7 KB. Releasing the buffer and parking the reader thread before completion becomes observable makes both deterministic. For the same reason a one-shot sink finishes its result (decoder flush, buffer detach, scratch return) on the reader thread at end of output: detaching on the thread that resumed the caller left grown result buffers in a worker's cache, and the next run's growth on the reader thread allocated again (intermittent 7 KB and 119 KB averages on the one-shot benchmarks, also seen before this change).
