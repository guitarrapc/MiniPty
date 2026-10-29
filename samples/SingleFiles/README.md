# Single-file samples

Each file here is a complete program that runs with `dotnet <file>.cs`, with no project file. They build against the MiniPty sources in this repository, except the `README_*.cs` files, which use the published NuGet packages.

```bash
dotnet samples/SingleFiles/Session.cs
dotnet publish samples/SingleFiles/Session.cs -c Release -r linux-x64 --self-contained true -p:PublishAot=true -o out   # NativeAOT
```

Before a NativeAOT publish on Linux or macOS, build the native shim for the RID: `bash scripts/build-native.sh linux-x64` (`runtimes/` is not checked in). A JIT run builds the shim with the system C compiler by itself.

| To | Sample | MiniPty API |
|---|---|---|
| Try the code from the root README | [README_MiniPty.cs](README_MiniPty.cs), [README_MiniPty.Capture.cs](README_MiniPty.Capture.cs), [README_MiniPty.Console.cs](README_MiniPty.Console.cs) | all four packages' entry points |
| Run a command and get its output and exit code | [Session.cs](Session.cs) | `Pty.Start`, `CompleteAsync`, `Output`, `SendEof` |
| Drive a running program: wait for output, answer it, resize | [Interactive.cs](Interactive.cs) | `ReadOutputAsync`, `WriteInputAsync`, `Resize` |
| Record output with the time of each read | [Capture.cs](Capture.cs), [Observe.cs](Observe.cs) | `PtyCapture.RunAsync` |
| Hand your terminal to a shell or an editor | [ConsoleAttach.cs](ConsoleAttach.cs) | `PtyConsoleInput.Attach` |
| Serve a terminal to a browser | [WebTerminal.cs](WebTerminal.cs), [WebTerminalScale.cs](WebTerminalScale.cs) | `PtyWebSocketBridge` |
| Back a VS Code terminal | [VsCodeTerminalHelper.cs](VsCodeTerminalHelper.cs), [VsCodePersistentBridge.cs](VsCodePersistentBridge.cs) | `PtyStdioBridge`, `PtyWebSocketSessionManager` |
| See what `InheritCursor` waits for, measure the bridges, check the packages | [InheritCursorWait.cs](InheritCursorWait.cs), [BridgeTraffic.cs](BridgeTraffic.cs), [NuGetSmoke.cs](NuGetSmoke.cs) | tools for MiniPty's own development |

Full-screen TUIs and GUI terminal windows have their own projects: [TuiDemo](../TuiDemo), [WinUiTerminal](../WinUiTerminal), [AvaloniaTerminal](../AvaloniaTerminal).

## README snippets

`README_MiniPty.cs`, `README_MiniPty.Capture.cs`, and `README_MiniPty.Console.cs` hold the [root README](../../README.md#quick-start)'s Quick start code as runnable files. They reference the NuGet packages (`#:package MiniPty@1.4.0`), so they show exactly what an application that installs MiniPty writes.

## Run a command for its result

[Session.cs](Session.cs). `CompleteAsync` reads all output and waits for the exit, which is all a command run for its result needs:

```csharp
await using var session = Pty.Start(new PtyStartInfo
{
    FileName = "/bin/sh",
    Arguments = ["-c", "echo hello"],
    KillProcessTree = true,   // Windows: also end what the command started, when the session ends
});
var result = await session.CompleteAsync(new PtyCompleteOptions { ExitTimeout = TimeSpan.FromSeconds(15) });
Console.WriteLine($"exit={result.ExitCode}: {PtyOutput.ToDisplayText(result.GetText(), PtyOutputDisplayMode.PlainText)}");
```

To feed the child's stdin, read the `Output` stream yourself and end the input with `SendEof`:

```csharp
await using var session = Pty.Start(startInfo);
using var output = new MemoryStream();
var pump = session.Output.CopyToAsync(output);   // keep reading from the start, or the child blocks on a full PTY
await session.WriteInputAsync("line 3\rline 1\rline 2\r");
session.SendEof();                               // Ctrl+D on Unix, Ctrl+Z and Enter on Windows
var status = await session.WaitForExitAsync();
await pump;                                      // ends after the last output
var text = Encoding.UTF8.GetString(output.ToArray());   // decode once: a read can end inside a UTF-8 character
```

- **Enter is `\r`** on every platform, as a terminal sends it: ConPTY reads it as Enter, and MiniPty's Unix PTYs turn it into a newline.
- **`KillProcessTree`** (Windows) ends everything the command started when the session ends, even programs in a console of their own. Turn it on for commands run for their result; leave it off for interactive terminals, where a window the user opened should outlive the tab.
- PTY output is terminal output: colors, cursor moves, and `\r\n` line ends. `PtyOutput.ToDisplayText(..., PlainText)` strips them for logs and assertions; keep the raw bytes when a terminal displays them. The output also contains the input, because a terminal echoes what is typed.
- `session.Resize(new PtySize(100, 28))` works at any time; the child sees the new size (`stty size`).
- In a `cmd /c` command, name Windows tools by full path (`%SystemRoot%\System32\sort.exe`): a bare `sort` or `find` can resolve to another program first on PATH, such as Git's GNU tools. What follows `/c` is parsed by `cmd.exe`, so never put user input there.

## Drive an interactive program

[Interactive.cs](Interactive.cs). One reader runs for the whole session, and the code writes input when the output it waits for appears:

```csharp
await using var session = Pty.Start(startInfo);
var output = new OutputLog();
var reader = output.ReadAllAsync(session, ct);   // the one reader, started before the first write

await output.WaitForAsync("ready", ct);
await session.WriteInputAsync("alpha\r");
await output.WaitForAsync("first:alpha", ct);    // text only the child's answer contains, not the echo of the input
session.Resize(new PtySize(100, 28));
await session.WriteInputAsync("beta\r");

await reader;                                    // ends at the child's last output
var status = await session.WaitForExitAsync();
```

`OutputLog` is a small class in the sample:

- `ReadAllAsync` runs `await foreach` over `ReadOutputAsync` and decodes each chunk right away, because `chunk.Data` is valid only until the next iteration. It decodes with one `Decoder` kept across chunks, since a chunk can end in the middle of a UTF-8 character.
- `WaitForAsync` checks the text so far and otherwise waits for a signal the reader gives on each chunk, so nothing polls. One deadline (`ct`) bounds the whole conversation, so a child that never prints the marker cannot hang it.
- On Windows the output shows earlier lines twice after a resize: ConPTY repaints the screen, which a terminal frontend draws over the same cells.

## Capture output with timing

[Capture.cs](Capture.cs) is the smallest use of MiniPty.Capture; [Observe.cs](Observe.cs) prints a timeline and feeds stdin. `PtyCapture.RunAsync` starts the child, writes the input, and returns every read with its time since `Pty.Start`:

```csharp
var result = await PtyCapture.RunAsync(startInfo, new PtyCaptureOptions
{
    // Input is typed once the child starts, then end of input follows (SendEofAfterInput defaults to true).
    Completion = new PtyCompleteOptions { Input = "line 1\r", ExitTimeout = TimeSpan.FromSeconds(30) },
});

foreach (var chunk in result.GetTextChunks())
    Console.WriteLine($"+{chunk.Time.TotalSeconds:F3}s  {chunk.Text.Length} chars");
```

- `result.Output` holds all the bytes; `result.Chunks` splits them into reads, in order, with no gaps. `GetTextChunks` gives the same reads as text.
- Each chunk is one read from the PTY, not one write by the child: the OS can join or split writes.

## Hand your terminal to a program

[ConsoleAttach.cs](ConsoleAttach.cs) runs a shell in your terminal through a PTY. Run it from an interactive terminal (on Windows: Windows Terminal, PowerShell, or cmd, not Git Bash or MSYS).

```csharp
using var session = Pty.Start(startInfo);       // InheritCursor = true on Windows keeps your screen
using var exited = new CancellationTokenSource();
using (var input = PtyConsoleInput.Attach(session))
{
    var pump = CopyOutputToStdoutAsync(session);   // await foreach + stdout.WriteAsync(chunk.Data)
    _ = session.WaitForExitAsync().ContinueWith(_ => exited.Cancel());
    input.PumpInputUntil(exited.Token);           // keys are forwarded in the background; this waits for the exit
    pump.GetAwaiter().GetResult();
}
```

- MiniPty.Console handles input and the window size. Copying output to stdout stays your job.
- Write status messages before `Attach`. While attached, stdout on Unix is raw and does not turn `\n` into `\r\n`, so after detaching the sample writes `\r\n` to put the parent shell's prompt at the start of a line.
- With `InheritCursor`, attach before the output copy starts; the [TUI demo](../TuiDemo/README.md#using-minipty-with-a-tui) explains why, and covers running full-screen programs.
- On Windows the sample sets `Console.OutputEncoding = Encoding.UTF8` while the shell runs and restores it afterwards: PTY output is UTF-8, and a console with a legacy code page would misdecode non-ASCII text. MiniPty.Console handles the input side itself.

## Web terminal

[WebTerminal.cs](WebTerminal.cs) serves a shell to [xterm.js](https://xtermjs.org/) in the browser; [WebTerminalScale.cs](WebTerminalScale.cs) does the same in ASP.NET Core with many sessions.

```bash
dotnet samples/SingleFiles/WebTerminal.cs                        # then open the http://localhost:5170/#token=... address it prints
dotnet run -c Release samples/SingleFiles/WebTerminalScale.cs    # http://localhost:5171/#token=...
```

### Server

`PtyWebSocketBridge.RunAsync` is the whole server side: it starts the PTY, sends output over the WebSocket with flow control, writes the browser's input, applies resizes, and reports the exit. With `HttpListener`:

```csharp
var ws = await context.AcceptWebSocketAsync("minipty");
var status = await PtyWebSocketBridge.RunAsync(
    new PtyStartInfo { FileName = shell, Size = new PtySize(80, 24) },
    ws.WebSocket,
    cancellationToken: ct);
```

With ASP.NET Core, pass `HttpContext.RequestAborted`, so the bridge stops as soon as the connection is gone:

```csharp
app.UseWebSockets();
app.Map("/ws", async context =>
{
    // Authenticate first (see below).
    using var socket = await context.WebSockets.AcceptWebSocketAsync("minipty");
    await PtyWebSocketBridge.RunAsync(startInfo, socket, cancellationToken: context.RequestAborted);
});
```

`RunAsync` returns the exit status when the shell exits, after the last output and an `exit` message. When the browser disconnects or the token is canceled, it ends the shell.

The shell's `PtyStartInfo` describes the frontend, not the server's own terminal: `TerminalName = "xterm-256color"` (MiniPty uses that default only when no `TERM` is inherited, and a server started from tmux would pass on `screen-256color`), and `COLORTERM=truecolor` in `Environment`, because xterm.js draws 24-bit color. On Windows the sample stays on the in-box ConPTY so it publishes as one file; a production terminal should use the [out-of-band ConPTY](../../README.md#out-of-band-conpty-for-windows). One connection is one shell; for terminals that survive a reload, use `PtyWebSocketSessionManager` as the [VS Code sample's persistent terminal](../VsCodeExtension/README.md#persistent-terminal-a-service-that-outlives-the-extension) does.

### Browser

Binary frames carry terminal bytes in both directions, and text frames carry JSON control messages:

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

- **ACK the bytes xterm.js has written**, in 128 KiB steps (the bridge's `LowWatermark`). The bridge stops sending at 384 KiB unacknowledged (`HighWatermark`), so a flood pauses the shell instead of filling the browser's memory. A client that never ACKs stops receiving output after 384 KiB.
- **Send `resize` on open and on every `term.onResize`.** Until then the PTY has the `PtyStartInfo.Size`.
- **Pass the bytes to `term.write` as they are.** xterm.js reassembles UTF-8 characters split across frames.

### Authenticate the connection

The bridge authenticates nothing: whoever holds the WebSocket types into the shell. Listening on localhost is not enough, because any web page the user visits can open a WebSocket to localhost. The sample's `LocalAccess` class refuses a request unless all three checks pass:

| Check | What it refuses |
|---|---|
| `Origin`, when present, is the server's own origin | pages from other sites |
| `Host` is the server's loopback name and port | a site that points its own domain at 127.0.0.1 (DNS rebinding) |
| The request offers this run's random token as the subprotocol `minipty-token.<hex>` | other local users and non-browser clients, which can forge any header |

The server prints the token in the URL fragment (`#token=...`), which browsers never send to a server, and the page sends it back as a subprotocol rather than in the URL, so request logs never record it. Copy `LocalAccess`, or put your own authentication in front of `RunAsync`.

The page is embedded in the executable and loads xterm.js from a CDN, so `WebTerminal.cs` publishes with NativeAOT as one file. `WebTerminalScale.cs` also shows what each session costs the host (threads, memory, handles), and `-- bench --sessions 16,64,256` measures it. Protocol and options: [terminal.md](../../.github/docs/specs/terminal.md#ptywebsocketbridge--frontend-bridge).

## VS Code terminal backend

[VsCodeTerminalHelper.cs](VsCodeTerminalHelper.cs) and [VsCodePersistentBridge.cs](VsCodePersistentBridge.cs) are the MiniPty side of the [VS Code extension sample](../VsCodeExtension), which explains both:

```csharp
// VsCodeTerminalHelper.cs: started by the extension, framed over stdio; the shell ends with the helper.
var status = await PtyStdioBridge.RunAsync(startInfo, Console.OpenStandardInput(), Console.OpenStandardOutput(), cancellationToken: ct);

// VsCodePersistentBridge.cs: a loopback service; the shell survives the extension and is replayed on reconnect.
var credentials = manager.CreateSession(startInfo);
var exited = await manager.ConnectAsync(credentials.SessionId, sessionToken, acknowledgedOffset, webSocket, ct);
```

## Tools for MiniPty's own development

- [InheritCursorWait.cs](InheritCursorWait.cs) (Windows): with `InheritCursor = true`, ConPTY asks the frontend for the cursor position (`ESC[6n`) and holds the child until it gets an answer such as `await session.WriteInputAsync("\e[1;1R"u8.ToArray())`. The sample prints a timeline for answering at once, late, and never, on the in-box and the out-of-band ConPTY: a frontend that never answers shows a blank terminal that ignores Ctrl+C.
- [BridgeTraffic.cs](BridgeTraffic.cs): frames and latency of `PtyWebSocketBridge` for several kinds of output, used when changing how the bridges join output (`dotnet run -c Release samples/SingleFiles/BridgeTraffic.cs`).
- [NuGetSmoke.cs](NuGetSmoke.cs): not run on its own. [scripts/run-nuget-smoke.sh](../../scripts/run-nuget-smoke.sh) compiles it against the packed packages to check them, including that `OpenConsole.exe` is staged beside `conpty.dll` on Windows.

CI publishes the samples with NativeAOT on every platform and runs them; the server samples run with `--smoke`, a check against their own client.
