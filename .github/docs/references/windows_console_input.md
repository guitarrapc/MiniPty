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
3. Read raw bytes with `ReadFile` on the process stdin handle.
4. Forward bytes to `PtySession.Input` without decoding.

Physical keyboard input arrives as UTF-8 byte sequences (including escape sequences for arrows and function keys).

The forwarding loop consistently uses `ReadFile`. A `ReadConsoleInput` fallback could discard queued mouse events or take only part of a VT report before the next byte read, causing intermittent lost clicks. Let the console encode its negotiated mouse protocol, rather than interpreting native mouse records in the host. `ReadConsoleInput` / `PeekConsoleInput` remain useful for tests and for a TUI that consumes native input itself, as in [TuiDemo](../../../samples/TuiDemo).

`WaitForSingleObject` on the console input handle does **not** signal key events. The dedicated input thread blocks in `ReadFile`; cancellation interrupts that read.

## Thread Constraint

Microsoft does not document a per-thread rule for VT console input. Empirically, **`SetConsoleMode` and VT `ReadFile` must run on the same thread**.

| Setup | Inject (`WriteConsoleInput` / `WriteFile`) | Physical keyboard (Windows Terminal) |
|---|---|---|
| `SetConsoleMode` on thread A, `ReadFile` on thread B (+ `AttachThreadInput`) | Pass | Fail |
| `SetConsoleMode` + `ReadFile` on one dedicated thread | Pass | Pass |

**MiniPty.Console** runs host terminal setup and the `ReadFile` loop on a dedicated background thread (`MiniPty.Console.Input`). Resize polling runs on a separate task; console handles are process-wide.

## Dispose

`Dispose` cancels the input loop with `CancelIoEx` on stdin, joins the input thread, then restores saved console modes.

## NativeAOT Interop

`LibraryImport` must use wide entry points exported from `kernel32.dll`:

- `PeekConsoleInputW`
- `ReadConsoleInputW`
- `WriteConsoleInputW`

Undecorated names are not exported and fail at runtime under NativeAOT publish.

`KEY_EVENT_RECORD` includes `wVirtualScanCode` between `wVirtualKeyCode` and its Unicode character. The character is at byte offset 10, the control-key state at offset 12, and the record occupies 16 bytes. A test injecting the same incorrectly declared structure as its reader can conceal a layout defect.

## Testing Notes

Inject-based Windows tests can pass while physical keyboard input still fails. Validate interactive attach with a real TTY (for example `samples/SingleFiles/ConsoleAttach.cs`) after changing this path.

The TUI smoke mode drives both the child and a nested console host through real PTYs and checks colored drawing cells, mouse clicks/releases/wheel input, hit testing after resize, and screen/cursor/mouse-mode cleanup. A focused native-input regression queues a mouse press before a host read; this covers the discarded-record path. These automated checks do not establish physical-keyboard/mouse behavior in every terminal application.
