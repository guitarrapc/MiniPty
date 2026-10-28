# Web terminal

Serve a shell to [xterm.js](https://xtermjs.org/) in the browser. MiniPty.Terminal is the whole server side: `PtyWebSocketBridge.RunAsync` starts the PTY, sends its output over a WebSocket with flow control, writes the browser's input, applies resizes, and reports the exit. Your host accepts the WebSocket and decides who may connect.

```bash
dotnet samples/SingleFiles/WebTerminal.cs                        # HttpListener, then open the http://localhost:5170/#token=... address it prints
dotnet run -c Release samples/SingleFiles/WebTerminalScale.cs    # ASP.NET Core with many sessions, at http://localhost:5171/#token=...
```

## Server

[WebTerminal.cs](WebTerminal.cs) uses `HttpListener`. The MiniPty part is one call per connection:

```csharp
var ws = await context.AcceptWebSocketAsync("minipty");
var status = await PtyWebSocketBridge.RunAsync(
    new PtyStartInfo { FileName = shell, Size = new PtySize(80, 24) },
    ws.WebSocket,
    cancellationToken: ct);
```

In ASP.NET Core ([WebTerminalScale.cs](WebTerminalScale.cs)) it is the same call. Pass `HttpContext.RequestAborted`, so the bridge stops as soon as the connection is gone:

```csharp
app.UseWebSockets();
app.Map("/ws", async context =>
{
    // Authenticate first (see below).
    using var socket = await context.WebSockets.AcceptWebSocketAsync("minipty");
    await PtyWebSocketBridge.RunAsync(startInfo, socket, cancellationToken: context.RequestAborted);
});
```

`RunAsync` returns the exit status when the shell exits, after the last output and an `exit` message have been sent. When the browser disconnects or the token is canceled, it ends the shell. One connection is one shell; for terminals that survive a reload or a dropped connection, use `PtyWebSocketSessionManager` as the [VS Code sample's persistent terminal](../VsCodeExtension/README.md#persistent-terminal-a-service-that-outlives-the-extension) does.

## Browser

The page is xterm.js plus a few lines that speak the bridge's protocol: binary frames carry terminal bytes in both directions, and text frames carry JSON control messages.

```js
const ws = new WebSocket(`ws://${location.host}/ws`, ['minipty', `minipty-token.${token}`]);
ws.binaryType = 'arraybuffer';
const encoder = new TextEncoder();
let unacked = 0;

ws.onopen = () => {
  sendResize();
  term.onData(data => ws.send(encoder.encode(data)));    // keys and paste
  term.onResize(sendResize);
};
ws.onmessage = e => {
  if (typeof e.data === 'string') {                       // {"type":"exit","exitCode":0}
    return;
  }
  const bytes = new Uint8Array(e.data);
  term.write(bytes, () => {                               // ACK what xterm.js has processed
    unacked += bytes.length;
    if (unacked >= 131072) {
      ws.send(JSON.stringify({ type: 'ack', bytes: unacked }));
      unacked = 0;
    }
  });
};

function sendResize() {
  ws.send(JSON.stringify({ type: 'resize', cols: term.cols, rows: term.rows }));
}
```

- **ACK the bytes xterm.js has written**, in 128 KiB steps (the bridge's `LowWatermark`). The bridge stops sending at 384 KiB unacknowledged (`HighWatermark`), so a flood such as `cat` of a large file pauses the shell instead of filling the browser's memory. A client that never ACKs stops receiving output after 384 KiB.
- **Send `resize` on open and on every `term.onResize`.** The PTY starts at the `PtyStartInfo.Size` until then.
- **Decode nothing yourself.** Hand the binary payload to `term.write` as bytes; xterm.js reassembles UTF-8 characters split across frames.

## Authenticate the connection

The bridge authenticates nothing: whoever holds the WebSocket types into the shell. Listening on localhost is not enough, because any web page the user visits can open a WebSocket to localhost. The sample's `LocalAccess` class refuses a request unless all three checks pass:

| Check | What it refuses |
|---|---|
| `Origin`, when present, is the server's own origin | pages from other sites |
| `Host` is the server's loopback name and port | a site that points its own domain at 127.0.0.1 (DNS rebinding) |
| The request offers this run's random token as the subprotocol `minipty-token.<hex>` | other local users and non-browser clients, which can forge any header |

The server prints the token in the URL fragment (`#token=...`), which browsers never send to a server, and the page sends it back as a subprotocol rather than in the URL, so request logs never record it. Copy `LocalAccess`, or put your own authentication in front of `RunAsync`.

## Notes

- The page is embedded in the executable and loads xterm.js from a CDN, so `WebTerminal.cs` publishes with NativeAOT as one file.
- xterm.js sends Ctrl+C and Ctrl+V to the program. The page's `attachCustomKeyEventHandler` leaves Ctrl+V, and Ctrl+C while text is selected, to the browser, so they paste and copy.
- [WebTerminalScale.cs](WebTerminalScale.cs) also shows what each session costs the host (threads, memory, handles), and its `bench` mode measures it: `dotnet run -c Release samples/SingleFiles/WebTerminalScale.cs -- bench --sessions 16,64,256`.
- `--smoke` runs the server against its own client and checks the refusals, a command's output, and the `exit` message; CI runs it.
- Protocol, flow-control options, and close behavior: [terminal.md](../../.github/docs/specs/terminal.md#ptywebsocketbridge--frontend-bridge).
