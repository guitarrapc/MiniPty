using System.Text;

namespace TerminalEmulator;

/// <summary>
/// Decodes the UTF-8 byte stream from the PTY and drives a <see cref="ScreenBuffer"/>: printable text (with
/// surrogate pairs and combining marks), C0 controls, ESC sequences, CSI with parameters and sub-parameters,
/// OSC strings, and the DCS/SOS/PM/APC strings it skips. The state machine follows the classic VT500 parser;
/// an ESC anywhere restarts it, and CAN/SUB abort to ground.
/// </summary>
internal sealed class VtParser
{
    private enum State
    {
        Ground,
        Escape,
        EscapeIntermediate,
        CsiEntry,
        CsiParam,
        CsiIntermediate,
        CsiIgnore,
        OscString,
        OscEscape,
        SkipString,
        SkipStringEscape,
    }

    private const int MaxParams = 32;

    private readonly ScreenBuffer _screen;
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _osc = new();
    private readonly int[] _params = new int[MaxParams];
    private readonly bool[] _subParams = new bool[MaxParams];
    private char[] _chars = new char[8192];
    private State _state = State.Ground;
    private int _paramCount;
    private bool _paramStarted;
    private char _privateMarker;
    private char _intermediate;
    private char _highSurrogate;

    public VtParser(ScreenBuffer screen) => _screen = screen;

    public void Feed(ReadOnlySpan<byte> bytes)
    {
        var needed = _decoder.GetCharCount(bytes, flush: false);
        if (_chars.Length < needed)
            _chars = new char[Math.Max(needed, _chars.Length * 2)];

        var count = _decoder.GetChars(bytes, _chars, flush: false);
        for (var i = 0; i < count; i++)
            Process(_chars[i]);
    }

    private void Process(char c)
    {
        // C0 controls act in every state except inside strings, and ESC restarts any sequence.
        if (c < 0x20)
        {
            switch (c)
            {
                case '\u001b':
                    if (_state is State.OscString)
                        _state = State.OscEscape;
                    else if (_state is State.SkipString)
                        _state = State.SkipStringEscape;
                    else
                        EnterEscape();
                    return;
                case '\u0018':
                case '\u001a':
                    _state = State.Ground;
                    return;
            }

            if (_state is State.OscString or State.OscEscape or State.SkipString or State.SkipStringEscape)
            {
                if (c == '\u0007' && _state is State.OscString)
                    DispatchOsc();
                return;
            }

            Execute(c);
            return;
        }

        switch (_state)
        {
            case State.Ground:
                Print(c);
                break;
            case State.Escape:
                if (c is >= ' ' and <= '/')
                {
                    _intermediate = c;
                    _state = State.EscapeIntermediate;
                }
                else
                {
                    EscapeDispatch(c);
                }

                break;
            case State.EscapeIntermediate:
                if (c is >= ' ' and <= '/')
                    _intermediate = c;
                else
                    EscapeDispatch(c);
                break;
            case State.CsiEntry:
            case State.CsiParam:
            case State.CsiIntermediate:
                CsiCollect(c);
                break;
            case State.CsiIgnore:
                if (c is >= '@' and <= '~')
                    _state = State.Ground;
                break;
            case State.OscString:
                _osc.Append(c);
                break;
            case State.OscEscape:
                // ESC \ terminates; any other ESC sequence ends the string and starts over.
                DispatchOsc();
                if (c != '\\')
                {
                    EnterEscape();
                    Process(c);
                }

                break;
            case State.SkipString:
                break;
            case State.SkipStringEscape:
                _state = State.Ground;
                if (c != '\\')
                {
                    EnterEscape();
                    Process(c);
                }

                break;
        }
    }

    private void EnterEscape()
    {
        _state = State.Escape;
        _intermediate = '\0';
        _privateMarker = '\0';
        _paramCount = 0;
        _paramStarted = false;
        // The first parameter accumulates into slot 0 without an explicit start, so it is cleared here; later
        // slots are cleared when their separator arrives.
        _params[0] = 0;
        _subParams[0] = false;
    }

    // ---------------------------------------------------------------- Text

    private void Print(char c)
    {
        if (c == '\u007f')
            return;

        if (char.IsHighSurrogate(c))
        {
            _highSurrogate = c;
            return;
        }

        string text;
        int codePoint;
        if (char.IsLowSurrogate(c) && _highSurrogate != '\0')
        {
            text = new string([_highSurrogate, c]);
            codePoint = char.ConvertToUtf32(_highSurrogate, c);
            _highSurrogate = '\0';
        }
        else
        {
            _highSurrogate = '\0';
            if (char.IsLowSurrogate(c))
                return;

            text = c < 0x7F ? Ascii[c - 0x20] : c.ToString();
            codePoint = c;
        }

        var width = CharWidth.Of(codePoint);
        if (width == 0)
        {
            if (CharWidth.IsCombining(codePoint))
                _screen.Combine(text);
            return;
        }

        _screen.Print(text, width);
    }

    private static readonly string[] Ascii = CreateAscii();

    private static string[] CreateAscii()
    {
        var table = new string[0x7F - 0x20];
        for (var i = 0; i < table.Length; i++)
            table[i] = ((char)(0x20 + i)).ToString();
        return table;
    }

    // ---------------------------------------------------------------- C0

    private void Execute(char c)
    {
        switch (c)
        {
            case '\u0007':
                _screen.RingBell();
                break;
            case '\b':
                _screen.Backspace();
                break;
            case '\t':
                _screen.Tab();
                break;
            case '\n':
            case '\v':
            case '\f':
                _screen.LineFeed();
                break;
            case '\r':
                _screen.CarriageReturn();
                break;
            case '\u000e':
                _screen.ShiftOut();
                break;
            case '\u000f':
                _screen.ShiftIn();
                break;
        }
    }

    // ---------------------------------------------------------------- ESC

    private void EscapeDispatch(char c)
    {
        _state = State.Ground;
        switch (_intermediate)
        {
            case '\0':
                switch (c)
                {
                    case '[':
                        _state = State.CsiEntry;
                        return;
                    case ']':
                        _osc.Clear();
                        _state = State.OscString;
                        return;
                    case 'P':
                    case 'X':
                    case '^':
                    case '_':
                        _state = State.SkipString;
                        return;
                    case '7':
                        _screen.SaveCursor();
                        break;
                    case '8':
                        _screen.RestoreCursor();
                        break;
                    case 'D':
                        _screen.LineFeed();
                        break;
                    case 'E':
                        _screen.NextLine();
                        break;
                    case 'H':
                        _screen.SetTabStop();
                        break;
                    case 'M':
                        _screen.ReverseIndex();
                        break;
                    case 'c':
                        _screen.Reset();
                        break;
                }

                break;
            case '(':
                _screen.DesignateCharset(g1: false, graphics: c == '0');
                break;
            case ')':
                _screen.DesignateCharset(g1: true, graphics: c == '0');
                break;
            case '#':
                if (c == '8')
                    _screen.FillWithE();
                break;
        }
    }

    // ---------------------------------------------------------------- CSI

    private void CsiCollect(char c)
    {
        if (c is >= '0' and <= '9')
        {
            if (_state == State.CsiIntermediate)
            {
                _state = State.CsiIgnore;
                return;
            }

            if (_paramCount == 0)
                _paramCount = 1;

            ref var value = ref _params[_paramCount - 1];
            value = Math.Min(value * 10 + (c - '0'), 65535);
            _paramStarted = true;
            _state = State.CsiParam;
            return;
        }

        if (c is ';' or ':')
        {
            if (_state == State.CsiIntermediate)
            {
                _state = State.CsiIgnore;
                return;
            }

            if (_paramCount == 0)
                _paramCount = 1;

            if (_paramCount >= MaxParams)
            {
                _state = State.CsiIgnore;
                return;
            }

            _params[_paramCount] = 0;
            _subParams[_paramCount] = c == ':';
            _paramCount++;
            _paramStarted = true;
            _state = State.CsiParam;
            return;
        }

        if (c is '?' or '>' or '<' or '=')
        {
            if (_state != State.CsiEntry)
            {
                _state = State.CsiIgnore;
                return;
            }

            _privateMarker = c;
            _state = State.CsiParam;
            return;
        }

        if (c is >= ' ' and <= '/')
        {
            _intermediate = c;
            _state = State.CsiIntermediate;
            return;
        }

        if (c is >= '@' and <= '~')
        {
            CsiDispatch(c);
            return;
        }

        _state = State.CsiIgnore;
    }

    private int Param(int index, int fallback)
    {
        if (index >= _paramCount)
            return fallback;

        var value = _params[index];
        return value == 0 ? fallback : value;
    }

    private void CsiDispatch(char final)
    {
        _state = State.Ground;
        if (_paramCount == 1 && !_paramStarted)
            _paramCount = 0;

        if (_intermediate != '\0')
        {
            // DECSCUSR (SP q), DECSTR (! p); other intermediates are unsupported.
            if (_intermediate == '!' && final == 'p')
                _screen.SoftReset();
            return;
        }

        switch (_privateMarker)
        {
            case '?':
                if (final is 'h' or 'l')
                {
                    for (var i = 0; i < Math.Max(1, _paramCount); i++)
                        _screen.SetDecMode(Param(i, 0), final == 'h');
                }

                return;
            case '>':
                if (final == 'c')
                    _screen.ReportSecondaryAttributes();
                return;
            case '<':
            case '=':
                return;
        }

        switch (final)
        {
            case '@':
                _screen.InsertCharacters(Param(0, 1));
                break;
            case 'A':
                _screen.MoveCursor(-Param(0, 1), 0);
                break;
            case 'B':
            case 'e':
                _screen.MoveCursor(Param(0, 1), 0);
                break;
            case 'C':
            case 'a':
                _screen.MoveCursor(0, Param(0, 1));
                break;
            case 'D':
                _screen.MoveCursor(0, -Param(0, 1));
                break;
            case 'E':
                _screen.MoveCursor(Param(0, 1), 0);
                _screen.CarriageReturn();
                break;
            case 'F':
                _screen.MoveCursor(-Param(0, 1), 0);
                _screen.CarriageReturn();
                break;
            case 'G':
            case '`':
                _screen.SetColumn(Param(0, 1));
                break;
            case 'H':
            case 'f':
                _screen.SetCursor(Param(0, 1), Param(1, 1));
                break;
            case 'I':
                for (var i = 0; i < Param(0, 1); i++)
                    _screen.Tab();
                break;
            case 'J':
                _screen.EraseInDisplay(Param(0, 0));
                break;
            case 'K':
                _screen.EraseInLine(Param(0, 0));
                break;
            case 'L':
                _screen.InsertLines(Param(0, 1));
                break;
            case 'M':
                _screen.DeleteLines(Param(0, 1));
                break;
            case 'P':
                _screen.DeleteCharacters(Param(0, 1));
                break;
            case 'S':
                _screen.ScrollUp(Param(0, 1));
                break;
            case 'T':
                _screen.ScrollDown(Param(0, 1));
                break;
            case 'X':
                _screen.EraseCharacters(Param(0, 1));
                break;
            case 'Z':
                _screen.BackTab(Param(0, 1));
                break;
            case 'b':
                _screen.RepeatLast(Param(0, 1));
                break;
            case 'c':
                if (Param(0, 0) == 0)
                    _screen.ReportPrimaryAttributes();
                break;
            case 'd':
                _screen.SetRow(Param(0, 1));
                break;
            case 'g':
                _screen.ClearTabStop(Param(0, 0));
                break;
            case 'h':
            case 'l':
                for (var i = 0; i < _paramCount; i++)
                {
                    if (_params[i] == 4)
                        _screen.SetInsertMode(final == 'h');
                }

                break;
            case 'm':
                SelectGraphicRendition();
                break;
            case 'n':
                switch (Param(0, 0))
                {
                    case 5:
                        _screen.ReportStatusOk();
                        break;
                    case 6:
                        _screen.ReportCursorPosition();
                        break;
                }

                break;
            case 'r':
                _screen.SetScrollRegion(Param(0, 1), Param(1, 0));
                break;
            case 's':
                _screen.SaveCursor();
                break;
            case 'u':
                _screen.RestoreCursor();
                break;
        }
    }

    private void SelectGraphicRendition()
    {
        var attributes = _screen.Attributes;
        if (_paramCount == 0)
        {
            _screen.SetAttributes(CellAttributes.Plain);
            return;
        }

        var i = 0;
        while (i < _paramCount)
        {
            var code = _params[i];
            // Sub-parameters (4:3, 38:2:r:g:b) belong to the parameter before them.
            var subCount = 0;
            while (i + 1 + subCount < _paramCount && _subParams[i + 1 + subCount])
                subCount++;

            switch (code)
            {
                case 0:
                    attributes = CellAttributes.Plain;
                    break;
                case 1:
                    attributes.Flags |= CellFlags.Bold;
                    break;
                case 2:
                    attributes.Flags |= CellFlags.Dim;
                    break;
                case 3:
                    attributes.Flags |= CellFlags.Italic;
                    break;
                case 4:
                    // 4:0 turns underline off; every other style is drawn as a plain underline.
                    if (subCount > 0 && _params[i + 1] == 0)
                        attributes.Flags &= ~CellFlags.Underline;
                    else
                        attributes.Flags |= CellFlags.Underline;
                    break;
                case 5:
                case 6:
                    attributes.Flags |= CellFlags.Blink;
                    break;
                case 7:
                    attributes.Flags |= CellFlags.Inverse;
                    break;
                case 8:
                    attributes.Flags |= CellFlags.Hidden;
                    break;
                case 9:
                    attributes.Flags |= CellFlags.Strikethrough;
                    break;
                case 21:
                case 24:
                    attributes.Flags &= ~CellFlags.Underline;
                    break;
                case 22:
                    attributes.Flags &= ~(CellFlags.Bold | CellFlags.Dim);
                    break;
                case 23:
                    attributes.Flags &= ~CellFlags.Italic;
                    break;
                case 25:
                    attributes.Flags &= ~CellFlags.Blink;
                    break;
                case 27:
                    attributes.Flags &= ~CellFlags.Inverse;
                    break;
                case 28:
                    attributes.Flags &= ~CellFlags.Hidden;
                    break;
                case 29:
                    attributes.Flags &= ~CellFlags.Strikethrough;
                    break;
                case >= 30 and <= 37:
                    attributes.Foreground = TermColor.Indexed(code - 30);
                    break;
                case 38:
                    i += ParseExtendedColor(i, subCount, out var foreground);
                    attributes.Foreground = foreground;
                    continue;
                case 39:
                    attributes.Foreground = TermColor.Default;
                    break;
                case >= 40 and <= 47:
                    attributes.Background = TermColor.Indexed(code - 40);
                    break;
                case 48:
                    i += ParseExtendedColor(i, subCount, out var background);
                    attributes.Background = background;
                    continue;
                case 49:
                    attributes.Background = TermColor.Default;
                    break;
                case >= 90 and <= 97:
                    attributes.Foreground = TermColor.Indexed(code - 90 + 8);
                    break;
                case >= 100 and <= 107:
                    attributes.Background = TermColor.Indexed(code - 100 + 8);
                    break;
            }

            i += 1 + subCount;
        }

        _screen.SetAttributes(attributes);
    }

    /// <summary>
    /// 38/48 followed by 5;n or 2;r;g;b, with either ';' or ':' separators (the colon form may carry a color-space
    /// id: 38:2::r:g:b). Returns the number of parameters consumed, including the 38/48 itself.
    /// </summary>
    private int ParseExtendedColor(int index, int subCount, out TermColor color)
    {
        color = TermColor.Default;
        var available = subCount > 0 ? subCount : _paramCount - index - 1;
        if (available < 1)
            return 1;

        var kind = _params[index + 1];
        if (kind == 5 && available >= 2)
        {
            color = TermColor.Indexed(_params[index + 2]);
            return subCount > 0 ? 1 + subCount : 3;
        }

        if (kind == 2)
        {
            if (subCount > 0 && subCount >= 5)
            {
                // 38:2:<colorspace>:r:g:b
                color = TermColor.FromRgb(_params[index + 3], _params[index + 4], _params[index + 5]);
                return 1 + subCount;
            }

            if (available >= 4)
            {
                color = TermColor.FromRgb(_params[index + 2], _params[index + 3], _params[index + 4]);
                return subCount > 0 ? 1 + subCount : 5;
            }
        }

        return subCount > 0 ? 1 + subCount : 2;
    }

    // ---------------------------------------------------------------- OSC

    private void DispatchOsc()
    {
        _state = State.Ground;
        var text = _osc.ToString();
        _osc.Clear();
        var separator = text.IndexOf(';');
        if (separator < 0)
            return;

        if (!int.TryParse(text.AsSpan(0, separator), out var command))
            return;

        var argument = text[(separator + 1)..];
        switch (command)
        {
            case 0:
            case 2:
                _screen.SetTitle(argument);
                break;
        }
    }
}
