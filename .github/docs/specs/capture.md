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
- Merged `Output`, decoded text, and `ExitCode` are stable results. Chunk count and split points follow transport reads and are not compatibility guarantees.

PTY output is a raw byte stream. Capture APIs do not normalize newlines or strip control sequences by default; sequences may span chunk boundaries.

## Relationship To Core

Timestamped chunks are not part of `MiniPty` core. Core callers use streams and completion results; capture callers opt into timestamped observation by referencing `MiniPty.Capture`.

Capture uses the one-shot completion transport pump rather than layering a second persistent output
consumer over `ReadOutputAsync`. Timestamping and decoding stay in **MiniPty.Capture**; lifecycle,
stdin, exit wait, and post-exit drain remain owned by the shared completion orchestration.

## Lessons Learned

- Start the output pump before waiting for child exit. Waiting for the pump to reach EOF before polling exit deadlocks when a large-output child fills the PTY pipe.
- `OutputDrainGrace` is post-exit drain. Closing the transport while the child is still running caused truncated output and platform-specific failures; exit is observed first, then the bounded drain may close a stalled transport.
- Capture allocation work must not weaken `ReadOutputAsync` strict-handoff backpressure or turn chunk boundaries into a public contract. The transport pump keeps capture allocation near its historical baseline while core streaming remains independently optimized.
