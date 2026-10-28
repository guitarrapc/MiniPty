# TUI demo

Run a full-screen TUI program inside a MiniPty session and show it in the terminal you are using. The host is the `DemoHost` class in [Program.cs](Program.cs): MiniPty starts the TUI in a PTY, MiniPty.Console forwards your keyboard, mouse, and window size to it, and the host copies the PTY's output bytes to stdout unchanged. Your terminal does the rendering.

```sh
dotnet run --project samples/TuiDemo -c Release    # from an interactive terminal; 1-3 or Tab switch pages, Q or Ctrl+C quits
```

The TUI itself (a dashboard, a file tree with a filter box, braille charts) is a dependency-free child program that knows nothing about MiniPty. The same executable runs as that child with `--child`. It exists to give the PTY realistic traffic: 24-bit color, Unicode and wide characters, mouse reporting with drags, scroll regions, resize, and continuous animation.

## The host

```csharp
// Windows: the console must decode the PTY's UTF-8 output. Restore the previous encoding afterwards.
Console.OutputEncoding = Encoding.UTF8;

await using var session = Pty.Start(new PtyStartInfo
{
    FileName = tuiExecutable,
    Arguments = ["--child"],
    Size = new PtySize(80, 24),    // replaced by the host terminal's size when MiniPty.Console attaches
    InheritCursor = true,          // Windows: keep what the terminal already shows (ignored on Unix)
});

// Keys, mouse reports, terminal replies, and window resizes now go to the TUI.
using (PtyConsoleInput.Attach(session))
{
    var output = CopyOutputAsync(session);
    var status = await session.WaitForExitAsync();
    await output;                  // drain the TUI's last bytes before the host terminal is restored
    return status.ExitCode;
}

static async Task CopyOutputAsync(PtySession session)
{
    using var stdout = Console.OpenStandardOutput();
    try
    {
        await foreach (var chunk in session.ReadOutputAsync())
        {
            await stdout.WriteAsync(chunk.Data);   // raw bytes: UTF-8 and VT sequences may be split across chunks
            await stdout.FlushAsync();
        }
    }
    catch
    {
        session.Kill();            // a host that cannot display must not wait for a child nobody can see
        throw;
    }
}
```

## Using MiniPty with a TUI

Running interactive TUI programs is an intended PTY use case. MiniPty transports the bytes; the terminal frontend (your terminal here, or an emulator such as xterm.js or the [GUI samples](../WinUiTerminal)) interprets them. MiniPty does not render screens or implement a TUI framework.

- **Pass output through untouched.** A TUI is cursor moves, erases, and mode switches. Write `chunk.Data` as it is; do not run it through `PtyOutput.ToDisplayText`, which strips exactly those sequences. A sequence split across chunks is fine, because the terminal reassembles it.
- **Read from the start, with one reader.** Start copying output before waiting for exit. A PTY that nobody reads fills up and stalls the TUI.
- **Let MiniPty.Console own the input.** `Attach` puts the host terminal in raw mode, forwards keys, mouse reports, and replies to terminal queries, and keeps the PTY at the window's size. Do not read the console yourself while attached (`Console.ReadKey`, `ReadConsoleInput`): it would take input meant for the TUI, including parts of mouse reports. A GUI or browser host supplies its own input and resize handling instead.
- **UTF-8 on Windows.** MiniPty.Console reads the host console as UTF-8 while attached. Output is yours to write, so set `Console.OutputEncoding = Encoding.UTF8` before attaching and restore it after detaching; with a legacy code page, Japanese output is corrupted even though the PTY delivered every byte.
- **With `InheritCursor`, attach first.** Without it, ConPTY clears the screen when the child starts. With it, ConPTY first asks the terminal for the cursor position and waits for the answer. `Attach` turns on the host console's VT processing, so the terminal sees that query and answers it, and forwards the answer to ConPTY. So call `Attach` before any output is displayed, then start copying right away.
- **Drain, then detach.** Await the output copy after the exit and before disposing the attach handle, so the TUI's final bytes (leave the alternate screen, show the cursor, stop mouse reporting) reach the terminal before the host restores its own modes. A TUI that is killed cannot restore them; the frontend has to.
- **`TERM` on Unix.** It defaults to `xterm-256color`. Set `PtyStartInfo.TerminalName` when the host terminal supports less (or more).
- **Windows in-box ConPTY.** It reinterprets and repaints output, drops graphics protocols and some queries, and forwards mouse tracking only to a program that reads VT input; this TUI enables VT input before it requests mouse tracking. This sample stays on the in-box ConPTY to keep zero native dependencies. Programs that need more should run on the [out-of-band ConPTY](../../README.md#out-of-band-conpty-for-windows).

## Other frontends

The child runs in any terminal, so it also shows how other frontends draw the same screen. The [WinUI](../WinUiTerminal) and [Avalonia](../AvaloniaTerminal) samples take the program to run as arguments and can host it directly (`... -- <path-to>/TuiDemo --child`).

## Checks

```sh
dotnet run --project samples/TuiDemo -c Release -- --smoke
dotnet publish samples/TuiDemo -c Release -r win-x64 --self-contained true -p:PublishAot=true -o samples/TuiDemo/bin/aot   # any RID
```

`--smoke` needs no interactive terminal. It drives the TUI inside a PTY, then the whole host (with `PtyConsoleInput.Attach`, the inherited cursor, input forwarding, and size sync) inside an outer PTY, and checks the interpreted screen through [samples/TerminalEmulator](../TerminalEmulator). CI runs it under NativeAOT on Linux, Windows, and macOS. What building and testing this sample taught is in [console.md](../../.github/docs/specs/console.md#tui-sample-validation-lessons-2026-10-28).
