namespace MiniPty.Console.Internal;

internal sealed class WindowsHostTerminal : IHostTerminal
{
    private readonly IntPtr _stdin;
    private readonly IntPtr _stdout;
    private readonly uint _stdinModeSnapshot;
    private readonly uint _stdoutModeSnapshot;
    private int _lastColumns;
    private int _lastRows;
    private bool _disposed;

    internal static bool IsInteractiveHost()
    {
        var stdin = ConsoleWindowsInterop.GetStdHandle(ConsoleWindowsInterop.StdInputHandle);
        var stdout = ConsoleWindowsInterop.GetStdHandle(ConsoleWindowsInterop.StdOutputHandle);
        return ConsoleWindowsInterop.GetConsoleMode(stdin, out _)
            && ConsoleWindowsInterop.GetConsoleMode(stdout, out _);
    }

    public WindowsHostTerminal()
    {
        _stdin = ConsoleWindowsInterop.GetStdHandle(ConsoleWindowsInterop.StdInputHandle);
        _stdout = ConsoleWindowsInterop.GetStdHandle(ConsoleWindowsInterop.StdOutputHandle);

        if (!ConsoleWindowsInterop.GetConsoleMode(_stdin, out _stdinModeSnapshot)
            || !ConsoleWindowsInterop.GetConsoleMode(_stdout, out _stdoutModeSnapshot))
        {
            throw new InvalidOperationException("Failed to read the host console mode.");
        }

        uint stdinMode = _stdinModeSnapshot;
        stdinMode &= ~(ConsoleWindowsInterop.EnableEchoInput
            | ConsoleWindowsInterop.EnableLineInput
            | ConsoleWindowsInterop.EnableProcessedInput);
        stdinMode |= ConsoleWindowsInterop.EnableWindowInput;

        uint stdoutMode = _stdoutModeSnapshot;
        stdoutMode |= ConsoleWindowsInterop.EnableVirtualTerminalProcessing;

        if (!ConsoleWindowsInterop.SetConsoleMode(_stdin, stdinMode))
        {
            throw new InvalidOperationException("Failed to configure the host terminal.");
        }

        if (!ConsoleWindowsInterop.SetConsoleMode(_stdout, stdoutMode))
        {
            ConsoleWindowsInterop.SetConsoleMode(_stdin, _stdinModeSnapshot);
            throw new InvalidOperationException("Failed to configure the host terminal.");
        }

        if (TryGetSize(out var columns, out var rows))
        {
            _lastColumns = columns;
            _lastRows = rows;
        }
    }

    public bool TryGetSize(out int columns, out int rows)
    {
        if (!ConsoleWindowsInterop.GetConsoleScreenBufferInfo(_stdout, out var info))
        {
            columns = 0;
            rows = 0;
            return false;
        }

        columns = info.Window.Right - info.Window.Left + 1;
        rows = info.Window.Bottom - info.Window.Top + 1;
        return columns > 0 && rows > 0;
    }

    public unsafe int ReadInput(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
            return 0;

        var wait = ConsoleWindowsInterop.WaitForSingleObject(_stdin, 100);
        if (wait == ConsoleWindowsInterop.WaitTimeout)
            return 0;

        if (wait != ConsoleWindowsInterop.WaitObject0)
            return 0;

        ConsoleWindowsInterop.InputRecord record;
        while (true)
        {
            if (!ConsoleWindowsInterop.ReadConsoleInput(_stdin, &record, 1, out var read) || read == 0)
                return 0;

            if (record.EventType == ConsoleWindowsInterop.WindowBufferSizeEvent)
                continue;

            if (record.EventType != ConsoleWindowsInterop.KeyEvent)
                continue;

            var encoded = WindowsConsoleInputEncoder.EncodeKeyEvent(record.KeyEvent, buffer);
            if (encoded > 0)
                return encoded;
        }
    }

    public bool TryPollResize(out int columns, out int rows)
    {
        if (!TryGetSize(out columns, out rows))
            return false;

        if (columns == _lastColumns && rows == _lastRows)
            return false;

        _lastColumns = columns;
        _lastRows = rows;
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        ConsoleWindowsInterop.SetConsoleMode(_stdin, _stdinModeSnapshot);
        ConsoleWindowsInterop.SetConsoleMode(_stdout, _stdoutModeSnapshot);
    }
}
