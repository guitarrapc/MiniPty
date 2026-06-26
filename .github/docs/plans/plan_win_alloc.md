# Plan: Windows `ReadOutputAsync` Allocation Reduction

Working implementation plan for closing the cross-OS allocation gap on `Session_32KiB_StreamBytes` (Windows ConPTY drain path). This is a **plan**, not an implemented API contract. After merge, record lessons learned in [pty_crossplatform.md](../references/pty_crossplatform.md) and [lifecycle.md](../specs/lifecycle.md).

Related: [plan_minipty_next.md](plan_minipty_next.md) (Milestone 3 PR3 / Gate C), allocation gate script `scripts/compare-benchmark-allocations.ps1`, baseline `BenchmarkDotNet.Artifacts/baselines/integration.json`.

## Summary

| Item | Choice |
|---|---|
| **Scope** | **Windows ConPTY drain path** (`BoundedOutputBuffer.ObserveExitForOutputDrainAsync` and related backend exit polling). Benchmark/CI fairness is a **reference-only** optional phase — not a library merge gate. |
| **Delivery** | **One PR**, multiple commits. **Tests + narrow benchmarks after every commit** so hangs/regressions map to a single diff. |
| **Stretch target** | `Session_32KiB_StreamBytes` **≤ 15 KB** allocated on Windows (ShortRun). Linux (~4.65 KB) gap partly accepted as spawn / integration overhead. |
| **If target missed** | **Decision at PR end** — merge is not pre-authorized; rejection is possible. See [Decision gate](#decision-gate-if-15-kb-not-met). |

## Problem

### Measured gap (CI, `PtyIntegrationBenchmarks`, 2026-06)

| Benchmark | Ubuntu | Windows | Role |
|---|---:|---:|---|
| `Session_32KiB_StreamBytes` | 4.65 KB | 25.84 KB | `ReadOutputAsync` — editor-backend path |
| `Session_32KiB_OutputStreamBytes` | 1.24 KB | 3.91 KB | `Output.ReadAsync` transport — control |
| `Session_Exit0_Bytes` | 1.5 KB | 3.43 KB | spawn baseline — control |

### Why Windows is higher

1. **ConPTY EOF semantics** — `ReadFile` on an empty ConPTY pipe blocks until the output transport is closed; Unix PTY reads return EOF naturally when the child exits.
2. **`ObserveExitForOutputDrainAsync`** — runs for `ReadOutputAsync` when completion is not orchestrated; after exit it polls a 100 ms stall window, then calls `CloseOutputTransport`.
3. **Allocating waits (today)** — stall loop uses `Task.Delay(10)` (~10–20 timer tasks per iteration); exit wait uses `WaitForExitInternalAsync` → `WindowsPtyBackend.WaitForExitAsync` with `Task.Yield()` on `WaitTimeout`.
4. **Rejected approach** — `Monitor.Wait(_sync, PollMs)` inside the stall loop caused `ReadOutputAsync` tests to see **empty output** on Windows (producer `Handoff` / `PulseAll` interaction). Do not retry without a new design review.

### Non-goals

- **Busy loops** (`SpinWait`, tight `TickCount` spin) — this library may stay resident behind editor backends; blocking sleep is acceptable, CPU burn is not.
- **Benchmark child change as merge gate** — Windows `SmallStdout` uses PowerShell; Unix uses `head -c`. Improves latency/fairness of CI numbers but does not fix library drain logic. Optional reference phase only.
- **Parity with Linux absolute numbers** — spawn and async enumerator overhead may remain; target is meaningful Windows reduction, not identical bytes.

## Constraints

- NativeAOT-safe; no new dependencies.
- No weakening `ReadOutputAsync` handoff / no-drop semantics.
- `CloseOutputTransport` must **not** run while holding `BoundedOutputBuffer._sync` (deadlock with producer `Complete()`).
- All **61** tests must pass after every commit.
- **No `integration.json` update per commit** — update once at PR merge if the Windows baseline improves.

## Benchmark log (Windows ShortRun)

Machine: Windows 11, Ryzen 9 7950X3D, .NET 10.0.9.

**Gate (every commit):** B-set Allocated must be **≤ prior step** for all three benchmarks. **Stretch:** `StreamBytes` ≤ 15 KB.

### Allocated per step (ShortRun)

| Step | StreamBytes | OutputStream | Exit0 | Tests |
|------|------------:|-------------:|------:|------:|
| PR start | 26.21 KB | 3.91 KB | 3.44 KB | — |
| Commit 1 | 24.17 KB | 3.91 KB | 3.44 KB | 61/61 |
| Commit 2 | 24.11 KB | 3.91 KB | 3.44 KB | 61/61 |
| Commit 3 | 24.04 KB | 3.78 KB | 3.44 KB | 61/61 |

### Δ vs prior step

| Step | StreamBytes | OutputStream | Exit0 | Gate |
|------|------------:|-------------:|------:|------|
| Commit 1 | −2.04 KB | 0 | 0 | pass |
| Commit 2 | −0.06 KB | 0 | 0 | pass |
| Commit 3 | −0.07 KB | −0.13 KB | 0 | pass |

### Δ vs PR start (cumulative, Commit 3)

| Benchmark | PR start | Commit 3 | Δ |
|-----------|----------:|---------:|--:|
| `Session_32KiB_StreamBytes` | 26.21 KB | 24.04 KB | −2.17 KB |
| `Session_32KiB_OutputStreamBytes` | 3.91 KB | 3.78 KB | −0.13 KB |
| `Session_Exit0_Bytes` | 3.44 KB | 3.44 KB | 0 |

**Stretch target:** `StreamBytes` 24.04 KB — still **> 15 KB**; see [Decision gate](#decision-gate-if-15-kb-not-met).

## Commit plan (single PR)

Implement in order. **Stop and run gates after each commit** before continuing.

### Commit 1 — Stall poll: `Task.Delay` → `Thread.Sleep`

**File:** `src/MiniPty/PtySession.cs` (`ObserveExitForOutputDrainAsync` stall loop only).

Replace:

```csharp
await Task.Delay(10, _producerCancellation.Token).ConfigureAwait(false);
```

With `Thread.Sleep(remaining)` **outside** `_sync`, using `Environment.TickCount64` deadlines (same pattern as `UnixPtyBackend.PollForChildExit`).

**Also required (correctness):** capture `concurrentExitWait = _session.IsExitWaitActive` **before** `await WaitForExitInternalAsync`. Checking after `await` races with a concurrent `WaitForExitAsync` that disposes the exit-wait scope; `Thread.Sleep` timing exposed empty output in `PtyReadOutputAsyncConcurrentWithWaitForExitAsync`.

**Do not** use `Monitor.Wait` on `_sync` (regression observed). **Do not** use busy loops.

**Gates:**

| Gate | Criterion |
|---|---|
| Tests | 61/61 pass |
| Bench B-set | `StreamBytes` Allocated **≤ PR start**; `OutputStreamBytes` and `Exit0` must not regress vs PR start |
| Hang | Benchmark completes without manual kill |

**Recorded probe (local Windows, pre-plan):** ~26.3 KB → ~24.2 KB with `Thread.Sleep` approach before full validation; treat as indicative only.

---

### Commit 2 — Exit wait: sync `PollForChildExit` on Windows

**Files:**

- `src/MiniPty/Internal/WindowsPtyBackend.cs` — add private `PollForChildExit(int timeoutMs, CancellationToken)` mirroring Unix (`TryRefreshExitState`, `WaitForSingleObject` with bounded wait, `Thread.Sleep` for remainder, `PromoteEofIfPending` / `CloseInputPipeIfEofSignaled` as in existing async loop).
- `src/MiniPty/PtySession.cs` — internal dispatch from `ObserveExitForOutputDrainAsync`; replace `await WaitForExitInternalAsync(..., closeTransportOnExit: false)` with synchronous polling until exit (or cancellation).

Keep `concurrentExitWait` captured **before** exit polling (see Commit 1). Replace `await WaitForExitInternalAsync(..., closeTransportOnExit: false)` with `_session.PollForChildExitUntilExited(..., closeTransportOnExit: false)` — allocation-free, no `Task.Yield`.

**Gates:** same as Commit 1. **Focus tests:**

- `PtyReadOutputAsyncReadsBytesUntilExit`
- `PtyReadOutputAsyncConcurrentWithWaitForExitAsync`

---

### Commit 3 — `WaitForExitAsync`: reuse `PollForChildExit` (conditional)

**Condition:** Run only if after Commit 2, `Session_32KiB_StreamBytes` is still **> 15 KB** on Windows ShortRun.

**File:** `src/MiniPty/Internal/WindowsPtyBackend.cs` — replace `Task.Yield()` loop in `WaitForExitAsync` with `PollForChildExit` chunks (or shared wait helper).

**Gates:** Commit 1 gates + full `*PtyIntegrationBenchmarks*` (11 methods) on **final commit** (whether or not Commit 3 runs).

---

### Optional reference phase (not a library merge gate)

**Benchmark child on Windows** — replace PowerShell `SmallStdout` with a lighter process (`head` equivalent or small helper) so CI latency/allocation comparisons are fairer. Track under a separate commit or issue; do not block drain-path PR on this.

## Per-commit verification commands

```powershell
# Tests (every commit)
dotnet test tests/MiniPty.Tests/MiniPty.Tests.csproj -c Release

# Bench B-set (every commit, Windows)
dotnet run -c Release --no-launch-profile --project src/MiniPty.Benchmarks/MiniPty.Benchmarks.csproj -- `
  --filter "*Session_32KiB_StreamBytes" --job short
dotnet run -c Release --no-launch-profile --project src/MiniPty.Benchmarks/MiniPty.Benchmarks.csproj -- `
  --filter "*Session_32KiB_OutputStreamBytes" --job short
dotnet run -c Release --no-launch-profile --project src/MiniPty.Benchmarks/MiniPty.Benchmarks.csproj -- `
  --filter "*Session_Exit0_Bytes" --job short

# Full integration (final commit only)
dotnet run -c Release --no-launch-profile --project src/MiniPty.Benchmarks/MiniPty.Benchmarks.csproj -- `
  --filter "*PtyIntegrationBenchmarks*" --job short

# Baseline compare (final commit, if merging)
./scripts/compare-benchmark-allocations.ps1
```

Record each commit's `StreamBytes` Allocated in the PR description (monotonic decrease required).

## Success criteria (PR end)

| Criterion | Required |
|---|---|
| 61/61 tests | Yes |
| `StreamBytes` monotonic decrease across commits | Yes |
| `StreamBytes` ≤ 15 KB (Windows ShortRun) | **Stretch** — decision at PR end |
| `OutputStreamBytes`, `Exit0` ≤ PR-start values | Yes |
| All 11 integration benchmarks ≤ `integration.json` (if baseline updated) | Yes, when baseline is refreshed |
| No benchmark hang | Yes |

## Decision gate (if 15 KB not met)

Do **not** assume the PR merges. At PR end, choose among (no fixed priority):

1. **Allocation profiler** — ~~`dotnet run ... --profiler EP`~~ **Done** — see [Profiler findings](#profiler-findings-2026-06-26-windows).
2. **Further core work** — [plan_win_coalesce.md](plan_win_coalesce.md) (producer coalescing + transport peek; **separate PR**). Optional: shrink async state machines if coalesce alone misses target.
3. **Benchmark isolation** — optional PowerShell child change to separate measurement from library cost.
4. **Stop** — revert or withhold PR until a new plan is agreed.

Document the chosen path and measured residual in this file and in `pty_crossplatform.md`.

## Profiler findings (2026-06-26, Windows)

**Method:** B-set differential benchmarks + `GC.GetTotalAllocatedBytes` diagnostic + EventPipe (`--profiler EP`) call stacks.

### 1. Spawn vs transport vs ReadOutputAsync (B-set, ShortRun)

| Benchmark | Allocated | Δ vs Exit0 | What it measures |
|-----------|----------:|-----------:|------------------|
| `Session_Exit0_Bytes` | 3.44 KB | — | ConPTY spawn only |
| `Session_32KiB_OutputStreamBytes` | 3.78 KB | **+0.34 KB** | spawn + raw `Output.ReadAsync` loop + post-exit drain |
| `Session_32KiB_StreamBytes` | 24.04 KB | **+20.60 KB** | spawn + `ReadOutputAsync` (`BoundedOutputBuffer`) |

**Takeaway:** ConPTY transport I/O and drain are cheap (~0.34 KB over spawn for 32 KiB). **~86% of `StreamBytes` (20.6 / 24.0 KB) is `ReadOutputAsync`-specific**, not spawn and not raw pipe reads.

Cross-OS gap decomposition (CI numbers from plan header):

| Component | Ubuntu | Windows | Δ |
|-----------|-------:|--------:|--:|
| Spawn (`Exit0`) | 1.50 KB | 3.44 KB | +1.94 KB |
| Transport + drain (`OutputStream`) | 1.24 KB | 3.78 KB | +2.54 KB |
| **ReadOutputAsync overhead** (Stream − Output) | **~3.4 KB** | **~20.3 KB** | **~+16.9 KB** |

Windows is worse primarily in the **editor-backend path**, not because ConPTY spawn is 5× slower to allocate.

### 2. Chunk count drives multiplication

Diagnostic (`SmallStdout` PowerShell child, 32 KiB target):

| Metric | Value |
|--------|------:|
| `transport_reads` (`Output.ReadAsync`, 4 KiB buffer) | **~125** |
| `ReadOutputAsync` chunks | **~125** (1:1 with transport reads) |
| Bytes delivered | ~37 KiB (PowerShell `[string]` overhead) |
| Average read size | **~297 B** |

Linux benchmark child is `head -c 32768 /dev/zero` (binary, no shell string expansion). With a 4 KiB read buffer that path typically yields **~8 reads**, not ~125.

**Estimated per-chunk async overhead:** (24.04 − 3.78) KB / 125 chunks ≈ **166 B/chunk** — consistent with `ProduceAsync` + `ReadAsync` + `ReadOutputAsync` state-machine boxes, `ValueTask`/`ManualResetValueTaskSourceCore`, and `Monitor.Wait`/`PulseAll` per handoff cycle.

Commits 1–3 removed **timer/`Task.Yield` drain polling** (~2.17 KB total). They did **not** reduce per-chunk multiplication; that explains why 15 KB stretch remains unreachable without a new design.

### 3. EventPipe stacks (qualitative)

Dominant frames in `Session_32KiB_StreamBytes` trace:

- `BoundedOutputBuffer.ProduceAsync` / `+<ProduceAsync>d__*.MoveNext`
- `BoundedOutputBuffer.ObserveExitForOutputDrainAsync` (exit poll + stall + `CloseOutputTransport`)
- `BoundedOutputBuffer.Handoff` / `+<ReadAsync>d__*.MoveNext`
- `PtySession+<ReadOutputAsync>d__*.MoveNext` (consumer `await foreach`)
- `AsyncTaskMethodBuilder` / `AsyncStateMachineBox` (thread-pool continuations)

Drain polling (`PollForChildExitUntilExited`, `Thread.Sleep` stall) appears in the trace but is **not** the bulk allocator compared with ~125 handoff cycles.

### 4. Decision (post-profiler)

| Option | Assessment |
|--------|------------|
| **Profiler** | Done — see above. |
| **Further core work** | **Primary lever:** reduce allocations **per handoff** (state-machine shrink, fewer `Task`/box allocations) and/or **coalesce** small ConPTY reads before exposing chunks. Higher risk; needs new spec for chunk coalescing semantics. |
| **Benchmark isolation** | PowerShell child inflates bytes (~37 KiB) and may affect read granularity; swapping to a lighter child improves CI fairness but **does not remove** per-chunk `ReadOutputAsync` cost. Optional reference phase. |
| **Stop / withhold** | Not required if merging current −2.17 KB drain win; **15 KB stretch needs a follow-up plan** (not this PR). |

**Recommended follow-up:** [plan_win_coalesce.md](plan_win_coalesce.md) — producer coalescing with transport peek (separate PR).

Trace artifacts: `BenchmarkDotNet.Artifacts/MiniPty.Benchmarks.PtyIntegrationBenchmarks.Session_32KiB_StreamBytes-*.speedscope.json`

## Lessons (implementation)

| Lesson | Detail |
|---|---|
| `Monitor.Wait` on `_sync` in stall loop | Broke Windows `ReadOutputAsync` output delivery; reverted. |
| `IsExitWaitActive` after `await WaitForExit` | Races with concurrent `WaitForExitAsync`; capture flag before `await`. |
| `CloseOutputTransport` under `_sync` | Deadlocks producer `Complete()`; always call outside buffer lock. |
| `_producer.Wait()` in `Dispose` | Deadlocks with `ProduceAsync` finally awaiting exit observer; keep `ContinueWith` or equivalent non-blocking cleanup. |
| Incremental commits | Hang during exploration traced to uncommitted drain changes; always gate each commit with tests + bench. |
| Sync observer on producer thread | `ObserveExitForOutputDrainAsync` must `await Task.Yield()` before blocking exit poll; `ProduceAsync` assigns the task without awaiting. |
| `WaitForExitAsync` evaluated before `await` | `await AwaitExitAsync(_backend.WaitForExitAsync(...))` runs sync `Poll` on the caller thread; blocks dispose test and starves concurrent reads. Fix: `await Task.Yield()` in `WaitForExitInternalScopedAsync` only; backend keeps `TryRefreshExitState` fast path + `Task.FromResult`. |
| `Task.Yield` in backend `WaitForExitAsync` | Adds ~0.36 KB to `Exit0` / `OutputStream` vs fast-path `Task.FromResult`; do not yield on already-exited children. |

## Post-merge

- Update `integration.json` once if Windows allocations improved.
- Add a short cross-OS note to `pty_crossplatform.md` (ConPTY drain polling, why `Thread.Sleep` not `Task.Delay`).
- Link resolved outcome back to [plan_minipty_next.md](plan_minipty_next.md) Gate C / follow-up section.
- Phase 2 coalescing: [plan_win_coalesce.md](plan_win_coalesce.md) (separate PR).
