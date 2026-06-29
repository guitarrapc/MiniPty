# MiniPty Specification

User-facing specification entry point for **MiniPty**, **MiniPty.Capture**, and **MiniPty.Console** NuGet packages. Detailed contracts are split by behavior area under [specs/](specs/). OS-level implementation notes live in [references/](references/) (for example [pty_crossplatform.md](references/pty_crossplatform.md), [windows_console_input.md](references/windows_console_input.md)).

## Motivation

Many CLI tools and terminal UIs behave differently when stdout is not a TTY: they disable color, skip animations, or refuse to run. A pseudo-terminal gives the child real terminal semantics while the parent reads and writes a byte stream.

MiniPty exists as a **standalone, NativeAOT-friendly** library so any .NET program can spawn PTY children without bundling winpty or external helpers. Observation semantics are split into **MiniPty.Capture** so core callers that only need streams and exit codes are not forced to depend on capture types.

## Use Cases

| # | Goal | Primary packages | Specification |
|---|---|---|---|
| **1** | PTY transport (node-pty–equivalent core) | **MiniPty** | [Core session](specs/core_session.md), [Lifecycle](specs/lifecycle.md) |
| **2** | One-shot stdin + timestamped record | **MiniPty.Capture** | [Capture](specs/capture.md) |
| **3** | Interactive host (vim, etc.) — human types on real terminal | **MiniPty** + **MiniPty.Console** | [Console](specs/console.md) (embedder owns `ReadOutputAsync`) |
| **4** | In-editor terminal backend (xterm.js, etc.) | **MiniPty** only | Core embedder pattern in [Core session](specs/core_session.md); editor parity features are a **separate plan** |

Use case 3 recording and cast format remain **scenetake** (or other embedder) responsibilities. **MiniPty.Console** does not record output.

## Package Responsibilities

| Package | Responsibility |
|---|---|
| **MiniPty** | Spawn a child in a PTY; expose `Input` / `Output` streams; persistent bytes-only output streaming; lifecycle operations; one-shot completion |
| **MiniPty.Capture** | One-shot `PtyCapture.RunAsync` observation with per-read timestamps, merged output, decoded text, and exit code (use case 2) |
| **MiniPty.Console** | Host terminal input attach for use case 3: TUI host modes, keyboard bytes → PTY, resize sync; **does not** read PTY output |

Timestamped chunks are **not** part of the core API. Consumers that need to observe PTY output over time take a dependency on **MiniPty.Capture** (one-shot) or implement their own `ReadOutputAsync` loop (interactive).

## Implemented Scope

| Goal | Specification |
|---|---|
| Spawn and control a PTY-backed child process | [Core session](specs/core_session.md) |
| Consume persistent bytes-only PTY output | [Core session](specs/core_session.md), [Lifecycle](specs/lifecycle.md) |
| Run a one-shot command with optional stdin and drained output | [Completion](specs/completion.md) |
| Observe one-shot output with per-read timestamps | [Capture](specs/capture.md) |
| Convert PTY text into host-readable output | [Display text](specs/display_text.md) |
| Understand cancellation, EOF, drain, and disposal behavior | [Lifecycle](specs/lifecycle.md) |
| Understand supported OS targets and public platform guarantees | [Platform support](specs/platform_support.md) |
| Persistent transport sample (`ReadOutputAsync` command loop) | [samples/Interactive.cs](../../samples/Interactive.cs) |
| Attach host terminal input to an existing `PtySession` (use case 3) | [Console](specs/console.md) |

## Planned Scope

_(No open package milestones. Editor terminal backend parity for use case 4 is tracked in a separate plan.)_

## Out of Scope For The Current Implementation

- Terminal emulation, TUI replay, or faithful screen-buffer rendering
- Cast / asciinema recording (embedder responsibility; scenetake for example)
- In-editor terminal integration and node-pty parity (pause, ConPTY clear, exit signal split) — separate plan for use case 4
- Remote shells (`ssh`)
- Spilling capture to disk when memory is exhausted
- Capture tuning such as max chunk size or chunk timestamp modes

Planning notes for Console implementation and deferred editor parity are in [plans/plan_minipty_next.md](plans/plan_minipty_next.md). Planning documents are not implemented API contracts unless mirrored in [specs/](specs/).

## Related Documents

- [specs/core_session.md](specs/core_session.md) — `Pty.Start`, `PtySession`, streams, resize, wait, kill, dispose, embedder patterns
- [specs/completion.md](specs/completion.md) — `CompleteAsync`, `PtyCompleteOptions`, `PtyResult`
- [specs/capture.md](specs/capture.md) — `MiniPty.Capture` timestamped observation
- [specs/console.md](specs/console.md) — `MiniPty.Console` host input attach
- [specs/display_text.md](specs/display_text.md) — `PtyOutput.ToDisplayText`
- [specs/lifecycle.md](specs/lifecycle.md) — cancellation, EOF, drain, disposal, failure behavior
- [specs/platform_support.md](specs/platform_support.md) — public platform support and verification constraints
- [references/pty_crossplatform.md](references/pty_crossplatform.md) — ConPTY, `forkpty`, EOF staging, interop details
- [references/windows_console_input.md](references/windows_console_input.md) — Windows host stdin path for **MiniPty.Console**
- [README.md](../../README.md) — quick-start examples
- [scenetake spec_pty.md](https://github.com/guitarrapc/scenetake/blob/main/.github/docs/spec_pty.md) — `pty: true` YAML and cast integration (use case 2 today)
