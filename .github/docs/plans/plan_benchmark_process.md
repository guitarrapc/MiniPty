# Plan: Unified benchmark child process

Working plan for fair cross-OS `PtyIntegrationBenchmarks` comparisons. This is a **plan**, not a library API contract. Library-side Windows drain and coalescing work is recorded in [core_session.md](../specs/core_session.md) and [pty_crossplatform.md](../references/pty_crossplatform.md).

Related: [plan_minipty_next.md](plan_minipty_next.md) (Milestone 3.5 allocation gates), `scripts/compare-benchmark-allocations.ps1`.

## Summary

| Item | Choice |
|---|---|
| **Problem** | Bulk stdout benchmarks used **PowerShell on Windows** and **`head -c /dev/zero` on Unix**. That skewed latency, delivered byte count, and PTY read granularity, so CI could not compare library cost fairly. |
| **Scope** | **`SmallStdout` only** for v1 — the child behind all `*32KiB*` integration benchmarks. `Echo` / `Exit0` stay shell-based (small spawn asymmetry is acceptable). |
| **Approach** | Ship **`MiniPty.Benchmarks.Child`**: a tiny console executable invoked directly (no shell) with `--bytes <count>`, writing zero bytes in 4 KiB chunks to stdout. Use it on **every OS**. |
| **Non-goals** | Changing MiniPty public API; replacing test-only PowerShell children in `MiniPty.Tests`; NativeAOT-publishing the benchmark child (regular `dotnet build` is enough). |

## Why unify

Integration benchmarks measure end-to-end PTY sessions. When the child differs by OS:

- **Latency** — PowerShell startup and `[string]::new` dominated Windows `*32KiB*` means (often ~200 ms vs ~11 ms on Linux).
- **Bytes on the wire** — PowerShell wrote UTF-16-backed `'x'` text (~37 KiB for a 32 KiB target), not 32 KiB of zeros.
- **Read granularity** — ConPTY returned ~125 micro-reads (~297 B avg) vs ~8 reads for `head -c`, inflating handoff counts before library coalescing.

Those effects hid the residual **ConPTY vs Unix** library gap behind benchmark harness noise. The two primary backend scenarios — `Session_32KiB_StreamBytes` (`ReadOutputAsync`) and `Session_32KiB_OutputStreamBytes` (raw transport) — need the same child on every runner.

## Design

### Benchmark child contract

| Property | Value |
|---|---|
| Entry | `MiniPty.Benchmarks.Child --bytes <n>` |
| stdout | Exactly `n` zero bytes, binary stream |
| stderr | Usage text only on failure |
| exit code | `0` on success; `2` on bad args |
| write pattern | 4 KiB chunks (matches `PtyReadBuffer` size) |

### Build and deployment

- Project: `src/MiniPty.Benchmarks.Child/` (no MiniPty dependency).
- `MiniPty.Benchmarks` references the child with `ReferenceOutputAssembly=false` and copies the built executable into its output directory after build.
- CI `dotnet build -c Release` then `dotnet run --no-build` on benchmarks must find the child next to the benchmark assembly.

### Benchmark wiring

`BenchmarkPtyCommands.SmallStdout(int byteCount)` spawns the child directly on all platforms. Missing child → `FileNotFoundException` with a rebuild hint (fail fast in local/CI setup).

## Success criteria

| Criterion | Required |
|---|---|
| Same child binary contract on Linux, macOS, Windows | Yes |
| `SmallStdout` no longer uses PowerShell or `head` | Yes |
| All existing tests pass (tests unchanged) | Yes |
| Windows `*32KiB*` mean latency drops toward Linux order-of-magnitude | Stretch — validate in CI |
| Cross-OS allocation ratios for `StreamBytes` / `OutputStreamBytes` reflect library cost, not child skew | Yes — qualitative review of next CI run |

## Verification

```powershell
dotnet build -c Release

dotnet test tests/MiniPty.Tests/MiniPty.Tests.csproj -c Release

dotnet run -c Release --no-launch-profile --project src/MiniPty.Benchmarks/MiniPty.Benchmarks.csproj -- `
  --filter "*PtyIntegrationBenchmarks*" --job short
```

After CI publishes new artifacts, compare Ubuntu vs Windows Job Summary tables for `Session_32KiB_*` methods. Refresh `BenchmarkDotNet.Artifacts/baselines/integration.json` only when the team accepts new baseline numbers.

## Lessons learned (prior work, now in specs)

Windows `ReadOutputAsync` allocation work (drain polling without allocating waits, producer coalescing with `PIPE_NOWAIT` / `FIONREAD`) is **implemented** and documented under `core_session.md` / `pty_crossplatform.md`. That work addressed library-side handoff multiplication; this plan addresses **measurement fairness** so remaining cross-OS gaps are interpretable.

| Prior observation | Where recorded |
|---|---|
| ConPTY micro-reads need producer coalescing | `core_session.md` |
| `PeekNamedPipe` unreliable on ConPTY; use `PIPE_NOWAIT` | `pty_crossplatform.md` |
| Stall loop must not use `Monitor.Wait` on buffer lock | `core_session.md` lessons |
| PowerShell benchmark child skewed CI | This plan |

## Post-merge

- Link from [plan_minipty_next.md](plan_minipty_next.md) follow-up section.
- Optional: note in README benchmark section that integration bulk scenarios use the shared child helper.
