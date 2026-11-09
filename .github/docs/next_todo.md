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
- **Termios:** `InputMode` reads echo and canonical mode through the master. The path is not a way to read more: a caller that opens the slave holds it when the child exits, which on FreeBSD discards output not yet read ([core session lessons](specs/core_session.md#lessons-learned)).

## Open

### Linux bursts of 32 KiB or more cost their consumer one 200 μs wait

On Linux a plain `ReadOutputAsync` consumer waits for the master on its own thread once the child has streamed eight whole reads (32 KiB), so a burst that large and then silence costs a thread-pool thread one 200 μs `ppoll` at the burst's end (`platform_support.md`, lessons). Bursts up to 16 KiB never wait, floods wait and are repaid, and a consumer on a UI thread never waits. Nothing is planned: the trade-off is measured and bounded, and a host that serves many sessions redrawing large screens at once can be measured with `MiniPty.Benchmarks --burst-probe`.


### Windows floods on two CPUs trail Porta.Pty's default mode

On CI's 2-core Windows runner and locally on two logical CPUs, MiniPty's 1 MiB floods trail Porta.Pty's default mode by 5–30% on both ConPTYs (CI: 11 of 12 cases over six runs, 0.60–0.99; the in-box flood about 5% behind locally), while all-CPU floods are level in-box and about 12% behind out-of-band. Porta.Pty holds a thread per session blocked in `ReadFile`, so one wake carries each ConPTY write to its consumer; MiniPty needs the loop's wake and the consumer's continuation, since no consumer code runs on the loop. A consumer that reads the pipe itself was measured slower, because ConPTY renders more slowly than a consumer reads ([platform lessons](specs/platform_support.md#lessons-learned)). Closing the gap without a thread per session has no known design; the gap is recorded, not planned.

### FreeBSD floods are handed to their consumer about once per two reads

A consumer that keeps up with a flood waits for the master on its own thread on macOS and Linux, so the loop hands it output about once per wait. FreeBSD has no such wait, so in a 14.5 guest under QEMU a 4 MiB flood took 483 thread-pool hand-offs for 1,061 reads (`AFloodTheConsumerKeepsUpWithIsNotHandedOverOncePerRead` checks only the output there). The macOS wait on a kqueue of the session's own would apply as is, but the rule for when a FreeBSD child counts as streaming, and whether the wait pays off, need measuring on a FreeBSD machine, not under emulation.

### FreeBSD sometimes loses the end of a 32 KiB burst

In the FreeBSD 14.5 guest on CI, a child that writes 32 KiB with `head -c 32768 /dev/zero` and exits sometimes reaches its consumer without the last 1.5–2 KiB: twice in 40 runs, in two different tests. Two paths can drop what the master has not read when the child exits. One is the tty's last close, if it comes by revoke or its drain stops waiting for the master. The other is the post-exit drain, if the loop reads nothing for 100 ms after the exit while output is still queued. Telling them apart needs a FreeBSD host, or one with KVM, and a probe that records whether output ended by end of file or by MiniPty's close. No local host here has KVM. The tests are retried meanwhile ([platform_support.md](specs/platform_support.md#platform-specific-test-constraints)).

### macOS floods trail Porta.Pty's default mode when it runs at full speed

On CI's `macos-26` runner (M1 Virtual, 3 vCPUs), MiniPty's 1 MiB flood held 38.9–64.4 MiB/s over 20 runs (median 49.8, one iteration in 200 over 30 ms), while Porta.Pty's default mode, a thread blocked in `read` per session, ranged 32.6–81.3. In five of the six runs where Porta.Pty reached 49.6 MiB/s or more, MiniPty was at 0.73–0.98 of it, and where Porta.Pty stalled, MiniPty led at 1.11–1.53 ([platform lessons](specs/platform_support.md#lessons-learned)). The five runs on `main` measured 0.73–1.08. On an idle M2, MiniPty leads (244–280 against 169 MiB/s). The cause on the hosted runner is not known: on the M2, the same wait in kqueue matched a blocked reader on every load. MiniPty's own figure is stable and holds no thread per session, so the gap is recorded, not planned.

## Unverified

- **The in-box ConPTY on Windows 10 and on Windows 11 before 24H2.** Of the builds without `ReleasePseudoConsole`, only Windows Server 2022 (build 20348) was measured: its close waits for the host, it can drop an early resize, its host never exits while an inherit-cursor query is unanswered, for a child that writes nothing it writes nothing until it closes, every pseudo-console leaks one handle, it forwards neither the switch to the alternate screen nor the hidden cursor, and it expects a pending wrap to outlive `ESC[K` ([Verification](specs/platform_support.md#verification)). MiniPty handles the first four the same way on every such build; for the fourth, a one-shot completion closes on quiet without waiting for its first read. Whether the other builds behave alike is not verified, and neither is whether a process that inherited the output pipe keeps a host alive and so holds its close, which then holds the thread it runs on. Disposal there keeps the output pipe open until the host has ended for every session, but a host that waited for good after a write to a closed pipe was seen only after an inherit-cursor report. The builds left to measure are Windows 10 22H2 (19045), Windows 11 23H2 (22631), and the minimum, build 17763 (Windows 10 1809 or Server 2019). None has a hosted runner. On each, run the suite, `scripts/conpty-close-probe.cs`, and the TUI sample as Build's smoke runs it, and record which of these differences hold.
- **The close-return report on an in-box ConPTY without `ReleasePseudoConsole`.** The post-exit drain waits for such a close to return before it ends output ([Post-exit drain](references/pty_crossplatform.md#post-exit-drain)). Only scripted backends have reported a return so far. On windows-2022 the frame also arrives under the 1 s wait alone, since the pipe breaks as the host exits, so a passing run does not show that the report fires. A run that times the close would show it, where the close returns while another process holds the output pipe: output then ends about 100 ms after the return, not 1.1 s after the request.
- **FreeBSD on a machine of its own, and on arm64.** CI compiles the shim for 14.4 and 15.1 on amd64 and arm64, and runs the suite in a 14.5 amd64 guest under QEMU with a community build of .NET 10 ([Verification](specs/platform_support.md#verification)). Nothing has run on FreeBSD hardware or on arm64, and `scripts/pty-readiness-probe.c`, which checks the readiness behavior `kqueue` was chosen on, has not run on FreeBSD.
- **Host exit on macOS under NativeAOT.** A host that disposes a session whose child ignores `SIGHUP` and then exits sends the pending kill at exit. On macOS that was probed under CoreCLR and NativeAOT with an earlier version that armed a timer per disposal, not with the current exit-watcher deadline; CI runs the CoreCLR test (`HostExitKillsAChildStillInItsHangupGrace`) there.
- **An echo during a flood through a bridge** is not tested against the coalescing bound; the tests time the end of a burst only.
- **The output ring's capacity on macOS.** 16, 32, and 64 KiB measured alike on Linux and Windows, so the ring stays at 16 KiB ([platform lessons](specs/platform_support.md#lessons-learned)); macOS was not measured. A macOS master hands out at most 1,024 bytes per read.

## Rare Test Timeouts Under Heavy Load

- **Windows:** in `QueuedInputIsDeliveredInOrderWithEofAfterItWhileOutputIsBackedUp`, a blocking write issued behind a queue that had stayed pending for 500 ms completed within 100 ms once in 11 runs of the EOF tests under 3× CPU oversubscription on a desktop, before `SendEof` was called: the console host took input for a moment while its output was still backed up, so the test's premise did not hold and the EOF path was not involved. Not seen on hosted runners since the flood tests were reworked.
- **macOS:** `SendEofEndsInputAfterACanceledWriteLeftAPartialLine` timed out once in 48 full-suite runs with 64 busy processes on 8 cores, and once in 20 such runs before an earlier round of fixes. It passed 84 later runs with about 128 busy processes, alone, with its class, or with the suite. The cause is not known.
