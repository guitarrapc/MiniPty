# Cross-Platform PTY Implementation Reference

This document describes **how** MiniPty implements pseudo-terminals on each OS. Public API contracts live in [spec.md](../spec.md).

Source layout:

| Area | Location |
|---|---|
| Factory + session | `src/MiniPty/Pty.cs`, `PtySession.cs` |
| Output pump / drain | `src/MiniPty/Internal/PtyOutputPump.cs`, `PtyReaderThread.cs`, `PtyOutputDrainScheduler.cs`, `PtyOutputReader.cs`, `PtyOutputStream.cs` |
| Completion | `src/MiniPty/Internal/PtyCompletion.cs`, `PtyOutputSink.cs`, `PtyCompletionSink.cs` |
| Windows backend | `src/MiniPty/Internal/WindowsPtyBackend.cs` |
| Unix backend | `src/MiniPty/Internal/UnixPtyBackend.cs` |
| Timestamped observation | `src/MiniPty.Capture/PtyCapture.cs`, `Internal/PtyCaptureSink.cs` |

## Architecture

MiniPty selects one OS-specific backend at runtime:

| OS | API | Entry point |
|---|---|---|
| Windows | ConPTY (`CreatePseudoConsole`) | `WindowsPtyBackend.Start` |
| Linux / macOS / FreeBSD | Platform native shim + `execve` | `UnixPtyBackend.Start` |

Layers:

| Layer | Responsibility |
|---|---|
| **PTY backend** | Spawn child, attach PTY, read/write bytes, wait, exit code |
| **Completion** | Stdin pump, exit wait, output drain (`PtyCompletion`) |
| **Capture** (optional package) | Observe output: timestamp each read (`PtyCapturePump`) |

Do not parse escape sequences inside the PTY backend. Terminal rendering (ANSI → screen buffer) belongs in consumers such as scenetake.

## Session Lifecycle

Library callers use `Pty.Start` → `PtySession`. `PtyCapture.RunAsync` wraps the same session with a timestamping completion sink. For responsibility split and session-state flow diagrams, see [lifecycle.md](../specs/lifecycle.md) → Mental Model and Session Flow.

| API | Behavior |
|---|---|
| `Pty.Start(PtyStartInfo)` | Spawns the child. Does not wait. |
| `HasExited` | Reports the exit recorded by the process-wide exit watcher (see [Child exit observation](#child-exit-observation)). Windows may also check `WaitForSingleObject(0)` on the process handle. On Unix it never calls `waitpid`: the watcher is the only reaper, so the one-time wait status is read once. |
| `WriteInputAsync` | Writes UTF-8 (default) or raw bytes to PTY stdin. Does not close stdin. |
| `SendEof()` | **Windows:** after bytes were written, writes Ctrl+Z + CR (`0x1A`, `0x0D`) and keeps the input pipe open until exit (pipe close during wait yields `0xC000013A`); when input lacks a trailing `\r`/`\n`, an extra CR is written first. With no bytes, defers input pipe close—see [Staged stdin EOF](#staged-stdin-eof). **Unix:** writes EOT (`0x04`, Ctrl-D) on the first staging tick. Staging runs on a per-session timer, so it progresses whether or not anyone awaits exit. |
| `WaitForExitAsync(CancellationToken)` | Awaits the OS exit notification; holds no thread and never polls. Does not close the transport. Cancellation stops waiting only; the child keeps running (`OperationCanceledException`). |
| `CompleteAsync(PtyCompleteOptions, CancellationToken)` | Optional stdin, wait for exit, drain output, return `PtyResult`. Cancellation kills the child when `KillOnCancellation` is true (default). |
| `Kill()` | `TerminateProcess` (Windows) or `kill(SIGKILL)` (Unix). Does not release handles; call `Dispose` afterward. |
| `Kill(PtySignal)` | **Unix:** `kill(2)` with the per-OS native number (SIGUSR1/2 differ: Linux 10/12, macOS/FreeBSD 30/31). **Windows:** signal is advisory; validates the enum then `TerminateProcess` (node-pty parity). |
| `WaitForExitStatusAsync` / `ExitStatus` | Adds the Unix termination signal (`waitpid` `WTERMSIG`) to the exit code; decoding is fully managed (identical exited/signaled status layout on Linux/macOS/FreeBSD). Windows always reports a null signal. |
| `PtyCompleteOptions.OutputDrainGrace` | Default 1s—post-exit drain before closing transport. |
| `PtyCompleteOptions.OutputReaderCloseTimeout` | Default 5s—wait after transport close for the reader to finish. |
| `Dispose()` | If the child is still running, **kills** it, then closes ConPTY/pipes/process handles without blocking; the output pump stops on its own (see [Disposal](#disposal)). The exit watcher reaps the killed child, so `Dispose` never waits for it and leaves no zombie. `DisposeAsync` also waits for the pump to stop. |
| `PtyCapture.RunAsync` | `Pty.Start` + timestamping completion sink. Returns `PtyCaptureResult` with `Chunks`. |

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

## Unix spawn

Linux and FreeBSD use `forkpty` + native `execve`. macOS uses `posix_openpt` + `posix_spawn` of a bundled helper so the child acquires a controlling terminal without `fork` from a multithreaded parent.

### Linux / FreeBSD: forkpty + execve

MiniPty uses a small `libminipty_unix` native shim that calls the platform `forkpty`, resolves executable names against the child environment `PATH`, then runs `execve` with an explicit environment block in the child process.

### Child setup

```text
forkpty(master, NULL, NULL, winsize)   /* parent blocked all signals before fork */
child: reset signal handlers to SIG_DFL (while signals still blocked in child)
child: restore signal mask
parent: restore signal mask
child: scrub inherited fds >= 3 (Linux only; close_range closes, else FD_CLOEXEC scan)
child: optional chdir(cwd)
child: resolve file against child PATH when needed
child: execve(file, argv, envp)
parent: keep master fd for PTY input/output
```

**Fork child hygiene:** After `forkpty`, the child inherits the parent's fd table and signal dispositions. Before `chdir` / `execve`, the native shim resets all signal handlers to `SIG_DFL` (Linux and FreeBSD) while signals are still blocked in the child, then scrubs inherited descriptors `>= 3` on Linux (`close_range(3, ~0, CLOSE_RANGE_CLOEXEC)` closes them when available; otherwise a bounded `fcntl(F_SETFD, FD_CLOEXEC)` scan marks them close-on-exec). The fallback scan bound is computed in the **parent** via `sysconf(_SC_OPEN_MAX)` before `forkpty`; the child uses only async-signal-safe syscalls (`fcntl`, `syscall`, `sigaction`, `chdir`, `execve`). Work avoids malloc and runs only in the short-lived fork child; the parent managed path is unchanged. FreeBSD fd close is tracked as follow-up work.

**`forkpty()` safety:** Build executable path, `argv`, `envp`, and optional `cwd` as unmanaged UTF-8 C strings in the **parent** before `forkpty()`. The child path stays inside the native shim for `chdir`, path lookup, `execve`, and `_exit` after the fork boundary—no managed allocation, no `RuntimeInformation`, no string marshalling. The managed parent holds the payload until session/backend disposal; the fork child calls `chdir(cwd)` on its copy-on-write mapping (no child-side `strdup`). `execve` replaces the child address space.

The native boundary uses `LibraryImport` with `byte* file`, `byte** argv`, and `byte** envp`—not `string[]` marshalling—so NativeAOT does not depend on runtime array marshalling for the exec boundary.

If `file` contains `/`, the shim calls `execve` directly. Otherwise it searches the final child `PATH`; absent `PATH` falls back to `/bin:/usr/bin`, while an empty `PATH` is treated as an empty path entry for current-directory lookup. The fallback is fixed so the post-`forkpty()` child path does not need libc environment/path discovery calls before `execve`.

### Plain scripts and shell fallback

After `execve(path, …)` fails, the shim may retry with `/bin/sh` (then `/usr/bin/sh`) and the resolved file path as `argv[1]`—the same pattern as a manual `sh /path/to/script` invocation. This path is used when:

| `errno` | Typical cause |
|---|---|
| `ENOEXEC` | Plain script with no shebang |
| `EACCES` | File exists and is marked executable but the mount is `noexec` (common on hardened `/tmp`) |
| `ENOENT` | Shebang names a missing interpreter while the script file itself exists |

**Why this matters:** Plain scripts only need **read** permission for the `sh` fallback. Shebang execution tries to **exec** the script file first; on `noexec` mounts that path fails even when `sh script` would succeed. Callers spawning scripts on restrictive filesystems should either rely on this fallback (no shebang) or execute via an explicit interpreter (`sh`, `-c`, …).

On total failure the child exits with status **127**, matching the common shell convention for a failed `exec` (not only a missing `PATH` lookup).

### macOS: posix_spawn + spawn-helper

```text
parent (libminipty_unix.dylib):
  posix_openpt → grantpt → unlockpt
  ioctl(TIOCPTYGNAME) → open slave, TIOCSWINSZ
  resolve minipty_spawn_helper via dladdr(dylib path)
  inject MINIPTY_CWD into envp when PtyStartInfo.WorkingDirectory is set
  posix_spawn(helper, dup2 slave → stdio, SETSID, …)
  close slave; keep master fd

minipty_spawn_helper:
  open(ttyname(STDIN), O_RDWR)   # controlling terminal
  chdir(MINIPTY_CWD) when present; strip MINIPTY_CWD from env
  minipty_execvpe(file, argv, envp)
```

| Item | Detail |
|---|---|
| Helper binary | `minipty_spawn_helper` next to `libminipty_unix.dylib` (`runtimes/osx-*/native/`) |
| argv to helper | `[helper_path, file, arg1, …]` — not node-pty's `[helper, cwd, file, …]` |
| cwd | Internal env key `MINIPTY_CWD`; never passed to the target child; stripped from parent env and ignored in `PtyStartInfo.Environment` overlay |
| envp | Explicit block from managed overlay via `posix_spawn` env argument |
| exec semantics | Shared `minipty_unix_exec.c` (`PATH`, plain-script `sh` fallback) |
| Low-fd reservation | Before opening the real master, open dummy PTYs on vacant stdio slots (0–2) until the next fd is `>= 3` or three slots are held — keeps `dup2(slave → 0/1/2)` reliable when the embedding host closed or repurposed low fds |
| `posix_spawn` and `EINTR` | Retry `posix_spawn` in a tight inner loop on `EINTR` only, reusing the same `actions` / `attrs` — separate from the outer transient retry below |
| Transient spawn errors | macOS only: outer retry up to 4× on `EAGAIN` / `ENOMEM` / `ENXIO` with 25 ms × attempt backoff; closes the master between attempts |
| Spawn errors to managed | `minipty_fork_pty_exec` returns positive errno; `IOException` uses that value |

Burst parallel `Pty.Start` from a multithreaded host must not require a global `forkpty` mutex on Linux or macOS.

### Output reading

The session's output pump starts reading at spawn on a dedicated reader thread, for every consumer and on every platform, so fast children never race a consumer's first read. See [Output pump](#output-pump).

### Parent I/O (all Unix)

1. `forkpty()` returns the PTY master fd and child pid to the parent.
2. Consumer reads `Output` while the child runs.
3. If one-shot stdin was provided, `SendEof()` before waiting—see [Unix stdin EOF](#unix-stdin-eof).
4. If no stdin bytes, leave the master open for writes until the child exits (TUI programs).
5. The exit watcher reaps the child. The output pump reads until natural EOF; the post-exit drain closes the master only when output stays open after exit (a grandchild holds the slave on Linux; on macOS the session leader's exit revokes the tty, so output ends at once and later grandchild output is lost; FreeBSD is expected to revoke like macOS but is unverified). `Dispose()` closes the master, and the descriptor closes once the reader releases it.

### Unix stdin EOF

PTY master fds are **not** sockets. `shutdown(master, SHUT_WR)` is invalid (`ENOTSOCK`) and must not be used. Closing the master fd ends both read and write, so the parent cannot keep draining output.

| Approach | When | MiniPty |
|---|---|---|
| **EOT (`0x04`, Ctrl-D)** | One-shot / line-discipline programs (`cat`, shells) | `SendEof()` writes EOT to the master |
| **Leave master open** | TUI / no stdin (`matrix`, `cmatrix`) | `CompleteAsync` with `Input: null`—no `SendEof()` |
| **`Kill()` / `close(master)` on dispose** | Forced teardown | `Dispose()` kills if still running, then `close(master)` |

EOT is a **terminal convention**, not a kernel EOF like closing a pipe. It is reliable for shell-wrapped one-shot commands when the line discipline is in **canonical mode**.

Staged EOT writes are best-effort: if the slave is already closed (for example after the child exits), `EIO` or `EPIPE` on the master is harmless and does not throw.

**Does not work reliably for:** raw-mode readers, full-screen TUIs (`vim`, `less`), REPLs, or apps that bind Ctrl-D to another action.

**Windows vs Unix asymmetry:** `SendEof()` signals EOF differently—Windows writes Ctrl+Z + CR when bytes were written (pipe close only for empty stdin); Unix writes Ctrl-D only (does **not** close the PTY master fd). Both use the same **staged** rule below for empty stdin.

### Staged stdin EOF (`SendEof`)

Platform-specific staging avoids signaling EOF before the child has attached stdin right after `CreateProcess` / `fork`+`exec`.

| Platform | After successful `WriteInputAsync` | EOF with no bytes (`Input: ""`) |
|---|---|---|
| **Windows** | After bytes: Ctrl+Z + CR on the first staging tick; pipe stays open until exit. Empty stdin: close the pipe after 40 further ticks (or at transport close)—`WriteFile` does **not** mean the child stdin is wired yet (`0xC000013A` if closed too early) | Same |
| **Unix** | Write EOT (`0x04`) on the first staging tick (a second EOT on the next tick when the last line lacks a terminator) | Defer EOT to the staging ticks / transport close |

Staging ticks are 100 ms apart on a per-session one-shot timer that re-arms itself while staging is pending. They replaced the exit-wait poll loop that used to advance staging, so `SendEof` no longer depends on somebody awaiting exit; the tick count and spacing are unchanged.

### Platform differences

Shared session logic lives in `UnixPtyBackend`; spawn and resize ioctl live in `libminipty_unix` (+ `minipty_spawn_helper` on macOS). Runtime dispatch selects the correct native library for the target OS.

| OS | Spawn API | Controlling TTY |
|---|---|---|
| Linux | `forkpty` + child `execve` | `forkpty` session |
| FreeBSD | `forkpty` + child `execve` | `forkpty` session |
| macOS | `posix_spawn(minipty_spawn_helper)` | helper `open(slave)` |

| OS | Native library / headers |
|---|---|
| Linux | `<pty.h>` / `libutil` |
| macOS | `<util.h>`, `<spawn.h>` / `libminipty_unix.dylib` + helper |
| FreeBSD | `<libutil.h>` / `libutil` |

`waitpid`, `kill`, `read`, `write`, and other syscalls remain on `libc` in the shared partial class. `TIOCSWINSZ` resize uses `minipty_set_winsize` in `libminipty_unix`—not a direct `ioctl` P/Invoke—because `ioctl` is variadic and mis-marshals on macOS arm64. Output readiness waits (`minipty_output_wait`) and the reader wake descriptors live in the same native library.

## Output pump

Each session's output is read by one pump, from spawn until end of output, on a dedicated reader thread. Consumers never read the transport themselves, so no consumer blocks a caller's thread or a thread-pool worker, and output produced before a consumer attaches is never stranded.

| Aspect | Implementation |
|---|---|
| Reader thread | A process-wide pool of dedicated background threads, one per session that is still reading. When a session's output ends, its thread parks and serves the next session; a thread idle for 30 s exits. Never a thread-pool worker, never the caller's thread. |
| Buffer | A ring of 4 slots × 4 KiB in one array from the shared array pool. Each read fills one slot and keeps appending output that arrives within a short micro-window. On Unix the windows chained into one slot stop 2 ms after its first read, so a slot is held for at most about 3 ms; on Windows the chain is unbounded until a window passes empty. When all four slots hold unread output the pump stops reading, and backpressure reaches the child through the OS pipe. |
| Ring consumers | `ReadOutputAsync` hands out slots in place; `Output` copies out of them. Both wait through an allocation-free wait slot that the pump completes; continuations run asynchronously, so no consumer code runs on the reader thread. |
| Sink consumers | `CompleteAsync` and `PtyCapture.RunAsync` attach a sink. The pump writes into it on the reader thread, after delivering anything the ring already held; writes are serialized with attach and detach under the pump lock. |
| Windows read | Blocking `ReadFile` on the ConPTY output pipe. It ends when ConPTY closes its end of the pipe, after the pseudo-console is closed. |
| Windows coalescing | Continuation reads toggle `PIPE_NOWAIT`, because `PeekNamedPipe` often reports zero bytes on ConPTY pipes while output is in flight, and retry with `Thread.Sleep(0)` until `Environment.TickCount64` passes a 1 ms deadline, which on Windows lasts until the next system clock tick. |
| Unix read | `minipty_output_wait` waits on the master and the reader thread's wake descriptor together (`poll` on Linux and FreeBSD; `select` on macOS, whose `poll` does not support devices such as PTY masters), then `read`s the master. The master stays in blocking mode because input writes share it. |
| Unix coalescing | The same wait with a 1 ms timeout before each continuation read. |
| Unix stop | Closing a descriptor does not interrupt a `read` or `poll` blocked on it in another thread (close(2)), so a close or disposal signals the reader's wake descriptor: an `eventfd` on Linux, a pipe elsewhere. |
| Unix master lifetime | Reference-counted. The reader, input writes, resize, foreground-process lookup, and EOT writes each pin the master; a close stops new users and drops the owner's reference, and the descriptor closes on the last release, so its number is never reused under an in-flight call. |

### Post-exit drain

ConPTY reports end of output only after the pseudo-console closes, and on Linux a grandchild can keep the slave open after the child exits (macOS revokes the tty when the session leader exits, so output ends there without the drain; FreeBSD is unverified). Once the exit watcher reports exit, a process-wide 10 ms timer, running only while some session is draining, closes the transport after the pump has read nothing for 100 ms and is not parked on a full ring.

A one-shot completion sink adds bounds: the transport closes at `OutputDrainGrace` after exit at the latest, and the drain wait fails with `TimeoutException` when the reader has not finished `OutputReaderCloseTimeout` after the close. It also never quiet-closes before the first read, because ConPTY can emit its first frame after a fast child has already exited.

Closing ends output without losing it: ConPTY flushes its final frame and the pump drains the pipe to its end, and Unix closes only after output has been quiet. Waiting for exit never closes the transport.

### Disposal

`Dispose` switches the pump to discarding output, kills the child, closes the transport, and returns. On Windows the pump keeps reading until the pipe ends, because closing a pseudo-console can wait until its final frame fits in the pipe; on Unix the wake descriptor stops it at once. The ring returns to the pool when both the session and the pump have released it, so the reader never writes into an array that another renter owns. `DisposeAsync` additionally completes once the pump has stopped.

## Child exit observation

Exit is observed by one process-wide watcher per platform, so observing exit adds no per-session thread and no thread-pool worker (output reading still holds the session's reader thread until end of output). Exit is recorded once; awaiters get a task created only on demand, library-internal waits use an allocation-free waiter slot, and the session's output drain is notified through an allocation-free listener.

| Platform | Mechanism |
|---|---|
| **Windows** | Background threads, each waiting on up to 63 child process handles with `WaitForMultipleObjects` (slot 0 is a wake event). The watcher owns a **duplicate** of each process handle and closes it when the child exits. |
| **Linux** | One thread blocked in `epoll_wait` over pidfds (`pidfd_open`, Linux 5.3+) plus a wake pipe. |
| **macOS / FreeBSD** | One thread blocked in `kevent` on `EVFILT_PROC` `NOTE_EXIT` plus a wake pipe. |
| **Fallback** | A child that cannot be registered (no pidfd, seccomp, or already exited before `kqueue` registration) joins a set the same thread polls with `waitpid(WNOHANG)` every 10 ms. |

The Unix watcher is the only code that calls `waitpid` on MiniPty children. `HasExited`, `Kill`, and EOF staging read the recorded state instead; `Kill` and the reap share a lock so a signal can never reach a pid the reap has already released for reuse.

Lessons learned:

- **Duplicated process handles make teardown unordered.** Closing a handle while another thread waits on it is undefined behavior. With the watcher owning a duplicate, a session closes its own handle at any time, and the child it kills on dispose drains its watcher slot without any unregistration handshake.
- **`RegisterWaitForSingleObject` costs about 270 bytes per session** (wait-handle wrappers, the registration object, its lock, a callback wrapper, and a completion work item). The shared `WaitForMultipleObjects` watcher costs nothing per session.
- **A closed pidfd can stay registered in epoll.** A child forked concurrently holds a duplicate of every parent descriptor until it execs, and epoll drops a registration only when every descriptor for the open file description is closed. The stale registration reported the same exit again with a pidfd number since reused for an unrelated descriptor, which closed live descriptors of other sessions and hung the parallel test suite. Registrations are now `EPOLLONESHOT` and are removed with `EPOLL_CTL_DEL` before the pidfd is closed.

## Shared Rules

### Byte streams

PTY output is a **byte stream**, not lines or Unicode strings:

- Do not translate `\n` ↔ `\r\n`.
- Do not use line-based APIs (`ReadLine`) as the primary read path.
- Decode PTY bytes with `PtyCompleteOptions.OutputEncoding` (default **UTF-8**). Do not use `Console.OutputEncoding`—in NativeAOT, containers, and CI it may not match the child terminal.

`PtyCaptureSink` timestamps each read at its completion, relative to a `TimeProvider` origin captured right after spawn (`PtyCaptureOptions.TimeProvider`, default `TimeProvider.System`).

### Terminal size

Initial size comes from `PtyStartInfo.Size` (character cells, not pixels). Windows: `COORD` for `CreatePseudoConsole` and `ResizePseudoConsole`. Unix: `winsize` in `forkpty` and `TIOCSWINSZ` via `minipty_set_winsize` (`PtySession.Resize`).

### Environment variables

Current builds inherit the parent environment. Unix tools often expect `TERM=xterm-256color`; Windows behavior varies. Do not set `TERM` on Windows unless a specific tool requires it.

### Shutdown ordering

PTY shutdown is timing-sensitive. General pattern:

```text
1. The output pump reads PTY output from spawn (dedicated reader thread)
2. One-shot stdin: WriteInputAsync → SendEof (Windows: Ctrl+Z + CR when bytes written, else pipe close; Unix: EOT) before wait
3. Detect child exit (exit watcher notification)
4. Post-exit drain: close ConPTY (Windows) or the master (Unix) once output is quiet; one-shot completion bounds this by OutputDrainGrace
5. One-shot completion waits for the reader (OutputReaderCloseTimeout); disposal releases process handles
```

Exact ordering differs slightly by OS; avoid forcing one sequence if it causes hangs on one platform.

## Concurrency

At minimum, separate:

- **Output read**—the session's reader thread reads the PTY while the child runs; one consumer drains its buffer
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
| TTY detection (`redirected=False`) | Confirms ConPTY / `forkpty` attach |
| Simple command output | Confirms spawn + decode |
| Stdin + EOF | Confirms staged EOF on both OSes |
| Short TUI (`matrix 3`) | Confirms chunked ANSI capture |
| Cancellation on wait vs complete | Confirms kill-on-cancel contract |

[scenetake](https://github.com/guitarrapc/scenetake) adds fixture scenarios and `SCENETAKE_BIN` integration tests; that layer is outside this repository.

## Future Work

| Area | Features |
|---|---|
| **Near term** | Ctrl-C (`\x03` write), capture tuning (chunk size) |
| **Later** | Long-lived interactive sessions, disk spill for huge captures |

## NativeAOT Interop

- Use `[LibraryImport]` (source-generated P/Invoke), not `[DllImport]`. `AllowUnsafeBlocks` is required in the MiniPty project.
- Windows `CreateProcessW` takes a writable `char[]` command line; `InitializeProcThreadAttributeList` size query uses `ref nuint` (not `out`).
- Unix spawn passes `byte* file`, `byte** argv`, and `byte** envp` to the native shim. The managed side builds UTF-8 payloads with `NativeMemory.Alloc`—do not marshal `string[]` across the exec boundary.

## External References

- [Creating a pseudoconsole session (Microsoft Learn)](https://learn.microsoft.com/en-us/windows/console/creating-a-pseudoconsole-session)
- [Windows Terminal ConPTY samples](https://github.com/microsoft/terminal/tree/main/samples/ConPTY)
- [spec.md](../spec.md) — public API contract
