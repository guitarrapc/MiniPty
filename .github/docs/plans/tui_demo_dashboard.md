# TuiDemo Dashboard Plan

Status: implemented (2026-10-28); retire this plan once the change is committed. See [Results](#results).

## Goal

Turn [TuiDemo](../../../samples/TuiDemo) from a static diagram into a screen that looks and behaves like a typical TUI application: spinners, progress bars, colored panels, a scrolling log, and a modal dialog. The same screen keeps serving as the cross-platform smoke for MiniPty and MiniPty.Console in CI.

## Why

- **Terminals differ.** The same TUI renders differently in Windows Terminal, conhost, VS Code, the WinUI/Avalonia emulators, and xterm.js. A screen that uses common TUI techniques makes those differences visible at a glance, which is more useful than the current 16-color diagram.
- **Continuous output is untested.** The current screen only changes on input. Animation adds a steady stream of small cursor-addressed writes, the traffic a real TUI produces, through both the direct PTY and the nested `MiniPty.Console` host.
- **CI already runs it everywhere.** The TUI smoke step runs under NativeAOT on all six matrix entries (Linux, Windows, macOS × x64/arm64). Extending it extends that coverage without new CI plumbing.
- **Rejected: a Shell/TUI switch in the GUI and web terminal samples.** The TUI child must stay a separate console executable (the GUI samples are `WinExe`), the nested-host smoke must stay, and switching sessions would bury those samples' main point. The WinUI and Avalonia samples already take the program to run as arguments, so they can host `TuiDemo --child` without a switch.

## Screen

Fixed layout in the existing 72 × 22 minimum; smaller terminals keep the compact message.

| Rows | Content |
|---|---|
| 1 | Header bar with background color: title, elapsed time, size |
| 2 | Unicode sample (`日本語 / café`) and a 24-bit color gradient |
| 3–10 | Spinners panel (braille, ASCII, quadrant, dots, bar) and Jobs panel (three jobs with spinner or ✓/✗, partial-block progress bar, percent, colored state; Pause and Details buttons) |
| 11–19 | Log panel; new entries enter at the bottom through a scroll region (DECSTBM + SU) |
| 20 | Status: selection, paused state, last mouse position |
| 21–22 | Key help bar and the terminal text-selection hint |

- Jobs advance from an animation clock (one frame per 100 ms) and loop forever: queued, running, done or failed, then the next run. Job state is a pure function of the frame, so tests and the renderer agree without shared mutable state.
- Space or the Pause button stops the clock; the screen then emits nothing.
- Enter or the Details button opens a modal dialog with a shadow for the selected job. The clock stops while it is open; Enter or Close closes it and the screen is redrawn without clearing.
- Selection works as before: click a job row, wheel, Up/Down, J/K. Q and Ctrl+C quit and restore the terminal.

## Constraints

- **No flicker.** Keep the renderer's partial-update policy: a tick rewrites only the cells that changed (spinner glyphs, bars, percent, elapsed time, new log lines). Full redraws happen only on first draw, layout transitions, and dialog close; only the first two clear the screen.
- **No timers or tasks.** The existing 20 ms input poll loop also advances the clock.
- **Glyph widths.** No emoji; no new East Asian Ambiguous glyphs beyond the box-drawing and block characters already used. Missing glyphs in some fonts (braille, eighth blocks) are acceptable and are part of the comparison; the smoke interprets cells, not pixels.
- **Restore everything on exit,** including the scroll region.

## Validation

- **Renderer tests** (before the PTY): animation ticks, selection, pause, and normal resize never clear the screen or redraw static text; an unchanged view emits nothing; a log entry scrolls the previous one up; opening and closing the dialog restores the panels.
- **Smoke** (direct PTY and nested host, every CI platform): static structure and colors; the spinner cell, percent, and log change on their own within a timeout (no fixed frames, which would hide the streaming being tested); pause freezes them; keys, clicks, wheel, buttons, dialog, resize, compact mode, and exit cleanup.
- **Local runs** before finishing: Windows JIT and NativeAOT, Linux JIT and NativeAOT. macOS is left to CI.

## Done

TuiDemo shows the dashboard, the renderer and input tests and the smoke cover it, README and the related specs describe it, and this plan is retired with its lessons moved into those documents.

## Results

- Implemented as planned. The lessons are in [console.md](../specs/console.md#tui-sample-validation-lessons-2026-10-28) and the [TuiDemo README](../../../samples/TuiDemo/README.md).
- Local runs: Windows JIT 5/5 and NativeAOT 2/2; Linux (WSL) JIT 5/5, four concurrent runs under contention 4/4, and NativeAOT 2/2 (container). Each smoke takes about 10 s. macOS is covered by CI only.
- The simulated jobs' frames make the first screen deterministic: Build starts at frame 0 and Test at frame 15, so a log scroll is visible within 1.5 s, which keeps the "changes on its own" checks fast.
