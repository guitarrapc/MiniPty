## Deferred Until an Embedder Needs It

### Unix disposal grace as an option

The one-second hang-up grace before the kill (`UnixPtyBackend.DisposeHangupGraceMs`) is fixed. It can become an option if an embedder needs a different one.

### Out-of-process PTY host guidance

A web host that serves untrusted users would run MiniPty in a separate, less privileged process or container and talk to it over `PtyStdioBridge`, as VS Code runs node-pty in its pty host. That gives crash and privilege isolation and lets the web tier restart without ending shells. It is documentation and a sample, not a library change.

### A session's tty path

node-pty (`ptsName`), `portable-pty` (`tty_name`), and creack/pty (`Name`) expose the path of the terminal's slave device, such as `/dev/pts/3`. No known embedder needs it. Adding a read-only property later breaks nothing, while removing one would.

If an embedder asks:

- **Getting the path:** it is cheap to read once at spawn. macOS already reads it with `TIOCPTYGNAME`, and Linux and FreeBSD can use `ptsname_r` on the master.
- **Windows:** ConPTY has no device, so the property is null there, as `ActiveProcessName` is.
- **Naming:** name it for a path (`TtyPath`). `PtyStartInfo.TerminalName` already means the `TERM` value.
- **Reading termios:** a caller that wants to read the termios can open this path with `O_NOCTTY` and call `tcgetattr`, so no termios API is needed for that.

## Open

### Linux bursts of 32 KiB or more cost their consumer one 200 μs wait

On Linux a plain `ReadOutputAsync` consumer waits for the master on its own thread once the child has streamed eight whole reads (32 KiB), so a burst that large and then silence costs a thread-pool thread one 200 μs `ppoll` at the burst's end (`platform_support.md`, lessons). Bursts up to 16 KiB never wait, floods wait and are repaid, and a consumer on a UI thread never waits. Nothing is planned: the trade-off is measured and bounded, and a host that serves many sessions redrawing large screens at once can be measured with `MiniPty.Benchmarks --burst-probe`.


### Windows floods on two CPUs trail Porta.Pty's default mode

On CI's 2-core Windows runner and locally on two logical CPUs, MiniPty's 1 MiB floods trail Porta.Pty's default mode by 5–30% on both ConPTYs (CI: 11 of 12 cases over six runs, 0.60–0.99; the in-box flood about 5% behind locally), while all-CPU floods are level in-box and about 12% behind out-of-band. Porta.Pty holds a thread per session blocked in `ReadFile`, so one wake carries each ConPTY write to its consumer; MiniPty needs the loop's wake and the consumer's continuation, since no consumer code runs on the loop. A consumer that reads the pipe itself was measured slower, because ConPTY renders more slowly than a consumer reads ([platform lessons](specs/platform_support.md#lessons-learned)). Closing the gap without a thread per session has no known design; the gap is recorded, not planned.

### macOS floods trail Porta.Pty's default mode when it runs at full speed

On CI's `macos-26` runner (M1 Virtual, 3 vCPUs), MiniPty's 1 MiB flood held 38.9–64.4 MiB/s over 20 runs (median 49.8, one iteration in 200 over 30 ms), while Porta.Pty's default mode, a thread blocked in `read` per session, ranged 32.6–81.3. In five of the six runs where Porta.Pty reached 49.6 MiB/s or more, MiniPty was at 0.73–0.98 of it, and where Porta.Pty stalled, MiniPty led at 1.11–1.53 ([platform lessons](specs/platform_support.md#lessons-learned)). The five runs on `main` measured 0.73–1.08. On an idle M2, MiniPty leads (244–280 against 169 MiB/s). The cause on the hosted runner is not known: on the M2, the same wait in kqueue matched a blocked reader on every load. MiniPty's own figure is stable and holds no thread per session, so the gap is recorded, not planned.

## Unverified

- **`ClosePseudoConsole` on older Windows builds.** On build 26200 it returned within 1 ms in every measured session, also while an unrelated process held the output pipe open. Whether it can block on Windows 10, or on Windows 11 before 24H2, while output is not drained is not verified; disposal switches the pump to discarding output before the close for that reason. If it can block, `Dispose` must close off the calling thread and `DisposeAsync` must include the close in its wait. Also unverified: whether, on a build where the close waits for the console host, a process that inherited the output pipe keeps that host alive and makes the close wait for it too.
- **FreeBSD** is tested neither locally nor in CI. The `forkpty` start lost its Linux branches when Linux moved to `clone`, and that change has not been compiled. Its termios (`VERASE2`, and `_POSIX_VDISABLE` being 0xff), whether it revokes the tty when the session leader exits, as macOS does, and the readiness behavior `kqueue` was chosen on, which the Unix loop and its coalescing tick run on, follow from source only. `scripts/pty-readiness-probe.c` checks the readiness part where a FreeBSD machine is available.
- **The Linux start's fallbacks for older kernels.** Before 4.13 there is no `TIOCGPTPEER`, so the slave is opened by the name `ptsname_r` gives. A kernel before 5.2 ignores `CLONE_PIDFD`, and a `clone` that rejects it runs again without it. Either way the start opens the pidfd by pid, which needs 5.3 ([Linux: clone + execve](references/pty_crossplatform.md#linux-clone--execve)). No test forces these fallbacks, and every kernel the tests ran on is 5.3 or later.
- **Host exit on macOS under NativeAOT.** A host that disposes a session whose child ignores `SIGHUP` and then exits sends the pending kill at exit. On macOS that was probed under CoreCLR and NativeAOT with an earlier version that armed a timer per disposal, not with the current exit-watcher deadline; CI runs the CoreCLR test (`HostExitKillsAChildStillInItsHangupGrace`) there.
- **An echo during a flood through a bridge** is not tested against the coalescing bound; the tests time the end of a burst only.
- **The output ring's capacity on macOS.** 16, 32, and 64 KiB measured alike on Linux and Windows, so the ring stays at 16 KiB ([platform lessons](specs/platform_support.md#lessons-learned)); macOS was not measured. A macOS master hands out at most 1,024 bytes per read.

## Rare Test Timeouts Under Heavy Load

- **Windows:** in `QueuedInputIsDeliveredInOrderWithEofAfterItWhileOutputIsBackedUp`, a blocking write issued behind a queue that had stayed pending for 500 ms completed within 100 ms once in 11 runs of the EOF tests under 3× CPU oversubscription on a desktop, before `SendEof` was called: the console host took input for a moment while its output was still backed up, so the test's premise did not hold and the EOF path was not involved. Not seen on hosted runners since the flood tests were reworked.
- **macOS:** `SendEofEndsInputAfterACanceledWriteLeftAPartialLine` timed out once in 48 full-suite runs with 64 busy processes on 8 cores, and once in 20 such runs before an earlier round of fixes. It passed 84 later runs with about 128 busy processes, alone, with its class, or with the suite. The cause is not known.
