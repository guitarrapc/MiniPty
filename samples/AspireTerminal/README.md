# Aspire terminal

A shell in the browser under an [Aspire](https://aspire.dev) AppHost, so the Aspire dashboard charts MiniPty's metrics next to ASP.NET Core's and the runtime's. It shows the path the root README recommends for hosts that already use OpenTelemetry: one `AddMeter("MiniPty")`.

```bash
dotnet run samples/AspireTerminal/apphost.cs
```

1. Open the dashboard's login URL that the AppHost prints.
2. On the Resources page, open the **Terminal** link of the `terminal` resource. The link carries this run's access token.
3. Flood the terminal (`seq 1 2000000`, or `dir /s /b C:\Windows\WinSxS` on Windows), then open **Metrics** > `terminal` > **MiniPty**.

Both files are .NET 10 file-based apps, so no project files are needed. The dashboard and its OTLP endpoint use the HTTPS development certificate; run `dotnet dev-certs https --trust` once if the browser warns about it.

## Files

| File | What it is |
|---|---|
| [apphost.cs](apphost.cs) | The Aspire AppHost. It runs `terminal.cs` with `AddCSharpApp`, hands it a fresh access token, and puts the token in the dashboard's link. |
| [terminal.cs](terminal.cs) | An ASP.NET Core app: an xterm.js page and one shell per WebSocket through `PtyWebSocketBridge`, with OpenTelemetry exporting metrics to the dashboard. Run alone, it serves `http://localhost:5172/` and exports nothing. |

## How the app exports MiniPty's metrics

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddMeter("MiniPty", "Microsoft.AspNetCore.Hosting", "System.Runtime"))
    .UseOtlpExporter();
```

Aspire passes the dashboard's OTLP endpoint in `OTEL_EXPORTER_OTLP_ENDPOINT`, and the app exports only when it is set.

| Instrument | What the dashboard shows |
|---|---|
| `minipty.session.active` | Open sessions: one per browser tab. |
| `minipty.session.start.duration` | How long each `Pty.Start` took, as a histogram. |
| `minipty.session.start.failures` | Failed starts by `error.type`. It appears once a start fails. |
| `minipty.output.bytes` | Output read from the shells. |
| `minipty.output.stall.duration` | Each time reading stood still because a session's output buffer was full: the browser had not taken the output yet, and the shell waited. It appears once a stall happens. On Windows ConPTY renders more slowly than xterm.js draws, and the floods tried here (2.5 MiB and more) did not stall. |

`minipty.output.consumer_wait.duration` does not appear: the bridge coalesces bursts on a timer and never waits for output in native code ([Metrics](../../.github/docs/specs/core_session.md#metrics)).

## Points to get right

- **Keep the access check.** `PtyWebSocketBridge` authenticates nothing: whoever holds the socket types into the shell. The app accepts a WebSocket only on its own loopback address, from its own page, with this run's token, as WebTerminal.cs does. The AppHost turns off Aspire's proxy for the endpoint (`isProxied: false`), because the check compares the request's port with the port the app listens on.
- **Export often enough to see a flood.** OpenTelemetry exports metrics once a minute by default. The AppHost sets `OTEL_METRIC_EXPORT_INTERVAL=1000` so the charts follow a flood as it runs.
- **Dependencies stay in the sample.** Aspire and OpenTelemetry are this sample's packages, not MiniPty's: MiniPty itself only creates the meter, with `System.Diagnostics.Metrics` from the runtime.

## Notes

- `AddCSharpApp` for a file-based app is experimental in Aspire 13, so `apphost.cs` suppresses `ASPIRECSHARPAPPS001`. The build warns `ASPIRE010` without the Aspire CLI; `dotnet run` works without it.
- CI builds both files on one runner. Running them needs the dashboard and a browser.
