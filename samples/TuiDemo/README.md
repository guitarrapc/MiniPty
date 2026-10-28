# TUI demo

A dependency-free full-screen TUI running as a child of MiniPty. The default mode starts another copy of the executable as the TUI child (`--child`), attaches the host keyboard and size with `MiniPty.Console`, and copies PTY output bytes to the host terminal. The child owns the application UI; the user's terminal renders it.

Run from an interactive terminal:

```sh
dotnet run --project samples/TuiDemo -c Release
```

The screen looks like a typical TUI application, after the familiar ratatui/tui-rs demos, and is built from ANSI colors and Unicode characters without image protocols or a TUI framework. It fills the terminal: 72 × 22 is the minimum, and a larger terminal widens the progress bars, the tree, the chart, and the sparklines and adds log lines, tree rows, and chart height. A header bar holds the title, three tabs, and the size; the row under it shows Unicode text and a 24-bit color gradient on every page; a bar on the last row shows how to quit.

- **1 Dashboard:**
  - A Spinners panel with five styles (braille, ASCII, quadrant blocks, dots, a rising bar).
  - A Jobs panel with the elapsed time: three simulated jobs loop through queued, running, done, and failed, each with a spinner or ✓/✗, a progress bar drawn in eighths of a cell, its percent, and a colored state.
  - A Log panel whose new lines enter at the bottom and scroll the older ones up.
  - A modal Details dialog with a shadow.
- **2 Files:** a simulated repository tree (fixed data, not the file system) with guides, folder markers, colored names, and right-aligned sizes, including `日本語メモ.txt` and `café.md`, under a filter box. A Details panel shows the selected item's name, type, size, and path, and a preview: a folder's children or a file's sample lines.
- **3 Charts:** two scrolling signals plotted with braille dots on a y axis, five animated bars in eighth blocks, and three sparklines with their current values. Drag the border between the plot and the bottom panels, or between the bar chart and the Load panel, to resize them.

Terminals differ in how they draw these (24-bit color, braille and block glyphs, wide characters, scroll regions, mouse reporting), so the same screen in Windows Terminal, conhost, VS Code, or the GUI and web terminal samples shows those differences side by side. The WinUI and Avalonia terminal samples take the program to run as arguments, so they can host the TUI directly as `TuiDemo --child`.

- 1–3, Tab and Shift+Tab, or a click on the header switch tabs; each page keeps its state.
- Dashboard: click a job row, use the mouse wheel, or Up/Down (J/K) to select a job. Enter or `[ Details ]` opens the dialog for the selected job, and the animation stops while it is open; Enter or `[ Close ]` closes it.
- Files: Up/Down, PgUp/PgDn, Home/End, and the wheel move; Enter or a click on the selected folder folds it; Right unfolds and Left folds or goes to the parent folder.
- Files filter: `/` or a click on the box starts typing, and the terminal cursor appears in it. The tree shows the items whose names contain the text (ignoring case) with their folders, open. Backspace deletes, Ctrl+U clears, Up/Down still move, and Enter or a click elsewhere stops typing and keeps the filter. Japanese and other wide characters work as typed.
- Charts borders: press on either horizontal border (the plot's bottom or the bottom panels' top) or on the vertical border between the bar chart and the Load panel, move, and release. The border is highlighted while it moves; the plot gets more or fewer dot rows, the bars more eighths and wider slots, and the sparklines more or less history. The bottom panels keep at least 7 rows, the plot 3, the bar chart 30 columns, and the sparklines 8 cells. The split stays when you switch tabs and is clamped again when the terminal shrinks.
- Space, P, or `[ Pause ]` stops the animation on every page; the screen then stops changing. Releasing a button does not activate it again.
- Q or Ctrl+C quits. While the filter takes text, Q and digits are letters, so only Ctrl+C quits; the bottom bar says which applies. Esc is not used: without a timeout it cannot be told apart from the start of a key sequence.
- Resize the terminal and the layout follows. Below 72 columns or 22 rows a compact message replaces the controls; hidden controls cannot be clicked.

Application mouse input requires a terminal that supports VT mouse reporting. The TUI requests button-event tracking (DECSET 1002, which also reports presses and releases) with SGR coordinates (1006), so a held button's motion reaches it for dragging. While reporting is active, terminal text selection often uses Shift+drag (depending on the terminal). Clicking a row selects the application's item; copying text is a frontend feature. On orderly exit the application disables mouse reporting, resets the scroll region, and restores the original screen and cursor.

## Animation and partial updates

The animation clock advances in the same 20 ms input poll loop that watches the size; there are no timers or tasks. One frame is 100 ms, and a job's state is a pure function of the frame, so the renderer and the tests compute the same screen. Pausing or opening the dialog stops the clock.

`DemoRenderer` remembers the displayed state and overwrites only what changed. Identical states emit nothing, and a reusable character buffer batches each update before writing to the terminal.

- **Dashboard:** a tick rewrites the spinner glyphs that changed, the job rows, and the elapsed time. A new log line scrolls the log region with DECSTBM and SU and writes only the new line, instead of redrawing the panel. The renderer keeps at least as many entries as the log has rows, growing with a taller terminal, so a redraw repaints every line the scrolls brought in. Selection rewrites the old and new job rows and the status line.
- **Files:** moving the cursor rewrites the old and new rows and the Details panel; scrolling, folding, or filtering rewrites the tree's rows, and a keystroke also rewrites the filter box. It does not use a scroll region, because DECSTBM scrolls whole rows and would move the Details panel beside the tree. Names and previews are padded by display width, so wide characters keep the size column and the border aligned.
- **Charts:** the plot changes nearly every cell each frame, so the renderer keeps the displayed braille canvas and writes only the span of each row that differs. Bars and sparklines are small and are rewritten on each tick. A dragged border redraws the page in place; the canvas buffers are sized for the tallest plot at the current size, so dragging does not allocate.
- Mouse coordinates update their own field on every page.

Positions follow the terminal size: fixed-size parts (spinners, buttons, the Details panel, the bar chart, the dialog) keep their size, the rest takes the remaining width and height, and the renderer, hit testing, and the smoke compute the same `DemoGeometry`. Every page paints every cell of the page area, so switching tabs, resizing, and closing the dialog rewrite cells in place without clearing. The screen is cleared only on first draw, when switching between the full and compact layout, or when resizing the compact layout (the message may have wrapped). The bottom bar's color is filled with EL rather than by writing the screen's last cell, which some terminals answer by scrolling. Shorter labels explicitly erase their old suffixes. Clearing and rebuilding the entire screen on every tick would expose a blank screen between writes and cause flicker.

Glyphs are limited to box drawing, eighth blocks, braille, and ASCII; folders use `+` and `-` rather than ▸ and ▾, which are East Asian Ambiguous and drawn wide by some terminals. Missing braille or block glyphs in some fonts are part of what the comparison shows.

This is TUI rendering policy. MiniPty transports the cursor-addressing and overwrite sequences as ordinary output bytes; it requires no partial-rendering API. On Windows ConPTY may further transform the stream into its own repaint operations. These checks validate emitted updates and resulting cells rather than promising tear-free rendering in every frontend.

## Automated validation

```sh
dotnet run --project samples/TuiDemo -c Release -- --smoke
```

The smoke mode needs no interactive terminal. It first drives the TUI inside a real PTY, then drives the complete host inside an outer PTY. The second pass also exercises `PtyConsoleInput.Attach`, inherited-cursor replies, keyboard/mouse forwarding, and host-to-child size synchronization. Both passes check:

- Rendered Unicode text, the colored header, the color gradient, the bottom bar, and the panels reaching the right edge and the page bottom.
- That a spinner, a job's percent, and the log change on their own, and that pausing freezes them.
- Key and click selection with row highlighting, button press/release behavior, wheel selection, and clicks outside controls and in compact mode.
- The dialog opening and closing by key and by button, with the covered panels restored.
- Files: switching by key; Up/Down, End, Home, PgDn, PgUp; folding by Enter, Right, Left, and a click; wheel; and the wide-character row's size column lining up with its neighbors'.
- The filter box: typing `日本` (sent as UTF-8), the shown cursor after the wide text, a miss with `q` that does not quit, Backspace, Ctrl+U, an ASCII filter, Enter keeping the filter and hiding the cursor, and a click on the box.
- Charts: switching by Tab; the braille plot and a sparkline changing on their own and freezing while paused; dragging the horizontal border up and the vertical border right with motion reports, highlighted while moving and kept across a tab switch; returning by Shift+Tab and by clicking the dashboard's tab, with each page's state kept.
- Growth to 100 × 30 filling the dashboard and the chart, shrink to the compact layout and recovery, restored screen/cursor/mouse modes, final drained output, and exit code zero.

On Windows the nested pass carries the typed `日本` as win32-input-mode key events (`ESC [ … ; 26085 ; … _`), because the inner ConPTY's `ESC [ ? 9001 h` reaches the host's console. It therefore does not check MiniPty.Console's UTF-8 input code page; the console-input tests do.

Animated cells are checked by waiting until they change, not at fixed frames: the point is that a steady stream of small writes keeps reaching the screen through each path, and a slow CI runner must not fail it. The first pass quits with Q, the second with Ctrl+C. CI runs the smoke under NativeAOT on Linux, Windows, and macOS (x64 and arm64).

The existing emulator in `samples/TerminalEmulator` interprets the output for these assertions. Comparing raw strings is insufficient on Windows: ConPTY can send only changed cells rather than repeating a complete status line. This emulator is only used by smoke mode; interactive mode forwards bytes directly.

`TuiDemoRendererTests` also checks output before the PTY:

- Animation ticks, selection, pause, folding, and filtering do not clear the screen or redraw static text, and no-op draws emit nothing.
- Resizing to 100 × 30 and back stretches the layout in place; the chart uses the extra cells; the bottom bar fills its row without writing its last cell.
- The filter box shows the cursor after wide text, filters the tree, says when nothing matches, and restores the help and the bottom bar.
- A new log entry scrolls the previous one up through the scroll region.
- The dialog closes, and tabs switch and switch back, without clearing and leave the screen as it was, including a 75-row log filled by scrolling.
- A Files cursor move rewrites only its two rows and the details; scrolling keeps the cursor visible and the wide-character row aligned.
- A chart tick writes braille and only the changed cells; a dragged split moves the borders and scales the bars and sparklines without clearing, clamps to the size, and highlights the dragged border.
- Shorter text leaves no stale suffix, and compact-layout transitions restore the screen.

It also checks the simulated jobs and the tree's visible rows, folding, and filtering, and `TuiDemoInputTests` the CSI, SS3, `~`, modified, Tab, and Shift+Tab key forms and the text that printable characters carry.

Publish and run the same checks with NativeAOT (substitute your platform's RID):

```sh
dotnet publish samples/TuiDemo -c Release -r win-x64 --self-contained true -p:PublishAot=true -o samples/TuiDemo/bin/aot
./samples/TuiDemo/bin/aot/TuiDemo.exe --smoke
# Linux/macOS: use linux-x64, linux-arm64, osx-x64, or osx-arm64 and ./.../TuiDemo
```

## Using MiniPty with a TUI

Running interactive TUI programs is an intended PTY use case. MiniPty's scope excludes implementing a TUI framework, rendering terminal screens, and reconstructing a screen from stripped capture text. A terminal frontend (the host terminal here, or an emulator such as xterm.js in a GUI/browser) supplies those terminal semantics.

- Forward raw output continuously with one reader. Do not call `PtyOutput.ToDisplayText` before displaying a TUI: stripping cursor/erase/mode sequences destroys its screen behavior. Do not wait for process exit before reading output.
- Forward keys and terminal replies, and synchronize the actual frontend size. `MiniPty.Console` does this for an interactive host; GUI/browser hosts supply their own input and resize handling. `TERM` must describe the frontend's capabilities on Unix.
- Windows console byte I/O must use UTF-8. MiniPty.Console reads the host console as UTF-8 while attached; this host sets `Console.OutputEncoding` before attaching and restores it after detaching, because output is the embedder's to write. Otherwise a legacy host code page can corrupt Japanese output even when the PTY transports the bytes correctly.
- A text box in a TUI cannot rely on shortcut keys: the same event must carry both the command and its character, and Esc needs a timeout to be told apart from the start of a key sequence, so this sample ends typing with Enter instead.
- Windows `Console.ReadKey` discards native mouse events. This child reads native `INPUT_RECORD` data with mouse/VT input enabled and Quick Edit disabled, and also incrementally decodes SGR mouse and arrow reports. On Unix it feeds terminal characters/keys to the same decoder. Input normalization and hit testing belong to the TUI/frontend.
- On Unix, an isolated two-character `ESC [` fragment can be represented as Alt+`[` by [.NET's key parser](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Console/src/System/IO/KeyParser.cs). The decoder reconstructs that prefix so a split mouse report is not lost.
- The Windows host forwards input through `ReadFile` with VT input enabled. Mixing `ReadConsoleInput` into that host forwarding loop could discard queued mouse records or consume only part of a VT report. The child can use native records because it owns the application input, rather than transporting it to another process.
- With `InheritCursor`, attach before starting display output so the host can answer ConPTY's cursor query. Drain final output before detaching; the TUI restores its colors, cursor, and alternate screen in `finally` on orderly exit. Forced termination of an arbitrary TUI may require frontend cleanup.
- Windows in-box ConPTY interprets/repaints output and has limitations for graphics protocols, some queries, and mouse handling; see the root README's [out-of-band ConPTY guidance](../../README.md#out-of-band-conpty-for-windows). This sample enables VT input before requesting mouse tracking, so the in-box host can forward the mouse modes. It deliberately uses the OS ConPTY and adds no native package dependency. Its smoke checks do not establish compatibility with every TUI, mouse protocol, or terminal extension.

Validation on Windows (2026-10-28): both JIT and NativeAOT passed the direct and nested-host smoke checks. Linux/macOS are covered by the cross-platform CI smoke step; local Windows results do not establish those platforms' results or physical-keyboard behavior in every terminal app.
