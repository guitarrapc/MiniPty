# Completion Specification

Implemented contract for `PtySession.CompleteAsync`, `PtyCompleteOptions`, and `PtyResult`.

## `CompleteAsync`

`CompleteAsync` collects the PTY output, optionally writes stdin, waits for child exit, drains remaining output, and returns a `PtyResult`. Output is collected as it arrives (on the I/O loop on Windows, on the watcher loop on Unix), so awaiting `CompleteAsync` does not block the calling thread on output or exit. `Input`, when set, is written first as by `WriteInputAsync`, without blocking the calling thread (see [Core Session](core_session.md#caller-context)).

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
| `MaxOutputBytes` | null | Most output bytes collected. Null means as much as one result can hold, `Array.MaxLength` (just under 2 GiB); a larger value means the same, and zero or less is rejected when set. Past it the child is killed and the call fails with `IOException` ([Output limit](#output-limit)). |
| `KillOnCancellation` | true | `CompleteAsync` only: cancellation kills the child when true. |

## Output limit

The result holds the whole output, so a one-shot run takes memory that grows with it: until the result is built, about twice the output with `DecodeOutput` off and about six times with it on (the bytes and the decoded text as they arrive, then each merged into one array). Measured on 64 MiB (Windows) and 256 MiB (Linux) floods, the process peaked at 2.2–2.5 and 6.2–6.5 times the output. A result can hold at most `Array.MaxLength` bytes.

| Condition | Behavior |
|---|---|
| Output passes `MaxOutputBytes` (or `Array.MaxLength`) | Collection stops, the buffers are given back, the child is killed, and the call throws `IOException` naming the limit once the exit is observed. |
| Collecting output fails otherwise (the output transport fails) | The same: the child is killed and the failure is thrown. |
| Input still being written when the child is killed | The output failure is thrown, not the input write's. |

**Why the child is killed:** the completion waits for the child's exit before it drains output. A collection that stopped without ending the child left that wait open for good: a 2.2 GiB flood stopped being collected at 2 GiB, where a buffer's length wrapped, the child blocked on a full terminal, and the call never returned, with no error. Killed, the same flood failed after 17 s with the limit in the message. `ExitTimeout` would have bounded the wait, but its default is to wait without end, and a timeout would not have said why.

## `PtyResult`

| Member | Type | Description |
|---|---|---|
| `Output` | `ReadOnlyMemory<byte>` | Merged raw PTY output. |
| `ExitCode` | `int` | Child exit code; `128 + signal` for a Unix child ended by a signal. Equals `ExitStatus.ExitCode`. |
| `ExitStatus` | `PtyExitStatus` | Exit code plus the Unix termination signal, as `WaitForExitAsync` reports it ([Exit Status](core_session.md#exit-status)). |
| `GetText()` | `ReadOnlyMemory<char>` | Decoded text; zero-alloc when `DecodeOutput` was true. |
| `GetTextString()` | `string` | Materialized decoded text. |
| `Contains(string)` | `bool` | Search decoded text. |
| `Contains(ReadOnlySpan<byte>)` | `bool` | Search raw bytes without decoding. |
| `ContainsUtf8(string)` | `bool` | Search a UTF-8 pattern in raw bytes. |

Use `GetText()`, `Contains`, or `Output.Span` for inspection. Use [Display text](display_text.md) helpers when writing PTY output to a host-readable log or console.
