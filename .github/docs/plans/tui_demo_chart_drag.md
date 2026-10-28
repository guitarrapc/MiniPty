# TuiDemo Draggable Chart Borders Plan

Status: implemented (2026-10-28); retire this plan once the change is committed. Builds on [tui_demo_layout_input.md](tui_demo_layout_input.md). See [Results](#results).

## Goal

On the Charts tab, drag the border between the plot and the bottom panels up or down, and the border between the bar chart and the Load panel left or right. The charts take the new space; the split is kept across tab switches and resizes, within limits.

## Why

- **Dragging is the mouse input the demo does not exercise yet.** Clicks and the wheel need only press reports. A drag needs button-event tracking (DECSET 1002) and motion and release reports, which terminals, ConPTY, and the nested host pass on differently, so it belongs in the comparison and in the smoke.
- **The charts show it best.** Their content already scales with the space: the braille plot gets more dot rows, the bars more eighths, the sparklines more history.

## Behavior

- Press on either horizontal border row (the plot's bottom border or the bottom panels' top border), move, and release: the bottom panels' top border follows the pointer. The bottom panels keep at least 7 rows and the plot at least 3 dot rows.
- Press on the vertical border (the bar chart's right border, the gap, or the Load panel's left border) between the bottom panels' rows: the gap follows the pointer. The bar chart keeps at least 30 columns and the sparklines at least 8 cells.
- While dragging, the border being moved is highlighted. Release, or a press elsewhere, ends the drag.
- The bar chart's bars grow with its height (eighths per row), and its slots with its width; labels are cut to fit. Extra Load rows stay blank.
- The split lives in the TUI's state, not the renderer, and the geometry clamps it to the current size, so shrinking the terminal never produces an invalid layout.

## Input

- Enable 1002 instead of 1000 (1002 reports presses and releases too), and disable both on exit.
- Decode SGR motion with the left button held (code 32, final `M`) as a drag event, and a left release (final `m`) as a release event. Other motion, other buttons, and modified reports stay ignored. Native Windows records: `MOUSE_MOVED` with the left button down, and a record with no button down after a press.

## Rendering

- A changed split or drag state redraws the Charts page in place, without clearing; ticks keep diffing the plot. Plot buffers are allocated for the tallest plot at the current size, so dragging does not allocate.

## Validation

- **Renderer tests:** a split moves the borders, resizes the plot, bars, and sparklines, clamps to its limits, and highlights the dragged border, without clearing.
- **Input tests:** motion and release decode; other motion and buttons do not.
- **Smoke,** both paths and all CI platforms: drag the horizontal border up and the vertical border right with motion reports, check the highlight during the drag and the new borders after release, and check the split survives a tab switch.
- **Local runs:** Windows and Linux, JIT and NativeAOT.

## Results

- Implemented as planned. The split is `ChartsLayout` in the TUI's state; `ChartsGeometry` clamps it, so the renderer, hit testing, and the smoke share one calculation, and the default split at 72 × 22 equals the earlier fixed layout.
- Local runs: Windows JIT 5/5 and NativeAOT 2/2; Linux (WSL) JIT 5/5, four concurrent runs 4/4, NativeAOT 2/2 (container), and the full test suite. macOS is covered by CI only.

## Open reports from the same review

- **A dialog remnant over the log after closing Details.** Reported on the dashboard. The renderer alone always repaints every covered cell. Through ConPTY at 120 × 40, where the not-yet-full log leaves the covered rows blank, a fragment of the dialog's bottom border stayed once in 21 runs; a targeted close-then-scroll race did not reproduce it. Cause not established.
- **IME composition shown slightly below the filter box.** The terminal cursor is where the smoke expects it (row 4, after the text's display width) through the direct PTY and the nested host; the composition itself is drawn by the terminal's IME. Needs the user's terminal to investigate.
