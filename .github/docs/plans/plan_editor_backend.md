# MiniPty Editor Backend Plan (Use Case 4)

The "separate plan" deferred from [plan_minipty_next.md](plan_minipty_next.md) (Deferred: Editor Terminal Backend). Goal: make MiniPty usable as the backend PTY (node-pty–equivalent role) for xterm.js and editor terminals such as VS Code.

**Status: implemented.** Contracts live in [specs/terminal.md](../specs/terminal.md) and [specs/core_session.md](../specs/core_session.md); this document records scope decisions and the gap analysis that drove them.

## Gap analysis (node-pty / xterm.js / VS Code)

Research inputs: the node-pty `IPty` API, the xterm.js flow-control guide (ACK watermark protocol), and the VS Code `Pseudoterminal` extension API.

| Needed by frontends | node-pty shape | MiniPty before this plan | Resolution |
|---|---|---|---|
| Push output / exit-after-drain | `onData` / `onExit` | pull (`ReadOutputAsync` / `WaitForExitAsync`) | **MiniPty.Terminal** `PtyTerminal` (handler + `Completion`) |
| Flow control | `pause()` / `resume()` | strict handoff (consumer-driven) | `PtyTerminal.Pause/Resume` — pump gate; no core change needed |
| WebSocket bridging + ACK watermarks | external server code | none | `PtyWebSocketBridge` (binary=data, text=JSON control) |
| Exit signal reporting | `onExit {exitCode, signal}` | exit code only | core `PtyExitStatus` / `WaitForExitStatusAsync` / `ExitStatus` |
| Signal kill | `kill(signal)` | SIGKILL only | core `Kill(PtySignal)` (Windows: advisory, terminates — node-pty parity) |
| ConPTY `clear()` | conpty.dll signal pipe | — | **rejected**: not reachable via public Win32 API; node-pty ships its own conpty.dll |
| Pixel-size resize, uid/gid, `openpty` w/o spawn | supported | — | deferred (no frontend demand yet) |
| Process title tracking (`process`) | supported | — | deferred; needs per-OS foreground-process lookup (tcgetpgrp + /proc, libproc) |

## Scope decisions

- **Single new package `MiniPty.Terminal`** (facade + transport-agnostic bridge; depends on MiniPty only). One package because the bridge is thin and both layers target the same audience; a split (`.Xterm`) would multiply packaging for no dependency win.
- **Core additions limited to exit-status/signal parity** (`PtyExitStatus`, `WaitForExitStatusAsync`, `Kill(PtySignal)`). Flow control did not need core changes: strict handoff already makes "stop consuming" equal "backpressure the child", so pause/resume is a facade-level gate.
- **Sample = browser E2E** ([samples/WebTerminal.cs](../../../samples/WebTerminal.cs)): `HttpListener` + embedded xterm.js page + `--smoke` self-check wired into the CI NativeAOT sample loop. A VS Code extension is documented as a bridging pattern in [terminal.md](../specs/terminal.md), not shipped.

## Implementation record (2026-07-09)

| Stage | Delivered |
|---|---|
| Core parity | `PtySignal`, `PtyExitStatus`, `PtySession.ExitStatus` / `WaitForExitStatusAsync` / `Kill(PtySignal)`; `IPtyBackend.ExitSignal` / `Kill(signal)`; Unix `_termSignal` capture in `TryRefreshExitState`; portable `WIFSIGNALED` fix (see lessons). No native shim change — waitpid decoding was already managed and the exited/signaled status layout is identical on Linux/macOS/FreeBSD. |
| Facade | `PtyTerminal` + `PtyTerminalOptions` (required output handler, `Completion`, `Pause`/`Resume` gate). |
| Bridge | `PtyWebSocketBridge` + `PtyBridgeOptions`; `Internal/BridgeFlowControl` (locked watermark state, `Disable` for teardown), `Internal/BridgeJson` (source-generated `System.Text.Json`). |
| Sample/CI | `samples/WebTerminal.cs` added to the `build.yaml` NativeAOT sample loop; `scripts/pack-with-native.sh` packs the new package; verified interactively in a real browser (input, output, resize, exit banner, NormalClosure). |
| Tests | `PtyTests` exit-status/kill-signal additions; `PtyTerminalTests`; `PtyWebSocketBridgeTests` over `WebSocket.CreateFromStream` on an in-memory duplex stream pair (framing, flow-control stall/resume, exit ordering, close semantics, policy violation). |

## Lessons learned

Contract-level lessons live in [specs/terminal.md](../specs/terminal.md), [core_session.md](../specs/core_session.md), and [lifecycle.md](../specs/lifecycle.md). Plan-level:

- The direct C# transcription of glibc's `WIFSIGNALED` macro loses the signed-char cast and misclassifies the stopped marker `0x7f`; unreachable under WNOHANG-only polling, but it became load-bearing the moment the signal turned into public API. Use `(status & 0x7f) != 0 && (status & 0x7f) != 0x7f`.
- SIGUSR1/SIGUSR2 numbering differs per OS (Linux 10/12, macOS/FreeBSD 30/31), which is why `Kill` takes a `PtySignal` enum mapped per platform instead of a raw int, while `PtyExitStatus.Signal` reports the raw OS number (node-pty parity).
- `PtyExitStatus.ExitCode` deliberately keeps MiniPty's `128 + signal` semantics instead of node-pty's 0-on-signal so a killed child never reads as success and `ExitStatus.ExitCode == ExitCode` always holds.

## Deferred / future

- `Attach(PtySession)` facade overload → prerequisite for bridge reconnect/detach options.
- Process title tracking, pixel-size resize, uid/gid, `openpty` without spawn — revisit on demand.
- ConPTY `clear()` — only viable by shipping conpty.dll (third-party dependency; against AGENTS.md) or if Windows exposes a public API.
