# Aspire terminal

A shell in the browser under an [Aspire](https://aspire.dev) AppHost, so the Aspire dashboard charts MiniPty's metrics next to ASP.NET Core's and the runtime's. It shows the path the root README recommends for hosts that already use OpenTelemetry: one `AddMeter("MiniPty")`.

```bash
dotnet run samples/AspireTerminal/apphost.cs
```

1. Open the dashboard's login URL that the AppHost prints.
2. Make the charts move, either way:
   - Press **Start** on the `load` resource (in its **...** menu, or on its console logs page). Every round, it opens 8 sessions, floods one more through a slow reader, and closes them all.
   - Or open the **Terminal** link of the `terminal` resource, which carries this run's access token, and flood it yourself (`seq 1 2000000`, or `dir /s /b C:\Windows\WinSxS` on Windows).
3. Open **Metrics**, choose the `terminal` resource, and pick an instrument under **MiniPty**.

All three files are .NET 10 file-based apps, so no project files are needed. The dashboard and its OTLP endpoint use the HTTPS development certificate; run `dotnet dev-certs https --trust` once if the browser warns about it.

## Files

| File | What it is |
|---|---|
| [apphost.cs](apphost.cs) | The Aspire AppHost. It runs `terminal.cs` with `AddCSharpApp`, hands it a fresh access token, and puts the token in the dashboard's link. |
| [terminal.cs](terminal.cs) | An ASP.NET Core app: an xterm.js page and one shell per WebSocket through `PtyWebSocketBridge`, with OpenTelemetry exporting metrics to the dashboard. Run alone, it serves `http://localhost:5172/` and exports only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set. |
| [load.cs](load.cs) | Browser-like clients of `terminal.cs`, started from the dashboard. Each round opens `LOAD_SESSIONS` idle sessions (8), floods one more with `LOAD_FLOOD_BYTES` (4 MiB) read by a client that acknowledges each 128 KiB `LOAD_ACK_DELAY_MS` late (100 ms), closes them all, and rests 5 s. Its console log reports each round. |

## How the app exports MiniPty's metrics

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddMeter("MiniPty", "Microsoft.AspNetCore.Hosting", "System.Runtime"))
    .UseOtlpExporter();
```

Aspire passes the dashboard's OTLP endpoint in `OTEL_EXPORTER_OTLP_ENDPOINT`, and the app exports only when it is set.

| Instrument | What the dashboard shows |
|---|---|
| `minipty.session.active` | Open sessions: one per browser tab, and 9 while a `load` round runs. |
| `minipty.session.start.duration` | How long each `Pty.Start` took, as P50, P90, and P99. |
| `minipty.session.start.failures` | Failed starts by `error.type`. It appears once a start fails. |
| `minipty.output.bytes` | Output read from the shells. |
| `minipty.output.stall.duration` | Each time reading stood still because a session's output buffer was full: the client had not taken the output yet, and the shell waited. It appears once a stall happens. `load`'s slow reader stalls in every round; on Windows its P50 fell in the 0.1–0.25 s bucket. A browser tab on Windows did not stall: ConPTY renders more slowly than xterm.js draws. |

`minipty.output.consumer_wait.duration` does not appear: the bridge coalesces bursts on a timer and never waits for output in native code ([Metrics](../../.github/docs/specs/core_session.md#metrics)).

## Points to get right

- **Keep the access check.** `PtyWebSocketBridge` authenticates nothing: whoever holds the socket types into the shell. The app accepts a WebSocket only on its own loopback address, from its own page, with this run's token, as WebTerminal.cs does. The AppHost turns off Aspire's proxy for the endpoint (`isProxied: false`), because the check compares the request's port with the port the app listens on.
- **A slow client is backpressure, not loss, while the shell runs.** The bridge stops delivering once 384 KiB are unacknowledged. MiniPty's output buffer then fills and reading stops, which blocks the shell until the client acknowledges. That is what `load`'s slow reader shows, and what a browser tab that cannot draw fast enough causes. Once the shell has exited and the bridge has sent the rest of the output, the exit message, and its close, it waits only its `CloseTimeout`, 5 s by default, for the client to close, and aborts the connection if the client has not. A client still reading then, such as `load` with a long `LOAD_ACK_DELAY_MS`, loses what it has not read.
- **Export often enough to see a flood.** OpenTelemetry exports metrics once a minute by default. The AppHost sets `OTEL_METRIC_EXPORT_INTERVAL=1000` so the charts follow a flood as it runs.
- **Dependencies stay in the sample.** Aspire and OpenTelemetry are this sample's packages, not MiniPty's: MiniPty itself only creates the meter, with `System.Diagnostics.Metrics` from the runtime.

## Notes

- `AddCSharpApp` for a file-based app is experimental in Aspire 13, so `apphost.cs` suppresses `ASPIRECSHARPAPPS001`. The build warns `ASPIRE010` without the Aspire CLI; `dotnet run` works without it.
- On Windows, `dotnet run apphost.cs` runs the AppHost as a child process. Stop it with Ctrl+C in its console: ending only the `dotnet` process can leave the AppHost, the dashboard, and the terminal running.
