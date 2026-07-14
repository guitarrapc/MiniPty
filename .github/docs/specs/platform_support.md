# Platform Support Specification

Implemented public platform support, verification constraints, and platform-level lessons.

## Supported Platforms

| OS | Backend | Minimum |
|---|---|---|
| Windows | ConPTY (`CreatePseudoConsole`) | Windows 10 1809+, Windows 11 |
| Linux | `forkpty` + `execve` through native shim | Common glibc/musl targets |
| macOS | `posix_spawn` + `minipty_spawn_helper` through native shim | Supported runners; helper bundled in `runtimes/osx-*/native/` |
| FreeBSD | `forkpty` + `execve` through native shim | `libutil` |

Pipe redirect without ConPTY is not a PTY. On Windows, TUI tools require ConPTY-backed spawn.

MiniPty does not use winpty or third-party PTY binaries. Backends are in-process interop plus the small Unix native shim.

## Verification

CI runs `tests/MiniPty.Tests` on Linux, macOS, and Windows.

| Check | Why |
|---|---|
| TTY detection (`redirected=False`, `isatty`) | Confirms real PTY semantics. |
| Simple command output | Confirms spawn and decode. |
| Stdin + EOF (`cat`, `sort`) | Confirms staged EOF. |
| Empty stdin EOF | Confirms deferred EOF with no bytes. |
| Multiple capture chunks + ANSI | Confirms concurrent read and timestamps. |
| `WaitForExitAsync` cancel vs `CompleteAsync` cancel | Confirms cancellation contract. |
| Resize | Confirms `TIOCSWINSZ` / `ResizePseudoConsole`. |

Tests use property assertions, not golden byte captures, to reduce OS and timing flake.

Cross-OS integration benchmarks (`*PtyIntegrationBenchmarks*`, especially `*32KiB*`) use `MiniPty.Benchmarks.Child` for bulk stdout: a shell-free child that writes exactly *n* zero bytes in 4 KiB chunks (`--bytes <n>`). `Echo` and `Exit0` scenarios still use lightweight shell commands where small spawn asymmetry is acceptable. `dotnet build -c Release` copies the child next to the benchmark assembly; missing child fails fast with a rebuild hint.

Allocation regressions are checked with `scripts/compare-benchmark-allocations.ps1` against
`BenchmarkDotNet.Artifacts/baselines/integration.json`; the gate is allocated bytes less than or
equal to the recorded baseline. Refresh that baseline only when an intentional lifecycle change
alters the measured path, and record the reason. The current one-shot baseline includes the bounded
post-exit quiet polling and drain behavior; persistent streaming and capture remain separate
benchmark categories because they use different orchestration.

## Platform-specific Test Constraints

These are verification choices, not API requirements, but they document pitfalls that broke CI when ignored.

| OS | Pitfall | Mitigation in tests |
|---|---|---|
| Linux | `bash -lc` on a PTY often stays interactive after `-c` completes. | Prefer `sh -c`, or spawn utilities directly. |
| Linux | A single EOT with no trailing newline does not signal EOF in canonical mode. | One-shot stdin tests using EOT end input with `\n` before `SendEof()`. |
| Windows | Ctrl+Z without a preceding line submit does not signal EOF for line-oriented readers. | `SendEof` submits a pending line with CR when input lacks a trailing `\r`/`\n`, then sends Ctrl+Z + CR; `PtyStdinEof_withoutTrailingNewline` covers Windows. |
| Linux | GNU `stty rows` / `stty columns` without arguments set size instead of printing it. | Query via `stty size`. |
| macOS | Spawn paths that do not attach a controlling terminal make `stty` and resize probes unreliable. | macOS uses `posix_spawn` + `minipty_spawn_helper` (controlling TTY via slave `open`); Linux/FreeBSD use `forkpty`. |
| macOS ARM | Variadic `ioctl` for `TIOCSWINSZ` is unsafe to P/Invoke directly. | Resize runs in `libminipty_unix`. |
| Windows | Closing ConPTY stdin while a child is still attaching can yield `STATUS_CONTROL_C_EXIT`. | Empty-stdin EOF is staged; one-shot writes use Ctrl+Z + CR stream EOF instead of wait-loop pipe close. |
| Windows | Input pipe close after bytes were written yields `STATUS_CONTROL_C_EXIT` for direct stdin readers (`sort`, `more`). | `SendEof` writes Ctrl+Z + CR and keeps the pipe open until exit; tests assert ExitCode 0 for `sort`. |
| Windows | `pwsh` is optional on runners. | Prefer built-in Windows PowerShell unless pwsh-only behavior is needed. |
| Linux (CI) | GitHub-hosted runners (including `ubuntu-24.04-arm`) may mount `/tmp` with `noexec`. Shebang execution of scripts under `/tmp` can fail with exit **127** even when `sh script` would work. | Integration tests that spawn executable fixtures use a directory under the test output (`AppContext.BaseDirectory`), not `Path.GetTempPath()`. PATH-overlay tests for plain scripts exercise the `ENOEXEC` → `sh` fallback, not shebang exec. |
| Linux (CI) | `PtyUnixPathLookupTreatsEmptyPathEntriesAsCurrentDirectory` can still flake as exit **127** on `ubuntu-24.04-arm` under parallel full-suite load. | `[Retry(3)]` on that test; keep fixtures under `AppContext.BaseDirectory` (see row above). |

## Lessons Learned

- **Pipe redirect is not a PTY.** Redirected stdin/stdout captures bytes but children report not-a-TTY.
- **winpty is a poor fit for NativeAOT single-binary goals.** Bundled helpers add environment dependency; in-process ConPTY avoids that.
- **macOS spawn must establish a controlling terminal.** A bare `posix_openpt` + `posix_spawn` path left the slave without a controlling tty. macOS uses `posix_spawn` of `minipty_spawn_helper`, which `open`s the slave from `ttyname(STDIN)` before `execve`. Linux and FreeBSD keep `forkpty` + native `execve`.
- **macOS uses `posix_spawn` instead of `forkpty`.** Parallel CI and embedding hosts (.NET thread pool, many concurrent `Pty.Start`) hit burst PTY limits and fragile `fork` from a multithreaded parent. A global mutex around `forkpty` stabilizes CI but serializes every in-process spawn and was rejected; Darwin follows node-pty with `posix_spawn` + helper.
- **`minipty_fork_pty_exec` returns spawn errno.** Failures return a positive errno to managed code; do not rely on `SetLastError` across the native shim boundary.
- **Linux fork child scrubs inherited fds; FreeBSD fd scrub is follow-up.** After `forkpty`, Linux closes or marks close-on-exec on inherited descriptors `>= 3` and resets signal handlers to `SIG_DFL` before exec. FreeBSD gets signal reset only; inherited-fd close (`closefrom` or equivalent) remains open work — see [pty_crossplatform.md](../references/pty_crossplatform.md) → Fork child hygiene.
- **Linux `forkpty` exec payloads must outlive the pre-exec child.** Freeing managed UTF-8 blocks immediately after `forkpty` returns races the child under parallel CI and flakes as exit **127**. Hold the payload until session/backend disposal; see [lifecycle.md](lifecycle.md) and [pty_crossplatform.md](../references/pty_crossplatform.md) → `forkpty()` safety.
- **macOS one-shot capture needs a prompt transport pump.** Under burst parallel CI, `Task.Run` can queue the blocking PTY read too late and fast children close the slave before output is captured (`PtyUnixParallelSpawnCompletes`). macOS queues the pump via `UnsafeQueueUserWorkItem(preferLocal: true)`; a caller-side wait loop was rejected (thread-pool starvation / cancel-before-kill). See [pty_crossplatform.md](../references/pty_crossplatform.md) → One-shot transport pump scheduling.
- **Explicit Unix environments require `execve`.** Passing `envp` means MiniPty cannot rely on plain `execvp`; the native shim provides portable path lookup before `execve`.
- **Unix plain-script fallback differs from shebang exec.** `ENOEXEC` / `EACCES` / missing-interpreter `ENOENT` retry via `/bin/sh` then `/usr/bin/sh`. Shebang execution is attempted first and is sensitive to `noexec` mounts; see [pty_crossplatform.md](../references/pty_crossplatform.md) → Plain scripts and shell fallback.
- **Do not assume `/tmp` is exec-enabled on Linux CI.** Hardened runners treat `/tmp` as data-only; spawn tests that need executable fixtures belong under the build output tree or another exec-enabled path.
- **Capture timing requires concurrent reads.** Reading only after exit loses TUI animation timing.
- **PTY output includes terminal echo.** Tests that drive stdin manually may capture echoed input and control characters.
- **Raw PTY text can break the parent console.** Use [Display text](display_text.md) helpers for logs or keep escaped/raw bytes for inspection.
- **Child-visible resize tests must synchronize with the parent.** Block the child until after parent `Resize` when asserting child-visible size.
- **Cross-OS integration benchmarks need the same bulk child on every OS.** OS-specific children (for example PowerShell `[string]::new` on Windows vs `head -c` on Unix) skew latency, bytes on the wire, and ConPTY read granularity, so CI compared harness noise instead of library cost. `MiniPty.Benchmarks.Child` isolates measurement; library-side ConPTY coalescing and drain polling are separate concerns documented in [core_session.md](core_session.md) and [pty_crossplatform.md](../references/pty_crossplatform.md).
- **Benchmark persistent, one-shot, and capture paths separately.** They intentionally differ in pump ownership, buffering, materialization, and drain behavior; collapsing them into one number hides regressions and can reward an implementation that violates another path's contract.
