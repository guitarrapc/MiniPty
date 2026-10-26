# Cross-Platform PTY Implementation Reference

This document describes **how** MiniPty implements pseudo-terminals on each OS. Public API contracts live in [spec.md](../spec.md).

Source layout:

| Area | Location |
|---|---|
| Factory + session | `src/MiniPty/Pty.cs`, `PtySession.cs` |
| Output pump / drain | `src/MiniPty/Internal/PtyOutputPump.cs`, `WindowsOutputDriver.cs`, `PtyOutputDrainScheduler.cs`, `PtyOutputReader.cs`, `PtyOutputCoalescing.cs`, `PtyOutputStream.cs`; the Unix loop is `UnixChildWatcher.cs` with `UnixPtyBackend.cs` |
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
| `SendEof()` | **Windows:** writes Ctrl+Z + CR (`0x1A`, `0x0D`) after every byte written before it and keeps the input pipe open until exit (a pipe close yields `0xC000013A`); when input lacks a trailing `\r`/`\n`, an extra CR is written first. With no bytes, the chord goes on the first staging tick—see [Staged stdin EOF](#staged-stdin-eof-sendeof). **Unix:** writes EOT (`0x04`, Ctrl-D) on the first staging tick with no input still queued and with room in the tty input queue, and on macOS no earlier than the fifth tick. Staging runs on a per-session timer, so it progresses whether or not anyone awaits exit. |
| `WaitForExitAsync(CancellationToken)` | Awaits the OS exit notification; holds no thread and never polls. Does not close the transport. Cancellation stops waiting only; the child keeps running (`OperationCanceledException`). |
| `CompleteAsync(PtyCompleteOptions, CancellationToken)` | Optional stdin, wait for exit, drain output, return `PtyResult`. Cancellation kills the child when `KillOnCancellation` is true (default). |
| `Kill()` | `TerminateProcess` (Windows) or `SIGKILL` (Unix: through the child's pidfd where Linux provides one, and otherwise with a checked `kill(2)`, see [Child exit observation](#child-exit-observation)). Does not release handles; call `Dispose` afterward. |
| `Kill(PtySignal)` | **Unix:** the per-OS native number (SIGUSR1/2 differ: Linux 10/12, macOS/FreeBSD 30/31), sent the way `Kill()` sends. **Windows:** signal is advisory; validates the enum then `TerminateProcess` (node-pty parity). |
| `WaitForExitAsync` / `ExitStatus` | Report the exit code together with the Unix termination signal. The native shim decodes them, from the `waitid` siginfo when it reaps through a pidfd on Linux and from the `waitpid` status (`WTERMSIG`) otherwise. Windows always reports a null signal. |
| `PtyCompleteOptions.OutputDrainGrace` | Default 1s—post-exit drain before closing transport. |
| `PtyCompleteOptions.OutputReaderCloseTimeout` | Default 5s—wait after transport close for output reading to finish. |
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

`CreateProcess` with a `.bat` or `.cmd` file starts `cmd.exe` on the given command line, and `cmd.exe` toggles its quoting on every `"`, ignoring the MSVCRT `\"` escape. `"x\" & echo INJECTED & \""` therefore leaves `& echo INJECTED &` outside quotes, and `cmd.exe` runs it. MiniPty starts `cmd.exe` itself for a batch file and escapes for it. The rules are in [Batch files](../specs/core_session.md#batch-files-windows). Points that took measurement to settle:

| Point | Finding |
|---|---|
| `""` inside quotes | Keeps `cmd.exe`'s quoting intact, since it closes and reopens. A program that receives the arguments through `%*` reads it as one quote under the UCRT parser that .NET's apphost uses. The pre-2008 parsers of `CommandLineToArgvW` and `msvcrt.dll` read it as a quote that also ends the quoting: `x" --evil "` arrived as `x"`, `--evil`, and `"`. |
| `%` | Expands inside quotes too: without the `%%cd:~,%` escape, `%PATH%` reached the forwarded program as the PATH value. A lone `%` stays literal, because `cmd.exe` finds no variable up to the next `%`, so only a `%name%` that is defined shows the difference: an unescaped script path under a directory named `%OS%` pointed at `Windows_NT` and the script was not found, while one under `100%` still ran. |
| Length limit | 8191 characters ran and 8192 failed with "The command line is too long", for any script path length (157 and 276 characters measured). The count is the whole line as written, `cmd.exe /e:ON /v:OFF /d /c` included, before `%` expansion: with 100 escaped `%`, the boundary was still 8191 written characters (7491 after expansion). |
| Quoted path through `CreateProcess` | `CreateProcess` hands the line to `cmd.exe /c` as given. `cmd.exe /c` strips the first and last quote of a line that starts with a quote and holds more than two, or has a special character such as `&` or `(` inside the first pair. A quoted path followed by a quoted argument therefore failed with "is not recognized". Unquoted, the same call ran the injected command. Starting `cmd.exe` with an outer pair of quotes leaves the path's own quotes in place. |
| `/v:OFF`, `/e:ON`, `/d` | Defaults that a user's registry can change (`DelayedExpansion`, `EnableExtensions`, `AutoRun`), so they are passed explicitly. With the defaults in place, no test can tell them apart from leaving them off. |
| Trailing spaces, dots, and dot segments | Windows opens `script.cmd.` as `script.cmd`, so detection trims them first. Without that, the name with a dot ran on the unescaped path and the injection worked. `CreateProcess` also started `cmd.exe` for `script.cmd\.`, `script.cmd/.`, `script.cmd\sub\..`, `script.cmd\. `, `script.cmd\sub\.. `, and `script.cmd \.`, and the injection worked through the first four although `cmd.exe` reported the script as not recognized. It did not for `script.cmd\` (error 2) or `script.cmd\.. ` (error 5). `GetFullPathNameW` gave `script.cmd\` for `script.cmd\. `, so its result does not decide which names start `cmd.exe`. |
| Opening the script | `CreateProcess` failed with error 32 while another handle held the `.cmd` file for writing or denied reading, and ran it while other handles only read it. An empty `.cmd` failed with 193, and a dangling link named `.cmd` with 2. |
| `%1` versus `%~1` | `%1` and `%*` keep MiniPty's quotes. With the injection payloads, `echo %1`, `set v=%1`, `call %1`, and `call echo %1` ran nothing injected. `%~1` strips the quotes, and `cmd /c %1` strips the first and last quote of its line, so a script that uses either in a command reopens the injection inside the script. |
| Non-ASCII spaces | Outside quotes, `cmd.exe` split a script's parameters on U+3000, U+00A0, U+2003, and U+180E. .NET's `char.IsWhiteSpace` is false for U+180E. |
| `NoDefaultCurrentDirectoryInExePath` | A shell can set it (the one these measurements ran in did). Without it, a bare `FileName` such as `whoami` ran a `whoami.exe` planted in the host's current directory instead of the System32 one. |
| Program lookup by `CreateProcess` | Measured with `CreateProcess(NULL, name)` and copies of programs that exit with different codes. A name with a directory part (`sub\x.exe`, `sub/x.exe`) is searched for like a bare name: a copy under the host executable's directory ran before one under the current directory. `.\`, `./`, `..\`, and `../` names resolved against the current directory only. With `NoDefaultCurrentDirectoryInExePath` set, `sub\x.exe` was still found in the current directory and `sub/x.exe` was not. In `PATH`, a leading or middle empty entry and `.` found a file in the current directory, and a final empty entry did not. A relative entry was relative to the current directory. A quoted entry, one padded with spaces, and one with only a trailing space found nothing, and one with a trailing backslash worked. A directory of the name found first failed with `ERROR_ACCESS_DENIED` (5), even with a file of the name later in `PATH`. A missing file failed with `ERROR_FILE_NOT_FOUND` (2), also under a missing directory. A `.cmd` file that starts with `MZ` but is no valid image failed with error 216 and did not run as a batch file. |
| `.exe` appended by `CreateProcess` | Measured the same way, with the host's `PATH` set to one directory and `NoDefaultCurrentDirectoryInExePath` unset. `tool` ran `tool.exe` and did not find an extensionless `tool`; with both present, `tool.exe` ran. `sub\tool`, `sub/tool`, and `sub.d\tool` ran `sub\tool.exe` and `sub.d\tool.exe` and did not find the extensionless files, although the documentation says a name with a path gets no extension. `tool.` ran the extensionless `tool` and did not find `tool.exe`; `tool.foo` ran as written and did not find `tool.foo.exe`. A quoted `"my tool"` ran `my tool.exe`. A rooted name without an extension ran either file, `tool.exe` or `tool`, whichever existed; MiniPty leaves rooted names to `CreateProcess`, so that case is not modeled. The environment block passed for the child was not searched: a tool found only on the child's `PATH` failed with `ERROR_FILE_NOT_FOUND`, which is why MiniPty searches the child's `PATH` itself ([core_session.md](../specs/core_session.md#program-lookup-windows)). |
| A batch file as `lpApplicationName` | `CreateProcess` still started `cmd.exe` for it, and rebuilt the command line: with `"s.cmd" a "b c"` as the command line, `cmd.exe` exited 1 and the script did not run; with `s.cmd a "b c"` it ran and saw `%0` as `s.cmd`; with `"x y" a` it ran `x`. So a script is never passed as an application name; MiniPty starts `cmd.exe` itself, and a batch file with a raw `CommandLine` stays with `CreateProcess` and the host's `PATH`. |
| Relative `PATH` entries and `NoDefaultCurrentDirectoryInExePath` | A copy of `hostname.exe` planted in the current directory and started by name with `CreateProcess(NULL, name)`: with `PATH` set to an unrelated directory, the opt-out stopped it (`ERROR_FILE_NOT_FOUND`); with `;dir`, `.;dir`, `dir;;x`, or `dir\..` it ran whether the opt-out was set or not. The opt-out covers only the implicit current-directory step, not entries that point at the current directory. MiniPty's child-`PATH` lookup therefore skips every entry that is not fully qualified ([core_session.md](../specs/core_session.md#program-lookup-windows)); without that, its own test showed the planted program found for those four and for `C:dir\..` and a root-relative `\...\dir`. |
| Stream and short names of a batch file | `CreateProcess` refused `s.cmd::$DATA`, `s.cmd::$DATA.`, `s.CmD::$data`, and `s.bat::$DATA` with `ERROR_BAD_EXE_FORMAT` (193), with the name searched and as the full application name, and the script did not run, although `GetFileAttributesW` found each of them. `s.cmd:$DATA`, `s.cmd:`, and `s.cmd::$INDEX_ALLOCATION` failed to open. So a stream name, which batch detection does not recognize, fails closed rather than reaching `cmd.exe` with arguments quoted for a program. The test volume generates no 8.3 names; a short name keeps the first three characters of the extension, so a `.cmd` file's short name ends in `.CMD` and is detected. |

### Out-of-band ConPTY

`PtyStartInfo.ConPty` runs a session on `conpty.dll` plus `OpenConsole.exe` supplied by the application ([Out-of-band ConPTY](../specs/core_session.md#out-of-band-conpty)). What differs from the in-box path, verified against the console host source of the 1.22, 1.24, 1.25, and main branches, the export tables of the 1.22 and 1.25 builds, and package `1.25.260930003` on Windows 11 build 26200:

| Topic | In-box (`kernel32.dll` + `conhost.exe`) | Out-of-band (`conpty.dll` + `OpenConsole.exe`) |
|---|---|---|
| Entry points | `CreatePseudoConsole`, `ResizePseudoConsole`, `ClosePseudoConsole`; build 26100 also exports `ReleasePseudoConsole`, and nothing else: no clear, show/hide, or reparent in `kernel32.dll` or `kernelbase.dll` on build 26200 | The same names plus a `Conpty`-prefixed set: `ConptyCreatePseudoConsole`, `ConptyResizePseudoConsole`, `ConptyClearPseudoConsole(HPCON, BOOL keepCursorRow)`, `ConptyReleasePseudoConsole`, `ConptyClosePseudoConsole`, `ConptyShowHidePseudoConsole`, `ConptyReparentPseudoConsole`, `ConptyPackPseudoConsole`, `ConptyCreatePseudoConsoleAsUser`. Identical in the 1.22 and 1.25 builds. `ConptyClosePseudoConsoleTimeout` appears in some header copies but in no binary. |
| Binding | `LibraryImport` | `NativeLibrary.Load` by absolute path, `GetExport`, unmanaged function pointers. `ConptyClearPseudoConsole` takes two arguments in every published version; the second is always passed, since a one-argument call leaves it to whatever the register holds. |
| Host discovery | `System32\conhost.exe` | Beside `conpty.dll`; then `<dir>\<arch>\OpenConsole.exe` with `<arch>` the operating system's native architecture (`IsWow64Process2`, so an x64 process on ARM64 Windows looks in `arm64\`); then `System32\conhost.exe`, silently. |
| Flags | `PSEUDOCONSOLE_INHERIT_CURSOR` (0x1). The other bits change nothing: on build 26200 (conhost `10.0.26100.1`), 0x2, 0x4, 0x8, 0x10, and 0x18 were each accepted, and the startup, the forwarded and dropped sequences, the host's answers to queries, and the resize repaint were byte for byte those without a flag, apart from the per-session `id` the host puts in a re-encoded OSC 8 link, with either console input mode. The in-box host has no passthrough mode to turn on | The same bit. Older headers defined 0x2, 0x4, 0x8 as resize-quirk, Win32-input-mode, passthrough; 1.22 and later use 0x08, 0x10, 0x18 for glyph-width measurement and 1.25 adds 0x20. MiniPty passes nothing else. |
| Startup | `ESC[?9001h ESC[?1004h`, then a clear (`ESC[2J ESC[H`, with the cursor hidden around it) unless inherit-cursor, and after the child's first text a window title naming the program (`ESC]0;C:\Windows\System32\cmd.exe BEL`) that the child never wrote. That title comes from `STARTUPINFO.lpTitle`, which MiniPty leaves unset: on build 26200 an empty `lpTitle` removed it, while a title the child set (`cmd`'s `title`, an OSC 0 it wrote) still reached the frontend, and a non-empty `lpTitle` was announced instead of the program | `ESC[1t ESC[c ESC[?1004h ESC[?9001h ESC[?7l ESC[?7h`, then the child's text: no clear and no title, also without inherit-cursor. Whether the host's later cursor positioning agrees with a frontend that already showed content is unverified, so inherit-cursor remains the way to keep that content. The `ESC[c` is a Primary Device Attributes query that the frontend answers. 1.22 and 1.24 wait 3000 ms for the answer unconditionally; 1.25 and later wait 1000 ms only when inherit-cursor was requested. The input engine consumes the first DA1 reply per host, with or without a wait; later replies reach the child as keyboard input, which is why MiniPty never answers on an emulator's behalf. Measured: a read-only `CompleteAsync` of `cmd /c echo` completes in about 40 ms on 1.25 (140 ms on an in-box host that is not released, about 37 ms on one that is). |
| Unanswered inherit-cursor | Holds the whole child with no timeout ([Inherited cursor](../specs/core_session.md#inherited-cursor)): `cmd /c echo` had still produced nothing after 8 s, and `cmd /c ping -n 3 127.0.0.1 >nul` had not started `ping` after 3 s. Input written meanwhile, Ctrl+C included, is held and delivered after the answer. `Kill` during the wait makes the host emit its skipped startup with the clear (`ESC[2J`) as the session ends | Bounded: the host gives up after its 1000 ms wait and runs the child; `cmd /c echo` completed in 1043 ms (`UnansweredInheritCursorIsBoundedOnTheOutOfBandHost`). Answered, it continues from the reported position without clearing, as in-box. An answer arriving after the wait (at 2 s) was consumed and did not reach the child |
| Second cursor report | Consumed: a report written 500 ms after the first did not reach a `cmd` `set /p` child (3 of 3 runs) | The same (3 of 3 runs) |
| End of output | From build 26100 (Windows 11 24H2), `kernel32.dll` exports `ReleasePseudoConsole`, and MiniPty calls it right after `CreateProcess` as on the out-of-band path, resolving the export once at first use: the host exits with the child and the pipe breaks. In a raw probe on 26200 the pipe broke 1.9–2.2 ms after exit over 6 sessions (without the release, never within 2 s), and a `ResizePseudoConsole` after the release still reached the running child. Once the host has ended with the child, `ResizePseudoConsole` fails, and `Resize` skips it as exit tolerance allows: resizes 5 ms or more after the exit applied none of 20 in each of four delays, against 20 of 20 at 0–2 ms, and 20 of 20 at every delay while a grandchild kept the host. Older builds lack the export, and there output ends only after `ClosePseudoConsole`, which the post-exit drain calls after 100 ms of quiet (about 114 ms after exit measured) | `ConptyReleasePseudoConsole` right after `CreateProcess` drops MiniPty's reference to the console server; the host exits with its last client and the pipe breaks. Measured over 10 sessions of `cmd /c echo`: output ended 2.4 ms after exit (min 2.0, max 2.6) against 114 ms on an in-box host that is not released, and `PtyConPtyBenchmarks` put a whole `cmd /c echo` completion at 33 ms against 141 ms. Once the in-box host was released too, on build 26200, the two paths measured the same: `cmd /c echo` 37 ms in-box and 40 ms out-of-band, 32 KiB 74–75 ms and 76–77 ms. The drain's close-after-quiet stays as the bound for a host kept alive by a grandchild. |
| Close | Returns within 1 ms on build 26200; older builds are unverified | Closes the signal, reference, and process handles and never waits for the host (1.22 and main) |
| Clear | No operation: `kernel32.dll` exports none. The host itself acts on the clear signal `conpty.dll` writes to the signal pipe: on build 26200 it erased every row (`ESC[K` per row), homed the cursor, and placed the next output at row 1. Reaching that pipe means reading the first field of the opaque `HPCON` and writing an undocumented packet, and what a host without the signal does with it was not measured, so MiniPty does not use it | `ConptyClearPseudoConsole(hpc, keepCursorRow: false)` on the signal pipe. The host emits nothing; the next output is placed at row 1 (`ESC[1;1H`). The clear and input travel on different pipes with no ordering between them, so typing immediately after the clear can be processed first (observed once); a frontend's user types long after. |
| Census | A `conhost.exe` child of the calling process | An `OpenConsole.exe` child of the calling process. The only direct evidence of which implementation runs, since the fallback is silent. |

VT sequences written by a child, through each implementation (a probe set first run against the in-box host while investigating a terminal emulator that could not get mouse events, rerun with `PtyConPtyTests` on build 26200, then rerun with the child's console input mode varied, and again with text between the sequences to check their order; the in-box column is that build's conhost). "Default input" is the mode a console program starts with; "VT input" is a child that set `ENABLE_VIRTUAL_TERMINAL_INPUT` on its console input, as a program that reads VT key and mouse sequences does:

| Sequence | In-box | Out-of-band |
|---|---|---|
| Mouse tracking 1000, 1003, SGR 1006 | default input: consumed; VT input: forwarded, but ahead of text the child wrote before them | forwarded |
| DECCKM (`ESC[?1h`) | default input: consumed; VT input: forwarded, ahead of earlier text likewise | forwarded |
| None written; the child enables `ENABLE_MOUSE_INPUT` to read Win32 mouse events | the host requests `ESC[?1003;1006h` from the frontend itself | the same |
| DA1 (`ESC[c`), cursor position (`ESC[6n`), DECRQSS | answered by the host from its own state (DA1 `ESC[?61;6;7;21;22;23;24;28;32;42c`, the position in its own buffer); the frontend never sees the query | forwarded; the host answers nothing, so the child waits for the frontend |
| OSC 11 color query, XTGETTCAP | dropped and unanswered: the child gets no reply at all | forwarded; the host answers nothing |
| Kitty graphics APC, Sixel DCS | dropped, whatever the input mode | forwarded |
| OSC 0 title, OSC 8 hyperlink, OSC 52 clipboard, OSC 133, OSC 9;4 | forwarded (OSC 8 re-encoded) | forwarded |
| DEC modes 1049, 2004, 2026; `CSI > 1 u`; truecolor and `4:3` SGR | forwarded | forwarded |

The forwarded sequences also arrive in their original order relative to text out-of-band, where in-box reorders them. On build 26200, with VT input, a child that wrote text, `ESC[?1000h`, more text, `ESC[?1003h`, and so on had the in-box host send `ESC[?1000h` before even its startup clear, then the first text, then the remaining modes together, and only then the rest of the text. The host's own VT output differs too: a resize repaints the whole screen in-box, with or without the resize-quirk flag, and emits nothing out-of-band when nothing changed.

So the in-box host does not withhold mouse input as such. A program that reads VT input gets its mouse modes forwarded, and a program that reads Win32 mouse events gets them requested for it. Only a program that writes VT mouse modes while leaving its console input in the default mode loses them, and it could not have read VT mouse reports through that console input anyway. What the in-box host withholds unconditionally is graphics and the queries it neither answers nor forwards. The difference in queries also cuts the other way: a read-only consumer (`CompleteAsync`, Capture) of a child that asks for the cursor position or device attributes gets the in-box host's answer, while the out-of-band host leaves that child waiting for a frontend that is not there.

So the raw bytes of the same command differ by implementation. A read-only `cmd /c echo HELLO` on build 26200 and package `1.25.260930003`:

| Implementation | Output |
|---|---|
| In-box | `ESC[?9001h ESC[?1004h ESC[?25l ESC[2J ESC[m ESC[H` `HELLO` CR LF `ESC]0;C:\Windows\System32\cmd.exe BEL ESC[?25h ESC[?9001l ESC[?1004l` |
| Out-of-band | `ESC[1t ESC[c ESC[?1004h ESC[?9001h ESC[?7l ESC[?7h` `HELLO` CR LF `ESC[?1004l ESC[?9001l` |

A consumer that compares or parses output strips VT sequences on either implementation rather than expecting a fixed prefix.

Packaging: the `Microsoft.Windows.Console.ConPTY` package carries `runtimes/win-{x86,x64,arm64}/native/conpty.dll` and `build/native/runtimes/{x86,x64,arm64}/OpenConsole.exe`. Its managed build files copy `<arch>\OpenConsole.exe` to the output root only when `ConptyRequires{x86,x64,ARM64}Host` is true, which they derive from `PlatformTarget`; a .NET project's `AnyCPU` sets none, and `build/` files import only for a direct reference. `buildTransitive/MiniPty.ConPty.targets` therefore adds the host from the resolved `conpty.dll`'s own package directory, with the DLL's `DestinationSubDirectory`, so it lands beside the DLL at the output root (runtime-specific build) or under `runtimes\win-<arch>\native\` (portable build), where the DLL's first probe finds it. Publish recomputes its file list from the package assets, so the targets add the host to it with an item of its own. Verified: a portable test build gets all three pairs, and `ConPtyLibrary.TryLoadFromApplicationDirectory()` finds the process architecture's; a NativeAOT publish for win-x64 builds without trim or AOT warnings, loads the library, and runs under `OpenConsole.exe`.

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
  keep master fd, and slave fd until the master closes

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
| The host's slave descriptor | Returned with the master and closed with it, close-on-exec like it, so the child's exit is never the terminal's last close and macOS does not discard output the master has not read yet ([Post-exit drain](#post-exit-drain)) |
| Host gone before the helper's open | A blocking open of a slave with `CLOCAL` clear waits for carrier, and a closed master never brings it back. The helper therefore opens non-blocking, which still makes the slave its controlling terminal. A terminal that has already hung up reports `POLLHUP` (macOS sets it only when `POLLIN` is requested), and the helper ends by `SIGHUP`, the signal a session leader gets when its terminal hangs up. Before this, a helper whose host crashed right after the spawn waited in `open` forever, reparented to `launchd` |

Burst parallel `Pty.Start` from a multithreaded host must not require a global `forkpty` mutex on Linux or macOS.

### Output reading

The session's output pump starts reading at spawn, for every consumer and on every platform (through I/O completions on Windows, on the exit watcher's loop on Unix), so fast children never race a consumer's first read. See [Output pump](#output-pump).

### Parent I/O (all Unix)

1. `forkpty()` returns the PTY master fd and child pid to the parent.
2. Consumer reads `Output` while the child runs.
3. If one-shot stdin was provided, `SendEof()` before waiting—see [Unix stdin EOF](#unix-stdin-eof).
4. If no stdin bytes, leave the master open for writes until the child exits (TUI programs).
5. The exit watcher reaps the child. The output pump reads until natural EOF; the post-exit drain closes the master only when output stays open after exit (a grandchild holds the slave on Linux; on macOS the session leader's exit revokes the tty, so output ends at once and later grandchild output is lost; FreeBSD is expected to revoke like macOS but is unverified). `Dispose()` closes the master, and the descriptor closes once the loop has removed it.

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

**macOS line limit:** in canonical mode a macOS tty holds at most 1,024 bytes on one line, LF included (`MAX_INPUT`). Once 1,024 bytes are pending without an LF, the tty takes later writes without blocking and drops later bytes, LF and EOT included. The write reports success, but the child keeps waiting. Kill (Ctrl-U), interrupt (Ctrl-C), quit (Ctrl-\\), and suspend (Ctrl-Z) free the line, and the pending bytes are lost then. Erase and word erase (Ctrl-W) free only the bytes they remove, and the rest of the line arrives if its LF comes before that room fills again. With the default IMAXBEL setting, the tty answers each dropped byte with a BEL while its output has room, but that looks like a BEL the child prints. MiniPty cannot detect this and sends none of these characters. Send longer input as shorter lines, or to a child in raw mode. This was verified on macOS 26 (arm64) with a probe whose child counted the stdin bytes it received: a line of 1,023 bytes plus LF arrived, while one of 1,024 bytes or more, or 3,000 bytes without LF, never reached the child, which was still running 10 s later. 1 MiB of 64-byte lines and 64 KiB of short lines to a raw-mode child arrived in full. The limit is the tty's own: the same probe gave the same results for long lines before MiniPty's current input path existed.

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

`read`, `write`, `close`, and other plain syscalls remain on `libc` in the shared partial class. A child's signals and reap go through `minipty_signal_child` and `minipty_reap_child` in `libminipty_unix`. The first uses the child's pidfd or checks its pid first, and the second reaps through the pidfd or by pid. `TIOCSWINSZ` resize uses `minipty_set_winsize` in `libminipty_unix`—not a direct `ioctl` P/Invoke—because `ioctl` is variadic and mis-marshals on macOS arm64. The I/O loop's registrations and its wait (`minipty_watch_*`), which also report children's exits, live in the same native library.

## Output pump

Each session's output is read by one pump, from spawn until end of output: through overlapped reads completed on the runtime's I/O completion port on Windows, on the exit watcher's loop on Unix (epoll on Linux, kqueue on macOS and FreeBSD). Consumers never read the transport themselves, so no consumer blocks a caller's thread or a thread-pool worker, and output produced before a consumer attaches is never stranded.

| Aspect | Implementation |
|---|---|
| Pump and driver | The pump owns the ring, its consumers, and every decision about end of output; an output driver owns how reads are waited for. The driver hands each read to the pump, and the pump calls the driver back when something the driver cannot see from the transport changes: the consumer freed space, a sink attached, the close was requested, disposal began, or the drain forced the end. The backend picks the driver when the session starts: the Windows driver or the Unix loop below, which hold no thread per session ([why](#why-this-engine)). There is no other driver: a Unix master the loop cannot register fails `Pty.Start` with `IOException`, and the failed start hangs the child up as a disposal does. A loop whose epoll or kqueue failed to open is retried by the next start (see [Child exit observation](#child-exit-observation)). |
| Windows driver | Overlapped reads of MiniPty's end of the output pipe, completed on the runtime's I/O completion port through `ThreadPoolBoundHandle`, with completion-port notification skipped for reads that complete at once. While nothing is unread it waits with a zero-byte read, which holds no buffer; when output arrives it takes a ring, reads what the pipe holds, and publishes each read. While output is unread the next read is posted into the ring itself; once the consumer has released everything, that read is canceled with `CancelIoEx` and replaced by a zero-byte read, and the ring goes back. On a full ring no read is posted, so the pipe fills and ConPTY stops. Steps run on pool threads only, never block, and a step that keeps finding output continues as a new work item after 16 reads. The driver objects, each with its `PreAllocatedOverlapped`, are pooled across sessions; a session pays its pipe's binding (40 B). |
| Unix loop | The exit watcher's thread is the I/O loop of every session. Each session's non-blocking master is registered on the same epoll (Linux) or kqueue (macOS, FreeBSD) that reports the children's exits, level-triggered, keyed by a token of its slot and the slot's generation (a report for a freed slot is dropped), with the interest its state needs: output while its ring has room, input while some is queued. kqueue keeps the two interests as two filters of the master (`EVFILT_READ`, `EVFILT_WRITE`), each enabled or disabled with the interest, and the wait joins their events into one report per master, as epoll gives. Each pass reads every reporting master up to four reads (16 KiB) into its ring, taken on demand and let go of once the session has nothing unread, writes queued input, looks at each session that asked (a consumer freed space, input was queued, a sink attached, the close or disposal began); each read is published as it completes, with no coalescing window, as on Windows. Interest 0 arms an epoll registration for one report (`EPOLLONESHOT`), because a master whose slaves all closed reports hang-up whatever the interest, and a parked session would spin the loop on it: 750,000 passes in 500 ms in a test. Under kqueue it disables both filters, which then report nothing, end of file included. kqueue follows the [readiness probe](#readiness-on-a-pty-master): a master is registered only after its slave is open, since before that kqueue reports it writable while writes fail with `EAGAIN`; `EV_EOF` ends output only once a read returns 0; and a hang-up report whose first read finds nothing renews the registration (both filters deleted and added in one call), because `EV_EOF` stays on a registration after a slave opens again while reads fail with `EAGAIN`, and only a new registration clears it. A token needs 64 bits of the event's user data, so a 32-bit kqueue build cannot register a master and fails every start. A session's failure ends its output alone. The registration holds a reference on the master, so the descriptor closes only after the loop removed it. Reading needs no thread-pool worker. While a bridge waits to join a burst, the loop also serves the deadlines' tick: its wait times out about every millisecond, as it does for a hang-up's deadline, because a .NET timer on Linux fires at the kernel's tick rate (every 4 ms at 250 Hz). |
| Buffer | A 16 KiB byte ring in one pinned buffer from a process-wide free list, which keeps every buffer returned to it, up to the peak number held at once, and is never trimmed. Not the shared array pool: its per-thread caches strand an array returned on a thread other than the next renter's, and a ring is returned by whichever party lets go of it last. The driver takes it when it first reads into it, and lets go of it whenever the consumer has caught up and the driver waits for output, so an idle session holds none. It goes back once the driver has let go of it and nothing in it is unread (or the session was disposed). Each read publishes up to 4 KiB, as it completes. When less than 4 KiB is free the pump stops reading, and backpressure reaches the child through the OS pipe. |
| Ring consumers | `ReadOutputAsync` hands out all contiguous unread output in place, up to the end of the buffer, so a consumer that fell behind takes several publishes as one chunk; `Output` copies out of the ring. Both wait through an allocation-free wait slot that the pump completes; continuations run asynchronously, so no consumer code runs on the driver's thread. |
| Sink consumers | `CompleteAsync` and `PtyCapture.RunAsync` attach a sink. The pump writes into it on the driver's thread (an I/O completion on Windows, the loop on Unix), after delivering anything the ring already held; writes are serialized with attach and detach under the pump lock. A sink collects bytes and text in 16 KiB segments from a process-wide free list without per-thread caches, so its cost does not depend on the delivering thread, and merges them into the result arrays on the caller's thread. |
| Windows read | MiniPty's end of the output pipe is the overlapped server end of a named pipe, created like the input pipe (random name, one instance, the first instance, remote clients rejected, a 4 KiB buffer); ConPTY's end is opened synchronously by name. Reading ends when the console host exits and its end of the pipe closes: with the child, once MiniPty has released its reference to the console server (out-of-band, and in-box from Windows 11 24H2), otherwise after the pseudo-console is closed. It has to be a named pipe: an anonymous pipe cannot be opened for overlapped I/O, and a blocking read is not allowed on a handle that is. |
| Windows coalescing | None for the Windows driver: each read is published as it completes, and a consumer still busy with its previous chunk takes everything that arrived meanwhile as one chunk. The bridges join output that arrives in a burst on every platform ([Output frames](../specs/terminal.md#output-frames)); their deadlines tick on a high-resolution waitable timer, because a .NET timer fires every 15.6 ms on Windows, and ConPTY pauses longer than a millisecond often enough that a deadline kept to the millisecond ends a flood's wait about once per 60 KiB. ConPTY delivers bulk output in slices of up to about 1.7 KB that arrive 20–45 µs apart (median and 90th percentile), and `PeekNamedPipe` often reports zero bytes on its pipes while more is in flight, so nothing on the pipe tells a reader whether more is coming. The reader threads joined slices with a 500 µs window of non-blocking retries (`PIPE_NOWAIT`), which cost about half a core while eight children trickled output; that is why the window went rather than moved ([Why this engine](#why-this-engine)). |
| Unix read | On the loop, each readiness report is followed by non-blocking reads of the master, until one finds nothing or the per-pass bound. A macOS master holds 1,024 bytes of output at any line speed, so a read there returns at most 1 KiB. The master is non-blocking (see Unix input), so a read that finds nothing after a readiness report waits for the next one. |
| Unix coalescing | None: the loop publishes each read as it completes, the Windows driver's policy. Dropping the reader threads' 1 ms window made keystroke echo about 1 ms faster, and a flood through the WebSocket bridge took about twice as many frames on Linux (15–17 per 32 KiB instead of 7.7), which was accepted. A macOS read returns at most 1 KiB, so there a flood through the bridge took four times the frames (33 per 32 KiB instead of 8) and ran about 12% slower, a flood read directly cost the host half again as much CPU in thread-pool wake-ups, and echo became about 1.4 ms faster. The core keeps its policy, and the bridges coalesce bursts instead ([Output frames](../specs/terminal.md#output-frames)): a macOS flood through them takes 4 frames per 32 KiB and runs faster than with the reader threads. |
| Unix master lifetime | Reference-counted. The loop's registration (until the loop removes the master), input writes, resize, foreground-process lookup, and EOT writes each pin the master; a close stops new users, drops the owner's reference, and asks the loop to remove the master, and the descriptor closes on the last release, so its number is never reused under an in-flight call. Closing alone would not do: `close(2)` interrupts no `read` or `poll` blocked on the descriptor in another thread, and epoll keeps a registration until every duplicate of the descriptor closes, which a child forked on another thread holds until it execs. On macOS the session's descriptor of the slave closes right after the master. |
| Unix input | The master is non-blocking. A write takes what the tty input queue accepts on the calling thread, and the rest is queued in issue order. The loop sets the master's write interest while input is queued, also while the session is parked on a full ring, and writes the queue whenever the master can take more. A queued write completes when written; disposal fails it with `ObjectDisposedException`, and the end of output with `IOException`. A blocked `Input.Write` is woken by the loop directly, so it needs no thread-pool worker. Staged EOT waits until nothing is queued, and goes by the last byte the tty took, because a canceled write discards the rest. |
| Windows input | MiniPty's end of the input pipe is overlapped. ConPTY takes input into its own buffer whether or not the child reads, but stops reading input while its output is blocked: after about 4 KiB when a child writes output nobody reads. A write the pipe takes at once completes synchronously through a reusable overlapped structure and one 4 KiB native staging buffer, both created on the first write. The rest is queued in issue order and written by I/O completions on the runtime's completion port, with completion-port notification skipped for writes that finish at once. Disposal fails queued writes with `ObjectDisposedException`, and the transport close after exit fails them with `IOException`, as on Unix. Cancellation discards the bytes not yet staged; up to 4 KiB already staged in a pending write still reaches the child. Staged Ctrl+Z / CR waits until nothing is queued or in flight, and goes by the last byte staged. Completions run on the runtime's thread pool, so a queued write, including a blocked `Input.Write`, finishes only once a pool worker runs its completion. |

### Why this engine

A web host keeps many sessions open, and most of them are idle tabs. Before these engines, every session that was still reading held a dedicated reader thread blocked in the transport (`ReadFile` on Windows, `poll` or `select` and `read` on Unix) and its 16 KiB ring from spawn to end of output. 1,000 tabs cost about 1,000 threads and 16 MB whether or not they printed, and on Linux each thread also reserved an 8 MB stack. The engines keep what those threads guaranteed (output read from spawn, no consumer code on MiniPty's threads, bounded memory, the drain and disposal rules) without the thread and the idle buffer.

Measured with [WebTerminalScale.cs](../../../samples/SingleFiles/WebTerminalScale.cs) (`bench`), reader threads against the engines that replaced them, alternating the builds:

| | Reader threads | Engines |
|---|---:|---:|
| Threads at 128 sessions, Windows | 209–213 | 100–101 |
| Threads at 1,000 sessions, Linux | 1,051 | 31–50 |
| Reserved memory at 1,000 sessions, Linux | 8.4 GB | 240–396 MB |
| Threads at 256 sessions, macOS | 308 | 33 |
| Keystroke echo p50 at 64 sessions, Windows / Linux / macOS (ms) | 0.70 / 1.47 / 1.48 | 0.19–0.22 / 0.38–0.39 / 0.10–0.12 |
| Host CPU while 8 children write 20 bytes every 5 ms, Windows (% of a core) | 52.7 | 7.0 |
| A 32 MiB flood through the WebSocket bridge, frames per 32 KiB | 5.2–8.0 | 3.9–4.0, with the bridges' coalescing |

- The thread counts include the thread-pool workers a burst of connections injects, 9 to 52 depending on the round.
- Echo is faster because the reader threads' coalescing windows (500 µs on Windows, 1 ms on Unix) and the hand-off to their own thread are gone.
- Without the bridges' coalescing, publishing each read took a flood to 5.5 frames per 32 KiB on Windows, 15–17 on Linux, and 33 on macOS, where it also ran about 12% slower. That is why the bridges join bursts ([Output frames](../specs/terminal.md#output-frames)).
- A session start allocates a little more: 40 B on Windows for the pipe's completion-port binding, 10–20 B on Unix for the loop's state. That was accepted as the price of having no thread per session. Reads, chunks, and exits allocate what they did.

What the reference libraries taught, read from source (node-pty 1.1.0, WezTerm, Porta.Pty 2.2.3, creack/pty):

- **Blocking a pool worker per session is a correctness hazard, not only a cost.** Porta.Pty's default reads through a synchronous `FileStream`, which holds a pool worker per session, and on a 4-vCPU runner its own test delivered output from 0 of 24 concurrent sessions while the pool starved.
- **On Windows the output pipe must be a named pipe that MiniPty's end opens overlapped**, as Porta.Pty's `UseAsyncIo` already runs ConPTY. On Unix a non-blocking master on one shared readiness loop works, as node-pty does on libuv, and creack/pty on Go's netpoller on Linux.
- **Output keeps draining while the pseudo-console closes.** node-pty relays ConPTY output through a worker thread because `ClosePseudoConsole` waits until output is drained, and its main thread is the one that closes.
- **Push events with unbounded queues give a web host no backpressure** (node-pty, the WezTerm mux); pull from a bounded buffer does.

Rejected:

| Alternative | Why not |
|---|---|
| Unix: wrap the master in `System.Net.Sockets.Socket` and use the runtime's socket engine | On Linux it held no thread and allocated nothing (`new Socket(new SafeSocketHandle(fd, ownsHandle: false))` over the non-blocking master), the smallest change. But a socket over a non-socket descriptor is runtime behavior, not a contract; a zero-byte receive completes at once with 0, so an idle session could not wait without a buffer; `EIO` arrives as a generic `SocketError`; and macOS would depend on the runtime's kqueue |
| Unix `FileStream` or `RandomAccess` asynchronous reads, or a `Task.Run` loop that blocks | Synchronous reads on a pool worker: the starvation above |
| A relay thread into a socket (node-pty's worker, WezTerm's socketpair) | A thread per session again |
| Windows: a dedicated completion port and thread | Isolates reads from pool starvation, at the cost of a thread and a second completion mechanism beside the input pipe's. Kept as the fallback should pool dependence matter |
| Keeping the coalescing windows (high-resolution timers per session on Windows, loop deadlines on Unix) | Wake-ups per session for a policy that the consumer's own pace replaces. The bridges, which pay per frame, coalesce instead |
| Linux `io_uring` | Blocked by the default seccomp profile of common container runtimes, needs recent kernels, and adds a third Unix mechanism |
| A public option to choose the engine | Not a choice a user can make better than the library |
| An `AppContext` switch back to the reader threads | It existed during the transition (`MiniPty.UseOutputReaderThreads`) and was removed with the threads before any release. A rollback had to read as the old code did, so it kept the anonymous Windows pipe, the synchronous cancel of a blocked read, and the Unix wake descriptors alive beside the engines |

Rules the engines keep:

- **Nothing that can block runs on the loop or on a completion.** Spawning, `ClosePseudoConsole`, consumer continuations, and growing a sink stay off them.
- **Faults stay with their session.** A failure in one session's work ends that session's output with the error; the loop goes on.
- **No lock is shared across sessions while a slow call runs.** node-pty holds a global mutex across `ClosePseudoConsole`.
- **Memory is bounded per session** by its ring, the pipe or tty buffer, and a persistent session's replay buffer. A slow client throttles its own child, never the loop.
- **Thread-pool dependence is stated.** On Unix reading goes on while the pool is starved, until rings fill, and only delivery to consumers waits. On Windows completions need a pool worker, as Kestrel's sockets do.

### Readiness on a pty master

Each Unix loop waits with the mechanism a standalone probe chose before the loop was written, [pty-readiness-probe.c](../../../scripts/pty-readiness-probe.c). On macOS 26.6.2 (Apple M2) all 71 of its checks passed. It has not run on FreeBSD.

| OS | Wait | Basis |
|---|---|---|
| Linux | epoll, level-triggered | The exit watcher's epoll over pidfds, which every master joins |
| macOS | kqueue, level-triggered | Probed. `select` and `poll` work on a master too, but their wait cost grows with every master |
| FreeBSD | kqueue, level-triggered | Source only (`ptsdev_kqfilter`, FreeBSD 8.0 and later), unverified |

kqueue on a macOS master (each behavior is in XNU's `ptmx_kqfilter` too):

- `EVFILT_READ` and `EVFILT_WRITE` are level-triggered: reported on every wait while output is unread or input has room. Output stopped by `^S` (MiniPty sets `IXON`) is not reported readable until `^Q`, so a stopped terminal does not spin the loop.
- `EVFILT_READ`'s `data` is the exact unread byte count; `EVFILT_WRITE`'s is always 0. Using the count to skip the read that returns `EAGAIN` changed nothing measurable, so the loop reads until `EAGAIN`, as on Linux.
- After the slave's last close, `EVFILT_READ` reports `EV_EOF` with `data` 0, a read returns 0 (Linux fails it with `EIO`), and a write fails with `EIO`. The report repeats on every wait until the master is removed.
- `EV_EOF` sticks: a slave opened again after its last close left the registration reporting `EV_EOF` while reads failed with `EAGAIN`, which would spin a loop. Only deleting and adding the registration cleared it.
- Before the slave first opens, a master is reported writable while writes fail with `EAGAIN`; `select` reports nothing.
- **Why others avoid kqueue for ttys.** Up to macOS 14, `O_NONBLOCK` on a master whose slave was never opened failed with `ENOTTY` but set the flag anyway (macOS 15 added an exception, and on macOS 26 it succeeded); that is behind Go issue 22099 and creack/pty's blocking master, while Go itself still puts character devices in kqueue. tmux's "kqueue only works on sockets" concerns the client's `/dev/null` and `/dev/tty`. XNU builds `poll` on kqueue's filters, so the poll(2) page's "does not support devices" is outdated for masters.

Time from a write on one slave to the wait reporting its master, p50 / p99 in µs over 300 rounds. With all 436 masters active each was reported exactly once, and idle for 500 ms kqueue used 0.01–0.03 ms of CPU, `select` and `poll` up to 0.4 and 0.7 ms:

| Masters | kqueue | `select` | `poll` |
|---:|---:|---:|---:|
| 1 | 4 / 14 | 9 / 21 | 10 / 22 |
| 64 | 5 / 28 | 28 / 69 | 31 / 63 |
| 256 | 9 / 21 | 48 / 243 | 68 / 386 |
| 436 | 7 / 29 | 62 / 332 | 121 / 564 |

Eight floods of 8 MiB at once moved 297–298 MB/s through one kqueue loop at 3.3–3.4 ms of CPU per MiB, against 225–228 MB/s and 13.8–14.2 ms with a thread per session, and the loop shared them best (`select` reports in descriptor order, which favors low descriptors). A single flood moved faster through a thread of its own (204–285 against 155–233 MB/s), because each KiB costs a wait plus a read.

A macOS master holds exactly 1,024 bytes of output and 1,022 of input at every line speed, so each readiness report moves at most 1 KiB. On FreeBSD (source) the output queue is the line speed divided by 5, at most 64 KiB, so about 7.5 KiB at MiniPty's 38,400 baud; `EVFILT_READ` reports the bytes queued and `EVFILT_WRITE` the room left, queued bytes are read before the end of file after the last close, and nothing is reported before the slave first opens.

What the probe taught about probing:

- **Whether an exit is the slave's last close decides between 0.6 s and no bound** (see [Post-exit drain](#post-exit-drain)). A forked child discarded its output after 0.6 s while `sh -c "exec head …"` never exited until read. The variable was not `exec` but another holder of the slave, so a probe of exit behavior controls who else holds the slave.
- **A forked session leader must ignore `SIGHUP` before it forks a grandchild it means to keep.** The leader's exit sends `SIGHUP` to the foreground group at once, and a grandchild that set its own handler after the fork died first.
- **A write larger than the tty's buffer cannot test the discard.** The child blocks in its write until the master reads, so it exits only after the read.

### Post-exit drain

An in-box ConPTY older than Windows 11 24H2, which cannot be released, reports end of output only after the pseudo-console closes; a released console host stays alive while a grandchild is still attached to it; and on Linux a grandchild can keep the slave open after the child exits (macOS revokes the tty when the session leader exits, so output ends there without the drain; FreeBSD is unverified). Once the exit watcher reports exit, a process-wide 10 ms timer, running only while some session is draining, closes the transport after the pump has read nothing for 100 ms and is not parked on a full ring.

A one-shot completion sink adds bounds: the transport closes at `OutputDrainGrace` after exit at the latest, and the drain wait fails with `TimeoutException` when output reading has not finished `OutputReaderCloseTimeout` after the close. It also never quiet-closes before the first read, because ConPTY can emit its first frame after a fast child has already exited.

On macOS the session holds a descriptor of the slave from spawn until the master closes, so the child's last output survives a consumer that has stopped reading. When a child's exit is the terminal's last close, macOS waits at most 0.6 s (60 clock ticks) for the master to read what the tty still holds, up to 1,024 bytes, then discards it, and the exit completes only then. When another descriptor of the slave is open, the session leader's exit waits for the master without a bound and then revokes the terminal, which still ends output at the exit. Without MiniPty's descriptor, a session whose ring was full when its child exited lost up to 1 KiB of final output once its consumer stayed away for 0.6 s. Measured on macOS 26: a zsh child filled the ring, wrote 500 more bytes, and exited, and a consumer that began reading 2 s later received everything but those 500 bytes. With `/bin/sh` (bash), whose exit was not the last close for a reason not identified, nothing was lost. The exit is reported after the window, so the exit report could not trigger a rescue read ([readiness probe](#readiness-on-a-pty-master)).

The descriptor trades that loss for a wait. The child's exit now waits until the consumer frees space and the pump reads the rest, so the exit is reported only then. A child whose output nobody reads already blocks once the ring and the tty fill; this moves that point by at most 1 KiB. Disposal discards the output as before, so a child held in its exit ends with the session. A child that dropped its controlling terminal before it exits has no terminal for its exit to revoke, so output ends through the post-exit drain 100 ms after the exit, as on Linux. Only macOS holds the descriptor. Linux keeps unread output readable after the last close, and there a descriptor of the host would keep output from ending at the exit. On FreeBSD the source shows the leader's exit revoking the terminal and discarding at once while another descriptor is open, the opposite of macOS.

Closing ends output without losing it: ConPTY flushes its final frame and the pump drains the pipe to its end, and Unix closes only after output has been quiet. A ring that is full when the close lands parks the driver as it does before the close, rather than ending output with the transport's remaining bytes unread; a consumer slow to free space delays the end, and parked time is not silence, so the forced end below waits for it too. Waiting for exit never closes the transport.

The close does not always end the transport. ConPTY's `CreatePseudoConsole` duplicates its pipe ends as inheritable while it starts its console host, and a process the host application creates with handle inheritance in that window (`Process.Start` always does) inherits the output pipe's write end and keeps it open until it exits; MiniPty's own handles are all non-inheritable and cannot prevent it. Measured on Windows 11 build 26200: `ClosePseudoConsole` returns within 1 ms, ConPTY's final frame (`ESC[?9001l ESC[?1004l`) arrives within 1 ms of the close in every run, and the pipe breaks 3 ms after the close when nothing else holds it, but only when the inheriting process exits otherwise. So after the close the drain keeps ticking: once reads have been quiet for the same 100 ms, the pump ends output itself. The Windows driver cancels its posted read with `CancelIoEx`, takes what the pipe holds without waiting (as much as `PeekNamedPipe` reports), and reports end of output. The cancel is repeated every tick until the driver has finished, because a cancel that lands between two reads cancels nothing, and it is aimed by pump identity so a pooled driver that has moved on to another session is never interrupted. Disposal issues the same cancel after closing the pipe handle, so `DisposeAsync` does not wait for such a process either.

### Disposal

`Dispose` switches the pump to discarding output, signals the child, closes the transport, and returns without waiting for the child to exit. On Windows the pump keeps reading until the pipe ends, because closing a pseudo-console can wait until its final frame fits in the pipe. On Unix reading stops at once, even when the master stays open for a hung-up child (below): the loop keeps the master registered and discards there until the master closes. A session the loop no longer reads (its service failed, or its start failed before the master was registered) has its output discarded by the exit watcher instead. The ring returns to the pool once the pump has let go of it, which it does only with no read posted into it, so the driver never writes into an array that another renter owns; unread output no longer keeps it once the session is disposed. `DisposeAsync` additionally completes once the pump has stopped.

How the child is ended differs by platform:

| Platform | On dispose | Why |
|---|---|---|
| Unix | `SIGHUP` on the disposing thread (through the child's pidfd on Linux; see [Child exit observation](#child-exit-observation)). `close(master)` waits until the exit watcher has reaped the child or sent the kill below, and until then the watcher reads and discards the child's output, since the session's reading has stopped: the loop through the master's own registration, and for a session the loop no longer reads through a watch of the master's output (epoll `EPOLLET`, kqueue `EV_CLEAR`) that its 10 ms polling interval backs up. The exit watcher sends `SIGKILL` one second later unless it has reaped the child by then; the deadline is part of the watcher's wait timeout, so disposal allocates no timer. An `AppDomain.ProcessExit` handler sends the pending kills at once when the host exits normally, and a disposal after it, such as one in the host's own exit handler, kills at once. A host ended by a signal it does not cancel (a .NET console host receiving `SIGTERM` with no handler of its own, or with one that does not cancel it) raises no `ProcessExit`, so a child it disposed within the grace and that ignores `SIGHUP` keeps running. The kill and the watcher's reap share the pid lock, so a child the watcher has reaped is never signaled. | `SIGHUP` is the signal a shell receives when its terminal window closes: it saves its history and runs its exit hooks, and a program that catches it can clean up. Closing the last reference to the master also hangs the tty up and signals the session leader, which the child is (Linux `tty_vhangup`; FreeBSD `ttydisc_modem` and XNU `ttymodem`, both only while `CLOCAL` is clear). A copy of the master that another process holds also keeps the tty from hanging up until that process exits: glibc's `forkpty` opens the master without close-on-exec, and MiniPty marks it right after, so only a process the host forks in that instant gets a copy. On macOS and FreeBSD a child that sets `CLOCAL` gets no hang-up from the close at all. Measured on Linux with such a process holding the master: with the explicit signal the child's `HUP` trap ran within about 90 ms, without it the trap never ran and the child was killed at the deadline. The close waits for the child because its hang-up is a second `SIGHUP`: bash saves its history before it resends the hang-up to its jobs, and a second signal during the save ended it with its jobs still running ([lessons](../specs/lifecycle.md#lessons-learned)). A child that ignores `SIGHUP` and reads its terminal therefore waits in the read until the kill, where the close used to fail the read. MiniPty signals only the child. When the child exits, the system sends `SIGHUP` to the terminal's foreground process group (POSIX `_exit` for a controlling process), so a descendant in that group ends too unless it ignores `SIGHUP`. An interactive shell resends `SIGHUP` to its jobs before it exits (bash documents this), so its background jobs end as well. The previous `SIGKILL` gave the shell no chance to, and they kept running. A descendant that ignores `SIGHUP` (`nohup`), was disowned, or started its own session keeps running, as it does when a terminal window closes. `Kill()` stays `SIGKILL`; only disposal is graceful. |
| Windows | `TerminateProcess`, then `ClosePseudoConsole` | ConPTY has no hang-up signal to send first. |

## Child exit observation

Exit is observed by one watcher per platform for each load of MiniPty, which is one per process unless a host loads MiniPty into several `AssemblyLoadContext`s. Observing exit adds no per-session thread and no thread-pool worker, and the same thread is every session's I/O loop. Exit is recorded once; awaiters get a task created only on demand, library-internal waits use an allocation-free waiter slot, and the session's output drain is notified through an allocation-free listener.

| Platform | Mechanism |
|---|---|
| **Windows** | Background threads, each waiting on up to 63 child process handles with `WaitForMultipleObjects` (slot 0 is a wake event). The watcher owns a **duplicate** of each process handle and closes it when the child exits. |
| **Linux** | One thread blocked in `epoll_wait` over pidfds (`pidfd_open`, Linux 5.3+) plus a wake pipe opened with `pipe2(O_CLOEXEC)`. Each session keeps its child's pidfd, which the spawn opens right after the fork, and signals and reaps the child through it (`pidfd_send_signal`, `waitid(P_PIDFD)`). The watcher closes it after the reap. |
| **macOS / FreeBSD** | One thread blocked in `kevent` on `EVFILT_PROC` `NOTE_EXIT`, woken by an `EVFILT_USER` event on the same kqueue on macOS and by a wake pipe on FreeBSD. |
| **Fallback** | A child that cannot be registered (no pidfd, seccomp, or already exited before `kqueue` registration) joins a set the same thread polls every 10 ms, through the child's pidfd when it has one and with `waitpid(WNOHANG)` otherwise. So does every child while the watcher's epoll or kqueue could not be opened; each later registration tries to open it again, and once one succeeds the thread waits natively, with the children registered meanwhile still polled until they are reaped. |

The Unix watcher is the only code that reaps a session's child, through its pidfd on Linux and with `waitpid` otherwise. `HasExited`, `Kill`, and EOF staging read the recorded state instead; `Kill` and the reap share a lock so a signal can never reach a pid the reap has already released for reuse.

Code outside MiniPty can still reap a child: a host that reaps every child it has, or one that ignores `SIGCHLD` so the kernel reaps them. The child's pid is then free before the watcher notices, and a later process can get it. On Linux the spawn opens the child's pidfd right after the fork and, on Linux 5.4 and later, refuses one that names no child of the host. The child's signals and its reap then go through the pidfd, so neither reaches a process that gets the pid later. The pidfd is still opened by pid: if the child dies in that instant and other code reaps it, another child of the host that got the pid would pass the check. The reap through the pidfd needs Linux 5.4, and Linux 5.3 reaps by pid. Without a pidfd (macOS, FreeBSD, or Linux when the spawn gets none), a signal is sent only while `waitid` with `WNOWAIT` reports the pid as an unreaped child of the host, and the watcher reaps by pid. That check cannot tell the session's child from another child of the host that got the same pid. If such a reuse lands before the watcher notices the outside reap, the session sends its signals to that other child while that child runs, and the watcher can take that child's exit status when that child ends, unless other code reaps that child first. Otherwise a child that other code reaped is reported with exit code 0, because its wait status is lost.

Lessons learned:

- **Duplicated process handles make teardown unordered.** Closing a handle while another thread waits on it is undefined behavior. With the watcher owning a duplicate, a session closes its own handle at any time, and the child it kills on dispose drains its watcher slot without any unregistration handshake.
- **`RegisterWaitForSingleObject` costs about 270 bytes per session** (wait-handle wrappers, the registration object, its lock, a callback wrapper, and a completion work item). The shared `WaitForMultipleObjects` watcher costs nothing per session.
- **A closed pidfd can stay registered in epoll.** A child forked concurrently holds a duplicate of every parent descriptor until it execs, and epoll drops a registration only when every descriptor for the open file description is closed. The stale registration reported the same exit again with a pidfd number since reused for an unrelated descriptor, which closed live descriptors of other sessions and hung the parallel test suite. Registrations are now `EPOLLONESHOT` and are removed with `EPOLL_CTL_DEL` before the pidfd is closed.
- **A watcher kept in the native library is shared by every load of MiniPty.** A second `AssemblyLoadContext` that loads MiniPty gets its own managed watcher but the same native library, and its initialization found the first load's epoll and reused it. Both threads then waited on one epoll, each took exits of the other's children, which it did not know, and those sessions never reported exit. Measured on Linux with eight children per load exiting together: the test timed out in 3 of 3 runs. The managed watcher now owns its epoll or kqueue and its wake descriptors, and passes them to every native call.
- **A fallback can hide a failure that would otherwise last.** The watcher opened its epoll or kqueue once per load of MiniPty and never again. That was harmless while a session whose master could not be registered read on a reader thread instead; with the reader threads gone, a first open that failed for a moment (descriptors or kernel memory exhausted) would have failed every later start in the process. Each registration now retries the open. A test makes it fail in a load of MiniPty of its own, whose next start recovers, and with the retry removed that start failed.

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
1. The output pump reads PTY output from spawn (I/O completions on Windows, the exit watcher's loop on Unix)
2. One-shot stdin: WriteInputAsync → SendEof (Windows: Ctrl+Z + CR when bytes written, else pipe close; Unix: EOT) before wait
3. Detect child exit (exit watcher notification)
4. Post-exit drain: close ConPTY (Windows) or the master (Unix) once output is quiet; one-shot completion bounds this by OutputDrainGrace
5. One-shot completion waits for output reading to end (OutputReaderCloseTimeout); disposal releases process handles
```

Exact ordering differs slightly by OS; avoid forcing one sequence if it causes hangs on one platform.

## Concurrency

At minimum, separate:

- **Output read**—the session's output driver reads the PTY while the child runs (I/O completions on Windows, the exit watcher's loop on Unix); one consumer drains its buffer
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
