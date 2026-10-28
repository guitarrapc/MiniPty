# Windows Host Console Input Reference

Public contract, embedder pattern, and lessons learned: [console.md](../specs/console.md).

This document describes **how** **MiniPty.Console** reads host stdin on Windows. It records empirical platform behavior useful when changing the Windows host-terminal backend.

## Source Layout

| Area | Location |
|---|---|
| Attach orchestration | `src/MiniPty.Console/Internal/PtyConsoleAttach.cs` |
| Host terminal (Windows) | `src/MiniPty.Console/Internal/WindowsHostTerminal.cs` |
| Console P/Invoke | `src/MiniPty.Console/Internal/ConsoleWindowsInterop.cs` |
| VT byte encoding (inject tests) | `src/MiniPty.Console/Internal/WindowsConsoleInputEncoder.cs` |

## Input Path

On VT-aware hosts (Windows Terminal, modern conhost):

1. Save and configure host stdin/stdout modes (`SetConsoleMode`).
2. Enable `ENABLE_VIRTUAL_TERMINAL_INPUT` on stdin.
3. Save the input code page and set it to UTF-8 (`SetConsoleCP(65001)`).
4. Read raw bytes with `ReadFile` on the process stdin handle.
5. Forward bytes to `PtySession.Input` without decoding.

Keyboard input arrives as byte sequences in the console **input code page** (including escape sequences for arrows and function keys). It is UTF-8 only because step 3 sets it: under the default 932 code page, `ReadFile` returned `日` as Shift-JIS `93 FA`, and under 437 `é` as `82`.

The forwarding loop consistently uses `ReadFile`. A `ReadConsoleInput` fallback could discard queued mouse events or take only part of a VT report before the next byte read, causing intermittent lost clicks. Let the console encode its negotiated mouse protocol, rather than interpreting native mouse records in the host. `ReadConsoleInput` / `PeekConsoleInput` remain useful for tests and for a TUI that consumes native input itself, as in [TuiDemo](../../../samples/TuiDemo).

`WaitForSingleObject` on the console input handle does **not** signal key events. The dedicated input thread blocks in `ReadFile`; cancellation interrupts that read. (On conhost, 2026-10-28, the handle did signal for a key injected with `WriteConsoleInput`; the original observation may be host-specific, so do not build on either behavior without checking physical input.)

## Thread Constraint

Microsoft does not document a per-thread rule for VT console input. Empirically, **`SetConsoleMode` and VT `ReadFile` must run on the same thread**.

| Setup | Inject (`WriteConsoleInput` / `WriteFile`) | Physical keyboard (Windows Terminal) |
|---|---|---|
| `SetConsoleMode` on thread A, `ReadFile` on thread B (+ `AttachThreadInput`) | Pass | Fail |
| `SetConsoleMode` + `ReadFile` on one dedicated thread | Pass | Pass |

**MiniPty.Console** runs host terminal setup and the `ReadFile` loop on a dedicated background thread (`MiniPty.Console.Input`). Resize polling runs on a separate task; console handles are process-wide.

## Dispose

`Dispose` cancels the input loop with `CancelIoEx` on stdin, joins the input thread, then restores saved console modes and the input code page.

Known issue: conhost keeps a `ReadFile` cancelled by `CancelIoEx` registered, and that orphaned read consumes the next queued input record without returning it to anyone (reproduced 3/3; exactly one record is lost). After detach, the first key typed into the next attach, or into the embedder's own `Console.ReadKey`, is dropped. Waking the blocked read with an input record instead of cancelling it left no orphaned read.

## NativeAOT Interop

`LibraryImport` must use wide entry points exported from `kernel32.dll`:

- `PeekConsoleInputW`
- `ReadConsoleInputW`
- `WriteConsoleInputW`

Undecorated names are not exported and fail at runtime under NativeAOT publish.

`KEY_EVENT_RECORD` includes `wVirtualScanCode` between `wVirtualKeyCode` and its Unicode character. The character is at byte offset 10, the control-key state at offset 12, and the record occupies 16 bytes. Records cross P/Invoke as raw `INPUT_RECORD*`, so no marshaling runs and the managed in-memory layout must equal the native one. Declare `BOOL bKeyDown` as `int`: a `[MarshalAs(Bool)] bool` occupies 1 byte in memory and shifts the following fields by 2 bytes, putting the character on the scan code. `Marshal.OffsetOf` reports the marshaled layout and still returns 10 in that case; check offsets with pointer arithmetic instead. A test injecting the same incorrectly declared structure as its reader can also conceal a layout defect.

## Testing Notes

Inject-based Windows tests can pass while physical keyboard input still fails. Validate interactive attach with a real TTY (for example `samples/SingleFiles/ConsoleAttach.cs`) after changing this path.

Console-input tests return early when the test host cannot obtain a console (for example when `AllocConsole` fails because one is already attached), so a green `dotnet test` does not prove they ran. Run them on a real console as well, for example by starting the test executable with `Start-Process -WindowStyle Hidden` and `--report-trx`. Lessons from making them pass together in one process (2026-10-28, Windows 11 26200):

- The tests share one console input queue, so they run `NotInParallel`, and each resets the queue first (`WindowsConsoleInputInjector.ResetInputQueue`): a throwaway key absorbs the orphaned read left by an earlier test's detach (see Dispose), then the queue is flushed.
- `WriteFile` on the console input handle fails with `ERROR_INVALID_HANDLE` on conhost, so raw UTF-8 bytes cannot be injected. Inject Unicode key events and start from a legacy input code page to check UTF-8 encoding.
- A child reading with cooked-mode `Console.In.Read` waits for Enter; use `Console.ReadKey` and inject only after the child prints a ready marker.
- Match child output after removing VT sequences: ConPTY emitted `ESC[?9001l ESC[?1004l` between an echoed key and the marker written right after it.

The TUI smoke mode drives both the child and a nested console host through real PTYs and checks colored drawing cells, mouse clicks/releases/wheel input, hit testing after resize, and screen/cursor/mouse-mode cleanup. It passed both before and after the `ReadConsoleInput` fallback was removed: the fallback only consumed records already queued when a read started, a race the smoke does not hit. Native mouse records injected with `WriteConsoleInput` are not encoded into the negotiated mouse protocol by `ReadFile` (conhost encodes mouse input it receives from its window or terminal, not injected records), so the discarded-record path cannot be reproduced by injection. The focused regression instead queues a native mouse record ahead of a key and requires the key to arrive intact. These automated checks do not establish physical-keyboard/mouse behavior in every terminal application.
