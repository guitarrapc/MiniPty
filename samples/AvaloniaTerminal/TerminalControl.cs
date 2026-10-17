using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using TerminalEmulator;

namespace AvaloniaTerminal;

/// <summary>
/// A terminal view: feeds PTY bytes to a <see cref="VtParser"/>, draws the <see cref="ScreenBuffer"/> in
/// <see cref="Render"/>, and turns keyboard, text input, mouse, and clipboard actions into the bytes a program
/// expects. It knows nothing about MiniPty: bytes come in through <see cref="Feed"/> and go out through
/// <see cref="InputRequested"/>, and the window owning the session wires the two. The emulator itself lives in
/// <c>samples/TerminalEmulator</c>, shared with the WinUI sample; this file is the Avalonia rendering and input layer.
/// </summary>
/// <remarks>
/// Mouse: drag selects by cell (double-click selects a word), Ctrl+Shift+C / Ctrl+Insert copy, Ctrl+Shift+V /
/// Shift+Insert / Ctrl+V paste (Cmd on macOS), Ctrl+C copies when a selection exists and otherwise interrupts,
/// and a right click copies the selection if there is one or pastes if there is none. When the program has
/// enabled mouse tracking, clicks are reported to it instead, and Shift forces local selection. The wheel scrolls
/// the scrollback, or sends arrow keys to a full-screen program. Wheel deltas are accumulated, so a trackpad's many
/// small deltas scroll by distance instead of by event count.
/// <para>
/// Text input: the control hands Avalonia an input method client (<see cref="TerminalInputMethodClient"/>), which
/// is what turns the platform input method on for it. Without one, Avalonia on macOS bypasses the input method
/// entirely, so Japanese and Chinese input and dead keys never compose. The composition in progress (preedit) is
/// drawn underlined at the cursor; committed text arrives through <see cref="OnTextInput"/>.
/// </para>
/// </remarks>
internal sealed class TerminalControl : Control
{
    private const double FontSize = 14;
    private const int WheelLinesPerNotch = 3;

    private readonly ScreenBuffer _screen;
    private readonly VtParser _parser;
    private readonly TerminalSelection _selection = new();
    private readonly Typeface[] _typefaces = new Typeface[4];
    private readonly Dictionary<uint, IBrush> _brushes = new();
    private readonly StringBuilder _runText = new();
    private readonly IBrush _selectionBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x60, 0x80, 0xB0, 0xFF));
    private readonly double _cellWidth;
    private readonly double _cellHeight;
    private readonly TerminalInputMethodClient _inputMethodClient;
    private string _preedit = string.Empty;
    private (int Row, int Column) _inputMethodCursor = (-1, -1);
    private WheelAccumulator _wheel;
    private bool _focused;
    private bool _mouseButtonDown;
    private (int Row, int Column) _lastReportedCell = (-1, -1);

    public TerminalControl(int columns, int rows)
    {
        _screen = new ScreenBuffer(columns, rows);
        _parser = new VtParser(_screen);
        _screen.Response += text => InputRequested?.Invoke(text);
        _screen.TitleChanged += title => TitleChanged?.Invoke(title);

        // A monospace family per platform; the list falls back left to right, and glyphs the chosen font lacks
        // (CJK, emoji) come from the system's fallback fonts.
        var family = new FontFamily("Cascadia Mono, Consolas, Menlo, SF Mono, DejaVu Sans Mono, Noto Sans Mono, Liberation Mono, monospace");
        for (var i = 0; i < _typefaces.Length; i++)
        {
            _typefaces[i] = new Typeface(
                family,
                (i & 2) != 0 ? FontStyle.Italic : FontStyle.Normal,
                (i & 1) != 0 ? FontWeight.Bold : FontWeight.Normal);
        }

        var probe = Format("M", _typefaces[0], Brushes.White);
        _cellWidth = Math.Ceiling(probe.Width * 100) / 100;
        _cellHeight = Math.Ceiling(probe.Height);

        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);

        // Asked for when the control takes focus; answering is what enables the platform input method for it.
        _inputMethodClient = new TerminalInputMethodClient(this);
        AddHandler(TextInputMethodClientRequestedEvent, (_, e) => e.Client = _inputMethodClient);
    }

    /// <summary>Text to send to the program: keystrokes, pasted text, mouse reports, and terminal replies.</summary>
    public event Action<string>? InputRequested;

    /// <summary>
    /// The user typed or pasted. Raised alongside <see cref="InputRequested"/> but not for mouse reports, focus
    /// events, or terminal replies, so a host can react to deliberate input alone (a "press any key" prompt).
    /// </summary>
    public event Action? UserInput;

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
            InvalidateVisual();
        NotifyCursorMoved();
    }

    /// <summary>The visible screen as text, one line per row, for smoke tests.</summary>
    public string VisibleText()
    {
        var text = new StringBuilder();
        for (var row = 0; row < _screen.Rows; row++)
            text.AppendLine(_screen.ViewLine(row).ToText());
        return text.ToString();
    }

    /// <summary>Cells that fit in <paramref name="size"/>, at least 2×1.</summary>
    public (int Columns, int Rows) GridFor(Size size) =>
        (Math.Max(2, (int)(size.Width / _cellWidth)), Math.Max(1, (int)(size.Height / _cellHeight)));

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var (columns, rows) = GridFor(e.NewSize);
        if (columns == _screen.Columns && rows == _screen.Rows)
            return;

        _screen.Resize(columns, rows);
        _selection.Clear();
        GridSizeChanged?.Invoke(columns, rows);
        InvalidateVisual();
        NotifyCursorMoved();
    }

    // ---------------------------------------------------------------- Drawing

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brush(Palette.DefaultBackground), new Rect(Bounds.Size));
        _screen.Dirty = false;

        var rows = _screen.Rows;
        var columns = _screen.Columns;
        for (var row = 0; row < rows; row++)
        {
            var line = _screen.ViewLine(row);
            var cells = line.Cells;
            var y = row * _cellHeight;

            // Backgrounds first, merged over runs of the same color, so glyph overhang never covers a neighbor.
            var runStart = 0;
            while (runStart < columns)
            {
                var background = BackgroundOf(cells[runStart]);
                var runEnd = runStart + 1;
                while (runEnd < columns && BackgroundOf(cells[runEnd]) == background)
                    runEnd++;

                if (background != Palette.DefaultBackground)
                    context.FillRectangle(Brush(background), new Rect(runStart * _cellWidth, y, (runEnd - runStart) * _cellWidth, _cellHeight));

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
                var typeface = _typefaces[(attributes.Has(CellFlags.Bold) ? 1 : 0) | (attributes.Has(CellFlags.Italic) ? 2 : 0)];
                var foreground = Brush(ForegroundOf(cell), attributes.Has(CellFlags.Dim) ? 0.55 : 1);
                var x = column * _cellWidth;

                var end = column + 1;
                string text;
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

                    text = _runText.ToString();
                }
                else
                {
                    text = cell.Text;
                    end = column + Math.Max(1, (int)cell.Width);
                }

                if (!attributes.Has(CellFlags.Hidden))
                {
                    var formatted = Format(text, typeface, foreground);
                    if (attributes.Has(CellFlags.Underline))
                        formatted.SetTextDecorations(TextDecorations.Underline);
                    if (attributes.Has(CellFlags.Strikethrough))
                        formatted.SetTextDecorations(TextDecorations.Strikethrough);
                    context.DrawText(formatted, new Point(x, y));
                }

                column = end;
            }

            if (_selection.TryGetRowRange(_screen.ViewRowToAbsolute(row), columns, out var from, out var to))
                context.FillRectangle(_selectionBrush, new Rect(from * _cellWidth, y, (to - from) * _cellWidth, _cellHeight));
        }

        // An input method composition replaces the cursor: it is drawn where the committed text will go.
        if (_preedit.Length > 0)
        {
            DrawPreedit(context);
            return;
        }

        // Cursor: a solid block with focus, a hollow one without; hidden while scrolled back.
        if (_screen.CursorVisible && _screen.ScrollOffset == 0)
        {
            var cursorCell = _screen.ViewLine(_screen.CursorRow).Cells[_screen.CursorColumn];
            var width = Math.Max(1, (int)cursorCell.Width) * _cellWidth;
            var rect = new Rect(_screen.CursorColumn * _cellWidth, _screen.CursorRow * _cellHeight, width, _cellHeight);
            if (_focused)
            {
                context.FillRectangle(Brush(Palette.DefaultForeground), rect);
                if (cursorCell.Text is not null)
                {
                    var typeface = _typefaces[(cursorCell.Attributes.Has(CellFlags.Bold) ? 1 : 0) | (cursorCell.Attributes.Has(CellFlags.Italic) ? 2 : 0)];
                    context.DrawText(Format(cursorCell.Text, typeface, Brush(Palette.DefaultBackground)), rect.TopLeft);
                }
            }
            else
            {
                context.DrawRectangle(new Pen(Brush(Palette.DefaultForeground)), rect.Deflate(0.5));
            }
        }
    }

    /// <summary>
    /// The composition in progress, underlined on the default background from the cursor on. Wide characters take
    /// two cells as they will once committed, and text past the right edge is clipped.
    /// </summary>
    private void DrawPreedit(DrawingContext context)
    {
        var cells = 0;
        foreach (var rune in _preedit.EnumerateRunes())
            cells += CharWidth.Of(rune.Value);

        var origin = new Point(_screen.CursorColumn * _cellWidth, _screen.CursorRow * _cellHeight);
        context.FillRectangle(Brush(Palette.DefaultBackground), new Rect(origin, new Size(Math.Max(1, cells) * _cellWidth, _cellHeight)));
        var formatted = Format(_preedit, _typefaces[0], Brush(Palette.DefaultForeground));
        formatted.SetTextDecorations(TextDecorations.Underline);
        context.DrawText(formatted, origin);
    }

    /// <summary>The cell the next character goes into, where an input method places its candidate window.</summary>
    internal Rect CursorCellRect() =>
        new(_screen.CursorColumn * _cellWidth, _screen.CursorRow * _cellHeight, _cellWidth, _cellHeight);

    /// <summary>Tells the input method the cursor moved, only when it did, so output does not flood it.</summary>
    private void NotifyCursorMoved()
    {
        var cursor = (_screen.CursorRow, _screen.CursorColumn);
        if (cursor == _inputMethodCursor)
            return;

        _inputMethodCursor = cursor;
        _inputMethodClient.NotifyCursorRectangleChanged();
    }

    internal void SetPreedit(string? text)
    {
        text ??= string.Empty;
        if (text == _preedit)
            return;

        _preedit = text;
        InvalidateVisual();
    }

    private static FormattedText Format(string text, Typeface typeface, IBrush foreground) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, FontSize, foreground);

    private IBrush Brush(uint rgb, double alpha = 1)
    {
        var key = rgb | ((uint)(alpha * 255) << 24);
        if (!_brushes.TryGetValue(key, out var brush))
        {
            brush = new ImmutableSolidColorBrush(Color.FromArgb((byte)(alpha * 255), (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            _brushes[key] = brush;
        }

        return brush;
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

    // ---------------------------------------------------------------- Keyboard

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        _focused = true;
        if (_screen.FocusEvents)
            InputRequested?.Invoke("\u001b[I");
        InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _focused = false;
        if (_screen.FocusEvents)
            InputRequested?.Invoke("\u001b[O");
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        // Cmd on macOS plays the role Ctrl plays elsewhere for the clipboard shortcuts.
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var command = ctrl || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        // AltGr arrives as Ctrl+Alt and produces ordinary text through OnTextInput.
        if (ctrl && alt)
            return;

        // While an input method composes, every key belongs to it (Enter commits, Backspace edits the composition).
        if (e.Key == Key.ImeProcessed)
            return;

        var termKey = AvaloniaKeys.ToTermKey(e.Key);

        // On macOS, Option is the layout's third level, as AltGr is elsewhere: Option+E is a dead key, Option+L is
        // @ on a German layout, Option+Y is ¥ on a US layout. Encoding Option+letter as ESC+letter would swallow those
        // characters, so letters and digits with Option are left to text input, as Terminal.app and iTerm2 do by
        // default. Option still modifies keys that type nothing: arrows, Backspace, Enter, and function keys.
        if (alt && !command && OperatingSystem.IsMacOS() && termKey is >= TermKey.A and <= TermKey.Z or >= TermKey.D0 and <= TermKey.D9)
            return;

        if (command && shift && e.Key is Key.C or Key.V)
        {
            if (e.Key == Key.C)
                CopySelection();
            else
                _ = PasteAsync();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Insert && (command || shift) && !(command && shift))
        {
            if (command)
                CopySelection();
            else
                _ = PasteAsync();
            e.Handled = true;
            return;
        }

        if (command && !shift && e.Key == Key.V)
        {
            _ = PasteAsync();
            e.Handled = true;
            return;
        }

        if (command && !shift && e.Key == Key.C && _selection.HasSelection)
        {
            CopySelection();
            e.Handled = true;
            return;
        }

        var sequence = KeyEncoder.Encode(termKey, shift, ctrl, alt, _screen.ApplicationCursorKeys);
        if (sequence is null)
            return;

        Send(sequence);
        e.Handled = true;
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (string.IsNullOrEmpty(e.Text))
            return;

        // Control characters were already sent from the key event.
        var text = e.Text;
        var clean = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c >= ' ' && c != '\u007f')
                clean.Append(c);
        }

        if (clean.Length == 0)
            return;

        e.Handled = true;
        Send(clean.ToString());
    }

    private void Send(string text)
    {
        _screen.ScrollViewToBottom();
        UserInput?.Invoke();
        InputRequested?.Invoke(text);
        if (_screen.Dirty)
            InvalidateVisual();
    }

    // ---------------------------------------------------------------- Clipboard

    private void CopySelection()
    {
        var text = _selection.GetText(_screen);
        if (text.Length == 0)
            return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
            _ = clipboard.SetTextAsync(text);

        _selection.Clear();
        InvalidateVisual();
    }

    private async Task PasteAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
            return;

        string? text;
        try
        {
            text = await clipboard.TryGetTextAsync();
        }
        catch (Exception)
        {
            // Another process holds the clipboard; a paste that does nothing is the right outcome.
            return;
        }

        if (!string.IsNullOrEmpty(text))
            Send(PasteText.Normalize(text, _screen.BracketedPaste));
    }

    // ---------------------------------------------------------------- Mouse

    private (int Row, int Column) CellAt(Point position)
    {
        var column = Math.Clamp((int)(position.X / _cellWidth), 0, _screen.Columns - 1);
        var row = Math.Clamp((int)(position.Y / _cellHeight), 0, _screen.Rows - 1);
        return (row, column);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetCurrentPoint(this);
        var (row, column) = CellAt(point.Position);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        e.Handled = true;

        if (_screen.Mouse != MouseTracking.None && !shift)
        {
            var button = point.Properties.IsLeftButtonPressed ? MouseReport.LeftButton : point.Properties.IsMiddleButtonPressed ? MouseReport.MiddleButton : MouseReport.RightButton;
            _mouseButtonDown = true;
            _lastReportedCell = (row, column);
            ReportMouse(button, row, column, release: false, motion: false, e.KeyModifiers);
            return;
        }

        if (point.Properties.IsRightButtonPressed)
        {
            // Windows Terminal's convention: right click copies a selection, or pastes when there is none.
            if (_selection.HasSelection)
                CopySelection();
            else
                _ = PasteAsync();
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
            return;

        var absoluteRow = _screen.ViewRowToAbsolute(row);
        if (e.ClickCount >= 2)
        {
            _selection.SelectWord(_screen, absoluteRow, column);
        }
        else
        {
            _selection.Begin(absoluteRow, column);
            e.Pointer.Capture(this);
        }

        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetCurrentPoint(this);
        var (row, column) = CellAt(point.Position);

        if (_selection.IsDragging)
        {
            if (_selection.Extend(_screen.ViewRowToAbsolute(row), column))
                InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (_screen.Mouse is MouseTracking.Button or MouseTracking.Any && (row, column) != _lastReportedCell)
        {
            if (_mouseButtonDown || _screen.Mouse == MouseTracking.Any)
            {
                _lastReportedCell = (row, column);
                var button = point.Properties.IsLeftButtonPressed ? MouseReport.LeftButton
                    : point.Properties.IsMiddleButtonPressed ? MouseReport.MiddleButton
                    : point.Properties.IsRightButtonPressed ? MouseReport.RightButton
                    : MouseReport.NoButton;
                ReportMouse(button, row, column, release: false, motion: true, e.KeyModifiers);
            }
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_selection.IsDragging)
        {
            _selection.EndDrag();
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        if (_mouseButtonDown)
        {
            _mouseButtonDown = false;
            var (row, column) = CellAt(e.GetPosition(this));
            ReportMouse(MouseReport.LeftButton, row, column, release: true, motion: false, e.KeyModifiers);
            e.Handled = true;
        }
    }

    /// <remarks>
    /// A mouse wheel notch arrives as a delta of 1, but a trackpad sends a stream of small fractional deltas (macOS
    /// reports each as pixels / 50), so <see cref="WheelAccumulator"/> carries the fractions and only whole lines
    /// are acted on.
    /// </remarks>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.Delta.Y == 0)
            return;

        e.Handled = true;
        var lines = e.Delta.Y * WheelLinesPerNotch;
        var (row, column) = CellAt(e.GetPosition(this));
        if (_screen.Mouse != MouseTracking.None && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            // One report per notch's worth of lines: a program scrolls its own step for each report.
            var notches = _wheel.Add(lines, WheelLinesPerNotch);
            for (var i = 0; i < Math.Abs(notches); i++)
                ReportMouse(notches > 0 ? MouseReport.WheelUp : MouseReport.WheelDown, row, column, release: false, motion: false, e.KeyModifiers);
            return;
        }

        var whole = _wheel.Add(lines);
        if (whole == 0)
            return;

        if (_screen.IsAlternateScreen)
        {
            // A full-screen program has no scrollback; arrow keys are what it expects from a wheel.
            var key = KeyEncoder.ScrollArrow(whole > 0, _screen.ApplicationCursorKeys);
            for (var i = 0; i < Math.Abs(whole); i++)
                InputRequested?.Invoke(key);
            return;
        }

        _screen.ScrollView(whole);
        if (_screen.Dirty)
            InvalidateVisual();
    }

    private void ReportMouse(int button, int row, int column, bool release, bool motion, KeyModifiers modifiers)
    {
        var report = MouseReport.Encode(
            _screen.Mouse, _screen.MouseSgr, button, row, column, release, motion,
            modifiers.HasFlag(KeyModifiers.Shift), modifiers.HasFlag(KeyModifiers.Alt), modifiers.HasFlag(KeyModifiers.Control));
        if (report is not null)
            InputRequested?.Invoke(report);
    }
}

/// <summary>Maps Avalonia keys onto the framework-neutral <see cref="TermKey"/>.</summary>
internal static class AvaloniaKeys
{
    public static TermKey ToTermKey(Key key)
    {
        if (key is >= Key.A and <= Key.Z)
            return TermKey.A + (key - Key.A);
        if (key is >= Key.D0 and <= Key.D9)
            return TermKey.D0 + (key - Key.D0);
        if (key is >= Key.F1 and <= Key.F12)
            return TermKey.F1 + (key - Key.F1);

        return key switch
        {
            Key.Up => TermKey.Up,
            Key.Down => TermKey.Down,
            Key.Right => TermKey.Right,
            Key.Left => TermKey.Left,
            Key.Home => TermKey.Home,
            Key.End => TermKey.End,
            Key.Insert => TermKey.Insert,
            Key.Delete => TermKey.Delete,
            Key.PageUp => TermKey.PageUp,
            Key.PageDown => TermKey.PageDown,
            Key.Enter => TermKey.Enter,
            Key.Tab => TermKey.Tab,
            Key.Back => TermKey.Backspace,
            Key.Escape => TermKey.Escape,
            Key.Space => TermKey.Space,
            Key.OemOpenBrackets => TermKey.BracketLeft,
            Key.Oem5 => TermKey.Backslash,
            Key.OemCloseBrackets => TermKey.BracketRight,
            Key.OemMinus => TermKey.Minus,
            _ => TermKey.None,
        };
    }
}

/// <summary>
/// What Avalonia's input method support needs from a terminal: where the cursor is (for the candidate window) and
/// a place to show the composition. A terminal has no editable text of its own, so it offers no surrounding text
/// or selection; committed text reaches the control as an ordinary <c>TextInput</c> event.
/// </summary>
internal sealed class TerminalInputMethodClient(TerminalControl owner) : TextInputMethodClient
{
    public override Visual TextViewVisual => owner;

    public override bool SupportsPreedit => true;

    public override bool SupportsSurroundingText => false;

    public override string SurroundingText => string.Empty;

    public override Rect CursorRectangle => owner.CursorCellRect();

    public override TextSelection Selection
    {
        get => default;
        set { }
    }

    public override void SetPreeditText(string? preeditText) => owner.SetPreedit(preeditText);

    public void NotifyCursorRectangleChanged() => RaiseCursorRectangleChanged();
}
