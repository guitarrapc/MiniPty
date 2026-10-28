# TUI demo

A dependency-free full-screen TUI running as a child of MiniPty. The default mode starts another copy of the executable as the TUI child, attaches the host keyboard and size with `MiniPty.Console`, and copies PTY output bytes to the host terminal. The child owns the application UI; the user's terminal renders it.

Run from an interactive terminal:

```sh
dotnet run --project samples/TuiDemo -c Release
```

The screen contains three clickable boxes joined by arrows, colored bars, and a small landscape made of background-colored cells. These use ANSI colors and Unicode characters, without image protocols or a TUI framework.

- Click a diagram box to select it; the selected box changes color.
- Click `[Toggle]` to switch the value. Releasing the button does not activate it again.
- Use the mouse wheel or Up/Down (J/K) to change selection; Enter/Space toggles, Q or Ctrl+C quits.
- Resize the terminal to see the size update. Below 72 columns or 22 rows a compact message replaces the controls; hidden controls cannot be clicked.

Application mouse input requires a terminal that supports VT mouse reporting. While reporting is active, terminal text selection often uses Shift+drag (depending on the terminal). Clicking a box selects the application's item; copying text is a frontend feature. On orderly exit the application disables mouse reporting and restores the original screen and cursor.

## Partial updates

`DemoRenderer` remembers the displayed state. Selection changes overwrite the old and new boxes plus the status line; toggles update the status line; mouse coordinates update their own line. The landscape, bars, arrows, and help text stay on screen. Identical states emit nothing. A reusable character buffer batches each update before writing to the terminal.

The diagram has fixed cell positions. Resizing while it fits only updates the size label. The screen is cleared on first draw, when switching between the diagram and compact layout, or when resizing the compact layout (the message may have wrapped). Shorter labels explicitly erase their old suffixes. Clearing and rebuilding the entire screen on every interaction would expose a blank screen between writes and cause flicker.

This is TUI rendering policy. MiniPty transports the cursor-addressing and overwrite sequences as ordinary output bytes; it requires no partial-rendering API. On Windows ConPTY may further transform the stream into its own repaint operations. These checks validate emitted updates and resulting cells rather than promising tear-free rendering in every frontend.

## Automated validation

```sh
dotnet run --project samples/TuiDemo -c Release -- --smoke
```

The smoke mode needs no interactive terminal. It first drives the TUI inside a real PTY, then drives the complete host inside an outer PTY. The second pass also exercises `PtyConsoleInput.Attach`, inherited-cursor replies, keyboard/mouse forwarding, and host-to-child size synchronization. Both passes check rendered Unicode text, distinct landscape colors, click-driven box highlighting, button press/release behavior, wheel selection, clicks outside controls and in compact mode, growth/shrink/recovery, restored screen/cursor/mouse modes, final drained output, and exit code zero. The first pass quits with Q, the second with Ctrl+C.

The existing emulator in `samples/TerminalEmulator` interprets the output for these assertions. Comparing raw strings is insufficient on Windows: ConPTY can send only changed cells rather than repeating a complete status line. This emulator is only used by smoke mode; interactive mode forwards bytes directly.

`TuiDemoRendererTests` also checks partial-update output before the PTY: ordinary interactions and normal resizing do not clear the screen or redraw static artwork, no-op draws emit nothing, shorter text leaves no stale suffix, and compact-layout transitions restore the screen. Selection updates must emit less than half the initial frame's UTF-8 bytes.

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
- Windows console byte I/O must use UTF-8. This host sets `Console.InputEncoding` and `Console.OutputEncoding` before attaching, and restores them after detaching. Otherwise a legacy host code page can corrupt Japanese output even when the PTY transports the bytes correctly.
- Windows `Console.ReadKey` discards native mouse events. This child reads native `INPUT_RECORD` data with mouse/VT input enabled and Quick Edit disabled, and also incrementally decodes SGR mouse and arrow reports. On Unix it feeds terminal characters/keys to the same decoder. Input normalization and hit testing belong to the TUI/frontend.
- On Unix, an isolated two-character `ESC [` fragment can be represented as Alt+`[` by [.NET's key parser](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Console/src/System/IO/KeyParser.cs). The decoder reconstructs that prefix so a split mouse report is not lost.
- The Windows host forwards input through `ReadFile` with VT input enabled. Mixing `ReadConsoleInput` into that host forwarding loop could discard queued mouse records or consume only part of a VT report. The child can use native records because it owns the application input, rather than transporting it to another process.
- With `InheritCursor`, attach before starting display output so the host can answer ConPTY's cursor query. Drain final output before detaching; the TUI restores its colors, cursor, and alternate screen in `finally` on orderly exit. Forced termination of an arbitrary TUI may require frontend cleanup.
- Windows in-box ConPTY interprets/repaints output and has limitations for graphics protocols, some queries, and mouse handling; see the root README's [out-of-band ConPTY guidance](../../README.md#out-of-band-conpty-for-windows). This sample enables VT input before requesting mouse tracking, so the in-box host can forward the mouse modes. It deliberately uses the OS ConPTY and adds no native package dependency. Its smoke checks do not establish compatibility with every TUI, mouse protocol, or terminal extension.

Validation on Windows (2026-10-28): both JIT and NativeAOT passed the direct and nested-host smoke checks. Linux/macOS are covered by the cross-platform CI smoke step; local Windows results do not establish those platforms' results or physical-keyboard behavior in every terminal app.
