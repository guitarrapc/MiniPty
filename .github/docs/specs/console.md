# Console Input Specification

Planned user-facing contract for the **MiniPty.Console** NuGet package.

This package serves **use case 3** in [spec.md](../spec.md): a human operates an interactive program (for example vim) on the **host terminal** while an embedder records or observes the PTY byte stream through **MiniPty** core APIs.

**Status: implemented** (Milestone 5).

## Motivation

PTY transport ([Core session](core_session.md)) moves bytes between parent and child. It does not configure the host terminal or read the user's keyboard.

Interactive recording hosts (for example [scenetake](https://github.com/guitarrapc/scenetake)) need a human to type into a real TUI while the embedder remains the **sole PTY output consumer** ([Lifecycle](lifecycle.md) output exclusivity). **MiniPty.Console** bridges the host terminal to PTY **input** and host terminal **mode**, without reading `PtySession` output APIs.

PTY output **display** on the host and **timestamped recording** stay embedder responsibilities (typically `ReadOutputAsync` plus writing bytes to host `stdout`).

## Package

| Item | Value |
|---|---|
| NuGet | **MiniPty.Console** |
| Depends on | **MiniPty** only |
| NativeAOT | Required (same bar as core) |

## Non-Goals

| ID | Out of scope |
|---|---|
| N1 | Reading PTY output (`ReadOutputAsync`, raw `Output`, `CompleteAsync` output pump) |
| N2 | Writing PTY output to the host display (embedder writes `stdout` after reading the PTY) |
| N3 | Cast / timestamped recording / `MiniPty.Capture` orchestration |
| N4 | Terminal emulation (screen buffer, ANSI parsing, scrollback) |
| N5 | In-editor terminal integration (VS Code / xterm.js backend — separate plan) |
| N6 | Spawning the child (`Pty.Start` remains **MiniPty** core) |
| N7 | Generating or interpreting bracketed-paste sequences |

## Entry Point

v1 exposes one public attach API:

```csharp
IDisposable PtyConsoleInput.Attach(PtySession session)
```

- No options type, callbacks, or `CancellationToken` parameters in v1.
- Stopping host attach and restoring the terminal is **`Dispose`** on the returned handle.

## Attach Contract

### Preconditions

| Rule | Behavior |
|---|---|
| Session state | `Attach` is valid after `Pty.Start` and before `PtySession` disposal. |
| Host TTY | `Attach` **throws** if host stdin **or** host stdout is not a terminal. Interactive attach is for real consoles only; piped or redirected hosts are not supported. |
| Duplicate attach | At most **one** active `PtyConsoleInput` attach per `PtySession`. A second `Attach` throws `InvalidOperationException`. |

### Host terminal modes

On `Attach`, **MiniPty.Console** configures the **host** terminal for TUI passthrough:

- Host **stdin** enters a mode suitable for raw byte input (not line discipline).
- Host **stdout** enters a mode suitable for writing PTY output bytes (including VT sequences) without host-side line editing or spurious translation.

On `Dispose`, saved host terminal state is **restored**.

This package does **not** read from `PtySession` output APIs. Configuring host stdout mode is not the same as becoming a PTY output consumer.

### Input pump

After `Attach`, a background input loop reads **raw bytes** from host stdin and forwards them with `PtySession.WriteInputAsync(ReadOnlyMemory<byte>)`.

- No UTF-8 decoding, `Console.ReadKey`, or line buffering in **MiniPty.Console**.
- Arrow keys, function keys, and paste appear as the host terminal delivers them (often escape sequences as bytes).

### Resize

| Phase | Behavior |
|---|---|
| On attach | Read current host terminal size and call `PtySession.Resize` once to match. |
| While attached | Detect host terminal size changes and call `PtySession.Resize` when columns or rows change. |

Pixel dimensions are not part of v1.

### Concurrent core operations

While attached, core operations follow [Lifecycle](lifecycle.md):

| Operation | Allowed while attached |
|---|---|
| `ReadOutputAsync` (embedder) | Yes — embedder remains the sole output consumer |
| `WaitForExitAsync` | Yes |
| `Resize` (embedder) | Yes (Console also calls `Resize` on host size changes) |
| `WriteInputAsync` / `SendEof` (embedder) | Yes — see below |
| `Kill` / `Dispose` (session) | Yes |

### Embedder writes while attached

`WriteInputAsync` and `SendEof` from the embedder **are allowed** while attached (core concurrent-write rules apply). Ordering between Console-forwarded input and embedder writes is the **embedder's responsibility**.

Typical use case 3 hosts use **Console only** for stdin during human operation.

## Control Characters

Host key chords are forwarded as bytes to the PTY. **MiniPty.Console** does not call `Kill()` or `SendEof()` for these keys.

| Host input | PTY input byte | Notes |
|---|---|---|
| Ctrl+C | `0x03` | Child / line discipline handles SIGINT semantics inside the PTY |
| Ctrl+D | `0x04` | Distinct from `PtySession.SendEof()` (parent-initiated EOF contract) |
| Ctrl+Z | `0x1A` | |
| Paste | Host-delivered bytes | No bracketed-paste generation or interpretation in v1 |

## Dispose

| Item | Contract |
|---|---|
| Recommended order | Dispose **`PtyConsoleInput`** first (stop input pump, restore host terminal), then dispose **`PtySession`**. |
| Session disposed first | Allowed by core but **discouraged**; host terminal may remain in a raw/non-restored state. |
| Returned handle | `IDisposable`; no finalizer reliance for terminal restoration. |

## Platform

Public attach, input, resize, control-character, dispose, and failure behavior are **the same on all supported platforms** (Windows, Linux, macOS, FreeBSD).

Implementation differences (for example `termios` vs Windows Console VT APIs, `SIGWINCH` vs console size polling) belong in implementation notes and **Lessons Learned**, not in separate public contracts per OS.

## Embedder Pattern (Use Case 3)

Typical interactive host flow:

1. `await using var session = Pty.Start(...)`
2. `using var consoleInput = PtyConsoleInput.Attach(session)`
3. Start embedder `ReadOutputAsync` loop: for each chunk, record and write bytes to host `stdout`
4. Wait for session end (`WaitForExitAsync` or application exit condition)
5. On scope exit, `using` disposes `consoleInput` before `await using` disposes `session` (reverse declaration order)

`Attach` returns **`IDisposable`** only in v1; do not use `await using` on the console handle unless a future API adds `IAsyncDisposable`.

Use case 2 (one-shot recorded steps) continues to use [Capture](capture.md) only. Use case 4 (editor terminal backend) uses **MiniPty** core without **MiniPty.Console**.

## Failure Behavior

| Condition | Behavior |
|---|---|
| Host not a TTY | `Attach` throws (for example `InvalidOperationException`). |
| Second attach on same session | `InvalidOperationException`. |
| Session disposed while attached | In-flight Console operations fail per core disposal rules; `Dispose` on the console handle remains safe. |
| Unsupported OS | Same as core: `Pty.Start` fails with `PlatformNotSupportedException` before attach matters. |

## Lessons Learned

- Benchmark allocation comparison must use the benchmark class default `SimpleJob` only; adding `--job short` runs a second job and the compare script prefers `ShortRun`, which reports higher allocations on spawn-heavy benchmarks without any code change.
- CI test hosts are usually non-TTY; `Attach` guard tests rely on `Console.IsInputRedirected` / `IsOutputRedirected`, while duplicate-attach and resize smoke tests skip when redirected.

## Related Documents

- [spec.md](../spec.md) — four use cases and package map
- [core_session.md](core_session.md) — `PtySession`, embedder patterns
- [lifecycle.md](lifecycle.md) — output exclusivity, concurrent operations
- [capture.md](capture.md) — use case 2 (one-shot); not used for interactive attach
- [scenetake spec_pty.md](https://github.com/guitarrapc/scenetake/blob/main/.github/docs/spec_pty.md) — cast integration (scenetake-owned)
