## Deferred Until an Embedder Needs It

### Unix disposal grace as an option

The one-second hang-up grace before the kill (`UnixPtyBackend.DisposeHangupGraceMs`) is fixed. It can become an option if an embedder needs a different one.

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


### Windows out-of-band floods on two CPUs trail Porta.Pty's default mode

On CI's 2-core Windows runner, MiniPty's 1 MiB floods trailed Porta.Pty's default mode by 5–30% on both ConPTYs (11 of 12 cases over six runs, 0.60–0.99): each ConPTY write cost the loop's wake and the consumer's continuation, where Porta.Pty's thread blocked in `ReadFile` needs one wake. Since `ReadOutputAsync` joins a burst's writes on Windows ([Backpressure](specs/core_session.md#backpressure)), a flood wakes the consumer about 128 times per MiB instead of about 4,000. On two logical CPUs of a desktop (2026-11-13, 32 MiB floods, three alternating rounds), MiniPty then led: 7.7–8.2 against 6.4–7.2 MiB/s in-box and 11.4–12.1 against 7.5–9.6 out-of-band, at 30–39 against 71–100 ms of host CPU per MiB.

CI's `TwoCpus` job did not repeat the out-of-band lead. In four runs of `PtyThroughputBenchmarks` beside four of `main` (2026-11-12, `windows-2025`), the out-of-band 1 MiB flood measured 0.79–0.96 of Porta.Pty's rate against 0.70–1.01 (median 0.74) before: closer, still behind. In-box, MiniPty led either way (1.12–1.28 joined, 1.29–1.33 before, one run of `main` at 0.70). In the one pair that ran on the same processor model, the joined in-box flood took 194 ms against 168–176 ms before. On all CPUs the out-of-band ratio moved from 0.90–0.91 to 0.88–1.07. What CI did confirm is the CPU: 23–59 against 106–260 ms of the host's CPU per MiB, and 129–221 against about 4,100 work items. The desktop's two logical CPUs did not predict the ranking on the runner's two Hyper-V vCPUs, and why is not known; closing the gap needs measuring on such a guest.

### FreeBSD floods are handed to their consumer about once per two reads

A consumer that keeps up with a flood waits for the master on its own thread on macOS and Linux, so the loop hands it output about once per wait. FreeBSD has no such wait, so in a 14.5 guest under QEMU a 4 MiB flood took 483 thread-pool hand-offs for 1,061 reads (`AFloodTheConsumerKeepsUpWithIsNotHandedOverOncePerRead` checks only the output there). The macOS wait on a kqueue of the session's own would apply as is, but the rule for when a FreeBSD child counts as streaming, and whether the wait pays off, need measuring on a FreeBSD machine, not under emulation.

In the same 14.5 guest under KVM rather than emulation (2026-11-13, 8 vCPUs, the flood probe through `scripts/freebsd`'s harness), 256 MiB floods ran at 569–634 MiB/s with 23–85 hand-offs per MiB, against about 120 per MiB in the emulated guest, in chunks of 4.6–6.4 KB, at 2.8–3.8 ms of CPU per MiB, less than Linux's 4.6 on its 146 MiB/s. A wait would save few hand-offs there; whether hardware agrees is still open.

### macOS floods trail Porta.Pty's default mode when it runs at full speed

On CI's `macos-26` runner (M1 Virtual, 3 vCPUs), MiniPty's 1 MiB flood held 38.9–64.4 MiB/s over 20 runs (median 49.8, one iteration in 200 over 30 ms), while Porta.Pty's default mode, a thread blocked in `read` per session, ranged 32.6–81.3. In five of the six runs where Porta.Pty reached 49.6 MiB/s or more, MiniPty was at 0.73–0.98 of it, and where Porta.Pty stalled, MiniPty led at 1.11–1.53 ([platform lessons](specs/platform_support.md#lessons-learned)). The five runs on `main` measured 0.73–1.08. On an idle M2, MiniPty leads (244–280 against 169 MiB/s). The cause on the hosted runner is not known: on the M2, the same wait in kqueue matched a blocked reader on every load. MiniPty's own figure is stable and holds no thread per session, so the gap is recorded, not planned.

## Unverified

- **The in-box ConPTY on Windows 10 and on Windows 11 before 24H2.** Of the builds without `ReleasePseudoConsole`, only Windows Server 2022 (build 20348) was measured: its close waits for the host, it can drop an early resize, its host never exits while an inherit-cursor query is unanswered, for a child that writes nothing it writes nothing until it closes, every pseudo-console leaks one handle, it forwards neither the switch to the alternate screen nor the hidden cursor, and it expects a pending wrap to outlive `ESC[K` ([Verification](specs/platform_support.md#verification)). MiniPty handles the first four the same way on every such build; for the fourth, a one-shot completion closes on quiet without waiting for its first read. Whether the other builds behave alike is not verified. On build 20348 a process that holds the output pipe neither keeps the host alive nor holds its close (`PtyConsoleHostTests.OutputEndsSoonAfterTheConsoleHostExitsWhileTheOutputPipeIsHeld`). Disposal there keeps the output pipe open until the host has ended for every session, but a host that waited for good after a write to a closed pipe was seen only after an inherit-cursor report. The builds left to measure are Windows 10 22H2 (19045), Windows 11 23H2 (22631), and the minimum, build 17763 (Windows 10 1809 or Server 2019). None has a hosted runner. On each, run the suite, `scripts/conpty-close-probe.cs`, and the TUI sample as Build's smoke runs it, and record which of these differences hold.

## Rare Test Timeouts Under Heavy Load

- **Windows:** in `QueuedInputIsDeliveredInOrderWithEofAfterItWhileOutputIsBackedUp`, a blocking write issued behind a queue that had stayed pending for 500 ms completed within 100 ms once in 11 runs of the EOF tests under 3× CPU oversubscription on a desktop, before `SendEof` was called: the console host took input for a moment while its output was still backed up, so the test's premise did not hold and the EOF path was not involved. Not seen on hosted runners since the flood tests were reworked.
- **macOS:** `SendEofEndsInputAfterACanceledWriteLeftAPartialLine` timed out once in 48 full-suite runs with 64 busy processes on 8 cores, and once in 20 such runs before an earlier round of fixes. It passed 84 later runs with about 128 busy processes, alone, with its class, or with the suite. The cause is not known.
