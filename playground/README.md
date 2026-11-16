# Playground (prototype)

A static page for GitHub Pages. It shows how a terminal works when it uses MiniPty for its PTY. The lede explains a PTY for readers who do not know the word. A PTY is a terminal without a window, where the app types keys and reads the screen. The terminal is on the left, and each call to MiniPty is on the right. The app code is below them, and the line that handles each event lights up when the event occurs. The page does not run a PTY and does not need a server. `Record.cs` recorded all the bytes on a real PTY, and the page replays them.

```
playground/
  Record.cs        runs each scene in a real shell through MiniPty and records each call
  Serve.cs         serves site/ on http://127.0.0.1:5180/ for a local preview
  og.html          the source of site/og.png, the link card on X, Reddit, Zenn, and other sites
  site/            index.html, playground.js, playground.css (xterm.js 5.5.0 from jsDelivr, no build step), og.png
  site/recordings/ written by Record.cs and ignored by git. The Playground workflow makes the published set.
```

## Scenes

| Scene | The user does | MiniPty shows |
|---|---|---|
| Run a command | types `ls` and a Japanese `echo` | one `WriteInputAsync` per key, the echo and output as `ReadOutputAsync` chunks, UTF-8 in both directions |
| Resize the window | runs a full-screen program and resizes the window twice | `Resize`, the redraw of the program, and the repaint of ConPTY on Windows |
| Stop a command | runs `ping` and presses Ctrl+C | 0x03 as input, and the shell that continues to run |
| Type a password | types a password at a hidden prompt | `InputMode` Echo off with line input on (Unix), or null (Windows) |
| Print a large file | prints a 30,000-line file | how the output arrives in chunks |
| Close the tab | closes the tab while a command runs | `DisposeAsync`, and `ExitStatus` 129 (SIGHUP) on Unix or none on Windows |

The page opens on the first scene, ready to play. The page has no menu to choose from first. A scene bar above the terminal shows the scene and its place in the sequence (`1 / 6`). Its arrows and its list move to another scene. On a phone, the list opens the phone's own picker. When a scene ends, the play button moves to the next scene.

"Recorded on" under the terminal selects the OS, as a playback setting. An OS with more than one recording, such as Windows with the in-box and the out-of-band ConPTY, also shows a button for each. The page keeps the last choice for each OS.

In guided mode, the page stops before each user action and shows it as the next step. The play button plays the action at the recorded speed. The bar with the play button is above the terminal. On a 375 × 812 phone, the first screen shows the title, the lede, the scene bar, the play button, and the full terminal. On a laptop with a 1440 × 900 screen, the play button and the terminal are in the first screen too. The scene summary is at the top of the MiniPty column, beside the terminal on a wide screen and under it on a phone.

Pause stops playback at once. Until playback starts again, the code keeps the lines of the latest event in the log lit. So the terminal, the log, and the code show the same moment. A click on a row in the log lights the code for that row. A user action has no code. During playback, the lines light up only for a moment.

The play button replaces its icon and its label only when they change, and its icon and label do not take clicks. Chrome drops a click when the element under the pointer changes between the press and the release. An earlier version replaced the icon and the label on each key. So a Pause click during typing often got lost, and playback continued until the next click.

On a phone, the action has a row of its own under the label and the play button. Beside them, the action had only about 130 px, and most commands did not fit. The row is two lines high for every action, so the terminal does not move from one step to the next. At 320 px, only the Windows password command needs a third line. The longest scene name fits in the scene bar from 360 px. Below 360 px, the scene bar does not show the count (`4 / 6`).

Autoplay plays the full scene without stops. At the end of the scene, it waits 2 seconds and then plays the next scene. During the wait, the play button shows the seconds left and goes to the next scene at once. Autoplay stops after the last scene. Autoplay is off by default. Each scene takes 2 to 8 seconds, so without stops the MiniPty log moves too fast to read. On a phone, the log is under the terminal, and only the terminal would move on the screen.

The terminal window sits on a desk. The desk has the size of the largest terminal in any scene (100 × 28), and it scales to fit its column. A resize moves the edges of the window on the desk, but not the layout. So the terminal, the MiniPty log, and the code keep the same ratio at every page width and in every scene.

The desk does not scale the terminal text below 9.5 px. At that size, 80 columns are wider than a phone. So on a phone, the desk puts the window at its left edge and scrolls sideways inside its frame. The page does not scroll sideways. When the desk fit a 375 px phone, the text was 5.4 px, and nobody could read it.

"Why MiniPty" starts with three measured numbers for 1,000 idle shells. They are 0 threads per shell, 6.4 MiB of host memory, and 0 ms of CPU per second. They come from the Linux rows of the scaling table in the root README's [Performance](../README.md#performance) section. The benchmark workflow writes that table from CI, so update the two together. The numbers are HTML, not an image, so they take the page's dark colors and stack on a phone. The README shows the thread count as a chart (`assets/scaling-threads.svg`). Its line is flat beside a dashed line for a thread per shell, which shows the difference without the name of another library. A chart of memory alone was not used: it had nothing to compare with, and its longest bar made the memory look large.

Below the numbers, four cards show what MiniPty does for an app:

- `InputMode` at a password prompt
- a command's result in one call
- browser terminals that survive a web server restart
- thousands of shells in one process

Each one is a feature that Porta.Pty does not have, but the page names no other library. Porta.Pty holds no thread per session only with `UseAsyncIo`, so the card on thousands of shells claims the default, not the ability. An earlier card named Porta.Pty, and it was too direct. The page does not list the process tree kill, because Porta.Pty also has it. The link in the password item plays that scene from a Linux or macOS recording, because `InputMode` is null on Windows.

"Use MiniPty" shows the package, the samples, and [scenetake](https://github.com/guitarrapc/scenetake) for people who want to record their own CLI. MiniPty does not write recording files. scenetake writes `.cast`, SVG, and MP4 files, and uses MiniPty for its `pty: true` steps.

In the code panel, MiniPty's names are blue. `terminal` stands for the reader's own UI and shows in gray italics, because MiniPty has no `terminal.MarkSecretInput`.

## Link card

`site/og.png` (1200 × 630) is the image for X, Reddit, Zenn, and other sites. The `og:` and `twitter:` tags in `index.html` point to it with an absolute URL, because crawlers do not resolve relative ones. To make it again after a change to `og.html`, run this from the repository root on Windows. The separate profile keeps an open Edge window out of it.

```powershell
& "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe" --headless=new --hide-scrollbars --force-device-scale-factor=1 --window-size=1200,630 --user-data-dir="$env:TEMP\minipty-og" --screenshot="$PWD\playground\site\og.png" "$PWD\playground\og.html"
```

## Record and preview

```bash
dotnet playground/Record.cs
dotnet playground/Serve.cs
```

`Record.cs` starts bash on Unix and Windows PowerShell on Windows. Each shell has a fixed prompt and a small `project` folder, so the recordings contain no path or name from the recording machine. Each shell also has a `fullscreen` command that draws a frame at the edges of the terminal.

On Windows, the folder is `C:\ProgramData\MiniPtyPlayground`. PowerShell's `ls` prints the full path, and the path of the temp folder contains the user name. `Record.cs` sets PSReadLine so that it does not show or save history.

On Windows, `Record.cs` records with both ConPTYs: the in-box one and the out-of-band one from the `Microsoft.Windows.Console.ConPTY` package (`--conpty inbox|oob|both`). `--scene <id>` records one scene.

To add Linux recordings from a Windows machine:

1. Run `Record.cs` with `--out <dir>` in WSL or in a container.
2. Copy the environment directory into `site/recordings/`.
3. Run `dotnet playground/Record.cs --index-only` to rebuild `index.json`.

`--env-id` and `--env-label` set the name of the environment. Without them, `Record.cs` uses the OS and the architecture, for example `linux-x64` and `Linux x64`.

## Deploy

[`playground.yaml`](../.github/workflows/playground.yaml) deploys the page to GitHub Pages in three jobs:

1. `record` runs `Record.cs` on Linux x64, macOS arm64, and Windows x64. On Windows, it records with both ConPTYs.
2. `pages` collects the recordings into `site/recordings/`, runs `--index-only`, and uploads `site/` as the Pages artifact.
3. `deploy` publishes the artifact. It runs only on `main`.

The workflow runs on a push to `main` that changes `playground/` or `src/MiniPty/`, and on demand. A pull request that changes `playground/` runs `record` and `pages` only. Before the first deploy, set Settings > Pages > Source to GitHub Actions.

## Recording format

Only the playground uses `minipty-playground/2`. MiniPty itself defines no recording format ([capture.md](../.github/docs/specs/capture.md)).

- `index.json`: the scenes (title, summary, a note for Unix, and a note for Windows), and each environment with its recorded scenes.
- `<env>/environment.json`: OS, architecture, .NET, MiniPty version, ConPTY kind and version, record date, and the Actions run for a CI recording.
- `<env>/<scene>.json`: the exit status and the events.

Each event time is in milliseconds from just before `Pty.Start`. Event types:

- `step`: a user action (`type`, `key`, `resize`, or `close`) with its text, key, or size. The viewer pauses before it and describes it from those fields.
- `input`: one key that `WriteInputAsync` wrote, as bytes.
- `output`: one chunk from `ReadOutputAsync`, with the time when the recorder received it.
- `resize`.
- `inputMode`: a change of `InputMode`. The recorder reads `InputMode` after each chunk and every 10 ms.
- Lifecycle: `started`, `dispose`, `disposed`, `exit`, `eof`, and `note`.

## Not done yet

- A live mode: a local MiniPty process serves the same page and connects it to a real shell over a same-origin WebSocket.
