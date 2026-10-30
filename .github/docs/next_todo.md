## Deferred Until an Embedder Needs It

### Unix disposal grace as an option

The one-second hang-up grace before the kill (`UnixPtyBackend.DisposeHangupGraceMs`) is fixed. It can become an option if an embedder needs a different one.

### Windows exit observation in O(1) threads

Windows exit watchers each wait on up to 63 children with `WaitForMultipleObjects`, so a host adds a thread per 63 sessions. Thread-pool waits or another mechanism can remove that once it matters.

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

## Open

### Linux bursts of 32 KiB or more cost their consumer one 200 μs wait

On Linux a plain `ReadOutputAsync` consumer waits for the master on its own thread once the child has streamed eight whole reads (32 KiB), so a burst that large and then silence costs a thread-pool thread one 200 μs `ppoll` at the burst's end (`platform_support.md`, lessons). Bursts up to 16 KiB never wait, floods wait and are repaid, and a consumer on a UI thread never waits. Nothing is planned: the trade-off is measured and bounded, and a host that serves many sessions redrawing large screens at once can be measured with `MiniPty.Benchmarks --burst-probe`.


### Windows floods on two CPUs trail Porta.Pty's default mode

On CI's 2-core Windows runner and locally on two logical CPUs, MiniPty's 1 MiB floods trail Porta.Pty's default mode by 5–30% on both ConPTYs (CI: 11 of 12 cases over six runs, 0.60–0.99; the in-box flood about 5% behind locally), while all-CPU floods are level in-box and about 12% behind out-of-band. Porta.Pty holds a thread per session blocked in `ReadFile`, so one wake carries each ConPTY write to its consumer; MiniPty needs the loop's wake and the consumer's continuation, since no consumer code runs on the loop. A consumer that reads the pipe itself was measured slower, because ConPTY renders more slowly than a consumer reads ([platform lessons](specs/platform_support.md#lessons-learned)). Closing the gap without a thread per session has no known design; the gap is recorded, not planned.

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
