# Cross-Platform PTY Implementation Reference

This document describes **how** MiniPty implements pseudo-terminals on each OS. Public API contracts live in [spec.md](../spec.md).

Source layout:

| Area | Location |
|---|---|
| Factory + session | `src/MiniPty/Pty.cs`, `PtySession.cs` |
| Completion / drain | `src/MiniPty/Internal/PtyCompletion.cs`, `PtyOutputDrain.cs`, `PtyTextPump.cs` |
| Windows backend | `src/MiniPty/Internal/WindowsPtyBackend.cs` |
| Unix backend | `src/MiniPty/Internal/UnixPtyBackend.cs` |
| Timestamped capture | `src/MiniPty.Capture/PtyCapture.cs`, `Internal/PtyCapturePump.cs` |

## Architecture

MiniPty selects one OS-specific backend at runtime:

| OS | API | Entry point |
|---|---|---|
| Windows | ConPTY (`CreatePseudoConsole`) | `WindowsPtyBackend.Start` |
| Linux / macOS / FreeBSD | `openpty` + `fork` + `execvp` | `UnixPtyBackend.Start` |

Layers:

| Layer | Responsibility |
|---|---|
| **PTY backend** | Spawn child, attach PTY, read/write bytes, wait, exit code |
| **Completion** | Stdin pump, exit wait, output drain (`PtyCompletion`) |
| **Capture** (optional package) | Timestamp each read (`PtyCapturePump`) |

Do not parse escape sequences inside the PTY backend. Terminal rendering (ANSI → screen buffer) belongs in consumers such as scenetake.

## Session Lifecycle

Library callers use `Pty.Start` → `PtySession`. `PtyCapture.RunAsync` wraps the same session with a timestamp pump and `CompleteAsync`.

| API | Behavior |
|---|---|
| `Pty.Start(PtyStartInfo)` | Spawns the child. Does not wait. |
| `HasExited` | Polls the child (`WaitForSingleObject(0)` / `waitpid(WNOHANG)`). On Unix, a successful poll reaps the zombie and records the exit code. |
| `WriteInputAsync` | Writes UTF-8 (default) or raw bytes to PTY stdin. Does not close stdin. |
| `SendEof()` | **Windows:** closes the ConPTY input pipe on the first wait poll (or transport close)—always deferred; `WriteFile` success is not a safe attach signal. **Unix:** writes EOT (`0x04`, Ctrl-D); immediate after successful write, deferred when there were no bytes—see [Staged stdin EOF](#staged-stdin-eof). |
| `WaitForExitAsync(CancellationToken)` | Polls the child. Cancellation stops waiting only; the child keeps running (`OperationCanceledException`). |
| `CompleteAsync(PtyCompleteOptions, CancellationToken)` | Optional stdin, wait for exit, drain output, return `PtyResult`. Cancellation kills the child when `KillOnCancellation` is true (default). |
| `Kill()` | `TerminateProcess` (Windows) or `kill(SIGKILL)` (Unix). Does not release handles; call `Dispose` afterward. |
| `PtyCompleteOptions.OutputDrainGrace` | Default 1s—post-exit drain before closing transport. |
| `PtyCompleteOptions.OutputReaderCloseTimeout` | Default 5s—wait after transport close for the reader to finish. |
| `Dispose()` | If the child is still running, **kills** it, then closes ConPTY/pipes/process handles. On Unix, `Dispose` also attempts a bounded `waitpid` after `SIGKILL` to avoid leaving a zombie (up to ~1s). |
| `PtyCapture.RunAsync` | `Pty.Start` + timestamp pump + `CompleteAsync`. Returns `PtyCaptureResult` with `Chunks`. |

## Windows: ConPTY

### Topology

Pipes are transport only. The pseudo-terminal is the `HPCON` from `CreatePseudoConsole`:

```text
MiniPty (parent)
  ├─ write → input pipe  → ConPTY (HPCON) → child stdin/stdout/stderr
  ├─ read  ← output pipe ← ConPTY
  └─ CreateProcess with PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE
```

Redirecting `CreateProcess` stdin/stdout to anonymous pipes **without** ConPTY is not a PTY. Children report stdout as redirected; TUI tools such as `matrix` will not render.

### Setup sequence

1. Create input and output pipe pairs (`CreatePipe` with inheritable handles).
2. Mark the **parent** ends non-inheritable (`SetHandleInformation` on `inputWrite` and `outputRead`).
3. `CreatePseudoConsole(size, inputRead, outputWrite, …)` → `HPCON`. Returns **HRESULT** (not `GetLastError`); treat `hr < 0` as failure and use `Marshal.ThrowExceptionForHR(hr)`.
4. **Close** `inputRead` and `outputWrite` in the parent immediately (ConPTY holds its own duplicates).
5. Build `STARTUPINFOEX` with `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE`.
6. `CreateProcessW` with `EXTENDED_STARTUPINFO_PRESENT` and `bInheritHandles = false`.
7. Consumer reads `Output` while the child runs (via `CompleteAsync` or `PtyCapture`).
8. On shutdown: staged `SendEof()` when needed → wait for child → close ConPTY / drain reader.

### Parent console attachment

When the host runs attached to a real console (e.g. `dotnet run`), the OS may duplicate the parent's standard handles into a ConPTY child unless explicitly prevented. MiniPty sets:

```text
STARTUPINFO.dwFlags |= STARTF_USESTDHANDLES
hStdInput  = INVALID_HANDLE_VALUE
hStdOutput = INVALID_HANDLE_VALUE
hStdError  = INVALID_HANDLE_VALUE
```

Without this, command output can appear on the parent's console instead of the capture pipe. `CREATE_NO_WINDOW` alone was insufficient in testing.

### Handle pitfalls

| Mistake | Symptom |
|---|---|
| Not closing ConPTY pipe ends after `CreatePseudoConsole` | Missing output, hung reads |
| Letting `HPCON` leak as raw `IntPtr` | Double-close or missed `ClosePseudoConsole` on error paths |
| Closing `inputWrite` before the child is ready | `STATUS_CONTROL_C_EXIT` (0xC000013A) |
| `CREATE_NEW_CONSOLE` with ConPTY | Wrong console attachment |
| Serial read while child blocks on full pipe buffer | Deadlock |

### Minimum Windows version

ConPTY requires Windows 10 1809+ / Windows 11. MiniPty does not use winpty.

## Unix: openpty + fork

MiniPty uses `openpty` + `fork` rather than `forkpty` to keep explicit control over `setsid`, `TIOCSCTTY`, `chdir`, and `execvp`.

### Child setup

```text
close(master)
setsid()
ioctl(slave, TIOCSCTTY, 0)
dup2(slave, 0..2)
execvp via UTF-8 argv built with NativeMemory (byte**)
```

**`fork()` safety:** Build executable path, `argv`, and optional `cwd` as unmanaged UTF-8 C strings in the **parent** before `fork()`. The child path calls only libc P/Invoke (`close`, `setsid`, `ioctl`, `dup2`, `chdir`, `execvp`, `_exit`)—no managed allocation, no `RuntimeInformation`, no string marshalling. The parent frees the payload after `fork()`; the child retains a copy-on-write mapping until `execvp` replaces the address space.

`execvp` uses `LibraryImport` with `byte* file` and `byte** argv`—not `string[]` marshalling—so NativeAOT does not depend on runtime array marshalling for the exec boundary.

`execve` (explicit environment block) is a future option if callers need to override `TERM` or other variables independently of the parent process.

### Parent I/O

1. `close(slave)` after fork.
2. Consumer reads `Output` while the child runs.
3. If one-shot stdin was provided, `SendEof()` before waiting—see [Unix stdin EOF](#unix-stdin-eof).
4. If no stdin bytes, leave the master open for writes until the child exits (TUI programs).
5. `waitpid`; on the normal exit path, `CompleteAsync` drains output (natural EOF, then `close(master)` on timeout). `Dispose()` closes the master immediately, then drains with `OutputReaderCloseTimeout` only.

### Unix stdin EOF

PTY master fds are **not** sockets. `shutdown(master, SHUT_WR)` is invalid (`ENOTSOCK`) and must not be used. Closing the master fd ends both read and write, so the parent cannot keep draining output.

| Approach | When | MiniPty |
|---|---|---|
| **EOT (`0x04`, Ctrl-D)** | One-shot / line-discipline programs (`cat`, shells) | `SendEof()` writes EOT to the master |
| **Leave master open** | TUI / no stdin (`matrix`, `cmatrix`) | `CompleteAsync` with `Input: null`—no `SendEof()` |
| **`Kill()` / `close(master)` on dispose** | Forced teardown | `Dispose()` kills if still running, then `close(master)` |

EOT is a **terminal convention**, not a kernel EOF like closing a pipe. It is reliable for shell-wrapped one-shot commands when the line discipline is in **canonical mode**.

**Does not work reliably for:** raw-mode readers, full-screen TUIs (`vim`, `less`), REPLs, or apps that bind Ctrl-D to another action.

**Windows vs Unix asymmetry:** `SendEof()` signals EOF differently—Windows closes the ConPTY input pipe; Unix writes Ctrl-D only (does **not** close the PTY master fd). Both use the same **staged** rule below.

### Staged stdin EOF (`SendEof`)

Platform-specific staging avoids signaling EOF before the child has attached stdin right after `CreateProcess` / `fork`+`exec`.

| Platform | After successful `WriteInputAsync` | EOF with no bytes (`Input: ""`) |
|---|---|---|
| **Windows** | Defer pipe close to first wait poll (or transport close)—`WriteFile` to ConPTY does **not** mean the child stdin is wired yet (`0xC000013A` if closed too early) | Same |
| **Unix** | Write EOT (`0x04`) immediately | Defer EOT to wait loop / transport close |

### Platform differences

Shared session logic and per-OS constants / `openpty` imports live in `UnixPtyBackend`. Runtime dispatch selects the correct pair; do not reuse Linux-only values on BSD.

| OS | `TIOCSCTTY` | `openpty` library |
|---|---|---|
| Linux | `0x540E` | `libc` |
| macOS | `0x20007461` (`_IO('t', 97)`) | `libutil` |
| FreeBSD | `0x20007461` (`_IO('t', 97)`) | `libutil` |

`fork`, `setsid`, `waitpid`, and other syscalls remain on `libc` in the shared partial class. `TIOCSWINSZ` resize uses `minipty_set_winsize` in `libminipty_unix`—not a direct `ioctl` P/Invoke—because `ioctl` is variadic and mis-marshals on macOS arm64.

## Shared Rules

### Byte streams

PTY output is a **byte stream**, not lines or Unicode strings:

- Do not translate `\n` ↔ `\r\n`.
- Do not use line-based APIs (`ReadLine`) as the primary read path.
- Decode PTY bytes with `PtyCompleteOptions.OutputEncoding` (default **UTF-8**). Do not use `Console.OutputEncoding`—in NativeAOT, containers, and CI it may not match the child terminal.

`PtyCapturePump` timestamps each read while a `Stopwatch` runs from session start.

### Terminal size

Initial size comes from `PtyStartInfo.Size` (character cells, not pixels). Windows: `COORD` for `CreatePseudoConsole` and `ResizePseudoConsole`. Unix: `winsize` in `forkpty` and `TIOCSWINSZ` via `minipty_set_winsize` (`PtySession.Resize`).

### Environment variables

Current builds inherit the parent environment. Unix tools often expect `TERM=xterm-256color`; Windows behavior varies. Do not set `TERM` on Windows unless a specific tool requires it.

### Shutdown ordering

PTY shutdown is timing-sensitive. General pattern:

```text
1. Start reading PTY output (pump task)
2. One-shot stdin: WriteInputAsync → SendEof (Windows: pipe close; Unix: EOT) before wait
3. Detect child exit (wait / WaitForSingleObject)
4. Drain output (OutputDrainGrace), then close ConPTY (Windows) or master fd (Unix)
5. Wait for reader (OutputReaderCloseTimeout), release process handles
```

Exact ordering differs slightly by OS; avoid forcing one sequence if it causes hangs on one platform.

## Concurrency

At minimum, separate:

- **Output read**—background task reading the PTY while the child runs
- **Process wait**—foreground wait for exit
- **Input write**—optional; `SendEof()` when done (platform-specific EOF semantics)

PTY is full-duplex; serializing read and wait on one thread risks deadlock when buffers fill.

## Anti-patterns

| Anti-pattern | Why |
|---|---|
| Pipe redirect without ConPTY (Windows) | No TTY semantics |
| winpty / external PTY helpers | Extra dependency; conflicts with NativeAOT single-binary goal |
| Line-based PTY API | Breaks ANSI and binary-safe capture |
| Coupling PTY to VT parsing | Complicates testing and downstream renderers |
| Golden byte-identical capture tests | OS/timing variance; use property assertions |
| Blocking sync wrapper over async completion | Risks deadlocks; use `await` on `CompleteAsync` / `RunAsync` |

## Testing

See [spec.md](../spec.md) → Verification. `tests/MiniPty.Tests` exercises:

| Check | Why |
|---|---|
| TTY detection (`redirected=False`) | Confirms ConPTY / `openpty` attach |
| Simple command output | Confirms spawn + decode |
| Stdin + EOF | Confirms staged EOF on both OSes |
| Short TUI (`matrix 3`) | Confirms chunked ANSI capture |
| Cancellation on wait vs complete | Confirms kill-on-cancel contract |

[scenetake](https://github.com/guitarrapc/scenetake) adds fixture scenarios and `SCENETAKE_BIN` integration tests; that layer is outside this repository.

## Future Work

| Area | Features |
|---|---|
| **Near term** | Ctrl-C (`\x03` write), `execve` env control, capture tuning (`TimeProvider`, chunk size) |
| **Later** | Long-lived interactive sessions, disk spill for huge captures |

## NativeAOT Interop

- Use `[LibraryImport]` (source-generated P/Invoke), not `[DllImport]`. `AllowUnsafeBlocks` is required in the MiniPty project.
- Windows `CreateProcessW` takes a writable `char[]` command line; `InitializeProcThreadAttributeList` size query uses `ref nuint` (not `out`).
- Unix `execvp` is declared as `execvp(byte* file, byte** argv)`. The child builds a UTF-8 `argv` with `NativeMemory.Alloc`—do not marshal `string[]` across the exec boundary.
- `execve` remains an option when callers need an explicit environment block instead of inheriting the parent env.

## External References

- [Creating a pseudoconsole session (Microsoft Learn)](https://learn.microsoft.com/en-us/windows/console/creating-a-pseudoconsole-session)
- [Windows Terminal ConPTY samples](https://github.com/microsoft/terminal/tree/main/samples/ConPTY)
- [spec.md](../spec.md) — public API contract
