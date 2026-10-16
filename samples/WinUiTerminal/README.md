# WinUI 3 host sample

A code-only WinUI 3 desktop app that runs `cmd.exe` in a MiniPty session and drives **every** MiniPty call from the UI thread. It exists to show the threading model a GUI host needs; it is not a terminal emulator (escape sequences are stripped and the output is shown as a text log).

What it demonstrates, all on the UI thread and without `Task.Run`:

| Host event | MiniPty call | Why it is safe |
|---|---|---|
| Window loaded | `Pty.Start`, then `await foreach (var chunk in session.ReadOutputAsync())` | `MoveNextAsync` never blocks; each chunk arrives on the UI thread and is decoded in place |
| Enter in the input box | `await session.WriteInputAsync(line + "\r")` | Completes at once when ConPTY takes the bytes, otherwise in the background |
| Output area resized | `session.Resize(new PtySize(cols, rows))` | Dimensions are clamped to the platform limit, and a resize after child exit is a no-op |
| Child exits | `await session.WaitForExitAsync()` | Completes from the exit watcher; no polling, no thread |
| Window closed | `await session.DisposeAsync()` | Kills the child if needed and does not block the UI thread |

Run it (Windows only; the Windows App SDK is a dependency of this sample, not of MiniPty):

```bash
dotnet run --project samples/WinUiTerminal
```

Smoke mode starts `cmd /c echo …` and closes the window once the output has been shown; the exit code is 0 when the output arrived:

```bash
dotnet run --project samples/WinUiTerminal -- --smoke
```

For Windows on ARM pass `-r win-arm64`. NativeAOT is disabled for this sample.
