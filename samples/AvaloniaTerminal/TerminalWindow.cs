using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MiniPty;
using TerminalEmulator;

namespace AvaloniaTerminal;

/// <summary>
/// The host window: a <see cref="TerminalControl"/> filling the window, a status line, and one MiniPty session
/// behind it. Every MiniPty call in this file runs on the UI thread: <c>Pty.Start</c>, the <c>await foreach</c>
/// over <c>ReadOutputAsync</c>, <c>WriteInputAsync</c>, <c>Resize</c> from size-changed events,
/// <c>WaitForExitAsync</c>, and <c>DisposeAsync</c> on close. None of them blocks it, and no marshaling is needed,
/// because MiniPty's asynchronous members never block the caller and chunks arrive on the awaiting thread's context.
/// </summary>
internal sealed class TerminalWindow : Window
{
    private const string SmokeMarker = "minipty-avalonia-smoke";
    private const string DefaultTitle = "MiniPty Avalonia terminal";

    private readonly IClassicDesktopStyleApplicationLifetime _lifetime;
    private readonly bool _smoke;
    private readonly string[] _command;
    private readonly string? _dumpPath;
    private readonly string? _screenshotPath;
    private readonly TerminalControl _terminal;
    private readonly TextBlock _status;
    private PtySession? _session;
    private string _statusCommand = string.Empty;
    private string _statusState = "starting";
    private bool _exited;
    private readonly TerminalRun _run = new();

    public TerminalWindow(IClassicDesktopStyleApplicationLifetime lifetime, bool smoke, string[] command, string? dumpPath, string? screenshotPath)
    {
        _lifetime = lifetime;
        _smoke = smoke;
        _command = command;
        _dumpPath = dumpPath;
        _screenshotPath = screenshotPath;
        Title = DefaultTitle;
        Width = 1000;
        Height = 620;
        Background = new SolidColorBrush(Color.FromRgb(0x0C, 0x0C, 0x0C));

        _terminal = new TerminalControl(columns: 120, rows: 30);
        _terminal.InputRequested += OnInputRequested;
        _terminal.UserInput += () =>
        {
            // After a failed exit the window stays up to show why; the next keystroke dismisses it.
            if (_exited)
                Close();
        };
        _terminal.GridSizeChanged += (columns, rows) =>
        {
            _session?.Resize(new PtySize(columns, rows));
            UpdateStatus();
        };
        _terminal.TitleChanged += title => Title = title.Length == 0 ? DefaultTitle : title;

        // A status line below the grid says who hosts the PTY. It lives outside the emulator on purpose: the shell's
        // first screen clear would erase a banner written into the grid.
        _status = new TextBlock
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace"),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)),
            Padding = new Thickness(8, 3, 8, 4),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var statusBar = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = _status,
        };

        var layout = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        Grid.SetRow(_terminal, 0);
        Grid.SetRow(statusBar, 1);
        layout.Children.Add(_terminal);
        layout.Children.Add(statusBar);
        Content = layout;
        UpdateStatus();

        _terminal.Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private static readonly string MiniPtyVersion = typeof(Pty).Assembly.GetName().Version?.ToString(3) ?? "?";

    private void UpdateStatus()
    {
        var backend = OperatingSystem.IsWindows() ? "ConPTY" : OperatingSystem.IsMacOS() ? "PTY (posix_spawn)" : "PTY (forkpty)";
        var pid = _session is { } session ? $" · pid {session.ProcessId}" : string.Empty;
        _status.Text = $"MiniPty {MiniPtyVersion} hosting {backend}{pid} · {_terminal.Columns}×{_terminal.Rows} · {_statusCommand} · {_statusState}";
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _terminal.Focus();
        var (columns, rows) = _terminal.GridFor(_terminal.Bounds.Size);
        _terminal.Screen.Resize(columns, rows);

        var startInfo = CreateStartInfo(columns, rows);

        // Spawned, read, and awaited on the UI thread. No Task.Run anywhere in this file.
        _session = Pty.Start(startInfo);
        _statusCommand = startInfo.Arguments.Count == 0 ? startInfo.FileName : startInfo.FileName + " " + string.Join(' ', startInfo.Arguments);
        _statusState = "running";
        UpdateStatus();
        var pump = PumpOutputAsync(_session);
        var exit = _session.WaitForExitAsync();
        if (!await _run.WaitAsync(exit, pump))
            return;
        var status = await exit;

        _exited = true;
        _statusState = $"exited with {status.ExitCode}";
        UpdateStatus();
        Title = $"{DefaultTitle} — exited with {status.ExitCode}";
        if (_dumpPath is not null || _screenshotPath is not null)
        {
            // The status line first, then the screen, so a script can check both.
            if (_dumpPath is not null)
                File.WriteAllText(_dumpPath, _status.Text + Environment.NewLine + _terminal.VisibleText());
            if (_screenshotPath is not null)
                SaveScreenshot(_screenshotPath);
            _lifetime.Shutdown(0);
        }
        else if (_smoke)
        {
            _lifetime.Shutdown(_terminal.VisibleText().Contains(SmokeMarker, StringComparison.Ordinal) ? 0 : 1);
        }
        else if (status.ExitCode == 0)
        {
            // The shell ended normally (`exit`), so the window has nothing left to show.
            Close();
        }
        else
        {
            // A failure is worth reading. The message goes through the emulator like any output, and UserInput
            // closes the window on the next keystroke.
            _terminal.Feed(Encoding.UTF8.GetBytes(
                $"\r\n\u001b[1;7m [process exited with code {status.ExitCode}] \u001b[0m Press any key to close this window.\r\n"));
        }
    }

    private PtyStartInfo CreateStartInfo(int columns, int rows)
    {
        string fileName;
        string[] arguments;
        if (_smoke)
        {
            if (OperatingSystem.IsWindows())
            {
                fileName = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
                arguments = ["/c", "echo " + SmokeMarker];
            }
            else
            {
                fileName = "/bin/sh";
                arguments = ["-c", "echo " + SmokeMarker];
            }
        }
        else if (_command.Length > 0)
        {
            fileName = _command[0];
            arguments = _command[1..];
        }
        else if (OperatingSystem.IsWindows())
        {
            fileName = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
            arguments = [];
        }
        else
        {
            fileName = Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } shell ? shell : "/bin/sh";
            arguments = [];
        }

        return new PtyStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            Size = new PtySize(columns, rows),
            // Programs decide between 16 colors and 24-bit color from this; the control renders both.
            Environment = new Dictionary<string, string?> { ["COLORTERM"] = "truecolor" },
        };
    }

    /// <summary>
    /// Renders the window content (grid and status line) to a PNG through Avalonia's own renderer, so the drawing
    /// can be checked on a machine with no desktop to look at, such as a CI runner under Xvfb.
    /// </summary>
    private void SaveScreenshot(string path)
    {
        if (Content is not Control root || root.Bounds.Width < 1 || root.Bounds.Height < 1)
            return;

        var scale = RenderScaling;
        var pixels = new PixelSize((int)Math.Ceiling(root.Bounds.Width * scale), (int)Math.Ceiling(root.Bounds.Height * scale));
        using var bitmap = new RenderTargetBitmap(pixels, new Vector(96 * scale, 96 * scale));
        bitmap.Render(root);
        bitmap.Save(path, new PngBitmapEncoderOptions());
    }

    /// <summary>
    /// The pull pattern on the UI thread: each chunk is parsed in place while it is valid (until the next
    /// <c>MoveNextAsync</c>), so nothing is copied and nothing is marshaled.
    /// </summary>
    private async Task PumpOutputAsync(PtySession session)
    {
        await foreach (var chunk in session.ReadOutputAsync())
        {
            if (!_run.IsClosing)
                _terminal.Feed(chunk.Data.Span);
        }
    }

    private async void OnInputRequested(string text)
    {
        if (_session is not { } session || session.HasExited)
            return;

        try
        {
            // Never blocks the UI thread, even while the terminal is not taking input.
            await session.WriteInputAsync(text);
        }
        catch (ObjectDisposedException)
        {
            // Closed while a key was in flight.
        }
        catch (IOException)
        {
            // The child closed its input.
        }
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        _run.BeginClose();
        if (_session is { } session)
        {
            _session = null;
            // Ends the child if it is still running, with a hang-up first on Unix. Does not block, and completes once the
            // reader has stopped.
            await session.DisposeAsync();
        }
    }
}
