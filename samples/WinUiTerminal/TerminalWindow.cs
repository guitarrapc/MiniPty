using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MiniPty;
using TerminalEmulator;
using Windows.UI;

namespace WinUiTerminal;

/// <summary>
/// The host window: a <see cref="TerminalControl"/> filling the window, and one MiniPty session behind it. Every
/// MiniPty call in this file runs on the UI thread: <c>Pty.Start</c>, the <c>await foreach</c> over
/// <c>ReadOutputAsync</c>, <c>WriteInputAsync</c>, <c>Resize</c> from size-changed events, <c>WaitForExitAsync</c>,
/// and <c>DisposeAsync</c> on close. None of them blocks it, and no marshaling is needed, because MiniPty's
/// asynchronous members never block the caller and chunks arrive on the awaiting thread's context.
/// </summary>
internal sealed class TerminalWindow : Window
{
    private const string SmokeMarker = "minipty-winui-smoke";

    private readonly bool _smoke;
    private readonly string[] _command;
    private readonly string? _dumpPath;
    private readonly TerminalControl _terminal;
    private readonly TextBlock _status;
    private PtySession? _session;
    private string _statusCommand = string.Empty;
    private string _statusState = "starting";
    private bool _exited;
    private readonly TerminalRun _run = new();

    public TerminalWindow(bool smoke, string[] command, string? dumpPath)
    {
        _smoke = smoke;
        _command = command;
        _dumpPath = dumpPath;
        Title = "MiniPty WinUI terminal";

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
        _terminal.TitleChanged += title => Title = title.Length == 0 ? "MiniPty WinUI terminal" : title;

        // A status line below the grid says who hosts the PTY. It lives outside the emulator on purpose: ConPTY
        // clears the screen when the shell starts, so a banner written into the grid would not survive.
        _status = new TextBlock
        {
            FontFamily = new FontFamily("Cascadia Mono"),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x9A, 0x9A, 0x9A)),
            Padding = new Thickness(8, 3, 8, 4),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var statusBar = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x1C, 0x1C, 0x1C)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x33, 0x33, 0x33)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = _status,
        };

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
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
        var backend = OperatingSystem.IsWindows() ? "ConPTY" : "PTY";
        var pid = _session is { } session ? $" · pid {session.ProcessId}" : string.Empty;
        _status.Text = $"MiniPty {MiniPtyVersion} hosting {backend}{pid} · {_terminal.Columns}×{_terminal.Rows} · {_statusCommand} · {_statusState}";
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var (columns, rows) = _terminal.GridFor(new Windows.Foundation.Size(_terminal.ActualWidth, _terminal.ActualHeight));
        _terminal.Screen.Resize(columns, rows);

        var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
        var startInfo = new PtyStartInfo
        {
            FileName = _smoke ? cmd : _command.Length > 0 ? _command[0] : cmd,
            Arguments = _smoke ? ["/c", "echo " + SmokeMarker] : _command.Length > 1 ? _command[1..] : [],
            Size = new PtySize(columns, rows),
            // Programs decide between 16 colors and 24-bit color from this; the control renders both.
            Environment = new Dictionary<string, string?> { ["COLORTERM"] = "truecolor" },
        };

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
        Title = $"MiniPty WinUI terminal — exited with {status.ExitCode}";
        if (_dumpPath is not null)
        {
            // The status line first, then the screen, so a script can check both.
            File.WriteAllText(_dumpPath, _status.Text + Environment.NewLine + _terminal.VisibleText());
            Close();
        }
        else if (_smoke)
        {
            Environment.ExitCode = _terminal.VisibleText().Contains(SmokeMarker, StringComparison.Ordinal) ? 0 : 1;
            Close();
        }
        else if (status.ExitCode == 0)
        {
            // The shell ended normally (`exit`), so the window has nothing left to show. Same default as Windows
            // Terminal's "graceful" close.
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
            // Never blocks the UI thread, even while ConPTY is not taking input.
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

    private async void OnClosed(object sender, WindowEventArgs args)
    {
        _run.BeginClose();
        _terminal.Dispose();
        if (_session is { } session)
        {
            _session = null;
            // Kills the child if it is still running. Does not block; completes once the reader has stopped.
            await session.DisposeAsync();
        }
    }
}
