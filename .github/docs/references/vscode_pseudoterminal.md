# VS Code Pseudoterminal Reference

A VS Code extension cannot load MiniPty directly, so it spawns the NativeAOT-capable [VsCodeTerminalHelper.cs](../../../samples/VsCodeTerminalHelper.cs) and implements `vscode.Pseudoterminal` over its stdin/stdout.

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
