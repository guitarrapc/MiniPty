# Release Gate Plan

Goal: resolve the items below before the next release, the first since `v1.3.0` (see [next_todo.md](../next_todo.md#release)). They came from reviewing MiniPty as a general-purpose PTY library against the reference libraries under `.references/`: node-pty 1.1.0, Porta.Pty 2.2.3, WezTerm's `portable-pty` 0.9.0, and creack/pty. Each gate is closed by a fix, or by a decision recorded in the specifications, as its own section says.

**Status:** open (2026-10-27). R1 and R2 are implemented and verified locally on Linux and macOS (arm64) (2026-10-27); R3–R5 are open.

## Gate Inventory

| ID | Kind | Gate | Closed when |
|---|---|---|---|
| R1 | Bug | The Unix program lookup runs files through `/bin/sh` that `execvp` would refuse | Only `ENOEXEC` falls back to `sh`, with tests and documents updated |
| R2 | Behavior | A Unix start that cannot `exec` or `chdir` succeeds and the child exits 127 or 126, where Windows throws from `Pty.Start` | `Pty.Start` throws on Unix too, or the asymmetry is kept by a recorded decision |
| R3 | Documentation | The README and the specification say MiniPty bundles no helpers, while macOS ships `minipty_spawn_helper` | Both state what ships on macOS |
| R4 | Decision | Features other PTY libraries offer that MiniPty lacks | Each one is implemented, or recorded as a non-goal with its reason |
| R5 | Documentation | The documented reason for holding the Unix exec payload until disposal contradicts `fork` semantics | The reason is corrected or replaced by the observed cause |

## R1: The Unix lookup's `sh` fallback

### Finding

When `execve` fails, `minipty_execve_compat` ([minipty_unix_exec.c:121](../../../src/MiniPty/Native/minipty_unix_exec.c)) runs the same file through `/bin/sh` (then `/usr/bin/sh`) for any of three errors:

- `ENOEXEC`
- `EACCES`
- `ENOENT` when the file exists

The `PATH` search calls it for every entry, and the `sh` that it starts does not return. The result:

- **A file without execute permission runs.** If an earlier `PATH` entry holds a non-executable file with the program's name, `sh` reads it as a script. The search stops there and never reaches the executable in a later entry.
- **A missing interpreter is replaced.** A script whose shebang names an interpreter that does not exist runs under `sh` instead of failing.

glibc's `execvp` and POSIX fall back to `sh` only for `ENOEXEC`. They treat `EACCES` as "try the next entry", and report `EACCES` at the end only when nothing else was found. The shim already tracks that (`saw_eacces`), but the fallback runs first.

Reproduced on WSL (Ubuntu) on 2026-10-27 by compiling `minipty_unix_exec.c` with a driver that calls `minipty_execvpe`:

| Case | MiniPty | `env` (`execvp`) |
|---|---|---|
| `PATH=d1:d2`. `d1/probe-target` has mode 644 and contains `echo WRONG`. `d2/probe-target` has mode 755 and runs `echo RIGHT` | Printed `WRONG`: ran the non-executable file through `sh` | Printed `RIGHT` |
| `PATH=d3`. `d3/probe-target` has mode 755 and the shebang `#!/no/such/interpreter` | Printed `WRONG`: ran under `sh` | Fails with `ENOENT` |

The macOS helper resolves the program with the same function, so macOS behaves the same.

### Why it matters

- The program that runs depends on files that `execvp`, a shell, and .NET's `Process.Start` all skip. That contradicts the program-lookup guidance in the README, which tells callers that `PATH` chooses the program.
- A missing interpreter surfaces as `sh` syntax errors from the script's body, after part of the script may already have run under the wrong interpreter.
- The `EACCES` fallback was added on purpose, for scripts on `noexec` mounts ([pty_crossplatform.md](../references/pty_crossplatform.md#plain-scripts-and-shell-fallback)). `noexec` is an administrator's control, and a general-purpose library should not work around it for every caller. A caller who wants that can start `sh` with the script as its argument.

### Decision

- `sh` fallback for `ENOEXEC` only, as `execvp` does.
- `EACCES` continues the `PATH` search, and is reported only if no entry succeeds.
- A missing interpreter fails with `ENOENT`.

### Behavior change for the release note

- A script on a `noexec` mount, started directly, now fails instead of running through `sh`.
- A script whose interpreter is missing now fails instead of running under `sh`.

### Result (implemented 2026-10-27)

- `minipty_execve_compat` falls back to `sh` for `ENOEXEC` only. When no `sh` exists, it reports `ENOEXEC` instead of the shell's `ENOENT`, so a `PATH` search does not mistake the script for a missing file.
- The compiled shim was checked on WSL with the four cases below. All matched `execvp`:
  - A non-executable file earlier on `PATH`, then an executable: the executable ran.
  - A missing interpreter: `ENOENT`.
  - A plain script: ran through `sh`.
  - A non-executable file as the only match: `EACCES`.
- The documented reason is updated in [pty_crossplatform.md](../references/pty_crossplatform.md#plain-scripts-and-shell-fallback).

### Tests

These run on Unix, through `Pty.Start`, in `PtyTests`:

- `PtyUnixPathLookupSkipsFilesWithoutExecutePermission`: a non-executable file earlier on `PATH` is skipped, and the executable in a later entry runs.
- `PtyUnixPathLookupFailsWhenShebangInterpreterMissing` replaces the test that asserted the old fallback. A script with a missing interpreter does not run (with R2, the start fails with `ENOENT`).
- `PtyUnixFileWithoutExecutePermissionDoesNotStart`: a non-executable file, found on `PATH` or named by its full path, does not run (with R2, the start fails with `EACCES`).
- `PtyUnixPathLookupUsesOverlayAndFallsBackToShellForPlainScripts` is unchanged. A plain script without a shebang still runs through `sh`; this is the `ENOEXEC` path, which stays.

### Lesson

The shim ends the child with 127 for every exec failure, so the exit status alone cannot tell `EACCES` from `ENOENT`. R2's report surfaces the `errno`, and the R1 tests now assert it.

## R2: Unix start failures are not reported by `Pty.Start`

### Finding

- **Windows:** `CreateProcess` fails inside `Pty.Start`, so a missing program or working directory throws `Win32Exception` (`ERROR_FILE_NOT_FOUND`, `ERROR_DIRECTORY`).
- **Unix:** `exec` and `chdir` run in the child after the fork. A failure there ends the child with 127 or 126, and `Pty.Start` succeeds ([lifecycle.md](../specs/lifecycle.md), failure table; [PtyBaselineTests.cs](../../../tests/MiniPty.Tests/PtyBaselineTests.cs) asserts 127). On macOS the helper exits the same way.
- `Pty.Start` documents `IOException` for a Unix exec failure, which today is never thrown.

The reference libraries split on this:

| Reports the failure at start | Exits with a status |
|---|---|
| Go's `os/exec` (creack/pty), Rust's `std::process::Command` (`portable-pty`), and .NET's own `Process.Start` on Unix | node-pty (`_exit(1)`), Porta.Pty (`_exit(errno)`) |

The first group writes the child's `errno` to a close-on-exec pipe. A successful `exec` closes the pipe empty, and a failure sends the error before the child exits.

### Why it matters

- A caller cannot tell a missing program from a program that exits 127 or 126 itself.
- The same `PtyStartInfo` fails differently per platform, so cross-platform callers need two error paths.

### Decision

- **Report.** Unix `exec` and `chdir` failures fail `Pty.Start`.
- **`Win32Exception` carrying the `errno`.** `Process.Start` throws it on Unix, Windows already throws it, and a cross-platform caller catches one type. Failures before a child exists (opening the terminal, forking) stay `IOException`; the documentation that promised `IOException` for exec failures is corrected.

### Constraints known from earlier work

- **The child must never wait on the parent.** Holding a forked child until the parent signals it deadlocked concurrent spawns, because each held child had inherited another spawn's pipe ([minipty_unix.c](../../../src/MiniPty/Native/minipty_unix.c), comment before `minipty_pidfd_open`).
  - An error pipe that only the child writes keeps that rule. The parent reads until end of file.
  - A concurrent spawn's child that inherited the write end closes it at its own `exec`. That can delay the parent's read but not block it.
- **macOS needs the helper to report too.** The parent spawns `minipty_spawn_helper` with `posix_spawn`, and the `chdir` and `exec` that can fail run inside the helper.
- **Linux `forkpty` cannot open the pipe for the caller.** The pipe needs `O_CLOEXEC` from creation (`pipe2`), not `fcntl` afterwards, for the reason recorded in [lifecycle.md](../specs/lifecycle.md) (descriptors marked after opening leak to concurrent `Process.Start`).

### Result (implemented 2026-10-27)

- **Mechanism:** a close-on-exec report pipe ([Start failure report](../references/pty_crossplatform.md#start-failure-report)).
  - A child that cannot `chdir` or exec writes the stage and `errno` before `_exit`.
  - The parent reads until end of file or a report.
  - On a report, the parent reaps the child, closes the master, the slave, and the pidfd, and returns the `errno` with the stage.
  - On macOS the helper receives the write end as descriptor 3.
- **The wait is bounded at 10 s.** It matters only when another process keeps the write end. On macOS, which has no `pipe2`, a concurrent fork can keep it for good. When the wait gives up, the start succeeds as before, and a failure shows as the exit status.
- **macOS does not retry a reported failure.** Its transient-error retry (`EAGAIN`, `ENOMEM`, `ENXIO`) would otherwise rerun a start whose exec failed with `ENOMEM`.
- **Documents updated:**
  - [pty_crossplatform.md](../references/pty_crossplatform.md): new section, the flows, the helper table.
  - [lifecycle.md](../specs/lifecycle.md): failure table.
  - [core_session.md](../specs/core_session.md): `FileName` row.
  - [platform_support.md](../specs/platform_support.md): the `noexec` row.
  - The `Pty.Start` exception documentation.
  - The README never described the old behavior.

### Tests

- `StartFailureIsReported` now expects `Win32Exception` on every platform. On Unix it expects `ENOENT` for a missing program and for a missing directory, and checks that the messages name the program and the working directory. It also checks that a child that exits 127 or 126 by itself starts normally.
- `StartFailureLeaksNothing` fails starts on every platform and checks descriptor counts. On Linux it also checks that the test process has no zombie children afterwards: a failed child that the start did not reap would stay one, since no watcher registers it.
- The R1 tests now expect the start to fail:
  - a missing interpreter fails with `ENOENT`;
  - a non-executable file fails with `EACCES`.

### macOS verification (2026-10-27)

Run locally on macOS (arm64, Apple clang 21, .NET 10.0.402):

- **Build:** the shim and the helper compile without warnings, also with `-Wall -Wextra -Wpedantic -Wshadow -Wconversion`.
- **Tests:** the full suite passes (676 tests).
  - No start-failure test comes near the 10 s bound. The R1 tests take about 30 ms each, and `StartFailureIsReported` takes 0.44 s, most of it the two children that exit 127 and 126 by themselves.
  - Descriptor counts do not change.
  - The zombie check runs only on Linux. A driver that called `minipty_fork_pty_exec` directly found no unreaped child instead.
- **Cost:** a start now returns only after the helper has execed the target, where it used to return once the helper was spawned. The start cannot succeed before the exec does, so this is the price of the report.
  - Measured through `minipty_fork_pty_exec`: about 2.8 ms per start against 0.65 ms before, about 2 ms more. The same holds for starts that succeed and starts that fail.
  - 8 threads starting at once took about the same total time as before (412 ms against 449 ms).
  - The full suite took about 32 s, against 29–30 s before.
  - The cost on Linux was not measured.
- **The bounded wait:**
  - 7,200 concurrent starts never waited over 1 s, with about 3,500 plain `fork` + `exec` calls on other threads, the calls `Process.Start` makes on macOS.
  - A test build then slept 20 ms between `pipe()` and `FD_CLOEXEC`, so that the forked processes kept the write end.
  - A start that succeeds then waited until that process exited (2 s), or for the 10 s bound, and still succeeded.
  - A start that fails was still reported at once, because the report arrives before end of file.

**Lesson:** a leaked write end delays only starts that succeed. A start failure that takes about 10 s means the helper did not write its report, not that another process holds the pipe.

### Behavior change for the release note

On Unix, `Pty.Start` throws `Win32Exception` for a program that cannot be executed or a working directory that cannot be entered, where it used to return a session whose child exited 127 or 126.

## R3: "No bundled helpers"

### Finding

- **[README.md](../../../README.md) (Features):** "NativeAOT ready, in-process backends only, no winpty or bundled helpers".
- **[spec.md](../spec.md) (Motivation):** "without bundling winpty or external helpers".
- **The package on macOS:** ships `minipty_spawn_helper` in `runtimes/osx-*/native/`, and every macOS session starts through it ([platform_support.md](../specs/platform_support.md)).

### Decision

State what ships. The helper is a launcher that replaces itself with the child by `exec`; no helper process stays running per session.

- Windows needs nothing beyond the OS.
- Linux and FreeBSD need the native shim library.
- macOS needs the shim library and the launcher.
- No winpty, on any platform.

## R4: Features other libraries offer

### Inventory

| Feature | Offered by | MiniPty today |
|---|---|---|
| Read and change termios | `portable-pty` (`get_termios`, read-only); creack/pty points to `x/term` | Fixed defaults with `IUTF8`; no API |
| tty name of the slave | node-pty (`ptsName`), `portable-pty` (`tty_name`), creack/pty (`Name`) | None |
| `uid` / `gid` on spawn | node-pty (not on macOS) | Non-goal N10 in [terminal.md](../specs/terminal.md#non-goals) |
| `Kill(PtySignal)` on Windows | node-pty kills the console's process list; no library delivers a signal | Always terminates, whatever the signal |
| `ActiveProcessName` on Windows | node-pty (from its own process lookup) | Always null; the reason is recorded in [terminal.md](../specs/terminal.md) and [core_session.md](../specs/core_session.md) |

### Decision required

For each row, choose one:

- Implement it for this release.
- Record it as a non-goal with its reason: a new row in [terminal.md](../specs/terminal.md#non-goals), or a confirmation of the existing one.

Points to weigh:

- **termios and tty name:** Both are Unix-only, and MiniPty's defaults (`IUTF8`, erase) were chosen for correctness ([pty_crossplatform.md](../references/pty_crossplatform.md)). A read-only accessor costs less than a setter, and is enough for a caller that needs to check echo or canonical mode.
- **`uid` / `gid`:** N10 already rejects them as security-sensitive. Confirm that, or say what changed.
- **`Kill(PtySignal)` on Windows:**
  - Windows has no signals.
  - `SIGINT` could be delivered as Ctrl+C written to the input, which ConPTY turns into `CTRL_C_EVENT` for the processes in its console. That is what a user's keystroke does, but it waits behind queued input.
  - The alternative is to document that every signal terminates, as today.
- **`ActiveProcessName` on Windows:** node-pty starts a separate process to list a console's processes. A lookup inside MiniPty would need a process-tree snapshot, which has no foreground notion on Windows.

## R5: The stated reason for holding the exec payload

### Finding

[lifecycle.md](../specs/lifecycle.md) ("Linux `forkpty` exec payloads must outlive the pre-exec child") and [pty_crossplatform.md](../references/pty_crossplatform.md) (`forkpty()` safety) say the parent must hold `file`, `argv`, `envp`, and `cwd` until disposal. Their reason is that freeing them right after `forkpty` returns races the child, observed as exit 127 and wrong working directories under parallel CI load.

`fork` gives the child a copy-on-write image of the parent's memory as of the fork. Once `forkpty` has returned in the parent, the child's copy exists, and the parent freeing its own copy cannot change what the child reads.

The flake was fixed in [#32](https://github.com/guitarrapc/MiniPty/pull/32) together with other changes (transport pump readiness, post-exit drain), so the hold may have coincided with the fix rather than caused it.

### Why it matters

Holding the payload is harmless: a few hundred bytes per session. But the documented reason teaches a model of `fork` that is wrong, and a later change could rely on it, for example:

- moving the spawn to `vfork` or `posix_spawn`, where the parent's memory is shared until `exec`;
- adding a step in the child that does depend on timing.

### Decision required

Re-verify the cause, then do one of the following:

- Correct both documents to the cause that is observed, and keep or drop the hold accordingly.
- If the cause cannot be found, state that the hold is kept as a precaution without a known mechanism.

## Reviewed and Not a Gate

- **MiniPty.Capture and MiniPty.Terminal use MiniPty's internals** (`InternalsVisibleTo`). The packages are released and updated together, so a version mismatch between them does not arise in practice. This settles the version-pin question in [next_todo.md](../next_todo.md#release).
