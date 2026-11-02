# Improvement Plan

Goal: MiniPty loads wherever .NET 10 runs on its platforms. A session's start costs the same whatever the host's size. The library reports accurately what it supports and how a child ended. The items below came from a review of the library as a whole. Each is closed by a fix, or by a decision recorded in the specifications, as its section says.

**Status:** open (2026-11-02). Phase 1 is implemented. On x64 Linux it is verified locally. arm64 Linux and macOS are checked only by CI, which has not run yet. The measurements below were taken on 2026-11-02.

## Inventory

| ID | Kind | Problem | Closed when |
|---|---|---|---|
| I1 | Distribution | The Linux shim needs glibc 2.38, and no musl build ships | The shim loads on glibc 2.27 and musl 1.2.3, and CI fails a build that raises the floor |
| I2 | Distribution | The macOS binaries declare macOS 26.0 as their minimum | They declare macOS 12.0, and CI checks it |
| I3 | Performance, correctness | A Linux start forks the host, so its cost grows with the host's memory, and the terminal's descriptors can leak to processes started at the same time | Linux starts the child without copying the host, with both ends of the terminal opened close-on-exec |
| I4 | API | `Pty.IsSupported` returns `true` on Windows builds without ConPTY | It returns `false` there, and `Pty.Start` throws `PlatformNotSupportedException` |
| I5 | API | `PtyResult` carries the exit code only | It carries the exit status, signal included |
| I6 | Measurement | The published macOS flood figure contradicts the recorded runs, and the output ring's size was never tuned | Both are measured over several runs and the results recorded |
| I7 | Observability | Hosts cannot see backpressure, consumer waits, or start times | Instruments are added, or the decision not to add them is recorded |
| I8 | Security | Every child inherits the host's whole environment | An opt-in minimal environment exists, or the deferral stands with its reason |
| I9 | Scale | Windows adds an exit-watcher thread per 63 sessions | Exit is observed by a fixed number of threads, or the deferral stands |

- **I1–I5 are fixes.** I1 and I2 block users today: the shipped package fails to load on platforms .NET 10 supports.
- **I6 is measurement.**
- **I7–I9 each need a decision before work starts.** I8 and I9 are deferred in [next_todo.md](../next_todo.md).

## I1: The Linux binaries need glibc 2.38

### Finding

`objdump -T` on the 1.4.0 package's `libminipty_unix.so` gives the same list for x64 and arm64. These symbol versions are above glibc 2.28:

| Symbol | Version | Source |
|---|---|---|
| `__isoc23_sscanf`, `__isoc23_strtol` | `GLIBC_2.38` | `sscanf` and `strtol` in the `/proc` parsing of `minipty_get_active_process_name` ([minipty_unix.c:912](../../../src/MiniPty/Native/minipty_unix.c)). glibc 2.38's headers redirect both to their C23 variants when `_GNU_SOURCE` is defined, and the file defines it for `pipe2`. |
| `forkpty` | `GLIBC_2.34` | glibc 2.34 moved `forkpty` from libutil into libc under a new version. |
| `pthread_sigmask` | `GLIBC_2.32` | glibc 2.32 moved it from libpthread into libc. |

Consequences:

- **Older glibc cannot load it.** `ld.so --list` on that library in a Debian 12 container (glibc 2.36) fails with ``version `GLIBC_2.38' not found``. Every glibc before 2.38 fails the same way, including distributions .NET 10 supports: Ubuntu 22.04 (2.35), RHEL 9 (2.34) and RHEL 8 (2.28).
- **The static archive has the same problem.** NativeAOT links `libminipty_unix.a`, which references `__isoc23_sscanf` and `__isoc23_strtol` as undefined symbols. A NativeAOT build on a host with older glibc therefore cannot resolve them. This is inferred from `nm`; no link was attempted.
- **Alpine is not covered.** No `linux-musl-*` binary ships. The package's `linux-x64` library is a glibc binary, and musl's loader cannot load it.

Nothing catches this:

- [build-native.sh:40](../../../scripts/build-native.sh) compiles with the runner's own glibc.
- Every Linux build, test and release job runs on `ubuntu-24.04` or `ubuntu-24.04-arm` (glibc 2.39).
- No job checks the symbol versions of what it built.
- [platform_support.md](../specs/platform_support.md) states the Linux minimum as "Common glibc/musl targets", and the README states none.

### Why it matters

- **MiniPty narrows .NET 10's reach without saying so.** .NET 10 runs on glibc 2.27 (x64 and arm64) and on musl 1.2.3, according to its supported-OS list. The failure appears at the first `Pty.Start` in production, not at install or build time.
- **The affected systems are common.** Long-lived servers run on older-glibc distributions, and Alpine is a common base image for .NET containers.

### Fix

- **Floor:** the runtime's own, glibc 2.27 on x64 and arm64 and musl 1.2.3.
- **Build:** compile the Linux library and archive against glibc 2.27, so that every symbol binds to its oldest version. Changing the source is not enough, because the build host's headers also pick the versions of `forkpty` and `pthread_sigmask`.
- **musl:** ship `linux-musl-x64` and `linux-musl-arm64`, built on Alpine.
- **Check:** each Linux build fails if the library requires a glibc version above the floor (`readelf -V`), or if the archive references a symbol that exists only in newer glibc (`__isoc23_*`).

### Constraints

- **The 2.27 build cannot be a job container.** The Actions runner's JavaScript actions (checkout, artifact upload) need glibc 2.28 or later inside a job container. The build therefore runs with `docker run`, from a step on the runner.
- **musl has no symbol versions.** A binary built on a newer musl loads on an older one unless it calls a function added since. The build runs on the oldest Alpine that CI can run, and the smoke test there is what proves it.
- **The musl build is unproven.** The code is expected to compile for musl: `close_range` and `pidfd_open` go through `syscall`, and musl's libc has `forkpty`. The first musl build is the check.

## I2: The macOS binaries declare macOS 26.0

### Finding

- **All four macOS files declare macOS 26.0.** The `LC_BUILD_VERSION` load command of each file in 1.4.0 says `minos 26.0`, built with SDK 26.5. The files are `libminipty_unix.dylib` and `minipty_spawn_helper`, each for arm64 and x64.
- **The minimum comes from whichever runner builds them.** [build-native.sh:44](../../../scripts/build-native.sh) passes no `-mmacosx-version-min`, and no workflow sets `MACOSX_DEPLOYMENT_TARGET`. The release builds on `macos-26` and `macos-26-intel`.
- **The static archive is built the same way.** Linking a NativeAOT application against it warns about the newer minimum ([next_todo.md](../next_todo.md#unverified)).

### Why it matters

- **macOS 15 is supported by .NET 10 but untested here.** Whether macOS 15 refuses binaries that declare 26.0 has not been tested. Every session's start goes through the helper, which is an executable.
- **Newer APIs can slip in unnoticed.** Even where the binaries load, a deployment target of 26.0 lets the shim call an API that macOS 15 lacks, with no warning from the compiler. A later change could break macOS 15 without anyone noticing.

### Fix

- **Deployment target:** macOS 12.0 for the library, the archive and the helper. Availability warnings are errors, so a call to a newer API fails the build.
  - **Why 12.0 and not 15:** NativeAOT links macOS applications for 12.0 (`AppleMinOSVersion` in the ILCompiler package's targets). An archive that declares a newer minimum makes the link warn.
  - **Cost:** none. The shim calls nothing newer than macOS 10.15, and 12.0 also covers every macOS .NET 10 supports.
- **Check:** the build fails if a binary declares a newer minimum (`vtool -show-build` or `otool -l`).
- **Smoke test:** the package's smoke test also runs on the oldest hosted macOS runner, macOS 15.

## I3: A Linux start copies the host

### Finding

On Linux the child is started with `forkpty` ([minipty_unix.c:336](../../../src/MiniPty/Native/minipty_unix.c)), which is a full `fork`. Before the child can `exec`, the kernel copies the host's page tables and marks every resident page copy-on-write. `Pty.Start` is synchronous: it waits for the fork, then for the child's start report.

A C probe measured the cost: a Ryzen 9 7950X3D, WSL2 with kernel 6.6, and the minimum of 20 starts of `/bin/true` after touching N MiB:

| Host resident memory | `forkpty` | `clone(CLONE_VM \| CLONE_VFORK)`, via glibc's `posix_spawn` |
|---|---|---|
| 0 | 0.132 ms | 0.123 ms |
| 512 MiB | 3.377 ms | 0.136 ms |
| 2 GiB | 12.979 ms | 0.164 ms |
| 4 GiB | 25.733 ms | 0.161 ms |

- **The cost is about 6 ms per GiB.** The README's start benchmark (1.749 ms) runs in a small benchmark process, so it does not show this.
- **The terminal's descriptors can leak.** `forkpty` opens both ends of the terminal without `O_CLOEXEC`, and there is no way to ask it for that flag ([minipty_unix.c:375](../../../src/MiniPty/Native/minipty_unix.c)). Another thread of the host may fork in that moment, `Process.Start` included. That process inherits both ends, and they stay open across its `exec`. Either copy lasts as long as that unrelated process:
  - A copy of the slave keeps the terminal open, so output ends only through the post-exit drain.
  - A copy of the master keeps the tty from hanging up when the session closes it.

  This is invariant 9 in [lifecycle.md](../specs/lifecycle.md) and the fork child hygiene paragraph in [pty_crossplatform.md](../references/pty_crossplatform.md#linux--freebsd-forkpty--execve). The macOS path opens both ends close-on-exec and has no such window.

### Why it matters

- **Large hosts pay the most.** IDE backends and web terminals with a big heap are the hosts that start many sessions. At 4 GiB, each start blocks the caller for 26 ms. If the host starts sessions from request handlers, a thread-pool thread is blocked that long.
- **.NET already avoids this.** Its own `Process.Start` does not use `fork` on Linux, for the same reason.

### Fix

The Linux child is started with `clone(CLONE_VM | CLONE_VFORK)` on a stack of its own, which is how glibc's `posix_spawn` starts children.

- **The parent opens the terminal.**
  - The master: `posix_openpt(O_RDWR | O_NOCTTY | O_CLOEXEC)`, then `grantpt`, `unlockpt` and `ptsname_r`.
  - The slave: opened with `O_RDWR | O_NOCTTY | O_CLOEXEC`.
  - The termios and the window size are set on the slave in the parent, as on macOS.
- **The child keeps today's steps, in this order:**
  1. `setsid`.
  2. `TIOCSCTTY` on the slave.
  3. `dup2` of the slave onto stdio, which clears close-on-exec on the copies.
  4. Descriptors from 3 up marked close-on-exec.
  5. Signals reset.
  6. `chdir`.
  7. The existing `PATH` lookup and `exec`.

  On a failure the child records the stage and `errno` in memory the parent owns, then exits.
- **The report pipe goes away on Linux.** The parent resumes only once the child has exec'd or exited, so the result is already there when it resumes. The start report pipe and its 10-second bound are no longer needed.
- **`forkpty` and `-lutil` leave the Linux build.** That removes the `GLIBC_2.34` symbol at its source.

Not chosen:

- **`posix_spawn`.**
  - It cannot run the child's own `PATH` lookup with its `sh` fallback for `ENOEXEC`.
  - It cannot tell a `chdir` failure from an `exec` failure.
  - Its `chdir` action (`posix_spawn_file_actions_addchdir_np`) needs glibc 2.29, above the floor.
- **A launcher executable, as on macOS.** It costs one more `exec` per start and one more file to ship.

### Constraints

- **The child may only make system calls until `exec`.** It runs in the host's memory, so it may not allocate, take a lock or use stdio. Everything it reads is prepared before the clone, as today: the parent builds the environment block, and the `PATH` lookup uses a stack buffer.
- **Only the calling thread is suspended.** The host's other threads keep running in the memory the child uses, so the child touches only the data of its own start.
- **Signals stay blocked until the child has reset them.** They are blocked from before the clone until the child has reset every disposition to its default. Without `CLONE_SIGHAND` the child has its own handler table, so no runtime handler runs in the child.
- **The parent never reads the result from `errno`.** The child's `errno` lands in the calling thread's thread-local storage. That thread is suspended, so this is harmless, but the parent reads the stage and error from the shared record instead.
- **The pidfd arrives with the child.** `CLONE_PIDFD` (Linux 5.2) returns the pidfd together with the child. That removes the instant in which a `pidfd_open` after the fork could name a pid that was reused. Kernels that reject the flag fall back to `pidfd_open`.
- **`clone`, not `clone3`.** Some container seccomp profiles refuse `clone3`.
- **Behavior does not change.**
  - The child is a session leader.
  - The terminal is its controlling terminal, and the child is its foreground process group.
  - The termios includes `IUTF8`.
  - Inherited descriptors are scrubbed and signals are at their defaults.
  - `Pty.Start` still throws for `chdir` and `exec` failures.
- **FreeBSD keeps `forkpty`.** It is untested, and nothing here changes that.

## I4: `Pty.IsSupported` on Windows without ConPTY

### Finding

- **.NET 10 runs on Windows builds that lack ConPTY.** ConPTY exists from Windows 10 1809 (build 17763). .NET 10 also runs on Windows 10 1607 and on Windows Server 2012, 2012 R2 and 2016, which have no ConPTY.
- **`Pty.IsSupported` does not check the build.** It looks at the OS family only ([Pty.cs:38](../../../src/MiniPty/Pty.cs)), although its documentation says "Windows 10 1809+". On those builds it returns `true`.
- **`Pty.Start` then fails with the wrong exception.** It fails at the call to the missing `CreatePseudoConsole` export, instead of throwing the `PlatformNotSupportedException` it documents.

### Fix

- **`IsSupported`** returns `false` on Windows before build 17763.
- **`Pty.Start`** throws `PlatformNotSupportedException` on those builds. This includes starts with an out-of-band ConPTY (`PtyStartInfo.ConPty`), because whether one runs before 1809 is unverified.

## I5: `PtyResult` carries only the exit code

### Finding

- **`CompleteAsync` returns `PtyResult`.** For a Unix child ended by a signal, its `ExitCode` is `128 + signal` ([PtyResult.cs](../../../src/MiniPty/PtyResult.cs)).
- **`WaitForExitAsync` returns `PtyExitStatus`,** which includes the signal.
- **A `CompleteAsync` caller cannot tell the cases apart.** A child killed with `kill -TERM` and a child that ran `exit 143` give the same `ExitCode`. The caller has to read `PtySession.ExitStatus` afterwards.

### Fix

Add `PtyResult.ExitStatus` (`PtyExitStatus`). The change is additive, and `ExitCode` stays.

## I6: Flood measurements

### Finding

- **The README's macOS flood figure does not match the recorded runs.**
  - **The README's run:** [run 40951368543](https://github.com/guitarrapc/MiniPty/actions/runs/40951368543), made on 2026-10-29 after the macOS reader was rebuilt. It puts the macOS 1 MiB flood at 56.0 MiB/s, 0.73 of the same run's baseline. The baseline is a reader that blocks one thread per session, at 77.2 MiB/s.
  - **The recorded runs:** the nine CI runs recorded when that reader was built put it at 1.11–1.53 times the baseline, with a median of 1.35 ([platform_support.md](../specs/platform_support.md#lessons-learned)).
  - **Keystrokes:** in the same README run, the macOS keystroke P50 is also behind (50.75 against 43.83 μs), while its P95 is ahead.
  - **Possible causes:** the Linux work changed shared Unix output code after those runs. Either a change regressed macOS, or the README shows a slow run. The same lessons say a single macOS run cannot decide which.
- **The ring's size was never tuned.** The 16 KiB ring and 4 KiB reads were never tuned for throughput ([next_todo.md](../next_todo.md#unverified)).
  - **The kernels bound a read.** Linux hands out at most 4,095 bytes per read, and a macOS master holds 1,024 bytes.
  - **A larger read did nothing.** An 8 KiB read into a 24 KiB ring measured no change on Linux during the flood work (`plan_flood_throughput.md`, in the tree of commit `f48fe64`).
  - **What is left:** the ring's capacity is the only size still to measure.

### Fix

- **Re-measure macOS.** Run the full benchmark several times on current `main` and on commit `817a001`, which rebuilt the macOS reader. Judge each run by the ratio within it and by the per-iteration times, as the lessons prescribe. If `main` is behind, bisect.
- **Sweep the ring's capacity.** Measure 16, 32 and 64 KiB on all three operating systems, on the `AllCpus` and `TwoCpus` jobs.
  - **Gates:** floods, keystrokes, process CPU, 0 B allocated, and the memory a host spends per session.
  - **Outcome:** the size changes only if every gate holds. Either way, the result is recorded in the platform lessons.

## I7: Hosts cannot observe the output path

### Finding

The output path runs on thresholds that were chosen by measurement. Each is named in the backends:

| Threshold | Value | Name |
|---|---|---|
| A Linux consumer's wait on the master, once the child has streamed 32 KiB | 200 μs | `LinuxConsumerWaitMicros` |
| A macOS consumer's wait on the master | 1 ms | `MacOsConsumerWaitMs` |
| How long a reader that reads inline may be idle before the loop takes reading back | 1 ms | `InlineReaderCheckMs` |
| How long output must stay quiet after exit before it ends | 100 ms | `DefaultPostExitQuietMs` |
| How long a UI consumer runs before it yields | 8 ms | `PtyConsumerYield.BudgetMs` |
| How long coalescing waits at most | 1 ms | `PtyOutputCoalescing.MaxDelayMs` |

- **Hosts pay these costs.** A thread-pool thread can be held in a native wait, and a child can be stalled behind a full ring.
- **Hosts cannot see them.** They show up only as latency, and only a profiler shows the cause.
- **Instruments are deferred.** [next_todo.md](../next_todo.md#metrics) leaves them to a separate decision.

### Decision required

Add a `System.Diagnostics.Metrics` meter, or record why not.

Candidate instruments:

- Active sessions.
- Output bytes.
- Stalls on a full ring.
- Consumer waits in native code, and their time (Unix).
- Start duration and start failures.

Constraints:

- **Cheap when unused.** Nothing is allocated, and a branch is the only cost, when nothing listens: each instrument checks `Enabled` first.
- **No per-session tags.** No tag carries a value that differs per session.
- **AOT-safe.** `System.Diagnostics.Metrics` uses no reflection.
- **Allocation-free in the benchmarks.** Their allocation columns stay at 0 B, and the cost with a listener attached is measured.

## I8: Every child inherits the host's environment

### Finding

- **`PtyStartInfo.Environment` only overlays the host's environment.** MiniPty removes a fixed list of terminal-specific variables and nothing else ([PtyEnvironment.cs](../../../src/MiniPty/Internal/PtyEnvironment.cs)).
- **Host secrets reach every shell.** A web host's connection strings and cloud credentials reach every child unless the caller removes each key. Doing that requires knowing every key.

### Decision required

The proposal is an opt-in that starts from an empty environment plus a minimum per platform, with `Environment` applied on top.

Constraints:

- **Windows needs a minimum to start a child.** Windows children fail to start without variables such as `SystemRoot` ([next_todo.md](../next_todo.md)). The minimum for each OS is found by measurement, not guessed.
- **`TERM` stays.** It comes from `TerminalName`, as today.
- **Unix already copes without `PATH`.** The lookup falls back to `/bin:/usr/bin` when `PATH` is missing.

## I9: A Windows exit-watcher thread per 63 sessions

### Finding

[WindowsExitWatcher.cs](../../../src/MiniPty/Internal/WindowsExitWatcher.cs) waits on up to 63 process handles per thread with `WaitForMultipleObjects`. A host with 630 sessions therefore runs 10 watcher threads, beside its one I/O thread.

### Decision required

This item is deferred until an embedder needs it ([next_todo.md](../next_todo.md)). The candidates:

- **Wait completion packets** (`NtCreateWaitCompletionPacket` and `NtAssociateWaitCompletionPacket`, Windows 8 and later). Each one posts a process handle's signal to the I/O loop's completion port. That is one packet per session and no thread.
- **Job completion messages** (`JOBOBJECT_ASSOCIATE_COMPLETION_PORT`). The documentation does not guarantee they are delivered, so they cannot be the only signal of an exit.

## Phases

### Phase 1: Native reach (I1, I2)

This phase comes first: until it lands, the shipped package fails to load on platforms .NET 10 supports.

**Linux:**

- Build the x64 library and archive on `ubuntu-24.04`, and arm64 on `ubuntu-24.04-arm`, inside a glibc 2.27 container started with `docker run`. Do this in [build.yaml](../../workflows/build.yaml) and [release.yaml](../../workflows/release.yaml) alike.
- Add the floor check after each Linux build.
- Build `linux-musl-x64` and `linux-musl-arm64` on Alpine, and pack them under `runtimes/linux-musl-*/native/`.

**macOS:**

- Set the deployment target to 12.0, with availability warnings as errors.
- Check the declared minimum after each build.

**Smoke tests:**

- Run the NuGet smoke sample in containers: an RHEL 8-compatible image (glibc 2.28, the oldest glibc .NET 10 supports) and Alpine, on x64 and arm64.
- Run it on the oldest hosted macOS runner.
- Run the test suite once on Alpine x64, since musl is a new platform.

**Closed when:**

- `readelf -V` on both Linux libraries shows no `GLIBC_` version above 2.27, and neither archive references `__isoc23_*`.
- The smoke sample passes on every image and runner listed above.
- The test suite passes on Alpine x64.
- All macOS files (the library, the archive and the helper, for both architectures) declare `minos 12.0`.

**Documents:**

- [platform_support.md](../specs/platform_support.md) and the README state the minimums: glibc 2.27, musl 1.2.3, and macOS 12.0.
- The README's native files table lists the musl RIDs.
- [next_todo.md](../next_todo.md#unverified): the bullet on the minimum macOS version is removed.

**Built (2026-11-02).** `scripts/build-native-package.sh` builds each shipped file and checks it.

- **Linux:** the build runs in a pinned image on a runner of the file's own architecture, with no bind mount: tar carries the sources in and the files out.
  - glibc: Ubuntu 18.04.
  - musl: Alpine 3.17.
- **macOS:** the build runs on the runner, with `-mmacosx-version-min=12.0 -Werror=unguarded-availability`.

`build.yaml` and `release.yaml` build all six RIDs through it, and `pack-with-native.sh` requires the musl files. `buildTransitive/MiniPty.targets` links the musl archive under NativeAOT.

The NuGet smoke runs in more places, each on x64 and arm64:

- macOS 15.
- AlmaLinux 8 containers.
- Alpine 3.21 containers (`scripts/run-nuget-smoke-in-container.sh`).

A new job runs the test suite in an Alpine SDK container on x64.

Results on x64, locally, through Docker Desktop:

| Check | Result |
|---|---|
| glibc library | Highest version required: `GLIBC_2.17`. `forkpty` binds to `GLIBC_2.2.5` through `libutil.so.1`, `pthread_sigmask` through `libpthread.so.0`, and `sscanf` and `strtol` to their plain names. |
| musl library | Links `libc.musl-x86_64.so.1`. |
| `--check` on 1.4.0's glibc library | Fails: `GLIBC_2.38` is above the floor. |
| `--check` on a glibc library placed as musl | Fails: it does not link musl's libc. |
| NuGet smoke, AlmaLinux 8 (glibc 2.28) | Passes under `dotnet run` and as a NativeAOT executable. |
| NuGet smoke, Alpine 3.21 | Passes under `dotnet run` and as a NativeAOT executable. |
| Same smoke with the published 1.4.0, AlmaLinux 8 | Fails with `DllNotFoundException`, whose message names ``version `GLIBC_2.34' not found``. |
| Test suite, Alpine 3.24 (SDK 10.0.403) | 732 of 733 tests pass. The other, `BurstsReachAParkedConsumerInFull`, ran `perl` as its child, and the image has none. The child now uses shell builtins only. The test then passed 10 of 10 runs on Alpine (busybox ash, no `perl`) and on Ubuntu (dash), and its class passed there too. |

Two more defects surfaced while building at the floors. Both are fixed and recorded in the [platform lessons](../specs/platform_support.md#lessons-learned):

- **`close_range` compiled out silently.** The old headers lack `SYS_close_range`, so every start would have scanned the descriptor table instead.
- **`-lutil` before the sources dropped libutil and libpthread on glibc 2.27.** The library then had unresolved `forkpty` and `pthread_sigmask`.

Not verified locally:

- **arm64:** Docker Desktop here has no arm64 emulation. CI builds on native arm64 runners and runs both smoke containers there.
- **macOS:** no Mac is available. CI checks `minos` and runs the smoke on macOS 15.

### Phase 2: Linux start without fork (I3)

This phase needs Phase 1, for two reasons:

- Phase 1's floor check proves that the new path adds no newer symbol.
- Phase 1's musl build runs the new path on musl.

**Steps:**

- Open both ends of the terminal close-on-exec in the parent.
- Start the child with `clone`, and replace the Linux report pipe with the shared start record.
- Use `CLONE_PIDFD`, with the fallback to `pidfd_open`.
- Remove `forkpty` and `-lutil` from the Linux build.
- Add a parameter for the host's resident memory to `PtyStartupBenchmarks`, on Linux only: 0, and 2 GiB touched before the measured starts.

**Tests:**

- The existing Unix tests pass unchanged on glibc and on musl. They cover starts, start failures, signals, job control and descriptors.
- New: one thread starts sessions in a loop while another starts processes with `Process.Start`. None of those processes may hold a descriptor of a session's terminal, as checked through `/proc/<pid>/fd`.
- New: concurrent starts that mix missing programs and missing working directories each report their own stage and error.

**Closed when:**

- A start with 2 GiB resident is within 10% of a start with nothing touched. Today it is about 13 ms slower.
- A start's mean time and allocation, with nothing touched, are no worse than today's: 1.749 ms and 1.26 KB in CI's run.
- The descriptor test passes 100 consecutive runs.
- The Linux library no longer imports `forkpty`.

**Documents:**

- [platform_support.md](../specs/platform_support.md): the Linux backend row, plus the lessons learned.
- [lifecycle.md](../specs/lifecycle.md): the failure table's close-on-exec pipe, and invariant 9's instants inside and after `forkpty`.
- [pty_crossplatform.md](../references/pty_crossplatform.md): "Linux / FreeBSD: forkpty + execve", including the fork child hygiene paragraph, and "Start failure report" for Linux.

### Phase 3: API (I4, I5)

This phase is independent of Phases 1 and 2 and can ship with either.

**Steps:**

- Add the Windows build check to `IsSupported` and to `Pty.Start`.
- Add `PtyResult.ExitStatus`.

**Tests:**

- `CompleteAsync` on a Unix child ended by `SIGTERM` reports `Signal` 15 and `ExitCode` 143.
- A child that runs `exit 143` itself reports `Signal` null.
- On Windows, `Signal` is null.
- `IsSupported` is `true` on CI's Windows runners. The builds before 1809 cannot be tested on hosted runners.

**Behavior change for the release note:** on Windows before 1809, `IsSupported` is `false` and `Pty.Start` throws `PlatformNotSupportedException`.

**Documents:**

- The doc comments of `Pty.IsSupported` and `PtyResult`.
- [core_session.md](../specs/core_session.md).

### Phase 4: Measurement (I6)

This phase is independent of the others. It runs after Phase 1, so that it measures binaries built the way they will ship.

**Steps:**

- Benchmark `main` and `817a001` several times each.
- Bisect if `main` is behind.
- Sweep the ring's capacity.

**Closed when:**

- The distribution of macOS flood ratios over at least five runs of `main` is recorded.
- If that distribution falls below the recorded 1.11–1.53, the cause is fixed, or it is recorded under [next_todo.md](../next_todo.md#open).
- The capacity sweep is recorded in the platform lessons, and it replaces the "Output buffer geometry" bullet in [next_todo.md](../next_todo.md#unverified).

### Phase 5: Decisions (I7, I8, I9)

Each item starts only after its decision. The three decisions are independent of each other.

- **I7: Metrics.**
  - Decide.
  - If yes, add the meter and its instruments, with a `MeterListener` test that sees each instrument move.
  - The benchmarks' allocation columns stay at 0 B, with and without a listener.
- **I8: Minimal environment.**
  - Decide the API's shape.
  - Measure the minimum for each OS.
  - Test that a secret set in the host's environment does not reach the child.
  - Test that `cmd.exe` and `pwsh` start with the Windows minimum.
- **I9: Windows exit watcher.**
  - Probe wait completion packets.
  - If they hold, replace the watcher.
  - Test that 200 sessions all have their exit observed, while the number of threads stays the same.

**Closed when:** each decision is recorded, in a specification or in [next_todo.md](../next_todo.md). Each item that was implemented passes its tests and keeps 0 B.

## Not in This Plan

- **Windows `Kill(PtySignal)` terminates the child, whatever the signal.** This is a recorded non-goal, N14 in [terminal.md](../specs/terminal.md#non-goals). A host that wants an interrupt writes `0x03` as input.
- **`PtyExitStatus.NodePtyExitCode` on a core type.** The samples that use only the core package rely on it. Moving it would break them and change no behavior.
- **A clearer error when the shim fails to load.** Once the floor is the runtime's own, every system that runs .NET 10 can load the shim. A wrong RID is the only remaining cause.
- **Windows floods on two CPUs, and Linux bursts of 32 KiB or more.** Both are recorded under [next_todo.md](../next_todo.md#open), with their reasons.
- **FreeBSD.** It stays experimental and untested.
