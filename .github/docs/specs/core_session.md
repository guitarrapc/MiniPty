# Core Session Specification

Implemented user-facing contract for the **MiniPty** core session API.

## Entry Point

`Pty.Start(PtyStartInfo)` spawns a child process attached to a new pseudo-terminal and returns a `PtySession`. It does **not** wait for the child to exit.

`PtyStartInfo` supplies:

| Member | Behavior |
|---|---|
| `FileName` | Executable path or name. Required. On Windows a `.bat` or `.cmd` file runs through `cmd.exe` with its arguments escaped for it; see [Batch files](#batch-files-windows). |
| `Arguments` | Arguments after the executable. Default is empty. Each reaches the child as one argument: quoted with the MSVCRT rules for a program, escaped for `cmd.exe` for a batch file. |
| `CommandLine` | Optional pre-escaped Windows argument command line. Mutually exclusive with `Arguments`; unsupported on Unix. Passed without any escaping, also to a batch file. |
| `WorkingDirectory` | Optional child working directory. Null inherits the parent working directory. |
| `Size` | Initial terminal columns x rows. Default is 80x24. Each dimension is clamped to the platform limit at spawn and on `Resize`: 1–32767 on Windows (ConPTY's signed 16-bit `COORD`), 1–65535 on Unix (`winsize`). `PtySession.Size` reports the value applied. |
| `Environment` | Optional child environment overlay. Null inherits the parent environment. Non-null entries override or remove inherited variables. |
| `TerminalName` | Optional Unix terminal name for `TERM`. Null or empty uses default behavior. Currently ignored on Windows. |
| `InheritCursor` | Default `false`. On Windows, start the child at the frontend's cursor instead of on a cleared screen (node-pty `conptyInheritCursor`); the frontend must answer ConPTY's cursor position query. Ignored on Unix, which already starts at the frontend's cursor. See [Inherited cursor](#inherited-cursor). |
| `KillProcessTree` | Default `false`. On Windows, run the child in a kill-on-close job, so `Dispose`, `Kill`, and the end of the host process, including a crash, end every process the child started. Ignored on Unix. See [Process tree](#process-tree). |
| `ConPty` | Default `null`: the ConPTY Windows ships. A `ConPtyLibrary` the application loaded runs the session on the out-of-band ConPTY (`conpty.dll` plus `OpenConsole.exe`) it supplies. Windows only. See [Out-of-band ConPTY](#out-of-band-conpty). |

## Batch Files (Windows)

`CreateProcess` runs a batch file by starting `cmd.exe` on the command line it is given, and `cmd.exe` does not parse that line by the MSVCRT rules every other program follows. An argument quoted by those rules could therefore close `cmd.exe`'s quoting and run a command: `x" & echo INJECTED & "` printed `INJECTED` from a `.cmd` file that only echoed its first argument. A host that passes user input to a tool's `.cmd` shim would run that user's commands.

When `FileName` names a batch file and `Arguments` supplies the arguments, MiniPty starts the interpreter itself:

| Rule | Behavior |
|---|---|
| Interpreter | `cmd.exe` from the system directory, never from `ComSpec`, run as `cmd.exe /e:ON /v:OFF /d /c ""script" args"`. `/d` skips the registry's AutoRun commands, `/v:OFF` keeps `!name!` from expanding, and `/e:ON` enables the extensions the percent escape needs; the registry can change all three defaults. `/c` strips the outer pair of quotes. |
| Quoting | An argument is quoted when it is empty, ends with a backslash, or contains a control character or any ASCII character other than a letter, a digit, or `#$*+-./:?@\_`, so every character `cmd.exe` treats specially is inside quotes. A quote inside is doubled. Backslashes before a quote, and before the closing quote, are doubled. |
| Percent | Each `%`, in an argument or in the script path, is written as `%%cd:~,%`. `%cd:~,%` is an empty substring of the current directory, so the first `%` survives and no `%name%` expands. |
| Rejected | An argument that contains a carriage return, a line feed, or a NUL throws `ArgumentException` before anything starts: `cmd.exe` ends or cuts its command line there, so no quoting can deliver it. A script path that contains a quote or ends with a backslash is rejected the same way. |
| Detection | By the extension after trailing spaces and dots, which Windows drops from a file name: `script.cmd.` is a batch file. |
| Lookup | Where `CreateProcess` would have found it, since `cmd.exe`'s own lookup (its working directory, then `PATH` with `PATHEXT`) differs: a name with a directory is relative to the host's current directory; a bare name is searched for in the host executable's directory, the host's current directory (skipped when `NoDefaultCurrentDirectoryInExePath` is set), the system directories, the Windows directory, and the host's `PATH`. A bare name that is not found fails with `Win32Exception` (`ERROR_FILE_NOT_FOUND`), as before. |
| Delivered | A program the script forwards `%*` to, as tool shims do, receives each argument exactly; verified for quotes, backslashes, `%`, `!`, `& | < > ^ ( )`, `= , ;`, tabs, empty arguments, and non-ASCII text. The script itself sees the escaped form in `%1` (`%~1` drops only the outer quotes). |
| Not covered | A raw `CommandLine` is passed unchanged, as its contract says. A script that uses an argument unquoted in a command (`%~1` in `call %~1`, for example) can still be made to run something; only the script can prevent that. Starting `cmd.exe` directly with `/c` and user input in `Arguments` is likewise `cmd.exe` parsing that input. |

Escaping was chosen over rejecting `cmd.exe`'s special characters: shims are called with paths, URLs, version ranges, and JSON, which contain `&`, `%`, `^`, and quotes, and the escaping above delivers all of them. Only the three characters no quoting can deliver are rejected.

## Spawn Environment

`PtyStartInfo.Environment` is an overlay on top of the parent process environment, not a replacement block.

| Value | Behavior |
|---|---|
| `Environment = null` | Inherit the parent environment. |
| `Environment["KEY"] = "value"` | Set or override `KEY` for the child. |
| `Environment["KEY"] = null` | Remove `KEY` from the child environment. Removing a missing key succeeds. |
| `Environment["KEY"] = ""` | Set an empty value on platforms that preserve empty environment variables. Windows children observe empty entries as missing. |

Environment keys are case-insensitive on Windows and case-sensitive on Unix. Invalid names are rejected at spawn time: empty names, names containing `=`, and names or values containing NUL are invalid. Duplicate-equivalent overlay keys are resolved by enumeration order; the last value wins.

On Unix, MiniPty removes inherited terminal-container and size variables before applying the explicit overlay: `TMUX`, `TMUX_PANE`, `STY`, `WINDOW`, `WINDOWID`, `TERMCAP`, `COLUMNS`, and `LINES`. This prevents a fresh PTY from inheriting stale parent terminal state. Because sanitize runs before the overlay, callers can still explicitly pass any of these variables.

On Unix, `TerminalName` sets `TERM` after the environment overlay. If `TerminalName` is null or empty and no `TERM` remains, MiniPty sets `TERM=xterm-256color` unless `TERM` was explicitly removed by the overlay. `Environment["TERM"] = ""` is respected as an explicit empty value on Unix. On Windows, `TerminalName` does not set `TERM`; use `Environment["TERM"]` to pass it explicitly.

MiniPty environment inheritance follows normal process-spawn behavior and is not a sandbox. Callers exposing PTYs to untrusted users must isolate the child process outside MiniPty, for example with OS users, containers, sandboxing, or explicit environment removal.

## Process Tree

By default, ending a session on Windows ends the child and the processes attached to its pseudo-console. A descendant in a console of its own, or a GUI program, keeps running, and when the host process crashes every descendant does ([Descendants](lifecycle.md#descendants)).

`PtyStartInfo.KillProcessTree = true` runs the child in a job object with kill-on-close. The child joins it at creation, and its descendants inherit it.

| Rule | Behavior |
|---|---|
| Ends the tree | `Dispose`, `Kill` (also after the child itself has exited), and the end of the host process in any way, including a crash: the kernel closes the job's only handle. |
| Leaves it running | The child's own exit. The session owns the tree until it is disposed, so a descendant of a finished child runs until then. |
| Breakaway | Not allowed. A program that asks to leave the job (`CREATE_BREAKAWAY_FROM_JOB`) fails to start in the session. |
| Host inside a job | Supported: jobs nest, as under CI runners and editors. |
| Failure | A job that cannot be created fails `Pty.Start`; there is no fallback to a session without it. |
| Unix | Ignored. Disposal hangs the child up as without it. |

The option is off by default because it also ends programs the user started from the terminal on purpose, such as an editor window.

## Inherited Cursor

ConPTY keeps its own screen buffer and starts by assuming that the frontend's screen is empty. By default its first output clears the screen and homes the cursor (`ESC[2J ESC[H`), which erases whatever the frontend showed before the child started: an editor's banner written ahead of the shell (VS Code's `initialText`), or the prompt and earlier lines of the terminal a console host runs in. A Unix PTY passes bytes through without a screen model, so the child simply continues at the frontend's cursor.

`PtyStartInfo.InheritCursor = true` gives Windows the Unix behavior. ConPTY is created with `PSEUDOCONSOLE_INHERIT_CURSOR`, asks the frontend for the cursor position (`ESC[6n`) as its first output, and continues from the reported position without clearing. The option is off by default because it needs an answer:

| Rule | Behavior |
|---|---|
| Answer | The frontend writes a cursor position report (`ESC[row;columnR`) to the session input. Terminal emulators (xterm.js, VS Code, Windows Terminal) do this on their own; with [Console](console.md) attach, the host terminal answers and the input pump forwards the answer, provided `Attach` runs before the output pump writes the query. |
| Until answered | ConPTY holds the child's console output, so a child that writes stalls. The in-box ConPTY has no timeout. A late answer resumes the child normally. |
| The report | Consumed by ConPTY; the child never reads it. |
| Never answered | `Kill` and `Dispose` still end the session and its output. |
| Output-only consumers | `CompleteAsync`, Capture, and similar consumers that never write input must leave it off, or write a report themselves. |

## Out-of-band ConPTY

Windows ships ConPTY in `kernel32.dll`, backed by the `conhost.exe` of the user's Windows build. That host renders the child's output and decides which VT sequences reach the frontend: the builds measured (Windows 11 26100 and 26200) consume mouse tracking modes (1000, 1003, 1006), DECCKM, terminal queries (OSC 11, DECRQSS, XTGETTCAP), and graphics protocols (Sixel, Kitty) instead of forwarding them, reorder forwarded sequences relative to text, and expose no clear operation. A terminal emulator on the in-box ConPTY therefore never learns that a program wants mouse events, and its behavior changes with Windows updates.

Microsoft publishes the implementation Windows Terminal carries as the NuGet package `Microsoft.Windows.Console.ConPTY`: `conpty.dll` plus `OpenConsole.exe`, one host process per session like the in-box one. MiniPty ships neither file and takes no dependency on the package; an application that wants it brings its own, and nothing changes for applications that do not.

| Rule | Behavior |
|---|---|
| Opt-in | The application references the package, loads the DLL with `ConPtyLibrary.Load(absolutePath)` or `ConPtyLibrary.TryLoadFromApplicationDirectory()`, and sets `PtyStartInfo.ConPty`. Per session, so a process can run both implementations and a host can say which one a session used. |
| Loading | By absolute path only; a bare name would be searched for along the loader's path and could bind a copy of `conpty.dll` an operating system ships. One instance per path, loaded for the process lifetime. The contract is the DLL's exports, not its version: `Load` resolves the five functions MiniPty calls (`ConptyCreatePseudoConsole`, `ConptyResizePseudoConsole`, `ConptyClosePseudoConsole`, `ConptyReleasePseudoConsole`, `ConptyClearPseudoConsole`) and throws `EntryPointNotFoundException` when one is missing. `FileVersion` is reported for diagnostics. |
| The host | `conpty.dll` launches `OpenConsole.exe` from beside itself, or from a subdirectory named after the operating system's architecture; when neither exists it launches the in-box `conhost.exe` instead, without an error. `ConPtyLibrary.HasConsoleHost` reports that probe before the first spawn. MiniPty's `buildTransitive` targets stage the host beside `conpty.dll` in a consumer's output, in both the runtime-specific and the portable layout, when the package is referenced (`MiniPtyStageConPtyHost=false` disables it). |
| Flags | None are passed beyond `InheritCursor`; the other flag bits have changed meaning between package versions. |
| End of output | After the child is attached, MiniPty releases its reference to the console server (`ConptyReleasePseudoConsole`), so the host exits with its last client and the output pipe breaks: output ends on the pipe break, 2–3 ms after exit measured, instead of after the 100 ms post-exit quiet interval the in-box path needs. Resize and clear keep working, as they use the signal pipe. |
| Startup query | The host asks the frontend what terminal it is (`ESC[c`, Primary Device Attributes) when the session starts, and consumes the first answer. A terminal emulator answers on its own; MiniPty never answers for it, because a second answer reaches the child as keystrokes. With the verified package version the host does not wait for the answer unless `InheritCursor` is set; versions before 1.25 waited 3 s per session for it. |
| `Clear()` | `PtySession.Clear()` and `PtyTerminal.Clear()` clear the host's buffer (`ConptyClearPseudoConsole`, cursor to the top). The host emits nothing for it, as the frontend has already cleared its own display; the next output is placed at row 1. A no-op on the in-box ConPTY and on Unix. |
| Verified version | `1.25.260930003`, pinned for the repository's tests and Windows samples only (`Directory.Packages.props`). Any published version loads; 1.25 is the recommended floor because of the startup query. |
| Trust | `Load` runs native code with the process's token, and the DLL launches the host beside itself with a full path (never a search): the trust boundary is the directory the two files live in, the same one the application's own binaries live in. The application passes only paths it controls; a path on a network share or removable media is accepted rather than rejected, because an application running from such a place loads its own code from there too; `TryLoadFromApplicationDirectory` probes nothing outside the application directory; the staging targets act only on the DLL resolved from the `Microsoft.Windows.Console.ConPTY` package. The package's `conpty.dll` and `OpenConsole.exe` are Authenticode-signed by Microsoft, and `conpty.dll` imports only Windows API sets, so no dependent DLL can be planted beside it. No token, user, or privilege is involved: `ConptyCreatePseudoConsoleAsUser` is not exposed. |
| Unix | A `ConPtyLibrary` cannot be loaded; `Load` throws `PlatformNotSupportedException` and `TryLoadFromApplicationDirectory` returns null. |

MiniPty ships no binary because it cannot guarantee the host's placement in every consumer's output layout (the package's own build files place it only for a direct, architecture-specific reference, so a .NET project gets `conpty.dll` and no host), because the version and its security updates belong to the application, and because most consumers, which only read output, do not need a 1 MB host process.

## Session Contract

| Member | Behavior |
|---|---|
| `Input` / `Output` | Raw byte streams. No line-ending translation. stdout and stderr are merged on `Output`. `Output` is a view over the session's output buffer: `ReadAsync` never blocks the calling thread; `Read` blocks, per the `Stream` contract. |
| `ReadOutputAsync` | Persistent bytes-only output streaming. Returns `IAsyncEnumerable<PtyOutputChunk>`. |
| `WriteInputAsync` | Writes UTF-8 text by default, or raw bytes. Does not close stdin. Never blocks the calling thread: bytes the terminal accepts at once are written synchronously (the usual case, allocation-free after the session's first write), and the rest is written in order in the background: on Unix by the session's reader thread as the child reads, on Windows by I/O completions as ConPTY takes input again. Writes reach the child in issue order, and `SendEof` takes effect after every write issued before it. A canceled token, a closed input, and a failed write are reported through the returned task rather than thrown from the call. Canceling a waiting write discards the bytes not yet handed to the terminal. Those already handed over still arrive, which on Windows include a pending part of up to 4 KiB that ConPTY has not read yet. |
| `SendEof()` | Signals end of stdin using platform-specific behavior. See [Lifecycle](lifecycle.md). |
| `Resize(PtySize)` / `Resize(PtySize, PtyPixelSize?)` | Resizes after spawn; Unix also reports optional pixel dimensions, while Windows ignores pixels. Child exit never makes it fail: while the transport is open the size is applied, and once the transport has been closed after exit the call is a no-op that keeps `Size`. Only disposal (`ObjectDisposedException`), invalid pixel dimensions, and an OS failure while the child is running throw. A running child is notified as on a terminal: `SIGWINCH` on Unix, and on Windows a `WINDOW_BUFFER_SIZE_EVENT` input record for a child that enabled window input. |
| `Clear()` | Clears the pseudo-console's screen buffer for a frontend that cleared its own display. Effective on Windows with an out-of-band ConPTY ([Out-of-band ConPTY](#out-of-band-conpty)); a no-op on the in-box ConPTY, on Unix, and once the transport has been closed after exit. Throws only after disposal and when the OS call fails while the child is running. |
| `WaitForExitAsync` | Waits for child exit and returns `PtyExitStatus` (exit code plus Unix termination signal). Cancellation stops waiting only; the child keeps running. Without a cancelable token every call returns the same task, so repeated waits allocate nothing. Disposal before the exit has been observed fails the wait with `ObjectDisposedException`, and a call made after disposal throws it. A wait started earlier still reports the status of a child whose exit was observed before disposal; after disposal, read `ExitStatus` instead. |
| `CompleteAsync` | Convenience API for one-shot input, wait, drain, and result materialization. See [Completion](completion.md). |
| `Kill()` | Terminates the child process without releasing handles. |
| `Kill(PtySignal)` | Sends a signal on Unix, through the child's pidfd on Linux when the kernel provides one, and otherwise with `kill(2)` once a check shows the pid is still an unreaped child. No signal is sent once MiniPty has reaped the child. Without a pidfd, another child of the host that got the pid after other code reaped the child would receive it. The child may handle or ignore catchable signals. On Windows the signal is advisory and the child is terminated (node-pty parity). Undefined enum values throw `ArgumentOutOfRangeException` on both platforms. |
| `HasExited` / `ExitCode?` | Reports the exit observed by the background exit watcher; reading it never blocks or reaps. `ExitCode` is null until the child has exited. On Unix the watcher keeps observing after disposal, so a child disposed while running reports what ended it: its own exit, 129 (`SIGHUP`), or 137 (`SIGKILL` after the grace period). |
| `ExitStatus?` | `PtyExitStatus` after exit; null before. |
| `ActiveProcessName` | Foreground process name for editor title polling on Unix; null when unavailable and on Windows. |
| `Dispose` / `DisposeAsync` | Ends the child if still running and releases handles without blocking. On Unix the child is hung up first (`SIGHUP`, the signal a shell receives when its terminal window closes, so it saves its history). The signal is sent on the disposing thread without blocking. The terminal's master stays open until the child is reaped or killed, so that hang-up is the only one the child gets while it runs, which lets a shell save its history and hang up its jobs. Its output is discarded meanwhile. A child that has not exited one second later is killed by the background exit watcher, and if the process exits normally before then (returns from `Main` or calls `Environment.Exit`), the kill is sent at exit. A host ended within that second by a crash or by a signal it does not cancel, such as `SIGTERM` to a console app, never sends the kill, so a child that ignores the hang-up survives. Calling `Kill()` first ends such a child at once but skips the hang-up. An interactive shell killed this way cannot resend the hang-up to its jobs, so a running background job keeps running. A host can instead cancel such a signal, end its sessions as in the app-exit row of [GUI hosts](#gui-hosts), and exit normally. The kills are then sent at exit. MiniPty signals only the child. When it exits, the system hangs up the terminal's foreground process group, so a descendant in that group ends too unless it ignores `SIGHUP`. On Windows it is terminated, and closing the pseudo-console ends every process attached to it; a descendant in a console of its own keeps running. In-flight output reads and exit waits fail with `ObjectDisposedException`. `DisposeAsync` also completes once the background output reader has stopped; every call waits, including one after `Dispose` and concurrent ones. Output chunk memory is invalid after either. Safe to call more than once and from several threads at once: the first call tears down, and handles are released exactly once. |

## Exit Status

`PtyExitStatus` reports `ExitCode` and `Signal`:

- `ExitCode` always equals `PtySession.ExitCode`. On Unix a signal-terminated child keeps MiniPty's existing `128 + signal` mapping (for example 143 for SIGTERM) — deliberately not node-pty's 0-on-signal, so a killed child never reads as success and there is one exit-code story across the library.
- `NodePtyExitCode` projects that status into node-pty's frontend shape: zero when `Signal` is present, otherwise `ExitCode`. Terminal bridges use this projection without changing core completion semantics.
- `Signal` is the raw OS signal number (`waitpid` `WTERMSIG`), null on normal exit and when the wait status was lost (ECHILD reap-lost path). Windows never reports a signal.
- `PtySignal` members are logical identifiers mapped to native numbers per platform (SIGUSR1/SIGUSR2 numbering differs between Linux and macOS/FreeBSD); reporting uses raw OS numbers.

## Embedder Patterns

MiniPty core is the PTY transport for multiple embedder shapes ([spec.md](../spec.md)). The core API is unchanged; patterns differ by who reads output and who writes input.

| Use case | Output consumer (exactly one) | Input source | Packages |
|---|---|---|---|
| **1 — General transport** | Embedder (`ReadOutputAsync` or raw `Output`) | Embedder (`WriteInputAsync`) | **MiniPty** |
| **2 — One-shot record** | `PtyCapture.RunAsync` (completion sink on the reader thread) | `PtyCompleteOptions` / capture options | **MiniPty.Capture** |
| **3 — Interactive host** | Embedder `ReadOutputAsync` (record + write host `stdout`) | **MiniPty.Console** host keyboard + optional embedder writes | **MiniPty** + **MiniPty.Console** |
| **4 — Editor terminal** | **MiniPty.Terminal** `PtyTerminal` (owns `ReadOutputAsync`) | Frontend key events → bridge → `WriteInputAsync` | **MiniPty** + **MiniPty.Terminal** |

**Use case 3** does not add a second PTY output reader. [Console](console.md) configures the host terminal and forwards host stdin bytes; the embedder keeps the single `ReadOutputAsync` enumeration for PTY → host display and recording.

**Use case 4** does not use **MiniPty.Console** (no host console attach). The push facade, flow control, and WebSocket bridge live in [Terminal](terminal.md).

## Backpressure

A PTY has backpressure. Each session reads its output from spawn on a dedicated background thread into a bounded buffer, and the session's single output consumer drains it. When the buffer is full, reading stops and the OS PTY buffer fills, so a child whose output nobody reads still blocks eventually. Callers must use `ReadOutputAsync`, `CompleteAsync`, `PtyCapture.RunAsync`, or continuously read `Output` themselves.

Reading from spawn means early output is never stranded: a child does not stall on its first writes before a consumer attaches, and output produced before the consumer starts is delivered first.

`ReadOutputAsync` is the supported high-level persistent output API. It is bytes-only and performs no text decoding. It does not drop data.

`PtyOutputChunk.Data` is ephemeral: it is valid only until the next successful `MoveNextAsync` call on the same enumeration, or until the session is disposed. Callers that need to retain bytes must copy them before advancing the reader. Chunks are handed out in place from the buffer and the enumerator is reused per session, so enumeration allocates nothing per chunk.

Only one output consumer is active per session (see [Lifecycle](lifecycle.md#output-consumer-exclusivity)). A concurrent reader attempt throws `InvalidOperationException`.

Managed buffering is bounded by the buffer's slot count times its slot size (currently 4 × 4 KiB). Each chunk holds one transport read plus the output that arrives within a short micro-window after it, so bulk output arrives in few full chunks without delaying the first byte of interactive output. Small reads fill the slots sooner: a few separate ConPTY frames can fill the buffer, so a session whose output nobody reads may not reach end of output after exit until a consumer reads, and the consumer then gets all of it. The slot geometry and the maximum per-chunk size are implementation details and may change. One-shot `CompleteAsync` and `PtyCapture.RunAsync` are the exception by design: their result holds the whole output, so it grows with it.

## Caller Context

Asynchronous members never block the calling thread and never run library code on the caller's `SynchronizationContext`, so they can be awaited from a UI thread. Only the caller's own continuation resumes on its captured context, and only when the operation had to wait; `ConfigureAwait(false)` opts out as usual.

| Member | Behavior on the caller's thread |
|---|---|
| `ReadOutputAsync`, `Output.ReadAsync` | Complete synchronously while output is buffered; otherwise resume when output arrives. Under a `SynchronizationContext` or a non-default `TaskScheduler`, a read that would complete synchronously after 8 ms of reads that all did completes through that context instead, once, so the dispatcher runs its queue before the consumer resumes. |
| `WaitForExitAsync` | Awaits the exit watcher's notification. |
| `CompleteAsync`, `PtyCapture.RunAsync` | Output is collected on the session's reader thread; the caller awaits the input write, exit, and end of output. `Input`, when set, is written as by `WriteInputAsync`. |
| `DisposeAsync` | Returns at once and completes when the reader has stopped, for every caller. |
| `Output.Read`, `Input.Write` | Block, per the `Stream` contract. |
| `WriteInputAsync` | Completes at once when the terminal takes the bytes; otherwise when the rest has been written in the background. That happens when the terminal's input buffer is full: on Unix when the child has not read the tty input queue, and on Windows when ConPTY has not read the 4 KiB input pipe, which it stops reading while output nobody reads has backed up. A macOS tty in canonical mode holds back a write only while a complete line is queued. Once 1,024 bytes of a line are pending, later bytes are dropped, and the write still completes (see [macOS line limit](../references/pty_crossplatform.md#unix-stdin-eof)). |

## GUI Hosts

A desktop terminal control (WinUI, WPF, WinForms, Avalonia) can drive a session entirely from its UI thread. The caller-context contract above is what makes that safe; this section is the recommended shape.

| Host concern | Recommended pattern | Why |
|---|---|---|
| ConPTY (Windows) | Reference the `Microsoft.Windows.Console.ConPTY` package and start every session with `ConPty = ConPtyLibrary.TryLoadFromApplicationDirectory()`. Log `HasConsoleHost` and `FileVersion`; `HasConsoleHost` false means `OpenConsole.exe` was not staged | The in-box ConPTY consumes mouse tracking modes, DECCKM, terminal queries, and graphics protocols instead of forwarding them, so a program's request for mouse events never reaches the emulator ([Out-of-band ConPTY](#out-of-band-conpty)). The out-of-band implementation forwards them, supports `Clear()`, and ends output as soon as the child exits. Without a staged host it silently runs on the in-box `conhost.exe`. `TryLoadFromApplicationDirectory` returns null on Unix and when the DLL is not staged, so the same line works on every platform and falls back to the in-box ConPTY |
| Output | `await foreach (var chunk in session.ReadOutputAsync())` in the UI thread's own async method, decoding each chunk in place with an incremental `Decoder` (a chunk can split a multi-byte sequence) | `MoveNextAsync` never blocks, and when it waits the continuation resumes on the dispatcher, so the chunk is processed on the UI thread with no copy and no marshaling. The chunk is valid until the next `MoveNextAsync`; copy it only if it must outlive that. A handler slower than the child's output always finds the next chunk buffered, so after 8 ms of synchronous chunks the next one is delivered through the dispatcher: input and rendering keep running during a flood, and nothing is needed in the loop for it |
| Push transports | `PtyTerminal` from **MiniPty.Terminal** | Its handlers run on background threads: marshal to the dispatcher and copy any bytes the UI retains |
| Input | `await session.WriteInputAsync(...)` from key handlers | Completes at once when the terminal takes the bytes, otherwise in the background; never blocks the UI thread |
| Layout | `session.Resize(...)` from size-changed events, at any time | Dimensions clamp to the platform limit, a zero-sized window before layout clamps to 1×1, and a resize after child exit and transport close is a no-op |
| Exit | `await session.WaitForExitAsync()` (or `PtyTerminal.Completion`) | Completes from the exit watcher, and the wait holds no thread. Disposing the session before the child exits, as a tab close does, fails this wait with `ObjectDisposedException`, so a host that keeps such a wait running catches it |
| Tab close | `await session.DisposeAsync()`; `Dispose()` when nothing can be awaited | Neither blocks; `DisposeAsync` also completes once the background reader has stopped. On Unix the shell is hung up and gets a one-second grace while the host keeps running |
| App exit | In a shutdown step the app waits for: `session.Kill(PtySignal.Hangup)`, then `await session.WaitForExitAsync(token)` with a timeout, catching the `OperationCanceledException` the timeout throws, with `DisposeAsync()` in `finally` (or `await using`) so it runs either way. With several tabs, hang up every session first and wait for all of them under one timeout | Disposal's grace ends when the process exits, because the pending kill is sent at exit. Hanging up and waiting first gives the shell its time to save history before the app goes away. The dispose must still run: a session that is never disposed gets no kill at exit, so a child that ignores the hang-up would outlive the app. Where the close handler cannot be awaited, dispose at once instead, as the samples do. On Windows `Kill` terminates at once |
| Many tabs | Nothing to do | A session holds one dedicated reader thread and no thread-pool worker, so idle tabs do not starve the host's pool |

Hosts that keep sessions in a non-UI context should know that, with no `SynchronizationContext` current, a waiting `MoveNextAsync` resumes through the thread pool like any other `await`; a host that blocks many pool workers (for example in `Process.WaitForExit`) delays those resumptions along with everything else in the process.

[samples/WinUiTerminal](../../../samples/WinUiTerminal) (code-only WinUI 3, Windows) and [samples/AvaloniaTerminal](../../../samples/AvaloniaTerminal) (Avalonia; Windows, Linux, macOS) are terminal emulators on one shared VT parser and screen buffer, [samples/TerminalEmulator](../../../samples/TerminalEmulator), that follow this table with no `Task.Run`; only the drawing and input layer differs between them. Each sample has a single window, whose close ends the app, so they dispose at once on close (the Tab close row) rather than hanging up and waiting first.

## Lessons Learned

- The textbook glibc `WIFSIGNALED` macro relies on a signed-char cast that a direct C# transcription loses, misclassifying the stopped marker `0x7f` as signaled. Unreachable because MiniPty never asks for stopped children (no `WUNTRACED`), but load-bearing once the termination signal became public API, so the managed decoder used the explicit `(status & 0x7f) != 0 && (status & 0x7f) != 0x7f` form. Decoding has since moved into the native shim, which uses the platform's own macros, or the `waitid` siginfo when it reaps through a pidfd on Linux.
- Environment overlay is safer for MiniPty than node-pty-style replacement because Windows child startup is fragile when inherited variables such as `SystemRoot` are accidentally omitted.
- Unix terminal-size variables such as `COLUMNS` and `LINES` can make a fresh PTY behave like the parent terminal. Sanitizing them before overlay avoids stale child-visible terminal state.
- Windows does not preserve empty environment variables as child-visible empty values. MiniPty keeps the API distinction so Unix can express empty values, but Windows children observe them like missing variables.
- A fixed public buffer capacity is a poor contract for `ReadOutputAsync`: it turns an allocation/backpressure tuning knob into observable API surface. The stable contract is bounded no-drop streaming with producer wait; capacity should remain internal unless a future options API exposes it deliberately.
- On Windows ConPTY, `PeekNamedPipe` often reports zero pending bytes even when more output is in flight. The output reader therefore appends to a slot with non-blocking continuation reads (`PIPE_NOWAIT`) within a short micro-window before publishing it, so bulk output batches without delaying the first byte of interactive output.
- Kill-on-cancellation must not rely on a token registration made alongside `WaitAsync(token)`. `CancellationTokenSource` runs callbacks in reverse registration order, so `WaitAsync`'s callback resumes the waiter first, and the waiter's cleanup unregisters the kill callback before it runs. The waiter kills in its own cancellation handler instead.
- Completing a consumer wait can throw. A wait completes by posting the consumer's continuation to its captured context, on the reader thread, a disposing thread, or a canceling (possibly timer) thread; a context whose `Post` throws (a UI dispatcher already shut down) crashed the process from the reader or timer thread. Every completion of an output wait swallows that exception, because that continuation can never run. Task-based waits such as `WaitForExitAsync` follow the BCL, which rethrows it on the thread pool and still terminates the process.
- An exit listener must be set with a full fence. The session sets the listener and then checks for an exit that happened earlier, while the watcher records exit and then reads the listener; with release/acquire ordering both sides missed each other on Apple Silicon in a litmus test, and the post-exit drain was never scheduled.
- A consumer wait must be completed outside the lock that guards its state. Completing it under the lock runs the consumer's continuation inline, and that continuation's `CancellationTokenRegistration.Dispose` blocks until a concurrently running cancellation callback returns, while the callback blocks on the lock. The output pump claims a wait with a compare-and-swap and completes it without holding any lock.
- Resize must tolerate child exit. GUI hosts forward layout changes asynchronously to child exit, so a size change can arrive after post-exit drain has closed the transport; throwing there terminated WinUI hosts from a size-changed handler. Defining the no-op by transport state rather than by `HasExited` keeps resize-after-fast-exit working while the transport is still open, and on Windows a reference on the pseudo-console handle keeps a concurrent drain close from invalidating it mid-call.
- Pixel dimensions fit Unix's 16-bit `winsize` fields. MiniPty clamps larger caller values to `ushort.MaxValue`; zero remains a valid "unknown" pixel dimension.
- The in-box ConPTY's inherit-cursor wait is unbounded. Measured on Windows 11 build 26200: without an answer to `ESC[6n`, `cmd /c echo` had not exited after 15 s; an answer sent after 3 s resumed it at once. Out-of-band ConPTY 1.25 bounds a related startup wait to 1 s (G7 in [plan_gui_host_readiness.md](../plans/plan_gui_host_readiness.md)), so the in-box behavior must not be assumed from the redistributable's source. This is why the option stays opt-in rather than defaulting on for terminal frontends.
- Inherit-cursor was first deferred as a fix for a ConPTY cursor bug that nobody had reported. Its real use is a frontend that already shows content when the child starts: VS Code sets it whenever a terminal has `initialText`, and a console host keeps the user's screen with it (verified in a VT terminal: without it the prompt and earlier lines were erased; with it they stayed and the child's output followed).
- Foreground process lookup belongs behind the existing Unix native boundary: `tcgetpgrp` supplies the foreground process group without spawning `ps`, preserving NativeAOT and avoiding a helper process on each editor poll. A process-group id is not always a live process id: after a pipeline leader exits, lookup must fall back to a remaining group member rather than reporting no active process.
- Output must be read on a thread the library owns. The former producer started on the caller's captured context (through `Task.Yield`) and then blocked inside `ReadFile` / `read(2)`, which deadlocked a UI thread before the first chunk and froze it on every idle read; every idle session also parked a thread-pool worker. A per-session reader thread that completes allocation-free waits, whose continuations run asynchronously, removes all three.
- A batch file's arguments are parsed by `cmd.exe`, not by MSVCRT rules, so the quoting that is correct for every program was an injection for `.bat` and `.cmd`. Two details only show up when trying: an injected `echo` can print its text with a stray quote after it (`INJECTED"`), so a test that looks for the exact line misses a working injection; and detecting the batch file by the name as given misses `script.cmd.` and `script.cmd `, which Windows opens as `script.cmd`.
- Never blocking is not enough for a UI consumer; it must also give the thread back. A read that completes synchronously never returns to the dispatcher, so a handler slower than the child's output found the next chunk buffered every time and kept the UI thread for the whole flood: with 2 ms of work per chunk, a callback posted to the UI context waited over a second (G12 in [plan_gui_host_readiness.md](../plans/plan_gui_host_readiness.md)). A yield must also be completed only after the awaiter has registered: a value task that is already complete when it is awaited runs the continuation inline, so the reader completes the yield from `OnCompleted`, which makes the core post it to the captured context.
- Reuse reader threads across sessions. A bare thread start allocates 137 bytes, and one pooled-array return on a fresh thread raised that to about 4.9 KB: the array pool allocates a per-thread cache on the first return, and the returned array is stranded in that cache when the thread exits. One-shot completion grows its result buffers on the reader thread, so a thread per session would pay this on every spawn. Parked reader threads serve later sessions and exit after 30 seconds idle.
- One-shot completion writes into a sink on the reader thread instead of draining the buffer from a consumer task. With a bounded buffer, a consumer that only started draining after exit would block a bulk-output child before it could exit. Writing on the reader thread needs no consumer thread and removed the pump task, the polling drain, and the timeout tasks: about 0.7 KB less per `CompleteAsync`.
- Coalescing needs a budget per chunk, not only a window per read. Each Unix continuation read waited its own 1 ms window, so a child writing small pieces faster than that (a progress meter, token streaming) had its output held until a 4 KiB slot filled: up to about 150 ms on macOS at one write every 0.5 ms. A slot now starts no continuation read 2 ms after its first read, on both platforms, so it is held for at most about 3 ms on Unix and 2.5 ms on Windows.
- Measure a sub-millisecond window with `Stopwatch`, never with `Environment.TickCount64`. The ConPTY window was "1 ms" by `TickCount64`, which on Windows lasts until the next clock tick, up to about 16 ms. While a child wrote small pieces, the reader spun in its `Thread.Sleep(0)` loop for most of every tick: about 80% of a core for 20-byte writes every 1–10 ms. ConPTY's bulk slices arrive 20–45 µs apart, so a 500 µs `Stopwatch` window keeps 32 KiB in 13 full chunks (12–13 before) and cuts that reader load to about 10% at 5–10 ms intervals (about 40% at 1 ms). Trickle latency fell from p50 8 ms / max about 30 ms to p50 0.6 ms / max about 13 ms; the rest is ConPTY's own rendering, which delivers some writes up to about 15 ms late even with coalescing disabled.
- Coalescing belongs to the reader, so every consumer gets it. Capture used to record one chunk per transport read; slots that keep appending within the micro-window cut a 32 KiB capture's chunks and the metadata they cost (about 5 KB less on Windows).
- A Unix reader cannot be stopped by closing the master: `close(2)` does not interrupt a `read` or `poll` blocked on the descriptor in another thread, and the number can be reused while that thread still uses it. The reader waits on the master and a wake descriptor together, and the master is reference-counted so it closes only after the reader, input writes, resize, and EOT writes release it. This also resolves the resize race in which a concurrent drain closed the master during the `ioctl`.
- Let go of pooled resources before publishing completion. The reader first signaled end of output and only then dropped its reference to the buffer, so a consumer that disposed at once sometimes left the final release to the reader thread. The array then went back to the pool from that thread and stayed in its per-thread cache, and the next session's rent allocated a fresh 16 KiB array: the streaming benchmark intermittently averaged 7.2 KB instead of 1.7 KB. Releasing the buffer and parking the reader thread before completion becomes observable makes both deterministic. For the same reason a one-shot sink finishes its result (decoder flush, buffer detach, scratch return) on the reader thread at end of output: detaching on the thread that resumed the caller left grown result buffers in a worker's cache, and the next run's growth on the reader thread allocated again (intermittent 7 KB and 119 KB averages on the one-shot benchmarks, also seen before this change).
- Post-exit quiet means the reader could read and found nothing. Time parked on a full ring had counted as silence, so a drain tick right after a stalled reader resumed could close a Unix master that still held output; resuming now restarts the quiet interval. A drain tick also needs the exit time: exit waits complete before the exit listener records it, so a completion could schedule ticks that read every deadline as long past. Ticks now wait for the exit to be recorded. Both came from review, not from failing tests.
- Check ring capacity on its own, never as the alternative branch to "a sink is attached". A failed completion can detach its sink between that check and the drain, and the reader then read into a full ring, overwriting an unread slot.
- A full ring after the close is not end of output. The pump used to finish when the post-exit close found the ring full, with whatever the transport still held (on Windows, ConPTY's frames flushed by the close) unread. Loaded CI runners, where the consumer released slots late, delivered 1–7 of 8 flushed bytes. The reader now parks on a full ring after the close as before it, and reads on once a slot is free.
- Scripted transports paced by sleeps need an order of magnitude of margin over the period they test. Reads every 50 ms against a 100 ms quiet period passed on desktops and failed on every hosted macOS run, where a sleeping reader thread was scheduled 100–200 ms late; the scripted backends now report a 500 ms quiet period through `IPtyBackend.PostExitQuietMs` while the platform backends keep 100 ms.
- Stream's async-over-sync defaults are a trap for a stream over a blocking transport. `Input.WriteAsync` and the bytes overload of `WriteInputAsync` ran the write on a thread-pool worker and allocated 112–248 bytes per call. The input stream now overrides every asynchronous overload, and a write the terminal takes at once completes synchronously, allocation-free after the session's first write.
- On Unix the session's reader thread writes the input a full tty input queue cannot take yet: it already waits on the master, so write readiness joins the same wait. The master is non-blocking, so callers never block, and `Dispose` fails queued writes itself rather than waiting on a writer. EOF is ordered input too: staged EOT waits until nothing is queued, or it would overtake input the child has not read.
- ConPTY takes input into its own buffer whether or not the child reads, but only while it can write its output. With a child writing output that nobody reads, ConPTY stops reading input after about 4 KiB, and a synchronous write blocked its caller; on a UI thread that also runs the output loop, that is a deadlock. MiniPty's end of the Windows input pipe is therefore overlapped. A write the pipe takes at once still completes synchronously, and allocates nothing after the first write creates the overlapped state. The rest is written by I/O completions, so no thread waits. The input benchmarks were measured with a child that reads, so they could not show this.
