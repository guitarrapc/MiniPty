# PTY Recording Specification

Status: **Implemented**

## Motivation

Some CLI tools and terminal UIs behave differently when stdout is not a TTY: they disable color, skip animations, or refuse to run. scenetake records demos as asciinema casts; for commands that need a real terminal session, `pty: true` on a step runs the command inside a pseudo-terminal and records the byte stream that would appear on screen.

PTY mode is opt-in so ordinary steps keep predictable pipe-based stdout/stderr capture and declarative highlighting. See [spec_scenario.md](spec_scenario.md) for where `pty` appears in YAML.

## Scope

### In scope (P0 / P1)

| Priority | Goal | Examples |
|---|---|---|
| **P0** | Child process sees a TTY; TUI output is captured | `matrix` / `cmatrix`-style programs |
| **P1** | Single commands run through the scenario shell with TTY semantics | `echo`, `Write-Output`, `[Console]::IsOutputRedirected` checks |

### Out of scope (for now)

- Long-lived interactive sessions (vim, less, REPLs)
- Bidirectional input beyond optional initial stdin bytes
- Remote shells (`ssh`)
- Spilling PTY capture to disk (Phase 1 keeps chunks in memory)

## What `pty: true` Does

When a step has `pty: true`:

1. **No simulated typing** — the cast does not emit the prompt, per-character typing, or a synthetic Enter for that step. (Other steps without `pty` keep the usual typing recording.)
2. **PTY-sized session** — the pseudo-terminal uses the scenario `width` and `height` (same as the cast header).
3. **Shell interpretation** — the `run` string is passed to the scenario `shell` the same way as non-PTY execution (`pwsh -Command`, `cmd /c`, `bash -lc`, etc.). PTY does not bypass the shell.
4. **Terminal stream output** — stdout and stderr are merged into one byte stream. `stderr-color` is ignored for that step.
5. **Timestamped chunks** — while the child runs, scenetake reads the PTY and attaches elapsed seconds to each chunk. After the child exits, those chunks become cast output events using `command_start + chunk_time`.
6. **Raw byte stream** — output is captured as a terminal byte stream. scenetake does not normalize newlines or split on lines; ANSI sequences may span chunk boundaries.
7. **No fallback** — if a PTY cannot be created, the run fails immediately. scenetake does not silently fall back to redirected pipes or simulated typing.

PTY capture records bytes from the child session. Terminal rendering (ANSI parsing, SVG) is a separate layer. See [references/pty_crossplatform.md](references/pty_crossplatform.md).

## Platform Support

| OS | Backend | Minimum |
|---|---|---|
| Windows | ConPTY (`CreatePseudoConsole`) | Windows 10 1809+, Windows 11 |
| Linux | `openpty` + `fork` | Common glibc/musl targets |
| macOS | `openpty` + `fork` | Supported macOS runners (BSD `TIOCSCTTY`, `libutil`) |
| FreeBSD | `openpty` + `fork` | `libutil` + BSD `TIOCSCTTY` (CI coverage TBD) |

Windows does **not** use winpty or third-party PTY libraries. scenetake ships as a NativeAOT binary; the PTY backend is in-process P/Invoke only.

## Failure and Diagnostics

PTY creation or child launch failures abort the scenario run (non-zero exit).

**Always on failure (stderr):**

- Failed step name (e.g. `CreatePseudoConsole`, `CreateProcess`, `openpty`)
- OS error code / message
- Shell identifier and terminal size (`cols` × `rows`)

**With `--verbose`:**

- Resolved executable and arguments
- Working directory
- Child process id (when available)
- Chunk count and total captured bytes on success

Successful runs do not emit PTY diagnostics unless `--verbose` is set.

## Verification

PTY behavior is checked in CI on Linux, macOS, and Windows:

| Layer | What it checks |
|---|---|
| **PTY backend** | `PseudoTerminal.Run` — TTY detection strings, simple command output, multiple chunks |
| **End-to-end** | Fixture scenarios → `.cast` — property assertions (substring presence, chunk/event count, ANSI), not golden cast files |

Fixture scenarios live under `tests/fixtures/` (e.g. `pty-tty-check.yaml`, `pty-cmd.yaml`, `matrix-pwsh-pty.yaml`). Integration tests run against a published binary via the `SCENETAKE_BIN` environment variable (set in CI after `dotnet publish`).

## Cross-Document Notes

- [spec_scenario.md](spec_scenario.md) — `pty` key on steps, terminal dimensions
- [spec_cast.md](spec_cast.md) — how output events are written
- [spec_cli.md](spec_cli.md) — `--verbose`, exit codes
- [spec_highlight.md](spec_highlight.md) — why post-hoc coloring remains the default for non-PTY steps
- [references/pty_crossplatform.md](references/pty_crossplatform.md) — cross-platform PTY implementation design (`PseudoTerminal.cs`)

## Lessons Learned

- **Pipe redirect is not a PTY.** `CreateProcess` with redirected stdin/stdout captures bytes but children report "not a TTY". Tools like `matrix` skip rendering.
- **ConPTY pipes are transport, not the terminal.** On Windows, `HPCON` is the pseudo-console; pipes connect the parent to ConPTY. Passing pipe handles to `STARTF_USESTDHANDLES` without ConPTY does not satisfy TTY checks.
- **ConPTY handle lifetime matters.** The pipe ends handed to `CreatePseudoConsole` must be closed in the parent immediately after creation. Leaving them open, or marking the wrong ends non-inheritable, leads to missing output, hangs, or leaks to the parent console. `CREATE_NEW_CONSOLE` alongside ConPTY is inappropriate.
- **winpty is a poor fit for scenetake.** Bundled `winpty.exe` expects a real console for its own stdin; running from a non-interactive parent fails with `stdin is not a tty`. It also adds environment dependency against the NativeAOT goal.
- **Shell path must match non-PTY semantics.** Direct execution of resolved `.exe` paths bypasses shell builtins and PowerShell cmdlets; P1 requires always going through the scenario shell.
- **Child stdin must not be closed before launch completes (Windows).** Closing the ConPTY input pipe too early yields `STATUS_CONTROL_C_EXIT` (0xC000013A). Pipe close is always deferred to the first `WaitForExitAsync` poll or `CloseTransport` — even after `WriteInput` succeeds. **Unix** uses staged EOT: immediate after `WriteInput`, deferred when there were no bytes.
- **Stdin EOF must be signaled for one-shot input.** After writing bytes, call `SendEof()` before waiting. Windows closes the ConPTY input pipe; Unix writes EOT (`0x04`, Ctrl-D) because PTY master fds cannot be half-closed — EOT is only EOF in canonical terminal mode. Do not close the input pipe via `FileStream` disposal on the write handle; use `WriteFile` and an explicit `SendEof()`.
- **Parent console attachment leaks child output.** When scenetake runs attached to a console, a ConPTY child may duplicate the parent's standard handles unless `STARTUPINFO` sets `STARTF_USESTDHANDLES` with `INVALID_HANDLE_VALUE` for stdin/stdout/stderr. `CREATE_NO_WINDOW` alone was not sufficient in testing; the invalid-handle workaround was required for pipe capture while attached to a console.
- **Fork before exec must stay async-signal-safe.** Prepare `argv` / `cwd` with `NativeMemory` in the parent; the child only calls libc (`dup2`, `execvp`, `_exit`, etc.). Managed allocation or runtime APIs in the child after `fork()` can deadlock or corrupt locks.
- **Capture timing vs cast emission.** Reading the PTY concurrently while the child runs (with per-chunk timestamps) is required for animated TUIs. Batching reads only after exit loses timing; failing to attach ConPTY loses content entirely.
- **Output drain after child exit.** `Complete()` must not block forever on the read task. After `waitpid`, Unix keeps the master open so buffered output can flush naturally; if the read does not finish within `OutputDrainTimeout`, the master is closed and `OutputCloseGrace` is applied before `TimeoutException`. `Dispose()` always closes transport first (same as Windows).
- **Phase 1 memory is sufficient.** P0/P1 outputs (short `matrix` runs, single-shot commands) are bounded; disk spill can wait until a real out-of-memory case appears.
