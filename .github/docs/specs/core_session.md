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

## Session Contract

| Member | Behavior |
|---|---|
| `Input` / `Output` | Raw byte streams. No line-ending translation. stdout and stderr are merged on `Output`. |
| `WriteInputAsync` | Writes UTF-8 text by default, or raw bytes. Does not close stdin. |
| `SendEof()` | Signals end of stdin using platform-specific behavior. See [Lifecycle](lifecycle.md). |
| `Resize(PtySize)` | Resizes the terminal after spawn. |
| `WaitForExitAsync` | Waits for child exit. Cancellation stops waiting only; the child keeps running. |
| `CompleteAsync` | Convenience API for one-shot input, wait, drain, and result materialization. See [Completion](completion.md). |
| `Kill()` | Terminates the child process without releasing handles. |
| `HasExited` / `ExitCode?` | Polls exit state. `ExitCode` is null until the child has exited. |
| `Dispose` / `DisposeAsync` | Kills the child if still running, then releases handles. |

## Backpressure

A PTY has backpressure. If the child writes output and nothing reads `PtySession.Output`, the child may block when the terminal buffer fills. Callers must use `CompleteAsync`, `PtyCapture.RunAsync`, or continuously read `Output` themselves.

Continuous manual reading is possible through `Output`, but long-lived interactive sessions are not yet a supported high-level scenario in the current implementation.
