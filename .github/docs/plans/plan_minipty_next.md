# MiniPty Next Plan: Persistent PTY Sessions

Planning notes for evolving MiniPty from a one-shot-oriented PTY runner into a persistent PTY transport while keeping the core package small, NativeAOT-friendly, and dependency-light.

This document is a working plan, not an implemented API contract. After each implemented milestone, update the relevant implemented specs and keep [spec.md](../spec.md) as the entry-point document map.

## Summary

MiniPty already creates real pseudo-terminals and exposes raw `Input` / `Output` streams. The missing layer is not PTY creation itself; it is the supported, documented lifecycle for long-lived bidirectional sessions.

The recommended direction is:

| Package | Responsibility |
|---|---|
| **MiniPty** | Core PTY transport: spawn, persistent read/write, resize, exit, kill, lifecycle, one-shot convenience |
| **MiniPty.Capture** | One-shot timestamped output capture and observation helpers |
| **MiniPty.Console** | Optional future package: attach a `PtySession` to the current console with raw mode and resize tracking |
| **Samples / future hosting helpers** | WebSocket, xterm.js, ASP.NET Core, or other frontend integration examples |

Do not split persistent PTY support into a package such as `MiniPty.Persistent`. Persistence is a property of PTY sessions themselves: `Pty.Start` already creates a long-lived child attached to a PTY. The core package should become a PTY transport library, with `CompleteAsync` remaining as a convenience API for one-shot use.

## Current State

MiniPty currently provides:

- Cross-platform PTY spawn: Windows ConPTY; Unix `forkpty` / native shim.
- Raw `Input` and `Output` streams on `PtySession`.
- `WriteInputAsync`, `SendEof`, `Resize`, `WaitForExitAsync`, `Kill`, and disposal.
- `CompleteAsync` for one-shot input, wait, output pump, drain, and result materialization.
- `MiniPty.Capture` for timestamped one-shot observation.
- Display helpers for host-readable text from PTY output.

Current public messaging treats these capabilities as one-shot oriented. Long-lived interactive sessions, ongoing bidirectional input, and Vim/less/REPL-style usage are documented as out of scope.

The practical reality is slightly different: low-level streams already allow manual bidirectional operation, but MiniPty does not yet provide a first-class, tested, lifecycle-safe persistent session API.

## node-pty Comparison

[node-pty](https://github.com/microsoft/node-pty) is primarily a terminal-backend library. It is designed to sit behind VS Code terminals, xterm.js, Electron apps, and browser terminal frontends.

| Capability | node-pty | MiniPty today | MiniPty target |
|---|---|---|---|
| Spawn PTY child | Yes | Yes | Core |
| Persistent session model | Primary model | Low-level only | Core |
| Write anytime | `write(data)` | `WriteInputAsync` | Core, documented for persistent use |
| Realtime output | `onData` event | Manual `Output.ReadAsync` | Core streaming API |
| Exit notification | `onExit` | `WaitForExitAsync`, polling | Core exit status/notification |
| Resize | `resize(cols, rows, pixelSize?)` | `Resize(PtySize)` | Core; consider pixel size |
| Environment block | `env` | Not supported | Core |
| Terminal name / `TERM` | `name` | Not supported | Core |
| Encoding option | String/Buffer mode | Decode mainly in completion/capture | Bytes-first core; optional text helpers |
| Flow control | `pause`, `resume`, XON/XOFF handling | Not supported | Consider after streaming API |
| Unix uid/gid | Supported | Not supported | Optional/future |
| Unix `openpty` without spawn | Supported | Not supported | Optional/future |
| Windows ConPTY clear | Supported | Not supported | Optional/future |
| Host console raw mode | Not the core responsibility | Not supported | Separate `MiniPty.Console` |
| Terminal rendering | External frontend | Not supported | Remains external |

Important distinction: node-pty does not implement a terminal emulator. Vim and htop work through node-pty because node-pty moves bytes in real time between the child PTY and a terminal frontend. Rendering, screen buffers, keyboard interpretation, and mouse handling belong to the frontend or host console adapter.

## Layered Problem Model

Persistent PTY support has three related but separate layers:

| Layer | Description | Package boundary |
|---|---|---|
| Persistent PTY transport | Keep a child running, read output continuously, write input anytime, resize, wait, kill | **MiniPty** |
| Console attach | Connect current process console to a PTY session, enable raw/VT modes, forward resize | Future **MiniPty.Console** |
| Terminal frontend/emulator | Render ANSI/VT output, maintain screen buffer, map rich input events | External app/library or sample integration |

The next MiniPty work should focus first on the transport layer. Console attach and terminal rendering should not drive the core API shape prematurely.

## Package Boundary Decisions

### Keep in `MiniPty`

- PTY spawn and backend lifetime.
- Raw byte input and output.
- Persistent output streaming primitive.
- Write, resize, wait, exit status, kill, dispose.
- Environment and terminal-name spawn options.
- Cancellation, drain, close, and error semantics for persistent sessions.
- One-shot `CompleteAsync` as a convenience built on the same primitives.

### Keep in `MiniPty.Capture`

- Timestamped one-shot capture.
- Chunk timelines.
- Capture-specific result materialization.
- Observation helpers that should not burden core callers.

### Put in a future `MiniPty.Console`

- Host console raw mode.
- Windows console VT input/output mode changes.
- Unix `termios` mode changes and restoration.
- Console input forwarding.
- Console output forwarding.
- Terminal resize detection and forwarding.
- Ctrl+C, Ctrl+D, Ctrl+Z, paste, and special-key policies.

### Leave outside core

- Terminal emulator or VT screen buffer.
- xterm.js-specific protocol wrappers.
- WebSocket server integration.
- Remote shell/tunnel management.
- UI framework integration.

These can be samples, adapters, or separate packages if repeated demand appears.

## API Direction

MiniPty should stay bytes-first. PTY output is a terminal byte stream, and escape sequences can span reads. Text helpers can remain optional.

### Start Options

Extend `PtyStartInfo` with persistent-terminal essentials:

```csharp
public sealed record PtyStartInfo
{
    public required string FileName { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    public PtySize Size { get; init; } = new(80, 24);

    public IReadOnlyDictionary<string, string?>? Environment { get; init; }
    public string? TerminalName { get; init; }
}
```

Open questions:

Resolved Milestone 1 decisions:

- `Environment = null` inherits the parent environment.
- A non-null `Environment` is an overlay on top of the parent environment, not a replacement block. This intentionally differs from node-pty, where callers commonly pass `process.env` or build their own overlay before spawn.
- Environment values use `null` to remove a variable and `""` to set an empty variable where the platform preserves empty environment variables. On Windows, empty environment entries are observed by children as missing, matching the OS environment model.
- Environment keys are case-insensitive on Windows and case-sensitive on Unix.
- Invalid environment keys or values are rejected at spawn time: empty keys, keys containing `=`, and keys or values containing NUL are invalid.
- Duplicate-equivalent overlay keys are resolved by enumeration order; the last value wins.
- `TerminalName` is a `string?` convenience for terminal identity. Empty string is treated as unspecified; NUL is invalid.
- On Unix, `TerminalName` sets `TERM` after the environment overlay. If no `TERM` remains and it was not explicitly removed, MiniPty sets `TERM=xterm-256color`.
- On Windows, `TerminalName` has no effect for now. It does not set `TERM`; callers may still set `Environment["TERM"]` explicitly.

Environment construction order:

| Platform | Order |
|---|---|
| Unix | Parent environment -> node-pty-style terminal sanitize -> `Environment` overlay -> `TerminalName` / default `TERM` |
| Windows | Parent environment -> `Environment` overlay |

Unix terminal sanitize removes inherited terminal-container and size variables that can make a fresh PTY report or use stale terminal state: `TMUX`, `TMUX_PANE`, `STY`, `WINDOW`, `WINDOWID`, `TERMCAP`, `COLUMNS`, and `LINES`. The sanitize step happens before the explicit overlay so advanced callers can still opt back into any of those variables.

Executable lookup decisions:

- Unix native spawn moved from `execvp` to a portable `execvpe`-equivalent path so explicit `envp` can be passed.
- Unix executable lookup uses the final child `PATH` after overlay. If `PATH` is absent, use `/bin:/usr/bin`; if `PATH` is empty, treat it as an empty path entry, matching current-directory lookup semantics. The fixed fallback avoids libc environment/path discovery calls in the post-`forkpty()` child path.
- Windows executable lookup remains delegated to `CreateProcessW`; MiniPty does not reimplement `PATHEXT`, system-directory, or application search rules.
- Windows explicit environment blocks are UTF-16 and require `CREATE_UNICODE_ENVIRONMENT`.
- Windows important variables such as `SystemRoot` are normally preserved by overlay semantics, but if a caller explicitly removes them MiniPty does not restore them.

Security contract:

- Environment inheritance follows normal PTY/process-spawn expectations and is not a security boundary.
- MiniPty does not attempt to detect or scrub secrets automatically. Callers that expose PTYs to untrusted users must isolate the process with OS users, containers, sandboxes, or explicit environment removal.

### Output Streaming

Milestone 2 decisions:

- Keep core bytes-first. No text decode in core streaming API.
- Add a first-class persistent output API on `PtySession`:

```csharp
public readonly struct PtyOutputChunk
{
    public ReadOnlyMemory<byte> Data { get; }
}

public IAsyncEnumerable<PtyOutputChunk> ReadOutputAsync(
    CancellationToken cancellationToken = default);
```

- Use `IAsyncEnumerable<PtyOutputChunk>` as the primary public shape.
- Enforce single active reader. A second concurrent reader attempt throws `InvalidOperationException`.
- Chunk lifetime is ephemeral: `Data` is valid until the next `MoveNextAsync` on that same enumeration.
- Child exit handling: after child exit, drain remaining output and then complete enumeration normally (EOF-style completion).
- Cancellation handling: canceling `ReadOutputAsync` stops the reader only; it does not kill the child.
- Error handling: unexpected transport/read failures complete enumeration with exceptions (`IOException` family; cancellation as `OperationCanceledException`).
- Dispose handling: disposing an active session while streaming causes reader termination with `ObjectDisposedException`.
- Backpressure contract: do not drop data. Use bounded buffering with producer wait.
- Initial implementation constants: internal bounded buffer capacity, max chunk size 16 KiB.
- Keep existing `Output` stream and one-shot APIs during Milestone 2 (no deprecation in this milestone).

Deferred ideas (not in Milestone 2):

- Public advanced reader API (`WaitToReadAsync` + `TryRead`).
- Public channel-based wrapper API.
- Timestamped output in core.

### Exit Status

Keep `WaitForExitAsync` and add a richer status type if Unix signal reporting becomes necessary.

```csharp
public readonly record struct PtyExitStatus(
    int ExitCode,
    string? Signal = null);
```

Open questions:

- Whether signal reporting is worth adding now or should remain future work.
- Whether `ExitCode` should remain the simple cross-platform contract and `PtyExitStatus` be introduced only when needed.

### Persistent Session Convenience

Avoid creating a separate `PtyPermanentSession` type unless it clearly reduces complexity. `PtySession` is already the persistent session object.

Potential additions:

```csharp
public Task<PtyExitStatus> WaitForExitStatusAsync(CancellationToken cancellationToken = default);
public ValueTask WriteInputAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default);
public ValueTask WriteInputAsync(string text, Encoding? encoding = null, CancellationToken cancellationToken = default);
public IAsyncEnumerable<PtyOutputChunk> ReadOutputAsync(CancellationToken cancellationToken = default);
```

Events can be added as convenience later, but the primary API should not depend on event-only semantics.

## Lifecycle Semantics To Specify

Persistent sessions need sharper lifecycle contracts than one-shot completion:

| Area | Milestone 2 decision |
|---|---|
| Concurrent reads | Exactly one active `ReadOutputAsync` reader is allowed |
| Concurrent writes | Keep existing `WriteInputAsync` behavior; caller-ordered by awaiting writes |
| Cancellation | Canceling output enumeration stops reading only; child remains alive |
| Child exit | Stream drains remaining output then completes normally |
| Dispose | Existing dispose behavior remains; active readers terminate with `ObjectDisposedException` |
| EOF | Preserve platform-specific `SendEof`; document limitations for raw/canonical modes |
| Read errors | Unexpected transport failures surface as exceptions from enumeration |
| Backpressure | Bounded buffer, producer wait, and no data drop |
| Buffer ownership | Chunk memory is valid until next `MoveNextAsync` on the same enumeration |

Resolved implementation notes:

- `WaitForExitAsync` cancellation continues to mean stop waiting only.
- `CompleteAsync` remains the API that may kill on cancellation through `PtyCompleteOptions.KillOnCancellation`.

## Implementation Milestones

Milestones are ordered by dependency. **Milestone 3.5 (Capture Alignment)** is deferred and must not start until Milestone 3 and the prerequisites listed there are satisfied. Downstream milestones (4–6) do not depend on 3.5.

### Milestone 1: Spawn Option Parity (implemented)

Goal: make child process environment suitable for terminal applications.

- Add `Environment` to `PtyStartInfo`.
- Add `TerminalName` or equivalent `TERM` support.
- Update Windows `CreateProcessW` to pass an explicit environment block when requested.
- Update Unix native shim path to support `execve` or equivalent explicit environment passing.
- Document inheritance and override rules.
- Add tests for environment inheritance, override, and `TERM`.

Lessons learned while specifying this milestone:

- node-pty passes an explicit environment supplied by the caller; examples commonly pass `process.env` or create an overlay manually. MiniPty should expose overlay semantics directly because .NET callers need a small, hard-to-misuse spawn surface and Windows child startup is fragile when critical inherited variables are accidentally omitted.
- Parent environment inheritance is normal PTY behavior, not a sandbox. Removing inherited secrets must be an explicit caller policy, while untrusted PTY hosting requires process isolation beyond environment filtering.
- Terminal correctness needs a small Unix sanitize step. Inheriting `COLUMNS`, `LINES`, tmux, screen, or termcap variables from the parent can make a new PTY behave as if it still lived inside the parent terminal context.
- `TERM` defaults are useful on Unix but should not override explicit caller intent. `Environment["TERM"] = null` means no `TERM`; `Environment["TERM"] = ""` means an empty `TERM`; `TerminalName` is the only dedicated option that overrides the overlay.
- Windows does not preserve an empty environment variable as a child-visible empty value. Treat `""` as distinct in the API so Unix can express it, but document that Windows children observe it like removal.
- Windows ConPTY currently has no `TerminalName` equivalent. Treating `TerminalName` as Unix-only avoids inventing misleading cross-platform behavior while keeping common `PtyStartInfo` construction portable.
- Passing explicit Unix `envp` changes executable lookup requirements. A portable `execvpe`-equivalent shim keeps Linux, macOS, and FreeBSD behavior aligned without depending on non-portable libc extensions.

### Milestone 2: Persistent Output Streaming (implemented)

Goal: make continuous output consumption a supported core API.

- Add `PtyOutputChunk` (bytes-only).
- Add `PtySession.ReadOutputAsync(CancellationToken)` returning `IAsyncEnumerable<PtyOutputChunk>`.
- Enforce single-reader behavior (`InvalidOperationException` on concurrent reader attempt).
- Treat expected PTY EOF conditions as normal completion after exit+drain.
- Use bounded buffering, no-drop policy, producer wait on pressure.
- Use 16 KiB max chunk size in the initial implementation.
- Define ephemeral chunk lifetime contract (valid until next `MoveNextAsync`).
- Keep existing `Output` stream API as-is in this milestone.
- Add tests for shell/REPL command loops, single-reader violations, cancellation semantics, drain-on-exit, and backpressure behavior.

Lessons learned while specifying this milestone:

- `ReadOutputAsync` is structurally heavier than reading `Output` directly: a bounded managed buffer, background producer, and exit observation are part of the contract, not optional overhead to remove for downstream convenience.
- Benchmarks must compare paths separately (`CompleteAsync`, `ReadOutputAsync`, raw `Output` stream). Collapsing `ReadOutputAsync` into a thin `Output` wrapper changes Milestone 2 semantics and invalidates backpressure tests.

### Milestone 3: Lifecycle Hardening **(implemented)**

Goal: make persistent sessions reliable under cancellation, exit, and disposal.

**Scope:** Specify contracts in `lifecycle.md`, add focused tests, and fix behavior only where tests prove gaps. **No new public readiness APIs** (ConPTY deferral stays internal).

**Delivery:** One PR containing spec updates, tests, implementation, baseline snapshot, and allocation comparison script.

#### Implementation record

| Item | Value |
|---|---|
| Baseline commit (pre-M3) | `7bd4eff80108a927fda9aced2d984cf2282fefcf` |
| Implementation commits | `b12bfcf` (lifecycle core) → `3371ba9` (producer yield) → `3773ebe` (ReadAsync gate) → `b816a19` (parallel tests) |
| Spec | `.github/docs/specs/lifecycle.md` |
| Allocation gate script | `scripts/compare-benchmark-allocations.ps1` |
| Baseline snapshot | `BenchmarkDotNet.Artifacts/baselines/integration.json` (local; `BenchmarkDotNet.Artifacts/` is gitignored — force-add `baselines/` or adjust `.gitignore` before PR) |

**Core implementation (no new public API):**

- Output consumer exclusivity (`ReadOutputAsync` / raw `Output` / `CompleteAsync`) in `PtySession`
- `ThrowIfDisposed` on in-flight wait/write; `CompleteAsync` / pumps use `OutputTransport` (ungated)
- `BoundedOutputBuffer` producer: `await Task.Yield()` before synchronous transport reads (stdin/write concurrency)
- `Output.ReadAsync`: acquire gate synchronously at call start; `Task.Yield` before blocking transport read when pipe is empty

**Tests:** 55/55 green (Release, parallel). `[NotInParallel]` removed; cancellation tests use stdin-blocking children (`read` / `set /p`) instead of short `sleep` to avoid parallel scheduling flakes.

#### Benchmark results (Release, OOP, Windows — 2026-06-25)

Compared to baseline `7bd4eff` via `scripts/compare-benchmark-allocations.ps1`:

| Benchmark | Baseline (B) | M3 (B) | Δ (B) | Gate |
|---|---:|---:|---:|---|
| `Session_Exit0_Bytes` | 3817 | 3410 | −407 | pass |
| `Session_Echo_Bytes` | 4096 | 3584 | −512 | pass |
| `Capture_Echo_Bytes` | 5335 | 4803 | −532 | pass |
| `Session_32KiB_Bytes` | 55890 | 41718 | −14172 | pass |
| `Capture_32KiB_Bytes` | 62095 | 47944 | −14151 | pass |
| `Session_32KiB_StreamBytes` | 47155 | 32993 | −14162 | pass |
| `Session_32KiB_OutputStreamBytes` | 18032 | 3983 | −14049 | pass |
| `Session_Echo_Text` | 4423 | 3912 | −511 | pass |
| `Capture_Echo_Text` | 6835 | 6308 | −527 | pass |
| `Capture_32KiB_Text` | 142684 | 128492 | −14192 | pass |
| `Capture_32KiB_DisplayPlain` | 212869 | 195594 | −17275 | pass |

**Hot-path fix:** `Output.ReadAsync` acquires the gate synchronously on every call; `Task.Yield` runs only on the **first** read of an exclusive raw-output session (`rawHoldActive == 0`). Continuing reads use `ValueTask.FromResult(ReadTransport(...))` with no async state machine.

Latency (`Mean`) remained within +10% on all integration benchmarks in the same run.

#### Resolved contracts

| Area | Decision |
|---|---|
| `Dispose` / `DisposeAsync` | In-flight `ReadOutputAsync`, `WaitForExitAsync`, and `WriteInputAsync` fail immediately with `ObjectDisposedException`. No cooperative wait. Child is killed if still running; handles are released. |
| `ReadOutputAsync` ∥ `WaitForExitAsync` | **Allowed and guaranteed** without deadlock, data loss, or premature transport close. Duplicate `WaitForExitAsync` calls are allowed. |
| Output consumer exclusivity | **Single consumer, bidirectional.** While `ReadOutputAsync` **or** a raw `Output` read is active: a second `ReadOutputAsync`, `CompleteAsync`, or the other output path throws `InvalidOperationException`. No queuing of `CompleteAsync`. |
| Allowed during `ReadOutputAsync` | `WriteInputAsync`, `SendEof`, `WaitForExitAsync`, `Resize`, `Kill`, `Dispose`. |
| `Kill()` during active read | Same as normal child exit: drain remaining output, then `ReadOutputAsync` completes normally (EOF). |
| Cancellation (concurrent ops) | Scoped per operation. Canceling read does not cancel wait (and vice versa). Child is not killed. After cancel, the same session may start a new `ReadOutputAsync` or `WaitForExitAsync`. |
| Timeouts | `ExitTimeout`, `OutputDrainGrace`, and `OutputReaderCloseTimeout` apply to **one-shot** `CompleteAsync` / `PtyCapture.RunAsync` only. Persistent `ReadOutputAsync` and `PtySession.WaitForExitAsync` use caller `CancellationToken` only. |
| ConPTY spawn readiness | Document current internal EOF/write deferral. Add Windows smoke tests for immediate post-`Pty.Start` `WriteInputAsync`, `Resize`, and empty-stdin `SendEof`. Fix only if tests fail. |

#### Definition of done

- [x] `lifecycle.md` updated with operation matrix (dispose, cancel, kill, concurrency, exclusivity, timeouts).
- [x] Tests cover every row in the matrix above (including dispose during wait/write, kill during read, concurrent wait+read, bidirectional output exclusivity).
- [x] Windows ConPTY spawn smoke tests (immediate write / resize / empty `SendEof`).
- [x] Full test suite green (55/55, parallel).
- [x] **Benchmark gate:** `PtyIntegrationBenchmarks` on Release; compare against baseline snapshot at M3 start (`7bd4eff`).
- [x] **Allocation rule:** all Integration benchmarks ≤ baseline `Allocated` (11/11 pass after raw `ReadAsync` hot-path optimization).
- [x] Latency (`Mean`) within +10% vs baseline.
- [ ] Baseline JSON committed under `BenchmarkDotNet.Artifacts/baselines/` (file exists; gitignore blocks — fix before PR) and comparison script in repo (`scripts/compare-benchmark-allocations.ps1`).
- [x] Milestone 3.5 prerequisite “M3 complete” satisfied (allocation gate closed; baseline JSON commit pending).

#### Lessons learned (specification)

- Do not queue or block `CompleteAsync` behind an active output consumer; fail fast with `InvalidOperationException` instead. Queuing hides deadlocks between read, wait, and one-shot completion.
- Persistent and one-shot APIs have different timeout models; do not silently apply `PtyCompleteOptions` drain timeouts to `ReadOutputAsync`.
- `BoundedOutputBuffer` must `await Task.Yield()` before the first synchronous `ReadOutputTransport`; otherwise `ReadOutputAsync` blocks the caller and stdin cannot be written (persistent loop deadlock).
- Output exclusivity for raw `Output.ReadAsync` must acquire the consumer **when `ReadAsync` is invoked**, not when the transport read runs; otherwise a racing `ReadOutputAsync` slips through.
- Parallel lifecycle tests must not use short `sleep`/`ping` windows to assert “child still alive”; use stdin-blocking children instead.
- Raw `Output.ReadAsync` hot path: gate synchronously at call start; defer `Task.Yield` to the first read only—per-read `Yield` was ~3 KB/iteration overhead, not inherent to exclusivity.

### Milestone 3.5: Capture Alignment (deferred; was Milestone 2.5)

**Status: deferred.** Do not start until the prerequisites below are met and recorded in this plan or an ADR.

**Placement rationale:** Originally scheduled immediately after Milestone 2. An integration attempt showed that Capture-on-`ReadOutputAsync` conflicts with Milestone 2 transport semantics and benchmark baselines unless core backpressure is weakened. Lifecycle ordering (stdin, read, exit, drain) also needs Milestone 3 hardening before Capture can migrate safely. Resume after Milestone 3, not in parallel with core transport work.

Goal: migrate safely without expanding Milestone 2 risk.

- Keep `MiniPty.Capture` public API unchanged.
- Incrementally move Capture internals to consume the Milestone 2 core streaming primitive (`ReadOutputAsync`).
- Keep timestamp/decode concerns in Capture, not in core.
- Validate parity with existing one-shot capture behavior via focused tests/benchmarks.
- Keep PRs small and separable from Milestone 2 core transport changes.

**Prerequisites before resuming:**

| Prerequisite | Why |
|---|---|
| Milestone 3 complete (or equivalent lifecycle tests landed) | Capture migration needs stable stdin/read/exit/drain ordering; parallel completion pumps deadlocked against `ReadOutputAsync`. |
| Measured gap documented | Benchmark `Capture` (stream path) vs `Capture` (`ReadOutputAsync` path) vs `Session_32KiB_StreamBytes` on the same machine and commit. |
| Design choice recorded | One of: (a) accept Capture allocation/latency overhead on `ReadOutputAsync`, (b) Milestone 2.x core optimization without weakening backpressure, or (c) Capture-only internal fast path — reviewed before coding. |
| No open Milestone 2 contract changes | `ReadOutputAsync` bounded buffering and producer-wait semantics remain as implemented in Milestone 2 unless a separate core milestone says otherwise. |

**Non-negotiable constraints (do not violate for Capture convenience):**

| Constraint | Detail |
|---|---|
| Do not weaken Milestone 2 `ReadOutputAsync` | Keep bounded managed buffering, producer wait on pressure, no data drop, and up-to-16 KiB chunk delivery. Do not replace with a pull-through `Output` wrapper to fix Capture benchmarks. |
| Do not change core in a Capture-only PR | Changes to `PtySession.ReadOutputAsync` belong in an explicit Milestone 2.x / core PR with separate review — not under a Capture Alignment PR title. |
| Benchmark gate | Capture integration must not regress Capture benchmark allocations vs the pre-migration baseline. If `ReadOutputAsync` is heavier, document the delta and get an explicit plan decision; do not hide it by altering core. |
| Parity contract | Merged `Output` bytes, decoded text, and `ExitCode` must remain equivalent. Per-chunk count and split points are **not** a stability contract. |
| Layering | Timestamping and decode stay in `MiniPty.Capture`. Core stays bytes-first. |
| Orchestration in Capture | Stdin-before-read or caller-thread drain belongs in Capture/completion orchestration, not in shortcuts that bypass Milestone 2 backpressure. |

Lessons learned from the deferred attempt:

- Satisfying Capture allocation targets by removing `BoundedOutputBuffer` traded Milestone 2 backpressure for OS-level PTY blocking and changed chunk sizing — unacceptable without a core milestone revision.
- `ReadOutputAsync` integration cost is real and pre-existing; Milestone 2.5 cannot assume it is free compared to `Output` + `PtyCompletion`.
- A failed integration should stop the milestone and escalate a design choice, not proceed by editing core transport under Capture scope.

### Milestone 4: Interactive Sample

Goal: prove the core API can drive a long-lived process without a console adapter.

- Add a sample that starts a shell or REPL.
- Pump output asynchronously.
- Write multiple commands over time.
- Resize the PTY.
- Exit cleanly.

This sample should not require raw console mode. It demonstrates persistent transport, not full human terminal attachment.

### Milestone 5: Optional node-pty Parity Features

Goal: close targeted feature gaps only if needed by consumers.

- Flow control (`pause` / `resume`, bounded buffering, or XON/XOFF handling).
- Unix `uid` / `gid`.
- Unix pixel size in winsize.
- Unix `openpty` without spawn.
- Windows ConPTY `clear()` behavior.
- Windows ConPTY cursor inheritance option.

These should remain optional until a real consumer needs them.

### Milestone 6: `MiniPty.Console`

Goal: local human operation of programs such as shells, Vim, htop, and less.

- Create a separate package only after the core persistent API is stable.
- Attach console stdin/stdout to a `PtySession`.
- Enable and restore raw/VT modes.
- Forward resize events or poll size changes.
- Define Ctrl+C, Ctrl+D, Ctrl+Z, paste, and special-key behavior.
- Add smoke samples, not broad terminal-emulator claims.

## Testing Strategy

Prioritize deterministic tests before full TUI smoke tests.

| Test class | Examples |
|---|---|
| Persistent command loop | shell receives multiple commands and emits expected markers |
| REPL behavior | start interpreter-like command, send multiple inputs, read prompts/responses |
| Backpressure | large output while reader is active does not deadlock |
| Cancellation | cancel read without killing child; cancel wait without killing child; cancel complete with configured kill behavior |
| Exit/drain | process exits while reader is active; final output is observed |
| Resize | child observes changed terminal size on Unix; Windows smoke verifies call succeeds |
| Environment | `TERM`, custom variables, inheritance/override rules |
| Disposal | pending read/write/wait complete predictably when session is disposed |

TUI programs such as Vim, htop, less, and top should be later smoke tests. They validate byte transport and attach behavior, but they should not be the first correctness oracle because rendering and host terminal behavior introduce extra variables.

## Documentation Updates Needed

As milestones land, update:

- [spec.md](../spec.md): move long-lived sessions from out of scope into implemented scope and add any new spec documents to the document map.
- [core_session.md](../specs/core_session.md): document `PtyStartInfo.Environment`, `TerminalName`, output streaming, and persistent session contracts.
- [lifecycle.md](../specs/lifecycle.md): document persistent cancellation, read, drain, EOF, and disposal semantics.
- [references/pty_crossplatform.md](../references/pty_crossplatform.md): document environment passing, ConPTY readiness decisions, and Unix `execve` details if implemented.
- [README.md](../../../README.md): update features and not-supported sections after the core persistent API is tested.

## Open Questions

- When resuming Milestone 3.5 (Capture Alignment), which design path applies: accept `ReadOutputAsync` overhead, optimize core first (Milestone 2.x), or add a Capture-only internal path?
- Should we later expose an advanced low-allocation reader API (`WaitToReadAsync` + `TryRead`) in addition to `IAsyncEnumerable`?
- Should backpressure limits (buffer upper bound, chunk size) become configurable start/read options?
- Is Unix `uid` / `gid` worth supporting in a minimal AOT-friendly library?
- Does Windows ConPTY require a public ready state or only internal write/resize deferral?
- Should flow control be explicit (`Pause` / `Resume`) or expressed through bounded async streams/channels?
- Should `MiniPty.Console` be a package, a sample, or both at first?

## Guiding Principle

MiniPty should become a small PTY transport library, not a terminal emulator.

Core should guarantee that bytes can move bidirectionally and continuously between parent and child with clear lifecycle semantics. Human console behavior, frontend protocols, and terminal rendering should live outside the core package unless repeated real-world usage proves they belong there.
