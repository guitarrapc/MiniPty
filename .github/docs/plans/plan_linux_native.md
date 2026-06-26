# Plan: Linux native fork hygiene (inherited FDs + signal reset)

Follow-up native work for the Linux / FreeBSD **`forkpty`** spawn path. This is a **plan**, not an implemented API contract. After merge, record lessons learned in [pty_crossplatform.md](../references/pty_crossplatform.md).

**Delivery:** **Separate PR** from macOS spawn hardening ([plan_macos_native.md](plan_macos_native.md)) and from any termios work. Do not implement on the same branch as macOS-only changes.

Related: node-pty `src/unix/pty.cc` (`PtyFork` child branch, `pty_close_inherited_fds`).

## Summary

| Item | Choice |
|---|---|
| **Problem** | After `forkpty`, the child inherits the parent's full fd table and signal dispositions. MiniPty does not sanitize either before `chdir` / `execve`. |
| **Scope** | **`spawn_pty_child_forkpty`** in `minipty_unix.c` (Linux + FreeBSD). macOS uses `posix_spawn` and is out of scope. |
| **Change 1** | **Close inherited fds ≥ 3** in the child before exec (Linux implementation matching node-pty; evaluate FreeBSD). |
| **Change 2** | **Reset all signal handlers to `SIG_DFL`** in the child before exec (all `forkpty` platforms). |
| **Out of scope** | `uid` / `gid`, termios, master `O_NONBLOCK`, changing exec / env semantics. |
| **Merge gate** | All tests green on Linux CI; FreeBSD unchanged or improved; benchmarks within threshold; no public API change. |

## Background

### Why macOS is unaffected

macOS spawn uses `posix_spawn` of `minipty_spawn_helper` — a **new process image** with fd actions defined by `posix_spawn_file_actions_t`. The hygiene issues below apply to **`fork(2)`**-style children that inherit the parent's address space and fd table until `execve`.

### 1. Inherited file descriptors (Linux)

When `forkpty` succeeds in the child (`pid == 0`), the child receives a **copy of every open fd** in the parent, including:

- The PTY **master** fd (opened in the parent before fork).
- Sockets, files, and pipes held by the .NET test host or embedding application.

node-pty closes descriptors **`>= 3`** in the child before `execvp`:

```c
#if defined(__linux__)
pty_close_inherited_fds();  // close_range(3, ~0, CLOSE_RANGE_CLOEXEC) or FD_CLOEXEC scan
#endif
```

Without this step, a spawned shell or tool can **inherit sensitive handles** (log files, network sockets, other PTY masters) and accidentally duplicate I/O or leak resources. Setting `FD_CLOEXEC` on the master in the parent helps the parent side but does **not** remove already-duplicated fds from the child's table at fork time unless CLOEXEC was set **before** fork on every inherited fd.

MiniPty opens the master via `forkpty` in the parent and does not scrub the child fd table today.

### 2. Inherited signal handlers (Linux + FreeBSD)

node-pty resets signal dispositions in the fork child before exec:

```c
for (int i = 0; i < NSIG; i++) {
    sigaction(i, &sig_action_with_SIG_DFL, NULL);
}
```

Rationale (from node-pty comments): avoid running **parent-registered signal handlers** in the child after fork and before `execve`. In a multithreaded parent (.NET runtime, thread pool, GC), custom handlers intended for the parent process must not run in the short-lived fork child.

MiniPty already blocks signals with `pthread_sigmask` around **`forkpty` in the parent** (same as node-pty). It does **not** reset handlers in the child. The child path is short (`chdir` → `minipty_execvpe` → `_exit`), but a signal delivered in that window could still invoke a parent handler in the child address space.

### node-pty platform split

| Hygiene | Linux | FreeBSD (forkpty) | macOS (posix_spawn) |
|---|---|---|---|
| `pty_close_inherited_fds` | Yes | No in node-pty | N/A |
| Signal reset in child | Yes | Yes | N/A (spawn attrs) |

MiniPty plan:

- **Signal reset:** implement for **all** `forkpty` platforms (Linux + FreeBSD).
- **FD close:** implement **Linux-first** using node-pty's `close_range` / fallback; verify FreeBSD portability in the same PR or a tiny follow-up.

## Current MiniPty fork child path

```c
pid = forkpty(master, NULL, NULL, winp);
/* parent restores signal mask */

if (pid == 0) {
    if (cwd) chdir(cwd);
    minipty_execvpe(file, argv, child_envp);
    _exit(127);
}
```

Missing vs node-pty:

1. No `pty_close_inherited_fds()` equivalent.
2. No `sigaction` loop to `SIG_DFL`.

## Target design

### Child-only hook

Add a static function invoked **only when `pid == 0`**, before `chdir` and exec:

```c
static void minipty_prepare_fork_child(void);
```

Call order in child:

```text
minipty_prepare_fork_child()   /* signals + fds */
chdir(cwd)                     /* existing */
minipty_execvpe(...)           /* existing */
_exit(127)
```

Keep work **async-signal-safe** where possible: no malloc, no stdio. node-pty's fd close uses `fcntl`, `close`, `syscall(close_range)`, and `/proc` fallback — all acceptable in the pre-exec child.

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
    sigaction(sig, &sa, NULL);
```

Notes:

- Skip signal 0 (`sigaction(0, …)` is invalid).
- Do not use `signal(3)` — prefer `sigaction` (node-pty pattern).
- Run **before** `chdir` so a signal during `chdir` does not run a parent handler.

Platform guard: `#if !defined(__APPLE__)` around the forkpty child block (or inside `minipty_prepare_fork_child` when only called from forkpty).

### Inherited FD close (Linux)

Port node-pty's `pty_close_inherited_fds` into `minipty_unix.c` (or a small `minipty_unix_fork.c` only if file size becomes unwieldy — prefer staying in `minipty_unix.c` for this PR).

Strategy (Linux):

1. Try `close_range(3, ~0U, CLOSE_RANGE_CLOEXEC)` when `SYS_close_range` and `CLOSE_RANGE_CLOEXEC` are available (glibc 2.34+, kernel 5.9+).
2. Fallback: for `fd = 3, 4, …`, `fcntl(F_GETFD)` / `fcntl(F_SETFD, FD_CLOEXEC)` or unconditional `close(fd)` until first `SetCloseOnExec` failure past fd 15 (node-pty stops after fd 15 on persistent error).

FreeBSD:

- node-pty omits fd close on FreeBSD. Evaluate whether a simple `for (fd = 3; ; fd++) close(fd)` loop is safe or if `closefrom(3)` (BSD extension) is preferable.
- **Merge gate:** if FreeBSD behavior is uncertain, land Linux-only fd close behind `#if defined(__linux__)` and file a follow-up for FreeBSD.

### What not to close

- Do **not** close stdin/stdout/stderr (0–2): `forkpty` wires the slave to the child's stdio; closing 0–2 would break the PTY session.
- Do **not** run fd close in the **parent** after fork — only in `pid == 0`.

## Implementation checklist

| Step | File | Action |
|---:|---|---|
| 1 | `minipty_unix.c` | Add `minipty_reset_child_signals()` (forkpty platforms). |
| 2 | `minipty_unix.c` | Add `minipty_close_inherited_fds()` (`#if defined(__linux__)` initially). |
| 3 | `minipty_unix.c` | Add `minipty_prepare_fork_child()` calling both; invoke at start of `pid == 0` branch. |
| 4 | — | No change to macOS `#if defined(__APPLE__)` spawn block. |
| 5 | — | No C# or public API changes. |
| 6 | docs | After merge: note fork child hygiene in `pty_crossplatform.md` Unix spawn section. |

## Testing

| Test | Expectation |
|---|---|
| Existing Unix integration tests (`PtyEchoOutput`, PATH, working directory, parallel spawn on Linux) | Unchanged behavior, all green. |
| Full `MiniPty.Tests` on Linux CI | Green. |
| FreeBSD | Green; if fd close is Linux-only, FreeBSD should match pre-PR behavior. |
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

## Risks and mitigations

| Risk | Mitigation |
|---|---|
| `close_range` unavailable on older Linux | node-pty fallback scan; CI covers modern runners. |
| Accidentally closing slave stdio | Only close fds **≥ 3**; never 0–2. |
| FreeBSD fd close semantics differ | Linux-first `#if`; FreeBSD follow-up if needed. |
| Signal reset hides intentional child handlers | Pre-exec child never runs user code; exec replaces dispositions. Correct for spawn helper path. |

## Merge checklist

- [ ] Linux CI green.
- [ ] FreeBSD CI green (or documented Linux-only fd close).
- [ ] macOS CI unchanged (no edits in Apple block).
- [ ] Benchmarks within threshold.
- [ ] `pty_crossplatform.md` updated (post-merge note).
- [ ] No public API change.
