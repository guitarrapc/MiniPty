# Platform Support Specification

Implemented public platform support, verification constraints, and platform-level lessons.

## Supported Platforms

| OS | Backend | Minimum |
|---|---|---|
| Windows | ConPTY (`CreatePseudoConsole`) | Windows 10 1809+, Windows 11 |
| Linux | `forkpty` + `execve` through native shim | Common glibc/musl targets |
| macOS | `forkpty` + `execve` through native shim | Supported runners with `libutil` |
| FreeBSD | `forkpty` + `execve` through native shim | `libutil` |

Pipe redirect without ConPTY is not a PTY. On Windows, TUI tools require ConPTY-backed spawn.

MiniPty does not use winpty or third-party PTY binaries. Backends are in-process interop plus the small Unix native shim.

## Verification

CI runs `tests/MiniPty.Tests` on Linux, macOS, and Windows.

| Check | Why |
|---|---|
| TTY detection (`redirected=False`, `isatty`) | Confirms real PTY semantics. |
| Simple command output | Confirms spawn and decode. |
| Stdin + EOF (`cat`, `sort`) | Confirms staged EOF. |
| Empty stdin EOF | Confirms deferred EOF with no bytes. |
| Multiple capture chunks + ANSI | Confirms concurrent read and timestamps. |
| `WaitForExitAsync` cancel vs `CompleteAsync` cancel | Confirms cancellation contract. |
| Resize | Confirms `TIOCSWINSZ` / `ResizePseudoConsole`. |

Tests use property assertions, not golden byte captures, to reduce OS and timing flake.

## Platform-specific Test Constraints

These are verification choices, not API requirements, but they document pitfalls that broke CI when ignored.

| OS | Pitfall | Mitigation in tests |
|---|---|---|
| Linux | `bash -lc` on a PTY often stays interactive after `-c` completes. | Prefer `sh -c`, or spawn utilities directly. |
| Linux | A single EOT with no trailing newline does not signal EOF in canonical mode. | One-shot stdin tests using EOT end input with `\n` before `SendEof()`. |
| Windows | Ctrl+Z without a preceding line submit does not signal EOF for line-oriented readers. | `SendEof` submits a pending line with CR when input lacks a trailing `\r`/`\n`, then sends Ctrl+Z + CR; `PtyStdinEof_withoutTrailingNewline` covers Windows. |
| Linux | GNU `stty rows` / `stty columns` without arguments set size instead of printing it. | Query via `stty size`. |
| macOS | Spawn paths that do not attach a controlling terminal make `stty` and resize probes unreliable. | Unix targets use `forkpty` + native `execve`. |
| macOS ARM | Variadic `ioctl` for `TIOCSWINSZ` is unsafe to P/Invoke directly. | Resize runs in `libminipty_unix`. |
| Windows | Closing ConPTY stdin while a child is still attaching can yield `STATUS_CONTROL_C_EXIT`. | Empty-stdin EOF is staged; one-shot writes use Ctrl+Z + CR stream EOF instead of wait-loop pipe close. |
| Windows | Input pipe close after bytes were written yields `STATUS_CONTROL_C_EXIT` for direct stdin readers (`sort`, `more`). | `SendEof` writes Ctrl+Z + CR and keeps the pipe open until exit; tests assert ExitCode 0 for `sort`. |
| Windows | `pwsh` is optional on runners. | Prefer built-in Windows PowerShell unless pwsh-only behavior is needed. |

## Lessons Learned

- **Pipe redirect is not a PTY.** Redirected stdin/stdout captures bytes but children report not-a-TTY.
- **winpty is a poor fit for NativeAOT single-binary goals.** Bundled helpers add environment dependency; in-process ConPTY avoids that.
- **macOS spawn must establish a controlling terminal.** A `posix_openpt` + `posix_spawn` path left the slave without a controlling tty; Unix targets use `forkpty` + native `execve`.
- **Explicit Unix environments require `execve`.** Passing `envp` means MiniPty cannot rely on plain `execvp`; the native shim provides portable path lookup before `execve`.
- **Capture timing requires concurrent reads.** Reading only after exit loses TUI animation timing.
- **PTY output includes terminal echo.** Tests that drive stdin manually may capture echoed input and control characters.
- **Raw PTY text can break the parent console.** Use [Display text](display_text.md) helpers for logs or keep escaped/raw bytes for inspection.
- **Child-visible resize tests must synchronize with the parent.** Block the child until after parent `Resize` when asserting child-visible size.
