# MiniPty

NativeAOT-friendly minimal cross-platform pseudo-terminal library for .NET.

Windows uses ConPTY (`CreatePseudoConsole`). Linux, macOS, and FreeBSD use `openpty` + `fork` + `execvp`.

## Packages

| Layer | API | Purpose |
|-------|-----|---------|
| **Core** | `Pty.Start` → `PtySession` | `Stream` I/O, process lifecycle, `SignalEof`, `Resize` (stub) |
| **Recording** | `Pty.RecordAsync` → `PtyRecording` | Timestamped chunks for cast/recording tools |
| **Convenience** | `Pty.CaptureAsync`, `Pty.RunAsync` | Merged text or exit code only |

## Core API

```csharp
await using var session = Pty.Start(new PtySpawnOptions
{
    FileName = "/bin/bash",
    Arguments = ["-lc", "echo hello"],
    Columns = 80,
    Rows = 24,
});

// session.Input / session.Output are read/write streams
await session.Input.WriteAsync(Encoding.UTF8.GetBytes("input\n"));
session.SignalEof();
var code = await session.WaitForExitAsync();
```

## Recording API

```csharp
PtyRecording recording = await Pty.RecordAsync(
    new PtySpawnOptions { FileName = "cmd.exe", Arguments = ["/c", "echo hi"], Columns = 120, Rows = 24 },
    new PtyRecordOptions { Input = null }); // null stdin = TUI / no EOF

// recording.ExitCode
// recording.Chunks — IReadOnlyList<PtyChunk> with TimeSeconds + Data
// recording.Text — concatenated output
```

### `PtyRecording`

| Member | Description |
|--------|-------------|
| `ExitCode` | Child exit code (`128 + signal` on Unix signals) |
| `Chunks` | Timestamped output slices (`PtyChunk`) |
| `Text` | All chunk text merged |

## scenetake integration

scenetake maps `PtyRecording` to its own `CommandOutput` / `CommandOutputChunk` types — those live in scenetake, not MiniPty.

## Build & test

```bash
dotnet build src/MiniPty.csproj
dotnet run --project tests/MiniPty.Tests.csproj
```
