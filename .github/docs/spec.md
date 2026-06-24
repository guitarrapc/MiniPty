# MiniPty Specification

User-facing specification entry point for the implemented **MiniPty** and **MiniPty.Capture** NuGet packages. Detailed contracts are split by behavior area under [specs/](specs/). OS-level implementation notes live in [references/pty_crossplatform.md](references/pty_crossplatform.md).

## Motivation

Many CLI tools and terminal UIs behave differently when stdout is not a TTY: they disable color, skip animations, or refuse to run. A pseudo-terminal gives the child real terminal semantics while the parent reads and writes a byte stream.

MiniPty exists as a **standalone, NativeAOT-friendly** library so any .NET program can spawn PTY children without bundling winpty or external helpers. Observation semantics are split into **MiniPty.Capture** so core callers that only need streams and exit codes are not forced to depend on capture types.

## Package Responsibilities

| Package | Responsibility |
|---|---|
| **MiniPty** | Spawn a child in a PTY; expose `Input` / `Output` streams; provide persistent bytes-only output streaming, lifecycle operations, and one-shot completion |
| **MiniPty.Capture** | One-shot `PtyCapture.RunAsync` observation with per-read timestamps, merged output, decoded text, and exit code |

Timestamped chunks are **not** part of the core API. Consumers that need to observe PTY output over time take a dependency on both packages.

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

## Out of Scope For The Current Implementation

- Full local-console attachment for programs such as vim, less, and htop
- Remote shells (`ssh`)
- Spilling capture to disk when memory is exhausted
- Capture tuning such as max chunk size or chunk timestamp modes
- Full terminal emulation, TUI replay, or faithful screen-buffer rendering

Future work for capture alignment, local console attachment, and optional node-pty parity features is tracked separately in [plans/plan_minipty_next.md](plans/plan_minipty_next.md). Planning documents are not implemented API contracts.

## Related Documents

- [specs/core_session.md](specs/core_session.md) — `Pty.Start`, `PtySession`, streams, resize, wait, kill, dispose
- [specs/completion.md](specs/completion.md) — `CompleteAsync`, `PtyCompleteOptions`, `PtyResult`
- [specs/capture.md](specs/capture.md) — `MiniPty.Capture` timestamped observation
- [specs/display_text.md](specs/display_text.md) — `PtyOutput.ToDisplayText`
- [specs/lifecycle.md](specs/lifecycle.md) — cancellation, EOF, drain, disposal, failure behavior
- [specs/platform_support.md](specs/platform_support.md) — public platform support and verification constraints
- [references/pty_crossplatform.md](references/pty_crossplatform.md) — ConPTY, `forkpty`, EOF staging, interop details
- [README.md](../../README.md) — quick-start examples
- [scenetake spec_pty.md](https://github.com/guitarrapc/scenetake/blob/main/.github/docs/spec_pty.md) — `pty: true` YAML and cast integration
