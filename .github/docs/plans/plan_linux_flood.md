# Linux Flood Throughput Plan

Goal: on Linux, MiniPty's output floods should be at least Porta.Pty's default mode on CI's `ubuntu-24.04` runner, on all CPUs and on two, for the 64 KiB flood as well as the 1 MiB one, without giving up what MiniPty is built on: no thread held per idle session, no allocation per chunk, and reads that keep pace with the child while the consumer is busy.

**Status:** implemented (2026-10-30), CI confirmation (Phase 3) pending. The cause is measured on a Linux VM (Apple Container, Ubuntu 24.04, kernel 6.18, 4 vCPUs, arm64) with a throwaway harness and `strace`; a prototype of the three changes below was built and measured there ([Results](#results)), and Phases 1 and 2 are built with their tests ([Implementation](#implementation)). The prototype is the patch `minipty-linux-flood-prototype.patch` from the session that wrote this plan (about 100 lines over `PtyOutputPump.cs`, `UnixPtyBackend.cs`, `UnixInterop.cs` and the native shim, with both waits measured in Phase 2 still in it); it is a measurement, not the implementation, which Phases 1 and 2 build with their tests.

## Where MiniPty stands

Three CI runs of `PtyThroughputBenchmarks` on commit `7ee5ce9`, whose code is `main`'s ([40936017236](https://github.com/guitarrapc/MiniPty/actions/runs/40936017236), [40936045092](https://github.com/guitarrapc/MiniPty/actions/runs/40936045092), [40936079094](https://github.com/guitarrapc/MiniPty/actions/runs/40936079094)), Linux, MiniPty ÷ Porta.Pty's default mode by MiB/s:

| Linux | Run 1 (EPYC 9V74) | Run 2 (EPYC 9V45) | Run 3 (EPYC 7763) | Work items per flood, MiniPty / Porta.Pty / `UseAsyncIo` | Process CPU per flood, MiniPty / Porta.Pty / `UseAsyncIo` |
|---|---|---|---|---|---|
| 64 KiB, `AllCpus` | **0.90** (196.0 / 218.9) | **0.90** (235.0 / 259.8) | **0.92** (149.3 / 162.0) | 5.5 / 11.1 / 2.6 | 545 / 592 / 541 μs |
| 64 KiB, `TwoCpus` | 0.92 | 1.04 | 0.96 | 5.0 / 13.0 / 1.7 | 514 / 533 / 485 μs |
| 1 MiB, `AllCpus` | 1.04 | 0.97 | 1.03 | 14 / 148 / 28 | 4.1 / 7.8 / 5.9 ms |
| 1 MiB, `TwoCpus` | 1.11 | 1.16 | 1.08 | 15 / 156 / 21 | 4.1 / 6.5 / 6.2 ms |

Porta.Pty's `UseAsyncIo` mode also leads the 64 KiB flood on all CPUs in every run (0.92, 0.96, 0.92). The 1 MiB flood is level or ahead, as the earlier plan left it. The gap is a fixed cost per flood, not spread: in run 1 every one of MiniPty's ten iterations took 317–323 μs per 64 KiB flood and every one of Porta.Pty's 282–291, so about 30 μs per flood separates them, and at 1 MiB MiniPty's lower per-byte cost more than covers it.

## Finding

Measured with a harness outside BenchmarkDotNet that drives the benchmarks' serve child (`MiniPty.Benchmarks.Child --serve`) through one `ReadOutputAsync` enumeration or Porta.Pty's `ReaderStream`, as the benchmarks do, and reports per flood the median time, thread-pool work items, the loop's passes (`UnixPtyBackend.LoopPassesForTests`), the consumer's waits on the master (`ConsumerWaitsForTests`) and the process's CPU time; `taskset -c 0,1` stands in for the `TwoCpus` job. Syscall timelines come from `strace -f -ttt`, whose overhead inflates every figure but keeps the order of events.

### A 64 KiB burst is read in 16 KiB turns

The consumer waits with the master armed. When the burst arrives the loop's pass reads it four times (`LoopReadsPerService`), 4 KiB each, which is exactly the ring, so the ring is full and the pass parks on it with no interest armed. The first publish woke the consumer on a pool thread; it takes the 16 KiB as one chunk, releases it, which wakes the loop (`WakeProducerIfSpace` → `RequestService`), and then tries to read inline, which `ReadInline` refuses while the loop is parked. So the consumer arms the master and waits; the loop wakes, unparks, arms again, is woken by the next readiness report, reads 16 KiB, parks, and signals the consumer. Each 16 KiB therefore costs a wake of the loop and a wake of a pool thread, which the work-item column shows: 4–6 per 64 KiB flood on CI and locally, 3 loop passes per flood locally. A 1 MiB flood pays it only 14–30 times per MiB, because once the child is blocked on a full terminal the consumer, already running, takes the producer's turn before the loop's report arrives and reads the rest inline; the 64 KiB burst is in the kernel's buffers before the consumer is even awake, and the loop always wins that race.

### The first chunk of a burst needs two wakes

The consumer's inline read before it waits finds nothing, so the burst's first bytes go master → epoll → loop thread → `read` → publish → thread-pool continuation → consumer. Porta.Pty's default mode has a thread blocked in `read`, which the kernel wakes once, and its `UseAsyncIo` mode has the same two wakes as MiniPty (its poller, then the pool). In the strace timeline the consumer's first read of the burst lands about 230 μs after the trigger for MiniPty and for `UseAsyncIo`, and about 180 μs for the blocking reader (strace-inflated figures). This is the same structure the macOS plan removed with the consumer's own wait on the master ([platform_support.md](../specs/platform_support.md), lessons), which is macOS-only today.

### The ring's geometry costs a read per 16 KiB

A Linux master hands out 4,095 bytes per read, not 4,096 (`N_TTY_BUF_SIZE` less one). The ring's read span ends at the ring's end, so after four reads 4 bytes are left before it, and the fifth read takes exactly those (`read(42, "T\r\nG", 4)` in the timeline), then the ring wraps. A 64 KiB flood is 21 reads and publishes instead of 17. macOS reads are 1 KiB, sixteen of which fit the ring exactly, so this is Linux's alone.

### What a flood costs beyond MiniPty

The n_tty line discipline receives the child's output a character at a time on a kernel worker and hands the reader 4 KiB per read, so a 64 KiB flood is about 250–300 μs on this VM for every library, and a 1 MiB flood about 3.8 ms. The libraries differ by their wake structure and their per-read overhead, which is what the harness isolates.

## Decision

Three changes, measured together as the prototype:

1. **The loop hands a waiting inline consumer one read and stops.** A consumer that read inline and now waits (`ConsumerReadsWaiting`) takes the burst from its first chunk: the loop reads once, publishes, and leaves the rest to the consumer, which reads on inline, as it does for the rest of a 1 MiB flood today. The pass neither fills the ring nor parks on it. With it, a consumer that finds the loop parked on a ring it has since drained ends the park itself, under the producer's turn, instead of waking the loop to do it, and the loop's takeover of a consumer that stopped while it was parked goes through a service request, so the pass unparks before it arms. Nothing changes for sinks, `Output`, coalescing readers or a consumer that never read inline: the loop reads for them as today.
2. **On Linux the consumer waits for the master on its own thread, as on macOS.** A plain `ReadOutputAsync` consumer that reads nothing while the child streams waits on the master for up to a millisecond, woken by the child's next write, and only a wait that times out arms the loop and parks. The same rule as on macOS decides when (`_outputStreams`: a read of at least 1 KiB starts it, a wait that brings nothing ends it), the same thread test excludes UI consumers, and the same native entry points serve it. The prototype waits on an epoll of the session's own with the master level-triggered, created on the first wait and closed with the master, which is what the macOS kqueue is; Phase 2 decides between that and `poll` on the master, which needs no descriptor and on Linux has none of the per-call registration cost that ruled it out on macOS.
3. **An empty ring starts over at its beginning.** When nothing is unread, the next read span starts at 0, so a read never has to fit the few bytes left before the ring's end. The pump's indices are the producer's to reset then: the consumer holds no chunk and reads neither index until the next publish.

Rejected after measurement:

- **Skipping the free-space wake of the loop while the consumer reads inline.** It deadlocked within the first flood: the loop's pass parks and then sets its interest to nothing, and a consumer turned away at the producer's gate during that pass arms the master between the two, so the loop's arm overwrote the consumer's and nobody was watching the master. The park and the arm are the loop's state; a consumer may end the park under the producer's turn (change 1), but the wake on freed space stays, so the loop re-reads its state whenever the ring has room again. With change 1 the loop rarely parks for a flood, so the wake is rarely sent.

Not pursued: a thread blocked in `read` per session (Porta.Pty's default); a larger ring or larger reads (the terminal hands out 4 KiB per read regardless, as the earlier plan found); reading on the loop only (what the earlier plan replaced).

## Results

Apple Container VM (Ubuntu 24.04, kernel 6.18, 4 vCPUs on an Apple M-series host), 2026-10-30, the harness above: 3,000 floods of 64 KiB and 300 of 1 MiB after 30 of warmup, two rounds each, medians of the per-flood time; `two` is `taskset -c 0,1`. Per flood: thread-pool work items, the loop's passes, and process CPU.

| 64 KiB flood, `all` | median μs (round 1 / 2) | Work items | Loop passes | Consumer waits | Process CPU |
|---|---|---|---|---|---|
| MiniPty `main` | 305 / 311 | 4.1 / 3.9 | 3.0 / 2.8 | 0 | 350 / 347 μs |
| + change 1 | 311 / 306 | 1.4 / 1.2 | 1.4 / 1.2 | 0 | 257 / 267 μs |
| + changes 1, 2 | 287 / 292 | 0.00 | 0.25 / 0.22 | see below | 136 / 137 μs |
| + changes 1, 2, 3 (prototype) | 306 / 288 | 0.00 | 0.27 / 0.22 | see below | 140 / 143 μs |
| Porta.Pty, default | 328 / 321 | 17 | – | – | 607 / 580 μs |
| Porta.Pty, `UseAsyncIo` | 303 / 298 | 1.7 | – | – | 295 / 288 μs |

| 64 KiB flood, `two` | median μs (round 1 / 2) | Work items | Loop passes | Process CPU |
|---|---|---|---|---|
| MiniPty `main` | 288 / 292 | 3.7 / 3.9 | 2.9 / 3.0 | 292 / 292 μs |
| + change 1 | 291 / 302 | 1.2 / 1.3 | 1.2 / 1.3 | 241 / 249 μs |
| + changes 1, 2 | 275 / 303 | 0.00 | 0.24 / 0.28 | 135 / 144 μs |
| + changes 1, 2, 3 (prototype) | 284 / 299 | 0.00 | 0.25 / 0.27 | 133 / 140 μs |
| Porta.Pty, default | 255 / 256 | 15 | – | 391 / 414 μs |
| Porta.Pty, `UseAsyncIo` | 287 / 314 | 1.4 / 1.6 | – | 265 / 284 μs |

| 1 MiB flood | `all` median ms (round 1 / 2) | `two` median ms | Work items (`all`) | Loop passes (`all`) | Process CPU (`all`) |
|---|---|---|---|---|---|
| MiniPty `main` | 3.94 / 3.97 | 3.80 / 3.96 | 20 / 17 | 15 / 13 | 3.09 / 2.97 ms |
| + change 1 | 3.86 / 3.96 | 3.96 / 3.93 | 6.9 / 6.2 | 7.8 / 7.4 | 2.58 / 2.62 ms |
| + changes 1, 2 | 3.94 / 3.91 | 3.77 / 3.98 | 0.00 | 2.8 / 2.8 | 2.00 / 1.98 ms |
| + changes 1, 2, 3 (prototype) | 3.93 / 3.88 | 3.85 / 3.86 | 0.00 | 2.8 / 2.8 | 1.90 / 1.91 ms |
| Porta.Pty, default | 4.09 / 4.28 | 4.86 / 4.18 | 236 / 239 | – | 7.10 / 7.92 ms |
| Porta.Pty, `UseAsyncIo` | 3.93 / 3.80 | 3.59 / 4.12 | 16 / 16 | – | 3.23 / 3.18 ms |

What the numbers say:

- **Change 1 removes the hand-offs and a quarter of the CPU, and the time does not move.** Work items per 64 KiB fell from 4 to 1.2 and loop passes from 3 to 1.2, process CPU from 350 to 260 μs, while the median stayed at about 305 μs: on this VM with a CPU to spare, the wakes overlapped the kernel's work. They are still the cost CI's smaller runner pays for, as the earlier plan measured for the 1 MiB flood, which is why the change is kept and judged on CI.
- **Change 2 takes the 64 KiB flood to Porta.Pty's `UseAsyncIo` and below the default mode on all CPUs** (287–292 μs against 298–303 and 319–333), at no thread-pool work item, a quarter of a loop pass per flood, and 136 μs of process CPU per flood against `main`'s 350 and Porta.Pty's 580–607. The 1 MiB flood keeps its time at a third less CPU (1.9–2.0 ms against 3.0–3.1). On two CPUs the blocking reader is still ahead on this VM (231–290 against 275–303 μs), by 8–10% in the typical round; the VM's `taskset` is a stand-in, so whether that holds on CI is Phase 3's question. Everywhere else MiniPty is level with or ahead of both of Porta.Pty's modes.
- **Change 3 is within the noise on time** (the 4 extra reads per 64 KiB are a few microseconds) and is kept for the syscalls it saves: 17 reads per 64 KiB instead of 21, which the timeline shows and a unit test can hold.
- The VM reproduces CI's ranking only in part: here Porta.Pty's default mode is the slowest on all CPUs and the fastest on two, where CI has it ahead on all CPUs for the 64 KiB flood. Judge the changes by work items, loop passes and process CPU, which held in every round, and confirm the time on CI.

The consumer's waits on the master (change 2) are few: 1.3–1.4 per 64 KiB flood over three rounds on each CPU set (the burst's first chunk, and now and then a read that caught the kernel's worker between refills), each a direct wake by the child's write instead of the loop's and the pool's.

The same VM through BenchmarkDotNet, as CI runs it (`PtyThroughputBenchmarks`, one run each of `main` and the prototype; Porta.Pty's time as a multiple of MiniPty's is the report's `Ratio` column):

| VM, BenchmarkDotNet | MiniPty MiB/s, `main` → prototype | Porta.Pty ÷ MiniPty, `main` → prototype | `UseAsyncIo` ÷ MiniPty, `main` → prototype | Work items per op, `main` → prototype | Process CPU per op, `main` → prototype |
|---|---|---|---|---|---|
| 64 KiB, `AllCpus` | 199.5 → **230.7** | 1.00 → **1.13** | 1.00 → 1.15 | 3.6 → 0 | 321 → 121 μs |
| 64 KiB, `TwoCpus` | 212.6 → **227.9** | 1.00 → **1.10** | 1.05 → 1.14 | 4.1 → 0 | 291 → 123 μs |
| 1 MiB, `AllCpus` | 265.6 → 283.7 | 1.08 → 1.16 | 1.01 → 1.07 | 18.9 → 0 | 2.75 → 1.80 ms |
| 1 MiB, `TwoCpus` | 268.9 → 283.6 | 1.11 → 1.19 | 1.04 → 1.10 | 22.6 → 0 | 2.64 → 1.80 ms |

Allocations stayed at 0 B in every case. `PtyLatencyBenchmarks` on the VM kept one work item per keystroke for MiniPty in both runs, as the consumer's wait never starts on a two-byte echo; its times moved with the run for every library (MiniPty's `AllCpus` P50 75 → 87 μs, Porta.Pty's 71 → 81, `UseAsyncIo`'s 79 → 68), which the short latency job on a shared VM does not resolve, so the keystroke gate is CI's.

**`poll` against a per-session epoll for the consumer's wait** (three rounds each of 3,000 floods of 64 KiB on the prototype, all CPUs / two): `poll` on the master 287, 289, 303 / 288, 286, 307 μs at 1.2–1.3 waits per flood and 132–144 μs of process CPU; the epoll 293, 280, 302 / 272, 280, 295 μs at 1.3–1.4 waits and 130–142 μs. Level, so Phase 2 takes `poll`, which holds no descriptor per session.

**The test suite on the VM** (728 tests): `main` fails one, `DisposeLetsAnInteractiveShellHangUpItsJobs`, which is this container's (an interactive bash under a root shell with no job control), before and after. The prototype fails two more, both in `PtyOutputCoalescingTests`, `OutputALateWaitLeavesDoesNotStartTheNextBurstWhenItWrapsTheRing` and `OutputAfterTheLeftoversOfALateWaitCountsTowardTheNextBurst`: they build their leftovers so that they wrap the ring's end, and with change 3 a ring emptied before the write starts over, so the chunk they expect split at the end (2,048 bytes) arrives whole (8,192). Phase 1 rewrites them to wrap the ring with output still unread, which is the case the rewind leaves alone, and keeps what they check. `HostExitKillsAChildStillInItsHangupGrace(exit-handler)` failed once in three runs of the suite on the VM, with the `poll` variant, and passed with the epoll one and on `main`; it is a 5-second host-exit test and is to be run again on the implementation before it is read as a regression.

## Implementation

Built 2026-10-30, on the `linux` branch: change 1 in `UnixPtyBackend` (`ReadOnLoop` reads once for a consumer in `ConsumerReadsWaiting`, `ReadInline` ends a park it finds, `TakeOverReading` requests a service while parked), change 2 as `minipty_master_poll` in the shim and the Linux branch of `WaitForOutputOnConsumer` (the streaming threshold is `StreamingReadBytes`, 1 KiB on both platforms), change 3 in `PtyOutputPump.NextReadSpan`. Tests: `ABurstReachesAWaitingConsumerWithOneHandOff` (64 bursts of 64 KiB from an `awk` child, fewer than 2 work items and 2 loop passes per burst, at least a wait per two bursts), `TheLoopReadsForAConsumerThatStopsAfterEndingItsPark`, `AnEmptyRingStartsOverAtItsBeginning`, the two coalescing tests rewritten to wrap the ring with output unread, and the macOS consumer-wait tests now run on Linux too (`AConsumerWaitsForTheMasterOnItsOwnThread`, which on Linux checks hand-offs and passes rather than a wait per chunk, `AConsumerDoesNotWaitAfterAShortRead`, `AConsumerWithASchedulingContextNeverWaitsForTheMasterInTheKernel`).

The same VM through BenchmarkDotNet on the implementation (one run; `main`'s and the prototype's are in [Results](#results)):

| VM, BenchmarkDotNet | MiniPty MiB/s | Porta.Pty ÷ MiniPty | `UseAsyncIo` ÷ MiniPty | Work items per op | Process CPU per op |
|---|---|---|---|---|---|
| 64 KiB, `AllCpus` | 210.4 | 1.06 | 1.07 | 0 | 132 μs |
| 64 KiB, `TwoCpus` | 228.9 | 1.25 | 1.16 | 0 | 123 μs |
| 1 MiB, `AllCpus` | 282.5 | 1.12 | 1.05 | 0 | 1.81 ms |
| 1 MiB, `TwoCpus` | 282.7 | 1.26 | 1.08 | 0 | 1.84 ms |

The harness on the implementation: 292–309 μs per 64 KiB flood, 1.3 waits, 0.27 loop passes, no work item and 130–136 μs of process CPU per flood, as the prototype. Allocations 0 B in every case.

Tests: the whole suite passes on macOS (731). On the VM 729 of 731 pass; the two that fail are the container's, and fail on `main` there alike: `DisposeLetsAnInteractiveShellHangUpItsJobs` (an interactive bash under a root shell with no job control) and `HostExitKillsAChildStillInItsHangupGrace`, whose killed child stays a zombie because the container's PID 1 is `sleep infinity` and reaps nothing (3 of 6 runs on `main`, 3 of 6 on the implementation; `kill(pid, 0)` succeeds on a zombie). A container started with `--init` would avoid the second.

## Phases

### Phase 0: Measure before changing (done)

- The harness, the strace timelines and the figures above. The harness is not kept, as the earlier plans' probes were not; its results are here.
- **Closed when:** the baseline and the prototype's figures are in this plan. Met.

### Phase 1: One read for a waiting consumer, and the ring starts over

- Change 1 in `UnixPtyBackend` (`ReadOnLoop`, `ReadInline`, `TakeOverReading`) and change 3 in `PtyOutputPump.NextReadSpan`, as the prototype has them, with the comments that say why.
- **Tests:** a 64 KiB flood through `ReadOutputAsync` on Linux makes fewer than 2 thread-pool work items and fewer than 2 loop passes per flood (today 4–6 and 3; `PtyUnixLoopTests` already counts both for the 1 MiB case); a consumer that stops after the loop parked on a full ring has the loop take reading over within a few milliseconds and the child is not stalled; output order across the consumer's and the loop's reads, with the park ended by the consumer; in `PtyOutputPumpTests`, a producer that publishes 4,095 bytes at a time into an emptied ring always gets a span of `PublishSize`, and a ring with unread output never moves its indices; the two coalescing tests that wrap the ring ([Results](#results)) rewritten to wrap it with output unread.
- **Closed when:** on the VM, work items and loop passes per 64 KiB flood are under 2 at no loss of time; the 1 MiB flood and keystrokes are within the spread between runs; allocations 0 B; the macOS figures of the macOS plan hold (change 1 runs there too: the loop's one read is the 1 KiB the master holds, which that plan's consumer already reads on from).

### Phase 2: The consumer waits on the master on Linux

- Change 2: `TryReadInline` waits on Linux as on macOS, by `poll` on the master with the millisecond timeout (`minipty_master_poll` in the prototype's last step), which measured level with a per-session epoll and holds no descriptor; the epoll branch the prototype added to `minipty_master_wait_open` is dropped. `_consumerWait` stays macOS's.
- The streaming rule stays as on macOS first (a read of at least 1 KiB starts the waits). If Linux keystroke or sparse-output measurements show waits that time out, raise the Linux threshold to a full read (4,095 bytes), which a flood's reads are and a line of a log never is.
- **Tests:** the macOS tests of the consumer's wait run on Linux too (`OnMacOSAConsumerWaitsForTheMasterOnItsOwnThread`, `OnMacOSAConsumerDoesNotWaitAfterAShortRead`, and the scheduling-context test); a wait sees end of output and hang-up and ends the enumeration; disposal and cancellation during the wait return within the timeout; the session's wait descriptor, if any, is closed with the master (descriptor count before and after 100 sessions); keystroke echoes cause no wait (`ConsumerWaitsForTests` stays 0 for a child that echoes one byte at a time).
- **Closed when:** on the VM, the 64 KiB flood on all CPUs is at least Porta.Pty's `UseAsyncIo` and ahead of its default mode, at no work item per flood and process CPU under half of `main`'s; keystroke P50 and P95 within the spread between runs on both jobs; allocations 0 B; the whole test suite passes on Linux and macOS.

### Phase 3: Confirm on CI, tune, record

- Three paired runs of `PtyThroughputBenchmarks` and `PtyLatencyBenchmarks` on `main` and the branch, started together, judged by MiniPty ÷ Porta.Pty within a run over the runs, as the earlier plans did.
- **Gates on `ubuntu-24.04`:** the 64 KiB flood's median ratio to Porta.Pty's default mode at least 1.0 on `AllCpus` (from 0.90–0.92) and on `TwoCpus` (from 0.92–1.04); the 1 MiB ratios no lower than today's (1.03 and 1.11); work items per 64 KiB flood under 1 (from 5–6); process CPU per flood no more than `UseAsyncIo`'s; keystroke P50 and P95 within the spread; allocations 0 B. macOS and Windows figures unchanged.
- Then move the finding and the lessons into [platform_support.md](../specs/platform_support.md) (the 16 KiB turns, the ring's geometry on Linux, the rejected wake skip), update the Backpressure paragraph of [core_session.md](../specs/core_session.md) (the consumer waits on the master on Linux too, and what that holds per session), the output pump table of [pty_crossplatform.md](../references/pty_crossplatform.md), the Linux paragraph of the README's Backends section, and the "Output buffer geometry" entry of [next_todo.md](../next_todo.md); remove this plan.

## Risks

- **A pool thread waits in the kernel for up to a millisecond per streaming session on Linux too.** The same bound as on macOS: only while the child streams, and the pool's injection covers the rest. Many sessions flooding at once each hold a pool thread while they flood, as they hold it to consume.
- **One more descriptor per plain consumer, if the wait is an epoll.** It counts against `RLIMIT_NOFILE` beside the master; 1,000 sessions read through `ReadOutputAsync` would hold 1,000 more. `poll` on the master avoids it, which is why Phase 2 measures it first.
- **The streaming rule on Linux.** A program that writes lines of a kilobyte or more a few milliseconds apart makes its consumer wait a millisecond per line, as it would on macOS; the full-read threshold is the knob if that shows.
- **A consumer ending the loop's park.** The park's flag and the free-space handshake were the loop's alone; the consumer now writes both under the producer's turn, which the loop holds for every write of its own. The takeover path is the one place the loop touches the park outside its pass, and it now goes through a service request. Phase 1's tests cover the consumer that stops after the park.
- **Two producers on one ring.** Unchanged from the earlier plans: the consumer and the loop take turns through the pump's gate, and the loop drops a report of output alone while the consumer holds the turn.
