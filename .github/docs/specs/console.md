# Console Input Specification

Implemented user-facing contract for the **MiniPty.Console** NuGet package.

This package serves **use case 3** in [spec.md](../spec.md): a human operates an interactive program (for example vim) on the **host terminal** while an embedder records or observes the PTY byte stream through **MiniPty** core APIs.

**Status: implemented.**

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
| N5 | In-editor terminal integration (VS Code / xterm.js backend — **MiniPty.Terminal**, [terminal.md](terminal.md)) |
| N6 | Spawning the child (`Pty.Start` remains **MiniPty** core) |
| N7 | Generating or interpreting bracketed-paste sequences |

## Entry Point

v1 exposes attach via two overloads:

```csharp
PtyConsoleInputHandle PtyConsoleInput.Attach(PtySession session)
PtyConsoleInputHandle PtyConsoleInput.Attach(PtySession session, PtyConsoleAttachOptions options)
```

`PtyConsoleAttachOptions`:

| Property | Default | Role |
|---|---|---|
| `SyncHostSize` | `true` | When `true`, apply host terminal size on attach and poll for resize while attached. When `false`, skip initial sync and resize polling so the PTY keeps the size from `Pty.Start` (useful when recording at a fixed geometry). |
| `InputObserver` | `null` | Optional `IPtyConsoleInputObserver` for timestamped host→PTY input bytes. `null` adds no observation overhead beyond a null check in the input pump. |
| `TimeProvider` | `TimeProvider.System` | Clock for observer elapsed times (elapsed since attach). Required only when `InputObserver` is set. |

`PtyConsoleInputHandle` implements **`IDisposable`** and also exposes:

| Method | Role |
|---|---|
| `PumpInputUntil(CancellationToken)` | Preferred: block until the token is canceled (see Input pump). |
| `PumpInputOnce(CancellationToken)` | No-op in v1 (reserved). |

- No callbacks beyond optional `IPtyConsoleInputObserver` on `PtyConsoleAttachOptions` (see Input observer).
- `PumpInputOnce` / `PumpInputUntil` accept an optional `CancellationToken` for cooperative cancel (for example when the child exits).
- Stopping host attach and restoring the terminal is **`Dispose`** on the returned handle.

### Input observer

When `PtyConsoleAttachOptions.InputObserver` is set, **MiniPty.Console** calls `OnForwardedInput` **immediately before** each host stdin read is written to `PtySession.Input`.

| Item | Contract |
|---|---|
| Payload | Raw bytes (same slice forwarded to the PTY; no UTF-8 decoding) |
| `elapsed` | `TimeProvider.GetElapsedTime(attachTimestamp)` — elapsed since attach started |
| Thread | Same thread as the input pump (on Windows, the dedicated input thread) |
| Overhead when `null` | No observer invocation; input pump performs a null check only |

**MiniPty.Console** does not record cast files, normalize typing, or interpret keys. Embedders (for example scenetake) correlate observer timestamps with PTY output chunks.

Observers may receive passwords and other sensitive keystrokes. Embedders must handle observed bytes accordingly.

This package does **not** observe bytes written through embedder `WriteInputAsync` while attached — only host keyboard input forwarded by the Console input pump.

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

After `Attach`, host stdin bytes are forwarded to `PtySession` input.

The public contract is **the same on all platforms**: `Attach` starts an internal background pump; embedders call `PumpInputUntil` only to block until session exit (or another cancel signal). Embedders do **not** read host stdin themselves.

| Platform | Input pump |
|---|---|
| All | A background pump started by `Attach` forwards host stdin bytes to the PTY. |
| `PumpInputUntil` | Blocks until the token is canceled; it does not read input itself. |
| `PumpInputOnce` | No-op in v1 (reserved). |

On Windows, host terminal setup and VT stdin reads must run on the **same** thread. **MiniPty.Console** satisfies this with a dedicated input thread so embedders are not tied to the attach caller thread.

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

Public attach, resize, control-character, dispose, and failure behavior are **the same on all supported platforms** (Windows, Linux, macOS, and the experimental FreeBSD backend).

Input pumping is **started differently** per OS (see Input pump); embedders should call `PumpInputUntil` linked to session exit on every platform.

Implementation differences (for example `termios` vs Windows Console VT APIs, `SIGWINCH` vs console size polling) belong in implementation notes and **Lessons Learned**, not in separate public contracts per OS.

## Embedder Pattern (Use Case 3)

Typical interactive host flow:

1. `await using var session = Pty.Start(...)`
2. Write status to **stderr before** `Attach` (Unix: avoid interleaving stderr with raw host stdout)
3. Start embedder `ReadOutputAsync` pump **before** `Attach` (avoids a full ConPTY pipe during child startup on Windows)
4. `using var consoleInput = PtyConsoleInput.Attach(session)`
5. Link a `CancellationTokenSource` to `WaitForExitAsync` completion; call `consoleInput.PumpInputUntil(token)`
6. Await the output pump and exit task
7. On scope exit, `using` disposes `consoleInput` before `await using` disposes `session` (reverse declaration order)
8. Unix only (optional): after dispose, write `\r\n` to host stdout so the parent shell prompt starts at column 0

On Windows, start the session with `PtyStartInfo.InheritCursor = true` to keep the host terminal's screen. Without it, ConPTY's first output clears the screen, erasing the prompt and everything above it. With it, ConPTY asks for the cursor position, the host terminal answers on stdin, and the input pump forwards the answer ([Inherited cursor](core_session.md#inherited-cursor)). Swap steps 3 and 4 in that case: `Attach` turns on VT processing for host stdout, and a query the output pump writes before then is never answered, so the child waits forever. Attaching first is safe there because ConPTY holds all child output until the answer, so the pipe cannot fill while attaching. [samples/SingleFiles/ConsoleAttach.cs](../../../samples/SingleFiles/ConsoleAttach.cs) follows this order.

`Attach` returns **`PtyConsoleInputHandle`** (`IDisposable`); do not use `await using` unless a future API adds `IAsyncDisposable`.

Use case 2 (one-shot recorded steps) continues to use [Capture](capture.md) only. Use case 4 (editor terminal backend) uses **MiniPty.Terminal** ([terminal.md](terminal.md)) without **MiniPty.Console**.

## Failure Behavior

| Condition | Behavior |
|---|---|
| Host not a TTY | `Attach` throws (for example `InvalidOperationException`). |
| Host terminal configuration fails during `Attach` | `InvalidOperationException`; the session is not left registered for a second attach attempt. |
| Second attach on same session | `InvalidOperationException`. |
| `PumpInputUntil` without a cancelable token | No-op when the token is not cancelable. |
| Session disposed while attached | In-flight Console operations fail per core disposal rules; `Dispose` on the console handle remains safe. |
| Unsupported OS | Same as core: `Pty.Start` fails with `PlatformNotSupportedException` before attach matters. |

## Lessons Learned

- Benchmark allocation comparison must use the benchmark class default `SimpleJob` only; adding `--job short` runs a second job and the compare script prefers `ShortRun`, which reports higher allocations on spawn-heavy benchmarks without any code change.
- CI test hosts are usually non-TTY; `Attach` guard tests rely on `Console.IsInputRedirected` / `IsOutputRedirected`, while duplicate-attach and resize smoke tests skip when redirected.
- On Windows, `WaitForSingleObject` on the console input handle does not signal key events. The dedicated input thread blocks in VT `ReadFile`; detach wakes that read with a sentinel input record. `CancelIoEx` was used until 2026-10-28, but conhost keeps a cancelled read registered and it swallowed the first key after detach (see [windows_console_input.md](../references/windows_console_input.md#dispose)). Do not add a native-record polling fallback that consumes part of the VT stream.
- On Windows Terminal and other VT-aware hosts, physical keyboard input is delivered as UTF-8 bytes via `ReadFile` after enabling `ENABLE_VIRTUAL_TERMINAL_INPUT` on stdin. Microsoft does not document a per-thread console input rule; failures came from splitting `SetConsoleMode` and `ReadFile` across threads. `AttachThreadInput` does not fix VT input in that split. Inject tests (`WriteConsoleInput` / `WriteFile`) can pass while real keyboard input still fails — manual TTY smoke is required on Windows.
- An embedder-thread `PumpInputOnce` loop worked as an interim fix but was rejected as the permanent API because it was asymmetric with Unix and burdened every host. The shipped design uses an internal background pump on all platforms. Also rejected: `AttachThreadInput` with split setup/read; VT-free `ReadConsoleInput`-only (legacy hosts); overlapped `CONIN$` reopen (unnecessary after the dedicated-thread fix).
- Manual validation (2026-06-30): Windows Terminal and Bash (WSL/Linux) with `ConsoleAttach` passed after moving host terminal setup and VT `ReadFile` onto one dedicated input thread.
- NativeAOT `LibraryImport` must name console APIs with their wide entry points (`PeekConsoleInputW`, `ReadConsoleInputW`, `WriteConsoleInputW`); undecorated names are not exported from `kernel32.dll`.
- Start the embedder `ReadOutputAsync` pump before `PtyConsoleInput.Attach` so the child cannot block on a full ConPTY output pipe during startup.
- On Unix, host stdout with `OPOST` off does not map `\n` to `\r\n`; embedders should not write status to stderr after `Attach`, and may emit `\r\n` on stdout after dispose to realign the parent shell prompt.
- Resize polling on a separate task is acceptable on Windows; console handles are process-wide.
- Manual validation (2026-10-18) in a VT terminal on Windows: a ConPTY session started without `InheritCursor` erased the prompt, the command line, and three lines the host had printed; with it they stayed, the terminal's cursor report reached ConPTY through the input pump, and the child exited by itself.
- With `InheritCursor`, the order of output pump and `Attach` decides whether the session starts. Measured the same day with host stdout's VT processing turned off first (as in a console whose shell never enabled it): when the pump wrote ConPTY's cursor query 300 ms before `Attach`, nothing answered and the child was still waiting 8 s later; attaching first, the child ran and exited. Pump-first without a delay also worked, but only because `Attach` won the race.
- Optional `IPtyConsoleInputObserver` uses a null check on the input pump hot path; when unset, forwarding matches direct `PtySession.Input.Write` allocation behavior (verified by `PtyConsoleInputForwardBenchmarks`).

### TUI sample validation lessons (2026-10-28)

- [TuiDemo](../../../samples/TuiDemo) exercises an actual full-screen child both directly and through an attached console host inside an outer PTY. Running a TUI is use case 3; terminal emulation and TUI screen replay remain frontend responsibilities.
- A Windows host copying UTF-8 PTY bytes to a console with a legacy code page can corrupt Japanese text. The sample sets host input/output encodings to UTF-8 before attach and restores them after detach; terminal mode restoration alone does not configure these encodings.
- Input encoding is now the host terminal's job (2026-10-28): `ReadFile` converts console input to the input code page, so under 932 `日` arrived as Shift-JIS `93 FA` and under 437 as `?`, and those bytes were forwarded unchanged to the UTF-8 PTY. `WindowsHostTerminal` sets the input code page to 65001 while attached and restores the snapshot on detach. The output code page stays the embedder's responsibility because MiniPty.Console does not write output.
- Under nested Windows ConPTY, arrow input can reach a .NET TUI as literal VT characters rather than native arrow key events. The sample child handles both representations. `Console.ReadKey` also discards native mouse events, so the mouse-enabled Windows child reads `INPUT_RECORD` data itself, including native mouse coordinates, and decodes SGR reports incrementally.
- The Windows host forwarding loop uses `ReadFile` consistently with VT input enabled. A `ReadConsoleInput` fallback could consume queued mouse records without forwarding them, or consume a portion of a report before `ReadFile` resumed. Reading through the console's VT path preserves the negotiated protocol without adding a host-side mouse parser. The native `KEY_EVENT_RECORD` declaration must include `wVirtualScanCode` before the Unicode character (offset 10) and declare `BOOL bKeyDown` as a 4-byte `int`, because records cross P/Invoke as raw pointers; `Marshal.OffsetOf` reports the marshaled layout and hid a 1-byte `bool` that moved the character onto the scan code. Injected tests using the same incorrect declaration can also mask a layout error. Injected native mouse records are not encoded by `ReadFile`, so the dropped-mouse race is covered only indirectly (see [windows_console_input.md](../references/windows_console_input.md#testing-notes)).
- The colored TUI sample checks diagram hit testing, box highlighting, button press/release behavior, wheel selection, ignored clicks outside controls/in compact layout, and mouse-mode cleanup. Color-cell artwork requires only ANSI colors; terminal text selection remains frontend behavior (often Shift+drag while mouse reporting is active).
- TUI partial rendering belongs to the application: repeated `ESC[2J` followed by many small writes exposes blank frames and causes flicker. The sample caches displayed state, batches updates in a reusable character buffer, and overwrites only affected controls. Its fixed layout needs only a size-label update during normal resize; full clears are limited to initial/layout rebuilds and compact resize. Tests check emitted output before ConPTY, because ConPTY can introduce its own repaint operations.
- Assert the interpreted screen, not raw status strings: ConPTY may send only changed cells. Smoke checks cover Unicode, color, keys, resize, alternate-screen/cursor restoration, final output drain, and normal exit; they do not replace physical-keyboard testing or certify all terminal extensions.

## Related Documents

- [spec.md](../spec.md) — four use cases and package map
- [TuiDemo](../../../samples/TuiDemo) — self-contained interactive TUI and headless PTY/console-host validation
- [core_session.md](core_session.md) — `PtySession`, embedder patterns
- [lifecycle.md](lifecycle.md) — output exclusivity, concurrent operations
- [capture.md](capture.md) — use case 2 (one-shot); not used for interactive attach
- [windows_console_input.md](../references/windows_console_input.md) — Windows host stdin implementation reference (optional)
- [scenetake spec_pty.md](https://github.com/guitarrapc/scenetake/blob/main/.github/docs/spec_pty.md) — cast integration (scenetake-owned)
