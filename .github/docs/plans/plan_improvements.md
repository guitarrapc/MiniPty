# Improvement Plan

Goal:

- Every figure the README publishes is measured on the code that ships.
- Every path the package promises runs in CI, or has a recorded manual check that can be repeated.
- CI fails only on regressions.

The items below came from a review of the library as a whole. Each is closed by a fix, or by a decision recorded in the specifications, as its section says.

**Status:** Phase 1 done on branch `improve` (2026-11-07), not yet merged. I2 and the bridge half of I3 are done. The echo half of I3 is fixed in the test: a temporary trace, since removed, found the test's own thread holding the slow echoes on macOS, not MiniPty, and the paced test passed 20 runs of 20 there; see [Phase 1](#phase-1-trustworthy-ci-i3-i2). Phase 1 closes when `improve` is merged.

Phase 2 is done on the same branch, also not yet merged; see [Phase 2](#phase-2-current-figures-i1-i5-i7).
- **I1:** three full runs hold every gate but the in-box keystroke. That one holds in a local A/B, and the cost behind it is located and recorded.
- **I5:** the bytes are one-time costs, and no flood allocates.
- **I7:** the helper stays, since `posix_spawn` cannot give the child its controlling terminal.

Phases 3 and 4 are not started. The CI evidence in the findings comes from the Build runs of 2026-11-05.

## Inventory

| ID | Kind | Problem | Closed when |
|---|---|---|---|
| I1 | Measurement | The README's benchmark tables predate the Linux start rewrite, the metrics and the Windows exit watcher. They show no start in a large host | The README shows a full run of current `main`, including starts at 0 and 2 GiB resident. No regression stands unexplained |
| I2 | Test coverage | The start's fallbacks for older Linux kernels were checked once, by hand. CI's kernels never take them | A CI job runs the suite with the newer calls refused, on glibc and on musl, and proves that the refusals took effect |
| I3 | Test reliability | Two tests fail intermittently on hosted runners: an echo bound on macOS Intel, and the bridge's flow control on Windows Server 2022 | Each cause is found and fixed in the test or the library. Each test passes 20 consecutive runs on its runner |
| I4 | Verification, docs | Server 2022 is the only Windows build before 24H2 that was measured. The package smoke test skips it, and the README does not say how the in-box host draws there | The suite has run on Windows 10 22H2, Windows 11 23H2 and build 17763. The smoke test runs on windows-2022, and the README states the differences |
| I5 | Measurement | Windows flood benchmarks report a few bytes per operation, explained only by inference | The source of each figure is shown by measurement, then fixed or explained next to the table |
| I6 | Platform | FreeBSD's start path has not been compiled since Linux stopped using it | FreeBSD is compiled in CI, or dropped |
| I7 | Performance | A macOS start takes about six times as long as a Linux start, and the helper's share is unknown | The breakdown is measured and recorded, and the helper stays or goes by that measurement |

- **I1 and I2 matter most.** I1 checks that the shipped code keeps the published numbers. I2 guards paths that the stated Linux floors depend on and that nothing else runs.
- **I3 comes first in time.** Every later item relies on CI runs.
- **I5 and I7 start with measurement.** A code change follows only from what they find.
- **I6 needs a decision before work starts.**

## I1: The README's benchmarks predate the current code

### Finding

- **The tables are from one run before the recent changes.** The README's tables come from [run 41106916265](https://github.com/guitarrapc/MiniPty/actions/runs/41106916265), made on 2026-10-30 with .NET 10.0.14 on commit `5ca042f`. Since then, these changes have reached paths that the benchmarks measure:
  - **Linux start:** the child is started with `clone(CLONE_VM | CLONE_VFORK)` instead of `forkpty`.
  - **Metrics:** a check of each instrument's `Enabled` flag at every recording on the output path.
  - **Windows exit watcher:** process exits are observed through thread-pool waits.
- **No later run rewrote them.** CI rewrites the tables only after a full benchmark run. The three benchmark runs since (2026-11-02) did not rewrite them.
- **The start's independence from host size is not published.** It is the main gain of the start rewrite:
  - **Measured only locally.** `PtyStartupHostMemoryBenchmarks` measured it in WSL and in Docker Desktop, with nothing touched and with 2 GiB resident. The start took 0.94 and 0.88 ms there, against 2.31 and 37.3 ms with a forking start ([platform lessons](../specs/platform_support.md#lessons-learned)).
  - **Not in the README.** The benchmark workflow runs it, but [UpdateReadmeBenchmarks.cs](../../../scripts/UpdateReadmeBenchmarks.cs) writes no table for it.
- **One macOS run cannot place macOS.** Over 20 runs on the hosted runner, MiniPty's 1 MiB flood ranged from 38.9 to 64.4 MiB/s ([next_todo.md](../next_todo.md#open)).

### Why it matters

- **Users compare libraries by these tables.** A regression since 2026-10-30 would stay hidden behind numbers measured before it.
- **The README does not show the strongest result of the start rewrite.** Without a figure, the claim rests on prose.

### Fix

- **Re-measure.** Run the benchmark workflow with the filter `*` three times on `main`, once the Windows Server 2022 changes have landed there. Each run rewrites the README, and the last one stays.
- **Compare.** Compare each run with run 41106916265 by the ratios within each run and by the per-iteration times, as the [platform lessons](../specs/platform_support.md#lessons-learned) prescribe.
- **Publish the start in a large host.** Add a README block for `PtyStartupHostMemoryBenchmarks` to the updater and to the README: Mean and Allocated at 0 and 2048 MiB, for each runner.
- **If a gate fails:** bisect between `5ca042f` and `main`.

Gates:

- **Allocations:** every keystroke and flood case that allocated 0 B still allocates 0 B. I5 covers the cases that did not.
- **Linux and Windows:** in at least two of the three runs, the keystroke and flood ratios are within 10% of those in run 41106916265.
- **macOS:** MiniPty's own 1 MiB flood falls within its recorded range, 38.9–64.4 MiB/s.
- **Start:** Mean and Allocated are within 10% of run 41106916265's.

  | Runner | Mean | Allocated |
  |---|---|---|
  | Linux | 1.569 ms | 1.26 KB |
  | macOS | 10.271 ms | 1.27 KB |
  | Windows | 16.87 ms | 1.23 KB |

- **Start in a large host:** with 2048 MiB resident, the start is within 10% of the start with nothing touched, on every runner.

## I2: The fallbacks for older Linux kernels run only by hand

### Finding

- **CI's kernels never take the fallbacks.** CI's Linux jobs, containers included, run on hosted Ubuntu 24.04 kernels (6.8 or later). Those kernels have every call MiniPty uses. A container shares its host's kernel, so the AlmaLinux 8 and Alpine smoke containers take the same paths as the runner.
- **The start has these fallbacks** ([Verification](../specs/platform_support.md#verification)):

  | Refused or missing | Fallback |
  |---|---|
  | `TIOCGPTPEER` | Opens the slave by its path |
  | `clone` with `CLONE_PIDFD` | Starts without the flag, then calls `pidfd_open` |
  | `pidfd_open` | Works without a pidfd: signals the child with `kill`, after checking that the pid is still its unreaped child |
  | `close_range` | Marks descriptors close-on-exec one by one |
  | `waitid(P_PIDFD)` | Reaps by pid |
  | A pidfd that epoll refuses | The exit watcher polls every 10 ms |

- **They were checked once, on 2026-11-04.** Two methods were used: seccomp profiles in Docker, with glibc and with musl, and Linux 4.9, 5.2 and 5.3 under QEMU. Neither the profiles nor the QEMU harness were kept in the repository.
- **Hosts at the stated floor take them.** glibc 2.27 is Ubuntu 18.04's, whose release kernel, 4.15, predates:
  - `CLONE_PIDFD` (5.2).
  - `pidfd_open` (5.3).
  - `close_range` with `CLOSE_RANGE_CLOEXEC` (5.11).

### Why it matters

- **A broken fallback passes CI.** A change to the start path that breaks a fallback fails only on hosts with older kernels, at their first `Pty.Start`.
- **Part of this code runs in the child,** between `clone` and `exec`, where a mistake is hard to see from the parent.

### Fix

- **Commit the profiles** under `scripts/`:
  - **Old kernel.** Refuses `TIOCGPTPEER` with `ENOTTY`, and `clone` with `CLONE_PIDFD` and `waitid` with `P_PIDFD` with `EINVAL`. Refuses `pidfd_open`, `pidfd_send_signal`, `close_range` and `clone3` with `ENOSYS`. This is the first profile of the manual check, plus `waitid`, which a kernel without pidfds lacks too.
  - **No `CLONE_PIDFD`.** Refuses only the flag, so the start opens the pidfd by pid. This is the second profile of the manual check.
  - **No `waitid(P_PIDFD)`.** Refuses `waitid` with `P_PIDFD` with `EINVAL`, so children are reaped by pid. This is the path that Linux 5.2 and 5.3 take. It was seen only under QEMU, never under a filter.
- **Prove each refusal first.** Before the suite, a small C probe checks that every refusal took effect, and that every other call works. It is built with the compiler that builds the shim in the container. A profile that stops matching then fails the job, instead of letting the suite pass on the newer paths. The probe also runs under Docker's default profile on every run, where it must fail.
- **Add the job to Build.** A job on `ubuntu-24.04` runs the whole suite in a container under each profile, on the .NET SDK image (glibc) and on its Alpine image (musl). A run takes about a minute, so no class list is kept.
- **Keep the QEMU harness.** Commit it to `scripts/`, so it can be repeated before a release that changes the start path. It downloads Debian kernels from snapshot.debian.org and packs a .NET runtime image as the guest's root file system.

### Constraints

- **The job cannot use a job container.** A job container starts before the checkout, so it cannot take a profile from the repository. The steps run Docker from the runner, as the native build does at the libc floors.
- **A filter cannot reproduce every kernel.**
  - **An ignored flag:** Linux 4.9 ignores `CLONE_PIDFD`, so `clone` succeeds there and gives no pidfd. A filter can refuse a call, but it cannot make the kernel ignore a flag.
  - **epoll on pidfds only:** Linux 5.2's epoll refuses pidfds. A filter cannot tell a pidfd from another descriptor by its arguments.

  Both stay with the QEMU harness, and [platform_support.md](../specs/platform_support.md#verification) says so.
- **The timing tests already adjust.** Tests that depend on pidfds relax their bounds where pidfds do not work (`PidfdsWork`), as in the manual check.

## I3: Two tests fail intermittently on hosted runners

### Finding

On 2026-11-05, six Build runs of one commit gave three failures, each in a different place:

| Run | Runner | Failure |
|---|---|---|
| [42092026752](https://github.com/guitarrapc/MiniPty/actions/runs/42092026752) | macos-26-intel | `AFloodDoesNotDelayAnotherSessionsEcho` ([PtyUnixLoopTests.cs:28](../../../tests/MiniPty.Tests/PtyUnixLoopTests.cs)) |
| [42089710520](https://github.com/guitarrapc/MiniPty/actions/runs/42089710520) | windows-2022 | `BridgeFlowControlPausesWithoutAcksAndResumesOnAck` ([PtyWebSocketBridgeTests.cs:202](../../../tests/MiniPty.Tests/PtyWebSocketBridgeTests.cs)) |
| [42085901803](https://github.com/guitarrapc/MiniPty/actions/runs/42085901803) | windows-2025 | The WinUI sample could not restore: downloading `Microsoft.Web.WebView2` from nuget.org failed. This was not MiniPty |

**The echo test:**

- **What failed.** 37,589 echoes were measured in about half a second, with a median of 0.00 ms. The slowest took 100.4 ms, and the bound is under 100 ms.
- **The test asserts the maximum.** One stall in 37,589 echoes therefore fails it.
- **The cause was unknown.** The test did not record whether the flood stalled at the same moment. The stall may have been the whole process, as when the VM is descheduled. It may also have been MiniPty's loop serving the flood ahead of the echo. The repeats of 2026-11-06 seemed to point at MiniPty. The trace of 2026-11-07 found the test's own thread instead ([Phase 1 progress](#phase-1-trustworthy-ci-i3-i2)): its first call of each method, the recompilation of its own method in the middle of its loop, and the scheduler leaving its busy loop without the CPU. MiniPty held no echo.

**The bridge test:**

- **What failed.** The test stopped acknowledging, saw delivery stall and stay stalled, and then granted all credit. After that, the client's wait for the close never ended, and the test's 60-second budget canceled it (line 242).
- **The child.** `cmd /c` echoes 256 lines of 118 characters.
- **The likely cause is already fixed.** The three runs tested commit `76058c8`, which predates the fix for acknowledgements lost while their send had not yet returned, merged with #72. [terminal.md](../specs/terminal.md) records that race as the likely cause of this hang: after the grant the test acknowledges every frame, a lost acknowledgement leaves its frame's bytes counted for good, and once they reach the high watermark, delivery pauses with nothing to resume it. The test does not record where the time went, so this is not confirmed.

### Why it matters

- **Failures that are not regressions train everyone to re-run.** Two of the six runs failed in MiniPty's tests, so about one push in three needs a re-run.
- **A failure that is a regression then looks like noise.**
- **The bridge failure may not be the test's fault.** If the cause is not the race, but a session's output that can fail to end on a build without `ReleasePseudoConsole`, bridges served on Windows 10 would hang at the end of a session.

### Fix

- **Echo test: record before deciding.**
  - In the test, record what else ran during each echo: a thread of the test's own ticks every millisecond and samples the flood's byte count and the thread pool's queue, and the echo records the collector's pauses. A long gap between ticks points to the process; a flood that kept flowing while the echo waited points to the loop or the thread pool.
  - Run the test 20 times on macos-26-intel, with a temporary CI job that repeats it.
  - If the process stalled: assert on a high percentile, and keep a looser bound on the slowest echo.
  - If the loop delayed the echo: fix the loop.
  - If the test's own thread held it (what the trace found): warm the echo path up before the clock starts, and pace the echoes, so the measuring thread waits between keystrokes as a typist does. A paced echo then goes through the loop, which is what the test means to measure; an echo loop that never waits measured mostly the consumer's inline reads.
- **Bridge test: confirm the fix, and record the phases.**
  - Put the phase timings in the failure message: the bytes received at the stall and at the grant, the bytes and the time of the last frame since, whether the exit message arrived, and the bridge's state.
  - Repeat the test's class on windows-2022, on code that has the fix, with a temporary CI job, until it fails or passes 20 times.
  - If it fails, fix the test or the library by what the message shows.
- **No test hooks in the library.** Every diagnostic lives in the tests. The temporary CI steps are removed once the cause is found.

## I4: Windows builds before 24H2 are measured only on Server 2022

### Finding

- **Only Server 2022 was measured.** Of the builds before Windows 11 24H2, only Windows Server 2022 (build 20348) was measured ([Verification](../specs/platform_support.md#verification)).
  - **Mitigations applied everywhere, unmeasured.** MiniPty applies its mitigations for that build to every build without `ReleasePseudoConsole`: Windows 10 1809 to 22H2, Server 2019, and Windows 11 21H2 to 23H2. They were not measured on those builds.
  - **No runner for the minimum.** Build 17763, the minimum, has no hosted runner.
- **The package smoke test never runs on such a build.** The package smoke test runs on every test runner except windows-2022: the `nuget-smoke` matrix in [build.yaml](../../workflows/build.yaml) does not list it. So the package as shipped never runs on a build without `ReleasePseudoConsole`.
  - The TUI smoke test skips windows-2022, because it checks exact cells and the in-box host draws differently there.
  - The package smoke test checks properties only, so it has no such reason to skip.
- **Disposal there is not benchmarked.** Disposal keeps the output pipe open until the in-box host has ended, for 1 s at most ([core_session.md](../specs/core_session.md)). What a session costs from start through disposal on such a build has not been benchmarked: the benchmark workflow runs on windows-2025, which has `ReleasePseudoConsole`.
- **The README is silent about these builds.** Its section on the out-of-band ConPTY lists what the in-box host drops: graphics protocols, some queries, and mouse tracking for programs that do not read VT input. It does not say what Server 2022 showed:
  - **No alternate screen and no hidden cursor.** The host forwards neither the switch to the alternate screen nor the hidden cursor.
  - **A pending wrap past `ESC[K`.** After a full row, it relies on the pending wrap outliving `ESC[K`, which xterm ends.

  MiniPty cannot change either. The README also does not say that a process should await `DisposeAsync` before it exits on these builds.

### Why it matters

- **Users there cannot tell where the fault is.** A frontend developer on Windows 11 24H2 never sees these differences, but their users on Windows 10 do. Without a note, broken full-screen programs there look like MiniPty's bug.
- **The mitigations may not hold elsewhere.** If another build differs from Server 2022, a session there may hang at disposal, and no test would notice.

### Fix

- **Measure on VMs.** Run the test suite and [conpty-close-probe.cs](../../../scripts/conpty-close-probe.cs) once on local VMs: Windows 10 22H2 (19045), Windows 11 23H2 (22631), and build 17763 (Windows 10 1809 or Server 2019). For each build, record which of the four differences found on Server 2022 hold, and which of the two drawing differences.
- **Add windows-2022 to the `nuget-smoke` matrix.**
- **Measure disposal once.** Run `PtyStartupBenchmarks` once on windows-2022, and record the cost of a session from start through disposal.
- **Update the README.** In "Out-of-band ConPTY for Windows":
  - State the drawing differences: measured on Server 2022, and on the other builds once the VM runs confirm them.
  - Recommend the out-of-band ConPTY for terminal emulators on builds before 24H2.
  - Add that a process should await `DisposeAsync` before it exits on those builds.

## I5: Windows floods report a few bytes per operation

### Finding

- **Three figures are not 0 B:**
  - **README:** the out-of-band 1 MiB flood shows 12 B per operation. The README's feature list says the steady state allocates nothing.
  - **`PtyMetricsBenchmarks`, without a listener:** the in-box flood allocated 8 B per operation in one case.
  - **`PtyMetricsBenchmarks`, with a listener:** it allocated 168 B in one case. [core_session.md](../specs/core_session.md#metrics) records that the recording calls do not account for it.
- **The explanation is an inference.** The [platform lessons](../specs/platform_support.md#lessons-learned) explain such figures as a thread-pool thread added during the run. BenchmarkDotNet counts every thread's allocations, and a new thread costs about 1 KB. The size fits, but no run has shown that a thread was added in these cases.

### Why it matters

- **The README contradicts itself without a note.** A reader sees a promise of zero allocations next to 12 B, with nothing to explain it.
- **The explanation could hide a real allocation.** If the bytes are not a thread, they are a per-flood allocation, and the explanation keeps it from being found.

### Fix

- **Find the source.** Use a diagnostic harness outside BenchmarkDotNet, which runs the same floods many times:
  - Around each round, it reads `GC.GetTotalAllocatedBytes(precise: true)` and `ThreadPool.ThreadCount`.
  - It covers all three cases: out-of-band, in-box, and in-box with a listener.
  - If bytes appear only in rounds where the thread count grew, they are the thread. If not, find the allocation and remove it.
- **If it is the thread:** state next to the README's throughput table that a fraction of a byte per operation can be a thread added during the run, and link the lesson.

Closed when each case is either shown to be a thread or fixed to 0 B. A fresh run from I1 that shows 0 B in a case does not close that case.

## I6: FreeBSD's start path is not compiled

### Finding

- **FreeBSD is experimental.** The README says it is tested neither locally nor in CI, and the package ships no FreeBSD binary.
- **Its start path was changed without a build.** When Linux moved to `clone`, the `forkpty` start that FreeBSD still uses lost its Linux branches. That change has not been compiled, and no CI job builds the shim's FreeBSD-only blocks.
- **Its child does not scrub descriptors.** Unlike the Linux child, the FreeBSD child does not mark inherited descriptors close-on-exec ([platform lessons](../specs/platform_support.md#lessons-learned)).

### Why it matters

- **"Experimental" still promises that it builds.** A user who builds from source on FreeBSD may get a compile error.
- **Or a start that leaks the host's descriptors into the child.**

### Decision required

- **Keep, with a compile check.**
  - A CI step compiles the shim with clang for a FreeBSD target, against the headers of FreeBSD's release base archive.
  - The child gets the descriptor scrub that the Linux child has.
  - Behavior stays untested.
- **Keep, with tests.** The suite runs in a FreeBSD guest under QEMU, as the old-kernel checks did. This costs more CI time, and the harness needs upkeep.
- **Drop.** Remove the `forkpty` start and the FreeBSD blocks from the shim, and state in the README that FreeBSD is unsupported. This makes the shim smaller.

## I7: A macOS start takes about six times as long as a Linux start

### Finding

- **CI figures.** In run 41106916265, a session through `sh -c exit 0` took 10.271 ms on macOS, against 1.569 ms on Linux.
- **Local figures.** On an Apple M2, `PtyStartupHostMemoryBenchmarks` took 6.04 ms, against 0.94 ms for the Linux start in WSL ([platform lessons](../specs/platform_support.md#lessons-learned)).
- **A macOS start runs two programs.** It starts `minipty_spawn_helper` with `posix_spawn`. The helper opens the slave, which makes it the controlling terminal, and then executes the target.
- **The helper's share is unknown.** No measurement shows how much of the start the helper's extra exec takes. macOS creates processes more slowly in general, so it may be a small share or a large one.

### Why it matters

- **Hosts that open many sessions pay it every time,** as when restoring tabs or running tests. It is the slowest start among the Unix platforms.

### Fix

- **Measure.** A C probe measures three starts on the hosted runner and on an M-series Mac, taking the minimum of many starts:
  - `posix_spawn` of `/usr/bin/true` directly.
  - The same through the helper.
  - MiniPty's full start.
- **Keep the helper if its share is small.** Record the breakdown in the platform lessons and keep the helper.
- **If the share is large, evaluate a start without the helper:** `posix_spawn` with a new session, and the slave opened by a file action. Whether Darwin applies the new session before the file actions is unverified. So is whether a `chdir` failure can still be told from an exec failure.
- **Gates for a replacement.** The replacement must keep everything the helper gives:
  - The controlling terminal and job control.
  - The failure stages.
  - The `PATH` lookup with its `sh` fallback.
  - The signal reset and the descriptor scrub.
  - Allocations per start.

## Phases

### Phase 1: Trustworthy CI (I3, I2)

This phase comes first, because I1 and I4 rely on CI runs. It works on the current code and does not wait for any merge.

**Steps:**

- I3: add the diagnostics to both tests, then repeat each on its runner with a temporary CI step until the cause shows. Fix the test or the library, then remove the temporary steps.
- I2: commit the three profiles, the probe and the QEMU harness, and add the seccomp job to Build.

**Closed when:**

- **I3:** both tests pass 20 consecutive runs on their runners, and each cause is recorded.
- **I2:** the job passes in Build. Its run of the probe under Docker's default profile fails, which proves that the probe detects a profile that refuses nothing.

**Documents:**

- [platform_support.md](../specs/platform_support.md#verification): which fallbacks CI covers, and which stay with the QEMU harness.
- [Test constraints](../specs/platform_support.md#platform-specific-test-constraints): any test whose bound changes, with the reason.

**Progress (2026-11-06):**

Build [run 42208696328](https://github.com/guitarrapc/MiniPty/actions/runs/42208696328), commit `ec88cac`, ran every job below. Every job that gates the build passed.

- **I2: done; closes when merged.**
  - **Added:** the profiles, the probe ([seccomp/](../../../scripts/seccomp/)), [run-tests-under-seccomp.sh](../../../scripts/run-tests-under-seccomp.sh), Build's `test-old-kernel-paths` job, and the QEMU harness ([old-kernel-qemu/](../../../scripts/old-kernel-qemu/)).
  - **CI:** `test-old-kernel-paths` passed with glibc and with musl, in 3 and 4 minutes. The whole suite passed under each profile (779 tests, 45 s to 87 s).
  - **The probe:** it passed under each profile. Under Docker's default profile it failed on 6 of its 7 calls, as it must, in both jobs. That profile refuses `clone3` itself.
  - **QEMU, locally:** the harness ran the suite on Linux 4.9 with glibc (779 passed). It traced Linux 5.2 with musl, and the path matched 2026-11-04's.
- **I3, bridge test: done.** The test's class passed 20 runs of 20 on windows-2022, on code with the fix for lost acknowledgements. That supports the cause recorded in [terminal.md](../specs/terminal.md). A timeout now prints how far delivery got; a run forced to time out showed the message.
- **I3, echo test, 2026-11-06: the first repeats, which seemed to point at MiniPty.** The trace of 2026-11-07, below, showed the test's own thread holding these echoes. The test failed 3 runs of 20 on macos-26-intel. Each run printed its three slowest echoes, with what else ran meanwhile:

  | Runs | Slowest echo | Flood during it | Longest gap between ticks | Reading |
  |---|---|---|---|---|
  | 17 that passed | 18.9–85.5 ms, 45–85 ms in 14 of them | +131 to +689 KiB; it never stood still for more than 18 ms | 4–11 ms | the process and the flood ran, the echo waited |
  | Run 18, failed | 199.7 ms | +902 KiB, still for 11 ms | 9.7 ms | the same, longer |
  | Run 13, failed | 265.4 ms; the next, 120.0 ms | +67 KiB, still for 177 ms; the next, +538 KiB | 9.8 ms | both sessions waited, while the test's threads ran |
  | Run 14, failed | 281.8 ms | +76 KiB, still for 118 ms | 92.7 ms | partly the host: the process stopped for 93 ms |

  - **What this rules out:** collector pauses (0 ms during every slow echo) and thread-pool starvation (its queue held at most 1 item).
  - **What it leaves:** only run 14 shows the host stopping the process. Otherwise the echo waited while the process ran and the flood flowed, often by several hundred KiB. On Linux, locally, the slowest echo took about 5 ms.
  - **The loop serves both directions of the echo.** Unix input is written by the loop, and a parked consumer's output is read by it, so either side may hold the echo. The loop has no 50 ms timer that would explain the typical figure.
  - **Next:** find which side holds it on macOS. Only CI has macOS here: a temporary hook in the library needs the user's consent, and is removed before the merge. The repeat job's macOS entry stays until then. Its windows-2022 entry is removed.
- **I3, echo test, 2026-11-07: a temporary trace, with the user's consent.**
  - **Added:** `TemporaryEchoTrace` in the library and lines marked `Temporary` in the backend, the watcher and the test. They record the echo session's consumer, the loop's waits, services, reads and arms, the test's own steps, and the test thread's CPU time. The test prints the trace around its three slowest echoes, with each echo's index. All of it is removed before the merge.
  - **Linux, locally (WSL, 8 CPUs), four runs:** the slowest echo (8.4–10.9 ms) was always echo #0. Its time was CPU on the test thread, mostly in the first TUnit assertion: JIT of the echo path, which the flood's warm-up never runs. The next slowest (about 5 ms) were the test's threads too: a continuation the loop had already signalled started 4.9 ms late, or the test thread went 5 ms without the CPU in a synchronous read. In none did the loop hold the echo.
  - **macOS, [run 42293992100](https://github.com/guitarrapc/MiniPty/actions/runs/42293992100) (4 CPUs):** 19 of 20 runs passed. Two kinds of slow echo, neither held by the loop:
    - **Echo #0, 12–22 ms (65 ms once):** first-call cost on the test thread, as on Linux: 2.4 ms of CPU time in the first write and 9.4 ms in the first TUnit assertion. In the 65 ms one the first write took 53 ms against 13.6 ms of CPU, while the loop's 1 ms waits ran 11–21 ms: the host stopped both.
    - **Echo #4998–#5014, 35–114 ms, in 7 runs, and #10007 (45 ms) in one:** the failure was one of them (114 ms). The index held while the runs made 12,000 to 28,000 echoes, so the trigger is a count, not time. It matches on-stack replacement of the test's own method: the runtime recompiles a method in the middle of its loop after 10 × 1,000 back-edges within one call. An echo takes two back-edges (the erase's echo, then the `a`), or one when both arrive in one chunk, hence #5000 and #10007. Runs whose loop went asynchronous too often to count 1,000 back-edges in one call showed no such echo. These echoes' traces were cut by the print's lead-in, so the cause is not yet shown.
  - **macOS, [run 42295152009](https://github.com/guitarrapc/MiniPty/actions/runs/42295152009):** each echo also reported the compilation time on its own thread (`JitInfo`). Runs 11–20 set `DOTNET_TC_QuickJitForLoops=0`, which compiles methods with loops fully at their first call, so nothing is replaced in the middle of a loop. 19 of 20 passed.

    | Slow echoes | Runs 1–10 (default) | Runs 11–20 (no recompilation in the loop) | What held it |
    |---|---|---|---|
    | Echo #0 | 9.6–12.0 ms, 8.2–10.8 ms of it compiling | 9.0–13.0 ms, 8.1–12.3 ms compiling | first calls, on the test thread |
    | Echo #4998, #6498, #10001 | 23–35 ms in 5 runs, all but 0.3 ms or less of it compiling | none | recompilation of the test's method in its loop: confirmed |
    | Others over 20 ms | 125.7 ms (the failure), 95.1 ms, 23.7 ms; no compiling | none; the slowest after #0 took 10.2 ms | the test thread went without the CPU |

    - **The failure:** the test thread spent 125.6 ms in `WriteInputAsync("a")` with 0.1 ms of CPU time. In the 95.1 ms echo the consumer read the echo inline at 0.16 ms, then the thread went 78 ms without the CPU inside the synchronous read and 17 ms more in the assertion. In the 23.7 ms echo it was the assertion. Meanwhile the loop ran its passes and the clock thread ticked.
    - **Why that thread:** every echo of this loop completes synchronously on macOS, so the test thread never waits. It is the one thread of the process that never sleeps, beside `yes` and the flood's reader, on 4 vCPUs, and macOS left it without the CPU for up to 126 ms. Ten runs without such a stall in runs 11–20 do not show that the setting prevents it.
    - **Neither MiniPty's loop nor its input held any slow echo** in either round.
  - **Fix, in the test (2026-11-07):** 20 echoes before the clock starts, and a pause of a millisecond (`Task.Delay(1)`) before each keystroke. The test thread then waits between echoes, as a terminal waits for its user. No call of the method runs 1,000 back-edges, so it is not recompiled mid-loop. By each keystroke the loop has taken reading back from the idle consumer, so the echo goes through the loop, which is what the test means to measure. Each echo also reports the compilation time on its thread.
  - **Linux, locally:** four runs, three default and one with `DOTNET_TC_QuickJitForLoops=0`, about 125 echoes each (the timer fires every 4 ms there). The slowest echo took 0.2–0.4 ms, against 8–11 ms before. The trace showed the consumer finding nothing and the loop reading the echo.
  - **macOS, [run 42298028987](https://github.com/guitarrapc/MiniPty/actions/runs/42298028987), commit `c405b97`: 20 runs of 20 passed.** 98–312 echoes per run, median 0.03–0.06 ms. The slowest echo per run took 0.1–6.9 ms, against 23–126 ms before. None compiled on its thread. In the 5–7 ms ones the process stopped about as long: the clock's ticks stopped for 4.8–7.6 ms, or the trace showed the thread without the CPU while reading. In the printed traces the loop read the echo first 40 times and the consumer 20 times.
  - **Removed (2026-11-07):** `TemporaryEchoTrace`, every line marked `Temporary`, and the repeat job. The library is again as at `ec88cac`. What stays in the test: the warm-up, the paced keystrokes, and each echo's compilation time on its thread. Paced, the test passed 20 runs of 20 on macos-26-intel, so the echo half of I3 closes when merged.

### Phase 2: Current figures (I1, I5, I7)

This phase measures `main` once the Windows Server 2022 changes have landed there. Its three parts are independent of each other.

**Steps:**

- I1: extend the README updater with the start in a large host, then make three full benchmark runs.
- I5: run the allocation harness on the three Windows cases.
- I7: run the start probe on macOS.

**Closed when:**

- **I1:** the README shows a full run of `main`, with the start-in-a-large-host table. Every gate holds, or each gap is bisected and fixed, or recorded under [next_todo.md](../next_todo.md#open).
- **I5:** each case is shown to be a thread or fixed to 0 B.
- **I7:** the breakdown is recorded. If the helper is replaced, every gate holds.

**Documents:**

- **README:** the tables, the new table, and the note on fractions of a byte if I5 confirms the thread.
- **Platform lessons:** the I5 and I7 results.
- **[core_session.md](../specs/core_session.md#metrics):** the metrics benchmark's figures, once explained.

**Progress (2026-11-07):**

- **I1, three full runs.** Against run 41106916265 (`5ca042f`):
  - **R1:** [run 42176016063](https://github.com/guitarrapc/MiniPty/actions/runs/42176016063), a run of `main` at `130020d`.
  - **R2 and R3:** [run 42334732063](https://github.com/guitarrapc/MiniPty/actions/runs/42334732063) and [run 42335813387](https://github.com/guitarrapc/MiniPty/actions/runs/42335813387), on `improve`. Its library (`src/MiniPty`, `src/MiniPty.Capture`) is `main`'s, and only the benchmarks' probes differ.
  - **The README now shows R3** (commit `2bf751a` on `improve`).
  - **Reading the ratio gates:** a ratio that moved in MiniPty's favor counts as holding.

  | Gate | R1 | R2 | R3 | Holds |
  |---|---|---|---|---|
  | Runner CPUs (Linux; Windows) | AMD EPYC 9V45; Intel Xeon 8573C | Intel Xeon 6973P; AMD EPYC 7763, as the baseline's | AMD EPYC 7763; AMD EPYC 9V45 | |
  | Allocations | keystrokes 0 B; Unix floods 0 B; Windows floods 8 B out-of-band, 12 B in-box | the same, with 12 B and 0 B | the same, with 12 B and 12 B | yes: the Windows bytes are one-time costs (I5); the baseline had 12 B and 0 B |
  | Linux keystroke P50 ratio, 0.870 | 1.118 | 0.781 | 0.902 | yes, R2 and R3 |
  | Linux flood ratio, 1.113 | 0.970 | 1.153 | 1.125 | yes, R2 and R3 |
  | Windows out-of-band keystroke ratio, 0.978 | 0.943 | 0.972 | 1.005 | yes, all three |
  | Windows out-of-band flood ratio, 0.905 | 0.857 | 0.895 | 0.922 | yes, all three |
  | Windows in-box flood ratio, 1.000 | 0.974 | 1.000 | 0.969 | yes, all three |
  | Windows in-box keystroke ratio, 0.745 | 0.989 | 0.931 | 1.085 | no run on CI; +2–3% in a local A/B, below |
  | macOS flood, 38.9–64.4 MiB/s | 46.3 | 60.8 | 58.7 | yes, all three |
  | Start, Linux 1.569 ms | 0.616 | 0.522 | 0.858 | yes, faster |
  | Start, macOS 10.271 ms | 10.077 | 9.762 | 5.606 | yes |
  | Start, Windows 16.87 ms | 22.68 | 17.29 | 11.88 | yes, R2 and R3 |
  | Allocated per start, 1.26–1.27 KB on Unix, 1.23 KB on Windows | 1.27; Windows 1.26 | 1.27; 1.26 | 1.27; 1.25 | yes, +2.4% at most |
  | Start in a large host, 2 GiB against none | Linux 0.0%, macOS +7.6%, Windows +1.7% | +2.8%, −14.8%, −1.0% | 0.0%, +0.9%, −3.4% | yes: no start was more than 10% slower |

  - **The Windows start in R1 is the runner's CPU, not the code.** R1 moved from an AMD EPYC 7763 to an Intel Xeon 8573C. R2, on the baseline's CPU, took 17.29 ms (+2.5%). Locally, three alternating rounds of `Session_Exit0_WaitForExit` took 39.15–39.77 ms at `5ca042f` and 37.86–39.98 ms on the current code.
  - **The in-box keystroke ratio swings with the runner, but a small cost is real.**
    - **CI:** the in-box ConPTY's round trip follows its frame schedule, and both libraries' P50 ranged 10.6–14.4 ms over the four runs, so the ratio moved by 25–46%.
    - **Locally:** on Windows 11 (build 26200) the round trip held within 0.1 ms per version. MiniPty's P50 rose from 7.21–7.48 ms at `5ca042f` to 7.42–7.66 ms. Porta.Pty's stayed at 7.43–7.70 ms, and the out-of-band round trip did not move.
    - **Bisect:** the rise begins at `d2f772a`. That commit, with the old exit watcher swapped back in, measured as `5ca042f` did. So the cost comes from observing exits with `SetThreadpoolWait`. Why a wait on the child's process handle delays the in-box host is not known.
    - **Recorded:** it is under [next_todo.md](../next_todo.md#open).
  - **The large-host table:** the updater writes a `startup-host-memory` block from `PtyStartupHostMemoryBenchmarks`, and the README has it.
  - **Done once `improve` is merged:** the gates hold, apart from the in-box keystroke. That one holds in the local A/B, and its cost is located and recorded.
- **I5, the allocation probe (`--alloc-probe` in the benchmarks).** It runs the benchmarks' 1 MiB flood round after round and records each round's bytes on every thread and on the thread that waits, the pool's threads and new OS threads. Locally on Windows 11 (build 26200), 200 rounds per case:
  - **A thread added during the run allocated 1,072 B,** in the round the pool went from 3 to 4 threads. `dotnet-trace` showed `PortableThreadPool.CreateWorkerThread` and the new worker's thread locals.
  - **Not threads, each once per process:**
    - 56 B in the first `MoveNextAsync` after a pause: `TaskScheduler`'s static constructor, through `PtyConsumerYield.HasSchedulingContext`.
    - 48 B inside `WriteInputAsync` around the 64th write: gone with `DOTNET_TieredPGO=0` and after a long warm-up, and never an allocation event in the trace, so the runtime's tiered compilation.
    - 6192 B on another thread with no thread added, in about one run in four, on Windows and on Linux: never an allocation event in the trace either. Over 8 Linux runs per setting it appeared by default and with `DOTNET_TieredPGO=0`, and never with `DOTNET_TieredCompilation=0`, so tiered compilation's own thread.
  - **After a warm-up of 150 rounds, the flood allocated 0 B in 150 rounds** on both ConPTYs, and with a listener after the one-off 56 B.
  - **Done:** each case is a thread or a one-time cost, and no flood allocates. The README says so beside the throughput table, the [platform lessons](../specs/platform_support.md#lessons-learned) have the detail, and [core_session.md](../specs/core_session.md#metrics) explains the metrics figures.
- **I7, the start probe.** `scripts/macos-start-probe/start-probe.c` times five starts, alternating round by round:
  - `posix_spawn` alone.
  - With MiniPty's terminal and attributes.
  - The same with the slave opened by name in a file action, which is the start without the helper.
  - Through `minipty_spawn_helper`.
  - Through the helper given the bare name.

  It also asks each child whether it has a controlling terminal. The benchmarks' `--start-probe` times MiniPty's whole start on the same runner. A temporary job in Build ran both on macos-26 and macos-26-intel, three times. The last two runs, [run 42335490843](https://github.com/guitarrapc/MiniPty/actions/runs/42335490843) and [run 42336144488](https://github.com/guitarrapc/MiniPty/actions/runs/42336144488), had all five starts. Below, the minimum of 300 starts, in ms, over four passes:

  | Start | macos-26 (M1, virtual), `/usr/bin/true` | macos-26, `/bin/sh -c 'exit 0'` | macos-26-intel (i7-8700B), `/usr/bin/true` | macos-26-intel, `/bin/sh -c 'exit 0'` |
  |---|---|---|---|---|
  | `posix_spawn` alone | 1.03–1.26 | 2.42–2.52 | 2.33–2.65 | 5.37–5.78 |
  | With the terminal, inheriting the slave | 1.22–1.52 | 3.44–3.53 | 2.94–3.38 | 7.67–7.98 |
  | With the terminal, opening the slave by name | 1.25–1.58 | 3.46–3.58 | 3.05–3.46 | 7.65–8.17 |
  | Through the helper | 3.23–4.07 | 4.65–4.83 | 7.83–8.99 | 10.63–11.60 |
  | Through the helper, by bare name | 3.59–4.48 | 5.10–5.38 | 8.62–9.96 | 11.81–12.41 |
  | MiniPty's whole start | 3.40–3.86 | 4.84–6.00 | 9.14–11.47 | 11.40–11.98 |
  | MiniPty's whole start of `sh -c 'exit 0'` | | 5.25–6.55 | | 12.44–13.61 (one pass 24.10) |

  - **The helper costs 1.2–2.6 ms on the M1 and 3.0–5.6 ms on the Intel runner.** This is the helper's exec plus taking the controlling terminal. On the M1 it is three fifths of a start of `/usr/bin/true`, and a fifth of the benchmark's start.
  - **The rest is small.**
    - Searching `PATH` for `sh` costs 0.3–0.55 ms on the M1 (22 entries) and 0.8–1.2 ms on the Intel runner (19 entries).
    - MiniPty's own work is about 0.2 ms on the M1.
  - **Only the helper gives a controlling terminal.** The start that opens the slave by name in a file action cost within 0.2 ms of inheriting it. Its child led its session, but it had no controlling terminal. The helper's child had one, with its group in the foreground. The probe itself had none. In the second run, a shell test (`exec 3</dev/tty`) had reported a terminal for every start, so the third asked the child itself.
  - **Done: the helper stays.** The only start without it that `posix_spawn` allows fails the first gate, the controlling terminal and job control. The breakdown is in the [platform lessons](../specs/platform_support.md#lessons-learned). The temporary job is removed. The probe stays in `scripts/` for a rerun. A start without the helper would need code in the child before its exec, as the Linux `clone` start has. That is a new design, outside this plan.

### Phase 3: Windows before 24H2 (I4)

This phase is independent of the others. It needs local VMs.

**Steps:**

- Add windows-2022 to the `nuget-smoke` matrix.
- Run the startup benchmark once on windows-2022.
- Run the suite and the probe on the three VMs.
- Update the README.

The README part can land first, worded as measured on Server 2022.

**Closed when:**

- The smoke test passes on windows-2022.
- Each VM build has a recorded result.
- The README states the drawing differences and the advice on disposal.

**Documents:**

- [platform_support.md](../specs/platform_support.md#verification): a row per measured build.
- [next_todo.md](../next_todo.md#unverified): the bullet on the in-box ConPTY before 24H2 is replaced by what is still unmeasured.
- The README.

### Phase 4: FreeBSD (I6)

This phase is independent of the others and starts after the decision.

**Closed when:** the decision is recorded in [platform_support.md](../specs/platform_support.md) and in the README, and its option is implemented: a compile check that passes, a test run that passes, or the code removed.

## Not in This Plan

- **Flood gaps against a reader that blocks a thread per session.** Two are recorded under [next_todo.md](../next_todo.md#open), with their reasons: on macOS's hosted runner, and on Windows with two CPUs. No design closes them without a thread per session. I1's runs add data points, not a change.
- **Linux bursts of 32 KiB or more cost one 200 μs wait.** This is recorded and bounded in [next_todo.md](../next_todo.md#open).
- **Items deferred until an embedder asks:** the hang-up grace as an option, guidance for an out-of-process host, and a session's tty path ([next_todo.md](../next_todo.md)).
- **The rest of the Unverified list:**
  - Host exit on macOS under NativeAOT with the current watcher.
  - An echo during a flood through a bridge.
  - The ring's capacity on macOS.

  They stay in [next_todo.md](../next_todo.md#unverified). None is known to affect a user.
- **Rare test timeouts under heavy load.** They need local CPU oversubscription to reproduce, not hosted runners, and they stay in [next_todo.md](../next_todo.md).
