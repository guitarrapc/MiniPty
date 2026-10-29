# macOS Flood Throughput Plan

Goal: on macOS, MiniPty's 1 MiB flood should reach Porta.Pty's default mode, on an 8-core desktop and on CI's `macos-26` runner (Apple M1 virtual machine, 3 vCPUs), without giving up what MiniPty is built on: no thread held per idle session, no allocation per chunk, and reads that keep pace with the child while the consumer is busy.

**Status:** implemented; the main change is confirmed on CI (2026-10-30) and the refinements after review await their CI run. The cause was measured outside .NET with a throwaway C probe (its modes and results are below; the file was not kept), Phases 1 and 2 are built and measured on the M2 ([Results](#results)), and three CI runs put macOS at 1.15–1.53 times Porta.Pty's default mode with no slow iteration ([Results on CI](#results-on-ci)). Left: the README's tables update themselves on the next full run on `main`, and the lessons move into the specs with [plan_flood_throughput.md](plan_flood_throughput.md)'s Phase 3, which carried this gap over from its macOS results ([macOS on CI](plan_flood_throughput.md#macos-on-ci)).

## Where MiniPty stands

Measured 2026-10-29 ([plan_flood_throughput.md](plan_flood_throughput.md#phase-1-unix)), 1 MiB flood, MiniPty ÷ Porta.Pty's default mode:

| Machine | MiniPty | Porta.Pty | Ratio | Work items per MiB, MiniPty |
|---|---|---|---|---|
| Apple M2, 8 cores, reads on the loop alone | 88–103 MiB/s | 105–122 MiB/s | about 0.85 | 1,031–1,037 |
| Apple M2, inline reads (reverted) | 122 MiB/s | 162 MiB/s | 0.75 | 695 |
| CI `macos-26`, 3 vCPUs, nine rounds | – | – | median 0.68 (0.30–0.96) | 508–878 |

On CI, MiniPty's iterations stay near 18 ms or near 40–60 ms for a whole run: 47% of them took over 30 ms, against 2% of Porta.Pty's default mode in the same processes.

## Finding

### A macOS master holds 1,024 bytes

The probe wrote to the slave without blocking until the write failed: 1,024 bytes, and the first read of the master returned all 1,024. That is the output queue's fixed size in the kernel (`TTYCLSIZE` in xnu's `tty.c`), not a water mark, so the terminal's speed does not change it: at the `B38400` the shim sets, the high-water mark is 2,048 and never reached. A child writing a flood therefore sleeps after every kilobyte until the master is read below the low-water mark, so a 1 MiB flood is at least 1,024 turnarounds of whoever reads the master, and the cost of one turnaround is paid 1,024 times per MiB. A Linux master hands out 4 KiB per read and holds many times that, which is why the Linux fix in the earlier plan cut work items to 16 per MiB there and to 695 on macOS.

### What a turnaround costs, by reader

The probe drove a child through a real pty with no .NET involved, 200 floods of 1 MiB after 10 of warmup, Apple M2 (8 cores), macOS 27.0.1, 2026-10-30. Medians:

| Reader | Models | ms per MiB | MiB/s | Syscalls per chunk |
|---|---|---|---|---|
| `block` | Porta.Pty default: one thread blocks in `read`, consumer runs on it | 5.2 | 192 | 1 |
| `kq_dispatch` | MiniPty's loop today, without its consumer: `kevent` wait, `read`, `read` to `EAGAIN`, `kevent` to re-arm | 6.2 | 161 | 4 |
| `kq_merged` | the re-arm folded into the next `kevent` wait | 4.6 | 218 | 3 |
| `kq_level` | level-triggered, no re-arm | 4.5 | 222 | 3 |
| `kq_merged_short` | as `kq_merged`, stopping after a short read instead of reading to `EAGAIN` | 4.7–5.0 | 199–212 | 2 |
| `kq_dispatch_handoff` | **MiniPty today**: `kq_dispatch`, each chunk handed to a second thread through a pipe | 8.8 | 114 | 4 + a wake |
| `kq_merged_handoff` | the re-arm folded, hand-off kept | 8.6 | 116 | 3 + a wake |
| `kq_merged_short_handoff` | re-arm folded and short reads, hand-off kept | 7.7 | 130 | 2 + a wake |
| `block_handoff` | blocking reader handing each chunk to a second thread | 5.6 | 178 | 1 + a wake |
| `inline_kq` | consumer's thread reads; on `EAGAIN` waits on a kqueue of its own, 1 ms timeout | 4.6 | 218 | 3 |
| `inline_poll` | consumer's thread reads; on `EAGAIN` waits in `poll(1 ms)` | 7.1 | 142 | 3 |
| `inline_spin` 10–200 μs | consumer's thread reads; on `EAGAIN` retries for that long, yielding between tries, then waits | 2.7 | 374 | about 2, no wait |

With 6 busy loops beside them on the 8 cores, so that the child, the reader and the consumer share about two:

| Reader | ms per MiB, median | MiB/s | p90 |
|---|---|---|---|
| `block` | 9.0 | 111 | 10.9 |
| `block_handoff` | 12.8 | 78 | 16.9 |
| `kq_dispatch_handoff` (MiniPty today) | 14.3 | 70 | 18.2 |
| `kq_merged_short_handoff` | 14.1 | 71 | 19.4 |
| `kq_merged_short` | 10.6 | 95 | 12.9 |
| `inline_kq` | 10.8 | 93 | 13.7 |
| `inline_spin` 20 μs | 8.5 | 118 | 24.2 |

With 7 busy loops, so that the child, the reader and the consumer share about one core, 100 floods:

| Reader | ms per MiB, median | MiB/s | p90 |
|---|---|---|---|
| `block` | 7.8 | 128 | 16.2 |
| `kq_dispatch_handoff` (MiniPty today) | 17.0 | 59 | 33.3 |
| `kq_merged_short` | 8.8 | 114 | 16.5 |
| `inline_kq` | 9.4 | 107 | 14.6 |
| `inline_spin` 20 μs | 23.1 | 43 | 65.8 |
| `inline_spin` 5 μs | 18.7 | 53 | 73.0 |

What the numbers say:

- **The hand-off per chunk is the cost.** Reading on one thread and consuming on another adds about 4 ms per MiB on an idle M2 (4.6 → 8.6 ms), and under load it puts the kqueue reader at 0.63 of the blocking reader (70 against 111 MiB/s), the ratio CI shows (0.68). MiniPty's structure sits at the probe's floor: 114 MiB/s against its measured 88–103. Porta.Pty's default mode has no hand-off because its blocked reader runs the consumer's continuation itself. The slow runs on CI are the hand-off's wakes landing on a sleeping pool thread: a wake that misses a spinning worker costs tens of microseconds on the virtual machine, once per kilobyte.
- **The separate re-arm `kevent` costs about 1.6 ms per MiB on its own, and the hand-off hides it** (8.8 against 8.6 ms with the hand-off). That is why the level-triggered experiment in the earlier plan measured nothing: the kqueue flags were not where the time went. Once the hand-off is gone, the re-arm is a quarter of what remains.
- **The read to `EAGAIN` costs about 0.9 ms per MiB with the hand-off** and nothing measurable without it. On macOS a short read means the queue is empty, as `ReadInline` already assumes.
- **One thread that reads and consumes beats the blocking reader when it waits in kqueue, and beats it twice over when it retries first.** On an idle machine the child refills the master within about 10 μs of a read, so a reader that retries for that long never sleeps and never wakes another thread: the probe's `inline_spin` made no `poll` call in 200 floods. `poll` is the wrong wait on macOS: 2.5 ms per MiB slower than a kqueue wait, since each call builds and tears down a kqueue registration.
- **Under load the retry turns against itself.** With two cores to share, its median still wins but its tail loses (p90 24 ms against 13 ms for `inline_kq`); with one, it is the slowest reader of all (43–53 MiB/s, p90 66–73 ms, against 59 for today's structure and 107 for `inline_kq`): a reader that yields to busy loops is not run again in time, and each retry it does get costs the child its turn. The kernel wait on the master, woken by the child's write, holds up under every load (107–218 MiB/s, within 15% of the blocking reader throughout), so it is the mechanism; a retry before it is a tuning that CI must earn, and may be 0.

### Why inline reads lost on CI

The earlier plan's inline reads on macOS (reverted) had the consumer read on its thread, and when the master was empty, arm the loop and park. On macOS the master is empty after almost every read, because the child has not yet been scheduled to refill it, so nearly every chunk still went master → loop → thread pool → consumer, now with the arm's `kevent` on top, and while the consumer was listed as reading inline the loop did not read ahead, so a consumer that fell behind on 3 vCPUs stalled the child until the loop's millisecond check. The consumer never waited on the master itself, which is the one thing the blocking reader does.

### Ruled out by analysis

- **A larger master buffer.** The queue is a compile-time kernel constant; neither the terminal's speed nor any ioctl changes it.
- **Larger reads.** A read never gets more than 1,024 bytes.
- **Level-triggered kqueue on macOS.** Same cost as folding the re-arm into the wait, and neither helps while the hand-off remains.
- **Running the consumer's continuation on the loop.** It is Porta.Pty's design in effect, and one slow consumer would then stall every session's reads.
- **A thread blocked in `read` per session.** Porta.Pty's default; it holds a thread per idle session.

## Decision

On macOS the consumer's thread reads the master and, when the master is empty, waits for it there: a kernel wait on the master with a short timeout, woken by the child's next write, and only when that times out does it arm the loop and park, as today. No retry of the read before the wait: in C it doubled an idle desktop's throughput, in .NET it lost everywhere ([Results](#results)). The loop keeps reading ahead while the consumer is busy with its chunk or gone, through the same millisecond check as on Linux, so a UI consumer that takes a frame per chunk still has the loop fill the ring for it.

The kernel wait is a kqueue of the session's own, created on the first such wait and closed with the master: `poll` was measured slower, and the loop's kqueue cannot be waited on by two threads. It is one more descriptor per session that a plain `ReadOutputAsync` consumer reads, none for sessions read by sinks, captures or the bridges.

Who takes this path: a plain `ReadOutputAsync` consumer on a thread-pool thread or a thread without a synchronization context. A consumer on a UI thread keeps today's path (the test [PtyConsumerYield](../../../src/MiniPty/Internal/PtyConsumerYield.cs) already makes), since it must neither spin nor wait in the kernel. Coalescing reads (the bridges) and sinks keep today's path too: they wait for a minimum the loop gathers.

The loop's own reads also get cheaper, which every path that stays on it keeps: the re-arm after a pass is folded into the next wait, and a pass stops after a short read.

## Phases

### Phase 0: Measure on the real stack before changing it

- Keep the probe's results here, so that a later reader starts from them. The probe itself was a one-off and was not kept: the harness that drives the real library ([Results](#results)) is what a later measurement would use.
- Record a baseline of `PtyThroughputBenchmarks` on the M2 and one paired CI round (`bench-baseline` started together), with `Completed Work Items` and `Process CPU`, which already tell the designs apart (about 1,030 work items per MiB today).
- **Closed when:** the baseline figures are in this plan.

### Phase 1: Cheaper passes on the loop (kqueue)

- `minipty_watch_wait` takes the arms the pass accumulated as the changelist of its `kevent` wait, instead of a `kevent` call per arm. Only arms from the loop's thread are deferred; a consumer's arm (`ArmForConsumer`) stays an immediate call, as it is on another thread. The probe's `kq_merged` ran 200 floods with one `kevent` per chunk and no missed readiness, so output that arrives while the filter is disabled is reported when it is enabled again.
- A pass on macOS stops after a short read instead of reading on to `EAGAIN`, as `ReadInline` does. A read of exactly the span's length reads again, as now.
- Keep `RenewIfHangupIsStale` working: a renewal deletes and re-adds the filters, which must stay an immediate call, since a deferred add after a deferred delete in one changelist is applied in order but the receipts must be checked.
- **Tests:** `PtyKqueueWatchTests` and `PtyUnixLoopTests` five runs each on macOS; a new test that output arriving between a pass and its wait is reported by that wait.
- **Closed when:** on the M2, a flood read by the loop alone (today's `ReadOutputAsync` on macOS, or `CompleteAsync`) is at least 15% faster, at no more work items and no more process CPU; keystroke P50 and P95 within the spread between runs; allocations 0 B.

### Phase 2: The consumer waits on the master (macOS)

- `TryReadInline` on macOS reads under the producer's turn as Linux does. When it reads nothing and the consumer may wait on its thread, it waits on the session's kqueue with a timeout of about a millisecond, then reads again under the turn. Only a wait that times out leaves the turn, arms the loop and parks, as today. No retry before the wait ([Results](#results)).
- The inline-reader listing and the millisecond check stay: a consumer busy in its own code for that long has the loop read for it, and the master is unarmed on the loop while the consumer reads inline, as on Linux.
- End of output and hang-up seen by the consumer's wait (`EV_EOF`, or a read of 0) are left to the loop: the consumer arms and parks, and the loop's pass ends output, so the kqueue's stale `EV_EOF` cannot spin the consumer.
- Disposal, cancellation and the enumeration's end while the consumer waits are bounded by the timeout; the close of the master drops its registration from the session's kqueue.
- The wait's timeout is a constant (1 ms, about the loop's own check of inline readers), to be confirmed on CI in Phase 3.
- **Tests:** a flood through `ReadOutputAsync` makes fewer than 50 work items and fewer than 50 loop passes per MiB (the loop's pass counter exists for tests); a consumer that stops reading mid-flood has the loop take over within a few milliseconds and the child is not stalled; end of output and hang-up seen during the consumer's wait end the enumeration; disposal and cancellation during the wait return within the timeout; a consumer with a synchronization context never retries or waits in the kernel; output order across consumer and loop reads; the session's kqueue is closed with the master.
- **Closed when:**
  - On the M2, the `AllCpus` 1 MiB and 64 KiB floods are at least Porta.Pty's default mode's in the same run (the probe puts the floor at 218–374 MiB/s against its 192).
  - On CI's `macos-26`, over at least six paired rounds, the median of MiniPty ÷ Porta.Pty is at least 1.0 and fewer than 10% of MiniPty's iterations take over 30 ms.
  - Process CPU per flood is no more than Porta.Pty's default mode's; work items per MiB under 50.
  - Keystroke P50 and P95 within the spread between runs; allocations 0 B.
  - Linux and Windows figures unchanged, and FreeBSD's kqueue path still builds (the per-session wait is macOS-only until measured there).

### Phase 3: Confirm on CI, tune, record

- Paired CI rounds against `bench-baseline`, judged as the earlier plan judges them: by ratios within a round, over many rounds, reading the per-iteration times.
- Tune the two bounds on CI, then the README's tables update themselves on a full run.
- Move the finding and lessons into [platform_support.md](../specs/platform_support.md) (the 1,024-byte master, the probe's results) and [core_session.md](../specs/core_session.md) (where a consumer reads and waits, per platform), and close this plan with the earlier one's Phase 3.

## Results

Measured 2026-10-30 on the Apple M2 (8 cores), macOS 27.0.1, with a harness outside BenchmarkDotNet that drives the benchmarks' serve child (`--serve 1048576`) the way the benchmarks do, through one `ReadOutputAsync` enumeration or Porta.Pty's `ReaderStream`: 200 floods of 1 MiB after 30 of warmup (100 under load), medians, two rounds each, the versions run alternately. Work items are thread-pool work items per flood, and passes are the loop's.

| Idle, 1 MiB flood | ms | MiB/s | p90 ms | Process CPU | Work items | Loop passes |
|---|---|---|---|---|---|---|
| Before (`c0ec5f0`) | 9.3–10.5 | 96–107 | 11.8–12.1 | 33–39 ms | 824–863 | 1,020–1,022 |
| Phase 1 alone | 9.8–10.6 | 95–102 | 12.1–12.3 | 36 ms | 665–798 | 1,025 |
| Phases 1 and 2 | 3.6–3.9 | 257–280 | 4.7–4.9 | 3.5–3.6 ms | 0 | 3 |
| Porta.Pty, default | 5.9 | 167–169 | 8.6–9.0 | 20–22 ms | 1,026 | – |
| Porta.Pty, `UseAsyncIo` | 6.9–7.0 | 143–145 | 9.3–10.0 | 19 ms | 687 | – |

| Under load, 1 MiB flood | 6 busy loops: MiB/s (p90 ms, floods over 30 ms) | 7 busy loops: MiB/s (p90 ms, floods over 30 ms) |
|---|---|---|
| Before | 62–64 (23.4–23.9, 5–6%) | 35–36 (58–65, **45–46%**) |
| Phases 1 and 2 | 76–77 (16.0–16.1, 0–3%) | 84–109 (11.6–25.8, 2–4%) |
| Porta.Pty, default | 80–84 (16.7–17.4, 1–2%) | 55–80 (24.8–31.5, 6–12%) |

- **Phase 1 alone changed nothing measurable**, as the probe predicted: the re-arm's cost hides behind the hand-off. It is kept because it is a quarter of what remains once the hand-off is gone, and because every path that stays on the loop (sinks, captures, the bridges, the discard after a hang-up) gets it.
- **Phase 2 meets its M2 gates with room**: 1.5–1.7 times Porta.Pty's default mode, a tenth of the process CPU, no work item per flood, and the loop woken about three times per flood for its millisecond check. Under load it is level with Porta.Pty on two shared cores and ahead on one, and the bimodal slow runs of the version before it, which 7 busy loops reproduce (45% of floods over 30 ms, as CI's 47%), are gone (2–4%).
- **The retry before the wait is removed, not a knob.** At 10 μs, with `Thread.Yield` and a `FIONREAD` check per try, it took the idle M2 from 250 to 72 MiB/s and the loaded core from 85 to 27 (63–82% of floods over 30 ms, 314–364 loop passes per flood): the retries took the CPU the child needed to refill the master, and the loop's millisecond check then took reading back. The probe's retry had looked better only in C and only idle. The kernel wait alone carries the design.
- **Keystrokes** (the same harness, 2,000 echoes of one byte, two rounds): P50 12 μs after against 14 before and 11–12 for Porta.Pty's default mode; P95 13–14 μs against 18–20 and 16–17. An echo that arrives within the consumer's wait costs no work item and no loop pass; one that arrives later takes the loop's path as before.
- **64 KiB floods**: 60.6 MiB/s against Porta.Pty's 62.7 in the same run (1.03 against 1.00 ms per flood), level.
- **NativeAOT**: the ILC compile of `samples/SingleFiles/Session.cs` for `osx-arm64` succeeds with the change; the link on this Mac fails with `ld: library 'ssl' not found` before and after alike (the Homebrew .NET's NativeAOT pack asks for OpenSSL), so the publish itself is left to CI.
- Tests: the whole suite passes on macOS (727), and `PtyUnixLoopTests` and `PtyKqueueWatchTests` five runs each, with two new tests, one that a flood's consumer waits for the master on its own thread with few loop passes, and one that a consumer on a thread with a scheduling context never waits in the kernel.

## Results on CI

Three runs of the benchmark workflow on commit `54d7c89`, started together on 2026-10-29: [40917502003](https://github.com/guitarrapc/MiniPty/actions/runs/40917502003), [40917535974](https://github.com/guitarrapc/MiniPty/actions/runs/40917535974), [40917577190](https://github.com/guitarrapc/MiniPty/actions/runs/40917577190). `PtyThroughputBenchmarks` only (10 iterations per case), judged as the earlier plan judged macOS: by MiniPty's throughput as a share of Porta.Pty's default mode in the same run, and by the per-iteration times in the log.

| `macos-26`, 1 MiB flood | MiniPty MiB/s (median ms) | Porta.Pty MiB/s (median ms) | MiniPty ÷ Porta.Pty | Iterations over 30 ms, MiniPty / Porta.Pty | Process CPU, MiniPty / Porta.Pty | Work items, MiniPty / Porta.Pty |
|---|---|---|---|---|---|---|
| Run 1 | 49.8 (20.5) | 32.6 (31.4) | 1.53 | 0 of 10 / 6 of 10 | 10.9 / 12.8 ms | 0.03 / 1,026 |
| Run 2 | 51.6 (19.8) | 44.0 (22.8) | 1.15 | 0 of 10 / 0 of 10 | 11.7 / 9.0 ms | 0.03 / 1,026 |
| Run 3 | 49.7 (19.8) | 36.8 (26.9) | 1.35 | 0 of 10 / 0 of 10 | 11.7 / 15.6 ms | 0.09 / 1,026 |

Three more runs of the same commit `54d7c89`, started together on 2026-10-29 after the refinements below were committed but before that push reached the workflow ([40932856203](https://github.com/guitarrapc/MiniPty/actions/runs/40932856203), [40932884053](https://github.com/guitarrapc/MiniPty/actions/runs/40932884053), [40932911073](https://github.com/guitarrapc/MiniPty/actions/runs/40932911073)):

| `macos-26`, 1 MiB flood | MiniPty MiB/s (median ms) | Porta.Pty MiB/s (median ms) | MiniPty ÷ Porta.Pty | Iterations over 30 ms, MiniPty / Porta.Pty | Process CPU, MiniPty / Porta.Pty |
|---|---|---|---|---|---|
| Run 4 | 53.1 (18.7) | 38.9 (26.0) | 1.37 | 0 of 10 / 1 of 10 | 11.0 / 10.9 ms |
| Run 5 | 43.7 (22.6) | 32.6 (31.4) | 1.34 | 0 of 10 / 7 of 10 | 13.8 / 15.5 ms |
| Run 6 | 46.9 (21.6) | 37.4 (28.3) | 1.26 | 0 of 10 / 3 of 10 | 12.3 / 14.3 ms |

- **The CI gates hold.** The median ratio over the six runs is 1.35 (1.15–1.53), against 0.68 over nine runs of the version before (range 0.30–0.96), and the goal of 1.0; none of MiniPty's 60 iterations took over 30 ms, against 47% before. MiniPty's iterations sit in a band of 16.7–24.9 ms across the six runs, where before a whole run sat near 18 ms or near 40–60 ms. Porta.Pty's `UseAsyncIo` shows what the runner still does to a design that waits on another thread: 80–100% of its iterations over 30 ms. Allocations stay at 0 B.
- The 64 KiB flood: 37.6, 43.9, 49.1, 50.1, 41.4 and 42.2 MiB/s against Porta.Pty's 15.7, 24.1, 48.2, 20.9, 15.0 and 35.7 (1.02–2.8).
- The plan asked for six paired rounds against `bench-baseline`; these are six unpaired runs. With every iteration of every run inside the gate, and the before figures from nine runs on the same runner type, pairing was not needed to tell the two apart.
- **Linux** reads as before: `AllCpus` 1 MiB at 0.90, 0.95, 1.04, 1.03, 1.04 and 1.03 times Porta.Pty (median 1.03, as over the earlier plan's nine runs) and `TwoCpus` at 1.08, 1.21, 1.10, 1.17, 1.16 and 1.11 (1.11 before), with 7–26 work items and 4.0–5.2 ms of process CPU per flood as before. The first three runs' `AllCpus` figures were runner spread, not the deferred arms these six runs all had; Linux arms at once again anyway (below).
- **Windows** code is untouched. Its `AllCpus` floods are level with Porta.Pty on the in-box ConPTY (0.99–1.00 in all six runs) and about 12% behind on the out-of-band one (0.86–0.89), as the README's run before this plan had them (3.4 against 3.9 MiB/s); on `TwoCpus` MiniPty trailed in 11 of 12 cases (0.60–0.99), where the earlier plan had the out-of-band floods ahead locally. That is a Windows question for that plan's Phase 3, outside this one.

## Refinements after review (2026-10-30)

A review of the change for side effects found two costs worth removing, measured with the same harness against the committed version (`54d7c89`):

- **A millisecond of a thread-pool thread per sparse output event.** After any chunk, the consumer's next read found the master empty and waited the whole millisecond before it parked: for a keystroke's echo or a line of a log, output that was not coming. Not CPU, so the earlier figures did not show it, but on a host with thousands of sessions each writing a line every few milliseconds it would have held a thread per session in waits. The consumer now waits only while the child streams (`_outputStreams`): a read that took the whole kilobyte the master holds sets it, since the child was held up and has more, and a wait that brings nothing clears it. The first version of this rule, "wait only if the last read was a full kilobyte", lost a third of the flood (150–170 MiB/s, 42–47 hand-offs per MiB): the consumer's reads wake on the first bytes the child queues and are often short, 461 bytes on average in the probe, even though its waits pay off. Keeping the flag until a wait fails keeps the flood whole.

| | 1 MiB flood, MiB/s | Keystrokes back to back, P50 / waits per key | Keystrokes 3 ms apart, P50 / waits per key / loop passes per key |
|---|---|---|---|
| `54d7c89` (always waits) | 241–302 | 12 μs / 1.00 | 41 μs / 0.99 / 3.4 |
| Refined | 244–246 | 12 μs / 1.00 | 48 μs / 0.05 / 1.2 |

  Back-to-back keystrokes still wait, and should: each wait brings the next echo within microseconds, so the stream of echoes is read without a hand-off. Three milliseconds apart, the wait would time out every time, and it no longer happens.
- **Linux deferred its arms for nothing.** `epoll_ctl` cannot ride on `epoll_wait`, so deferring an arm to the wait saved no call and only moved it to the end of the pass. Linux arms at once again, as before this plan; kqueue keeps the deferral. The CI Linux figures above (0.90–1.04 on `AllCpus`) were taken with the deferral and are the first thing to re-read on the next run.

Tests: `OnMacOSAConsumerDoesNotWaitAfterAShortRead` checks that a child writing a line every 10 ms never has its consumer wait in the kernel.

**Not yet on CI.** The refinements are commit `52ef862`; the six CI runs above all ran `54d7c89`, before them. A run of `52ef862` is still owed, to confirm that macOS keeps its figures with the streaming flag and that Linux is unchanged with its arms applied at once again.

## Risks

- **A pool thread waits in the kernel for up to the timeout.** During a flood it is the consumer's own thread, busy anyway; after a flood's last chunk it waits one timeout, then parks. Many sessions flooding at once each hold a pool thread while they flood, as they hold it to consume. The timeout bounds every wait, and the pool's injection covers the rest. Document it in core_session.md.
- **Retrying on a loaded 3-vCPU runner.** Measured and removed ([Results](#results)): in .NET the retry lost even on the idle M2.
- **One more descriptor per plain consumer.** Created lazily, closed with the master; counts toward the process's descriptor limit alongside the master. macOS caps terminals at 511 per machine anyway ([platform_support.md](../specs/platform_support.md)).
- **Two producers on one ring.** Unchanged from the Linux design: the consumer and the loop take turns through the pump's gate, and the loop drops a report of output alone while the consumer holds the turn.
- **Deferring arms to the wait.** An arm deferred behind other sessions' service in the same pass delays that session's readiness by the pass, as the re-arm at the end of its own service does today. A renewal after a stale hang-up stays an immediate call.
