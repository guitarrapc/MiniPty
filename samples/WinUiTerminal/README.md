# WinUI 3 terminal

A terminal window for Windows built on WinUI 3. MiniPty runs the shell; the sample adds the terminal emulator and the drawing. It shows that a GUI host can call every MiniPty API on the UI thread: no `Task.Run`, no dispatcher marshaling, and nothing that blocks the window.

```bash
dotnet run --project samples/WinUiTerminal              # cmd.exe
dotnet run --project samples/WinUiTerminal -- pwsh      # any program and its arguments
```

## How the window uses MiniPty

All MiniPty calls are in [TerminalWindow.cs](TerminalWindow.cs). Condensed:

```csharp
// Window loaded: start the shell at the size of the grid that fits the control.
var session = Pty.Start(new PtyStartInfo
{
    FileName = shell,
    Size = new PtySize(columns, rows),
    Environment = new Dictionary<string, string?> { ["COLORTERM"] = "truecolor" },  // programs then use 24-bit color
    ConPty = ConPtyLibrary.TryLoadFromApplicationDirectory(),                          // see "Out-of-band ConPTY" below
});

var pump = PumpOutputAsync(session);
var exit = session.WaitForExitAsync();
await Task.WhenAll(exit, pump);   // the exit code, after the last output has been drawn

async Task PumpOutputAsync(PtySession session)
{
    await foreach (var chunk in session.ReadOutputAsync())
        terminal.Feed(chunk.Data.Span);   // parsed in place on the UI thread
}

// Key, paste, mouse report, or a reply to a terminal query
await session.WriteInputAsync(text);

// Grid size changed
session.Resize(new PtySize(columns, rows));

// Window closed
await session.DisposeAsync();
```

| Host event | MiniPty call | Why it is safe on the UI thread |
|---|---|---|
| Window loaded | `Pty.Start`, then `await foreach` over `ReadOutputAsync` | `MoveNextAsync` never blocks, and each chunk arrives on the UI thread, so the parser needs no lock |
| Key, paste, mouse report, terminal reply | `await session.WriteInputAsync(text)` | Completes at once when ConPTY takes the bytes, otherwise later without holding the thread |
| Grid resized | `session.Resize(new PtySize(cols, rows))` | Clamped to the platform limit; a no-op after the child exits |
| Child exits | `await session.WaitForExitAsync()` | Completes from the exit watcher and holds no thread |
| Window closed | `await session.DisposeAsync()` | Ends the child if it is still running, without blocking |

Points to get right:

- **Read from the start, with one reader.** Start the output loop right after `Pty.Start`. A PTY that nobody reads fills up and stalls the child.
- **Use a chunk before the next iteration.** `chunk.Data` is valid until the loop asks for the next chunk. Parse it in place, or copy it to keep it.
- **Expect a closing window.** A key can still be in flight when the window closes, so `WriteInputAsync` catches `ObjectDisposedException` (session disposed) and `IOException` (the child closed its input). Mark the run as closing before `DisposeAsync`, and treat `ObjectDisposedException` from the pump and the exit wait as expected only then ([TerminalRun.cs](../TerminalEmulator/TerminalRun.cs)).
- **Show the status outside the grid.** ConPTY clears the screen when the shell starts, so a banner written into the terminal would be erased. The sample puts the PTY host, PID, size, and state in a status line below the grid.

## Out-of-band ConPTY

A terminal emulator on Windows should run on the ConPTY that Windows Terminal ships rather than the in-box one. The in-box ConPTY withholds graphics, some terminal queries, and mouse tracking from some programs (it forwards mouse tracking only to a program that reads VT input). The project opts in with one package:

```xml
<PackageReference Include="Microsoft.Windows.Console.ConPTY" Version="1.25.260930003" />
```

MiniPty's build targets copy `OpenConsole.exe` beside `conpty.dll`, and `PtyStartInfo.ConPty = ConPtyLibrary.TryLoadFromApplicationDirectory()` selects it. When `conpty.dll` is missing, `TryLoadFromApplicationDirectory` returns null and the session runs on the in-box ConPTY; the status line says which one is in use. See [Out-of-band ConPTY for Windows](../../README.md#out-of-band-conpty-for-windows).

## What the sample adds on top

MiniPty moves bytes and does not interpret them. The VT parser, screen buffer, key and mouse encoding, selection, and paste handling are in [samples/TerminalEmulator](../TerminalEmulator), shared with the [Avalonia sample](../AvaloniaTerminal). They cover what full-screen programs need: 256 and 24-bit color, the alternate screen, scroll regions, mouse tracking, bracketed paste, and synchronized output. [TerminalControl.cs](TerminalControl.cs) draws the grid with Win2D and turns WinUI input events into the text passed to `WriteInputAsync`.

## Notes

- Windows only. The same emulator runs on Windows, Linux, and macOS in [samples/AvaloniaTerminal](../AvaloniaTerminal).
- The Windows App SDK, Win2D, and the ConPTY package are dependencies of this sample, not of MiniPty. NativeAOT is off: the sample is about the UI threading model.
- The app has no XAML file. A code-only WinUI 3 app needs three things that `App.xaml` normally provides; [Program.cs](Program.cs) and [WinUiTerminal.csproj](WinUiTerminal.csproj) comment each one.
- `--smoke` runs a one-line command and exits 0 once its output is on screen; CI runs it.
