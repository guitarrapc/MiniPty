using System.Runtime.InteropServices;

namespace MiniPty.Console.Internal;

internal sealed class UnixHostTerminal : IHostTerminal
{
    private unsafe ConsoleUnixInterop.TermiosBlob* _stdinSnapshot;
    private unsafe ConsoleUnixInterop.TermiosBlob* _stdoutSnapshot;
    private int _lastColumns;
    private int _lastRows;
    private bool _disposed;

    internal static bool IsInteractiveHost()
    {
        return ConsoleUnixInterop.minipty_console_isatty(ConsoleUnixInterop.StdinFileno) != 0
            && ConsoleUnixInterop.minipty_console_isatty(ConsoleUnixInterop.StdoutFileno) != 0;
    }

    public unsafe UnixHostTerminal()
    {
        _stdinSnapshot = (ConsoleUnixInterop.TermiosBlob*)NativeMemory.Alloc(
            (nuint)sizeof(ConsoleUnixInterop.TermiosBlob));
        _stdoutSnapshot = (ConsoleUnixInterop.TermiosBlob*)NativeMemory.Alloc(
            (nuint)sizeof(ConsoleUnixInterop.TermiosBlob));

        var stdinSaved = false;
        var stdoutSaved = false;
        var stdinRaw = false;
        try
        {
            if (ConsoleUnixInterop.minipty_console_termios_save(ConsoleUnixInterop.StdinFileno, _stdinSnapshot) != 0)
                throw new InvalidOperationException("Failed to configure the host terminal.");
            stdinSaved = true;

            if (ConsoleUnixInterop.minipty_console_termios_save(ConsoleUnixInterop.StdoutFileno, _stdoutSnapshot) != 0)
                throw new InvalidOperationException("Failed to configure the host terminal.");
            stdoutSaved = true;

            if (ConsoleUnixInterop.minipty_console_termios_set_raw_input(ConsoleUnixInterop.StdinFileno) != 0)
                throw new InvalidOperationException("Failed to configure the host terminal.");
            stdinRaw = true;

            if (ConsoleUnixInterop.minipty_console_termios_set_raw_output(ConsoleUnixInterop.StdoutFileno) != 0)
                throw new InvalidOperationException("Failed to configure the host terminal.");
        }
        catch
        {
            if (stdinRaw && stdinSaved)
                ConsoleUnixInterop.minipty_console_termios_restore(ConsoleUnixInterop.StdinFileno, _stdinSnapshot);

            if (stdoutSaved)
                ConsoleUnixInterop.minipty_console_termios_restore(ConsoleUnixInterop.StdoutFileno, _stdoutSnapshot);

            NativeMemory.Free(_stdinSnapshot);
            NativeMemory.Free(_stdoutSnapshot);
            _stdinSnapshot = null;
            _stdoutSnapshot = null;
            throw;
        }

        if (TryGetSize(out var columns, out var rows))
        {
            _lastColumns = columns;
            _lastRows = rows;
        }
    }

    public bool TryGetSize(out int columns, out int rows)
    {
        if (ConsoleUnixInterop.minipty_console_get_winsize(
                ConsoleUnixInterop.StdinFileno,
                out var rowCount,
                out var colCount) != 0)
        {
            columns = 0;
            rows = 0;
            return false;
        }

        columns = colCount;
        rows = rowCount;
        return columns > 0 && rows > 0;
    }

    public unsafe int ReadInput(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
            return 0;

        fixed (byte* ptr = buffer)
        {
            int read;
            do
            {
                read = ConsoleUnixInterop.Read(ConsoleUnixInterop.StdinFileno, ptr, (nuint)buffer.Length);
            }
            while (read < 0 && Marshal.GetLastPInvokeError() == ConsoleUnixInterop.EINTR);

            if (read < 0)
                return 0;

            return read;
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

    public unsafe void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        ConsoleUnixInterop.minipty_console_termios_restore(ConsoleUnixInterop.StdinFileno, _stdinSnapshot);
        ConsoleUnixInterop.minipty_console_termios_restore(ConsoleUnixInterop.StdoutFileno, _stdoutSnapshot);
        NativeMemory.Free(_stdinSnapshot);
        NativeMemory.Free(_stdoutSnapshot);
        _stdinSnapshot = null;
        _stdoutSnapshot = null;
    }
}
