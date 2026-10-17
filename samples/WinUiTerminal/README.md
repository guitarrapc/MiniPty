# WinUI 3 terminal sample

A code-only WinUI 3 desktop app (no XAML file; `Main`, the `Application`, the window, and the terminal control are all C#) that runs a shell in a MiniPty session and drives **every** MiniPty call from the UI thread. It is a small but real terminal emulator: a VT parser and screen buffer behind a Win2D-drawn grid, so full-screen programs such as `matrix`, editors, and colored CLI output render as they would in Windows Terminal.

```bash
dotnet run --project samples/WinUiTerminal              # cmd.exe
dotnet run --project samples/WinUiTerminal -- pwsh      # any program and arguments
dotnet run --project samples/WinUiTerminal -- matrix --duration 0 --mode movie
```

Windows only. The Windows App SDK and Win2D are dependencies of this sample, not of MiniPty. NativeAOT is disabled here; the sample is about the UI threading model, not publishing.

## What the terminal supports

| Area | Supported |
|---|---|
| Text | UTF-8 with sequences split across chunks, East Asian wide characters (two cells), combining marks, DEC line-drawing charset |
| Colors and styles | 16 ANSI colors (Campbell scheme), 256-color palette, 24-bit color (`38;2` and `38:2::` forms), bold (bright), dim, italic, underline (`4:n` styles drawn plain), inverse, hidden, strikethrough |
| Cursor and editing | CUU/CUD/CUF/CUB, CUP/HVP, CHA, VPA, CNL/CPL, CHT/CBT, ED/EL, ICH/DCH/ECH, IL/DL, SU/SD, REP, DECSTBM scroll region, DECSC/DECRC, tab stops (HTS/TBC) |
| Modes | DECCKM application cursor keys, DECOM, DECAWM with deferred wrap, insert mode, DECTCEM cursor visibility, alternate screen (47/1047/1049), bracketed paste (2004), focus events (1004), synchronized output (2026), mouse tracking (9/1000/1002/1003) with SGR encoding (1006) |
| Reports | DSR cursor position and status, DA1/DA2; window title from OSC 0/2 |
| Scrollback | 5,000 lines on the main screen; mouse wheel scrolls, any input or output snaps back to the bottom; in a full-screen program the wheel sends arrow keys |

Not a goal: a scrollbar, cursor shapes and blinking, double-width lines, Sixel or Kitty graphics, Win32 input mode (`9001`), reflow on resize (the PTY repaints instead), or an IME candidate window placed at the cursor.

## Keys and mouse

| Action | Binding |
|---|---|
| Select | left drag; double-click selects a word |
| Copy | Ctrl+Shift+C, Ctrl+Insert, right-click with a selection, Ctrl+C with a selection |
| Paste | Ctrl+Shift+V, Shift+Insert, Ctrl+V, right-click with no selection (line breaks become CR; wrapped in `ESC[200~` … `ESC[201~` when the program asked for bracketed paste) |
| Interrupt | Ctrl+C with no selection sends `^C`; other Ctrl+letter chords send their control characters |
| Keys | arrows, Home/End, Insert/Delete, PgUp/PgDn, F1–F12 with xterm modifier encoding; Tab/Shift+Tab; Backspace sends DEL (Ctrl+Backspace sends BS); Alt+key sends ESC+key |
| Mouse to the program | when a program enables mouse tracking, clicks, drags, and the wheel are reported to it; hold Shift to select locally instead |

## What it demonstrates about MiniPty

All on the UI thread and without `Task.Run`:

| Host event | MiniPty call | Why it is safe |
|---|---|---|
| Window loaded | `Pty.Start`, then `await foreach (var chunk in session.ReadOutputAsync())` feeding the parser | `MoveNextAsync` never blocks; each chunk arrives on the UI thread and is parsed in place |
| Key, paste, mouse report, terminal reply | `await session.WriteInputAsync(text)` | Completes at once when ConPTY takes the bytes, otherwise in the background |
| Window resized | `session.Resize(new PtySize(cols, rows))` | Dimensions are clamped to the platform limit, and a resize after child exit is a no-op |
| Child exits | `await session.WaitForExitAsync()` | Completes from the exit watcher; no polling, no thread |
| Window closed | `await session.DisposeAsync()` | Kills the child if needed and does not block the UI thread |

The child gets `COLORTERM=truecolor`, which is how programs decide to use 24-bit color.

When the program exits with code 0 (`exit` in the shell) the window closes, as Windows Terminal does by default. Any other exit code prints `[process exited with code N]` in the terminal and the window stays until a key is pressed, so the reason can be read.

A status line below the grid shows who hosts the PTY: `MiniPty 1.3.0 hosting ConPTY · pid 12345 · 182×46 · C:\WINDOWS\system32\cmd.exe · running`, updated on resize and on exit. It sits outside the emulator because ConPTY clears the screen when the shell starts, so a banner written into the grid would not survive.

## Code-only WinUI 3 pitfalls

Three details crash the process with `0xC000027B` before any window appears. Two are in `Program.cs`: the `Application` must implement `IXamlMetadataProvider` (delegating to `XamlControlsXamlMetaDataProvider`), and `XamlControlsResources` must be merged in `OnLaunched`, not in the constructor. The third is in the project file: `EnableMsixTooling` must be `true`, or `dotnet publish` leaves out the project's PRI file and `new XamlControlsResources()` cannot find `Microsoft.UI.Xaml/Themes/themeresources.xaml` in the published app (`dotnet build` output is unaffected).

## Checks

`--smoke` runs `cmd /c echo <marker>` and exits 0 once the marker is on screen; `--dump <file>` writes the status line and then the visible screen as text when the program exits. The VT parser and screen buffer are also covered by `WinUiTerminalVtTests` in the test project, which compiles these sources directly.

```bash
dotnet run --project samples/WinUiTerminal -- --smoke
dotnet run --project samples/WinUiTerminal -- --dump out.txt cmd /c "echo あいう漢字"
```
