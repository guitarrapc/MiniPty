# Terminal Backend Specification

Implemented user-facing contract for the **MiniPty.Terminal** NuGet package.

This package serves **use case 4** in [spec.md](../spec.md): MiniPty as the backend PTY (node-pty–equivalent role) behind a frontend terminal such as xterm.js in a browser or an editor-integrated terminal.

**Status: implemented.**

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
| N4 | Remote shell protocols (`ssh`), user identity/authentication, or multi-user session management. Per-session bridge bearer authentication is included. |
| N6 | HTTP serving and request authentication — the embedder authenticates the request, checks its `Origin` and `Host`, accepts the WebSocket (Kestrel, `HttpListener`, …), and hands it to the bridge ([Embedder Pattern](#embedder-pattern-use-case-4)) |
| N7 | A buffer clear on the in-box Windows ConPTY, which exposes no clear operation: there `PtyTerminal.Clear()` is a safe no-op. On the out-of-band ConPTY an application supplies ([Out-of-band ConPTY](core_session.md#out-of-band-conpty)) it clears the host's buffer. |
| N9 | node-pty `open()` (a PTY pair without a spawned child). Neither VS Code nor any known embedder calls it. |
| N10 | `uid` / `gid` on spawn. Switching identity is security-sensitive and outside a minimal PTY library. |
| N11 | An `encoding` spawn option that decodes output to strings. The library is bytes-first; frontends decode at their boundary with an incremental decoder. |
| N12 | node-pty `handleFlowControl` (XON/XOFF interception). VS Code throttles with pause/resume plus ACK credit, which `PtyTerminal` and both bridges provide. |

## `PtyTerminal` — push facade

```csharp
PtyTerminal.Start(PtyStartInfo startInfo, PtyTerminalOptions options)  // options.Output is required
PtyTerminal.Attach(PtySession session, PtyTerminalOptions options)    // transfers ownership
```

| Member | Contract |
|---|---|
| `Output` handler (options) | `ValueTask handler(ReadOnlyMemory<byte> data, CancellationToken ct)`. Data is valid **only until the returned task completes**; copy to retain. Invoked sequentially. |
| `Completion` | `Task<PtyExitStatus>` that completes **after every output handler invocation has finished** (drain-then-exit, node-pty onData/onExit ordering). Faults with the handler exception when a handler throws (child is killed first). |
| `Pause()` / `Resume()` | Stops/resumes output delivery. Paused delivery parks the pump; the session's output buffer fills, its reader stops reading, the OS PTY buffer fills, and the child blocks on write. Managed buffering is bounded by the session's output buffer (16 KiB, including the in-flight chunk, which stays valid while paused); no drops. |
| `WriteInputAsync` / `SendEof` / `Resize` / `Kill(PtySignal)` | Pass-through to the owned session; callable concurrently with output delivery. Resize accepts optional pixel dimensions. |
| `Kill()` | SIGHUP on Unix (node-pty default); process termination on Windows. Core `PtySession.Kill()` remains forceful. |
| `Clear()` | Forwards to `PtySession.Clear()`: clears the console host's buffer on an out-of-band ConPTY (`PtyStartInfo.ConPty`), so the host does not reprint what the frontend has discarded; a safe no-op on the in-box ConPTY and on Unix. Equivalent to node-pty's `clear()`. |
| `ProcessId` / `Size` / `ActiveProcessName` / `HasExited` / `ExitStatus` | Session state. `ActiveProcessName` is polled by the embedder (typically 200 ms on Unix); no library polling task is created. It is null on Windows, where VS Code derives tab titles from its own process-tree lookup rather than from node-pty. |
| `DisposeAsync` | Stops the pump, ends the child if running (Unix: hang-up, then kill after a grace period; Windows: termination), releases the PTY. |

Design decisions (WHY):

- **The facade owns its session.** `Start` creates it; `Attach` explicitly transfers an existing session. Core still allows exactly one output consumer, so callers must attach before starting another reader and stop using the transferred session directly.
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

Exit JSON always uses `PtyExitStatus.NodePtyExitCode`: signal death reports exit code 0 plus the raw signal, while the status returned from `RunAsync` retains MiniPty's shell-oriented `128 + signal` value.

Unknown `type` values are ignored (forward compatibility). Malformed JSON or a control message larger than `MaxControlMessageSize` closes the socket with `PolicyViolation`, kills the child, and faults `RunAsync` with `InvalidDataException` — a broken client must not hold a shell open.

### Flow control

Server-side watermark over unacknowledged bytes; ACK credit was chosen over pause/resume messages because byte counting is self-clocking — a lost resume message cannot deadlock the stream.

| Option | Default | Role |
|---|---|---|
| `HighWatermark` | 384 KiB | pause output delivery at/above this unACKed count (keep < 500 KB per xterm.js buffer guidance) |
| `LowWatermark` | 128 KiB (2^17) | resume at/below; `0` requires all outstanding bytes to be ACKed; default matches the recommended client ACK chunk |
| `ReceiveBufferSize` | 16 KiB | client input/control receive buffer |
| `MaxControlMessageSize` | 4 KiB | control JSON bound |
| `SendExitMessage` | `true` | emit the `exit` control message |
| `CloseTimeout` | 5 s | bound on close handshakes against dead clients |

The client counts binary payload bytes it has fed through `term.write` callbacks and ACKs them; both sides measure the same metric, which the xterm.js guide calls out as mandatory for the stream not to stall.

### Output frames

`PtyWebSocketBridge` and `PtyStdioBridge` send one message per chunk they take from the session. The persistent-sessions bridge copies each chunk into its replay buffer, and each of its output frames carries what the buffer holds since the last send, up to `MaxOutputFrameSize`. All three bridges take chunks the same way, joining output that arrives in a burst into larger chunks:

- **Outside a burst**, such as a keystroke echo or the first output after a pause, a chunk is taken as soon as it is read.
- **A burst** starts once 4 KiB, or four chunks, have been taken within 1 ms. A keystroke echo never comes near either. A single chunk of 4 KiB counts, so the tail of a large repaint or paste waits as well. Four chunks count however small they are, so a stream of short lines that the terminal hands over one at a time is joined, and so is the output right after a run of small writes read one by one, such as a progress line redrawn in steps. Writes that the terminal hands over at once, as a prompt that writes itself in pieces usually is, are one chunk. A stream slower than four reads a millisecond goes out one message per read: 100-byte lines at 0.2 MB/s, for example.
- **In a burst** the bridge takes its next chunk once 8 KiB is unread, or once the wait's deadline, 1 ms after it asked, has been served. End of output and disposal end the wait at once. A process-wide tick serves deadlines about every millisecond, so a wait lasts up to about 2 ms. .NET timers do not keep that millisecond (every 15.6 ms on Windows, every 4 ms under a 250 Hz Linux kernel), so Windows ticks on a high-resolution waitable timer with a thread of its own while bridges wait, and Unix on the I/O loop's wait.
- **A burst ends when output stops.** A wait that output completes before its deadline keeps the burst, and so does one whose deadline still finds output unread: output kept coming, however slowly, so a stream of short lines stays in its burst, each chunk taken at a deadline. A wait that output completes only after its deadline found nothing ends the burst, and that output, such as an echo typed after the stream, goes out as it arrives. It keeps the burst only while what is unread then amounts to 2 KiB or more per millisecond since the bridge asked, half the rate that starts one, which covers a flood whose deadline a late tick missed. A bridge 8 KiB behind does not wait at all. What the wait that ended the burst left unread does not count toward the next burst, because it piled up over the whole wait.
- **The session's publish policy is unchanged.** The session still publishes each read as it completes, and only the bridge waits. The wait minimum and the tick live in the core library, which `PtyTerminal` reaches through an internal option that only the bridges set. A `PtyTerminal` that a host starts itself pushes each chunk as it is read.

**Why:** a macOS terminal holds 1 KiB of output, so each read returns at most that much. A bridge that keeps up sent each read as a frame of its own: a 32 MiB flood took 30–33 frames per 32 KiB (8 with the former reader threads' 1 ms window), ran about 12% slower, and woke the bridge once per KiB. Before the child writes its next KiB, nothing can tell a flood that goes on from one that has ended, so joining reads means waiting, which trades latency for frames. That trade belongs to the party that pays per frame, the bridge, rather than to every consumer of the session. Joined, the same flood takes 4 frames per 32 KiB, runs faster than with the reader threads' window (74–76 against 63–66 MB/s), and costs the host less CPU than sending each read (38–41 against 57–68 ms per MiB), while echo is unchanged. It is not a macOS special case: a flood took 5.5–5.9 frames per 32 KiB on Windows and 14–18 on Linux, and joined takes 3.9–4.0, at the same throughput or more and no more host CPU. The price is the wait: the tail of a 6 KiB repaint, which went out with its head, now waits about 1–1.4 ms on Windows, and up to about 1 ms on Linux.

**Why streams of short lines are joined too:** sent one message per read, 100-byte lines at 0.4–2 MB/s took 293–334 messages per 32 KiB on Windows and Linux, and `dir /s` on Windows 637–640, where the reader threads, whose windows joined every read, took 8–34 and 25. Each message costs both ends of the socket, and the browser one event. Joined by their count, the same streams take 8.7–48.6 messages per 32 KiB and `dir /s` 30–35, and the host spends a third to a sixth of the CPU on them. Before the next write comes, nothing tells such a stream from interactive output that has ended, so the joining puts a wait on output right after a run of small writes read one by one: on Linux an echo 0.5 ms after six writes of 40 bytes, 150 µs apart, went out at p50 1.5 ms instead of 0.4 ms, and at most 2.2 ms. An echo after idle, after a large tail, or after small writes that the terminal hands over at once is unchanged, and on Windows ConPTY delays an echo right after output by about 8 ms either way. The wait is bounded by the deadline, about 2 ms, so the rule is the same for every bridge, with no host option. Streams slower than four reads a millisecond (100-byte lines below about 0.4 MB/s) still go out one message per read.

**Cost:** a session that is not read through a bridge keeps its size, since its pump and reader measure as before. A bridge's session allocates its coalescing reader once. In a burst the pump wakes the bridge at 8 KiB unread, at the deadline, or at end of output, not once per read. While any bridge waits, the tick wakes once per millisecond: on Windows its thread, which stays 30 s after the last burst, and on Unix the loop that already reads every session.

### Close and failure behavior

| Condition | Behavior |
|---|---|
| Child exits | drain → final output frame → `exit` text message → `NormalClosure` (bounded by `CloseTimeout`) → dispose → return status |
| Client closes the socket | kill child → drain (output discarded) → close response → return killed status |
| Child outlasts the kill after a client close (Unix: it ignores the hang-up) | wait up to twice `CloseTimeout` → dispose, which still ends the child → `TimeoutException` |
| Protocol violation | `PolicyViolation` close → kill → `InvalidDataException` |
| Socket send failure | the pump's handler fault kills the child (`Kill()`) → the socket exception propagates from `RunAsync` |
| Cancellation | kill → dispose → `OperationCanceledException` |
| Invalid options / null args | `ArgumentOutOfRangeException` / `ArgumentNullException` before spawn |

Hosts stop `RunAsync` by canceling its token. The bridge notices a socket that was closed or aborted, by the client or by the host, only when it next reads from the socket or sends to it. It reads no further frames while it waits to write client input to a child that is not reading its input, so a host passes a token that is also canceled when the connection ends, such as ASP.NET Core's `HttpContext.RequestAborted`.

`PtyWebSocketBridge.RunAsync` retains its one-shot behavior: disconnect hangs up and disposes its child. Persistent/reconnect behavior is opt-in through `PtyWebSocketSessionManager`, so existing bridge callers do not accidentally leave detached shells running.

## Persistent WebSocket sessions

`PtyWebSocketSessionManager` owns a bounded set of authenticated PTY sessions independently of individual WebSocket lifetimes:

```csharp
await using var manager = new PtyWebSocketSessionManager(options);
var credentials = manager.CreateSession(startInfo);
PtyExitStatus? status = await manager.ConnectAsync(
    credentials.SessionId,
    credentials.AuthenticationToken,
    acknowledgedOffset,
    webSocket,
    cancellationToken);
```

`CreateSession` returns an opaque `Guid` and a random 256-bit bearer token. The token is returned once, compared in fixed time, zeroed when the session is destroyed, and must not be logged or placed in a URL. A host should obtain it through an authenticated control channel and pass it directly to `ConnectAsync`.

Only one WebSocket may attach to a session. A client disconnect returns null from `ConnectAsync` without killing the PTY. The manager kills and removes a detached session after `DetachedSessionTimeout`; connected sessions do not expire. `MaxSessions` bounds process and memory ownership, and `TerminateAsync` provides authenticated explicit teardown. Disposing the manager terminates every remaining session.

### Replay protocol

Persistent output uses absolute byte offsets because a disconnect can race the final socket write. Before every raw binary output message, the server sends:

```json
{"type":"output","offset":0,"bytes":4096}
```

After the frontend has processed those bytes, it sends the absolute next offset:

```json
{"type":"ack","offset":4096}
```

The frontend persists its latest acknowledged offset and supplies it to the next `ConnectAsync`. The manager replays retained bytes from that point, avoiding both missing output and duplicate rendering. An offset outside the retained range is rejected rather than silently corrupting the terminal stream. Input and resize messages retain the normal WebSocket bridge shapes.

Each session has one fixed `ReplayBufferSize` allocation. Output continues while detached until the buffer fills; it then backpressures the PTY instead of dropping bytes or growing memory without bound. A full buffer takes more output only once acknowledgements free room for one `MaxOutputFrameSize`, or for all of a smaller chunk. `MaxOutputFrameSize` controls message size. Persistent sessions use this replay capacity rather than `PtyBridgeOptions` high/low byte watermarks.

## `PtyStdioBridge` — helper-process bridge

`PtyStdioBridge.RunAsync` provides the same raw data, resize, ACK, and exit semantics over readable/writable streams. Each frame is a one-byte type (`1` output, `2` input, `3` control), a little-endian unsigned 32-bit payload length, then the payload. This fixed header lets a VS Code extension host a NativeAOT helper without HTTP, WebSocket, JSON-wrapped data, or base64.

Control payloads are the same UTF-8 JSON used by the WebSocket bridge. Output is fully delivered before the exit control frame, and ACK watermarks apply identically. See [VS Code Pseudoterminal reference](../references/vscode_pseudoterminal.md), [VsCodeTerminalHelper.cs](../../../samples/SingleFiles/VsCodeTerminalHelper.cs), the runnable [VsCodeExtension sample](../../../samples/VsCodeExtension), and its long-lived [persistent bridge service](../../../samples/SingleFiles/VsCodePersistentBridge.cs).

## VS Code integration pattern

VS Code cannot swap its internal node-pty; integration is a **`vscode.Pseudoterminal` extension** bridging to a MiniPty-based helper process (the extension host cannot P/Invoke). The helper speaks this bridge protocol (WebSocket or an equivalent framed stdio transport); the extension maps:

| Pseudoterminal side | Bridge side |
|---|---|
| `handleInput(data: string)` | UTF-8 encode → binary frame |
| binary frame | decode with an **incremental `TextDecoder`** → `onDidWrite.fire(string)` — the API is UTF-16 strings and a chunk can split a multi-byte UTF-8 sequence, so per-chunk decoding corrupts output |
| `setDimensions({columns, rows})` | `resize` control message (dimensions can be `undefined` until the panel is visible — skip until first real value) |
| `exit` control message | flush pending `onDidWrite`, then `onDidClose.fire(exitCode)` — VS Code buffers pty output on a short timer, so closing immediately after the last write can drop tail output |
| text written before the shell starts (VS Code's `initialText`) | start with `PtyStartInfo.InheritCursor = true`, as VS Code does in that case, or Windows ConPTY clears it; xterm.js answers the cursor position query as input, so the answer reaches the helper like keystrokes ([Inherited cursor](core_session.md#inherited-cursor)) |

`onDidWrite` has no flow-control hook, so the ACK side of this protocol should live in the extension's transport client (count bytes handed to `onDidWrite` and ACK) to keep server-side watermarks meaningful.

## Embedder Pattern (Use Case 4)

Browser (xterm.js) host flow — see [samples/SingleFiles/WebTerminal.cs](../../../samples/SingleFiles/WebTerminal.cs) for the complete version:

1. Authenticate the request and check its `Origin` and `Host` before accepting it (below).
2. Accept a WebSocket (`HttpListenerContext.AcceptWebSocketAsync`, Kestrel `UseWebSockets`, …)
3. `var status = await PtyWebSocketBridge.RunAsync(shellStartInfo, webSocket, ct);`
4. Client side: `term.onData(d => ws.send(encode(d)))`, binary `onmessage` → `term.write(bytes, ackCallback)`, ACK every 2^17 processed bytes, resize on `term.onResize`.
5. Clipboard keys: xterm.js sends Ctrl+V and Ctrl+C to the program as `^V` and `^C` and cancels the browser's copy and paste for them. A `term.attachCustomKeyEventHandler` that returns `false` for Ctrl+V, and for Ctrl+C while `term.hasSelection()`, leaves those keys to the browser. The browser's paste then reaches the program through `onData`, wrapped for bracketed paste when the program asked for it.

The bridge authenticates nothing: whoever holds the socket types into the child. Listening only on loopback does not protect a browser-facing endpoint, so the host must make three checks:

| Check | What it refuses |
|---|---|
| `Origin`, when present, is the server's own origin | Any other site's page. Browsers let a page open a WebSocket to any host, `localhost` included, and leave this check to the server. A client that is not a browser sends no `Origin`. |
| `Host` is the server's own loopback name and port | A page that points its own domain at 127.0.0.1 (DNS rebinding). It is same-origin with itself, so it passes the `Origin` check. |
| The request carries a per-run secret | Other local users and non-browser clients, which can reach a loopback port and forge any header. |

The browser samples print an address whose fragment carries a 256-bit per-run token. Browsers never send a URL fragment to the server. The page sends the token as the WebSocket subprotocol `minipty-token.<hex>`, not in the URL, so request logs do not record it. A browser fails a handshake whose answer names none of the subprotocols it offered, so the server answers with `minipty`. `PtyWebSocketSessionManager` hosts carry their per-session token the same way ([VsCodePersistentBridge.cs](../../../samples/SingleFiles/VsCodePersistentBridge.cs)).

Embedders use `PtyStdioBridge` for helper-process framing or `PtyTerminal` directly for other transports such as SignalR.

## Failure Behavior (`PtyTerminal`)

| Condition | Behavior |
|---|---|
| Handler throws | pump stops, child killed, `Completion` faults with the handler exception |
| `Kill()` during delivery | remaining output drained and delivered, then `Completion` resolves with the status |
| `Kill()` / child exit while `Pause()`d | `Completion` stays pending until `Resume()` or disposal — flow control holds the drain by design |
| Dispose before exit | `Completion` faults with `OperationCanceledException`; the child is ended as by `PtySession.Dispose` (Unix: hang-up, then kill after a grace period; Windows: termination) |
| `Resize` after exit (frontend size change before teardown) | no-op once the transport is closed; never throws because the child exited |
| Member call after dispose | `ObjectDisposedException` |

## Lessons Learned

- Disposing a `PtySession` immediately after canceling an active `ReadOutputAsync` races the session's internal buffer teardown (`ManualResetValueTaskSourceCore` double-signal). The facade cancels, awaits its pump to unwind, and only then disposes the session.
- So the pump's wait for the exit after output ends must observe the pump token. On Linux output also ends when the child closes the terminal and keeps running. A wait that ignored cancellation held `DisposeAsync`, and with it every bridge, until the child exited on its own. The session was disposed only after that, so a child that ignores `SIGHUP` was never hung up or killed. Verified red/green on Linux with a child that runs `trap '' HUP; exec >/dev/null 2>&1 </dev/null; exec sleep 60`. macOS was never affected: it does not end output while the child runs, because the child's session keeps the terminal open as its controlling terminal, so the pump stays in a read that observes the token. The same tests pass there without the fix.
- A client that stops reading wedges `SendAsync` via transport backpressure, and a wedged send parks the pump beyond the reach of `Kill()`. Bridge sends use a bridge-lifetime teardown token, but some `ManagedWebSocket` stream waits do not observe cancellation promptly; caller cancellation must also abort the WebSocket transport. Client-initiated close remains graceful and must not be aborted eagerly. Verified red/green with a bounded in-memory pipe simulating a full TCP send buffer.
- `Task.WaitAsync(CancellationToken)` cannot assert "does not hang" in tests: its timeout also surfaces as `OperationCanceledException`, masking a hang. Use `WaitAsync(TimeSpan)` so a hang fails as `TimeoutException`.
- Close frames are send-type operations under the WebSocket one-outstanding-send rule; a `PolicyViolation` close issued from the receive loop must take the same send lock as the output pump (bounded by the close timeout).
- A client-initiated close does not discard server frames already in transit. Clients and tests must drain any Binary or Text frames ordered before the server's close response; the first receive after `CloseOutputAsync` is not guaranteed to be Close, especially when PTY startup output races disconnect.
- Bridge teardown must disable flow control and release any pause **inside the same lock** that sets pauses (`BridgeFlowControl.Disable`), or an in-flight send can re-pause after the teardown resume and park the drain forever.
- A WebSocket allows one outstanding send; the bridge serializes the output pump and the exit message with a semaphore. Test clients need the same discipline (ACK sends vs test-driven input sends).
- Protocol tests that make the child exit immediately after input must first observe an explicit child-ready Binary frame. Otherwise PTY attachment, output-pump startup, control handling, input, and fast exit are all raced together, so missing output does not isolate the protocol behavior under test. An observed WebSocket frame is the synchronization point; a fixed delay is not.
- Unacknowledged-byte accounting saturates at `long.MaxValue`. Although normal watermarks pause output long before overflow, `HighWatermark = long.MaxValue` is valid and a wrapped negative count would bypass flow control in an extremely long-lived session.
- ConPTY line submission needs CR — sending `\n` echoes but never completes a `cmd.exe` `set /p` read. Cross-platform clients should send `\r` (xterm.js `onData` already does) and tests must not assert on LF-terminated input.
- `cmd.exe /c "set /p LINE= & echo %LINE%"` prints the literal `%LINE%` because expansion happens at parse time; interactive-input children in tests need `/v:on` with `!LINE!`.
- A stdio helper must reserve stdout exclusively for framed protocol bytes. Diagnostics belong on stderr; one accidental text write makes the length stream unrecoverable.
- `PtyTerminal.Kill()` can use SIGHUP without weakening deterministic cleanup: handler faults use core's forceful `Kill()`, and final disposal goes through `PtySession.Dispose`, which on Unix kills a child that ignores the hang-up after a one-second grace, or at once when the process exits normally. A host ended within the grace by a crash or by a signal it does not cancel never sends that kill. The remedies in the [session contract](core_session.md#session-contract) apply. `Kill(PtySignal.Kill)` before `DisposeAsync` ends such a child at once but skips the hang-up. An interactive shell killed this way cannot resend the hang-up to its jobs, so a running background job keeps running. A host can instead cancel such a signal, hang up each terminal with `Kill()`, await `Completion` with a timeout, dispose the terminal even when the wait times out, and exit normally. The wait gives a shell time to resend the hang-up to its jobs. A host that runs `PtyWebSocketBridge.RunAsync` has no terminal to call these on. It cancels the token it passed, awaits `RunAsync` and catches what it throws, then exits normally, so the kills are sent at exit. An exception that escapes `Main` sends none. On cancellation `RunAsync` hangs up and disposes without waiting for the child to exit.
- Reliable reconnect requires absolute acknowledgement state. Byte-count credit alone cannot distinguish “rendered but ACK lost” from “never received,” so the persistent protocol pairs each binary message with its stream offset.
- Expiration teardown must force child exit, release any producer blocked on replay capacity, and await the Terminal pump before disposing core session buffers. Canceling and disposing an active output reader concurrently reproduces the core double-signal race.
- Detach must also bound both send and receive shutdown. Cancellation alone does not reliably unblock every `ManagedWebSocket` transport wait, so persistent connections use `CloseTimeout` and abort a socket that remains wedged before making the session attachable again.
- After sending final output, the exit control, and a WebSocket close frame, do not cancel an already-running `ReceiveAsync` to stop the receive loop. `ManagedWebSocket` can turn that cancellation into an abort and discard the frames already queued to the peer, especially under NativeAOT timing. Wait for the peer's close reply within `CloseTimeout`; abort only after that bounded wait expires.
- TerminateProcess-based `Kill` is fire-and-forget: `HasExited` can lag `Completion` by a scheduler tick; tests must poll, not assert immediately.
- `HttpListener` on `http://localhost:<port>/` works for the sample cross-platform without URL ACLs; ephemeral binding retries random ports because `HttpListener` cannot bind port 0.
- The browser samples first served a shell to any WebSocket on `localhost` with no checks. A security review found that any site the user visited while they ran could open the socket and run commands: binding to loopback stops other machines, not pages in the user's own browser. ASP.NET Core's `WebSocketOptions.AllowedOrigins` was not enough on its own: it lets a request without `Origin` through and cannot express "the same host and port as this request". Both samples therefore make their own three checks, and their smoke modes assert that a request without the token, with a wrong token, or from another origin is refused.
- Flow control pauses before the next chunk, not within one, so unacknowledged bytes can pass `HighWatermark` by up to a chunk, and a chunk can hold the session's whole output buffer (16 KiB). A test that needs a pause before output ends produces more than the watermark plus one chunk: 64 lines (8 KiB) against a 4 KiB watermark arrived in one chunk on Linux, and the bridge closed before the test acknowledged anything.
- A WebSocket harness has to watch the bridge while it waits for frames. A benchmark client that only received hung for hours when `RunAsync` failed at spawn (`CreateProcess` error 206, a program path over `MAX_PATH` in a deep build directory): nothing ever closed its socket. The harness must also close the server's connection once `RunAsync` returns, as a web server does when its handler returns. A .NET client otherwise waits up to a second after the close handshake for the server to close it, and that second was most of every measured session (1.08 s against 90 ms).
- What a session costs the host is measured from outside the host, with thread-pool workers counted apart from other threads. A burst of WebSocket connections injected 14–37 pool workers that retire later. The other threads grew by exactly one per open session on Windows and Linux while each session had a reader thread, and by none once the engines read without one ([Why this engine](../references/pty_crossplatform.md#why-this-engine)). [WebTerminalScale.cs](../../../samples/SingleFiles/WebTerminalScale.cs) runs that census.
- Judge the bridges' burst rules on the traffic they serve, not on one flood. A single flood fills the 8 KiB minimum before nearly every deadline, so it almost never reaches the rules for a wait that outlasts its deadline: 1–8 late waits of about 4,096 in one review's count. Two exit rules that were right by the specification's words then took about twice the frames for 32 concurrent floods, and three times for `ls -lR`, because a late wait ended the burst whenever output slowed at all, as it does on a busy host. Keeping a burst at half the rate that starts one brought both back. A change to these rules is measured on concurrent floods, a bursty real command, steady streams between 1 and 8 MB/s, and streams of short lines, with [BridgeTraffic.cs](../../../samples/SingleFiles/BridgeTraffic.cs).
- A start rule alone does not join a slow stream. Four chunks within 1 ms start a burst for 100-byte lines, but at 1 MB/s a deadline of 1–2 ms finds only 1–2 KiB, below the rate that keeps a burst, so the stream would leave its burst at every deadline and start it again four chunks later. The burst therefore lasts while each deadline still finds output.
- Frame-size tests run alone. In one of four full parallel runs, a 1 MiB flood through the bridge took 263 frames against about 130 alone: a loaded machine slowed the flood below a burst's rate now and then, and each slowdown hands out what has arrived, as it should. A test of a joined chunk also needs the platform backends' chunk limit (the whole ring); the scripted backend of the pump tests caps a chunk at one 4 KiB read.
- A replay buffer that resumes with whatever one acknowledgement frees sends a slow client ever smaller frames. Once a flood fills the buffer with sent but unacknowledged output, the send loop has nothing else to send, so each freed piece went out alone, as large as the frame acknowledged before it and split again at each ring wrap. A 1 MiB flood then took about 330 frames with 66–78% of its bytes in frames of 4 KiB or more, against about 120 frames and 95–99% once the buffer waited for room for one frame. It first showed on a hosted Linux arm64 runner, whose test client fell behind the flood; a client that waits a few milliseconds per acknowledgement reproduces it on any Linux host.
- A frame-size test needs a flood, and a slow host does not give one. On macOS a read returns at most 1 KiB, so a flood goes only as fast as the terminal's writes and reads take turns. On a hosted macOS Intel runner whose suite took 2 min 57 s, against 54 s for the commit before, 1 MiB took about 2 s, and the three flood tests sent 36–70% of their bytes in frames of 4 KiB or more. A steady writer reproduces it on any host, on Linux too: 1 KiB writes 300 µs or more apart (2.7 MB/s or less) went out read by read, with 0–21% of their bytes in large frames, and writes 200 µs or less apart (3.1 MB/s or more) with 87–100%. That is the burst rule as specified, not a fault. A busy host is different: 8–32 busy loops, or the efficiency cores alone, slowed the flood to 0.08–14 MB/s, but it came in bursts, and 82–97% still went out large. So the frame-size tests check that the output came at least at the rate that starts a burst, 4 KiB per millisecond, and are skipped below it. A client that keeps up receives the output at the rate it came, so its frames show that rate. A slow client sets its own rate, so its test measures the host's flood through a plain session first: a child writing 1 KiB every 500 µs sent 55% of its bytes in small frames even to a client that waited 3 ms per acknowledgement, because the send loop sends each chunk as it comes whenever the buffer has room. With the joining turned off, every test still failed on macOS, and on Linux every test but the slow client's, whose reads are large there.

## Related Documents

- [spec.md](../spec.md) — four use cases and package map
- [core_session.md](core_session.md) — `ReadOutputAsync` bounded buffering, chunk lifetime, backpressure
- [lifecycle.md](lifecycle.md) — exit, kill, disposal semantics the facade builds on
- [samples/SingleFiles/WebTerminal.cs](../../../samples/SingleFiles/WebTerminal.cs) — browser demo with the client-side protocol and Origin, Host, and per-run token checks
- [samples/SingleFiles/VsCodeTerminalHelper.cs](../../../samples/SingleFiles/VsCodeTerminalHelper.cs) — stdio-framed helper entry point
- [samples/SingleFiles/VsCodePersistentBridge.cs](../../../samples/SingleFiles/VsCodePersistentBridge.cs) — authenticated loopback service and reconnect E2E smoke
- [samples/VsCodeExtension](../../../samples/VsCodeExtension) — runnable one-shot and persistent VS Code `Pseudoterminal` clients
