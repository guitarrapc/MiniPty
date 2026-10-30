# Linux Flood Review Fixes Plan

Goal: fix what an adversarial review of the `linux` branch (`git diff main...HEAD`, commits `7389cb5`..`cd70eb9`) found in the Linux flood change. That change added the consumer's `poll` wait on Linux, the loop's one read for a waiting consumer, the consumer ending the loop's park, and the empty ring's restart. Keep its flood gains, and remove the costs and gaps it added.

**Status:** done (2026-10-30): implemented, measured on the VM and confirmed on CI ([Implementation](#implementation)). Once the README's tables have updated on a full run, this plan can be removed; its lessons are in `platform_support.md`. The findings below are as the review wrote them. Each finding passed three independent attempts to refute it; one claim that two of three refuted is listed at the end. The full suite passed on Linux before any fix: 731 of 731 in Apple Container (`mcr.microsoft.com/dotnet/sdk:10.0`, arm64, 4 vCPUs requested).

## Implementation

Built on the `linux` branch, measured in Apple Container (Ubuntu 24.04, arm64, 4 vCPUs) and on macOS (M-series):

- **C1:** three rules in `UnixPtyBackend`. A read counts toward streaming only when it is a whole Linux read (`LinuxFullReadBytes`, 4,095); the child counts as streaming after `LinuxStreamingReads` (8, 32 KiB) of them since the consumer last parked with the child not streaming or a wait brought nothing (`_streamingReads`, `IsStreaming`, `CountStreamingRead`); and the wait is a `ppoll` of `LinuxConsumerWaitMicros` (200 μs, `minipty_master_poll` now takes microseconds). macOS keeps its 1 KiB read, one read, and a millisecond. The probe (`--burst-probe`, Phase 0) on the result: 64 sessions × 2 KiB every 5 ms, 0 waits and a probe p50 of 0.010 ms (p99 0.14–0.32), from 3,200–4,200 waits and 9.5–12.5 ms; 8 and 16 KiB bursts 3–5 waits in 12,800; 32 KiB 15% of bursts; 64 KiB 93% (p50 0.66 ms). Floods unchanged: 64 KiB 273–316 μs with 1.2–1.3 waits per flood, 1 MiB 3.8–4.0 ms, process CPU the same, on all CPUs and on two. A threshold of four reads was tried first: 16 KiB bursts then waited every time, and 64 KiB bursts three times each.
- **C2:** `ReadInline` ends a park only when the ring has room; on a ring still full it returns the read to the loop (`leftToLoop`). Test `AParkOnARingStillFullSurvivesTheConsumersInlineRead`: a hook (`InlineReadHookForTests`) holds the consumer's attempt until the loop has filled the ring and parked, then checks that the attempt ended no park (`ParksEndedByConsumerForTests`) and the loop still waits for space. It fails with the guard reverted (checked on macOS).
- **C3:** `TakeOverReading` is `SetLoopInterest()` again. Test-only counters `ParksEndedByConsumer`, `LoopReadsForWaitingConsumer` and `LoopPassesForWaitingConsumer`; `ABurstReachesAWaitingConsumerWithOneHandOff` asserts one loop read per pass for the waiting consumer (it fails with the break reverted on Linux: 345 reads in 64 bursts), and its child now answers after 2 ms, longer than the consumer's wait, so every burst reaches a parked consumer; `TheLoopReadsForAConsumerThatStopsAfterEndingItsPark` asserts the consumer ended at least one park.
- **C4:** `AConsumerWaitsForTheMasterOnItsOwnThread` asserts at least one wait on Linux.
- **C6:** `TryReadInlineOnce` reports a read left to the loop apart from an empty one, and the streaming count is reset only when the read was the consumer's own and found nothing, or the child was not streaming yet.
- **D1–D18:** rewritten from the code: the native poll sits after the kqueue wait with its own comment; the interop summary states the three results; the loop-state, `TryReadInline`, `TakeOverReading`, wait, `ReadOnLoop`, watcher and pump comments say what the code does now; `pty_crossplatform.md`, `core_session.md`, `platform_support.md` (the broken sentence, the commit reference, the contradiction) and `next_todo.md` (the appended clause removed; the 32 KiB residual recorded under Open) likewise. The test summaries say what the tests check.
- **Phase 0:** `MiniPty.Benchmarks --burst-probe [sessions] [bytes] [interval ms] [bursts]` and the child's `--burst` mode.

Tests: macOS 733 of 733; Linux in the container 732 of 733, the failure being the container's `DisposeLetsAnInteractiveShellHangUpItsJobs` (a root shell with no job control), as on `main` there; `HostExitKillsAChildStillInItsHangupGrace` fails there in some runs for the container's PID 1, which reaps nothing. The loop tests passed twice in a row on Linux and the burst test five times.

**CI** (commit `443b433`, three runs each of `PtyThroughputBenchmarks` [41003322808](https://github.com/guitarrapc/MiniPty/actions/runs/41003322808), [41003305999](https://github.com/guitarrapc/MiniPty/actions/runs/41003305999), [41003282698](https://github.com/guitarrapc/MiniPty/actions/runs/41003282698) and `PtyLatencyBenchmarks` [41003345864](https://github.com/guitarrapc/MiniPty/actions/runs/41003345864), [41003356908](https://github.com/guitarrapc/MiniPty/actions/runs/41003356908), [41003368492](https://github.com/guitarrapc/MiniPty/actions/runs/41003368492), 2026-10-30; the Windows throughput jobs had not finished when this was written, and Windows code is untouched):

| `ubuntu-24.04`, MiniPty ÷ Porta.Pty default | Before the Linux change | Linux change (`6290522`) | With the review fixes (`443b433`) |
|---|---|---|---|
| 64 KiB, `AllCpus` | 0.90, 0.90, 0.92 | 1.04, 0.93, 0.98 | **1.11, 1.10, 0.94** |
| 64 KiB, `TwoCpus` | 0.92, 1.04, 0.96 | 0.89 (Xeon), 1.41, 1.39 | **1.28, 1.29, 1.41** |
| 1 MiB, `AllCpus` | 1.04, 0.97, 1.03 | 1.17, 0.99, 1.00 | 1.15, 1.16, 0.99 |
| 1 MiB, `TwoCpus` | 1.11, 1.16, 1.08 | 1.39, 1.38, 1.49 | 1.35, 1.29, 1.48 |

Work items per flood stayed at 0 (0.00–0.04 at 1 MiB) and process CPU per 64 KiB flood at 192–290 μs (453–681 before the Linux change); allocations 0 B except the 2 B per 1 MiB flood the pool's own thread adds. The 64 KiB all-CPU median is now 1.10, over the gate of 1.0 the Linux change had missed by its spread. macOS: MiniPty's own figures are in the band of the runs before (64 KiB 54.5–61.6 MiB/s, 1 MiB 48.8–60.6, 0.00–0.06 work items, 10–12 ms of CPU per MiB), while Porta.Pty's default mode had faster runs than before on that runner (its 1 MiB at 49–81 MiB/s against 34–51), so the ratios there read 0.75–1.00 at 1 MiB against 1.20–1.39; the runner's own spread is 2× between runs, and MiniPty's structure did not change there. Keystrokes on Linux: P50 / P95 45.0 / 72.6, 97.8 / 129.3 and 47.6 / 70.4 μs against Porta.Pty's 38.0 / 68.8, 112.2 / 138.2 and 47.9 / 69.8 in the same runs (the second an EPYC 7763 runner, as the earlier 93.4 / 108.3 against 111.5 / 144.3), one work item per keystroke, 0 B; on two CPUs 35.2, 58.0 and 45.7 μs against 55.2, 104.2 and 47.3. macOS keystrokes 32.9–33.4 μs against 33.7–38.9 in two runs and a stalled third run for both libraries (P95 10 ms each). Windows keystrokes unchanged (out-of-band P50 206, 103 and 218 μs against Porta.Pty's 217, 107 and 224).

## Findings

Ranked by impact. Line numbers are on branch `linux` at `cd70eb9`.

### C1: A Linux consumer blocks a pool thread for 1 ms after every burst of 1–4 KiB (medium, measured)

`ReadInline` (`UnixPtyBackend.cs:1045`) and `ReadOnLoop` (`:1303`) set `_outputStreams` on any read of at least `StreamingReadBytes` (1,024, `:301`). The consumer's next empty `TryReadInline` then calls `minipty_master_poll(master, 1)` (`WaitForOutputOnConsumer`, `:905`). When the child pauses after a burst, that `poll` blocks a thread-pool thread for the whole millisecond, inside native code the pool cannot see. On Linux a read of 1–4 KiB can be all the child wrote, so the flag does not mean the child was held up, as it does on macOS, where 1 KiB is the whole master. This hits TUI redraws, JSON log lines over 1 KiB and `ls` output. Before this branch, Linux never waited on the consumer's thread.

The code comments and specs weigh the wait against keystroke echoes and short log lines only. Those stay under 1 KiB and never wait, as the measurement confirms. Repeated bursts over 1 KiB are not weighed anywhere.

Measured in Apple Container (`-c 2`, `nproc` 3, arm64). Each of N sessions ran a `perl` child that wrote 2 KiB every 5 ms, 200 times. A probe queued a pool work item every 2 ms and recorded how long it waited to run. "No Linux wait" is this branch with `OperatingSystem.IsLinux()` removed from the wait's gate in `TryReadInline`. Three rounds each:

| Workload | Build | Probe p50 (ms) | Probe p99 (ms) | Probes run | Consumer waits |
|---|---|---|---|---|---|
| 2 KiB / 5 ms, 64 sessions | this branch | 9.5 / 10.9 / 12.5 | 38 / 50 / 51 | 66 / 66 / 56 | 3.2k–4.2k |
| 2 KiB / 5 ms, 64 sessions | no Linux wait | 0.001 / 0.004 / 0.004 | 0.05 / 0.77 / 0.60 | 261 / 200 / 197 | 0 |
| 2 KiB / 5 ms, 16 sessions | this branch | 0.005 / 0.011 / 1.4 | 8.5 / 8.1 / 11.5 | 204 / 212 / 155 | 2.4k–2.8k |
| 2 KiB / 5 ms, 16 sessions | no Linux wait | 0.001 / 0.003 / 0.004 | 0.02 / 0.09 / 0.07 | 255 / 208 / 220 | 0 |
| 200-byte lines / 5 ms, 64 sessions | this branch | 0.002–0.003 | 0.12–0.18 | about 250 | 0 |

The same harness shows what the wait buys in floods. With 64 sessions each flooding 1 MiB, this branch used 203–242 work items against 1,808–2,543 without the wait, in 131–265 ms against 123–315 ms. The probe's p50 there was 0.003–0.042 ms against 1.7–4.2 ms. A fix must keep that.

The harness was a throwaway test class in the review session's scratch directory, not in the repo. Phase 0 rebuilds it.

### C2: On kqueue the consumer can end a park on a ring that is still full, and nobody then watches the master (low to medium, traced, not reproduced)

`ReadInline` clears `_loopParked` and calls `pump.EndWaitForFreeSpace()` (`:1020`) before its read loop checks `pump.IsFull`. The consumer enters `ReadInline` after its `TryPeek` found the ring empty, but the loop's passes can fill the ring and park between the two. For example, the consumer was preempted, or the loop had taken reading over. The consumer then reads nothing, and `TryReadInline` arms the master directly (`ArmForConsumer`).

On kqueue the parked pass queued interest 0 through `SetInterestOnLoop`, which `minipty_watch_wait_arm` applies as `EV_DISABLE` at the loop's next `kevent` (`minipty_unix_exit.c:603`). That can land after the consumer's arm. Then `_producerWaiting` is 0, so the consumer's `Release` no longer wakes the loop (`WakeProducerIfSpace`) and the drain's quiet check no longer sees a waiting producer (`IsQuietSince`). `CheckInlineReader` takes over only an `Inline` consumer, so it does not help either. After the child exits, a consumer that holds a chunk for more than 100 ms can have the master closed with output still in it.

Linux is not affected: epoll applies the loop's arm at once, inside its turn, so the consumer's arm always comes later. On macOS the child's exit may wait for unread output, which would hide the loss (platform lessons). So the exposure is mainly FreeBSD, which is untested. The branch's own lesson, "the wake stays", is what this path breaks.

### C3: No test fails when the park handling or the one-read break is reverted (low to medium)

Each change was reverted in a copy and `PtyUnixLoopTests` (17 tests) run on Linux:

- Restoring `ReadInline`'s refusal while parked and `TakeOverReading`'s plain `SetLoopInterest()`: 17 of 17 passed in one copy. Another copy failed only `AConsumerWaitsForTheMasterOnItsOwnThread`'s listings assertion, in a run that took 15 minutes on a loaded host.
- Removing the one-read `break` in `ReadOnLoop` (`:1309`): 17 of 17 passed in one copy. Another failed only the timing test `AFloodDoesNotDelayAnotherSessionsEcho`.
- `ABurstReachesAWaitingConsumerWithOneHandOff` measured 2 work items and 13–17 loop passes for 64 bursts, against bounds of 128 each. Its bounds cannot see the break.

`TakeOverReading`'s new parked branch (`:997`) also duplicates a service request that is already queued. `_loopParked` is set only after `BeginWaitForFreeSpace` set `_producerWaiting`, and the two are cleared together, so a release that frees space has already woken the loop. Instrumented, the branch saw `_loopParked` set 0–1 times per run of `TheLoopReadsForAConsumerThatStopsAfterEndingItsPark` (`PtyUnixLoopTests.cs:460`). That test checks only byte totals and exit, so it does not show which side ended the park. In the same runs `ReadInline` ended the park 59–89 times.

### C4: `AConsumerWaitsForTheMasterOnItsOwnThread` asserts no wait on Linux (low)

The test (`PtyUnixLoopTests.cs:621`) runs on Linux, but its waits assertion is inside `if (OperatingSystem.IsMacOS())`. It passed on Linux with the Linux wait disabled. With the wait on, it measured 21 waits for 438 chunks. `ABurstReachesAWaitingConsumerWithOneHandOff` does assert waits on Linux, so the wait has coverage, but not from the test that claims it.

### C6: A consumer that loses the producer's turn after a successful wait clears the streaming flag (low)

In `TryReadInline` (`:849`–`:857`), when the wait reports output but the second `TryReadInlineOnce` loses the gate to a loop pass, `published` is false and `_outputStreams` is cleared, although output came. The pass may be an input flush or a service with report 0, which reads nothing and so does not set the flag again. The cost is one park and hand-off until the next read of 1 KiB or more. The comment at `:855` ("timed out, or reported end of file") does not cover this case.

### D1–D18: Comments and docs that no longer match the code

All 18 passed three of three attempts to refute them.

| # | Where | Problem |
|---|---|---|
| D1 | `minipty_unix_exit.c:822`–`862` | `minipty_master_poll` and its comment sit between the kqueue wait's comment ("Without kqueue both fail with ENOTSUP") and `minipty_master_wait_open`. The kqueue text now reads as the poll function's. |
| D2 | `UnixInterop.cs`, `minipty_master_poll` | The summary omits that it returns 0 on an interruption and 1 on hang-up or error, not only on output. |
| D3 | `UnixPtyBackend.cs:250` | "used only on its thread unless noted" covers the park, but the consumer now writes `_loopParked`. The same applies to `ParkWhileFull`'s "on the loop" and `ReadInline`'s "the loop's own state holds still". |
| D4 | `UnixPtyBackend.cs:807` | The `TryReadInline` summary still lists "a park on a full ring" as the loop's to handle. `ReadInline` now ends it. |
| D5 | `UnixPtyBackend.cs:993` | `TakeOverReading`'s summary says it arms the master. The parked branch only requests a service, and its stated reason is the case the free-space wake already covers (C3). |
| D6 | `UnixPtyBackend.cs:292`, `:833` | "a read of at least `StreamingReadBytes` says the child was held up and has more" holds on macOS only (C1). |
| D7 | `UnixPtyBackend.cs:835` | "A flood's reads on the loop fill the master" is macOS wording. On Linux the loop reads once for a waiting consumer. |
| D8 | `UnixPtyBackend.cs:891`–`904` | The summary has a stacked parenthetical with a semicolon and a measurement. The remarks keep M2 figures in a method that now has a Linux branch. |
| D9 | `UnixPtyBackend.cs:1305`, `:1313` | "(a 64 KiB burst cost four of each)" is history in code. "Linux hands out what it has in 4 KiB reads" conflicts with "4,095 bytes per read" elsewhere. |
| D10 | `UnixChildWatcher.cs:29` | "On macOS such a consumer also waits for the master" was not updated for Linux. |
| D11 | `PtyOutputPump.cs:238` | The edited line is 169 characters where its neighbours wrap at about 120. "at the start" (the wrap) and "at the beginning" (the restart) blur two events. |
| D12 | `PtyOutputPump.cs:33`, `:99` | "the active consumer owns `_head`" and "Consumer side" are stale. `NextReadSpan` now writes `_head = 0` on an empty ring. |
| D13 | `pty_crossplatform.md:365` | The Ring consumers row still says only macOS waits on the master. |
| D14 | `pty_crossplatform.md:363` | The new Unix loop text chains clauses with a colon, semicolons and "since". "(next row)" points at Buffer, while inline reading is in Ring consumers (older text). |
| D15 | `platform_support.md:182` | "one work item per keystroke, 0 B The detailed tables" is broken text. Commit `7ee5ce9` is on no branch. The pointer to this plan "in the history up to the commit that removed it" will not survive a squash merge, which is how #65 and #66 landed. |
| D16 | `platform_support.md`, free-space wake lesson | "The park's flag and the arm are the loop's state: a consumer may end a park" contradicts itself. "rarely parks for a flood at all" has no measurement. |
| D17 | `next_todo.md:47` | Done work, the ring's restart, was appended to an open item with a semicolon. The lessons already record it. |
| D18 | `PtyUnixLoopTests.cs:410`, `:460` | `ABurstReachesAWaitingConsumerWithOneHandOff` states a Linux-only premise with "now", but also runs on macOS. `TheLoopReadsForAConsumerThatStopsAfterEndingItsPark` claims the consumer ends the park, which it does not check (C3). |

## Fix candidates

### C1

1. **On Linux, only a full read starts the wait (recommended).** Set `_outputStreams` on Linux only for a read that took a whole Linux read, 4,095 bytes, and keep 1,024 on macOS. A burst of 1–4 KiB then never waits, while a flood's reads are all 4,095 and keep the wait. What remains is one timed-out wait per burst over 4 KiB, which the bursts above never reach. The constant needs its own name and comment, since its reason (`N_TTY_BUF_SIZE` less one) is not the macOS one.
2. **A shorter wait on Linux.** `ppoll` with about 100–200 μs instead of `poll`'s 1 ms. During a flood the wait ends within microseconds, so a shorter cap mainly shortens the wasted tail. It still blocks a pool thread per burst, so it pairs with candidate 1 rather than replacing it.
3. **Wait only after a wait that paid off.** The first wait after a burst still times out, so this halves the cost at best for the 2 KiB bursts above.
4. **Keep and document.** Record the cost as a known trade-off in `platform_support.md` and `next_todo.md`.

Whichever is chosen must be measured on the burst probe and on the 64 KiB and 1 MiB floods (`PtyThroughputBenchmarks`, all CPUs and `TwoCpus`) against this branch.

### C2

End the park in `ReadInline` only when the ring has room (`!pump.IsFull`). Otherwise return without reading, as before this branch, so the loop's park, its `_producerWaiting` and the free-space wake stay intact. A regression test needs a deterministic way to reach `ReadInline` with the loop parked on a full ring. One way is a test hook that holds the consumer between `TryPeek` and `TryEnterProducer` while a child fills the ring. It then checks that the park and `IsWaitingForFreeSpace` survive, and that a release wakes the loop.

### C3

- Restore `TakeOverReading` to `SetLoopInterest()` and drop the parked branch, since the free-space wake covers it. Otherwise keep the branch and give it a deterministic test.
- Count the parks the consumer ends and the loop reads made for a waiting consumer (test-only counters, like `ConsumerWaitsForTests`). Assert them in `ABurstReachesAWaitingConsumerWithOneHandOff`: at most one loop read per burst while the consumer waits, which fails without the break. Assert in `TheLoopReadsForAConsumerThatStopsAfterEndingItsPark` that the consumer ended at least one park.
- Check each new assertion by reverting its change and seeing it fail on Linux.

### C4

Assert at least one wait on Linux (21 measured per 1 MiB flood). Alternatively, keep the macOS bound for macOS and add a Linux bound measured on CI.

### C6

Have `TryReadInlineOnce` report a lost gate apart from an empty read, and clear `_outputStreams` only when the wait timed out or the read found end of file.

### D1–D18

Rewrite each item from the code once C1–C6 are settled, since C1 and C3 change what D5, D6, D7 and D18 describe. For D15, replace the commit and plan references with the facts they stood for (the runs' dates and runners), and repair the broken sentence. Put measurements in the specs and leave one clause of reason in the code.

## Phases

### Phase 0: Measure the bursts

Add the burst probe as a benchmark or an opt-in test under `MiniPty.Benchmarks`. It runs N sessions of a child writing a fixed burst at an interval, measures a pool work item's queueing delay, and reports consumer waits, work items and process CPU. Record this branch's numbers on Linux (Apple Container and CI's `ubuntu-24.04`).

### Phase 1: Correctness and tests

C2, C6, C3 and C4, each with a test that fails before the fix. Run the full suite on Linux and macOS.

### Phase 2: The Linux wait's trigger

C1, the chosen candidate. Measure it with the burst probe and the throughput benchmarks against Phase 0, on all CPUs and on two. The project defines no regression threshold, so use +10% for latency and memory.

### Phase 3: Docs

D1–D18, rewritten from the code after Phases 1 and 2. Record the burst lesson and the numbers in `platform_support.md`. Remove this plan once the lessons hold what it measured.

## Risks

- A full-read trigger depends on Linux handing out 4,095 bytes per read. A kernel that changes `N_TTY_BUF_SIZE`, or a ring span shorter than 4,095 (unread output near the ring's end), would stop the wait. A trigger of "the read filled its span, less one byte" ties it to the span instead.
- Counter-based assertions are process-wide. They need `[NotInParallel]`, as the existing ones have.
- The C2 race needs preemption to reach. A test hook must not change the production path's ordering.

## Refuted claim

The one-read break slowing FreeBSD was refuted two to one. After the loop's one read, `ResumeInlineReading` makes the consumer an inline reader, and it reads the rest of the burst itself, up to 16 KiB per turn, with no further wake. The same setup measured better on Linux before the wait was added (work items per 64 KiB flood 4 to 1.2).
