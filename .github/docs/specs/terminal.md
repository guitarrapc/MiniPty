# Terminal Backend Specification

Implemented user-facing contract for the **MiniPty.Terminal** NuGet package.

This package serves **use case 4** in [spec.md](../spec.md): MiniPty as the backend PTY (node-pty–equivalent role) behind a frontend terminal such as xterm.js in a browser or an editor-integrated terminal.

**Status: implemented** (editor backend plan; see [plan_editor_backend.md](../plans/plan_editor_backend.md)).

## Motivation

PTY transport ([Core session](core_session.md)) is pull-based: an embedder enumerates `ReadOutputAsync` and awaits `WaitForExitAsync`. Frontend terminals expect the node-pty shape instead: output pushed to a callback, exit reported after all output, `pause`/`resume` for flow control, and `kill(signal)`.

Bridging to a browser needs one more layer. The WebSocket protocol has no backpressure hooks, so the xterm.js guidance is an ACK-based watermark protocol implemented by the backend: the server pauses the PTY when unacknowledged bytes exceed a high watermark and resumes on client ACKs. **MiniPty.Terminal** provides both layers: a push facade (`PtyTerminal`) and a WebSocket bridge (`PtyWebSocketBridge`) with that flow control built in.

## Package

| Item | Value |
|---|---|
| NuGet | **MiniPty.Terminal** |
| Depends on | **MiniPty** only (BCL otherwise; JSON via source-generated `System.Text.Json`) |
| NativeAOT | Required (same bar as core) |

## Non-Goals

| ID | Out of scope |
|---|---|
| N1 | Terminal emulation (screen buffer, ANSI parsing, scrollback) — the frontend renders |
| N2 | Output recording / timestamping ([Capture](capture.md)) |
| N3 | Host console attach ([Console](console.md); use case 3) |
| N4 | Remote shells (`ssh`), authentication, or multi-user session management |
| N5 | Session reconnect / detach-reattach (v1 bridge owns the session end-to-end; requires an `Attach` overload design first) |
| N6 | HTTP serving — the embedder accepts the WebSocket (Kestrel, `HttpListener`, …) and hands it to the bridge |
| N7 | Windows ConPTY `clear()` — requires the conpty.dll signal pipe node-pty ships; not reachable through the public Win32 API |

## `PtyTerminal` — push facade

```csharp
PtyTerminal.Start(PtyStartInfo startInfo, PtyTerminalOptions options)  // options.Output is required
```

| Member | Contract |
|---|---|
| `Output` handler (options) | `ValueTask handler(ReadOnlyMemory<byte> data, CancellationToken ct)`. Data is valid **only until the returned task completes**; copy to retain. Invoked sequentially. |
| `Completion` | `Task<PtyExitStatus>` that completes **after every output handler invocation has finished** (drain-then-exit, node-pty onData/onExit ordering). Faults with the handler exception when a handler throws (child is killed first). |
| `Pause()` / `Resume()` | Stops/resumes output delivery. Paused delivery parks the pump; the strict-handoff producer stops reading, the OS PTY buffer fills, and the child blocks on write. No managed buffering beyond one in-flight chunk; no drops. |
| `WriteInputAsync` / `SendEof` / `Resize` / `Kill` / `Kill(PtySignal)` | Pass-through to the owned session; callable concurrently with output delivery. |
| `ProcessId` / `Size` / `HasExited` / `ExitStatus` | Session state. `ExitStatus` is readable independently of `Completion` — useful when the pump faulted before draining. |
| `DisposeAsync` | Stops the pump, kills the child if running, releases the PTY. |

Design decisions (WHY):

- **The facade owns its session.** `Start` is the only entry point and the underlying `PtySession` is not exposed. Core allows exactly one output consumer; wrapping an existing session would invite the second-reader and unread-early-output misuse modes. An `Attach(PtySession)` overload can be added later without breaking changes.
- **The output handler is a required start option, not an event.** .NET events cannot carry async completion, so zero-copy delivery with backpressure would be impossible, and attach-after-start races would drop early output. A handler fixed at `Start` eliminates both.
- **Exit is an awaitable `Completion` task, not an event.** It cannot be subscribed too late and composes with `await`.
- **Slow consumers throttle the child for free.** The pump does not advance the core enumerator until the handler's task completes, which satisfies the core chunk-lifetime contract verbatim and turns a slow WebSocket send into PTY backpressure with zero copies.

## `PtyWebSocketBridge` — frontend bridge

```csharp
Task<PtyExitStatus> PtyWebSocketBridge.RunAsync(
    PtyStartInfo startInfo, WebSocket webSocket, PtyBridgeOptions? options = null, CancellationToken ct = default)
```

Takes the abstract BCL `System.Net.WebSockets.WebSocket`, so it works with Kestrel accepts, `HttpListener`, `ClientWebSocket`, and `WebSocket.CreateFromStream` (tests) while the library stays dependency-free.

### Protocol

Binary frames carry data; text frames carry JSON control messages. Raw PTY bytes never pass through JSON or base64.

| Direction | Frame | Payload | Meaning |
|---|---|---|---|
| server → client | Binary | raw PTY output | feed to `term.write(new Uint8Array(data), ackCallback)` |
| client → server | Binary | raw input bytes | written to the PTY verbatim (keystrokes, paste) |
| client → server | Text | `{"type":"resize","cols":120,"rows":30}` | resize the PTY |
| client → server | Text | `{"type":"ack","bytes":131072}` | flow-control credit: client finished processing N bytes |
| server → client | Text | `{"type":"exit","exitCode":0,"signal":15}` | child exited; always after the final output frame; `signal` omitted when null |

Unknown `type` values are ignored (forward compatibility). Malformed JSON or a control message larger than `MaxControlMessageSize` closes the socket with `PolicyViolation`, kills the child, and faults `RunAsync` with `InvalidDataException` — a broken client must not hold a shell open.

### Flow control

Server-side watermark over unacknowledged bytes; ACK credit was chosen over pause/resume messages because byte counting is self-clocking — a lost resume message cannot deadlock the stream.

| Option | Default | Role |
|---|---|---|
| `HighWatermark` | 384 KiB | pause output delivery at/above this unACKed count (keep < 500 KB per xterm.js buffer guidance) |
| `LowWatermark` | 128 KiB (2^17) | resume at/below; matches the recommended client ACK chunk |
| `ReceiveBufferSize` | 16 KiB | client input/control receive buffer |
| `MaxControlMessageSize` | 4 KiB | control JSON bound |
| `SendExitMessage` | `true` | emit the `exit` control message |
| `CloseTimeout` | 5 s | bound on close handshakes against dead clients |

The client counts binary payload bytes it has fed through `term.write` callbacks and ACKs them; both sides measure the same metric, which the xterm.js guide calls out as mandatory for the stream not to stall.

### Close and failure behavior

| Condition | Behavior |
|---|---|
| Child exits | drain → final output frame → `exit` text message → `NormalClosure` (bounded by `CloseTimeout`) → dispose → return status |
| Client closes the socket | kill child → drain (output discarded) → close response → return killed status |
| Protocol violation | `PolicyViolation` close → kill → `InvalidDataException` |
| Socket send failure | kill via disposal → the socket exception propagates from `RunAsync` |
| Cancellation | kill → dispose → `OperationCanceledException` |
| Invalid options / null args | `ArgumentOutOfRangeException` / `ArgumentNullException` before spawn |

v1 has exactly one close behavior (kill on disconnect). A keep-alive/reconnect option was rejected: with bridge-owned session creation there is no handle to reattach, so the option would leak sessions (see N5).

## VS Code integration pattern

VS Code cannot swap its internal node-pty; integration is a **`vscode.Pseudoterminal` extension** bridging to a MiniPty-based helper process (the extension host cannot P/Invoke). The helper speaks this bridge protocol (WebSocket or an equivalent framed stdio transport); the extension maps:

| Pseudoterminal side | Bridge side |
|---|---|
| `handleInput(data: string)` | UTF-8 encode → binary frame |
| binary frame | decode with an **incremental `TextDecoder`** → `onDidWrite.fire(string)` — the API is UTF-16 strings and a chunk can split a multi-byte UTF-8 sequence, so per-chunk decoding corrupts output |
| `setDimensions({columns, rows})` | `resize` control message (dimensions can be `undefined` until the panel is visible — skip until first real value) |
| `exit` control message | flush pending `onDidWrite`, then `onDidClose.fire(exitCode)` — VS Code buffers pty output on a short timer, so closing immediately after the last write can drop tail output |

`onDidWrite` has no flow-control hook, so the ACK side of this protocol should live in the extension's transport client (count bytes handed to `onDidWrite` and ACK) to keep server-side watermarks meaningful.

## Embedder Pattern (Use Case 4)

Browser (xterm.js) host flow — see [samples/WebTerminal.cs](../../../samples/WebTerminal.cs) for the complete version:

1. Accept a WebSocket (`HttpListenerContext.AcceptWebSocketAsync`, Kestrel `UseWebSockets`, …)
2. `var status = await PtyWebSocketBridge.RunAsync(shellStartInfo, webSocket, ct);`
3. Client side: `term.onData(d => ws.send(encode(d)))`, binary `onmessage` → `term.write(bytes, ackCallback)`, ACK every 2^17 processed bytes, resize on `term.onResize`.

Embedders that need a custom transport (stdio framing, SignalR, …) use `PtyTerminal` directly and reimplement only the framing; pause/resume and drain-then-exit ordering come from the facade.

## Failure Behavior (`PtyTerminal`)

| Condition | Behavior |
|---|---|
| Handler throws | pump stops, child killed, `Completion` faults with the handler exception |
| `Kill()` during delivery | remaining output drained and delivered, then `Completion` resolves with the status |
| `Kill()` / child exit while `Pause()`d | `Completion` stays pending until `Resume()` or disposal — flow control holds the drain by design |
| Dispose before exit | `Completion` faults with `OperationCanceledException`; child killed |
| Member call after dispose | `ObjectDisposedException` |

## Lessons Learned

- Disposing a `PtySession` immediately after canceling an active `ReadOutputAsync` races the session's internal buffer teardown (`ManualResetValueTaskSourceCore` double-signal). The facade cancels, awaits its pump to unwind, and only then disposes the session.
- A client that stops reading wedges `SendAsync` via transport backpressure, and a wedged send parks the pump beyond the reach of `Kill()`. Bridge sends use a bridge-lifetime teardown token, but some `ManagedWebSocket` stream waits do not observe cancellation promptly; caller cancellation must also abort the WebSocket transport. Client-initiated close remains graceful and must not be aborted eagerly. Verified red/green with a bounded in-memory pipe simulating a full TCP send buffer.
- `Task.WaitAsync(CancellationToken)` cannot assert "does not hang" in tests: its timeout also surfaces as `OperationCanceledException`, masking a hang. Use `WaitAsync(TimeSpan)` so a hang fails as `TimeoutException`.
- Close frames are send-type operations under the WebSocket one-outstanding-send rule; a `PolicyViolation` close issued from the receive loop must take the same send lock as the output pump (bounded by the close timeout).
- A client-initiated close does not discard server frames already in transit. Clients and tests must drain any Binary or Text frames ordered before the server's close response; the first receive after `CloseOutputAsync` is not guaranteed to be Close, especially when PTY startup output races disconnect.
- Bridge teardown must disable flow control and release any pause **inside the same lock** that sets pauses (`BridgeFlowControl.Disable`), or an in-flight send can re-pause after the teardown resume and park the drain forever.
- A WebSocket allows one outstanding send; the bridge serializes the output pump and the exit message with a semaphore. Test clients need the same discipline (ACK sends vs test-driven input sends).
- ConPTY line submission needs CR — sending `\n` echoes but never completes a `cmd.exe` `set /p` read. Cross-platform clients should send `\r` (xterm.js `onData` already does) and tests must not assert on LF-terminated input.
- `cmd.exe /c "set /p LINE= & echo %LINE%"` prints the literal `%LINE%` because expansion happens at parse time; interactive-input children in tests need `/v:on` with `!LINE!`.
- TerminateProcess-based `Kill` is fire-and-forget: `HasExited` can lag `Completion` by a scheduler tick; tests must poll, not assert immediately.
- `HttpListener` on `http://localhost:<port>/` works for the sample cross-platform without URL ACLs; ephemeral binding retries random ports because `HttpListener` cannot bind port 0.

## Related Documents

- [spec.md](../spec.md) — four use cases and package map
- [core_session.md](core_session.md) — `ReadOutputAsync` strict handoff, chunk lifetime, backpressure
- [lifecycle.md](lifecycle.md) — exit, kill, disposal semantics the facade builds on
- [plans/plan_editor_backend.md](../plans/plan_editor_backend.md) — implementation plan and decision record
- [samples/WebTerminal.cs](../../../samples/WebTerminal.cs) — browser demo with the client-side protocol
