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
- Merged `Output`, decoded text, and `ExitCode` are stable results. Chunk count and split points follow output reads, and are not compatibility guarantees: reads that follow one another within 500 µs join one chunk, up to 4 KiB and 2 ms from its first read, whose time is that first read's.

PTY output is a raw byte stream. Capture APIs do not normalize newlines or strip control sequences by default; sequences may span chunk boundaries.

## Relationship To Core

Timestamped chunks are not part of `MiniPty` core. Core callers use streams and completion results; capture callers opt into timestamped observation by referencing `MiniPty.Capture`.

Capture is a one-shot completion consumer, not a second persistent consumer layered over `ReadOutputAsync`: it attaches a sink that the session's output driver writes into right after each read, on the thread that completed the read. Timestamping and decoding stay in **MiniPty.Capture**; lifecycle, stdin, exit wait, and post-exit drain remain owned by the shared completion orchestration.

## Lessons Learned

- Start the output pump before waiting for child exit. Waiting for the pump to reach EOF before polling exit deadlocks when a large-output child fills the PTY pipe.
- `OutputDrainGrace` is post-exit drain. Closing the transport while the child is still running caused truncated output and platform-specific failures; exit is observed first, then the bounded drain may close a stalled transport.
- Capture allocation work must not weaken `ReadOutputAsync` backpressure or turn chunk boundaries into a public contract. Writing where output is read keeps capture free of a consumer thread and of per-read hand-off costs, and collecting in pooled segments without per-thread caches keeps its cost independent of the thread that writes.
- Timestamp at the read, not at the consumer. Delivering right after each read makes chunk times read-completion times, the moment output became available, independent of how quickly anything consumes it. Coalescing within the micro-window (Unix) does not move a chunk's time: it is taken when the slot's first read completes. On Windows a read's time is taken when its I/O completion is handled, so a starved thread pool delays it along with the delivery itself.
- Join close reads in the sink once the reader no longer does. The Windows driver publishes each ConPTY slice as it completes, so a sink written once per read recorded one chunk per slice again: 4.7 KB more per 32 KiB capture, 9.3 KB with text. The sink joins a read into the previous chunk when it follows within 500 µs, the chunk is younger than 2 ms, and it stays within 4 KiB, the reader thread's own windows, decided from the reads' timestamps as each arrives, so nothing is held back and no timer runs. Output delivered on attach has no read timestamp and never joins, so the backlog and the read in flight stay apart as before.
