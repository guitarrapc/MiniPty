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
- Environment values use `null` to remove a variable and `""` to set an empty variable.
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

- Unix native spawn should move from `execvp` to a portable `execvpe`-equivalent path so explicit `envp` can be passed.
- Unix executable lookup uses the final child `PATH` after overlay. If `PATH` is absent, use `_CS_PATH` or `/bin:/usr/bin`; if `PATH` is empty, treat it as an empty path entry, matching current-directory lookup semantics.
- Windows executable lookup remains delegated to `CreateProcessW`; MiniPty does not reimplement `PATHEXT`, system-directory, or application search rules.
- Windows explicit environment blocks are UTF-16 and require `CREATE_UNICODE_ENVIRONMENT`.
- Windows important variables such as `SystemRoot` are normally preserved by overlay semantics, but if a caller explicitly removes them MiniPty does not restore them.

Security contract:

- Environment inheritance follows normal PTY/process-spawn expectations and is not a security boundary.
- MiniPty does not attempt to detect or scrub secrets automatically. Callers that expose PTYs to untrusted users must isolate the process with OS users, containers, sandboxes, or explicit environment removal.

### Output Streaming

Add a first-class persistent output API. Prefer an async stream or channel over events as the primary primitive so backpressure and cancellation remain explicit.

Possible shape:

```csharp
public readonly record struct PtyOutputChunk(
    ReadOnlyMemory<byte> Data,
    TimeSpan? Time = null);

public IAsyncEnumerable<PtyOutputChunk> ReadOutputAsync(
    PtyReadOptions? options = null,
    CancellationToken cancellationToken = default);
```

Alternative shape:

```csharp
public ChannelReader<PtyOutputChunk> StartOutputReader(
    PtyReadOptions? options = null,
    CancellationToken cancellationToken = default);
```

Preference: start with `IAsyncEnumerable<PtyOutputChunk>` because it matches .NET streaming APIs and avoids owning a background task until enumeration starts. Add a channel wrapper later only if frontend integration needs it.

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

| Area | Decision needed |
|---|---|
| Concurrent reads | Whether only one active `ReadOutputAsync` enumeration is allowed |
| Concurrent writes | Whether multiple `WriteInputAsync` calls are serialized or caller-managed |
| Cancellation | Canceling output enumeration should stop reading only, not kill the child |
| Child exit | Whether output streaming naturally drains until EOF after exit |
| Dispose | Existing behavior kills the child if running; keep this for core consistency |
| EOF | Preserve platform-specific `SendEof`; document limitations for raw/canonical modes |
| Read errors | Distinguish expected PTY EOF/EIO-on-exit from unexpected transport failures |
| Backpressure | State whether slow consumers block the output pump or whether data can be dropped |
| Buffer ownership | Chunks must have stable memory after each read; no rented buffer leakage |

Initial recommendation:

- Allow one output reader at a time.
- Keep writes caller-ordered by awaiting `WriteInputAsync`; add internal serialization only if tests expose interleaving issues.
- Canceling `ReadOutputAsync` stops the enumeration and leaves the child alive.
- `WaitForExitAsync` cancellation continues to mean stop waiting only.
- `Dispose` continues to kill if the child is still running.
- `CompleteAsync` remains the API that may kill on cancellation through `PtyCompleteOptions.KillOnCancellation`.

## Implementation Milestones

### Milestone 1: Spawn Option Parity

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
- Windows ConPTY currently has no `TerminalName` equivalent. Treating `TerminalName` as Unix-only avoids inventing misleading cross-platform behavior while keeping common `PtyStartInfo` construction portable.
- Passing explicit Unix `envp` changes executable lookup requirements. A portable `execvpe`-equivalent shim keeps Linux, macOS, and FreeBSD behavior aligned without depending on non-portable libc extensions.

### Milestone 2: Persistent Output Streaming

Goal: make continuous output consumption a supported core API.

- Add `PtyOutputChunk`.
- Add `PtySession.ReadOutputAsync`.
- Define single-reader behavior.
- Treat expected PTY EOF conditions as normal completion.
- Ensure chunks own stable memory.
- Add shell/REPL tests that read prompt/output while writing multiple commands.

### Milestone 3: Lifecycle Hardening

Goal: make persistent sessions reliable under cancellation, exit, and disposal.

- Specify and test cancellation of read, wait, and completion separately.
- Test child exit while output reader is active.
- Test dispose while read/write/wait operations are pending.
- Test output drain after process exit.
- Review Windows ConPTY startup readiness and whether write/resize deferral is needed.

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

- Should persistent output streaming return only raw bytes, or should it optionally decode text chunks with a `Decoder` like capture does?
- Should `ReadOutputAsync` include timestamps, or should timestamps remain exclusive to `MiniPty.Capture`?
- Should environment dictionaries replace the full environment or overlay the parent environment?
- Should `TerminalName` be a first-class property, or should callers set `TERM` through `Environment`?
- Is Unix `uid` / `gid` worth supporting in a minimal AOT-friendly library?
- Does Windows ConPTY require a public ready state or only internal write/resize deferral?
- Should flow control be explicit (`Pause` / `Resume`) or expressed through bounded async streams/channels?
- Should `MiniPty.Console` be a package, a sample, or both at first?

## Guiding Principle

MiniPty should become a small PTY transport library, not a terminal emulator.

Core should guarantee that bytes can move bidirectionally and continuously between parent and child with clear lifecycle semantics. Human console behavior, frontend protocols, and terminal rendering should live outside the core package unless repeated real-world usage proves they belong there.
