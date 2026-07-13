# VS Code Pseudoterminal Reference

A VS Code extension cannot load MiniPty directly, so it spawns the NativeAOT-capable [VsCodeTerminalHelper.cs](../../../samples/VsCodeTerminalHelper.cs) and implements `vscode.Pseudoterminal` over its stdin/stdout.

The runnable [VsCodeExtension sample](../../../samples/VsCodeExtension) implements this mapping without npm dependencies. It opens a real VS Code integrated terminal and is the manual integration check for input, output, UTF-8 streaming, resize, flow-control acknowledgement, and ordered exit.

## Frame mapping

Each frame is `[type: u8][length: u32 little-endian][payload]`.

| Direction | Type | Payload |
|---|---:|---|
| helper → extension | 1 | raw PTY output |
| extension → helper | 2 | UTF-8 encoded terminal input |
| either | 3 | UTF-8 control JSON (`resize`, `ack`, `exit`) |

The extension must parse stdout incrementally because a stream read can split either header or payload. It must also use one persistent `TextDecoder("utf-8")` with streaming decode for type-1 payloads; decoding each frame independently corrupts UTF-8 characters split across frames.

Map `handleInput` to type 2 and `setDimensions` to `{"type":"resize","cols":...,"rows":...}`. Count type-1 bytes handed to `onDidWrite` and periodically send `{"type":"ack","bytes":...}`. On the exit control, flush the decoder and pending writes before firing `onDidClose(exitCode)`. Signal exits already carry the node-pty-compatible `exitCode: 0` plus `signal`.

The helper's stdout is protocol-only. Route logs and diagnostics to stderr.

The stdio extension sample is deliberately one-shot. Closing it closes the helper input and therefore the owned PTY. It validates the editor backend path but not persistent reconnect.

## Persistent reconnect option

The stdio helper is intentionally process-owned: if the helper exits, its PTY exits too. A host that needs terminals to survive renderer or extension-client disconnects runs a long-lived .NET service with `PtyWebSocketSessionManager` instead.

Create the session through an authenticated service endpoint and return its session id plus bearer token to the extension without placing the token in logs or URLs. On each WebSocket attach, the service calls `ConnectAsync` with the last absolute output offset persisted by the extension. Persistent WebSocket output is preceded by an `output` control containing `offset` and `bytes`; after `onDidWrite` accepts that binary payload, persist and ACK the next absolute offset. `ConnectAsync` returns null on detach and an exit status after final output when the child exits.
