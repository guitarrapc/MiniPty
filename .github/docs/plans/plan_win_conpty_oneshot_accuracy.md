# Windows ConPTY One-shot Accuracy Plan

## Goal

Reduce poor one-shot completion latency on Windows ConPTY without regressing output correctness or allocation behavior.

Scope is strictly Windows one-shot completion paths (`PtySession.CompleteAsync` and `PtyCapture.RunAsync` via transport pump). This plan does not change shell command-completion semantics and does not treat idle as command completion.

## Context

Current behavior uses post-exit drain timing that can impose a fixed-feeling ~1s wait on short outputs in Windows ConPTY scenarios. The root issue is not process-exit detection, but post-exit transport drain behavior when blocking reads do not naturally complete immediately.

`pty-output-completion-design.md` conclusions are adopted here:

- PTY cannot perfectly detect "output completed" from bytes alone.
- Idle/stall heuristics are acceptable only as transport-drain control, not as command completion truth.
- Shell command completion remains a separate concern (sentinel/high-level layer), out of scope for this change.

## Final Decisions (Locked)

- `closeTransportOnExit` removal: **deferred** (no change in this plan).
- One-shot post-exit strategy: **A2** (stall-based early close + micro-window).
- OS scope: **B1** (Windows only).
- API exposure: **C2** (no new public option now; internal constant).
- Documentation policy: **E1** (update docs to reflect real semantics).
- Stall threshold: **100ms**.
- Stall start condition: **F3** (`now - exitObservedAt >= 100ms` and `now - lastReadAt >= 100ms`).
- Apply path: **G1** (all Windows one-shot transport pumps, decode on/off).
- Implementation location: **J1** (`PtyOutputDrain.AwaitPumpAsync`).
- Progress signal plumbing: **M1** (pass transport stream; no delegate/closure).
- Progress update rule: **N1** (update on all successful `ReadTransport` reads).
- Poll interval: **P1** (10ms).
- Micro-window: **Q1** (best-effort 1ms window using standard timing primitives, no high-resolution timer API).
- Tests: **R3** (add 2 Windows-focused tests).
- Gap test source: **T1** (PowerShell `Start-Sleep -Milliseconds 50`).
- Acceptance metric: **U1** (ShortRun `Mean` + allocation checks).
- Benchmark execution: **V2** (single full run).
- Targets: **H2 + I1** (`Echo` means under 200ms; full baseline refresh).

## Non-Goals

- No new high-level shell API (no sentinel execution layer in this plan).
- No change to Unix behavior.
- No kernel32 high-resolution timer introduction (`timeBeginPeriod` etc. not used).
- No broad refactor of completion architecture.

## Design

### 1) Progress Tracking (No New Allocation Pressure)

Store read-progress timestamp on transport stream implementations, not in one-shot-specific session state.

- Add internal read-progress tick field to `PtyHandleReadStream` (and keep parity-safe shape for `PtyFdReadStream` if needed).
- Update tick whenever `ReadTransport` returns `read > 0`.
- Do not add delegate/closure callbacks.
- Do not add one-shot-only state to `PtySession`.

Rationale: preserve data-oriented locality, avoid per-call allocations, and keep responsibility where transport reads occur.

### 2) Windows-only Early Close in `AwaitPumpAsync`

After process exit is already observed by completion orchestration:

- Enter post-exit wait loop in `AwaitPumpAsync`.
- For Windows transport streams, evaluate:
  - `now - exitObservedAt >= 100ms`
  - `now - lastReadAt >= 100ms`
- If both hold, close output transport early.
- Keep 10ms polling cadence.
- Keep best-effort 1ms micro-window before deciding no-progress close.

Important semantic boundary:

- This heuristic controls **drain closure timing only**.
- It is **not** a completion oracle for shell command semantics.

### 3) Timeout Semantics

Keep existing timeout contracts, with clarified meaning:

- `OutputDrainGrace`: post-exit drain budget upper bound (not fixed mandatory sleep).
- `OutputReaderCloseTimeout`: wait budget after transport close.

No public API additions in this phase.

## File-Level Change Plan

- `src/MiniPty/Internal/PtyStreams.cs`
  - Add read-progress tick storage/accessor on transport stream type.
  - Update tick on successful `ReadTransport` reads (`N1`).

- `src/MiniPty/Internal/PtyOutputDrain.cs`
  - Extend `AwaitPumpAsync` for Windows-only stall-aware early close (`J1`).
  - Implement 100ms threshold (`F3`), 10ms poll (`P1`), and best-effort micro-window (`Q1`).

- `src/MiniPty/Internal/PtyCompletion.cs`
  - Pass transport stream context into updated `AwaitPumpAsync` path (`M1`).

- `tests/MiniPty.Tests/PtyTests.cs`
  - Add Windows test: short one-shot completion returns without effectively consuming full `OutputDrainGrace`.
  - Add Windows test: intermittent output with 50ms gap (`Start-Sleep -Milliseconds 50`) retains tail output.

- `.github/docs/specs/completion.md`
  - Update `OutputDrainGrace` wording to reflect bounded budget semantics and potential early close behavior.

- `.github/docs/specs/lifecycle.md`
  - Add lesson/clarification: Windows one-shot drain uses post-exit quiet heuristic for transport closure timing.

## Validation Plan

1. Run tests:
   - `dotnet test tests/MiniPty.Tests/MiniPty.Tests.csproj -c Release`
   - Ensure all tests pass including new R3 tests.

2. Run full integration benchmark once (`V2`):
   - `dotnet run -c Release --project src/MiniPty.Benchmarks -- --filter "*PtyIntegrationBenchmarks*" --job short`

3. Acceptance checks (`U1`, `H2`):
   - `Session_Echo_Bytes` mean < 200ms (ShortRun).
   - `Capture_Echo_Bytes` mean < 200ms (ShortRun).
   - `Session_32KiB_Bytes` and `Capture_32KiB_Bytes` preserve full-read behavior (no truncation regression).

4. Baseline refresh (`I1`):
   - Update `BenchmarkDotNet.Artifacts/baselines/integration.json` from full run.
   - Run `scripts/compare-benchmark-allocations.ps1` against refreshed baseline.

## Risks and Mitigations

- Risk: premature close truncates delayed tail bytes.
  - Mitigation: F3 condition + micro-window + R3 intermittent-output test.

- Risk: allocation regression from signaling/plumbing changes.
  - Mitigation: avoid delegate/closure; use transport-local primitive tick state.

- Risk: semantic confusion ("idle means complete").
  - Mitigation: E1 docs update explicitly separates drain heuristic from command completion.

## Rollback Criteria

Rollback/rework if any of the following occur:

- New tail-truncation failures in tests.
- `Session_32KiB_Bytes` / `Capture_32KiB_Bytes` regress in correctness.
- Allocation regressions on unaffected paths not explained by measurement noise.
- `Echo` benchmarks fail to meet <200ms target after stabilization.

## Follow-up (Out of This Plan)

- Revisit `closeTransportOnExit` internal API simplification separately.
- Consider explicit idle API and/or higher-level sentinel shell layer as future work, not part of this patch.
