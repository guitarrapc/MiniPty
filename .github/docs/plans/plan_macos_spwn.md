# Plan: macOS parallel spawn stability (`forkpty` → `posix_spawn`)

Working plan for macOS CI flake under parallel tests and the follow-up spawn redesign. This is a **plan**, not an implemented API contract. After merge, record lessons learned in [platform_support.md](../specs/platform_support.md) and [pty_crossplatform.md](../references/pty_crossplatform.md).

Related: branch `flasky` commit `8f37ff083877a2a001774ed7918c94d344bf1e6b` (interim workaround), [plan_minipty_next.md](plan_minipty_next.md) (parallel test policy), CI workflow [build.yaml](../../workflows/build.yaml).

## Summary

| Item | Choice |
|---|---|
| **Problem** | macOS CI (GitHub Actions) intermittently fails when TUnit runs ~60+ PTY tests in parallel; spawn errors under burst `forkpty`. Local macOS may pass. |
| **Interim fix (on `flasky`)** | Global `pthread_mutex` around `forkpty()` in `libminipty_unix` + propagate errno from native shim. **Rejected** — not merged; superseded by this plan. |
| **Branch** | `macos` from `main`; errno + spawn restacked here (not `flasky`). |
| **Target direction** | **node-pty macOS path:** `posix_openpt` + `posix_spawn` + spawn-helper for controlling TTY. Keep `forkpty` on Linux / FreeBSD. |
| **Reference clone** | Clone [microsoft/node-pty](https://github.com/microsoft/node-pty) under `.references/node-pty` on the macOS dev machine (gitignored; not committed). |
| **Delivery** | Separate PR from Windows allocation work. Implement and validate primarily on macOS hardware. |
| **Merge gate** | All tests green on macOS (arm64 + x64 CI matrix) **with parallel TUnit**; Linux / FreeBSD unchanged or improved. |

## Problem

### Symptom

- **macOS CI only** (or much more often than local): tests fail during `Pty.Start` with errors like `PTY spawn failed (errno N)`.
- Failures correlate with **parallel test execution** (TUnit default parallelism; ~50+ tests call `Pty.Start` in `PtyTests.cs` alone).
- Linux and Windows CI are stable under the same parallel test suite.

### What is *not* the problem (clarification)

Commit `8f37ff0` message says "parallel test" but the change is **not** CI/test-runner serialization. It serializes **`forkpty()` inside `libminipty_unix`** for every Unix OS. Tests still run in parallel.

### Interim workaround (`flasky` / `8f37ff0`)

| Change | File | Purpose |
|---|---|---|
| `pthread_mutex_t minipty_fork_lock` around `forkpty()` | `src/MiniPty/Native/minipty_unix.c` | Serialize PTY allocation + fork across all callers in the process |
| Return errno from `minipty_fork_pty_exec` on failure | `minipty_unix.c`, `UnixPtyBackend.cs` | Accurate `IOException` message (`spawnError` from native return, not `Marshal.GetLastPInvokeError()`) |
| Remove `SetLastError = true` on `minipty_fork_pty_exec` | `UnixPtyBackend.cs` | errno travels in return value |

Lock scope is intentionally narrow: only the `forkpty()` syscall. Child `chdir` / `execve` and parent I/O run outside the lock.

**Status:** branch `flasky`, **not merged to `main`** at time of writing.

## Why parallel spawn fails on macOS

Three overlapping mechanisms explain CI-only flake.

### 1. Burst PTY / process allocation (resource ceiling)

macOS limits pseudo-terminals via `kern.tty.ptmx_max` (often 511 on runners). Parallel tests can issue many `forkpty` calls within seconds. Typical failure modes:

| errno | Name | Meaning in this context |
|------:|------|-------------------------|
| 11 | `EAGAIN` | Resource temporarily unavailable (`forkpty` / `posix_openpt`) |
| 12 | `ENOMEM` | Kernel refused new process / PTY under pressure |
| 6 | `ENXIO` | PTY pool exhausted (`Device not configured`) |

This is **peak-rate** behavior, not necessarily a leak — though leaks make it worse.

### 2. `fork` in a multithreaded parent (.NET test host)

The test host is multithreaded (TUnit, thread pool, GC). On macOS, `fork()` from a multithreaded process is fragile:

- Only the calling thread survives in the child; other threads vanish.
- Mutexes held by dead threads can leave the child in an inconsistent state if the child runs non-trivial code before `exec`.
- Apple documents `posix_spawn` as the preferred path; LLVM sanitizer replaced `forkpty` with `posix_spawn` on Darwin for the same class of issues.

MiniPty's child path is short (`chdir` → `execve`), which limits but does not eliminate risk when **many threads concurrently enter `forkpty`**.

### 3. `openpty` / `forkpty` threading notes (node-pty precedent)

node-pty blocks all signals with `pthread_sigmask` before `forkpty` on Linux, with an explicit comment: *"race condition in openpty"*. MiniPty already blocks signals the same way. node-pty does **not** add a global fork mutex on Linux.

## MiniPty history (why we use `forkpty` today)

From [platform_support.md](../specs/platform_support.md) lessons:

> **macOS spawn must establish a controlling terminal.** A `posix_openpt` + `posix_spawn` path left the slave without a controlling tty; Unix targets use `forkpty` + native `execve`.

Implications for this plan:

- Moving to `posix_spawn` on macOS is **not a revert** — it requires solving controlling TTY, which MiniPty previously failed to do.
- node-pty solved the same problem; their approach is the reference implementation.

## node-pty reference (clone on macOS)

`.references/` is gitignored. On the macOS dev machine:

```bash
mkdir -p .references
git clone --depth 1 https://github.com/microsoft/node-pty.git .references/node-pty
```

### Platform split in node-pty

| OS | Spawn API | Mutex? | Controlling TTY |
|---|---|---|---|
| Linux, FreeBSD, … | `forkpty` + `execvp` | No (signal mask only) | `forkpty` establishes session |
| **macOS** | `posix_openpt` + `posix_spawn` | No | **spawn-helper** opens slave |

### Files to read first

| Path (under `.references/node-pty`) | What to extract |
|---|---|
| `src/unix/pty.cc` | `PtyFork`: macOS vs non-macOS `#if` split; `pty_posix_spawn`; Linux `forkpty` + signal mask |
| `src/unix/spawn-helper.cc` | Controlling TTY: `ttyname(STDIN)` → `open(slave, O_RDWR)` without `O_NOCTTY` |
| `binding.gyp` / build scripts | How spawn-helper binary is built and located at runtime |

### macOS spawn flow (node-pty, conceptual)

```text
Parent:
  posix_openpt → grantpt → unlockpt
  ioctl(TIOCPTYGNAME)     # not ptsname() — thread-safe on macOS
  open(slave), tcsetattr, TIOCSWINSZ
  posix_spawn(helper, …)  # dup2 slave → stdin/stdout/stderr; SETSID; close slave in child actions
  close(slave), keep master fd

spawn-helper child:
  open(ttyname(STDIN), O_RDWR)   # acquire controlling terminal
  chdir(cwd)
  execvp(target, argv)
```

MiniPty must preserve existing semantics:

- Native shim `execve` + PATH lookup + plain-script `sh` fallback ([pty_crossplatform.md](../references/pty_crossplatform.md))
- Explicit `envp` (no `execvp` with parent environ)
- `PtyStartInfo.Size` → `winsize` at spawn
- NativeAOT / no third-party **package** dependencies (a small bundled helper binary inside `runtimes/` is acceptable — same model as `libminipty_unix.dylib`)

## Evaluation of interim mutex workaround

| Aspect | Assessment |
|---|---|
| CI stabilization | Likely effective — reduces simultaneous `forkpty` |
| Correctness of errno fix | **Keep** regardless of spawn strategy |
| Library consumers | **Regresses** parallel `Pty.Start` in one process (global serialization) |
| Platform scope | Applies to Linux / FreeBSD too — **should be `#if __APPLE__` at most** if kept at all |
| Root cause | Does not remove `fork` on macOS; masks burst + MT-fork risk |
| vs node-pty / Apple guidance | Opposite direction from industry practice on Darwin |

**Verdict:** acceptable **short-lived** branch to unblock CI investigation; **not** the merge target for `main`.

## Target design (decision)

| Item | Decision |
|---|---|
| macOS spawn | `posix_openpt` + `posix_spawn` (+ spawn-helper or equivalent controlling-TTY step) |
| Linux / FreeBSD | Keep `forkpty` + existing native `execve` shim |
| Interim mutex | Remove when macOS `posix_spawn` path is green; do not merge mutex to `main` without `#if __APPLE__` review |
| Transient errors | Bounded retry on `EAGAIN` / `ENOMEM` on **macOS spawn only** (4 attempts, 25 ms × attempt backoff) — **in initial PR** |
| CI test parallelism | Keep parallel TUnit — spawn path should tolerate it |
| CI-only concurrency cap | Optional second layer (`TUNIT_MAX_PARALLELISM` on macOS job only); not a substitute for library fix |

## Grill decisions (2026-06-26)

| # | Decision |
|---|---|
| 1 | Revert `flasky` mutex; keep **errno via native return value** only |
| 2 | Implement on **`macos` branch** from `main` (not `flasky`) |
| 3 | macOS: **`posix_spawn` + `minipty_spawn_helper`** (controlling TTY) |
| 4 | Helper language: **C (`.c`)** via `cc`; share `minipty_unix_exec.c` with dylib — not node-pty `.cc` |
| 5 | Helper protocol (MiniPty-specific): `posix_spawn(..., argv, envp)` with `argv = [helper, file, args…]`; **`MINIPTY_CWD`** injected into `envp`, stripped before child `execve` |
| 6 | Helper path: **`dladdr` on dylib → sibling `minipty_spawn_helper`** in same `runtimes/.../native/` directory |
| 7 | Commits: **(1) errno return**, **(2) macOS spawn + retry + build** |
| 8 | **EAGAIN/ENOMEM retry** included in spawn commit (macOS only) |

## Retry policy (macOS spawn)

Independent of `posix_spawn`, a small retry loop in the native shim helps peak-rate flakes without global serialization:

```c
/* Pseudocode — not final API */
for (attempt = 0; attempt < 4; attempt++) {
    if (spawn_attempt(...) == 0) return 0;
    if (errno != EAGAIN && errno != ENOMEM) return errno;
    usleep(25 * (attempt + 1) * 1000);
}
```

Use on macOS first; add to Linux only if measurements justify it. Do not retry non-transient errno (`EINVAL`, `ENOENT`, etc.).

## Non-goals

- Changing public `Pty.Start` / `PtyStartInfo` API.
- Replacing Linux `forkpty` with `posix_spawn` (no demonstrated need).
- Serializing the full test suite on macOS CI as the primary fix.
- Bundling winpty or external PTY packages.
- Golden-byte spawn tests; keep property-based assertions per [platform_support.md](../specs/platform_support.md).

## Constraints

- NativeAOT-safe; no new NuGet dependencies.
- Preserve Unix `execve` + PATH + plain-script fallback behavior.
- macOS resize tests (`stty size`, child-visible resize) must remain reliable — controlling TTY is **required**.
- All tests pass on macOS arm64 + x64 CI matrix after change.
- `scripts/build-native.sh` / CI `build-unix-native` must produce artifacts for `osx-arm64` and `osx-x64` (dylib + any helper binary).

## Verification on macOS (dev machine)

Record machine OS version, `kern.tty.ptmx_max`, and results in this file when running locally.

### Baseline diagnostics

```bash
sysctl kern.tty.ptmx_max
dotnet test tests/MiniPty.Tests/MiniPty.Tests.csproj -c Release
```

### Reproduce flake (pre-fix)

Checkout `main` (or `cf4afb4` without mutex). Run repeatedly:

```bash
for i in $(seq 1 30); do
  echo "=== run $i ==="
  dotnet test tests/MiniPty.Tests/MiniPty.Tests.csproj -c Release || break
done
```

Note failing test name and `errno` in `PTY spawn failed (errno N)`.

### PTY pressure check

During / after a test run:

```bash
lsof /dev/ptmx 2>/dev/null | wc -l
```

If count approaches `kern.tty.ptmx_max` without returning to baseline, investigate fd lifecycle (`UnixPtyBackend.Dispose` → `close(master)`).

### Stress test (add during PR if useful)

Parallel spawn from one process (validates mutex removal):

```csharp
// Conceptual — add as focused test or local scratch
await Task.WhenAll(Enumerable.Range(0, 16).Select(async _ => {
    await using var s = Pty.Start(new PtyStartInfo { FileName = "echo", Arguments = ["ok"] });
    await s.CompleteAsync();
}));
```

### CI parity

Push to PR branch; confirm both matrix legs pass:

- `macos-26` / `osx-arm64`
- `macos-26-intel` / `osx-x64`

Workflow: `.github/workflows/build.yaml` → `dotnet test -c Release` (parallel, no extra env today).

## Commit plan (single PR, macOS-focused)

Implement on a branch from `main` (not from `flasky` mutex unless cherry-picking errno fix only). Stop and run gates after each commit.

### Commit 0 — Cherry-pick errno propagation only (if not already on `main`)

**Files:** `minipty_unix.c`, `UnixPtyBackend.cs`

- Return errno from `minipty_fork_pty_exec`; throw with `spawnError` in C#.
- **Do not** include `minipty_fork_lock` unless macOS `posix_spawn` is delayed.

**Gate:** `dotnet test -c Release` on macOS.

---

### Commit 1 — macOS spawn backend skeleton

**Scope:** `#if defined(__APPLE__)` path in `minipty_unix.c` (or separate `minipty_unix_darwin.c` if clarity wins).

- `posix_openpt` / `grantpt` / `unlockpt`
- `TIOCPTYGNAME` for slave path
- `posix_spawn` file actions: dup2 slave → stdio, close slave + master in child
- `posix_spawnattr`: `POSIX_SPAWN_SETSID`, sigdefault, sigmask (mirror node-pty flags)

**Gate:** simplest spawn test (`echo hello`) on macOS; Linux CI unchanged.

---

### Commit 2 — spawn-helper + controlling TTY

**Scope:** Build and bundle small helper (node-pty `spawn-helper.cc` as reference).

- Helper: `open(ttyname(STDIN), O_RDWR)` → controlling tty → `chdir` → `execvp` / `execve`
- Parent passes `cwd`, target `file`, `argv`, `envp` through helper protocol (node-pty uses `argv[0]=helper`, `argv[1]=cwd`, `argv[2]=file`, `argv[3..]=args`)
- Wire `scripts/build-native.sh` + pack layout under `runtimes/osx-*/native/`
- C# backend locates helper next to dylib (document resolution rules)

**Gate:** TTY detection tests (`redirected=False`, `isatty`); `stty size` resize probes; full macOS test suite.

---

### Commit 3 — exec semantics parity

**Scope:** Plain-script fallback, PATH lookup, explicit `envp` — either in helper after exec failure or pre-resolve in parent (match current `minipty_execvpe` behavior).

**Gate:** Full test suite macOS + Linux; script / `noexec` / PATH overlay tests.

---

### Commit 4 — Remove interim mutex (if present) + optional retry

- Delete `minipty_fork_lock` from Unix path.
- Add bounded `EAGAIN`/`ENOMEM` retry on macOS spawn only if flake persists in 30× local runs.

**Gate:** 30× local `dotnet test` loop on macOS; CI green.

## Success criteria (PR end)

| Criterion | Required |
|---|---|
| macOS CI (`osx-arm64`, `osx-x64`) parallel tests | Green |
| Linux / FreeBSD CI | Green, no spawn regression |
| No global `forkpty` mutex on non-Apple platforms | Yes |
| Controlling TTY / resize / TTY detection tests | Pass |
| `platform_support.md` lesson updated | Yes — document macOS `posix_spawn` + helper |
| `pty_crossplatform.md` spawn section | Updated for Darwin split |

## Open questions (resolve on macOS machine)

Fill answers in this section during implementation.

| # | Question | Decision |
|---|---|---|
| 1 | Helper binary name / layout | `minipty_spawn_helper` next to `libminipty_unix.dylib` under `runtimes/osx-*/native/` |
| 2 | Helper protocol | MiniPty-specific: `argv=[helper,file,args…]`; `MINIPTY_CWD` in `envp` (not node-pty argv layout) |
| 3 | `execve` in helper vs parent | Helper runs `minipty_execvpe` after controlling-TTY `open`; same exec module as Linux fork child |
| 4 | Retry loop | macOS spawn only, 4 attempts, 25 ms × attempt backoff |
| 5 | Interim mutex | **No** — not merged |

## Lessons (implementation)

| Lesson | Detail |
|---|---|
| Interim mutex is not test serialization | Commit message is misleading; lock is in native shim. |
| errno via return value | P/Invoke `SetLastError` was wrong for this native boundary; keep return-value errno. |
| Prior `posix_spawn` failed on controlling TTY | node-pty spawn-helper pattern is the known fix; see `spawn-helper.cc`. |
| node-pty uses `posix_spawn` on macOS only | Linux keeps `forkpty`; MiniPty should mirror the split. |
| Parallel tests are intentional | [plan_minipty_next.md](plan_minipty_next.md) removed broad `[NotInParallel]`; spawn path must cope. |

## Post-merge

- Update [platform_support.md](../specs/platform_support.md): macOS row + lessons (controlling TTY via helper, not `forkpty`).
- Update [pty_crossplatform.md](../references/pty_crossplatform.md): Unix spawn section split by OS.
- Close / supersede branch `flasky` mutex approach.
- Link outcome here from [plan_minipty_next.md](plan_minipty_next.md) if spawn work unblocks a milestone gate.
