# Flood Throughput on Small Runners Plan

Goal: MiniPty's output floods should not fall behind Porta.Pty on CI's hosted runners, which have 2 cores and 4 threads, without giving up what MiniPty is built on: no thread held per session, no allocation per chunk, and reads that keep pace with the child while the consumer is busy.

**Status:** open (2026-10-29). The cause is measured. Phase 0 is in the benchmarks and reproduces CI's ranking locally. Phase 1 (Unix) is implemented and meets its gates on Linux locally; macOS has not run it. Both wait for a CI run: the build workflow's tests on macOS, and a full benchmark run. Phase 2 (Windows) is next.

## Finding

On CI, MiniPty's 1 MiB floods lose to Porta.Pty on Linux and Windows. Locally, on a 16-core machine, they win or tie.

| 1 MiB flood, MiB/s | MiniPty | Porta.Pty | Porta.Pty, `UseAsyncIo` |
|---|---|---|---|
| Linux, run 40706187008 (EPYC 7763) | 139.3 | 188.0 | 194.7 |
| Linux, run 40726196725 (EPYC 9V45) | 199.2 | 272.9 | 259.0 |
| Windows out-of-band, run 40706187008 (EPYC 9V74) | 4.7 | 4.9 | 4.6 |
| Windows out-of-band, run 40726196725 (EPYC 7763) | 3.5 | 3.9 | 3.5 |
| Windows in-box, run 40726196725 (EPYC 7763) | 2.7 | 2.9 | 2.9 |

An earlier run on a Xeon 8370C put MiniPty's Linux floods 10% ahead. The ranking follows the runner, so the cause is in how the work is spread over threads, not in a fixed per-byte cost.

### Why

Every chunk of a flood crosses threads in MiniPty, and in neither of Porta.Pty's modes:

- **MiniPty, Unix.** The I/O loop thread reads the master and publishes each read. The consumer's continuation then runs on a thread-pool thread (`RunContinuationsAsynchronously`), so each chunk is a wake of the loop by the kernel and a hand-off from the loop to the pool.
- **MiniPty, Windows.** A zero-byte read's completion runs on a pool thread, which reads and publishes. The consumer's continuation is a second work item. That makes two pool work items per ConPTY write, and the out-of-band host writes a flood about 256 B at a time.
- **Porta.Pty, default.** One pool thread blocks in `read`, and the consumer's continuation runs inline on that thread, so a chunk costs one wake, done by the kernel.
- **Porta.Pty, `UseAsyncIo`.** The consumer's `ReadAsync` reads non-blocking on the caller's thread. It waits for its poller thread only on `EAGAIN`, with a registration for that one wait. During a flood the data is already there, so almost nothing crosses threads.

A hand-off is cheap only while an idle pool thread is spinning when it arrives. With CPUs to spare, a spinning worker picks up each chunk at once, and MiniPty's two threads overlap reading and consuming, which is why it wins locally. A runner with 2 cores runs the child, the kernel's tty worker (or the ConPTY host), the loop, and the consumer at the same time. There, spinning threads are preempted, hand-offs become sleeps and wakes, and the spinning takes CPU from the threads that do the work.

### Measurements

Measured outside BenchmarkDotNet with a harness that drives the benchmark's serve child (`--serve 1048576`) the way the benchmarks do: 300 floods after 30 of warmup, median. Machine: Ryzen 9 7950X3D, WSL2 (kernel 6.6) for Linux, Windows 11 26200 for Windows, 2026-10-29. `taskset -c 0-3` is 2 cores with their SMT siblings, as on CI, and `0-1` is one core.

Linux, per 1 MiB flood:

| | MiniPty | Porta.Pty | Porta.Pty, `UseAsyncIo` |
|---|---|---|---|
| 4 CPUs, MiB/s | 173–209 | 151–178 | 170–172 |
| 4 CPUs, process CPU | 13.7–15.5 ms | 14.1–15.0 ms | 4.6 ms |
| 4 CPUs, thread-pool work items | 260–294 | 216–228 | 2 |
| 4 CPUs, preemptions of pool threads | 162–499 | 10–49 | 0–1 |
| 4 CPUs, pool spinning off, MiB/s | 142–153 | 169–180 | 168–170 |
| 2 CPUs, MiB/s | 100–173 | 186–197 (one run 103) | 169–170 |
| 2 CPUs, process CPU | 10.8–14.2 ms | 8.7–9.5 ms | 4.8–5.0 ms |

- Per thread on 4 CPUs, MiniPty's pool threads used 10.0 ms per flood, mostly spinning between chunks, and its loop thread 3.9 ms. Porta.Pty's pool threads used 13.7 ms, and `UseAsyncIo` used 4.2 ms on one thread. Reading and scanning a flood takes about 4 ms. The rest is waiting for work.
- Pool spinning off (`DOTNET_ThreadPool_UnfairSemaphoreSpinLimit=0`) or one core reproduces CI's ranking locally: MiniPty about 10–20% behind Porta.Pty. The 2-CPU figures for MiniPty vary widely between runs, which is itself the sign of a path that depends on catching a spinning thread.

Windows, in-box ConPTY, per 1 MiB flood:

| | MiniPty | Porta.Pty | Porta.Pty, `UseAsyncIo` |
|---|---|---|---|
| Thread-pool work items | ~8,100 | ~4,100 | ~8,100 |
| Process CPU, 4 logical CPUs | 221 ms | 115 ms | 174 ms |
| Process CPU, 2 logical CPUs | 136 ms | 103 ms | 127 ms |
| Flood time, 2 logical CPUs | 126.9 ms | 116.6 ms | 130.6 ms |

The host renders at the same rate for every library, so wall times are close. MiniPty pays two work items per write where Porta.Pty pays one, and with one core that becomes 8% of flood time, as on CI.

### What did not help

Each was prototyped on a copy of the Unix driver and measured as above.

| Change | Result | Why |
|---|---|---|
| Signal the consumer once per loop pass instead of once per read | Work items 267 → 70 and preemptions 300 → 10, but 209 → 153 MiB/s on 4 CPUs | The consumer started later, the 16 KiB ring filled sooner, and the loop parked on it more often (82 → 138 passes per flood). The per-read signal is what lets reading and consuming overlap. |
| Read 8 KiB at a time into a 24 KiB ring | No change | The tty layer hands out about 4 KiB per read. The average chunk went from 3.3 KB to 4.0 KB. |
| Consumer reads the master inline while the loop keeps its level-triggered interest | 181–199 MiB/s on 4 CPUs (5–12% up), but 54–61 MiB/s on 2 CPUs | The loop woke for the same output and spun against the consumer. |
| Consumer reads inline, and the loop drops its read interest while the consumer reads, through service requests | CPU 14–15.5 → 5.8–6.0 ms and work items 280 → 20 on 4 CPUs, but 166–176 MiB/s there (today 178–200) and 126–163 MiB/s on 2 CPUs (Porta.Pty 193–197) | Reading switched between the consumer and the loop on every wait, and each switch woke the loop: 238–325 loop passes per flood on 2 CPUs, against 82 today. |

The last row shows where the cost is: once floods stop crossing threads, MiniPty uses about the CPU `UseAsyncIo` does. It also shows the hand-back must not wake the loop.

## Decision

Floods read on the consumer's thread, as `UseAsyncIo` does, and the transport is watched only when someone waits for it:

- A consumer that finds the ring empty reads the transport without blocking, on its own thread, before it waits. Only on "nothing there" does it wait.
- Readiness is watched one wait at a time: a registration that disarms itself when it reports (`EPOLLONESHOT`, `EV_DISPATCH`; on Windows, one zero-byte read). The consumer arms it when it is about to wait. The loop or the driver arms it when the consumer is busy and the ring has room. Nothing wakes the loop for output the consumer is about to read itself, and a hand-back costs no loop wake.
- One producer at a time: the driver and the consumer take turns through the pump, so publishing order and the ring's ownership rules hold.
- Unchanged: the driver still reads while a consumer is busy (a UI frame), so the child is not held up and timestamps stay close to arrival. A sink (`CompleteAsync`, capture) is still filled by the driver, and coalescing reads (the bridges) still wait for their minimum.

Not pursued: a thread blocked in `read` per session, Porta.Pty's default mode. It is the fastest here on one core, but it holds a thread per session, which MiniPty is designed not to do.

## Phases

### Phase 0: Reproduce CI in the benchmarks and count the work

- `PtyThroughputBenchmarks` and `PtyLatencyBenchmarks` run every case as job `AllCpus` and, on Linux and Windows with more than 2 logical CPUs, as job `TwoCpus`, with affinity on logical CPUs 0 and 1 ([SteadyStateJobs.cs](../../../src/MiniPty.Benchmarks/SteadyStateJobs.cs)). The README's tables quote `AllCpus`.
- Both report `Completed Work Items` and `Lock Contentions` per operation (BenchmarkDotNet's `ThreadingDiagnoser`). The throughput class also reports `Process CPU`: the benchmark process's CPU time over the measured iterations, per operation ([ProcessCpuDiagnoser.cs](../../../src/MiniPty.Benchmarks/ProcessCpuDiagnoser.cs)).
- **Closed when:** the constrained job puts MiniPty behind Porta.Pty locally on Linux and Windows and on CI, in the same direction as the table above. Met locally (below); CI is pending.

Results on the machine above, 2026-10-29. WSL has 8 logical CPUs here.

Linux, 1 MiB flood:

| Job, run | MiniPty | Porta.Pty | Porta.Pty, `UseAsyncIo` |
|---|---|---|---|
| `AllCpus` run 1, MiB/s | 206.3 | 169.8 | 210.1 |
| `AllCpus` run 2, MiB/s | 174.5 | 143.1 | 189.8 |
| `TwoCpus` run 1, MiB/s | 177.7 | 192.3 | 214.5 |
| `TwoCpus` run 2, MiB/s | 158.2 | 179.4 | 193.6 |
| `AllCpus` run 1, Process CPU | 13.71 ms | 14.72 ms | 4.70 ms |
| `TwoCpus` run 1, Process CPU | 9.59 ms | 8.39 ms | 4.70 ms |
| `TwoCpus` run 1, work items | 318.9 | 190.7 | 10.7 |

Windows, 1 MiB flood on 2 CPUs (`TwoCpus`):

| ConPTY, run | MiniPty | Porta.Pty | Porta.Pty, `UseAsyncIo` |
|---|---|---|---|
| in-box run 1, MiB/s | 6.5 | 6.9 | 6.3 |
| in-box run 2, MiB/s | 6.2 | 7.1 | 6.5 |
| in-box run 3, MiB/s | 6.6 | 7.1 | 6.1 |
| out-of-band run 1, MiB/s | 9.2 | 10.2 | 8.5 |
| out-of-band run 2, MiB/s | 11.6 | 10.3 | 10.4 |
| out-of-band run 3, MiB/s | 10.0 | 10.2 | 11.0 |
| in-box run 1, Process CPU | 148.96 ms | 132.03 ms | 180.47 ms |
| out-of-band run 1, Process CPU | 135.62 ms | 98.70 ms | 122.27 ms |
| work items, every run and ConPTY | 7,739–8,224 | 4,066–4,105 | 7,753–8,174 |

- Linux ranks the libraries as CI does in both runs. Windows does on the in-box ConPTY in every run. On the out-of-band ConPTY MiniPty was behind in two runs of three and ahead in one; its CPU per flood was 125–136 ms in the runs it trailed and 89 ms in the one it led. A path that is fast only when it catches a spinning thread varies this way, so judge Windows floods over several runs, and by work items, which hold in every run.
- On `AllCpus`, MiniPty leads Porta.Pty on Linux in both runs; on Windows each library leads in some runs.
- Keystrokes on 2 CPUs: on Windows' out-of-band ConPTY, MiniPty's P50 was 153.5 and 121.0 μs in two runs, Porta.Pty's 92.7 and 94.8 μs; on `AllCpus` they are level (71.8 against 70.0 μs). Both libraries take 2 work items per keystroke there, so MiniPty's are slower hand-offs, not more of them. On Linux MiniPty's keystrokes are no slower on either job, and the in-box ConPTY's frame schedule (about 14.5 ms) hides any difference.
- On Linux, BenchmarkDotNet's affinity reached every thread. In 25 samples of `/proc/<pid>/task/*/status` during `TwoCpus` cases, every thread of the benchmark process and of the serve child was on CPUs 0–1. BenchmarkDotNet sets the main thread's affinity right after the process starts, evidently before the runtime starts another thread, and later threads inherit it.

### Phase 1: Unix

- Read inline on the consumer's thread when the ring is empty, under the one-producer rule.
- Watch the master one wait at a time, on epoll and kqueue, armed by whoever is about to need it. Keep the stale-`EV_EOF` renewal ([UnixPtyBackend.cs](../../../src/MiniPty/Internal/UnixPtyBackend.cs), `RenewIfHangupIsStale`) working with self-disarming registrations.
- Hand reading back to the loop whenever the consumer stops: a wait, the enumeration's disposal, cancellation, disposal of the session.
- **Tests:** end of output and hang-up seen by an inline read; disposal and enumeration disposal while the consumer reads inline; a consumer that stops reading mid-flood (the loop takes over, the child is not stalled); macOS's 1 KiB master buffer; output order across consumer and loop reads.
- **Closed when:**
  - The `TwoCpus` 1 MiB flood is at least `UseAsyncIo`'s and within the spread between runs of Porta.Pty's default mode.
  - The `AllCpus` flood is no more than 5% below today's.
  - Process CPU per flood is within 1.5 times `UseAsyncIo`'s.
  - Keystroke P50 and P95 stay within the spread between runs, on both jobs.
  - Allocations stay at 0 B.

**Built (2026-10-29).** A plain `ReadOutputAsync` that finds the ring empty reads the master on its own thread (`IPtyOutputDriver.TryReadInline`). The loop and the consumer take turns as the ring's one producer through a gate in the pump; while the consumer holds it, the loop drops a report of output alone and defers anything else to the consumer's exit from the gate. Masters are registered one report at a time (`EPOLLONESHOT`, `EV_DISPATCH`). The consumer arms the master when it is about to wait, and the loop arms it after its passes unless the consumer reads inline. The pass that hands a consumer output after it waited leaves the master unarmed, since that consumer reads on inline. The loop checks the consumers that read inline about every millisecond, and reads again for one that has not read for that long. Coalescing reads and `Output` read as before.

Results on the machine above (WSL, 8 logical CPUs), the gate run's two rounds, 1 MiB flood:

| Job, round | MiniPty | Porta.Pty | Porta.Pty, `UseAsyncIo` | MiniPty before, same round |
|---|---|---|---|---|
| `TwoCpus` 1, MiB/s | 192.5 | 181.0 | 189.2 | 166.0 |
| `TwoCpus` 2, MiB/s | 156.2 | 163.1 | 161.0 | 120.2 |
| `AllCpus` 1, MiB/s | 187.7 | 162.9 | 188.2 | 184.6 |
| `AllCpus` 2, MiB/s | 161.8 | 125.0 | 178.5 | 111.7 |
| `TwoCpus`, Process CPU | 4.80–5.73 ms | 8.88–9.90 ms | 5.08–5.66 ms | 10.23–12.65 ms |
| `TwoCpus`, work items | 15.9–18.5 | 193–202 | 10.2–10.7 | 319 |

- Keystrokes stayed within the spread between runs: on `AllCpus` MiniPty's P50 was 125–130 μs against `UseAsyncIo`'s 123–124 μs in the same runs, and one work item per keystroke as before. Allocations stayed at 0 B.
- The machine varied by up to 2× between rounds, because WSL shares its CPUs with the `docker-desktop` distro. Compare within a round, and read the CPU and work-item columns, which held in every round.

What this phase learned:

| Tried | Result | Why |
|---|---|---|
| Arm the master under the session's lock, from the loop and from the consumer | 10–14 lock contentions per 1 MiB flood; over five rounds a `TwoCpus` median of 139 MiB/s at 1 MiB and 97 MiB/s at 64 KiB, against 144 and 118 without the lock | Each wait armed twice, once on each thread, and they met on the lock. Each side now sets the whole interest without a lock; the arms may land in either order. |
| Arm again after the pass that hands a waiting consumer output | About two loop passes per wait | The consumer is back reading inline before that arm reports. A consumer that waited after reading inline now stays unarmed once handed output, and the millisecond check covers it if it does not come back. |
| Yield once and read again before waiting | No consistent gain | Dropped. |
| The loop's discard after disposal, unchanged | Hung-up children blocked until the grace kill | It had relied on level-triggered readiness reporting again. Every pass that keeps reading must arm the master again; two disposal tests caught it. |
| Nobody reads for a consumer that reads inline and then stops | Lost the child's last output on Linux | The post-exit drain closed a quiet master that still held output. On macOS the child's exit would wait until the master was read. Hence the millisecond check. |
| `MoveNextAsync` racing disposal in its own loop | Ended the enumeration normally | The withdrawn wait went on to see the end of output. Inline reads keep the consumer in that loop far more often. Disposal now fails each turn of the loop, as the wait would. |
| Count loop passes per chunk in a test | Too close to the old figure (0.27 against 0.32) | The millisecond check wakes the loop on its own. Thread-pool work items per chunk tell the designs apart: 0.89–0.97 before, under 0.25 after, for a child that writes in 1 MiB writes. A child that writes slower than the consumer reads makes it wait every few reads. |

### Phase 2: Windows

- The same model on the ConPTY output pipe: the consumer takes what the pipe holds when the ring is empty (`PeekNamedPipe`, then a read that completes at once without a completion packet). A zero-byte read is posted only when the consumer is about to wait or is busy with room in the ring.
- The earlier lesson still holds: never leave a read posted into the ring for a consumer that keeps up, because canceling it costs a completion per write ([platform_support.md](../specs/platform_support.md)).
- A keystroke takes 2 work items in MiniPty and in Porta.Pty, yet MiniPty's is 25–60 μs slower on `TwoCpus` on the out-of-band ConPTY (Phase 0). Find which hand-off waits there before deciding whether the read model above covers it.
- Phase 1's pieces carry over: `IPtyOutputDriver.TryReadInline` and `HandReadingBack`, the pump's producer gate, and the reader's disposal check on every turn. So do its lessons: nothing the consumer does before a wait may take a lock the driver takes per read, a consumer that stops reading inline must not hold the child up or leave output for the post-exit drain to close on, and runs are judged by work items and CPU over several rounds.
- **Closed when:**
  - Work items per 1 MiB fall from about 8,100 to 4,100 or fewer on both ConPTYs.
  - The `TwoCpus` floods are within the spread between runs of Porta.Pty's default mode, over at least three runs.
  - The `TwoCpus` out-of-band keystroke P50 is within the spread between runs of Porta.Pty's.
  - The `AllCpus` keystrokes and all allocations are unchanged.

### Phase 3: Confirm on CI and record

- A full benchmark run on CI, whose README tables update themselves.
- Move this plan's findings and lessons into [platform_support.md](../specs/platform_support.md) and the driver descriptions in [core_session.md](../specs/core_session.md), then remove this plan.

## Risks

- **A UI consumer makes a system call per wait.** It does not block and returns in microseconds. The consumer yield ([PtyConsumerYield.cs](../../../src/MiniPty/Internal/PtyConsumerYield.cs)) still bounds a run of synchronous completions on a UI thread.
- **Losing the overlap of reading and consuming.** That overlap is why MiniPty leads with CPUs to spare. The current job's gate guards it. If one-wait-at-a-time readiness loses it, letting the loop arm readiness again right after it hands a chunk over is the knob to measure.
- **Two producers on one ring.** Every producer step (taking the ring, reading into it, publishing, letting it go) moves under the one-producer rule. A sink attaching mid-flood must still see output in order.
