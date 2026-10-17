using System.Text;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Text;

namespace WinUiTerminal.Terminal;

/// <summary>
/// A terminal view: feeds PTY bytes to a <see cref="VtParser"/>, draws the <see cref="ScreenBuffer"/> with Win2D,
/// and turns keyboard, IME text, mouse, and clipboard actions into the bytes a program expects. It knows nothing
/// about MiniPty: bytes come in through <see cref="Feed"/> and go out through <see cref="InputRequested"/>, and the
/// window owning the session wires the two.
/// </summary>
/// <remarks>
/// Mouse: drag selects by cell (double-click selects a word), Ctrl+Shift+C / Ctrl+Insert copy, Ctrl+Shift+V /
/// Shift+Insert / Ctrl+V paste, Ctrl+C copies when a selection exists and otherwise interrupts, and a right click
/// copies the selection if there is one or pastes if there is none. When the program has enabled mouse tracking,
/// clicks are reported to it instead, and Shift forces local selection. The wheel scrolls the scrollback, or sends
/// arrow keys to a full-screen program.
/// </remarks>
internal sealed class TerminalControl : Grid, IDisposable
{
    private const string FontFamily = "Cascadia Mono";
    private const float FontSize = 14f;
    private const int WheelLinesPerNotch = 3;

    private readonly CanvasControl _canvas;
    private readonly ScreenBuffer _screen;
    private readonly VtParser _parser;
    private readonly CanvasTextFormat[] _formats = new CanvasTextFormat[4];
    private readonly StringBuilder _runText = new();
    private float _cellWidth;
    private float _cellHeight;
    private bool _focused;
    private bool _disposed;

    // Selection in absolute line coordinates, so it stays put while the view scrolls.
    private (int Row, int Column) _selectionAnchor;
    private (int Row, int Column) _selectionFocus;
    private bool _hasSelection;
    private bool _selecting;
    private bool _mouseButtonDown;
    private (int Row, int Column) _lastReportedCell = (-1, -1);
    private DateTime _lastPressTime;
    private (int Row, int Column) _lastPressCell;

    public TerminalControl(int columns, int rows)
    {
        _screen = new ScreenBuffer(columns, rows);
        _parser = new VtParser(_screen);
        _screen.Response += text => InputRequested?.Invoke(text);
        _screen.TitleChanged += title => TitleChanged?.Invoke(title);

        for (var i = 0; i < _formats.Length; i++)
        {
            _formats[i] = new CanvasTextFormat
            {
                FontFamily = FontFamily,
                FontSize = FontSize,
                FontWeight = (i & 1) != 0 ? FontWeights.Bold : FontWeights.Normal,
                FontStyle = (i & 2) != 0 ? FontStyle.Italic : FontStyle.Normal,
                WordWrapping = CanvasWordWrapping.NoWrap,
                HorizontalAlignment = CanvasHorizontalAlignment.Left,
                VerticalAlignment = CanvasVerticalAlignment.Top,
            };
        }

        MeasureCell();

        _canvas = new CanvasControl();
        _canvas.Draw += OnDraw;
        Children.Add(_canvas);

        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(ToColor(Palette.DefaultBackground));
        IsTabStop = true;
        UseSystemFocusVisuals = false;
        AllowFocusOnInteraction = true;
        TabFocusNavigation = KeyboardNavigationMode.Once;

        SizeChanged += OnSizeChanged;
        Loaded += (_, _) => Focus(FocusState.Programmatic);
        GotFocus += OnGotFocus;
        LostFocus += OnLostFocus;
        PreviewKeyDown += OnPreviewKeyDown;
        CharacterReceived += OnCharacterReceived;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCanceled += OnPointerReleased;
        PointerCaptureLost += OnPointerReleased;
        PointerWheelChanged += OnPointerWheelChanged;
    }

    /// <summary>Text to send to the program: keystrokes, pasted text, mouse reports, and terminal replies.</summary>
    public event Action<string>? InputRequested;

    /// <summary>The grid now fits a different number of cells; the PTY should be resized to match.</summary>
    public event Action<int, int>? GridSizeChanged;

    public event Action<string>? TitleChanged;

    public ScreenBuffer Screen => _screen;

    public int Columns => _screen.Columns;

    public int Rows => _screen.Rows;

    /// <summary>Output from the PTY. Call on the UI thread.</summary>
    public void Feed(ReadOnlySpan<byte> bytes)
    {
        _parser.Feed(bytes);
        if (_screen.Dirty && !_screen.SynchronizedOutput)
            _canvas.Invalidate();
    }

    /// <summary>The visible screen as text, one line per row, for smoke tests.</summary>
    public string VisibleText()
    {
        var text = new StringBuilder();
        for (var row = 0; row < _screen.Rows; row++)
            text.AppendLine(_screen.ViewLine(row).ToText());
        return text.ToString();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        // Win2D holds device resources that outlive the XAML tree unless the control is removed explicitly.
        _canvas.RemoveFromVisualTree();
        Children.Remove(_canvas);
    }

    // ---------------------------------------------------------------- Layout

    private void MeasureCell()
    {
        using var layout = new CanvasTextLayout(CanvasDevice.GetSharedDevice(), "M", _formats[0], 0, 0);
        _cellWidth = (float)Math.Ceiling(layout.LayoutBounds.Width * 100) / 100;
        _cellHeight = (float)Math.Ceiling(layout.LayoutBounds.Height);
    }

    /// <summary>Cells that fit in <paramref name="size"/> DIPs, at least 2×1.</summary>
    public (int Columns, int Rows) GridFor(Size size) =>
        (Math.Max(2, (int)(size.Width / _cellWidth)), Math.Max(1, (int)(size.Height / _cellHeight)));

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var (columns, rows) = GridFor(e.NewSize);
        if (columns == _screen.Columns && rows == _screen.Rows)
            return;

        _screen.Resize(columns, rows);
        ClearSelection();
        GridSizeChanged?.Invoke(columns, rows);
        _canvas.Invalidate();
    }

    // ---------------------------------------------------------------- Drawing

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var session = args.DrawingSession;
        session.Antialiasing = CanvasAntialiasing.Antialiased;
        session.TextAntialiasing = CanvasTextAntialiasing.ClearType;
        session.Clear(ToColor(Palette.DefaultBackground));
        _screen.Dirty = false;

        var (selectionStart, selectionEnd) = NormalizedSelection();
        var rows = _screen.Rows;
        var columns = _screen.Columns;
        for (var row = 0; row < rows; row++)
        {
            var line = _screen.ViewLine(row);
            var cells = line.Cells;
            var y = row * _cellHeight;
            var absoluteRow = _screen.ViewRowToAbsolute(row);

            // Backgrounds first, merged over runs of the same color, so glyph overhang never covers a neighbor.
            var runStart = 0;
            while (runStart < columns)
            {
                var background = BackgroundOf(cells[runStart]);
                var runEnd = runStart + 1;
                while (runEnd < columns && BackgroundOf(cells[runEnd]) == background)
                    runEnd++;

                if (background != Palette.DefaultBackground)
                    session.FillRectangle(runStart * _cellWidth, y, (runEnd - runStart) * _cellWidth, _cellHeight, ToColor(background));

                runStart = runEnd;
            }

            // Text: ASCII runs with identical attributes go out in one call; anything else is placed per cell so
            // fallback fonts and wide glyphs stay on the grid.
            var column = 0;
            while (column < columns)
            {
                var cell = cells[column];
                if (cell.IsContinuation || cell.Text is null)
                {
                    column++;
                    continue;
                }

                var attributes = cell.Attributes;
                var format = _formats[(attributes.Has(CellFlags.Bold) ? 1 : 0) | (attributes.Has(CellFlags.Italic) ? 2 : 0)];
                var foreground = ToColor(ForegroundOf(cell), attributes.Has(CellFlags.Dim) ? 0.55f : 1f);
                var x = column * _cellWidth;

                var end = column + 1;
                if (cell.Width == 1 && IsPlainAscii(cell.Text))
                {
                    _runText.Clear();
                    _runText.Append(cell.Text);
                    while (end < columns)
                    {
                        var next = cells[end];
                        if (next.IsContinuation || next.Width != 1 || !(next.Text is null || IsPlainAscii(next.Text)) || next.Attributes != attributes)
                            break;

                        _runText.Append(next.Text ?? " ");
                        end++;
                    }

                    if (!attributes.Has(CellFlags.Hidden))
                        session.DrawText(_runText.ToString(), x, y, foreground, format);
                }
                else if (!attributes.Has(CellFlags.Hidden))
                {
                    session.DrawText(cell.Text, x, y, foreground, format);
                    end = column + Math.Max(1, (int)cell.Width);
                }

                if (attributes.Has(CellFlags.Underline))
                    session.DrawLine(x, y + _cellHeight - 1.5f, end * _cellWidth, y + _cellHeight - 1.5f, foreground, 1f);
                if (attributes.Has(CellFlags.Strikethrough))
                    session.DrawLine(x, y + _cellHeight / 2, end * _cellWidth, y + _cellHeight / 2, foreground, 1f);

                column = end;
            }

            // Selection overlay.
            if (_hasSelection && absoluteRow >= selectionStart.Row && absoluteRow <= selectionEnd.Row)
            {
                var from = absoluteRow == selectionStart.Row ? selectionStart.Column : 0;
                var to = absoluteRow == selectionEnd.Row ? selectionEnd.Column + 1 : columns;
                if (to > from)
                    session.FillRectangle(from * _cellWidth, y, (to - from) * _cellWidth, _cellHeight, Color.FromArgb(0x60, 0x80, 0xB0, 0xFF));
            }
        }

        // Cursor: a solid block with focus, a hollow one without; hidden while scrolled back.
        if (_screen.CursorVisible && _screen.ScrollOffset == 0)
        {
            var cursorCell = _screen.ViewLine(_screen.CursorRow).Cells[_screen.CursorColumn];
            var width = Math.Max(1, (int)cursorCell.Width) * _cellWidth;
            var x = _screen.CursorColumn * _cellWidth;
            var y = _screen.CursorRow * _cellHeight;
            if (_focused)
            {
                session.FillRectangle(x, y, width, _cellHeight, ToColor(Palette.DefaultForeground));
                if (cursorCell.Text is not null)
                {
                    var format = _formats[(cursorCell.Attributes.Has(CellFlags.Bold) ? 1 : 0) | (cursorCell.Attributes.Has(CellFlags.Italic) ? 2 : 0)];
                    session.DrawText(cursorCell.Text, x, y, ToColor(Palette.DefaultBackground), format);
                }
            }
            else
            {
                session.DrawRectangle(x + 0.5f, y + 0.5f, width - 1, _cellHeight - 1, ToColor(Palette.DefaultForeground), 1f);
            }
        }
    }

    private static bool IsPlainAscii(string text) => text.Length == 1 && text[0] is >= ' ' and < '\u007f';

    private static uint ForegroundOf(in Cell cell)
    {
        var attributes = cell.Attributes;
        var bold = attributes.Has(CellFlags.Bold);
        return attributes.Has(CellFlags.Inverse)
            ? Palette.Resolve(attributes.Background, isForeground: false, bold)
            : Palette.Resolve(attributes.Foreground, isForeground: true, bold);
    }

    private static uint BackgroundOf(in Cell cell)
    {
        var attributes = cell.Attributes;
        var bold = attributes.Has(CellFlags.Bold);
        return attributes.Has(CellFlags.Inverse)
            ? Palette.Resolve(attributes.Foreground, isForeground: true, bold)
            : Palette.Resolve(attributes.Background, isForeground: false, bold);
    }

    private static Color ToColor(uint rgb, float alpha = 1f) =>
        Color.FromArgb((byte)(alpha * 255), (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    // ---------------------------------------------------------------- Keyboard

    private void OnGotFocus(object sender, RoutedEventArgs e)
    {
        _focused = true;
        if (_screen.FocusEvents)
            Send("\u001b[I");
        _canvas.Invalidate();
    }

    private void OnLostFocus(object sender, RoutedEventArgs e)
    {
        _focused = false;
        if (_screen.FocusEvents)
            Send("\u001b[O");
        _canvas.Invalidate();
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var shift = IsDown(VirtualKey.Shift);
        var ctrl = IsDown(VirtualKey.Control);
        var alt = IsDown(VirtualKey.Menu);
        // AltGr arrives as Ctrl+Alt and produces ordinary text through CharacterReceived.
        if (ctrl && alt)
            return;

        if (ctrl && shift && e.Key is VirtualKey.C or VirtualKey.V)
        {
            if (e.Key == VirtualKey.C)
                CopySelection();
            else
                _ = PasteAsync();
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Insert && (ctrl || shift) && !(ctrl && shift))
        {
            if (ctrl)
                CopySelection();
            else
                _ = PasteAsync();
            e.Handled = true;
            return;
        }

        if (ctrl && !shift && e.Key == VirtualKey.V)
        {
            _ = PasteAsync();
            e.Handled = true;
            return;
        }

        if (ctrl && !shift && e.Key == VirtualKey.C && _hasSelection)
        {
            CopySelection();
            e.Handled = true;
            return;
        }

        var sequence = KeyEncoder.Encode(e.Key, shift, ctrl, alt, _screen.ApplicationCursorKeys);
        if (sequence is null)
            return;

        Send(sequence);
        e.Handled = true;
    }

    private void OnCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        var c = e.Character;
        // Control characters were already sent from the key event; so were Ctrl chords (except AltGr text).
        if (c < ' ' || c == '\u007f')
            return;
        if (IsDown(VirtualKey.Control) && !IsDown(VirtualKey.Menu))
            return;

        e.Handled = true;
        Send(c.ToString());
    }

    private void Send(string text)
    {
        _screen.ScrollViewToBottom();
        InputRequested?.Invoke(text);
        if (_screen.Dirty)
            _canvas.Invalidate();
    }

    // ---------------------------------------------------------------- Clipboard

    private void CopySelection()
    {
        var text = SelectedText();
        if (text.Length == 0)
            return;

        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
        ClearSelection();
        _canvas.Invalidate();
    }

    private async Task PasteAsync()
    {
        string text;
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text))
                return;

            text = await content.GetTextAsync();
        }
        catch (Exception)
        {
            // Another process holds the clipboard; a paste that does nothing is the right outcome.
            return;
        }

        if (string.IsNullOrEmpty(text))
            return;

        // Terminals receive CR for a line break. Bracketed paste lets the program tell a paste from typing.
        text = text.Replace("\r\n", "\r").Replace('\n', '\r');
        if (_screen.BracketedPaste)
            text = "\u001b[200~" + text + "\u001b[201~";

        Send(text);
    }

    // ---------------------------------------------------------------- Mouse

    private (int Row, int Column) CellAt(Point position)
    {
        var column = Math.Clamp((int)(position.X / _cellWidth), 0, _screen.Columns - 1);
        var row = Math.Clamp((int)(position.Y / _cellHeight), 0, _screen.Rows - 1);
        return (row, column);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Pointer);
        var point = e.GetCurrentPoint(this);
        var (row, column) = CellAt(point.Position);
        var shift = IsDown(VirtualKey.Shift);
        e.Handled = true;

        if (_screen.Mouse != MouseTracking.None && !shift)
        {
            var button = point.Properties.IsLeftButtonPressed ? 0 : point.Properties.IsMiddleButtonPressed ? 1 : 2;
            _mouseButtonDown = true;
            _lastReportedCell = (row, column);
            ReportMouse(button, row, column, release: false, motion: false);
            return;
        }

        if (point.Properties.IsRightButtonPressed)
        {
            // Windows Terminal's convention: right click copies a selection, or pastes when there is none.
            if (_hasSelection)
                CopySelection();
            else
                _ = PasteAsync();
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
            return;

        var absoluteRow = _screen.ViewRowToAbsolute(row);
        var now = DateTime.UtcNow;
        var doubleClick = (now - _lastPressTime).TotalMilliseconds < 500 && _lastPressCell == (absoluteRow, column);
        _lastPressTime = now;
        _lastPressCell = (absoluteRow, column);

        if (doubleClick)
        {
            SelectWord(absoluteRow, column);
            _selecting = false;
        }
        else
        {
            _selectionAnchor = (absoluteRow, column);
            _selectionFocus = _selectionAnchor;
            _hasSelection = false;
            _selecting = true;
            CapturePointer(e.Pointer);
        }

        _canvas.Invalidate();
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        var (row, column) = CellAt(point.Position);

        if (_selecting)
        {
            var focus = (_screen.ViewRowToAbsolute(row), column);
            if (focus != _selectionFocus)
            {
                _selectionFocus = focus;
                _hasSelection = true;
                _canvas.Invalidate();
            }

            e.Handled = true;
            return;
        }

        if (_screen.Mouse is MouseTracking.Button or MouseTracking.Any && (row, column) != _lastReportedCell)
        {
            if (_mouseButtonDown || _screen.Mouse == MouseTracking.Any)
            {
                _lastReportedCell = (row, column);
                var button = point.Properties.IsLeftButtonPressed ? 0 : point.Properties.IsMiddleButtonPressed ? 1 : point.Properties.IsRightButtonPressed ? 2 : 3;
                ReportMouse(button, row, column, release: false, motion: true);
            }
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_selecting)
        {
            _selecting = false;
            ReleasePointerCapture(e.Pointer);
            e.Handled = true;
            return;
        }

        if (_mouseButtonDown)
        {
            _mouseButtonDown = false;
            var point = e.GetCurrentPoint(this);
            var (row, column) = CellAt(point.Position);
            ReportMouse(0, row, column, release: true, motion: false);
            e.Handled = true;
        }
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        var notches = point.Properties.MouseWheelDelta / 120;
        if (notches == 0)
            return;

        e.Handled = true;
        var (row, column) = CellAt(point.Position);
        if (_screen.Mouse != MouseTracking.None && !IsDown(VirtualKey.Shift))
        {
            for (var i = 0; i < Math.Abs(notches); i++)
                ReportMouse(notches > 0 ? 64 : 65, row, column, release: false, motion: false);
            return;
        }

        if (_screen.IsAlternateScreen)
        {
            // A full-screen program has no scrollback; arrow keys are what it expects from a wheel.
            var key = notches > 0 ? "\u001b[A" : "\u001b[B";
            if (_screen.ApplicationCursorKeys)
                key = notches > 0 ? "\u001bOA" : "\u001bOB";
            for (var i = 0; i < Math.Abs(notches) * WheelLinesPerNotch; i++)
                InputRequested?.Invoke(key);
            return;
        }

        _screen.ScrollView(notches * WheelLinesPerNotch);
        if (_screen.Dirty)
            _canvas.Invalidate();
    }

    /// <summary>X10/normal (CSI M with offset bytes) or SGR (CSI &lt; b;x;y M/m) mouse report.</summary>
    private void ReportMouse(int button, int row, int column, bool release, bool motion)
    {
        if (_screen.Mouse == MouseTracking.X10 && (release || motion || button > 2))
            return;

        var code = button + (IsDown(VirtualKey.Shift) ? 4 : 0) + (IsDown(VirtualKey.Menu) ? 8 : 0) + (IsDown(VirtualKey.Control) ? 16 : 0) + (motion ? 32 : 0);
        if (_screen.MouseSgr)
        {
            InputRequested?.Invoke($"\u001b[<{code};{column + 1};{row + 1}{(release ? 'm' : 'M')}");
            return;
        }

        if (release)
            code = 3 + (code & ~3);
        if (column > 222 || row > 222)
            return;

        InputRequested?.Invoke($"\u001b[M{(char)(32 + code)}{(char)(33 + column)}{(char)(33 + row)}");
    }

    // ---------------------------------------------------------------- Selection

    private ((int Row, int Column) Start, (int Row, int Column) End) NormalizedSelection()
    {
        var a = _selectionAnchor;
        var f = _selectionFocus;
        return a.Row < f.Row || (a.Row == f.Row && a.Column <= f.Column) ? (a, f) : (f, a);
    }

    private void ClearSelection()
    {
        _hasSelection = false;
        _selecting = false;
    }

    private void SelectWord(int absoluteRow, int column)
    {
        var line = _screen.AbsoluteLine(absoluteRow);
        if (line is null)
            return;

        var cells = line.Cells;
        if (cells[column].IsBlank)
            return;

        var start = column;
        while (start > 0 && !cells[start - 1].IsBlank)
            start--;
        var end = column;
        while (end + 1 < cells.Length && !cells[end + 1].IsBlank)
            end++;

        _selectionAnchor = (absoluteRow, start);
        _selectionFocus = (absoluteRow, end);
        _hasSelection = true;
    }

    private string SelectedText()
    {
        if (!_hasSelection)
            return string.Empty;

        var (start, end) = NormalizedSelection();
        var text = new StringBuilder();
        for (var row = start.Row; row <= end.Row; row++)
        {
            var line = _screen.AbsoluteLine(row);
            if (line is null)
                continue;

            var rowText = line.ToText();
            var from = row == start.Row ? start.Column : 0;
            var to = row == end.Row ? end.Column + 1 : int.MaxValue;
            from = Math.Min(from, rowText.Length);
            to = Math.Min(to, rowText.Length);
            text.Append(rowText, from, Math.Max(0, to - from));
            // A soft-wrapped row continues on the next one; a hard line break becomes a Windows newline.
            if (row < end.Row && !line.Wrapped)
                text.Append("\r\n");
        }

        return text.ToString();
    }
}
