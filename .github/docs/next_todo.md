Spawn options, persistent output streaming, lifecycle hardening, timestamped capture, the interactive sample, MiniPty.Console (use case 3), and the editor terminal backend (use case 4) are implemented. See [specs/core_session.md](specs/core_session.md), [specs/lifecycle.md](specs/lifecycle.md), [specs/capture.md](specs/capture.md), [specs/console.md](specs/console.md), [specs/terminal.md](specs/terminal.md).

GUI host readiness is implemented, and its plan is retired: caller-context safety and thread-pool independence ([Caller context](specs/core_session.md#caller-context)), exit-tolerant resize, platform size limits, GUI host guidance with WinUI 3 and Avalonia samples ([GUI hosts](specs/core_session.md#gui-hosts)), bounded end of output on Windows, explicit Unix termios with `IUTF8`, hang-up before kill on Unix dispose, the opt-in out-of-band ConPTY with `Clear()` ([Out-of-band ConPTY](specs/core_session.md#out-of-band-conpty)), opt-in process-tree cleanup ([Process tree](specs/core_session.md#process-tree)), bounded UI-thread occupancy during output floods, and child-`PATH` program lookup on Windows ([Program lookup](specs/core_session.md#program-lookup-windows)). Its lessons live in those specifications and in [platform_support.md](specs/platform_support.md), [pty_crossplatform.md](references/pty_crossplatform.md), and [gui_terminal_samples.md](references/gui_terminal_samples.md).

Thread-free I/O for web hosts that keep many sessions open is implemented, and its plan is retired: Windows reads every session through I/O completions and Unix on one loop, the exit watcher's thread (epoll on Linux, kqueue on macOS and FreeBSD), so an idle session holds no thread and no output buffer ([Output pump](references/pty_crossplatform.md#output-pump), [Why this engine](references/pty_crossplatform.md#why-this-engine)); the output buffer is a 16 KiB byte ring, and a chunk is all contiguous unread output ([Backpressure](specs/core_session.md#backpressure)); one-shot sinks cost the same on any delivering thread ([capture.md](specs/capture.md)); and the built-in bridges join bursts and streams of short lines into larger messages ([Output frames](specs/terminal.md#output-frames)). The reader threads, and the `MiniPty.UseOutputReaderThreads` switch that could bring them back, are removed. Along the way it fixed a pre-existing race in the output pump that could end a session and lose its remaining output under a flood, a macOS loss of a child's last output at its exit when the consumer had stopped reading, and a regression in which `CompleteAsync` and Capture with `DecodeOutput` threw for a backlog of several reads. Its lessons live in those documents and in [lifecycle.md](specs/lifecycle.md) and [platform_support.md](specs/platform_support.md), and the choice of kqueue in the readiness probe's results ([Readiness on a pty master](references/pty_crossplatform.md#readiness-on-a-pty-master)). What else is left is listed below. Remaining deferred and won't-do node-pty parity items are recorded as non-goals in [specs/terminal.md](specs/terminal.md).

A pre-release review against node-pty, Porta.Pty, WezTerm's `portable-pty`, and creack/pty is done, and its gate plan is retired. It found and fixed four things:

- **The Unix `sh` fallback** now runs only for `ENOEXEC`, as `execvp` does ([Plain scripts and shell fallback](references/pty_crossplatform.md#plain-scripts-and-shell-fallback)).
- **Unix start failures** now fail `Pty.Start` with `Win32Exception` and the `errno` ([Start failure report](references/pty_crossplatform.md#start-failure-report)).
- **The README** now states which native files ship, and calls FreeBSD experimental ([platform_support.md](specs/platform_support.md)).
- **The Unix exec payload** is freed when the spawn returns. The reason documented for holding it was wrong ([lifecycle.md](specs/lifecycle.md)).

Features other libraries offer were decided as non-goals in [terminal.md](specs/terminal.md#non-goals), except a session's tty path, deferred below. Everything was verified on Linux and macOS.

## Release

- None of the GUI host work is released. `v1.3.0` (2026-07-15) predates it, and `Directory.Build.props` still says `1.3.0`.
- The release breaks the API: `WaitForExitAsync` returns `Task<PtyExitStatus>`, and `WaitForExitStatusAsync` is removed. Behavior a release note should name: `Dispose` no longer blocks, and chunk memory is invalid after it; output is read from spawn into a bounded 16 KiB buffer, and a `ReadOutputAsync` chunk is all the contiguous output buffered when it is taken (up to 16 KiB), not one read; exit waits no longer close the transport, so on the in-box ConPTY before Windows 11 24H2 output ends about 100 ms after the last output rather than at exit (from 24H2 it ends at exit, as on the out-of-band ConPTY); `Resize` after exit no longer throws, and sizes clamp to the platform limit instead of 512; Unix `Dispose` hangs the child up and kills it one second later instead of killing at once; `SendEof` with no input sends Ctrl+Z + CR instead of closing the pipe; on Windows a `.bat` or `.cmd` runs through `cmd.exe` with its arguments escaped, and a bare `FileName` is looked up on the child's `PATH` when `Environment` sets or removes it; `Pty.Start` throws `ArgumentException` for a NUL in `FileName`, an argument, `WorkingDirectory`, or `CommandLine` on every platform, where it used to start whatever the string named before the NUL; on Windows a program under a path of 260 characters or more starts when the host opted into long paths, where it used to fail with `ERROR_FILENAME_EXCED_RANGE`; on macOS a child that exits while its last output is still in the terminal, because the output buffer is full, finishes exiting once a consumer reads, where macOS used to discard up to 1 KiB of that output 0.6 s after the exit; output is read through I/O completions on the runtime's thread pool on Windows, and by one loop, the exit watcher's thread, on Unix, instead of a reader thread per session, so an idle session holds no thread and no output buffer, chunks are no longer held for a coalescing window (500 µs on Windows, 1 ms on Unix), and a Unix session whose master that loop cannot register fails `Pty.Start` with `IOException`; the built-in bridges join output that arrives in a burst, and streams of short lines read one by one, into larger messages, so such output waits up to about 2 ms, and while a bridge waits a deadline tick runs about every millisecond (on Windows on a thread of its own, which stays 30 s after the last burst).
- More behavior a release note should name, from the pre-release review:
  - On Unix, `Pty.Start` throws `Win32Exception` with the `errno` (`ENOENT`, `EACCES`, …) for a program that cannot be executed or a `WorkingDirectory` that cannot be entered. It used to return a session whose child exited 127 or 126.
  - On Unix, only a file without a shebang (`ENOEXEC`) runs through `sh`. A script on a `noexec` mount started directly, and a script whose interpreter is missing, now fail instead of running under `sh`. A non-executable file earlier on `PATH` is now skipped for the executable in a later entry, where it used to run through `sh`.
  - FreeBSD is documented as experimental.
- `MiniPty.Terminal` and `MiniPty.Capture` use MiniPty's internals while each declares MiniPty with a lower bound only. That is settled: the packages are released and updated together, so no version pin is needed.
- The README samples under `samples/SingleFiles` (`README_MiniPty.cs`, `README_MiniPty.Console.cs`, `README_MiniPty.Capture.cs`) pin `MiniPty@1.2.0`, but the first two already use the new exit API (`(await session.WaitForExitAsync()).ExitCode`), so they do not build until the pins move to the release. CI does not build them.

## Deferred Until an Embedder Needs It

### Unix disposal grace as an option

The one-second hang-up grace before the kill (`UnixPtyBackend.DisposeHangupGraceMs`) is fixed. It can become an option if an embedder needs a different one.

### Windows exit observation in O(1) threads

Windows exit watchers each wait on up to 63 children with `WaitForMultipleObjects`, so a host adds a thread per 63 sessions. Thread-pool waits or another mechanism can remove that once it matters.

### Windows output completions on a starved pool

Windows output completions need a thread-pool worker, as Kestrel's sockets do, so a starved pool delays output there; Unix reads on its own loop regardless. A dedicated completion port with one MiniPty thread is the fallback if that proves to matter.

### Environment baseline for multi-tenant hosts

`Environment` is an overlay on the host's environment, so a web server's secrets (connection strings, cloud credentials) reach every shell unless each key is removed. A minimal-base option needs its own design, because Windows children fail to start without variables such as `SystemRoot`.

### Out-of-process PTY host guidance

A web host that serves untrusted users would run MiniPty in a separate, less privileged process or container and talk to it over `PtyStdioBridge`, as VS Code runs node-pty in its pty host. That gives crash and privilege isolation and lets the web tier restart without ending shells. It is documentation and a sample, not a library change.

### A session's tty path

node-pty (`ptsName`), `portable-pty` (`tty_name`), and creack/pty (`Name`) expose the path of the terminal's slave device, such as `/dev/pts/3`. No known embedder needs it. Adding a read-only property later breaks nothing, while removing one would.

If an embedder asks:

- **Getting the path:** it is cheap to read once at spawn. macOS already reads it with `TIOCPTYGNAME`, and Linux and FreeBSD can use `ptsname_r` on the master.
- **Windows:** ConPTY has no device, so the property is null there, as `ActiveProcessName` is.
- **Naming:** name it for a path (`TtyPath`). `PtyStartInfo.TerminalName` already means the `TERM` value.
- **Reading termios:** a caller that wants to read the termios can open this path with `O_NOCTTY` and call `tcgetattr`, so no termios API is needed for that.

### Metrics

`System.Diagnostics.Metrics` counters (active sessions, bytes, full-buffer stalls, loop delay) are AOT-safe and useful to web operators. Whether to add them is a separate decision.

## Unverified

- **`ClosePseudoConsole` on older Windows builds.** On build 26200 it returned within 1 ms in every measured session, also while an unrelated process held the output pipe open. Whether it can block on Windows 10, or on Windows 11 before 24H2, while output is not drained is not verified; disposal switches the pump to discarding output before the close for that reason. If it can block, `Dispose` must close off the calling thread and `DisposeAsync` must include the close in its wait. Also unverified: whether, on a build where the close waits for the console host, a process that inherited the output pipe keeps that host alive and makes the close wait for it too.
- **Minimum macOS version of the native binaries.** `scripts/build-native.sh` passes no `-mmacosx-version-min`, and no workflow sets `MACOSX_DEPLOYMENT_TARGET`, so `libminipty_unix.dylib`, the static archive, and `minipty_spawn_helper` declare the build host's macOS as their minimum (`minos 26.0` in `otool -l`), and the release builds them on `macos-26` runners. Linking a NativeAOT sample against the archive warns about it. Whether an older macOS refuses to load them was not tested.
- **FreeBSD** is tested neither locally nor in CI. Its termios (`VERASE2`, and `_POSIX_VDISABLE` being 0xff), whether it revokes the tty when the session leader exits, as macOS does, and the readiness behavior `kqueue` was chosen on, which the Unix loop and its coalescing tick run on, follow from source only. `scripts/pty-readiness-probe.c` checks the readiness part where a FreeBSD machine is available.
- **Host exit on macOS under NativeAOT.** A host that disposes a session whose child ignores `SIGHUP` and then exits sends the pending kill at exit. On macOS that was probed under CoreCLR and NativeAOT with an earlier version that armed a timer per disposal, not with the current exit-watcher deadline; CI runs the CoreCLR test (`HostExitKillsAChildStillInItsHangupGrace`) there.
- **An echo during a flood through a bridge** is not tested against the coalescing bound; the tests time the end of a burst only.
- **Output buffer geometry.** The 16 KiB ring keeps the size of the four 4 KiB slots it replaced, which were chosen because every allocation benchmark improved with them, and a read takes up to 4 KiB. Throughput has not been tuned for either size.

## Rare Test Timeouts Under Heavy Load

- **Windows:** in `QueuedInputIsDeliveredInOrderWithEofAfterItWhileOutputIsBackedUp`, a blocking write issued behind a queue that had stayed pending for 500 ms completed within 100 ms once in 11 runs of the EOF tests under 3× CPU oversubscription on a desktop, before `SendEof` was called: the console host took input for a moment while its output was still backed up, so the test's premise did not hold and the EOF path was not involved. Not seen on hosted runners since the flood tests were reworked.
- **macOS:** `SendEofEndsInputAfterACanceledWriteLeftAPartialLine` timed out once in 48 full-suite runs with 64 busy processes on 8 cores, and once in 20 such runs before an earlier round of fixes. It passed 84 later runs with about 128 busy processes, alone, with its class, or with the suite. The cause is not known.
