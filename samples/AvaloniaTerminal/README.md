# Avalonia terminal sample

A code-only Avalonia desktop app (no `.axaml` file; `Main`, the `Application`, the window, and the terminal control are all C#) that runs a shell in a MiniPty session and drives **every** MiniPty call from the UI thread, on Windows, Linux, and macOS. It is the same terminal emulator as the [WinUI sample](../WinUiTerminal): the VT parser, screen buffer, key encoding, mouse reporting, and selection live in [`samples/TerminalEmulator`](../TerminalEmulator) and are compiled into both; this project adds the Avalonia drawing and input layer.

```bash
dotnet run --project samples/AvaloniaTerminal                 # the user's shell ($SHELL, or cmd.exe on Windows)
dotnet run --project samples/AvaloniaTerminal -- pwsh         # any program and arguments
dotnet run --project samples/AvaloniaTerminal -- matrix --duration 0 --mode movie
```

Avalonia is a dependency of this sample, not of MiniPty. NativeAOT is disabled here; the sample is about the UI threading model, not publishing. On Linux it needs a display (an X11 or Wayland session, WSLg, or `xvfb-run` on a headless machine).

## What the terminal supports

The same as the WinUI sample, because the emulator is the same code: see [its table](../WinUiTerminal/README.md#what-the-terminal-supports) for the sequences, modes, and reports. Text is drawn with `DrawingContext.DrawText` and `FormattedText`, one call per run of same-attribute ASCII and one per cell otherwise, so wide characters stay on the grid and glyphs the chosen font lacks come from the platform's fallback fonts. The font is the first installed of Cascadia Mono, Consolas, Menlo, SF Mono, DejaVu Sans Mono, Noto Sans Mono, Liberation Mono. On Linux the fallback fonts are whatever fontconfig knows, so East Asian text needs a CJK font on the machine (for example `fonts-noto-cjk`); without one those cells show as boxes.

## Keys and mouse

| Action | Binding |
|---|---|
| Select | left drag; double-click selects a word |
| Copy | Ctrl+Shift+C, Ctrl+Insert, right-click with a selection, Ctrl+C with a selection |
| Paste | Ctrl+Shift+V, Shift+Insert, Ctrl+V, right-click with no selection (line breaks become CR; wrapped in `ESC[200~` … `ESC[201~` when the program asked for bracketed paste) |
| Interrupt | Ctrl+C with no selection sends `^C`; other Ctrl+letter chords send their control characters |
| Keys | arrows, Home/End, Insert/Delete, PgUp/PgDn, F1–F12 with xterm modifier encoding; Tab/Shift+Tab; Backspace sends DEL (Ctrl+Backspace sends BS); Alt+key sends ESC+key |
| Mouse to the program | when a program enables mouse tracking, clicks, drags, and the wheel are reported to it; hold Shift to select locally instead |

On macOS, Cmd works like Ctrl for the clipboard shortcuts. Printable text arrives through Avalonia's `TextInput` event (so dead keys, AltGr, and IMEs compose before the terminal sees anything) and control keys through `KeyDown`.

## What it demonstrates about MiniPty

All on the UI thread and without `Task.Run`, exactly as in the WinUI sample:

| Host event | MiniPty call | Why it is safe |
|---|---|---|
| Window loaded | `Pty.Start`, then `await foreach (var chunk in session.ReadOutputAsync())` feeding the parser | `MoveNextAsync` never blocks; each chunk arrives on the UI thread and is parsed in place |
| Key, paste, mouse report, terminal reply | `await session.WriteInputAsync(text)` | Completes at once when the PTY takes the bytes, otherwise in the background |
| Window resized | `session.Resize(new PtySize(cols, rows))` | Dimensions are clamped to the platform limit, and a resize after child exit is a no-op |
| Child exits | `await session.WaitForExitAsync()` | Completes from the exit watcher; no polling, no thread |
| Window closed | `await session.DisposeAsync()` | Kills the child if needed and does not block the UI thread |

Nothing in the window is platform-specific: the same `Pty.Start` runs a ConPTY on Windows, a `forkpty` child on Linux, and a `posix_spawn` child on macOS. The child gets `COLORTERM=truecolor`, which is how programs decide to use 24-bit color.

When the program exits with code 0 (`exit` in the shell) the window closes. Any other exit code prints `[process exited with code N]` in the terminal and the window stays until a key is pressed, so the reason can be read.

A status line below the grid shows who hosts the PTY: `MiniPty 1.3.0 hosting PTY (forkpty) · pid 26284 · 118×35 · /bin/bash · running`, updated on resize and on exit.

## Code-only Avalonia

`App.Initialize` does what `App.axaml` would: it adds `FluentTheme` to `Styles` and asks for the dark variant. `Program.Main` is the template's `AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args)`, and the window is created in `OnFrameworkInitializationCompleted`. The terminal control is a plain `Control` with `Focusable = true` that overrides `Render`, `OnKeyDown`, `OnTextInput`, the pointer events, and the focus events; `InvalidateVisual()` after each parsed chunk schedules the repaint, and the screen buffer's synchronized-output mode (`?2026`) holds repaints until the program ends its frame.

## Checks

`--smoke` runs `sh -c 'echo <marker>'` (`cmd /c echo` on Windows) and exits 0 once the marker is on screen; `--dump <file>` writes the status line and then the visible screen as text when the program exits; `--screenshot <file.png>` renders the window (grid and status line) to a PNG at the same moment, through Avalonia's own renderer, so the drawing can be checked without a desktop to look at. The emulator core is covered by `TerminalEmulatorTests` in the test project on every platform.

```bash
dotnet run --project samples/AvaloniaTerminal -- --smoke
dotnet run --project samples/AvaloniaTerminal -- --dump out.txt sh -c 'echo あいう漢字'
dotnet run --project samples/AvaloniaTerminal -- --screenshot out.png sh -c 'printf "\e[1;31mred \e[0;4mline\e[0m\n"'
xvfb-run -a dotnet run --project samples/AvaloniaTerminal -- --smoke   # headless Linux
```
