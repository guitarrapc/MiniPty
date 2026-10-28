# TuiDemo Stretching Layout, Filter Box, and Bottom Bar Plan

Status: implemented (2026-10-28); retire this plan once the change is committed. Builds on [tui_demo_tabs.md](tui_demo_tabs.md). See [Results](#results).

## Goal

Three changes to [TuiDemo](../../../samples/TuiDemo):

1. **Fill the terminal.** The layout stretches with the terminal instead of staying at 72 × 22 in the top-left corner. 72 × 22 stays the minimum; smaller terminals keep the compact message.
2. **A text input.** The Files tab gets a filter box: `/` starts typing, and the tree shows the items whose names contain the text, with their folders.
3. **Quit keys in a bottom bar.** Q and Ctrl+C are shown on a bar on the last row, on every page.

## Why

- **Stretching** is what TUI applications do, and it makes resizing exercise the whole screen instead of one label. Wider panels give the braille chart, progress bars, and the log more room, and taller ones show more tree rows and log lines.
- **The filter box** is the first place where the TUI takes text rather than keys. Typing Japanese sends wide characters through the direct PTY and the nested host. It also shows the cursor, which every other part of the screen keeps hidden. (The plan expected the nested pass to check MiniPty.Console's UTF-8 input code page; it cannot, see [Results](#results).)
- **The bottom bar** answers "how do I leave", which the page help rows only half answered (Q, not Ctrl+C), and the Files help row was already full at 72 columns.

## Layout

| Rows | Content |
|---|---|
| 1 | Header (title, tabs, size at the right edge) |
| 2 | Unicode sample and a gradient to the right edge |
| 3 … H−3 | The page |
| H−2 | Status and mouse position |
| H−1 | Page key help |
| H | Bottom bar: Q/Ctrl+C, tab keys, text selection |

Fixed-size parts keep their size (spinners, buttons, the Details panel, the bar chart, the dialog); the rest grows:

- **Dashboard:** job progress bars widen with the width; the log gains a line per row of height.
- **Files:** the tree widens and gains rows; the Details preview gains lines.
- **Charts:** the braille plot widens and heightens; sparklines lengthen.
- **Dialog:** centered horizontally.

Resizing redraws the whole screen in place without clearing; clearing stays limited to the switch to and from the compact layout.

## Filter box

- Row 4 of the Files tree panel. Without a filter it shows a hint; with one, the text.
- `/` or a click on the box starts typing. Printable characters (including wide ones) are added, Backspace deletes, Ctrl+U clears, Up/Down still move the selection, and Enter stops typing and keeps the filter. A click elsewhere also stops typing.
- While typing, Q, J/K, digits, and Space are text, so only Ctrl+C quits; the help row and the bottom bar say so.
- Esc is not used: without a timeout it cannot be told apart from the start of a key sequence.
- Matching is a case-insensitive substring of the name; matches are shown with their ancestor folders, open, regardless of folding. With no match the tree is empty and says so.
- The terminal cursor is shown in the box only while typing.

## Constraints

- Partial updates as before: typing rewrites the box, the tree rows, the details, and the status; arrays sized by the layout are reallocated only on resize.
- The bottom bar's color is filled with EL instead of writing the last cell of the last row, which some terminals scroll on.
- Hit testing and the smoke compute positions from the same size-dependent layout as the renderer.

## Validation

- **Renderer tests:** stretched layout at a larger size (bar width, log lines, plot width, right edge); resizing redraws without clearing; filtering, empty results, typing, the shown cursor, and the editing help and bottom bar.
- **Input tests:** printable characters carry their text, Backspace and Ctrl+U decode, and `q` is still Quit outside the box.
- **Smoke,** both paths and every CI platform: the bottom bar; typing `日本`, a miss, Backspace, Ctrl+U, and an ASCII filter through the direct PTY and the nested host; Enter keeps the filter; the cursor shows only while typing; stretched rows and right edges after resizing to 100 × 30.
- **Local runs:** Windows and Linux, JIT and NativeAOT.

## Results

- Implemented as planned. Positions that depend on the size live in `DemoGeometry`; at 72 × 22 every value equals the old fixed layout, so most renderer tests kept their rows and columns. The lessons are in [console.md](../specs/console.md#tui-sample-validation-lessons-2026-10-28), [windows_console_input.md](../references/windows_console_input.md#input-path), and the [TuiDemo README](../../../samples/TuiDemo/README.md).
- **The nested smoke cannot check the input code page.** With the setting disabled it still passed. The inner ConPTY's `ESC[?9001h` reaches the host console through the output copy, the host's `ReadFile` then returns win32-input-mode events (`ESC[0;0;26085;1;0;1_` for `日`), and no code page is involved. A ConPTY session alone still returns Shift-JIS `93 FA` for `日` under 932, so the setting is right for input before that sequence and for consoles that ignore the mode; the real-console test `WindowsHostTerminal_ReadInput_EncodesNonAsciiAsUtf8AndRestoresInputCodePage` is what checks it. The sample host no longer sets `Console.InputEncoding`, which had masked the library's behavior.
- A cleared result moves the cursor to the first row, where it stays as items come back; the first smoke draft expected the old row and failed until that was understood. An Alt+numpad guard added on a wrong guess was removed after logging the child's key records showed plain character records.
- Local runs: Windows JIT 5/5 and NativeAOT 2/2; Linux (WSL) JIT 5/5, four concurrent runs 4/4, NativeAOT 2/2 (container). A smoke run takes about 15 s. In the Linux full suite, two core UI-thread tests that this change does not touch failed once under load and passed 5/5 each when run alone. macOS is covered by CI only.
