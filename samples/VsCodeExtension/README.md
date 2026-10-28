# VS Code terminal backend

Run a VS Code integrated terminal on MiniPty, in the role node-pty plays for VS Code's own terminals. An extension cannot load .NET code, so the extension implements `vscode.Pseudoterminal` and talks to a MiniPty process. MiniPty.Terminal provides the server side for two designs:

| Command | MiniPty process | MiniPty API | When the extension or window goes away |
|---|---|---|---|
| **MiniPty: Open One-Shot Terminal** | [VsCodeTerminalHelper.cs](../SingleFiles/VsCodeTerminalHelper.cs), a NativeAOT helper the extension starts and talks to over stdio | `PtyStdioBridge` | the shell ends with the helper |
| **MiniPty: Open Persistent Terminal** | [VsCodePersistentBridge.cs](../SingleFiles/VsCodePersistentBridge.cs), a loopback service that runs on its own, over WebSocket | `PtyWebSocketSessionManager` | the shell keeps running; the extension reconnects and receives what it missed |

[extension.js](extension.js) is the client for both, with no npm dependencies.

## One-shot terminal: a helper over stdio

The helper is one call. stdout carries only protocol frames, so diagnostics go to stderr:

```csharp
await using var stdin = Console.OpenStandardInput();
await using var stdout = Console.OpenStandardOutput();
var status = await PtyStdioBridge.RunAsync(startInfo, stdin, stdout, cancellationToken: ct);
return status.NodePtyExitCode;
```

Each frame is `[type: u8][length: u32 little-endian][payload]`: type 1 is PTY output, type 2 is input, and type 3 is a JSON control message. The extension maps them onto `Pseudoterminal`:

| `Pseudoterminal` | Frame |
|---|---|
| `open(dimensions)`, `setDimensions(dimensions)` | type 3 `{"type":"resize","cols":120,"rows":30}` |
| `handleInput(data)` | type 2, the text as UTF-8 |
| `onDidWrite.fire(text)` | from type 1, then type 3 `{"type":"ack","bytes":4096}` for the bytes written |
| `onDidClose.fire(exitCode)` | from type 3 `{"type":"exit","exitCode":0}`, which always follows the last output |
| `close()` | end the helper's stdin; the helper ends the shell and exits |

Points to get right in the extension:

- **Parse stdout incrementally.** A read can end in the middle of a header or a payload.
- **Decode output with one `TextDecoder` in streaming mode** (`decode(payload, { stream: true })`). A frame can end in the middle of a UTF-8 character; decoding each frame on its own corrupts it.
- **ACK what you hand to `onDidWrite`.** `onDidWrite` has no backpressure, so the ACKs are what lets the helper pause a flood instead of buffering it.
- **Close after the last write.** VS Code buffers terminal output briefly, so fire `onDidClose` on the next tick after the final `onDidWrite` (the sample uses `setImmediate`), or the tail of the output is lost.

## Persistent terminal: a service that outlives the extension

The service keeps a `PtyWebSocketSessionManager` and puts it behind three HTTP endpoints:

```csharp
await using var manager = new PtyWebSocketSessionManager();

// POST /sessions, authenticated with the service's own bearer token.
// Returns a session id and a per-session token. The token is returned only once.
var credentials = manager.CreateSession(startInfo);

// GET /sessions/{id}/connect?offset=N, a WebSocket offering the session token as subprotocol minipty-token.<hex>.
// Returns null when the client disconnects (the shell keeps running), or the exit status when the shell exits.
PtyExitStatus? status = await manager.ConnectAsync(sessionId, sessionToken, offset, webSocket, ct);

// DELETE /sessions/{id}, with the session token as the bearer token.
await manager.TerminateAsync(sessionId, sessionToken);
```

- **Reconnect from the acknowledged offset.** Before each binary output frame the manager sends `{"type":"output","offset":0,"bytes":4096}`. After `onDidWrite`, the client ACKs the next absolute offset (`{"type":"ack","offset":4096}`), keeps it, and passes it as `offset` when it reconnects. The manager replays from there, so nothing is lost or written twice.
- **Map `ConnectAsync`'s exceptions to close codes.** It throws `UnauthorizedAccessException` for an unknown or expired session or a wrong token, `ArgumentOutOfRangeException` for an offset the replay buffer no longer holds, and `InvalidOperationException` when another client is already connected (one client per session).
- **Detached sessions are bounded.** Output keeps flowing into a fixed-size replay buffer per session; when it is full, the shell is paused, not dropped. A session that stays detached longer than `DetachedSessionTimeout` is killed, and disposing the manager ends every session.
- **Keep tokens out of logs and URLs.** This is why the session token travels as a WebSocket subprotocol.

The sample listens only on `127.0.0.1` and has no TLS. It is a sample of the session model, not a remote shell service.

## Try it

### 1. Publish the helper with NativeAOT

From the repository root, on macOS or Linux (macOS needs the Xcode Command Line Tools for the native link):

```bash
rid=osx-arm64                         # osx-x64, linux-x64, or linux-arm64
bash scripts/build-native.sh "$rid"   # the native shim; runtimes/ is not checked in
dotnet publish samples/SingleFiles/VsCodeTerminalHelper.cs -c Release -r "$rid" --self-contained true \
  -p:PublishAot=true -p:StripSymbols=true -p:DebugType=None -o "artifacts/vscode-helper/$rid"
```

On Windows, in PowerShell:

```powershell
$rid = "win-x64"   # or win-arm64
dotnet publish samples\SingleFiles\VsCodeTerminalHelper.cs -c Release -r $rid --self-contained true `
  -p:PublishAot=true -p:StripSymbols=true -p:DebugType=None -o "artifacts\vscode-helper\$rid"
```

The output is one executable, plus `minipty_spawn_helper` on macOS, which must stay beside it when you ship the helper. The extension looks for the helper in `artifacts/vscode-helper/<rid>`; set `minipty.helperPath` or `MINIPTY_VSCODE_HELPER` for another location, and `minipty.helperArguments` to run a command other than the default shell.

With a Homebrew-installed .NET SDK on macOS, the NativeAOT link does not find Homebrew's libraries. Before `dotnet publish`, run:

```bash
export LIBRARY_PATH="$(brew --prefix openssl@3)/lib:$(brew --prefix brotli)/lib:$(brew --prefix zlib)/lib${LIBRARY_PATH:+:$LIBRARY_PATH}"
```

### 2. Open a one-shot terminal

```bash
code --new-window --extensionDevelopmentPath="$PWD/samples/VsCodeExtension" "$PWD"
```

In the Extension Development Host, run **MiniPty: Open One-Shot Terminal**. Print some UTF-8 (`printf 'MiniPty: 日本語 🚀\n'`, or `chcp 65001 >NUL` and then `echo MiniPty: 日本語` in cmd.exe), resize the panel (`stty size` shows the new size on Unix), and run `exit`: the last output stays visible until the terminal closes.

### 3. Open a persistent terminal

Start the service in another terminal with a 256-bit access token, and keep it running:

```bash
export MINIPTY_BRIDGE_ACCESS_TOKEN="$(openssl rand -hex 32)"; echo "$MINIPTY_BRIDGE_ACCESS_TOKEN"
dotnet samples/SingleFiles/VsCodePersistentBridge.cs     # listens on http://127.0.0.1:5171
```

```powershell
$env:MINIPTY_BRIDGE_ACCESS_TOKEN = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)); $env:MINIPTY_BRIDGE_ACCESS_TOKEN
dotnet samples\SingleFiles\VsCodePersistentBridge.cs
```

Run **MiniPty: Open Persistent Terminal**. The extension reads the token from `MINIPTY_BRIDGE_ACCESS_TOKEN` when VS Code inherited it, and otherwise asks for it (it is not saved in settings). Set a variable in the shell (`export STATE=kept`, or `set STATE=kept` in cmd.exe), then run **MiniPty: Simulate Persistent Terminal Disconnect**. The terminal prints a green `[MiniPty: connected]` again, and the variable still has its value: the extension reattached to the same shell.

Both processes also have a non-interactive check, which CI runs: `--smoke` on the published helper, and `dotnet samples/SingleFiles/VsCodePersistentBridge.cs --smoke` for the service (wrong tokens rejected, detach, reconnect from the acknowledged offset, state kept, normal exit).
