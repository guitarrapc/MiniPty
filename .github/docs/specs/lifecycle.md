# Lifecycle Specification

Implemented lifecycle, cancellation, EOF, drain, and failure behavior for MiniPty.

## Cancellation

| API | On cancellation |
|---|---|
| `WaitForExitAsync` | Waiting stops with `OperationCanceledException`; the child continues running. |
| `ReadOutputAsync` | Output enumeration stops with `OperationCanceledException`; the child continues running. |
| `CompleteAsync` | When `KillOnCancellation` is true, the child is killed and `OperationCanceledException` is thrown. |
| `PtyCapture.RunAsync` | Same as `CompleteAsync`; it uses completion options. |

This split lets embedders observe cancellation without tearing down long-running children, while one-shot capture runs default to killing on cancellation so hung children do not leak.

## EOF

`SendEof()` is platform-specific:

| Platform | Behavior |
|---|---|
| Windows | Closes the ConPTY input pipe. EOF may be staged to avoid closing stdin before the child has attached. |
| Unix | Writes EOT (`0x04`, Ctrl-D) to the PTY master. It does not close the master fd. |

Unix EOT is a terminal convention, not kernel EOF. It is reliable for canonical-mode shell-wrapped one-shot commands, but not for raw-mode TUI programs.

## Drain And Disposal

| Behavior | Contract |
|---|---|
| `ReadOutputAsync` after child exit | Drains remaining output and then completes normally. |
| `CompleteAsync` after child exit | Drains output for `OutputDrainGrace`, then closes transport if needed. |
| Output reader close | Waits up to `OutputReaderCloseTimeout`. |
| `Dispose` while child running | Kills the child, then releases handles. |
| `Kill()` | Terminates the child but does not release handles. Call `Dispose` afterward. |

## Failure Behavior

| Condition | Behavior |
|---|---|
| Unsupported OS | `PlatformNotSupportedException` from `Pty.Start`. |
| Spawn, ConPTY, `openpty`, or `forkpty` failure | OS exception with error code; run aborts. |
| Child non-zero exit | Returned as `ExitCode`; MiniPty does not throw for non-zero child exits. |
| Concurrent `ReadOutputAsync` readers | `InvalidOperationException`. |
| Session disposed while output streaming | `ObjectDisposedException`. |
| `ExitTimeout` exceeded | `TimeoutException`. |
| Output drain or reader close timeout | `TimeoutException`. |

MiniPty does not fall back to pipe redirect when PTY creation fails.

## Lessons Learned

- **ConPTY pipes are transport, not the terminal.** `HPCON` is the pseudo-console; mishandling pipe ends after `CreatePseudoConsole` causes missing output, hangs, or leaks to the parent console.
- **Child stdin must not be closed before launch completes on Windows.** Early ConPTY input pipe close yields `STATUS_CONTROL_C_EXIT` (0xC000013A). EOF is staged to the first wait poll or transport close.
- **Unix PTY master fds cannot be half-closed.** Closing the master ends both read and write, so `SendEof()` writes EOT instead.
- **Canonical EOT is not kernel EOF on Unix.** One EOT on a non-empty line buffer delivers buffered bytes but does not end input; a submitted line or a second EOT may be needed.
- **Output drain after child exit needs bounded waits.** `OutputDrainGrace` and `OutputReaderCloseTimeout` prevent hung readers without dropping ordinary slow flushes.
- **Cancel semantics differ by use case.** Waiting cancellation does not kill; one-shot completion defaults to killing when canceled.
