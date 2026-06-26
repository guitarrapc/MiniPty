# Plan: Linux native fork hygiene (inherited FDs + signal reset)

Follow-up native work for the Linux / FreeBSD **`forkpty`** spawn path. This is a **plan**, not an implemented API contract. After merge, record lessons learned in [pty_crossplatform.md](../references/pty_crossplatform.md).

**Delivery:** **Separate PR** from macOS spawn hardening ([plan_macos_native.md](plan_macos_native.md)) and from any termios work. Do not implement on the same branch as macOS-only changes.

Related: node-pty `src/unix/pty.cc` (`PtyFork` child branch, `pty_close_inherited_fds`).

## Summary

| Item | Choice |
|---|---|
| **Problem** | After `forkpty`, the child inherits the parent's full fd table and signal dispositions. MiniPty does not sanitize either before `chdir` / `execve`. |
| **Scope** | **`spawn_pty_child_forkpty`** in `minipty_unix.c` (Linux + FreeBSD). macOS uses `posix_spawn` and is out of scope. |
| **Change 1** | **Mark inherited fds ≥ 3 with `FD_CLOEXEC`** in the child before exec (Linux only; node-pty `pty_close_inherited_fds` parity). |
| **Change 2** | **Reset all signal handlers to `SIG_DFL`** in the child before mask restore (all `forkpty` platforms: Linux + FreeBSD). |
| **Out of scope** | `uid` / `gid`, termios, parent master `O_NONBLOCK`, changing exec / env semantics, FreeBSD inherited-fd hygiene (follow-up). |
| **Merge gate** | All tests green on Linux CI; FreeBSD unchanged or improved; benchmarks within threshold; no public API change. |

## Background

### Why macOS is unaffected

macOS spawn uses `posix_spawn` of `minipty_spawn_helper` — a **new process image** with fd actions defined by `posix_spawn_file_actions_t`. The hygiene issues below apply to **`fork(2)`**-style children that inherit the parent's address space and fd table until `execve`.

### 1. Inherited file descriptors (Linux)

When `forkpty` succeeds in the child (`pid == 0`), the child receives a **copy of every open fd** in the parent, including:

- The PTY **master** fd (opened in the parent before fork).
- Sockets, files, and pipes held by the .NET test host or embedding application.

node-pty marks descriptors **`>= 3`** with `FD_CLOEXEC` in the child before `execvp`:

```c
#if defined(__linux__)
pty_close_inherited_fds();  // close_range(3, ~0, CLOSE_RANGE_CLOEXEC) or FD_CLOEXEC scan
#endif
```

`CLOSE_RANGE_CLOEXEC` and the fallback `SetCloseOnExec` loop **do not call `close(2)`** — they set `FD_CLOEXEC` so `execve` drops those fds from the new image. Without this step, a spawned shell or tool can **inherit sensitive handles** (log files, network sockets, other PTY masters) and accidentally duplicate I/O or leak resources.

MiniPty opens the master via `forkpty` in the parent and does not scrub the child fd table today.

### 2. Inherited signal handlers (Linux + FreeBSD)

node-pty resets signal dispositions in the fork child **before** restoring the parent's signal mask:

```c
if (!pid) {
    for (int i = 0; i < NSIG; i++)
        sigaction(i, &sig_action_with_SIG_DFL, NULL);
}
pthread_sigmask(SIG_SETMASK, &oldmask, NULL);
```

Rationale (from node-pty comments): avoid running **parent-registered signal handlers** in the child after fork and before `execve`. In a multithreaded parent (.NET runtime, thread pool, GC), custom handlers intended for the parent process must not run in the short-lived fork child.

MiniPty already blocks signals with `pthread_sigmask` around **`forkpty` in the parent** (same as node-pty). It does **not** reset handlers in the child. The child path is short (`chdir` → `minipty_execvpe` → `_exit`), but a signal delivered after mask restore and before reset could still invoke a parent handler in the child address space.

### node-pty platform split

| Hygiene | Linux | FreeBSD (forkpty) | macOS (posix_spawn) |
|---|---|---|---|
| `pty_close_inherited_fds` | Yes | No in node-pty | N/A |
| Signal reset in child | Yes | Yes | N/A (spawn attrs) |

MiniPty scope (decided):

- **Signal reset:** Linux + FreeBSD (`forkpty` platforms).
- **Inherited FD CLOEXEC:** **Linux only** (`#if defined(__linux__)`). FreeBSD follow-up PR if needed (`closefrom(3)` or equivalent).

### Parent master `O_NONBLOCK` (out of scope)

node-pty sets `O_NONBLOCK` on the parent master after fork for its poll-driven I/O model. MiniPty Linux uses blocking `read` plus `FIONREAD` peek for coalescing; macOS uses temporary `O_NONBLOCK` in `minipty_try_read`. Spawn-time non-blocking would require managed read-path changes — not part of this hygiene PR.

## Current MiniPty fork child path

```c
pid = forkpty(master, NULL, NULL, winp);
pthread_sigmask(SIG_SETMASK, &oldmask, NULL);   /* child too — no handler reset yet */

if (pid == 0) {
    if (cwd) chdir(cwd);
    minipty_execvpe(file, argv, child_envp);
    _exit(127);
}
```

Missing vs node-pty:

1. No signal reset before mask restore.
2. No `pty_close_inherited_fds()` equivalent after `chdir`.

## Target design

### Child call order (node-pty parity)

No single `minipty_prepare_fork_child()` wrapper — call sites are explicit to preserve ordering:

```text
forkpty(master, NULL, NULL, winp)
  → [child only] minipty_reset_child_signals()     /* before pthread_sigmask restore */
pthread_sigmask(SIG_SETMASK, &oldmask, NULL)
  → [child only] chdir(cwd)                        /* existing */
  → [child only] minipty_close_inherited_fds()     /* Linux only; after chdir */
  → minipty_execvpe(...) / _exit(127)
```

Keep work **async-signal-safe** where possible: no malloc, no stdio. FD hygiene uses `fcntl`, `syscall(close_range)`, and `CLOSE_RANGE_CLOEXEC` — all acceptable in the pre-exec child.

### Signal reset

```c
#ifndef NSIG
#define NSIG 32
#endif

struct sigaction sa;
memset(&sa, 0, sizeof(sa));
sa.sa_handler = SIG_DFL;
sa.sa_flags = 0;
sigemptyset(&sa.sa_mask);

for (int sig = 1; sig < NSIG; sig++)
    sigaction(sig, &sa, NULL);   /* EINVAL for SIGKILL/SIGSTOP: ignore */
```

Notes:

- Loop **`1 .. NSIG - 1`** (skip invalid signal 0; cleaner than node-pty's `0 .. NSIG - 1`).
- `SIGKILL` / `SIGSTOP` return `EINVAL` from `sigaction` — ignore.
- Do not use `signal(3)` — prefer `sigaction`.
- Run **before** `pthread_sigmask(SIG_SETMASK, …)` restore so no parent handler runs between restore and reset.

Platform guard: only called from `spawn_pty_child_forkpty` (`#if !defined(__APPLE__)`).

### Inherited FD CLOEXEC (Linux)

Port node-pty's `pty_close_inherited_fds` into `minipty_unix.c`.

Strategy (Linux):

1. Try `close_range(3, ~0U, CLOSE_RANGE_CLOEXEC)` when `SYS_close_range` and `CLOSE_RANGE_CLOEXEC` are available (glibc 2.34+, kernel 5.9+).
2. Fallback: for `fd = 3, 4, …`, `fcntl(F_GETFD)` / `fcntl(F_SETFD, FD_CLOEXEC)` via `SetCloseOnExec` until first failure past fd 15 (node-pty stops after fd 15 on persistent error).

FreeBSD: **out of scope for this PR** — follow-up if needed.

### What not to touch

- Do **not** close or CLOEXEC stdin/stdout/stderr (0–2): `forkpty` wires the slave to the child's stdio.
- Do **not** run fd hygiene in the **parent** after fork — only in `pid == 0`.
- Do **not** set parent master `O_NONBLOCK` in this PR.

## Implementation checklist

| Step | File | Action |
|---:|---|---|
| 1 | `minipty_unix.c` | Add `minipty_reset_child_signals()` (`#if !defined(__APPLE__)`). |
| 2 | `minipty_unix.c` | Add `minipty_close_inherited_fds()` + `SetCloseOnExec` (`#if defined(__linux__)`). |
| 3 | `minipty_unix.c` | Wire call order in `spawn_pty_child_forkpty` per target design above. |
| 4 | — | No change to macOS `#if defined(__APPLE__)` spawn block. |
| 5 | — | No C# or public API changes. |
| 6 | docs | After merge: note fork child hygiene in `pty_crossplatform.md` Unix spawn section. |

## Testing

| Test | Expectation |
|---|---|
| Existing Unix integration tests (`PtyEchoOutput`, PATH, working directory, parallel spawn on Linux) | Unchanged behavior, all green. |
| Full `MiniPty.Tests` on Linux CI | Green. |
| FreeBSD | Green; signal reset only; fd hygiene matches pre-PR (Linux-only). |
| Benchmarks `Session_Exit0_Bytes`, `Session_32KiB_StreamBytes` | No regression beyond +10% threshold (child hygiene runs once per spawn in child only — parent path unchanged). |

Deterministic tests for "child did not inherit master fd" are optional and expensive (would require a helper binary or `/proc/self/fd` inspection from a wrapper script). **Merge gate:** full suite green on Linux; code review against node-pty parity.

Optional manual check on Linux:

```bash
# Spawn sh -c 'ls -l /proc/self/fd' and assert low fd count / no unexpected paths
# (document in PR description, not necessarily committed as CI test)
```

## Non-goals

- macOS `posix_spawn` changes ([plan_macos_native.md](plan_macos_native.md)).
- `setuid` / `setgid` (node-pty optional spawn options; Milestone 5 optional).
- Replacing `forkpty` with `posix_spawn` on Linux.
- Closing fds in the **parent** after fork (master lifetime stays in parent as today).
- Parent master `O_NONBLOCK` at spawn (different I/O model; separate decision).
- FreeBSD inherited-fd hygiene (follow-up PR).

## Risks and mitigations

| Risk | Mitigation |
|---|---|
| `close_range` unavailable on older Linux | node-pty fallback `SetCloseOnExec` scan; CI covers modern runners. |
| Accidentally CLOEXEC on stdio | Only touch fds **≥ 3**. |
| FreeBSD fd close semantics differ | Linux-only `#if`; FreeBSD follow-up. |
| Signal reset hides intentional child handlers | Pre-exec child never runs user code; exec replaces dispositions. Correct for spawn path. |

## Merge checklist

- [ ] Linux CI green.
- [ ] FreeBSD CI green (signal reset only; fd hygiene Linux-only).
- [ ] macOS CI unchanged (no edits in Apple block).
- [ ] Benchmarks within threshold.
- [ ] `pty_crossplatform.md` updated (post-merge note).
- [ ] No public API change.
