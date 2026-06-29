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

### Concurrent cancellation

When `ReadOutputAsync` and `WaitForExitAsync` run concurrently, cancellation is **scoped to the operation whose token was canceled**. Canceling one does not cancel the other and does not kill the child.

After `ReadOutputAsync` is canceled, the same session may start a new `ReadOutputAsync` enumeration. After `WaitForExitAsync` is canceled, the same session may call `WaitForExitAsync` again.

## Timeouts

| API / option | Timeout behavior |
|---|---|
| `CompleteAsync` / `PtyCapture.RunAsync` | `ExitTimeout`, `OutputDrainGrace`, and `OutputReaderCloseTimeout` from `PtyCompleteOptions` apply. |
| `ReadOutputAsync` | No implicit drain or read timeout. Use `CancellationToken` only. |
| `PtySession.WaitForExitAsync` | No implicit exit timeout. Use `CancellationToken` only. |

One-shot completion may bound waits to avoid hung children in fire-and-forget scenarios. Persistent streaming expects the embedder to supply cancellation.

## Output consumer exclusivity

Exactly **one** active output consumer is allowed per session. The first started consumer holds exclusivity until that read path ends (enumeration completed, canceled, or faulted).

| Active consumer | Forbidden (throws `InvalidOperationException`) | Allowed concurrently |
|---|---|---|
| `ReadOutputAsync` | Second `ReadOutputAsync`; raw `Output` read; `CompleteAsync` | `WriteInputAsync`, `SendEof`, `WaitForExitAsync`, `Resize`, `Kill`, `Dispose` |
| Raw `Output` read | `ReadOutputAsync`; `CompleteAsync` | `WriteInputAsync`, `SendEof`, `WaitForExitAsync`, `Resize`, `Kill`, `Dispose` |

`CompleteAsync` is not queued or blocked behind an active output consumer. Callers must finish or cancel the active read before starting one-shot completion.

`ReadOutputAsync` and `WaitForExitAsync` **may run concurrently** without deadlock, data loss, or premature transport close. Duplicate `WaitForExitAsync` calls are allowed.

### Host input adapters

Packages such as **MiniPty.Console** ([Console](console.md)) forward host keyboard bytes to `WriteInputAsync`. They **must not** start a second PTY output consumer.

Interactive hosts (use case 3) keep a single embedder-owned `ReadOutputAsync` reader for recording and host display while Console handles host terminal modes and stdin forwarding.

## EOF

`SendEof()` is platform-specific:

| Platform | Behavior |
|---|---|
| Windows (bytes written) | Writes Ctrl+Z + CR (`0x1A`, `0x0D`) to the ConPTY input stream; keeps the input pipe open until the child exits. Pipe close during the wait loop is not used — it is observed as `STATUS_CONTROL_C_EXIT`, not EOF. |
| Windows (no bytes) | Closes the ConPTY input pipe after staging (attach deferral). |
| Unix | Writes EOT (`0x04`, Ctrl-D) to the PTY master. It does not close the master fd. |

Unix EOT is a terminal convention, not kernel EOF. It is reliable for canonical-mode shell-wrapped one-shot commands, but not for raw-mode TUI programs. Windows stream EOF uses the legacy console Ctrl+Z + CR convention; when input lacks a trailing line terminator, an extra CR is written first to submit the pending line. Raw/TUI programs are not guaranteed.

If the slave side is already closed when staged EOT runs, the master write may fail with `EIO` or `EPIPE`; MiniPty treats that as harmless and does not throw.

### ConPTY spawn readiness

Windows may defer stdin EOF until the wait loop has given the child time to attach. Milestone 3 validates that immediate post-`Pty.Start` `WriteInputAsync`, `Resize`, and empty-stdin `SendEof` do not cause spurious child failure (for example `STATUS_CONTROL_C_EXIT`). This remains an internal transport concern; there is no public ready-state API.

## Drain, kill, and disposal

| Behavior | Contract |
|---|---|
| `ReadOutputAsync` after child exit | Drains remaining output and then completes normally. |
| `ReadOutputAsync` after `Kill()` | Same as exit: drain remaining output, then complete normally (EOF). |
| `CompleteAsync` after child exit | Drains output for `OutputDrainGrace`, then closes transport if needed. |
| Output reader close (one-shot) | Waits up to `OutputReaderCloseTimeout`. |
| `Dispose` / `DisposeAsync` while child running | Kills the child, then releases handles. |
| `Dispose` while operations are in flight | All in-flight `ReadOutputAsync`, `WaitForExitAsync`, and `WriteInputAsync` operations fail immediately with `ObjectDisposedException`. |
| `Kill()` | Terminates the child but does not release handles. Call `Dispose` afterward. |

## Failure Behavior

| Condition | Behavior |
|---|---|
| Unsupported OS | `PlatformNotSupportedException` from `Pty.Start`. |
| Spawn, ConPTY, `openpty`, or `forkpty` failure | OS exception with error code; run aborts. |
| Child non-zero exit | Returned as `ExitCode`; MiniPty does not throw for non-zero child exits. |
| Second output consumer | `InvalidOperationException`. |
| Session disposed while output streaming | `ObjectDisposedException`. |
| `ExitTimeout` exceeded (`CompleteAsync` / capture) | `TimeoutException`. |
| Output drain or reader close timeout (one-shot) | `TimeoutException`. |

MiniPty does not fall back to pipe redirect when PTY creation fails.

## Lessons Learned

- **ConPTY pipes are transport, not the terminal.** `HPCON` is the pseudo-console; mishandling pipe ends after `CreatePseudoConsole` causes missing output, hangs, or leaks to the parent console.
- **Child stdin must not be closed before launch completes on Windows.** Early ConPTY input pipe close yields `STATUS_CONTROL_C_EXIT` (0xC000013A). Empty-stdin EOF is staged to the first wait poll or transport close.
- **ConPTY input pipe close after a write is not clean EOF.** For one-shot stdin with bytes, MiniPty writes Ctrl+Z + CR and waits for natural exit; pipe close is deferred to transport cleanup after exit.
- **Unix PTY master fds cannot be half-closed.** Closing the master ends both read and write, so `SendEof()` writes EOT instead.
- **Canonical EOT is not kernel EOF on Unix.** One EOT on a non-empty line buffer delivers buffered bytes but does not end input; a submitted line or a second EOT may be needed.
- **Output drain after child exit needs bounded waits on one-shot paths.** `OutputDrainGrace` and `OutputReaderCloseTimeout` prevent hung readers without dropping ordinary slow flushes. Persistent `ReadOutputAsync` does not use those timeouts.
- **Cancel semantics differ by use case.** Waiting cancellation does not kill; one-shot completion defaults to killing when canceled.
- **Do not queue `CompleteAsync` behind active output reads.** Fail fast with `InvalidOperationException` so read, wait, and completion cannot deadlock inside hidden session queues.
- **Defer ConPTY transport close on one-shot transport pumps.** `CompleteAsync` and `PtyCapture.RunAsync` must wait for child exit with `closeTransportOnExit: false` while the transport pump is still reading; closing on exit truncates bulk stdout before `OutputDrainGrace` / `AwaitPumpAsync` can drain it.
- **One-shot drain uses post-exit quiet heuristics, not completion truth.** After child exit, `AwaitPumpAsync` may close the ConPTY or Unix PTY master transport when reads have been quiet long enough, bounded by `OutputDrainGrace`. This unblocks EOF for blocked pumps; it does not detect shell command completion.
- **Do not block one-shot completion waiting for a transport pump on the caller thread.** A caller-side wait loop (busy-spin or `Thread.Sleep(0)`) can starve the thread pool under parallel CI load and can throw cancellation before `KillOnCancellation` runs. Queue the transport pump promptly after spawn instead; see [pty_crossplatform.md](../references/pty_crossplatform.md) → One-shot transport pump scheduling.
- **Linux `forkpty` exec payloads must outlive the pre-exec child.** The parent must keep `file`, `argv`, `envp`, and `cwd` pointers valid until the child has `chdir`/`execve` or exited. Freeing managed UTF-8 allocations immediately after `forkpty` returns races the child and flakes as exit **127** or wrong working-directory lookup under parallel CI load. Hold the payload until session/backend disposal; the fork child uses its copy-on-write mapping for `chdir` (no child-side copy when the parent holds — see [pty_crossplatform.md](../references/pty_crossplatform.md) → `forkpty()` safety). `posix_spawn` paths copy env/cwd in the kernel before returning.
