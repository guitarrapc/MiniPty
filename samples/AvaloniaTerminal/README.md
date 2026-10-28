# Avalonia terminal

A cross-platform terminal window built on Avalonia, for Windows, Linux, and macOS. MiniPty runs the shell; the sample adds the terminal emulator and the drawing. The same MiniPty code runs on every OS, and every call is made on the UI thread: no `Task.Run`, no dispatcher marshaling.

```bash
dotnet run --project samples/AvaloniaTerminal              # the user's shell ($SHELL, or cmd.exe on Windows)
dotnet run --project samples/AvaloniaTerminal -- pwsh      # any program and its arguments
```

On Linux it needs a display: an X11 or Wayland session, WSLg, or `xvfb-run` on a headless machine.

## How the window uses MiniPty

All MiniPty calls are in [TerminalWindow.cs](TerminalWindow.cs). The pattern is the same as the [WinUI sample](../WinUiTerminal/README.md#how-the-window-uses-minipty): start, read on the UI thread, write input, resize, dispose.

```csharp
// Window loaded: one PtyStartInfo for every OS.
var session = Pty.Start(new PtyStartInfo
{
    FileName = OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"
                                           : Environment.GetEnvironmentVariable("SHELL") ?? "/bin/sh",
    Size = new PtySize(columns, rows),
    Environment = new Dictionary<string, string?> { ["COLORTERM"] = "truecolor" },
    ConPty = ConPtyLibrary.TryLoadFromApplicationDirectory(),   // Windows: out-of-band ConPTY; null elsewhere, and ignored
});

var pump = PumpOutputAsync(session);
var exit = session.WaitForExitAsync();
await Task.WhenAll(exit, pump);

async Task PumpOutputAsync(PtySession session)
{
    await foreach (var chunk in session.ReadOutputAsync())
        terminal.Feed(chunk.Data.Span);   // parsed in place, on the UI thread
}

await session.WriteInputAsync(text);           // KeyDown and TextInput, paste, mouse reports, terminal replies
session.Resize(new PtySize(columns, rows));    // the control's grid size changed
await session.DisposeAsync();                  // window closed
```

What differs by OS is only what MiniPty does underneath:

| OS | `Pty.Start` creates | `DisposeAsync` on a running child |
|---|---|---|
| Windows | a ConPTY session (out-of-band when `conpty.dll` is beside the app) | terminates it |
| Linux | a `forkpty` child | hangs it up (`SIGHUP`, so the shell can save its history), then kills it if it is still running a second later |
| macOS | a child started through `posix_spawn` and `minipty_spawn_helper` | same as Linux |

Points to get right:

- **Read from the start, with one reader**, and use each chunk before the next iteration (copy it to keep it).
- **Reference the out-of-band ConPTY on Windows only.** The project adds the package under a condition, so Linux and macOS builds stay free of it:

  ```xml
  <PackageReference Include="Microsoft.Windows.Console.ConPTY" Version="1.25.260930003"
                    Condition="$([MSBuild]::IsOSPlatform('Windows'))" />
  ```

  MiniPty's build targets copy `OpenConsole.exe` beside `conpty.dll`. Without them the session uses the in-box ConPTY, which withholds graphics, some terminal queries, and mouse tracking from some programs ([details](../../README.md#out-of-band-conpty-for-windows)).
- **Expect a closing window.** Catch `ObjectDisposedException` and `IOException` from `WriteInputAsync`, mark the run as closing before `DisposeAsync`, and treat disposal exceptions as expected only then ([TerminalRun.cs](../TerminalEmulator/TerminalRun.cs)).

## What the sample adds on top

MiniPty moves bytes and does not interpret them. The VT parser, screen buffer, key and mouse encoding, selection, and paste handling are in [samples/TerminalEmulator](../TerminalEmulator), shared with the WinUI sample. [TerminalControl.cs](TerminalControl.cs) draws the grid with `DrawingContext.DrawText` and maps Avalonia's key, text-input, and pointer events onto it. It also gives Avalonia an input method client, which turns on the platform IME for Japanese, Chinese, and Korean input.

## Notes

- Avalonia and the ConPTY package are dependencies of this sample, not of MiniPty. NativeAOT is off: the sample is about the UI threading model.
- East Asian text on Linux needs a CJK font that fontconfig can find (for example `fonts-noto-cjk`).
- `--smoke` runs a one-line command and exits 0 once its output is on screen; CI runs it on every OS, under `xvfb-run` on Linux. `--screenshot <file.png>` renders the window to a PNG when the program exits, for checking the drawing without a desktop.
