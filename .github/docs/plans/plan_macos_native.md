# Plan: macOS native spawn hardening (EINTR + low-fd)

Follow-up native work for the macOS `posix_spawn` + `minipty_spawn_helper` path landed on branch `macos`. This is a **plan**, not an implemented API contract. After merge, record lessons learned in [platform_support.md](../specs/platform_support.md) and [pty_crossplatform.md](../references/pty_crossplatform.md).

**Delivery:** **Separate PR** from the initial macOS spawn PR. Do not combine with Linux fork hygiene ([plan_linux_native.md](plan_linux_native.md)).

Related: [plan_macos_spwn.md](plan_macos_spwn.md) (spawn redesign), node-pty `src/unix/pty.cc` (`pty_posix_spawn`).

## Summary

| Item | Choice |
|---|---|
| **Problem** | Two node-pty spawn robustness patterns are not yet mirrored in MiniPty's macOS path. |
| **Scope** | macOS only (`#if defined(__APPLE__)` block in `minipty_unix.c`). Linux / FreeBSD unchanged. |
| **Change 1** | Retry `posix_spawn(3)` on **`EINTR`** inside a single spawn attempt. |
| **Change 2** | **Low-fd reservation** before opening the real PTY master so `dup2(slave → 0/1/2)` remains reliable when stdio slots are vacant or unusual. |
| **Out of scope** | `EAGAIN` / `ENOMEM` outer retry (already implemented), termios / `tcsetattr`, helper protocol changes. |
| **Merge gate** | All tests green on macOS (arm64 + x64 CI); benchmarks within existing thresholds; no public API change. |

## Background

### Current MiniPty macOS spawn (after `macos` branch)

```text
minipty_spawn_darwin_once:
  resolve helper via dladdr
  posix_openpt → grantpt → unlockpt
  ioctl(TIOCPTYGNAME) → open slave
  TIOCSWINSZ
  inject MINIPTY_CWD into envp (when cwd set)
  posix_spawn(helper, dup2 slave → stdio, SETSID, …)   ← single call, no EINTR loop
  outer retry (spawn_pty_child_darwin): EAGAIN / ENOMEM only, up to 4 attempts
```

This matches node-pty's macOS architecture. Two details in node-pty's `pty_posix_spawn` are still absent.

### 1. `posix_spawn` and `EINTR`

`posix_spawn` is a library call that may return **`EINTR`** if a signal handler runs without `SA_RESTART` during the syscall path. node-pty wraps the call:

```c
do
    spawn_err = posix_spawn(pid, argv[0], &acts, &attrs, argv, env);
while (spawn_err == EINTR);
```

MiniPty calls `posix_spawn` once. Under a multithreaded .NET test host with many concurrent spawns and signal delivery, a spurious **`EINTR`** can surface as `PTY spawn failed (errno 4)` even when resources are available.

**Distinction:** This is separate from the existing **`EAGAIN` / `ENOMEM`** retry loop in `spawn_pty_child_darwin`. EINTR should be retried **inside** one attempt without closing the master or backing off; transient resource pressure still uses the outer loop.

### 2. Low-fd reservation trick

Before opening the real master PTY, node-pty opens temporary PTY fds until one lands at **`fd >= STDERR_FILENO (2)`**:

```c
for (; count < 3; count++) {
    low_fds[count] = posix_openpt(O_RDWR);
    if (low_fds[count] >= STDERR_FILENO)
        break;
}
/* … real posix_openpt, spawn with dup2(slave, 0/1/2) … */
for (size_t i = 0; i <= count; i++)
    close(low_fds[i]);
```

**Why:** `posix_spawn_file_actions_adddup2(&acts, slave, STDIN_FILENO)` (and stdout/stderr) assumes the child receives the slave on the canonical stdio fds. If the parent process has **closed or repurposed** fd 0–2 (unusual but possible in embedded hosts, test runners, or daemonized parents), the kernel may assign the slave to fd 0, 1, or 2 only if those slots are available in the **spawning** context. Pre-opening dummy PTYs on vacant low slots prevents the real master/slave setup from colliding with a bad fd layout before `dup2` actions run.

MiniPty does not perform this reservation today. Failures are rare in normal CLI/test hosts but node-pty carries the trick for the same class of edge cases.

## node-pty reference

Clone on the macOS dev machine (gitignored):

```bash
mkdir -p .references
git clone --depth 1 https://github.com/microsoft/node-pty.git .references/node-pty
```

| File | Relevant section |
|---|---|
| `.references/node-pty/src/unix/pty.cc` | `pty_posix_spawn`: `low_fds` loop, `posix_spawn` EINTR loop, cleanup in `done:` |

MiniPty intentionally keeps its own helper protocol (`argv=[helper,file,args…]`, `MINIPTY_CWD` in envp). This plan only ports the **spawn syscall hygiene**, not argv layout.

## Target design

### EINTR retry (inner loop)

Location: `minipty_spawn_darwin_once`, immediately around `posix_spawn`.

```c
do {
    spawn_err = posix_spawn(pid_out, helper_argv[0], &actions, &attrs,
                            helper_argv,
                            spawn_envp != NULL ? spawn_envp : (char **)envp);
} while (spawn_err == EINTR);
```

Rules:

- Retry **only** `EINTR`. Do not fold `EAGAIN` / `ENOMEM` into this loop (outer policy unchanged).
- Do **not** destroy/recreate `actions` / `attrs` between EINTR retries unless Apple documentation requires it (node-pty reuses them).
- If the loop exits with non-zero `spawn_err`, existing error path closes master and returns errno to managed code.

### Low-fd reservation

Location: start of `minipty_spawn_darwin_once`, **before** the real `posix_openpt(O_RDWR | O_CLOEXEC)`.

```c
int low_fds[3];
size_t low_fd_count = 0;

for (; low_fd_count < 3; low_fd_count++) {
    low_fds[low_fd_count] = posix_openpt(O_RDWR | O_CLOEXEC);
    if (low_fds[low_fd_count] < 0) {
        /* cleanup low_fds[0..low_fd_count), return errno */
    }
    if (low_fds[low_fd_count] >= STDERR_FILENO)
        break;
}
```

Cleanup:

- On **every** exit path (success, spawn failure, grantpt/unlockpt/open slave errors), close `low_fds[0..low_fd_count]`.
- Prefer a single `goto done` / labeled cleanup block (mirror node-pty `done:`) to avoid leak regressions on error branches.
- Use `O_CLOEXEC` on dummy opens consistent with the real master fd.

### Error-path refactor (recommended in same PR)

`minipty_spawn_darwin_once` currently duplicates `close(slave); close(*master); *master = -1` on many branches. Introducing low-fd cleanup is easier if the function uses one **`done:`** label for:

1. `posix_spawn_file_actions_destroy`
2. `posix_spawnattr_destroy`
3. `close(slave)` when `slave >= 0`
4. close all `low_fds`
5. `free(helper_argv)` / `minipty_free_spawn_env` as today
6. on failure only: `close(*master); *master = -1`

Keep the change localized to the Apple block; do not refactor the Linux `forkpty` path in this PR.

## Implementation checklist

| Step | File | Action |
|---:|---|---|
| 1 | `minipty_unix.c` | Add low-fd loop at top of `minipty_spawn_darwin_once`. |
| 2 | `minipty_unix.c` | Wrap `posix_spawn` in `do { … } while (spawn_err == EINTR)`. |
| 3 | `minipty_unix.c` | Consolidate error cleanup (`done:`) to close low fds on all paths. |
| 4 | — | No change to `minipty_spawn_helper.c`, build scripts, or public C# API. |
| 5 | docs | After merge: one-line note under macOS spawn in `pty_crossplatform.md`. |

## Testing

| Test | Expectation |
|---|---|
| Existing `PtyUnixParallelSpawnCompletes` | Still passes (16 concurrent spawns). |
| Full `MiniPty.Tests` on macOS CI matrix | Green with parallel TUnit. |
| `Session_Exit0_Bytes`, `Session_32KiB_StreamBytes` | No allocation regression beyond +10% threshold. |

**EINTR** and **low-fd vacancy** are difficult to reproduce deterministically without inject/mock hooks. This PR does **not** require a dedicated fault-injection test if:

- Code review confirms EINTR loop matches node-pty semantics, and
- Parallel spawn + full suite remain green on macOS hardware and CI.

Optional follow-up (not merge-blocking): stress test that spawns with `close(0); close(1); close(2);` in parent before `Pty.Start` — only if a reliable harness can be added without flaking CI.

## Non-goals

- Changing `MINIPTY_SPAWN_RETRY_MAX` / backoff policy.
- Adding `tcsetattr` / termios parity with node-pty (separate decision).
- Linux / FreeBSD `forkpty` hygiene ([plan_linux_native.md](plan_linux_native.md)).
- Windows ConPTY changes.

## Risks and mitigations

| Risk | Mitigation |
|---|---|
| Low-fd dummy PTYs consume kernel PTY budget briefly | At most 3 extra opens for milliseconds; same as node-pty. |
| Cleanup miss on new `goto done` refactor | Review all early returns; run leak-detection sanitizer locally if available. |
| EINTR loop masks real bugs | Loop condition is strict equality on `EINTR` only. |

## Merge checklist

- [ ] macOS arm64 + x64 CI green (parallel tests).
- [ ] Linux / FreeBSD CI unchanged.
- [ ] Benchmarks within threshold.
- [ ] `pty_crossplatform.md` updated (post-merge note).
- [ ] No public API or package layout change.
