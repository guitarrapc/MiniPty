# TuiDemo Tabs Plan

Status: implemented (2026-10-28); retire this plan once the change is committed. Builds on [tui_demo_dashboard.md](tui_demo_dashboard.md). See [Results](#results).

## Goal

Add two pages to [TuiDemo](../../../samples/TuiDemo) behind a tab bar, after the familiar ratatui/tui-rs demos: **Files**, a file tree you can browse and fold, and **Charts**, a braille line chart, a bar chart, and sparklines. The dashboard stays the first page and the smoke's starting point.

## Why

- **Files** exercises what the dashboard does not: a list longer than its viewport (scrolling with the cursor kept visible), tree guides, wide-character names whose columns must still line up, and navigation keys (Left/Right, PgUp/PgDn, Home/End, Tab, Shift+Tab) that arrive as different VT sequences from different terminals.
- **Charts** draws with braille dots (2 × 4 per cell) and eighth blocks, the densest common TUI technique. Font coverage and cell aspect ratio differ most here, which is what TuiDemo is for, and the plot changes nearly every cell each frame, a heavier stream than the dashboard.
- **Rejected: a world map page.** It would draw with the same braille canvas as Charts, so it shows little new, and the maps in tui-rs/ratatui are third-party data that a self-contained repository should not copy.

## Screen

Same fixed 72 × 22 layout and compact message.

| Rows | Content |
|---|---|
| 1 | Header: title, tabs `1 Dashboard`, `2 Files`, `3 Charts` (active one highlighted), size |
| 2 | Unicode sample and 24-bit gradient (all pages) |
| 3–19 | The page; every page paints every cell, so switching never clears the screen |
| 20 | Page status and the mouse position |
| 21 | Page key help |
| 22 | Tab keys and the terminal text-selection hint |

- **Dashboard:** unchanged, except the elapsed time moves from the header into the Jobs panel's title.
- **Files:** a simulated repository tree (deterministic, no file system access) with folder markers, guides, colored names, and right-aligned sizes, including `日本語メモ.txt` and `café.md`. A Details panel shows the selected item's name, type, size, and path, and a preview: the children of a folder or sample lines of a file. Up/Down/PgUp/PgDn/Home/End and the wheel move; Enter or a click on the selected folder folds it; Right expands and Left collapses or goes to the parent.
- **Charts:** two scrolling signals on a braille canvas with a y axis, five animated bars drawn in eighth blocks, and three sparklines with their current value. Everything follows the shared animation clock, so Space pauses it on every page.
- **Tabs:** 1–3, Tab and Shift+Tab, or a click on the header switch pages, except while the dashboard's dialog is open. Each page keeps its state.

## Constraints

- **No flicker,** as before: ticks and navigation rewrite only what changed. The chart canvas is diffed against the displayed one and only changed spans are written. Scrolling the tree rewrites its rows instead of using a scroll region, because DECSTBM would also scroll the Details panel beside it.
- **Glyphs:** braille, eighth blocks, box drawing, and ASCII markers; no emoji and no other new East Asian Ambiguous characters (so no ▸/▾ folder arrows).
- **Widths:** names and preview text are padded by display width, so wide characters keep the size column and the panel border aligned.

## Validation

- **Renderer tests:** switching pages and folding never clear the screen; a cursor move rewrites only the two rows, the details, and the status; scrolling keeps the cursor visible; the wide-character row keeps the size column aligned; a chart tick writes only changed cells and its output contains braille; the tree model's visible list and folding.
- **Input tests:** the new CSI/SS3 sequences (including modified arrows and `~` forms), Tab, Shift+Tab, and page keys, without breaking mouse reports.
- **Smoke,** both paths and all CI platforms: switch by key and by click; browse, scroll, fold, and check the aligned wide-character row on Files; check that the canvas and sparklines change on their own and freeze when paused on Charts; then the existing resize and exit checks.
- **Local runs:** Windows and Linux, JIT and NativeAOT.

## Results

- Implemented as planned; the lessons are in [console.md](../specs/console.md#tui-sample-validation-lessons-2026-10-28) and the [TuiDemo README](../../../samples/TuiDemo/README.md).
- The renderer is split by page (`DemoRenderer.Dashboard.cs`, `.Files.cs`, `.Charts.cs`); the tree and chart data are pure functions of their state and the frame, like the jobs.
- Local runs: Windows JIT 5/5 and NativeAOT 2/2; Linux (WSL) JIT 5/5, four concurrent runs 4/4, NativeAOT 2/2 (container), and the full test suite. A smoke run now takes about 15 s. macOS is covered by CI only.
- Under ConPTY, the Windows child handled every key form the smoke sends (Tab, Shift+Tab, End, PgDn, arrows) without host-side translation. The decoder accepts both VT characters and native key records, so which of the two ConPTY delivered was not determined.
