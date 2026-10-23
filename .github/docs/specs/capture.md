# Capture Specification

Implemented contract for the **MiniPty.Capture** package.

## Purpose

`MiniPty.Capture` observes a PTY execution from outside the child. Output is read while the process runs, and each output read becomes a timestamped chunk.

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
- Each chunk's `Time` is the elapsed time from session start, immediately after `Pty.Start`, to the completion of the read that produced it. With `TimeProvider.System` the read's own timestamp is used; another provider is asked when the read is delivered, which is the same moment for output read after capture attached. Output read before capture attached is timestamped on delivery; a read that completed before the origin counts as zero. Chunk times never decrease: a read already in flight when capture attached is delivered after that earlier output and takes its time.
- The session is disposed when `RunAsync` completes. Disposal ends the child if it is still running (see [Core session](core_session.md)).
- Merged `Output`, decoded text, and `ExitCode` are stable results. Chunk count and split points follow output reads (one transport read plus what arrives within a short micro-window) and are not compatibility guarantees.

PTY output is a raw byte stream. Capture APIs do not normalize newlines or strip control sequences by default; sequences may span chunk boundaries.

## Relationship To Core

Timestamped chunks are not part of `MiniPty` core. Core callers use streams and completion results; capture callers opt into timestamped observation by referencing `MiniPty.Capture`.

Capture is a one-shot completion consumer, not a second persistent consumer layered over `ReadOutputAsync`: it attaches a sink that the session's output reader writes into on its own thread right after each read. Timestamping and decoding stay in **MiniPty.Capture**; lifecycle, stdin, exit wait, and post-exit drain remain owned by the shared completion orchestration.

## Lessons Learned

- Start the output pump before waiting for child exit. Waiting for the pump to reach EOF before polling exit deadlocks when a large-output child fills the PTY pipe.
- `OutputDrainGrace` is post-exit drain. Closing the transport while the child is still running caused truncated output and platform-specific failures; exit is observed first, then the bounded drain may close a stalled transport.
- Capture allocation work must not weaken `ReadOutputAsync` backpressure or turn chunk boundaries into a public contract. Writing where output is read keeps capture free of a consumer thread and of per-read hand-off costs, and collecting in pooled segments without per-thread caches keeps its cost independent of the thread that writes.
- Timestamp at the read, not at the consumer. Delivering on the reader thread right after each read makes chunk times read-completion times, the moment output became available, independent of how quickly anything consumes it. Coalescing within the micro-window does not move a chunk's time: it is taken when the slot's first read completes.
