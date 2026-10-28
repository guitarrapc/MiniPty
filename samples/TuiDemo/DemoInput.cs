using System.ComponentModel;
using System.Runtime.InteropServices;

internal enum DemoCommand
{
    None,
    Up,
    Down,
    Left,
    Right,
    PageUp,
    PageDown,
    Home,
    End,
    Details,
    Pause,
    Backspace,
    ClearText,
    NextTab,
    PrevTab,
    /// <summary>Switch to the page in <see cref="DemoEvent.Column"/> (0-based).</summary>
    Page,
    Quit,
    /// <summary>A left-button press or a wheel step at <see cref="DemoEvent.Column"/>, <see cref="DemoEvent.Row"/>.</summary>
    Mouse,
    /// <summary>Motion with the left button held (button-event tracking, DECSET 1002).</summary>
    Drag,
    /// <summary>The left button was released.</summary>
    Release,
}

/// <summary>
/// A decoded input. <paramref name="Character"/> is set for every printable character, shortcut or not, so a text box
/// can take "q" or "1" while the rest of the screen reads them as commands.
/// </summary>
internal readonly record struct DemoEvent(DemoCommand Command, int Column = 0, int Row = 0, int Button = 0, char Character = '\0');

/// <summary>
/// A bounded, incremental decoder for the few VT input sequences this demo uses. It accepts split SGR mouse reports,
/// CSI and SS3 cursor keys (including modified forms such as <c>ESC [ 1 ; 5 A</c> and <c>ESC [ 5 ~</c>), and native
/// console keys without allocating per event. Other terminal protocols are ignored.
/// </summary>
internal struct DemoInputParser
{
    private int _state;
    private int _field;
    private int _number;
    private int _button;
    private int _column;
    private bool _digits;

    internal DemoEvent Feed(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.UpArrow: return new(DemoCommand.Up);
            case ConsoleKey.DownArrow: return new(DemoCommand.Down);
            case ConsoleKey.LeftArrow: return new(DemoCommand.Left);
            case ConsoleKey.RightArrow: return new(DemoCommand.Right);
            case ConsoleKey.PageUp: return new(DemoCommand.PageUp);
            case ConsoleKey.PageDown: return new(DemoCommand.PageDown);
            case ConsoleKey.Home: return new(DemoCommand.Home);
            case ConsoleKey.End: return new(DemoCommand.End);
            case ConsoleKey.Tab: return new((key.Modifiers & ConsoleModifiers.Shift) != 0 ? DemoCommand.PrevTab : DemoCommand.NextTab);
        }
        // .NET's Unix key parser can represent an isolated ESC+[ / ESC+O fragment as Alt+[ / Alt+O.
        // Reconstruct the prefix so a split mouse/arrow report still reaches our incremental decoder.
        if ((key.Modifiers & ConsoleModifiers.Alt) != 0 && key.KeyChar is '[' or 'O')
            Feed('\u001b');
        return Feed(key.KeyChar);
    }

    internal DemoEvent Feed(char value)
    {
        if (value == '\u0003')
        {
            _state = 0;
            return new(DemoCommand.Quit);
        }
        if (value == '\u001b')
        {
            _state = 1;
            return default;
        }
        switch (_state)
        {
            case 0:
                var character = value >= ' ' && value != '\u007f' ? value : '\0';
                return value switch
                {
                    'q' or 'Q' => new(DemoCommand.Quit, Character: character),
                    'k' or 'K' => new(DemoCommand.Up, Character: character),
                    'j' or 'J' => new(DemoCommand.Down, Character: character),
                    '\r' or '\n' => new(DemoCommand.Details),
                    ' ' or 'p' or 'P' => new(DemoCommand.Pause, Character: character),
                    '\t' => new(DemoCommand.NextTab),
                    '\b' or '\u007f' => new(DemoCommand.Backspace),
                    '\u0015' => new(DemoCommand.ClearText),
                    >= '1' and <= '3' => new(DemoCommand.Page, value - '1', Character: character),
                    _ => new(DemoCommand.None, Character: character),
                };
            case 1:
                _state = value is '[' or 'O' ? 2 : 0;
                return default;
            case 2:
                _state = 0;
                if (value == '<')
                {
                    _state = 3;
                    _field = _number = 0;
                    _digits = false;
                    return default;
                }
                if (value is >= '0' and <= '9')
                {
                    _state = 5;
                    _number = value - '0';
                    _field = 0;
                    return default;
                }
                if (value is < '@' or > '~')
                    _state = 4;
                return Final(value, 0);
            case 3:
                if (value is >= '0' and <= '9')
                {
                    _number = _number * 10 + value - '0';
                    _digits = true;
                    if (_number > 65535)
                        _state = 4; // Discard an oversized report through its final byte; never reinterpret it as keys.
                    return default;
                }
                if (_digits && value == ';' && _field < 2)
                {
                    if (_field == 0) _button = _number;
                    else _column = _number;
                    _field++;
                    _number = 0;
                    _digits = false;
                    return default;
                }
                _state = value is >= '@' and <= '~' ? 0 : 4;
                // Release, motion, modifier-clicks, and invalid/zero coordinates must not activate controls.
                if (_digits && _field == 2 && value == 'M' && _column > 0 && _number > 0 && _button is 0 or 64 or 65)
                    return new(DemoCommand.Mouse, _column, _number, _button);
                // Left-button motion (32) and the left release ('m'); other motion and buttons stay ignored.
                if (_digits && _field == 2 && value == 'M' && _column > 0 && _number > 0 && _button == 32)
                    return new(DemoCommand.Drag, _column, _number, _button);
                if (_digits && _field == 2 && value == 'm' && _column > 0 && _number > 0 && _button == 0)
                    return new(DemoCommand.Release, _column, _number, _button);
                return default;
            case 5:
                // CSI parameters: keep the first number (the key in "ESC [ 5 ~"), skip modifiers after ';'.
                if (value is >= '0' and <= '9')
                {
                    if (_field == 0)
                        _number = _number * 10 + value - '0';
                    if (_number > 65535)
                        _state = 4;
                    return default;
                }
                if (value == ';')
                {
                    _field = 1;
                    return default;
                }
                _state = value is >= '@' and <= '~' ? 0 : 4;
                return _state == 0 ? Final(value, _number) : default;
            default:
                if (value is >= '@' and <= '~')
                    _state = 0;
                return default;
        }
    }

    private static DemoEvent Final(char value, int number) => new(value switch
    {
        'A' => DemoCommand.Up,
        'B' => DemoCommand.Down,
        'C' => DemoCommand.Right,
        'D' => DemoCommand.Left,
        'H' => DemoCommand.Home,
        'F' => DemoCommand.End,
        'Z' => DemoCommand.PrevTab,
        '~' => number switch
        {
            1 or 7 => DemoCommand.Home,
            4 or 8 => DemoCommand.End,
            5 => DemoCommand.PageUp,
            6 => DemoCommand.PageDown,
            _ => DemoCommand.None,
        },
        _ => DemoCommand.None,
    });
}


/// <summary>
/// Windows console mouse input is native INPUT_RECORD data, which Console.ReadKey discards. Read those
/// records explicitly, while also accepting literal VT sequences from nested consoles. Unix ReadKey
/// supplies terminal keys/characters to the same parser. All reads and mode changes stay on the TUI thread.
/// </summary>
internal sealed partial class DemoInput : IDisposable
{
    private const uint EnableVirtualTerminalInput = 0x0200;
    private const uint EnableMouseInput = 0x0010;
    private const uint EnableExtendedFlags = 0x0080;
    private const uint ShiftPressed = 0x0010;
    private const uint DisableInputModes = 0x0001 | 0x0002 | 0x0004 | 0x0040; // Processed, line, echo, Quick Edit.
    private readonly nint _handle;
    private readonly uint _savedMode;
    private DemoInputParser _parser;

    internal DemoInput()
    {
        if (!OperatingSystem.IsWindows())
            return;
        _handle = GetStdHandle(-10);
        if (!GetConsoleMode(_handle, out _savedMode)
            || !SetConsoleMode(_handle, (_savedMode | EnableVirtualTerminalInput | EnableMouseInput | EnableExtendedFlags) & ~DisableInputModes))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    internal bool TryRead(out DemoEvent input)
    {
        input = default;
        if (OperatingSystem.IsWindows())
        {
            if (!PeekConsoleInput(_handle, out var record, 1, out var count))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            if (count == 0)
                return false;
            if (!ReadConsoleInput(_handle, out record, 1, out count))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            if (record.Type == 2 && record.Mouse.Flags == 0 && record.Mouse.Buttons == 1 && (record.Mouse.Modifiers & 0x1f) == 0)
                input = new(DemoCommand.Mouse, record.Mouse.Position.X + 1, record.Mouse.Position.Y + 1);
            else if (record.Type == 2 && record.Mouse.Flags == 4 && (record.Mouse.Modifiers & 0x1f) == 0)
                input = new(DemoCommand.Mouse, record.Mouse.Position.X + 1, record.Mouse.Position.Y + 1,
                    (short)(record.Mouse.Buttons >> 16) > 0 ? 64 : 65);
            else if (record.Type == 2 && record.Mouse.Flags == 1 && record.Mouse.Buttons == 1)
                input = new(DemoCommand.Drag, record.Mouse.Position.X + 1, record.Mouse.Position.Y + 1, 32);
            else if (record.Type == 2 && record.Mouse.Flags == 0 && record.Mouse.Buttons == 0)
                input = new(DemoCommand.Release, record.Mouse.Position.X + 1, record.Mouse.Position.Y + 1);
            else if (record.Type == 1 && record.Key.Down != 0 && record.Key.Character == '\t' && (record.Key.Modifiers & ShiftPressed) != 0)
                input = new(DemoCommand.PrevTab);
            else if (record.Type == 1 && record.Key.Down != 0)
                input = record.Key.Character != 0 ? _parser.Feed((char)record.Key.Character)
                    : new(record.Key.VirtualKey switch
                    {
                        0x26 => DemoCommand.Up,
                        0x28 => DemoCommand.Down,
                        0x25 => DemoCommand.Left,
                        0x27 => DemoCommand.Right,
                        0x21 => DemoCommand.PageUp,
                        0x22 => DemoCommand.PageDown,
                        0x24 => DemoCommand.Home,
                        0x23 => DemoCommand.End,
                        _ => DemoCommand.None,
                    });
            return true;
        }

        if (!Console.KeyAvailable)
            return false;
        var key = Console.ReadKey(intercept: true);
        input = _parser.Feed(key);
        return true;
    }

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
            SetConsoleMode(_handle, _savedMode);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord { public short X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyRecord { public int Down; public ushort Repeat, VirtualKey, ScanCode, Character; public uint Modifiers; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseRecord { public Coord Position; public uint Buttons, Modifiers, Flags; }
    [StructLayout(LayoutKind.Explicit, Size = 20)]
    private struct InputRecord
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(4)] public KeyRecord Key;
        [FieldOffset(4)] public MouseRecord Mouse;
    }

    [LibraryImport("kernel32.dll")] private static partial nint GetStdHandle(int handle);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetConsoleMode(nint handle, out uint mode);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetConsoleMode(nint handle, uint mode);
    [LibraryImport("kernel32.dll", EntryPoint = "PeekConsoleInputW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool PeekConsoleInput(nint handle, out InputRecord record, uint length, out uint count);
    [LibraryImport("kernel32.dll", EntryPoint = "ReadConsoleInputW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool ReadConsoleInput(nint handle, out InputRecord record, uint length, out uint count);
}
