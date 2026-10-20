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
| `HasExited` | Reports the exit recorded by the background exit watcher (see [Child exit observation](#child-exit-observation)). Windows may also check `WaitForSingleObject(0)` on the process handle. On Unix it never calls `waitpid`: the watcher is the only reaper, so the one-time wait status is read once. |
| `WriteInputAsync` | Writes UTF-8 (default) or raw bytes to PTY stdin without blocking the caller (see [Output pump](#output-pump)). Does not close stdin. |
| `SendEof()` | **Windows:** writes Ctrl+Z + CR (`0x1A`, `0x0D`) after every byte written before it and keeps the input pipe open until exit (a pipe close yields `0xC000013A`); when input lacks a trailing `\r`/`\n`, an extra CR is written first. With no bytes, the chord goes on the first staging tick—see [Staged stdin EOF](#staged-stdin-eof). **Unix:** writes EOT (`0x04`, Ctrl-D) on the first staging tick with no input still queued and with room in the tty input queue, and on macOS no earlier than the fifth tick. Staging runs on a per-session timer, so it progresses whether or not anyone awaits exit. |
| `WaitForExitAsync(CancellationToken)` | Awaits the OS exit notification; holds no thread and never polls. Does not close the transport. Cancellation stops waiting only; the child keeps running (`OperationCanceledException`). |
| `CompleteAsync(PtyCompleteOptions, CancellationToken)` | Optional stdin, wait for exit, drain output, return `PtyResult`. Cancellation kills the child when `KillOnCancellation` is true (default). |
| `Kill()` | `TerminateProcess` (Windows) or `SIGKILL` (Unix: through the child's pidfd where Linux provides one, and otherwise with a checked `kill(2)`, see [Child exit observation](#child-exit-observation)). Does not release handles; call `Dispose` afterward. |
| `Kill(PtySignal)` | **Unix:** the per-OS native number (SIGUSR1/2 differ: Linux 10/12, macOS/FreeBSD 30/31), sent the way `Kill()` sends. **Windows:** signal is advisory; validates the enum then `TerminateProcess` (node-pty parity). |
| `WaitForExitAsync` / `ExitStatus` | Report the exit code together with the Unix termination signal. The native shim decodes them, from the `waitid` siginfo when it reaps through a pidfd on Linux and from the `waitpid` status (`WTERMSIG`) otherwise. Windows always reports a null signal. |
| `PtyCompleteOptions.OutputDrainGrace` | Default 1s—post-exit drain before closing transport. |
| `PtyCompleteOptions.OutputReaderCloseTimeout` | Default 5s—wait after transport close for the reader to finish. |
| `Dispose()` | If the child is still running, signals it (**Unix:** `SIGHUP`, then `SIGKILL` from the exit watcher one second later if it has not exited; **Windows:** `TerminateProcess`) and closes ConPTY/pipes/process handles without waiting for it to exit (**Unix:** the master once the watcher has reaped the child or sent that kill); the output pump stops on its own (see [Disposal](#disposal)). The exit watcher reaps the child, so `Dispose` never waits for it and leaves no zombie. `DisposeAsync` also waits for the pump to stop. |
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

1. Create the output pipe with `CreatePipe`, and the input pipe as a named pipe whose MiniPty end (the server) is opened overlapped and whose ConPTY end is opened synchronously by name. An anonymous pipe cannot be overlapped. The named pipe has a random name, one instance, `FILE_FLAG_FIRST_PIPE_INSTANCE`, and `PIPE_REJECT_REMOTE_CLIENTS`, so anything else that connects first makes the spawn fail instead of sharing the pipe. It keeps a 4 KiB buffer; with none, every write would wait for ConPTY to read.
2. Create every handle non-inheritable. ConPTY duplicates its ends into its host process itself, and the child is created with `bInheritHandles = false`. Non-inheritable handles do not prevent one leak, though: a process the host spawns with inheritance while `CreatePseudoConsole` runs can still hold the output pipe open, so end of output must not depend on the pipe breaking (see [Post-exit drain](#post-exit-drain)).
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

### Batch files and `cmd.exe` parsing

`CreateProcess` with a `.bat` or `.cmd` file starts `cmd.exe` on the given command line, and `cmd.exe` toggles its quoting on every `"`, ignoring the MSVCRT `\"` escape. `"x\" & echo INJECTED & \""` therefore leaves `& echo INJECTED &` outside quotes, and `cmd.exe` runs it. MiniPty starts `cmd.exe` itself for a batch file and escapes for it; the rules are in [Batch files](../specs/core_session.md#batch-files-windows). Points that took measurement or the console host's behavior to settle:

| Point | Finding |
|---|---|
| `""` inside quotes | Keeps `cmd.exe`'s quoting intact (it closes and reopens), and a program that receives the arguments through `%*` reads it as one quote under the UCRT parser that .NET's apphost uses. |
| `%` | Expands inside quotes too: without the `%%cd:~,%` escape, `%PATH%` reached the forwarded program as the PATH value. A lone `%` stays literal, because `cmd.exe` finds no variable up to the next `%`, so only a `%name%` that is defined shows the difference: an unescaped script path under a directory named `%OS%` pointed at `Windows_NT` and the script was not found, while one under `100%` still ran. |
| Length limit | 8191 characters ran and 8192 failed with "The command line is too long", for any script path length (157 and 276 characters measured). The count is the whole line as written, `cmd.exe /e:ON /v:OFF /d /c` included, before `%` expansion: with 100 escaped `%`, the boundary was still 8191 written characters (7491 after expansion). |
| Quoted path through `CreateProcess` | With the batch file as `lpApplicationName` and its quoted path first on the command line, `cmd.exe /c` stripped the first and last quotes and the command failed ("is not recognized"); unquoted, the same call ran the injected command. Starting `cmd.exe` with the outer pair of quotes avoids both. |
| `/v:OFF`, `/e:ON`, `/d` | Defaults that a user's registry can change (`DelayedExpansion`, `EnableExtensions`, `AutoRun`), so they are passed explicitly; with the defaults in place, no test can tell them apart from leaving them off. |
| Trailing spaces and dots | Windows opens `script.cmd.` as `script.cmd`, so detection trims them first; without that, the name with a dot ran on the unescaped path and the injection worked. |
| `%1` versus `%~1` | `%1` and `%*` keep MiniPty's quotes; `%~1` strips them, so a script that uses `%~1` unquoted in a command reopens the injection inside the script. |
| `NoDefaultCurrentDirectoryInExePath` | A shell can set it (the one these measurements ran in did). Without it, a bare `FileName` such as `whoami` ran a `whoami.exe` planted in the host's current directory instead of the System32 one. |

### Out-of-band ConPTY

`PtyStartInfo.ConPty` runs a session on `conpty.dll` plus `OpenConsole.exe` supplied by the application ([Out-of-band ConPTY](../specs/core_session.md#out-of-band-conpty)). What differs from the in-box path, verified against the console host source of the 1.22, 1.24, 1.25, and main branches, the export tables of the 1.22 and 1.25 builds, and package `1.25.260930003` on Windows 11 build 26200:

| Topic | In-box (`kernel32.dll` + `conhost.exe`) | Out-of-band (`conpty.dll` + `OpenConsole.exe`) |
|---|---|---|
| Entry points | `CreatePseudoConsole`, `ResizePseudoConsole`, `ClosePseudoConsole`; build 26100 also exports `ReleasePseudoConsole` | The same names plus a `Conpty`-prefixed set: `ConptyCreatePseudoConsole`, `ConptyResizePseudoConsole`, `ConptyClearPseudoConsole(HPCON, BOOL keepCursorRow)`, `ConptyReleasePseudoConsole`, `ConptyClosePseudoConsole`, `ConptyShowHidePseudoConsole`, `ConptyReparentPseudoConsole`, `ConptyPackPseudoConsole`, `ConptyCreatePseudoConsoleAsUser`. Identical in the 1.22 and 1.25 builds. `ConptyClosePseudoConsoleTimeout` appears in some header copies but in no binary. |
| Binding | `LibraryImport` | `NativeLibrary.Load` by absolute path, `GetExport`, unmanaged function pointers. `ConptyClearPseudoConsole` takes two arguments in every published version; the second is always passed, since a one-argument call leaves it to whatever the register holds. |
| Host discovery | `System32\conhost.exe` | Beside `conpty.dll`; then `<dir>\<arch>\OpenConsole.exe` with `<arch>` the operating system's native architecture (`IsWow64Process2`, so an x64 process on ARM64 Windows looks in `arm64\`); then `System32\conhost.exe`, silently. |
| Flags | `PSEUDOCONSOLE_INHERIT_CURSOR` (0x1) | The same bit. Older headers defined 0x2, 0x4, 0x8 as resize-quirk, Win32-input-mode, passthrough; 1.22 and later use 0x08, 0x10, 0x18 for glyph-width measurement and 1.25 adds 0x20. MiniPty passes nothing else. |
| Startup | `ESC[?9001h ESC[?1004h`, then a clear (`ESC[2J ESC[H`) unless inherit-cursor | `ESC[1t ESC[c ESC[?1004h ESC[?9001h ESC[?7l ESC[?7h`: a Primary Device Attributes query (`ESC[c`) that the frontend answers. 1.22 and 1.24 wait 3000 ms for the answer unconditionally; 1.25 and later wait 1000 ms only when inherit-cursor was requested. The input engine consumes the first DA1 reply per host, with or without a wait; later replies reach the child as keyboard input, which is why MiniPty never answers on an emulator's behalf. Measured: a read-only `CompleteAsync` of `cmd /c echo` completes in about 40 ms on 1.25 (140 ms in-box). |
| Unanswered inherit-cursor | Holds the child's output with no timeout ([Inherited cursor](../specs/core_session.md#inherited-cursor)): `cmd /c echo` had still produced nothing after 8 s | Bounded: the host gives up after its 1000 ms wait and runs the child; `cmd /c echo` completed in 1043 ms (`UnansweredInheritCursorIsBoundedOnTheOutOfBandHost`). Answered, it continues from the reported position without clearing, as in-box. |
| End of output | Only after `ClosePseudoConsole`, so the post-exit drain closes the pseudo-console after 100 ms of quiet | `ConptyReleasePseudoConsole` right after `CreateProcess` drops MiniPty's reference to the console server; the host exits with its last client and the pipe breaks. Measured over 10 sessions of `cmd /c echo`: output ended 2.4 ms after exit (min 2.0, max 2.6) against 114 ms in-box, and `PtyConPtyBenchmarks` puts a whole `cmd /c echo` completion at 33 ms against 141 ms. The drain's close-after-quiet stays as the bound for a host kept alive by a grandchild. |
| Close | Returns within 1 ms on build 26200; older builds are unverified | Closes the signal, reference, and process handles and never waits for the host (1.22 and main) |
| Clear | No operation | `ConptyClearPseudoConsole(hpc, keepCursorRow: false)` on the signal pipe. The host emits nothing; the next output is placed at row 1 (`ESC[1;1H`). The clear and input travel on different pipes with no ordering between them, so typing immediately after the clear can be processed first (observed once); a frontend's user types long after. |
| Census | A `conhost.exe` child of the calling process | An `OpenConsole.exe` child of the calling process. The only direct evidence of which implementation runs, since the fallback is silent. |

VT sequences written by a child, through each implementation (the F10 probe of [plan_gui_host_readiness.md](../plans/plan_gui_host_readiness.md) rerun with `PtyConPtyTests` on build 26200; the in-box column is that build's conhost):

| Sequence | In-box | Out-of-band |
|---|---|---|
| Mouse tracking 1000, 1003, SGR 1006 | consumed | forwarded |
| DECCKM (`ESC[?1h`) | consumed | forwarded |
| OSC 11 color query, DECRQSS, XTGETTCAP | consumed | forwarded |
| Kitty graphics APC, Sixel DCS | consumed | forwarded |
| OSC 0 title, OSC 8 hyperlink, OSC 52 clipboard, OSC 133, OSC 9;4 | forwarded (OSC 8 re-encoded) | forwarded |
| DEC modes 1049, 2004, 2026; `CSI > 1 u`; truecolor and `4:3` SGR | forwarded | forwarded |

The forwarded sequences also arrive in their original order relative to text out-of-band, where in-box reorders them. The host's own VT output differs too: a resize repaints the whole screen in-box and emits nothing out-of-band when nothing changed.

Packaging: the `Microsoft.Windows.Console.ConPTY` package carries `runtimes/win-{x86,x64,arm64}/native/conpty.dll` and `build/native/runtimes/{x86,x64,arm64}/OpenConsole.exe`. Its managed build files copy `<arch>\OpenConsole.exe` to the output root only when `ConptyRequires{x86,x64,ARM64}Host` is true, which they derive from `PlatformTarget`; a .NET project's `AnyCPU` sets none, and `build/` files import only for a direct reference. `buildTransitive/MiniPty.ConPty.targets` therefore adds the host from the resolved `conpty.dll`'s own package directory, with the DLL's `DestinationSubDirectory`, so it lands beside the DLL at the output root (runtime-specific build) or under `runtimes\win-<arch>\native\` (portable build), where the DLL's first probe finds it. Verified: a portable test build gets all three pairs, and `ConPtyLibrary.TryLoadFromApplicationDirectory()` finds the process architecture's.

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

**Fork child hygiene:** After `forkpty`, the child inherits the parent's fd table and signal dispositions. Before `chdir` / `execve`, the native shim resets all signal handlers to `SIG_DFL` (Linux and FreeBSD) while signals are still blocked in the child, then scrubs inherited descriptors `>= 3` on Linux (`close_range(3, ~0, CLOSE_RANGE_CLOEXEC)` closes them when available; otherwise a bounded `fcntl(F_SETFD, FD_CLOEXEC)` scan marks them close-on-exec). The fallback scan bound is computed in the **parent** via `sysconf(_SC_OPEN_MAX)` before `forkpty`; the child uses only async-signal-safe syscalls (`fcntl`, `syscall`, `sigaction`, `chdir`, `execve`). Work avoids malloc and runs only in the short-lived fork child; the parent managed path is unchanged. FreeBSD fd close is tracked as follow-up work. In the **parent**, the master `forkpty` returns is marked close-on-exec at once, because glibc opens it without `O_CLOEXEC` and every child the host starts later would otherwise inherit it; a fork on another thread in that instant still can. The slave has a shorter window of its own: glibc's `openpty` opens it without `O_CLOEXEC` too, and `forkpty` closes the parent's copy only after the fork, so a process the host forks on another thread inside `forkpty` keeps the slave, and with it the terminal, open (output then ends through the [post-exit drain](#post-exit-drain)). The macOS path opens both ends with `O_CLOEXEC`.

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
  ioctl(TIOCPTYGNAME) → open slave (O_NOCTTY), TIOCSWINSZ, tcsetattr(default termios)
  resolve minipty_spawn_helper via dladdr(dylib path)
  inject MINIPTY_CWD into envp when PtyStartInfo.WorkingDirectory is set
  posix_spawn(helper, dup2 slave → stdio, SETSID, …)
  close slave; keep master fd

minipty_spawn_helper:
  open(ttyname(STDIN), O_RDWR | O_NONBLOCK)   # controlling terminal
  poll → POLLHUP: raise(SIGHUP)                # master already closed
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
| Host gone before the helper's open | A blocking open of a slave with `CLOCAL` clear waits for carrier, and a closed master never brings it back. The helper therefore opens non-blocking, which still makes the slave its controlling terminal. A terminal that has already hung up reports `POLLHUP` (macOS sets it only when `POLLIN` is requested), and the helper ends by `SIGHUP`, the signal a session leader gets when its terminal hangs up. Before this, a helper whose host crashed right after the spawn waited in `open` forever, reparented to `launchd` |

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
| **Hang-up / `close(master)` on dispose** | Teardown | `Dispose()` sends `SIGHUP` if still running; `close(master)` follows once the child is reaped, or with the `SIGKILL` sent after the grace period |

EOT is a **terminal convention**, not a kernel EOF like closing a pipe. It is reliable for shell-wrapped one-shot commands when the line discipline is in **canonical mode**.

Staged EOT writes are best-effort: if the slave is already closed (for example after the child exits), `EIO` or `EPIPE` on the master is harmless and does not throw.

**Does not work reliably for:** raw-mode readers, full-screen TUIs (`vim`, `less`), REPLs, or apps that bind Ctrl-D to another action.

**macOS line limit:** in canonical mode a macOS tty holds at most 1,024 bytes on one line, LF included (`MAX_INPUT`). Once 1,024 bytes are pending without an LF, the tty takes later writes without blocking and drops later bytes, LF and EOT included. The write reports success, but the child keeps waiting. Kill (Ctrl-U), interrupt (Ctrl-C), quit (Ctrl-\\), and suspend (Ctrl-Z) free the line, and the pending bytes are lost then. Erase and word erase (Ctrl-W) free only the bytes they remove, and the rest of the line arrives if its LF comes before that room fills again. With the default IMAXBEL setting, the tty answers each dropped byte with a BEL while its output has room, but that looks like a BEL the child prints. MiniPty cannot detect this and sends none of these characters. Send longer input as shorter lines, or to a child in raw mode. This was verified on macOS 26 (arm64), as recorded in [plan_gui_host_readiness.md](../plans/plan_gui_host_readiness.md#macos-check).

**Windows vs Unix asymmetry:** `SendEof()` signals EOF differently—Windows writes Ctrl+Z + CR (never a pipe close); Unix writes Ctrl-D only (does **not** close the PTY master fd). Both use the same **staged** rule below.

### Staged stdin EOF (`SendEof`)

Platform-specific staging avoids signaling EOF before the child has attached stdin right after `CreateProcess` / `fork`+`exec`.

| Platform | After successful `WriteInputAsync` | EOF with no bytes (`Input: ""`) |
|---|---|---|
| **Windows** | Ctrl+Z + CR on the first staging tick with no input still queued or in flight (a leading CR first when the last byte was not a line terminator); the pipe stays open until exit. The console host queues the chord for the child whether or not it has attached yet. Closing the pipe is not EOF to the host: it ends the host's clients with `0xC000013A`, and a child about to exit on its own loses that race under load (seen on hosted runners and under 3× CPU oversubscription on a desktop) | Same, with no leading CR: the chord alone, on the first tick. Until 2026-10-19 empty stdin closed the pipe after 40 further ticks instead, which is where the `0xC000013A` exits came from |
| **Unix** | Write EOT (`0x04`) on the first staging tick with no input still queued and with room in the tty input queue, and on macOS no earlier than the fifth tick (a second EOT on a later tick when the last line lacks a terminator) | Defer EOT to the staging ticks / transport close |

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

`read`, `write`, `close`, and other plain syscalls remain on `libc` in the shared partial class. A child's signals and reap go through `minipty_signal_child` and `minipty_reap_child` in `libminipty_unix`. The first uses the child's pidfd or checks its pid first, and the second reaps through the pidfd or by pid. `TIOCSWINSZ` resize uses `minipty_set_winsize` in `libminipty_unix`—not a direct `ioctl` P/Invoke—because `ioctl` is variadic and mis-marshals on macOS arm64. Output readiness waits (`minipty_pty_wait`) and the reader wake descriptors live in the same native library.

## Output pump

Each session's output is read by one pump, from spawn until end of output, on a dedicated reader thread. Consumers never read the transport themselves, so no consumer blocks a caller's thread or a thread-pool worker, and output produced before a consumer attaches is never stranded.

| Aspect | Implementation |
|---|---|
| Reader thread | A process-wide pool of dedicated background threads, one per session that is still reading. When a session's output ends, its thread parks and serves the next session; a thread idle for 30 s exits. Never a thread-pool worker, never the caller's thread. |
| Buffer | A ring of 4 slots × 4 KiB in one array from the shared array pool. Each read fills one slot and keeps appending output that arrives within a short micro-window. The windows chained into one slot stop 2 ms after its first read on both platforms, so a slot is held for at most about 3 ms on Unix (1 ms windows) and 2.5 ms on Windows (0.5 ms windows). When all four slots hold unread output the pump stops reading, and backpressure reaches the child through the OS pipe. |
| Ring consumers | `ReadOutputAsync` hands out slots in place; `Output` copies out of them. Both wait through an allocation-free wait slot that the pump completes; continuations run asynchronously, so no consumer code runs on the reader thread. |
| Sink consumers | `CompleteAsync` and `PtyCapture.RunAsync` attach a sink. The pump writes into it on the reader thread, after delivering anything the ring already held; writes are serialized with attach and detach under the pump lock. |
| Windows read | Blocking `ReadFile` on the ConPTY output pipe. It ends when ConPTY closes its end of the pipe, after the pseudo-console is closed. |
| Windows coalescing | Continuation reads toggle `PIPE_NOWAIT`, because `PeekNamedPipe` often reports zero bytes on ConPTY pipes while output is in flight, and retry with `Thread.Sleep(0)` for a 500 µs window measured with `Stopwatch`. ConPTY delivers bulk output in slices of up to about 1.7 KB that arrive 20–45 µs apart (median and 90th percentile), so the window still joins them into full 4 KiB slots. A window measured with `Environment.TickCount64` lasted until the next clock tick, up to about 16 ms, and the reader spun most of a core whenever a child wrote small pieces. |
| Unix read | `minipty_pty_wait` waits on the master and the reader thread's wake descriptor together (`poll` on Linux and FreeBSD; `select` on macOS, whose poll(2) page says it does not support devices), then `read`s the master. The master is non-blocking (see Unix input), so a read that finds nothing after a readiness report waits again. |
| Unix coalescing | The same wait with a 1 ms timeout before each continuation read. |
| Unix stop | Closing a descriptor does not interrupt a `read` or `poll` blocked on it in another thread (close(2)), so a close or disposal signals the reader's wake descriptor: an `eventfd` on Linux, a kqueue with an `EVFILT_USER` event on macOS (which `select` reports readable while the event is triggered), a pipe opened with `pipe2(O_CLOEXEC)` on FreeBSD. Each is close-on-exec from the moment it opens, so no child the host forks on another thread can catch it unmarked; macOS has no `pipe2`, and a kqueue is never inherited by a fork child. |
| Unix master lifetime | Reference-counted. The reader, input writes, resize, foreground-process lookup, and EOT writes each pin the master; a close stops new users and drops the owner's reference, and the descriptor closes on the last release, so its number is never reused under an in-flight call. |
| Unix input | The master is non-blocking. A write takes what the tty input queue accepts on the calling thread, and the rest is queued in issue order. The reader thread adds `POLLOUT` (the write set of `select` on macOS) to its wait while input is queued, including while it is parked on a full ring, and writes the queue whenever the master can take more. A queued write completes when written; disposal fails it with `ObjectDisposedException`, and the end of output with `IOException`. A blocked `Input.Write` is woken by the reader thread directly, so it needs no thread-pool worker. Staged EOT waits until nothing is queued, and goes by the last byte the tty took, because a canceled write discards the rest. |
| Windows input | MiniPty's end of the input pipe is overlapped. ConPTY takes input into its own buffer whether or not the child reads, but stops reading input while its output is blocked: after about 4 KiB when a child writes output nobody reads. A write the pipe takes at once completes synchronously through a reusable overlapped structure and one 4 KiB native staging buffer, both created on the first write. The rest is queued in issue order and written by I/O completions on the runtime's completion port, with completion-port notification skipped for writes that finish at once. Disposal fails queued writes with `ObjectDisposedException`, and the transport close after exit fails them with `IOException`, as on Unix. Cancellation discards the bytes not yet staged; up to 4 KiB already staged in a pending write still reaches the child. Staged Ctrl+Z / CR waits until nothing is queued or in flight, and goes by the last byte staged. Completions run on the runtime's thread pool, so a queued write, including a blocked `Input.Write`, finishes only once a pool worker runs its completion. |

### Post-exit drain

ConPTY reports end of output only after the pseudo-console closes, and on Linux a grandchild can keep the slave open after the child exits (macOS revokes the tty when the session leader exits, so output ends there without the drain; FreeBSD is unverified). Once the exit watcher reports exit, a process-wide 10 ms timer, running only while some session is draining, closes the transport after the pump has read nothing for 100 ms and is not parked on a full ring.

A one-shot completion sink adds bounds: the transport closes at `OutputDrainGrace` after exit at the latest, and the drain wait fails with `TimeoutException` when the reader has not finished `OutputReaderCloseTimeout` after the close. It also never quiet-closes before the first read, because ConPTY can emit its first frame after a fast child has already exited.

Closing ends output without losing it: ConPTY flushes its final frame and the pump drains the pipe to its end, and Unix closes only after output has been quiet. A ring that is full when the close lands parks the reader as it does before the close, rather than ending output with the transport's remaining bytes unread; a consumer slow to release a slot delays the end, and parked time is not silence, so the forced end below waits for it too. Waiting for exit never closes the transport.

The close does not always end the transport. ConPTY's `CreatePseudoConsole` duplicates its pipe ends as inheritable while it starts its console host, and a process the host application creates with handle inheritance in that window (`Process.Start` always does) inherits the output pipe's write end and keeps it open until it exits; MiniPty's own handles are all non-inheritable and cannot prevent it. Measured on Windows 11 build 26200: `ClosePseudoConsole` returns within 1 ms, ConPTY's final frame (`ESC[?9001l ESC[?1004l`) arrives within 1 ms of the close in every run, and the pipe breaks 3 ms after the close when nothing else holds it, but only when the inheriting process exits otherwise. So after the close the drain keeps ticking: once reads have been quiet for the same 100 ms, the pump ends output itself. The reader thread cancels its blocking `ReadFile` with `CancelSynchronousIo` on a handle to itself (a canceled read transfers nothing), takes whatever arrived meanwhile with a non-blocking read, and reports end of output. The cancel is repeated every tick until the reader has finished, because a cancel that lands between two reads cancels nothing, and it is aimed by pump identity so a reader thread that has moved on to another session is never interrupted. Disposal issues the same cancel after closing the pipe handle, so `DisposeAsync` does not wait for such a process either.

### Disposal

`Dispose` switches the pump to discarding output, signals the child, closes the transport, and returns without waiting for the child to exit. On Windows the pump keeps reading until the pipe ends, because closing a pseudo-console can wait until its final frame fits in the pipe. On Unix the wake descriptor stops it at once, even when the master stays open for a hung-up child (below), and the exit watcher then discards what that child writes. The ring returns to the pool when both the session and the pump have released it, so the reader never writes into an array that another renter owns. `DisposeAsync` additionally completes once the pump has stopped.

How the child is ended differs by platform:

| Platform | On dispose | Why |
|---|---|---|
| Unix | `SIGHUP` on the disposing thread (through the child's pidfd on Linux; see [Child exit observation](#child-exit-observation)). `close(master)` waits until the exit watcher has reaped the child or sent the kill below, and until then the watcher reads and discards the child's output on its 10 ms polling interval, since the session's reader has stopped. The exit watcher sends `SIGKILL` one second later unless it has reaped the child by then; the deadline is part of the watcher's wait timeout, so disposal allocates no timer. An `AppDomain.ProcessExit` handler sends the pending kills at once when the host exits normally, and a disposal after it, such as one in the host's own exit handler, kills at once. A host ended by a signal it does not cancel (a .NET console host receiving `SIGTERM` with no handler of its own, or with one that does not cancel it) raises no `ProcessExit`, so a child it disposed within the grace and that ignores `SIGHUP` keeps running. The kill and the watcher's reap share the pid lock, so a child the watcher has reaped is never signaled. | `SIGHUP` is the signal a shell receives when its terminal window closes: it saves its history and runs its exit hooks, and a program that catches it can clean up. Closing the last reference to the master also hangs the tty up and signals the session leader, which the child is (Linux `tty_vhangup`; FreeBSD `ttydisc_modem` and XNU `ttymodem`, both only while `CLOCAL` is clear). A copy of the master that another process holds also keeps the tty from hanging up until that process exits: glibc's `forkpty` opens the master without close-on-exec, and MiniPty marks it right after, so only a process the host forks in that instant gets a copy. On macOS and FreeBSD a child that sets `CLOCAL` gets no hang-up from the close at all. Measured on Linux with such a process holding the master: with the explicit signal the child's `HUP` trap ran within about 90 ms, without it the trap never ran and the child was killed at the deadline. The close waits for the child because its hang-up is a second `SIGHUP`: bash saves its history before it resends the hang-up to its jobs, and a second signal during the save ended it with its jobs still running ([lessons](../specs/lifecycle.md#lessons-learned)). A child that ignores `SIGHUP` and reads its terminal therefore waits in the read until the kill, where the close used to fail the read. MiniPty signals only the child. When the child exits, the system sends `SIGHUP` to the terminal's foreground process group (POSIX `_exit` for a controlling process), so a descendant in that group ends too unless it ignores `SIGHUP`. An interactive shell resends `SIGHUP` to its jobs before it exits (bash documents this), so its background jobs end as well. The previous `SIGKILL` gave the shell no chance to, and they kept running. A descendant that ignores `SIGHUP` (`nohup`), was disowned, or started its own session keeps running, as it does when a terminal window closes. `Kill()` stays `SIGKILL`; only disposal is graceful. |
| Windows | `TerminateProcess`, then `ClosePseudoConsole` | ConPTY has no hang-up signal to send first. |

## Child exit observation

Exit is observed by one watcher per platform for each load of MiniPty, which is one per process unless a host loads MiniPty into several `AssemblyLoadContext`s. Observing exit adds no per-session thread and no thread-pool worker (output reading still holds the session's reader thread until end of output). Exit is recorded once; awaiters get a task created only on demand, library-internal waits use an allocation-free waiter slot, and the session's output drain is notified through an allocation-free listener.

| Platform | Mechanism |
|---|---|
| **Windows** | Background threads, each waiting on up to 63 child process handles with `WaitForMultipleObjects` (slot 0 is a wake event). The watcher owns a **duplicate** of each process handle and closes it when the child exits. |
| **Linux** | One thread blocked in `epoll_wait` over pidfds (`pidfd_open`, Linux 5.3+) plus a wake pipe opened with `pipe2(O_CLOEXEC)`. Each session keeps its child's pidfd, which the spawn opens right after the fork, and signals and reaps the child through it (`pidfd_send_signal`, `waitid(P_PIDFD)`). The watcher closes it after the reap. |
| **macOS / FreeBSD** | One thread blocked in `kevent` on `EVFILT_PROC` `NOTE_EXIT`, woken by an `EVFILT_USER` event on the same kqueue on macOS and by a wake pipe on FreeBSD. |
| **Fallback** | A child that cannot be registered (no pidfd, seccomp, or already exited before `kqueue` registration) joins a set the same thread polls every 10 ms, through the child's pidfd when it has one and with `waitpid(WNOHANG)` otherwise. |

The Unix watcher is the only code that reaps a session's child, through its pidfd on Linux and with `waitpid` otherwise. `HasExited`, `Kill`, and EOF staging read the recorded state instead; `Kill` and the reap share a lock so a signal can never reach a pid the reap has already released for reuse.

Code outside MiniPty can still reap a child: a host that reaps every child it has, or one that ignores `SIGCHLD` so the kernel reaps them. The child's pid is then free before the watcher notices, and a later process can get it. On Linux the spawn opens the child's pidfd right after the fork and, on Linux 5.4 and later, refuses one that names no child of the host. The child's signals and its reap then go through the pidfd, so neither reaches a process that gets the pid later. The pidfd is still opened by pid: if the child dies in that instant and other code reaps it, another child of the host that got the pid would pass the check. The reap through the pidfd needs Linux 5.4, and Linux 5.3 reaps by pid. Without a pidfd (macOS, FreeBSD, or Linux when the spawn gets none), a signal is sent only while `waitid` with `WNOWAIT` reports the pid as an unreaped child of the host, and the watcher reaps by pid. That check cannot tell the session's child from another child of the host that got the same pid. If such a reuse lands before the watcher notices the outside reap, the session sends its signals to that other child while that child runs, and the watcher can take that child's exit status when that child ends, unless other code reaps that child first. Otherwise a child that other code reaped is reported with exit code 0, because its wait status is lost.

Lessons learned:

- **Duplicated process handles make teardown unordered.** Closing a handle while another thread waits on it is undefined behavior. With the watcher owning a duplicate, a session closes its own handle at any time, and the child it kills on dispose drains its watcher slot without any unregistration handshake.
- **`RegisterWaitForSingleObject` costs about 270 bytes per session** (wait-handle wrappers, the registration object, its lock, a callback wrapper, and a completion work item). The shared `WaitForMultipleObjects` watcher costs nothing per session.
- **A closed pidfd can stay registered in epoll.** A child forked concurrently holds a duplicate of every parent descriptor until it execs, and epoll drops a registration only when every descriptor for the open file description is closed. The stale registration reported the same exit again with a pidfd number since reused for an unrelated descriptor, which closed live descriptors of other sessions and hung the parallel test suite. Registrations are now `EPOLLONESHOT` and are removed with `EPOLL_CTL_DEL` before the pidfd is closed.
- **A watcher kept in the native library is shared by every load of MiniPty.** A second `AssemblyLoadContext` that loads MiniPty gets its own managed watcher but the same native library, and its initialization found the first load's epoll and reused it. Both threads then waited on one epoll, each took exits of the other's children, which it did not know, and those sessions never reported exit. Measured on Linux with eight children per load exiting together: the test timed out in 3 of 3 runs. The managed watcher now owns its epoll or kqueue and its wake descriptors, and passes them to every native call.

## Shared Rules

### Byte streams

PTY output is a **byte stream**, not lines or Unicode strings:

- Do not translate `\n` ↔ `\r\n`.
- Do not use line-based APIs (`ReadLine`) as the primary read path.
- Decode PTY bytes with `PtyCompleteOptions.OutputEncoding` (default **UTF-8**). Do not use `Console.OutputEncoding`—in NativeAOT, containers, and CI it may not match the child terminal.

`PtyCaptureSink` timestamps each read at its completion, relative to a `TimeProvider` origin captured right after spawn (`PtyCaptureOptions.TimeProvider`, default `TimeProvider.System`).

### Terminal size

Initial size comes from `PtyStartInfo.Size` (character cells, not pixels). Windows: `COORD` for `CreatePseudoConsole` and `ResizePseudoConsole`. Unix: `winsize` in `forkpty` and `TIOCSWINSZ` via `minipty_set_winsize` (`PtySession.Resize`).

Dimensions are clamped to what those structures encode: 1–32767 on Windows (`COORD` is two signed 16-bit values), 1–65535 on Unix. ConPTY accepts the full range: on Windows 11 build 26200, `CreatePseudoConsole` and `ResizePseudoConsole` succeeded at 32767×32767 in about 5 ms, and a PowerShell child reported 600×200, 4000×300, 32767×50, and 200×32767 as its window and buffer size. An earlier 512-cell cap silently disagreed with wide frontends.

### Unix line discipline

The child's termios is set at spawn (`forkpty`'s termios argument on Linux and FreeBSD; `tcsetattr` on the slave before `posix_spawn` on macOS), not left to the kernel default, which differs by platform. The settings are input `ICRNL IXON IXANY IMAXBEL BRKINT IUTF8`, output `OPOST ONLCR`, control `CREAD CS8 HUPCL`, local `ICANON ISIG IEXTEN ECHO ECHOE ECHOK ECHOKE ECHOCTL`, and 38400 baud. Every control character slot starts disabled (`_POSIX_VDISABLE`), and the usual ones are enabled: `VEOF` ^D, `VERASE` DEL, `VWERASE` ^W, `VKILL` ^U, `VREPRINT` ^R, `VINTR` ^C, `VQUIT` ^\\, `VSUSP` ^Z, `VSTART` ^Q, `VSTOP` ^S, `VLNEXT` ^V, `VDISCARD` ^O, `VMIN` 1, `VTIME` 0, and where the platform defines them `VERASE2` ^H (FreeBSD), `VDSUSP` ^Y and `VSTATUS` ^T (macOS, FreeBSD). Flags and control characters a platform does not define are left out, so the line discipline is not identical everywhere: ^T, for example, reports status on BSD and is an ordinary character on Linux.

Measured against the Linux pts default (iflag `ICRNL IXON`, oflag `OPOST ONLCR`, cflag `B38400 CS8 CREAD`, the same local flags and control characters), two differences change behavior. `IUTF8` makes a canonical-mode erase remove a whole multibyte character; without it, a shell that reads a line containing Japanese and a backspace receives invalid UTF-8. `IXANY` lets any character, not only ^Q, resume output stopped with ^S. `IMAXBEL`, `BRKINT`, and `HUPCL` have no visible effect on a Linux pts. The macOS default lacks `IUTF8` as well. Disabled slots matter on BSD, where `_POSIX_VDISABLE` is 0xff: a slot left at zero would make NUL act as that control character. A program that wants raw mode still changes the termios itself, as it would on any terminal.

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
