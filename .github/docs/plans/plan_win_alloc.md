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

## PR start baseline (recorded before Commit 1)

Windows 11, Ryzen 9 7950X3D, .NET 10.0.9, ShortRun, 2026-06-26.

| Benchmark | Allocated | Notes |
|---|---:|---|
| `Session_32KiB_StreamBytes` | **26.21 KB** | primary gate |
| `Session_32KiB_OutputStreamBytes` | **3.91 KB** | control (transport) |
| `Session_Exit0_Bytes` | **3.44 KB** | control (spawn) |

Commit log (fill as implemented):

| Commit | `StreamBytes` | `OutputStreamBytes` | `Exit0` | Tests |
|---|---:|---:|---:|---|
| PR start | 26.21 KB | 3.91 KB | 3.44 KB | — |
| 1 — `Thread.Sleep` stall poll + `concurrentExitWait` fix | 24.17 KB | 3.91 KB | 3.44 KB | 61/61 |

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

Keep `if (_session.IsExitWaitActive) return;` behavior unchanged.

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

1. **Allocation profiler** — `dotnet run ... --profiler EP` or similar on `Session_32KiB_StreamBytes` to split spawn vs drain vs async state machine.
2. **Further core work** — e.g. shrink `ProduceAsync` / `IAsyncEnumerable` state machines (higher risk).
3. **Benchmark isolation** — optional PowerShell child change to separate measurement from library cost.
4. **Stop** — revert or withhold PR until a new plan is agreed.

Document the chosen path and measured residual in this file and in `pty_crossplatform.md`.

## Lessons (pre-implementation)

| Lesson | Detail |
|---|---|
| `Monitor.Wait` on `_sync` in stall loop | Broke Windows `ReadOutputAsync` output delivery; reverted. |
| `IsExitWaitActive` after `await WaitForExit` | Races with concurrent `WaitForExitAsync`; capture flag before `await`. |
| `CloseOutputTransport` under `_sync` | Deadlocks producer `Complete()`; always call outside buffer lock. |
| `_producer.Wait()` in `Dispose` | Deadlocks with `ProduceAsync` finally awaiting exit observer; keep `ContinueWith` or equivalent non-blocking cleanup. |
| Incremental commits | Hang during exploration traced to uncommitted drain changes; always gate each commit with tests + bench. |

## Post-merge

- Update `integration.json` once if Windows allocations improved.
- Add a short cross-OS note to `pty_crossplatform.md` (ConPTY drain polling, why `Thread.Sleep` not `Task.Delay`).
- Link resolved outcome back to [plan_minipty_next.md](plan_minipty_next.md) Gate C / follow-up section.
