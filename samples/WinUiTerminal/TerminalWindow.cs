using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MiniPty;
using Windows.Foundation;
using Windows.System;

namespace WinUiTerminal;

/// <summary>
/// The host window. Every MiniPty call in this file runs on the UI thread: <c>Pty.Start</c>, the <c>await foreach</c>
/// over <c>ReadOutputAsync</c>, <c>WriteInputAsync</c>, <c>Resize</c> from size-changed events, <c>WaitForExitAsync</c>,
/// and <c>DisposeAsync</c> on close. None of them blocks it, and no marshaling is needed, because MiniPty's asynchronous
/// members never block the caller and chunks arrive on the awaiting thread's context. Rendering is a plain text log,
/// not a terminal emulator: escape sequences are stripped for display; a real terminal control would feed the raw
/// bytes to a VT parser instead.
/// </summary>
internal sealed class TerminalWindow : Window
{
    private const string SmokeMarker = "minipty-winui-smoke";

    private readonly bool _smoke;
    private readonly TextBlock _output;
    private readonly ScrollViewer _scroll;
    private readonly TextBox _input;
    private readonly StringBuilder _text = new();
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly char[] _chars = new char[PtyChunkChars];
    private string _heldEscape = string.Empty;
    private Size _cell = new(8, 16);
    private PtySession? _session;
    private bool _smokeSawMarker;

    // Output chunks are at most a few KiB; a decoder never produces more chars than bytes.
    private const int PtyChunkChars = 16 * 1024;

    public TerminalWindow(bool smoke)
    {
        _smoke = smoke;
        Title = "MiniPty WinUI sample";

        var font = new FontFamily("Cascadia Mono, Consolas");
        _output = new TextBlock
        {
            FontFamily = font,
            FontSize = 14,
            TextWrapping = TextWrapping.NoWrap,
            IsTextSelectionEnabled = true,
            Margin = new Thickness(8),
        };
        _scroll = new ScrollViewer
        {
            Content = _output,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        _input = new TextBox
        {
            FontFamily = font,
            PlaceholderText = "Type a command and press Enter",
            Margin = new Thickness(8),
        };
        _input.KeyDown += OnInputKeyDown;

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_scroll, 0);
        Grid.SetRow(_input, 1);
        grid.Children.Add(_scroll);
        grid.Children.Add(_input);
        Content = grid;

        _scroll.SizeChanged += OnOutputSizeChanged;
        _scroll.Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        MeasureCell();
        var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
        var startInfo = new PtyStartInfo
        {
            FileName = cmd,
            Arguments = _smoke ? ["/c", "echo " + SmokeMarker] : [],
            Size = CurrentGridSize(),
        };

        // Spawned, read, and awaited on the UI thread. No Task.Run anywhere in this file.
        _session = Pty.Start(startInfo);
        var pump = PumpOutputAsync(_session);
        var status = await _session.WaitForExitAsync();
        await pump;

        Title = $"MiniPty WinUI sample — exited with {status.ExitCode}";
        _input.IsEnabled = false;
        if (_smoke)
        {
            Environment.ExitCode = _smokeSawMarker ? 0 : 1;
            Close();
        }
    }

    /// <summary>
    /// The pull pattern on the UI thread: each chunk is decoded in place while it is valid (until the next
    /// <c>MoveNextAsync</c>), so nothing is copied and nothing is marshaled.
    /// </summary>
    private async Task PumpOutputAsync(PtySession session)
    {
        await foreach (var chunk in session.ReadOutputAsync())
        {
            var count = _decoder.GetChars(chunk.Data.Span, _chars, flush: false);
            Append(_chars.AsSpan(0, count));
        }

        // Flush a trailing partial UTF-8 sequence and any held-back escape prefix.
        var tail = _decoder.GetChars([], _chars, flush: true);
        Append(_chars.AsSpan(0, tail));
        if (_heldEscape.Length > 0)
            AppendDisplay(_heldEscape);
    }

    private void Append(ReadOnlySpan<char> decoded)
    {
        if (decoded.IsEmpty && _heldEscape.Length == 0)
            return;

        // An escape sequence can be split across chunks; stripping a half sequence leaves its tail on screen.
        // Hold the incomplete tail back and prepend it to the next chunk.
        var text = _heldEscape.Length == 0 ? decoded.ToString() : _heldEscape + decoded.ToString();
        var cut = IncompleteEscapeStart(text);
        _heldEscape = cut < 0 ? string.Empty : text[cut..];
        AppendDisplay(cut < 0 ? text : text[..cut]);
    }

    private void AppendDisplay(string raw)
    {
        var display = PtyOutput.ToDisplayText(raw, PtyOutputDisplayMode.PlainText);
        if (display.Length == 0)
            return;

        if (_smoke && display.Contains(SmokeMarker, StringComparison.Ordinal))
            _smokeSawMarker = true;

        _text.Append(display);
        _output.Text = _text.ToString();
        _scroll.UpdateLayout();
        _scroll.ChangeView(null, _scroll.ScrollableHeight, null, disableAnimation: true);
    }

    /// <summary>
    /// Index of the last ESC whose sequence has not ended within <paramref name="text"/>, or -1. CSI ends with a
    /// byte in 0x40–0x7E, OSC with BEL or ESC \, and any other two-character escape with its second character.
    /// </summary>
    private static int IncompleteEscapeStart(string text)
    {
        var start = text.LastIndexOf('\u001b');
        if (start < 0)
            return -1;

        if (start == text.Length - 1)
            return start;

        var kind = text[start + 1];
        if (kind == '[')
        {
            for (var i = start + 2; i < text.Length; i++)
            {
                if (text[i] is >= '\u0040' and <= '\u007e')
                    return -1;
            }

            return start;
        }

        if (kind == ']')
        {
            for (var i = start + 2; i < text.Length; i++)
            {
                if (text[i] == '\u0007' || (text[i] == '\\' && text[i - 1] == '\u001b'))
                    return -1;
            }

            return start;
        }

        return -1;
    }

    private async void OnInputKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || _session is null)
            return;

        e.Handled = true;
        var line = _input.Text;
        _input.Text = string.Empty;
        // ConPTY submits a line on CR. Never blocks the UI thread, even while ConPTY is not taking input.
        await _session.WriteInputAsync(line + "\r");
    }

    /// <summary>Safe at any time: Resize is a no-op once the child has exited and the transport has closed.</summary>
    private void OnOutputSizeChanged(object sender, SizeChangedEventArgs e) => _session?.Resize(CurrentGridSize());

    private PtySize CurrentGridSize()
    {
        var width = Math.Max(0, _scroll.ActualWidth - 16);
        var height = Math.Max(0, _scroll.ActualHeight - 16);
        // Zero before layout is fine: MiniPty clamps each dimension to at least 1 and at most the platform limit.
        return new PtySize((int)(width / _cell.Width), (int)(height / _cell.Height));
    }

    private void MeasureCell()
    {
        var probe = new TextBlock { FontFamily = _output.FontFamily, FontSize = _output.FontSize, Text = "M" };
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        if (probe.DesiredSize.Width > 0 && probe.DesiredSize.Height > 0)
            _cell = probe.DesiredSize;
    }

    private async void OnClosed(object sender, WindowEventArgs args)
    {
        if (_session is { } session)
        {
            _session = null;
            // Kills the child if it is still running. Does not block; completes once the reader has stopped.
            await session.DisposeAsync();
        }
    }
}
