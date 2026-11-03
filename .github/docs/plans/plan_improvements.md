# Improvement Plan

Goal: MiniPty loads wherever .NET 10 runs on its platforms. A session's start costs the same whatever the host's size. The library reports accurately what it supports and how a child ended. The items below came from a review of the library as a whole. Each is closed by a fix, or by a decision recorded in the specifications, as its section says.

**Status:** closed (2026-11-03). Every phase is closed. Phase 1: CI passed every job on all six RIDs ([run 41405509773](https://github.com/guitarrapc/MiniPty/actions/runs/41405509773)). Phase 2: its gates were met locally, and CI passed every job on all six RIDs ([run 41425428814](https://github.com/guitarrapc/MiniPty/actions/runs/41425428814)). Phases 3 and 5, the fix to a test race found along the way, and the samples passed every job of [run 41512274422](https://github.com/guitarrapc/MiniPty/actions/runs/41512274422) on `f2a0c18`: the 762 tests on the six RIDs and in the musl container, the NativeAOT samples on every RID, and the NuGet smoke on 12 systems. macOS skipped the bridge flood-frame tests whose flood came slower than 4 KiB per ms, 2 on arm64 and 3 on Intel. The review fixes after `f2a0c18` have not run in CI: the suite passed locally on Windows 11 and in glibc and musl containers, and macOS, the NativeAOT samples, and the NuGet smoke were not run. Phase 4: the macOS flood is not a regression, five runs on `main` are recorded, and the ring stays at 16 KiB; the ring on macOS stays under next_todo's unverified items. The measurements below were taken on 2026-11-02 and 2026-11-03.

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

- **The cost is about 6 ms per GiB.** The README's start benchmark (1.569 ms) runs in a small benchmark process, so it does not show this.
- **The terminal's descriptors can leak.** `forkpty` opens both ends of the terminal without `O_CLOEXEC`, and there is no way to ask it for that flag ([minipty_unix.c:375](../../../src/MiniPty/Native/minipty_unix.c)). Another thread of the host may fork in that moment, `Process.Start` included. That process inherits both ends, and they stay open across its `exec`. Either copy lasts as long as that unrelated process:
  - A copy of the slave keeps the terminal open, so output ends only through the post-exit drain.
  - A copy of the master keeps the tty from hanging up when the session closes it.

  This is invariant 9 in [lifecycle.md](../specs/lifecycle.md) and the paragraph on both ends close-on-exec in [pty_crossplatform.md](../references/pty_crossplatform.md#linux-clone--execve). The macOS path opens both ends close-on-exec and has no such window.

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
- **Instruments were deferred.** next_todo.md left them to a separate decision, which Phase 5 took ([core_session.md](../specs/core_session.md#metrics)).

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

**CI ([run 41401444747](https://github.com/guitarrapc/MiniPty/actions/runs/41401444747), 2026-11-02).** Every native build and its check passed. So did every NuGet smoke: macOS 15 (arm64 and Intel), AlmaLinux 8 and Alpine 3.21 (x64 and arm64), and the existing runners.

Only the musl test job failed, in one test: `DisposeLetsAnInteractiveShellHangUpItsJobs`.

- **Cause:** the job container's first process is `tail -f /dev/null`, which never reaps. The shell's job did end on the hang-up, but it stayed a zombie, and the test waited for it to be gone.
- **Reproduced** locally with the same entrypoint: the test failed, a zombie `sleep` was left with parent 1, and with `--init` it passed.
- **A second test was also affected:** `HostExitKillsAChildStillInItsHangupGrace(exit-handler)` judges the same way and failed 2 of 5 runs there.
- **Fix:** both tests now count a process that is not their own child as ended once it is a zombie.
- **After the fix:** with `tail` as the first process, both passed 5 of 5 runs and the suite passed 733 of 733. On Ubuntu with systemd, their class passed 3 of 3 runs.

**CI after the fix ([run 41405509773](https://github.com/guitarrapc/MiniPty/actions/runs/41405509773)).** All 27 jobs passed.

- **musl tests:** the musl test job ran all 733 tests in the same `tail` container, and they all passed.
- **Floor checks:** they reported the following.

| RID | Floor check |
|---|---|
| `linux-x64`, `linux-arm64` | Highest glibc version required is 2.17 |
| `linux-musl-x64`, `linux-musl-arm64` | Links musl's libc |
| `osx-x64`, `osx-arm64` | Every file declares `minos 12.0` |

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
- A start's mean time and allocation, with nothing touched, are no worse than today's: 1.569 ms and 1.26 KB in CI's run 41106916265.
- The descriptor test passes 100 consecutive runs.
- The Linux library no longer imports `forkpty`.

**Documents:**

- [platform_support.md](../specs/platform_support.md): the Linux backend row, plus the lessons learned.
- [lifecycle.md](../specs/lifecycle.md): the failure table's close-on-exec pipe, and invariant 9's instants inside and after `forkpty`.
- [pty_crossplatform.md](../references/pty_crossplatform.md): "Linux / FreeBSD: forkpty + execve", including the fork child hygiene paragraph, and "Start failure report" for Linux.

**Built (2026-11-02).** `spawn_pty_child_linux` in `minipty_unix.c` replaces `forkpty` on Linux; FreeBSD keeps it.

- **Terminal:** the parent opens both ends close-on-exec and `O_NOCTTY`. The slave comes from `TIOCGPTPEER`, or `ptsname_r` and `open` on kernels before 4.13. The termios and the window size are set on the slave before the child exists.
- **Child:** `clone(CLONE_VM | CLONE_VFORK | CLONE_PIDFD | SIGCHLD)` on an `mmap`ed stack of 64 KiB plus a pointer per argument, over a guard page. It runs these steps:
  1. Every signal set to `SIG_DFL` with `rt_sigaction`.
  2. `setsid`, then `TIOCSCTTY`.
  3. The slave `dup2`ed onto stdio, moved above stdio first if it landed there.
  4. Descriptors scrubbed.
  5. `chdir`.
  6. The calling thread's mask restored.
  7. The existing `PATH` lookup and exec.
- **Failure report:** a failing child writes its stage and `errno` into a record on the calling thread's stack. The Linux report pipe is gone.
- **Signals:** the parent blocks every signal with `rt_sigprocmask` around the clone.
- **pidfd:** it comes from `CLONE_PIDFD`. A kernel that leaves it empty falls back to `pidfd_open`, and one that rejects the flag gets a second clone without it.
- **Build:** `-lutil` leaves the Linux build in `build-native.sh`, the project's own native build, and the NativeAOT link (`buildTransitive/MiniPty.targets`). The Linux library links with `-z now`, and `build-native-package.sh` checks `BIND_NOW`.

Changes from the steps above:

- **Benchmark:** the host-memory case is a class of its own, `PtyStartupHostMemoryBenchmarks` (`HostResidentMiB` 0 and 2048), not a parameter of `PtyStartupBenchmarks`. The README generator reads `PtyStartupBenchmarks` by method name alone, and a parameter would have doubled every startup case.
- **Descriptor test:** the probe lists the terminals among its own descriptors (`[ -t N ]`) instead of reading `/proc/<pid>/fd`, so it runs on macOS too.
- **Two more tests:** one checks that the child leads a session on the terminal with its group in the foreground, from `/proc/<pid>/stat`. The other checks that it starts with no signal ignored and with the calling thread's mask. All four new tests are in `PtyUnixStartTests`.

A defect surfaced on musl: the signal reset left signal 34 ignored.

- **What was seen:** the first musl run failed the signal test, because the child's `SigIgn` had signal 34 set.
- **Cause:** musl's `sigaction` refuses its internal signals 32–34, and the musl host had 34 ignored. An ignored signal stays ignored across exec.
- **Not new:** the same test against the `forkpty` code failed the same way on musl.
- **Fix:** the Linux child now resets every signal with the system call. Its new test passes on glibc and on musl.

Results on x64, locally. "Old" is this tree with `minipty_unix.c`, `minipty_unix_internal.h`, and `MiniPty.csproj` from `HEAD`.

| Check | Result |
|---|---|
| Start, `PtyStartupHostMemoryBenchmarks`, WSL2 (kernel 6.6) | Nothing touched / 2 GiB: new 0.94 / 0.88 ms, old 2.31 / 37.27 ms; 1.26 KB allocated in every case |
| The same, in a Docker Desktop container (Ubuntu 24.04, glibc 2.39) | New 0.81 / 0.87 ms (2 GiB +7%), old 2.11 / 35.96 ms; 1.26 KB in every case |
| Descriptor test, new | 100 of 100 consecutive runs pass (glibc container), each with 100 starts and about 420 probes |
| Descriptor test, old | Fails every run: 4–6 of about 420 probes in WSL (three runs), 15 of 335 on musl |
| Test suite, glibc | 733 of 733 pass in WSL before the new tests existed; 737 of 737 in the glibc container, twice, the last with the final code |
| Test suite, musl (Alpine SDK container) | One run of the final code failed 1 of 737, and its log kept only the summary, so the test is not known. It did not recur in 30 later complete runs: 15 on all 8 CPUs and 15 pinned to 2. The four new tests passed 20 of 20 runs there. One more run on 2 CPUs hung instead, in a test race that predates this phase (below) |
| Package build, glibc 2.27 and musl 1.2.3 | Both build and pass `--check`, now including `BIND_NOW`, and compile without warnings under `-Wall -Wextra`. Highest glibc version required: 2.14 (2.17 before). No `forkpty`, no libutil |
| NativeAOT, linux-x64 and linux-musl-x64 | `Capture`, `Session`, `Observe`, and `VsCodeTerminalHelper --smoke` publish against the floor-built archives and run |

A test race found while chasing that failure, not caused by this phase: `PtyUnixLoopTests.AFailureInOneSessionEndsOnlyThatSession` can wait forever on Unix; it was seen on Linux.

- **Seen:** on 2 CPUs, one complete musl run stopped in it for over 16 minutes. Both `cat` children were asleep in `read`.
- **Cause:** the test injects its failure only when the loop serves the failing session (`FailLoopReadsOfPidForTests`), and reads that session's output with no timeout. A consumer that reads inline can take the turn first. The loop then drops its report of output alone, the consumer reads all the output itself and waits, and the loop never serves the session again. No output is lost; only the injected failure never happens.
- **Reproduced deterministically** with an instrumented build and a diagnosis test (neither kept). The test held the producer's turn while the loop reported, then let the consumer read. It hung in 5 of 5 runs on the new code and 5 of 5 on the `HEAD` code. The log read: report dropped, inline read published, inline read empty, no injected failure.
- **Not reproduced by repetition:** the test alone passed 100 of 100 runs on 2 CPUs, on both codes. In the instrumented complete runs, 3 of 12 had the consumer's inline read in the same millisecond as the loop's service.
- **Fix:** the test waits until the loop has failed the session (`OutputPump.IsProducerFinished`) before reading its output, and bounds the read with its timeout. The constraint is recorded in [platform_support.md](../specs/platform_support.md#platform-specific-test-constraints). Verified on 2 CPUs on musl: the test passed 100 of 100 runs, and the suite passed 737 of 737 tests in 10 of 10 runs. The suite also passed 737 of 737 on glibc.

Not verified locally:

- **arm64:** there is no arm64 Docker host here. CI builds and tests natively.
- **macOS:** only preprocessor guards changed there. `unifdef -D__APPLE__ -U__linux__ -U__FreeBSD__` gives the same 587 lines for `minipty_unix.c` before and after, comments aside. CI builds and tests it.
- **FreeBSD:** the `forkpty` path lost only its Linux branches, and it was not compiled.
- **CI's start figures:** the Build workflow runs no benchmark, so CI's 1.569 ms from its last benchmark run with a start case (41106916265) has no counterpart. The gate is met by the local A/B above.
- **Kernels before 5.2 or 4.13:** neither the fallback for `CLONE_PIDFD` nor the one for `TIOCGPTPEER` was run.

**macOS, on an Apple M2 with macOS 27.0.1 and .NET 10.0.13, run by hand.** `PtyStartupHostMemoryBenchmarks` took 6.042 ms with nothing touched and 6.134 ms at 2 GiB (+1.5%), with 1.27 KB allocated in both. `posix_spawn` does not copy the host, as its documentation says.

**CI ([run 41425428814](https://github.com/guitarrapc/MiniPty/actions/runs/41425428814), commit `5e61886`, 2026-11-02).** All 27 jobs passed.

| Check | Result |
|---|---|
| Test suite | 737 of 737 on each of `ubuntu-24.04`, `ubuntu-24.04-arm`, `macos-26`, `macos-26-intel`, `windows-2025`, `windows-11-arm`, and the Alpine container (`linux-musl-x64`) |
| Floor checks | Highest glibc version required: 2.14 on `linux-x64` and 2.17 on `linux-arm64`. Both musl RIDs link musl's libc. All four macOS files declare `minos 12.0` |
| NuGet smoke | macOS 15 and 26 (arm64 and Intel), AlmaLinux 8 and Alpine 3.21 (x64 and arm64), and the Ubuntu and Windows runners |

The commit predates the fix to `AFailureInOneSessionEndsOnlyThatSession`. That fix passed in CI later, with Phases 3 and 5 ([run 41512274422](https://github.com/guitarrapc/MiniPty/actions/runs/41512274422)).

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

**Built (2026-11-02).**

- **`IsSupported`:** on Windows it compares `Environment.OSVersion.Version` with 10.0.17763 (`Pty.IsSupportedWindowsVersion`). `Pty.Start` throws `PlatformNotSupportedException` when it is `false`, and on Windows the message names the required build and the current one.
- **`PtyResult.ExitStatus`:** `ExitCode` is now read from it. The shared completion (`PtyCompletion.RunAsync`) returns a `PtyExitStatus` instead of an exit code. The session's allocation-free exit wait still carries the code alone, so the signal is read from the session once that wait has completed.

Changes from the steps above:

- **`PtyCaptureResult.ExitStatus` too.** `PtyCapture.RunAsync` disposes the session it starts, so a capture caller had no way at all to learn that a signal ended the child. Both results come from the same completion, so both gained the property.
- **The build is pinned by a test.** Hosted runners cannot run a build before 1809, so `PtyIsSupportedOnlyOnWindowsBuildsWithConPty` checks the comparison itself, at builds 9200, 9600, 14393, 17134, 17763 and 26200.
- **README and specifications.** The README's platform table and [platform_support.md](../specs/platform_support.md) name Windows Server 2019, and say what happens on earlier builds. [completion.md](../specs/completion.md) and [capture.md](../specs/capture.md) list `ExitStatus`.

Cost: each result object holds an `int?` beside the exit code, which by field layout makes it 8 bytes larger (`PtyResult` 48 to 56 bytes on 64-bit). The startup benchmark that runs `CompleteAsync` allocates one per run; it was not rerun.

Results, locally:

| Check | Result |
|---|---|
| Test suite, Windows 11 (build 26200) | 746 of 746 pass |
| Test suite, Linux (WSL2, glibc) | 746 of 746 pass, including the new Unix signal tests, which return early on Windows |
| Solution build, Release | No warnings |

Not run locally: macOS, musl, and NativeAOT publishing. Nothing native changed, and the managed change uses no reflection. CI built, tested, and published them with Phase 5 ([run 41512274422](https://github.com/guitarrapc/MiniPty/actions/runs/41512274422)), including the Unix signal tests on macOS and in the musl container.

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

**Measured (2026-11-02).**

**The macOS flood is not a regression.** CI already held 17 runs of the rebuilt macOS reader, so they were read first. They come from the macOS branch, from `817a001` on `main`, from the Linux branch, and from `5ca042f` on `main`. Between them the code differs only off the macOS read path: `7ee5ce9` differs from `817a001` in two comments, and the Linux work changed the Linux path and test counters. Three runs of `df60b80` on `main` followed ([41456250878](https://github.com/guitarrapc/MiniPty/actions/runs/41456250878), [41457284601](https://github.com/guitarrapc/MiniPty/actions/runs/41457284601), [41458275092](https://github.com/guitarrapc/MiniPty/actions/runs/41458275092)); its only macOS change since `5ca042f` is the native compile flags (`-mmacosx-version-min=12.0`). 1 MiB flood on `macos-26`, job `AllCpus`; the baseline is a reader that blocks one thread per session:

| Code | Runs | MiniPty MiB/s | Baseline MiB/s | Ratio |
|---|---|---|---|---|
| macOS branch, `54d7c89` | 6 | 43.7–53.1 | 32.6–44.0 | 1.17–1.53 |
| macOS branch, `7ee5ce9` (the code of `817a001`) | 3 | 38.9–52.9 | 35.2–40.4 | 1.11–1.43 |
| `817a001` on `main` | 1 | 56.0 | 77.2 | 0.73 |
| Linux branch, `6290522` | 3 | 47.2–64.4 | 34.1–50.9 | 1.20–1.38 |
| Linux branch, `443b433` | 3 | 48.8–60.6 | 49.6–81.3 | 0.75–0.98 |
| `5ca042f` on `main` | 1 | 43.1 | 54.4 | 0.79 |
| `df60b80` on `main` | 3 | 42.6–48.4 | 40.9–48.9 | 0.97–1.08 |
| All 20 | 20 | 38.9–64.4, median 49.8 | 32.6–81.3, median 41.3 | 0.73–1.53 |

- **MiniPty's own figure held.** One of its 200 iterations took over 30 ms: 31.7 ms, in a run of `df60b80` whose other nine took 20–29 ms. Before the rebuild (`642be11`, 12 runs) it was 20.4–57.8 MiB/s, median 35.9, with 48 of 120 iterations over 30 ms.
- **The ratio followed the baseline.** The nine runs that recorded 1.11–1.53 had the baseline at 32.6–44.0 MiB/s, with 23 of its iterations over 30 ms. In five of the six runs where the baseline reached 49.6 MiB/s or more, MiniPty was at 0.73–0.98 of it. Both README runs on `main` are among those five. In the three runs of `df60b80` the baseline sat between the two groups, at 40.9–48.9, and the ratio at 0.97–1.08.
- **Five runs on `main`:** 0.73, 0.79, 0.97, 1.04, and 1.08, median 0.97. That falls below the recorded 1.11–1.53, which came from runs with a slow baseline, so the gap is recorded below.
- **Keystrokes swing the same way.** In the eight runs of the rebuilt reader that measured them, MiniPty's P50 was 33–105 μs and the baseline's 34–78 μs, and each led in some runs.
- **No bisect was needed.** MiniPty's figure on `main` (56.0, 43.1, 42.6, 47.2, and 48.4) sits inside its band.
- **Recorded:** the gap against a fast baseline is under [next_todo.md](../next_todo.md#open), and the lesson on judging a macOS runner in [platform_support.md](../specs/platform_support.md#lessons-learned) now says what the ratio leaves out. The README's caveat on hosted runners names the macOS swing.

**The ring stays at 16 KiB.** Each capacity was built from the same tree and run as `PtyThroughputBenchmarks.MiniPty` and `PtyLatencyBenchmarks.MiniPty`, in a rotating order. 32 and 64 KiB need the pump's 16-bit wait minimum (`_waiterMinimumData`) widened to an `int`, because a `short` cannot hold a minimum that large.

Linux, a glibc container (`mcr.microsoft.com/dotnet/sdk:10.0`) on WSL2 with 8 vCPUs, six rounds:

| Case | 16 KiB | 32 KiB | 64 KiB |
|---|---|---|---|
| 1 MiB flood, `AllCpus`, MiB/s | 185–230, median 215 | 179–214, median 190 | 183–216, median 205 |
| 1 MiB flood, `TwoCpus`, MiB/s | 201–227, median 211 | 193–224, median 204 | 199–213, median 205 |
| 64 KiB flood, `AllCpus`, MiB/s | 145–159, median 156 | 139–156, median 153 | 143–159, median 150 |
| 64 KiB flood, `TwoCpus`, MiB/s | 154–165, median 158 | 152–163, median 154 | 154–159, median 158 |
| Process CPU per 1 MiB flood | 3.57–4.82 ms | 3.63–4.20 ms | 3.64–4.33 ms |
| Keystroke P50 / P95, `AllCpus`, μs | 122–150 / 147–229 | 123–131 / 148–198 | 122–127 / 145–169 |
| Keystroke P50 / P95, `TwoCpus`, μs | 69–113 / 117–181 | 66–125 / 116–222 | 67–112 / 130–167 |

Every case allocated 0 B. On two CPUs, each capacity's keystroke P50 moved between about 66 and 125 μs from run to run, so the medians there say nothing about the ring.

Windows 11 (build 26200), two rounds:

| Case | 16 KiB | 32 KiB | 64 KiB |
|---|---|---|---|
| 1 MiB flood, `AllCpus`, out-of-band / in-box, MiB/s | 14.7–14.8 / 9.6–9.8 | 14.9 / 9.6–9.8 | 14.7–15.1 / 9.7–9.8 |
| 1 MiB flood, `TwoCpus`, out-of-band / in-box, MiB/s | 10.4–11.4 / 7.2–7.3 | 10.6–11.8 / 7.2 | 11.4–11.9 / 7.2 |
| 64 KiB flood, `AllCpus`, out-of-band / in-box, MiB/s | 14.8–14.9 / 9.2–9.5 | 14.9–15.0 / 9.7 | 14.8 / 9.5–9.6 |
| Thread-pool work items per 1 MiB flood | 4,074–4,106 | 4,094–4,107 | 4,094–4,106 |
| Keystroke P50, out-of-band, `AllCpus` / `TwoCpus`, μs | 58–65 / 94–106 | 60–74 / 95–104 | 58–59 / 83–95 |
| Keystroke P50, in-box, μs | 14,440–14,563 | 14,463–14,556 | 14,498–14,584 |

ConPTY's renderer sets the pace on Windows, as the throughput benchmark's remarks say. The in-box 1 MiB flood on two CPUs allocated 12 B per operation with every capacity, and one 64 KiB case with 32 KiB allocated 8 B; neither comes from the ring.

**Decision:** the ring stays at 16 KiB. No capacity won a case outside the others' spread on either platform. A larger one would cost each session 16 or 48 KiB more pinned memory, plus the widened field, and would double or quadruple the burst a bridge coalesces (half the ring). The lesson is in [platform_support.md](../specs/platform_support.md#lessons-learned). The "Output buffer geometry" bullet in [next_todo.md](../next_todo.md#unverified) is now a bullet for macOS alone.

The first Linux and Windows rounds were discarded. A wait loop of this session's own had been spinning one vCPU in the container the whole time, and the WSL VM shares the host's CPUs, so the Windows floods ran 10–25% slower under it on every capacity. The container also held unreaped children. The rounds above ran in a fresh container started with `--init`, and on Windows after it, with nothing else running.

Not measured: the ring on macOS. A run per capacity there needs a pushed branch per capacity on CI, or a Mac. A macOS master hands out at most 1,024 bytes per read.

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

**Decided and built (2026-11-03).** All three items were taken up. Their deferrals are removed from [next_todo.md](../next_todo.md).

**I9: the exit watcher uses a thread-pool wait per child.** The decision ruled out an API that is not documented, which excluded associating wait completion packets with MiniPty's completion port (`NtAssociateWaitCompletionPacket`). The documented Win32 thread-pool wait (`SetThreadpoolWait`, Windows Vista and later) was probed instead, on Windows 11 build 26200:

| Design | Threads added by 2,000 pending waits | Signal to observer, P50 / P90 / P99 |
|---|---|---|
| Threads that each wait on 63 handles (`WaitForMultipleObjects`), as before | 32 | 7.3–7.4 / 11.7–12.9 / 19.2–21.4 μs |
| `SetThreadpoolWait` | 0 | 7.0–7.5 / 11.2–12.2 / 18.8–19.7 μs |

- **No thread per 63 sessions, at the same latency.** Two runs each. .NET's `RegisterWaitForSingleObject` added 32 threads as the earlier design did.
- **The guarantee is not documented.** A pool wait holds no thread because of how the Windows thread pool has waited since Windows 8. A pool that waited on threads of its own would cost what the earlier design did, not more.
- **What moved.** The callback runs on a worker of the process's native thread pool for microseconds, and only records exit. The observer reaches it through a `GCHandle` in a native block, so a registration allocates nothing on the managed heap. The watcher keeps owning a duplicate of each process handle. The code lost its threads, lock, slot arrays, and wake event.
- **Tests.** 1,000 waits on events add no thread outside the pool beyond the tolerance of 4; against the earlier watcher the same test counted 16 more and failed. 200 open sessions whose children exit at once each report their own exit code. The idle-session thread test lost its allowance for watcher threads.

**I8: `PtyStartInfo.InheritEnvironment`, a `bool` defaulting to `true`.** `false` starts the child from a minimum, with `Environment` applied on top. The shape was chosen against an enumeration of starting points (`Inherit`, `Minimal`) after weighing the extensions in sight:

| Extension | `bool` | Enumeration |
|---|---|---|
| Keep some host variables (an allow-list) | The caller copies them through `Environment` | The same |
| The minimum changes on a future Windows | The internal list changes | The same |
| Expose the minimum for inspection | A separate property | The same |
| A fully empty environment | The minimum's keys removed through `Environment` | A new member |
| A starting point built from a user's account | A second property | A new member; MiniPty does not switch users, and a login shell builds it itself |

The minimum was measured by starting each shell with every host variable removed, on Windows 11 build 26200:

| Variables kept | `cmd.exe` | `pwsh` 7 | Windows PowerShell |
|---|---|---|---|
| None | Starts | Starts and resolves its host name | Fails: `8009001d`, a cryptographic provider that did not load |
| `SystemRoot` | Starts | Starts | Starts |
| `SystemRoot`, `PATH` | Starts, and finds `where` and `findstr` | Starts | Starts |

- **The Windows minimum is `SystemRoot`,** set to the Windows directory rather than copied from the host. On Unix it is empty: `TERM` follows `TerminalName` as before, MiniPty's lookup falls back to `/bin:/usr/bin`, and shells set a default `PATH`.
- **On Windows MiniPty looks `FileName` up itself:** the fixed locations, then a `PATH` the overlay sets. `cmd.exe` is found in the system directory. A batch file started with a raw `CommandLine` stays with `CreateProcess`.
- **Tests.** Builder tests check both platforms' minimum, the overlay on top, and the lookup flag. A variable set in the test host reaches a child that inherits and not one that does not, while the overlay and the minimum do. `cmd.exe`, Windows PowerShell, and `pwsh` (when installed) start and run from the Windows minimum.
- **Recorded:** [core_session.md](../specs/core_session.md#starting-from-the-minimum) and the README.

**I7: a meter named `MiniPty` with six instruments.** The instruments are open sessions, start duration, start failures, output bytes, stalls on a full ring, and the Unix consumer's native waits; [core_session.md](../specs/core_session.md#metrics) lists them.

- **When nothing listens,** each instrument is one branch on its `Enabled` flag, with no timestamp taken. Only the open-session count is kept always, so that a listener that attaches later reads the sessions open. It costs an interlocked add per start and per disposal, and is read through an observable instrument.
- **No tag differs per session.** Start failures carry `error.type`.
- **Tests.** A `MeterListener` sees each instrument published and each move: open sessions up by one and back on disposal, not again on a second disposal; one start timed and one failed start counted as `Win32Exception`, with an `ArgumentException`, and on Unix a `PlatformNotSupportedException`, not counted; a start that fails while its session is built counted with its type, not timed, and not counted as an open session; output bytes equal to the bytes delivered, and none counted for reads after the disposal; a stall of at least 90 ms when a flood waits 100 ms for a reader; and consumer waits on Unix, each under 0.5 s. A listener that throws on the start's duration leaves no session open. A test makes each recording call on the output path, and the one for the start's duration, 1,000 times and counts 0 B with and without a listener, and no timestamp without one.
- **One meter per load.** The open-session test read 0 throughout in the full suite and passed alone. An earlier test loads MiniPty into a second `AssemblyLoadContext`, whose meter has the same name and counts that load's sessions, and the test's listener had matched meters by name. It now matches its meter by identity, and the [specification](../specs/core_session.md#metrics) tells hosts that a reading by name is one load's count.
- **Cost, measured with `PtyMetricsBenchmarks`:** MiniPty's 1 MiB flood without a listener and with one that enables every instrument through no-op callbacks.

| Platform | Job | Without listener | With listener | Process CPU per flood, without / with | Allocated |
|---|---|---|---|---|---|
| Linux, glibc container on WSL2, 8 vCPUs, two rounds | `AllCpus` | 4.96–5.17 ms | 5.11–5.25 ms | 3.87–4.02 / 3.89–4.14 ms | 0 B |
| | `TwoCpus` | 5.16–5.24 ms | 5.11–5.15 ms | 3.89–3.95 / 3.83–3.98 ms | 0 B |
| Windows 11 build 26200, in-box ConPTY, one round | `AllCpus` | 128.3 ms | 140.6 ms ± 16.1 | 181 / 196 ms | 8 B / 0 B |
| | `TwoCpus` | 156.7 ms | 139.9 ms | 163 / 132 ms | 0 B / 168 B |

Every Linux difference is inside the run-to-run error (0.15–0.33 ms). On Windows ConPTY's renderer sets the pace, and the in-box ConPTY's flood allocated a few bytes per operation in earlier runs as well. The recording calls on the flood's path were counted at 0 B with a listener (above), so the 168 B does not come from them.

- **Histogram buckets in seconds, found through the Aspire sample.** The durations are recorded in seconds, but OpenTelemetry's default buckets are for milliseconds, so the Aspire dashboard showed a P50 of 5 s for stalls of about 0.1 s. Each histogram now advises boundaries for its range, and the same stalls fell in the 0.1–0.25 s bucket. A test checks that each histogram advises sub-millisecond, ascending boundaries ([core_session.md](../specs/core_session.md#metrics)).
- **Samples.** [WinUiTerminal](../../../samples/WinUiTerminal) and [AvaloniaTerminal](../../../samples/AvaloniaTerminal) show start time, output read, and stalls on their status line through a `MeterListener`; a 2.5 MiB flood stalled them 3 and 17 times. [WebTerminalScale](../../../samples/SingleFiles/WebTerminalScale.cs) shows the meter on its page and prints each step's start time and the flood's stalls in its bench. [AspireTerminal](../../../samples/AspireTerminal) exports the meter over OTLP to the Aspire dashboard, with a load resource whose slow reader stalls every round. WebTerminal stays the minimal embedding sample.

The decisions are recorded: I7 in [core_session.md](../specs/core_session.md#metrics), I8 in [core_session.md](../specs/core_session.md#starting-from-the-minimum), and I9 in [pty_crossplatform.md](../references/pty_crossplatform.md#child-exit-observation). CI passed all three on every RID with the samples at `f2a0c18` ([run 41512274422](https://github.com/guitarrapc/MiniPty/actions/runs/41512274422)), before the review fixes that added several of the I7 tests above.

## Not in This Plan

- **Windows `Kill(PtySignal)` terminates the child, whatever the signal.** This is a recorded non-goal, N14 in [terminal.md](../specs/terminal.md#non-goals). A host that wants an interrupt writes `0x03` as input.
- **`PtyExitStatus.NodePtyExitCode` on a core type.** The samples that use only the core package rely on it. Moving it would break them and change no behavior.
- **A clearer error when the shim fails to load.** Once the floor is the runtime's own, every system that runs .NET 10 can load the shim. A wrong RID is the only remaining cause.
- **Windows floods on two CPUs, and Linux bursts of 32 KiB or more.** Both are recorded under [next_todo.md](../next_todo.md#open), with their reasons.
- **FreeBSD.** It stays experimental and untested.
