# MiniPty

NativeAOT-friendly minimal cross-platform pseudo-terminal library for .NET.

All public types live in the `MiniPty` namespace. The static entry type is `Pty` (a namespace cannot share the same name as a type in C#).

## API

| Type | Role |
|------|------|
| `Pty.Start(PtyOptions)` | Spawn child → `PtySession` (stream I/O, no wait) |
| `Pty.Run(PtyOptions)` | One-shot → `PtyCaptureResult` |
| `Pty.RunExitCodeAsync` | Exit code only |
| `PtySession` | `Input` / `Output` streams, `SignalEof`, `Resize`, wait, `Kill` |
| `PtyOptions` | Spawn + capture options |
| `PtySize` | Terminal dimensions |
| `PtyCaptureResult` | `Output`, `ExitCode`, `Chunks` |
| `PtyOutputChunk` | `Time`, `Data` |
| `PtyOutputRecorder` | `Start` + `CollectAsync` for session-based capture |

PTY merges stdout/stderr into a single `Output` stream — there is no separate stderr field.

## Example

```csharp
using MiniPty;

PtyCaptureResult result = await Pty.Run(new PtyOptions
{
    FileName = "/bin/bash",
    Arguments = ["-lc", "echo hello"],
    Columns = 120,
    Rows = 24,
});

Console.Write(result.Output);
foreach (var chunk in result.Chunks)
    Console.WriteLine($"{chunk.Time:F3}s {chunk.Data.Length} chars");
```

## scenetake

scenetake uses `PtyCaptureResult` for PTY commands and a separate `CommandExecution` wrapper for pipe-redirected commands (real stdout/stderr).

## Build

```bash
dotnet build src/MiniPty/MiniPty.csproj
dotnet pack src/MiniPty/MiniPty.csproj -o artifacts -c Release
```
