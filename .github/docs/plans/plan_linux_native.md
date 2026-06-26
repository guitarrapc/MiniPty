# Plan: Linux native fork hygiene (inherited FDs + signal reset)

Follow-up native work for the Linux / FreeBSD **`forkpty`** spawn path. This is a **plan**, not an implemented API contract. After merge, record lessons learned in [pty_crossplatform.md](../references/pty_crossplatform.md).

**Status:** **Decisions locked** (grill-me, 2026-06). **Not yet implemented.** Implement as the **next standalone PR**, before persistent-session / terminal-backend integration in [plan_minipty_next.md](plan_minipty_next.md).

**Delivery:** **Separate PR** from macOS spawn hardening ([plan_macos_native.md](plan_macos_native.md)) and from any termios work. Do not implement on the same branch as macOS-only changes.

Related: node-pty `src/unix/pty.cc` (`PtyFork` child branch, `pty_close_inherited_fds`).

## Summary

| Item | Choice |
|---|---|
| **Problem** | After `forkpty`, the child inherits the parent's full fd table and signal dispositions. MiniPty does not sanitize either before `chdir` / `execve`. |
| **Why now** | MiniPty targets a **VS Code–style terminal backend**: the embedding host (.NET runtime, sockets, logs, other PTY masters) keeps many fds open while spawning shells. Inherited fds and parent signal handlers in the fork child are unacceptable for that model. |
| **Scope** | **`spawn_pty_child_forkpty`** in `minipty_unix.c` (Linux + FreeBSD). macOS uses `posix_spawn` and is out of scope. **Windows ConPTY** is out of scope (no `fork`); terminal-backend work on Windows proceeds in parallel. |
| **Change 1** | **Close inherited fds ≥ 3** in the child before exec — **Linux only** in this PR, ported from node-pty (`close_range` + full fallback). |
| **Change 2** | **Reset all signal handlers to `SIG_DFL`** in the child before exec — **Linux + FreeBSD** (`forkpty` platforms). |
| **Implementation** | Port node-pty's `pty_close_inherited_fds` and signal-reset loop **almost verbatim** into `minipty_unix.c`. Use every available fast path (`close_range` first); do not ship a minimal Linux-only `close_range`-without-fallback variant. |
| **Timing** | Land **before** persistent PTY transport / terminal-backend integration. No public API or C# changes required. |
| **Out of scope** | `uid` / `gid`, termios, master `O_NONBLOCK`, changing exec / env semantics, FreeBSD fd close (follow-up PR), Windows ConPTY hygiene. |
| **Merge gate** | **Prerequisite for “terminal-backend ready” on Unix:** both changes above. All tests green on Linux CI; FreeBSD unchanged or improved (signal reset only); benchmarks within threshold; no public API change. |

## Decisions (locked)

| Topic | Decision |
|---|---|
| **Use case** | Rich embedding host (VS Code–style terminal backend), not a thin dedicated spawn process. |
| **Both changes required** | FD close (Linux) **and** signal reset (Linux + FreeBSD) — not optional hardening. |
| **FreeBSD fd close** | **Follow-up PR.** This PR ships signal reset on FreeBSD only; primary terminal-backend targets are Linux and Windows. node-pty omits fd close on FreeBSD today. |
| **Windows** | No equivalent work in this plan. ConPTY uses a different spawn model; proceed with Windows terminal-backend work independently ([plan_win_alloc.md](plan_win_alloc.md) addresses managed drain allocations, not fork hygiene). |
| **Testing** | Existing `MiniPty.Tests` + Linux/FreeBSD CI + benchmarks. **No** committed deterministic fd-inheritance CI test (optional manual `/proc/self/fd` check in PR description only). |
| **Performance** | Apply all reasonable optimizations in the child (especially `close_range`). Accept up to **+10%** on `Session_Exit0_Bytes` and `Session_32KiB_StreamBytes` if unavoidable. |
| **Allocation** | **No managed hot-path regression.** Hygiene runs in the **child only** (`pid == 0`), async-signal-safe, no malloc. Parent read/write/resize paths and existing parent-side spawn allocations (`UnixExecPayload`, `minipty_envp_for_child` before `forkpty`) are unchanged. If benchmark alloc metrics move, treat as unrelated unless proven otherwise. |

## Background

### Why macOS is unaffected

macOS spawn uses `posix_spawn` of `minipty_spawn_helper` — a **new process image** with fd actions defined by `posix_spawn_file_actions_t`. The hygiene issues below apply to **`fork(2)`**-style children that inherit the parent's address space and fd table until `execve`.

### Why Windows is unaffected

Windows uses ConPTY (`CreatePseudoConsole` + pipes), not `forkpty`. The child does not inherit the parent's Unix fd table. This plan does not block or gate Windows terminal-backend milestones.

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

MiniPty plan (locked):

- **Signal reset:** implement for **all** `forkpty` platforms (Linux + FreeBSD) in this PR.
- **FD close:** implement **Linux only** in this PR using node-pty's `close_range` + full fallback. **FreeBSD fd close:** follow-up PR (`closefrom(3)` or equivalent — evaluate then).

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
minipty_prepare_fork_child()   /* signals + fds (Linux) */
chdir(cwd)                     /* existing */
minipty_execvpe(...)           /* existing */
_exit(127)
```

Keep work **async-signal-safe** where possible: no malloc, no stdio. Port node-pty's fd close (`fcntl`, `close`, `syscall(close_range)`, and `/proc` fallback) and signal reset — all acceptable in the pre-exec child.

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

Port node-pty's `pty_close_inherited_fds` into `minipty_unix.c` (prefer staying in `minipty_unix.c` for this PR; split to `minipty_unix_fork.c` only if file size becomes unwieldy).

Strategy (Linux) — **full node-pty port, not a minimal subset:**

1. Try `close_range(3, ~0U, CLOSE_RANGE_CLOEXEC)` when `SYS_close_range` and `CLOSE_RANGE_CLOEXEC` are available (glibc 2.34+, kernel 5.9+).
2. Fallback: for `fd = 3, 4, …`, `fcntl(F_GETFD)` / `fcntl(F_SETFD, FD_CLOEXEC)` or unconditional `close(fd)` until first `SetCloseOnExec` failure past fd 15 (node-pty stops after fd 15 on persistent error).

FreeBSD (follow-up PR, not this one):

- Evaluate `closefrom(3)` (BSD extension) or a simple `for (fd = 3; ; fd++) close(fd)` loop.
- node-pty omits fd close on FreeBSD; MiniPty mirrors that split for the first PR.

### What not to close

- Do **not** close stdin/stdout/stderr (0–2): `forkpty` wires the slave to the child's stdio; closing 0–2 would break the PTY session.
- Do **not** run fd close in the **parent** after fork — only in `pid == 0`.

## Implementation checklist

| Step | File | Action |
|---:|---|---|
| 1 | `minipty_unix.c` | Add `minipty_reset_child_signals()` (forkpty platforms). Port from node-pty. |
| 2 | `minipty_unix.c` | Add `minipty_close_inherited_fds()` (`#if defined(__linux__)`). Port full node-pty implementation. |
| 3 | `minipty_unix.c` | Add `minipty_prepare_fork_child()` calling both; invoke at start of `pid == 0` branch. |
| 4 | — | No change to macOS `#if defined(__APPLE__)` spawn block. |
| 5 | — | No C# or public API changes. |
| 6 | docs | After merge: note fork child hygiene in `pty_crossplatform.md` Unix spawn section. |

## Testing

| Test | Expectation |
|---|---|
| Existing Unix integration tests (`PtyEchoOutput`, PATH, working directory, parallel spawn on Linux) | Unchanged behavior, all green. |
| Full `MiniPty.Tests` on Linux CI | Green. |
| FreeBSD | Green; signal reset landed; fd close behavior matches pre-PR (follow-up tracks FreeBSD fd close). |
| Benchmarks `Session_Exit0_Bytes`, `Session_32KiB_StreamBytes` | No regression beyond **+10%** threshold after applying all reasonable child-side optimizations. Child hygiene runs once per spawn in child only — parent path unchanged. |
| Deterministic fd-inheritance CI test | **Not in scope** for merge. Optional manual check on Linux (document in PR description). |

**Merge gate:** full suite green on Linux; code review against node-pty parity; benchmarks within +10%.

Optional manual check on Linux:

```bash
# Spawn sh -c 'ls -l /proc/self/fd' and assert low fd count / no unexpected paths
# (document in PR description, not committed as CI test)
```

## Non-goals

- macOS `posix_spawn` changes ([plan_macos_native.md](plan_macos_native.md)).
- Windows ConPTY spawn hygiene (different OS model).
- `setuid` / `setgid` (node-pty optional spawn options; Milestone 5 optional).
- Replacing `forkpty` with `posix_spawn` on Linux.
- Closing fds in the **parent** after fork (master lifetime stays in parent as today).
- FreeBSD inherited-fd close in this PR (follow-up).
- Committed CI test for child fd table contents.

## Risks and mitigations

| Risk | Mitigation |
|---|---|
| `close_range` unavailable on older Linux | Full node-pty fallback scan; CI covers modern runners. |
| Accidentally closing slave stdio | Only close fds **≥ 3**; never 0–2. |
| FreeBSD fd leak until follow-up | Documented; signal reset still lands; primary terminal-backend OS is Linux. |
| Signal reset hides intentional child handlers | Pre-exec child never runs user code; exec replaces dispositions. Correct for spawn helper path. |
| Spawn benchmark regression | Prefer `close_range`; accept up to +10% if all optimizations applied. No managed hot-path change. |

## Merge checklist

- [ ] Linux CI green.
- [ ] FreeBSD CI green (signal reset; fd close deferred to follow-up).
- [ ] macOS CI unchanged (no edits in Apple block).
- [ ] Benchmarks within +10% threshold (`Session_Exit0_Bytes`, `Session_32KiB_StreamBytes`).
- [ ] No managed allocation regression on parent hot paths (verify benchmark alloc columns; child-only native work expected).
- [ ] `pty_crossplatform.md` updated (post-merge note).
- [ ] No public API change.

## Follow-up (out of this PR)

| Item | Notes |
|---|---|
| FreeBSD `minipty_close_inherited_fds` | Evaluate `closefrom(3)`; separate small PR. |
| Optional fd-inheritance integration test | Only if a cheap, deterministic approach emerges. |
