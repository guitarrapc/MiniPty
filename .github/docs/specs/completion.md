# Completion Specification

Implemented contract for `PtySession.CompleteAsync`, `PtyCompleteOptions`, and `PtyResult`.

## `CompleteAsync`

`CompleteAsync` pumps the PTY output stream, optionally writes stdin, waits for child exit, drains remaining output, and returns a `PtyResult`.

This API is intended for one-shot command execution. It is not a high-level long-lived interactive session API.

## `PtyCompleteOptions`

| Option | Default | Purpose |
|---|---|---|
| `OutputEncoding` | UTF-8 | Decode PTY bytes to text. |
| `Input` | null | Stdin text. Null leaves stdin open; `""` signals EOF with no bytes. |
| `SendEofAfterInput` | true | Calls `SendEof` after writing `Input`. Ignored when `Input` is null. |
| `ExitTimeout` | null | Maximum wait for child exit. Null waits until exit or cancellation. |
| `OutputDrainGrace` | 1 second | Time to drain after child exit before closing the transport. |
| `OutputReaderCloseTimeout` | 5 seconds | Time to wait for the reader after transport close. |
| `DecodeOutput` | true | When true, pump decodes bytes so `GetText()` returns a zero-alloc slice. When false, only `Output` is stored and `GetText()` decodes on demand. |
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
