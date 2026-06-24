# Display Text Specification

Implemented contract for `PtyOutput.ToDisplayText` and related display helpers.

## Purpose

PTY backends and capture APIs return a raw terminal byte stream. Writing that stream directly to the parent console can clear the screen, move the cursor, or change terminal modes.

For logging and other readable host output, transform decoded PTY text with `PtyOutput.ToDisplayText`.

```csharp
string plain = PtyOutput.ToDisplayText(result.GetText(), PtyOutputDisplayMode.PlainText);
```

## Modes

| Mode | Purpose |
|---|---|
| `Raw` | Return input unchanged for recording, replay, or custom handling. |
| `PlainText` | Remove CSI, OSC, and bell; normalize `\r\n` and lone `\r` to `\n`. |
| `AnsiText` | Same as `PlainText` but keep SGR sequences (`CSI ... m`) for colored host output. |

## Capture Helpers

`MiniPty.Capture` adds `PtyCaptureResult.ToDisplayText(mode)`, `ToDisplayTextFromOutput(mode)`, and `PtyCaptureTextChunk` list overloads that merge chunk text and then call `PtyOutput`.

## Non-goals

Display text conversion is best-effort. It is not full VT emulation, TUI replay, terminal-injection hardening, or faithful `\r` overwrite preservation. Unknown or malformed sequences may be left as-is. Callers that need progress-line fidelity should use `Raw`.
