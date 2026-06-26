# Plan: `ReadOutputAsync` producer coalescing (Phase 2)

Follow-up to [plan_win_alloc.md](plan_win_alloc.md) (Phase 1: ConPTY **drain** polling). Phase 1 removed allocating `Task.Delay` / `Task.Yield` waits (~−2.17 KB on `Session_32KiB_StreamBytes`) but did **not** address **per-handoff async multiplication** (~125 chunks × ~166 B/chunk ≈ ~20 KB on Windows).

**Delivery:** **Separate PR** from Phase 1. Merge Phase 1 first; implement coalescing on a new branch.

Related specs: [core_session.md](../specs/core_session.md) (strict handoff, no-drop), [lifecycle.md](../specs/lifecycle.md) (`ReadOutputAsync` ∥ `WaitForExitAsync`, dispose), [plan_minipty_next.md](plan_minipty_next.md) (chunk boundaries not a stability contract).

## Summary (grill-me decisions)

| Item | Choice |
|---|---|
| **Problem** | ConPTY + typical children deliver **~125 micro-reads** (~297 B avg) per 32 KiB benchmark; each read → one `Handoff` → ~166 B async overhead. Linux `head -c` path ≈ **8 reads**. |
| **Primary lever** | **Producer coalescing** in `BoundedOutputBuffer.ProduceAsync` — not transport-only (cannot force ConPTY to return 4 KiB per `ReadFile`). |
| **Scope trigger** | **Peek-based**, not OS-branched: coalesce when partial reads continue while pipe has more bytes. Linux large reads → no extra coalescing, behavior unchanged. |
| **Buffer strategy (v1)** | **B first:** accumulate multiple reads into the existing **4 KiB** `PtyReadBuffer` before one `Handoff` (no copy). **A later** if needed: second pool buffer to reach 16 KiB max chunk. |
| **Flush policy** | **C — Greedy + peek:** read into `offset`; while `offset < buffer.Length`, peek; if more bytes available, `ReadFile`/`read()` again; else handoff partial buffer. Also handoff on buffer full or EOF (`read == 0`). |
| **Peek API placement** | **B — Transport streams:** `PtyHandleReadStream.TryGetAvailableBytes` (Windows `PeekNamedPipe`), `PtyFdReadStream.TryGetAvailableBytes` (Unix `ioctl(FIONREAD)`). Dispatch from `PtySession` like `ReadOutputTransport`. |
| **v1 handoff cap** | **4 KiB** — measure; if `Session_32KiB_StreamBytes` still **> 15 KB**, add **A** (16 KiB coalesce) in follow-up. |
| **Stretch target** | `Session_32KiB_StreamBytes` **≤ 15 KB** (Windows ShortRun). |
| **Non-goals** | Changing public `PtyOutputChunk` API; weakening no-drop / strict handoff; benchmark child swap as merge gate. |

## Problem (profiler recap)

See [Profiler findings](plan_win_alloc.md#profiler-findings-2026-06-26-windows) in Phase 1 plan.

| Benchmark (Windows ShortRun) | Allocated | Δ vs `Exit0` |
|-----------------------------|----------:|-------------:|
| `Session_Exit0_Bytes` | 3.44 KB | — |
| `Session_32KiB_OutputStreamBytes` | 3.78 KB | +0.34 KB |
| `Session_32KiB_StreamBytes` | 24.04 KB | +20.60 KB |

**~86% of `StreamBytes` is `ReadOutputAsync` handoff overhead**, not spawn or raw transport I/O.

Estimated win from 125 → ~9 handoffs (4 KiB cap, ~37 KiB delivered): **~19 KB** async overhead removed → theoretical **~5 KB** `StreamBytes` (plus fixed producer cost). Validate with benchmarks.

## Constraints

- NativeAOT-safe; no new dependencies.
- Preserve strict handoff: producer may read multiple times **only while consumer is waiting** for the current chunk; single `Handoff` per coalesced slice; block until `Advance`.
- No-drop semantics unchanged.
- `CloseOutputTransport` not under `BoundedOutputBuffer._sync`.
- Chunk **count and split points** remain **non-contractual** ([plan_minipty_next.md](plan_minipty_next.md)).
- All **61** tests pass; no allocation regression on B-set vs Phase 1 end state.
- Interactive latency: peek must flush partial buffer when **no more bytes immediately available** (do not block waiting to fill 4 KiB before first handoff).

## Design

### ProduceAsync read loop (v1)

Replace per-read `Handoff`:

```text
offset = 0
loop while consumer waiting (existing WaitUntilReadyToRead):
  read = ReadOutputTransport(buffer[offset..])        // blocking when offset == 0
  if read <= 0: break
  offset += read
  if offset == buffer.Length: break
  read = TryReadOutputTransportIfReady(buffer[offset..])  // non-blocking continuation
  if read <= 0: micro-window retry, then break
Handoff(buffer[0..offset])
WaitForHandoffCleared (existing)
```

**Implemented (Windows ConPTY):** `TryReadOutputTransportIfReady` uses `PIPE_NOWAIT` because `PeekNamedPipe` is unreliable on anonymous ConPTY pipes; a 1 ms micro-window batches micro-slices without blocking the first byte. Unix uses `FIONREAD` via `minipty_peek_readable_bytes` before continuation reads.

- **EOF:** `read == 0` with `offset > 0` → handoff remainder, then complete.
- **Peek failure / no immediate bytes:** handoff partial (safe fallback; do not spin).

### Transport peek

| Platform | API | Notes |
|----------|-----|-------|
| Windows | `PeekNamedPipe` via `WindowsInterop` | On ConPTY output pipe handle |
| Unix | `ioctl(FIONREAD)` on master fd | Existing `PtyFdReadStream` fd |

Add `PtySession.TryGetAvailableOutputBytes()` internal dispatch mirroring `ReadOutputTransport`.

### Phase 2b (conditional)

If v1 benchmark gate misses 15 KB:

- **A:** extend coalesce buffer to **16 KiB** (Milestone 2 max chunk) via second `ArrayPool` rent or grow buffer — **copies** from 4 KiB read buffer into coalesce buffer; spec note in `core_session.md`.

## Commit plan

### Commit 1 — Transport peek + session dispatch

**Files:** `PtyStreams.cs`, `WindowsInterop.cs`, `PtySession.cs` (dispatch), Unix ioctl helper if needed.

**Gates:** 61/61 tests; B-set allocations **≤ Phase 1 end** (peek alone must not regress).

### Commit 2 — Producer coalesce loop

**File:** `PtySession.cs` (`BoundedOutputBuffer.ProduceAsync`).

**Gates:** 61/61 tests; B-set monotonic decrease vs Commit 1; focus tests:

- `PtyReadOutputAsyncReadsBytesUntilExit`
- `PtyReadOutputAsyncConcurrentWithWaitForExitAsync`
- `PtyReadOutputAsyncDrainsLargeOutputWithoutDropping`
- `PtyLargeOutputDoesNotBlock`

### Commit 3 (conditional) — 16 KiB coalesce buffer (A)

Only if Commit 2 `StreamBytes` **> 15 KB**.

## Verification

```powershell
dotnet test tests/MiniPty.Tests/MiniPty.Tests.csproj -c Release

dotnet run -c Release --no-launch-profile --project src/MiniPty.Benchmarks/MiniPty.Benchmarks.csproj -- `
  --filter "*Session_32KiB_StreamBytes" --job short
dotnet run -c Release --no-launch-profile --project src/MiniPty.Benchmarks/MiniPty.Benchmarks.csproj -- `
  --filter "*Session_32KiB_OutputStreamBytes" --job short
dotnet run -c Release --no-launch-profile --project src/MiniPty.Benchmarks/MiniPty.Benchmarks.csproj -- `
  --filter "*Session_Exit0_Bytes" --job short

# Final
dotnet run -c Release --no-launch-profile --project src/MiniPty.Benchmarks/MiniPty.Benchmarks.csproj -- `
  --filter "*PtyIntegrationBenchmarks*" --job short
```

Record chunk count diagnostic (optional): transport reads and `ReadOutputAsync` chunks should drop from ~125 toward ~9 on Windows `SmallStdout`.

## Success criteria

| Criterion | Required |
|---|---|
| 61/61 tests | Yes |
| B-set ≤ Phase 1 end (no regression) | Yes |
| `StreamBytes` monotonic decrease | Yes |
| `StreamBytes` ≤ 15 KB (Windows ShortRun) | **Stretch** |
| `OutputStreamBytes`, `Exit0` ≤ Phase 1 end | Yes |
| Interactive latency | No deliberate fill-4KiB-before-first-byte blocking |

## Post-merge

- Update `integration.json` if Windows allocations improve materially.
- Short note in `core_session.md`: producer may coalesce multiple transport reads into one handoff; peek-based flush; max chunk size still bounded by implementation.
- Link from [plan_win_alloc.md](plan_win_alloc.md) and [plan_minipty_next.md](plan_minipty_next.md).

## Lessons (pre-implementation)

| Lesson | Detail |
|---|---|
| Transport-only fix insufficient | `ReadFile` already uses 4 KiB buffer; ConPTY returns ~300 B available per call. |
| Linux parity via transport | Not realistic for PowerShell/Console children; library-side coalesce normalizes handoff count. |
| Partial read + blocking second read | Without peek, coalescing delays first byte to consumer; peek flush preserves REPL latency. |
| ConPTY `PeekNamedPipe` unreliable | Byte-count and buffer peek often return 0 while more data arrives microseconds later. Use `PIPE_NOWAIT` + `TryReadTransportIfReady` for non-blocking continuation reads, plus a 1 ms micro-window (`Thread.Sleep(0)`) to batch ConPTY micro-slices without blocking the first byte. |

## Benchmark log (Windows ShortRun, 2026-06-26)

| Benchmark | Phase 1 end | Coalesce PR | Δ |
|-----------|------------:|------------:|--:|
| `Session_32KiB_StreamBytes` | 24.04 KB | **6.14 KB** | **−17.9 KB** |
| `Session_32KiB_OutputStreamBytes` | 3.78 KB | 3.69 KB | −0.09 KB |
| `Session_Exit0_Bytes` | 3.44 KB | 3.69 KB | +0.25 KB |

Stretch target `StreamBytes` ≤ 15 KB: **met**. Commit 3 (16 KiB buffer) not required.
