# Completion Specification

Implemented contract for `PtySession.CompleteAsync`, `PtyCompleteOptions`, and `PtyResult`.

## `CompleteAsync`

`CompleteAsync` collects the PTY output, optionally writes stdin, waits for child exit, drains remaining output, and returns a `PtyResult`. Output is collected as it arrives (through I/O completions on Windows, on the watcher loop on Unix), so awaiting `CompleteAsync` does not block the calling thread on output or exit. `Input`, when set, is written first as by `WriteInputAsync`, without blocking the calling thread (see [Core Session](core_session.md#caller-context)).

This API is intended for one-shot command execution. It is not a high-level long-lived interactive session API.

## `PtyCompleteOptions`

| Option | Default | Purpose |
|---|---|---|
| `OutputEncoding` | UTF-8 | Decode PTY bytes to text. |
| `Input` | null | Stdin text. Null leaves stdin open; `""` signals EOF with no bytes. |
| `SendEofAfterInput` | true | Calls `SendEof` after writing `Input`. Ignored when `Input` is null. |
| `ExitTimeout` | null | Maximum wait for child exit. Null waits until exit or cancellation. |
| `OutputDrainGrace` | 1 second | Maximum post-exit drain budget before forcing transport close. On Windows ConPTY and Unix PTY masters, the transport closes earlier once reads have been quiet for an internal stall period (100 ms, after at least one read); this is drain timing only, not command completion. |
| `OutputReaderCloseTimeout` | 5 seconds | Time to wait for the reader after transport close. |
| `DecodeOutput` | true | When true, output is decoded as it arrives so `GetText()` returns a zero-alloc slice. When false, only `Output` is stored and `GetText()` decodes on demand. |
| `KillOnCancellation` | true | `CompleteAsync` only: cancellation kills the child when true. |

## `PtyResult`

| Member | Type | Description |
|---|---|---|
| `Output` | `ReadOnlyMemory<byte>` | Merged raw PTY output. |
| `ExitCode` | `int` | Child exit code. |
| `GetText()` | `ReadOnlyMemory<char>` | Decoded text; zero-alloc when `DecodeOutput` was true. |
| `GetTextString()` | `string` | Materialized decoded text. |
| `Contains(string)` | `bool` | Search decoded text. |
| `Contains(ReadOnlySpan<byte>)` | `bool` | Search raw bytes without decoding. |
| `ContainsUtf8(string)` | `bool` | Search a UTF-8 pattern in raw bytes. |

Use `GetText()`, `Contains`, or `Output.Span` for inspection. Use [Display text](display_text.md) helpers when writing PTY output to a host-readable log or console.
