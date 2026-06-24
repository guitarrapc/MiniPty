# Capture Specification

Implemented contract for the **MiniPty.Capture** package.

## Purpose

`MiniPty.Capture` observes a PTY execution from outside the child. Output is read while the process runs, and each read becomes a timestamped chunk.

MiniPty does not define a recording format. Consumers build timelines or artifacts from `Chunks`. Optional display helpers are for host-readable output, not recording.

## API Contract

```csharp
PtyCaptureResult result = await PtyCapture.RunAsync(startInfo, options);
// result.Output          - merged raw PTY bytes
// result.GetText()       - decoded text (ReadOnlyMemory<char>)
// result.ExitCode
// result.Chunks          - PtyCaptureChunk(TimeSpan Time, ReadOnlyMemory<byte> Data)
// result.GetTextChunks() - PtyCaptureTextChunk(TimeSpan Time, ReadOnlyMemory<char> Text)
```

- `PtyCaptureOptions.Completion` wraps `PtyCompleteOptions`.
- `PtyCaptureOptions.TimeProvider` supplies the clock for chunk timestamps. Default is `TimeProvider.System`.
- Each chunk's `Time` is elapsed since session start, immediately after `Pty.Start`.
- The session is disposed when `RunAsync` completes. Disposal kills the child if it is still running.

PTY output is a raw byte stream. Capture APIs do not normalize newlines or strip control sequences by default; sequences may span chunk boundaries.

## Relationship To Core

Timestamped chunks are not part of `MiniPty` core. Core callers use streams and completion results; capture callers opt into timestamped observation by referencing `MiniPty.Capture`.
