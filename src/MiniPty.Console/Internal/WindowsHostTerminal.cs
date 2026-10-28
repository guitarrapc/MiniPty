using System.Buffers;
using System.Runtime.InteropServices;

namespace MiniPty.Console.Internal;

internal sealed class WindowsHostTerminal : IHostTerminal
{
    private const int ReadIdle = 0;
    private const int ReadPending = 1;
    private const int ReadWakeRequested = 2;

    /// <summary>Private-use character carried by the sentinel record that wakes a blocked read.</summary>
    private const char WakeChar = '';

    private readonly IntPtr _stdin;
    private readonly IntPtr _stdout;
    private readonly uint _stdinModeSnapshot;
    private readonly uint _stdoutModeSnapshot;
    private readonly uint _inputCodePageSnapshot;
    private int _lastColumns;
    private int _lastRows;
    private bool _disposed;
    private int _readState;

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
        stdinMode |= ConsoleWindowsInterop.EnableWindowInput
            | ConsoleWindowsInterop.EnableVirtualTerminalInput;

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

        // ReadFile converts console input to the input code page, and the bytes are forwarded unchanged to a
        // UTF-8 PTY. Under a legacy code page (for example 932) non-ASCII keys would arrive as Shift-JIS bytes
        // or '?', so input is read as UTF-8 while attached and the previous code page is restored on detach.
        _inputCodePageSnapshot = ConsoleWindowsInterop.GetConsoleCP();
        if (_inputCodePageSnapshot != ConsoleWindowsInterop.Utf8CodePage
            && !ConsoleWindowsInterop.SetConsoleCP(ConsoleWindowsInterop.Utf8CodePage))
        {
            ConsoleWindowsInterop.SetConsoleMode(_stdin, _stdinModeSnapshot);
            ConsoleWindowsInterop.SetConsoleMode(_stdout, _stdoutModeSnapshot);
            throw new InvalidOperationException("Failed to configure the host terminal.");
        }

        if (TryGetSize(out var columns, out var rows))
        {
            _lastColumns = columns;
            _lastRows = rows;
        }
    }

    /// <summary>
    /// Interrupts a <see cref="ReadInput"/> blocked in <c>ReadFile</c>. The read is woken with a sentinel key
    /// record rather than <c>CancelIoEx</c>: conhost keeps a cancelled console read registered, and that orphaned
    /// read silently consumes the next input record, so the first key after detach would be lost. The woken read
    /// returns 0 and removes any sentinel it did not consume. Only one sentinel is written per pending read.
    /// </summary>
    internal void CancelPendingRead()
    {
        if (Interlocked.CompareExchange(ref _readState, ReadWakeRequested, ReadPending) != ReadPending)
            return;

        if (!TryWriteWakeRecord())
            ConsoleWindowsInterop.CancelIoEx(_stdin, IntPtr.Zero);
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

    public int ReadInput(Span<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
            return 0;

        ObjectDisposedException.ThrowIf(_disposed, this);

        // Publish the pending read before checking cancellation: a concurrent canceller either observes it and
        // wakes the read, or cancelled first and the check below skips ReadFile.
        Interlocked.Exchange(ref _readState, ReadPending);
        int read;
        bool woken;
        try
        {
            var registration = cancellationToken.CanBeCanceled
                ? cancellationToken.UnsafeRegister(static state => ((WindowsHostTerminal)state!).CancelPendingRead(), this)
                : default;
            try
            {
                read = cancellationToken.IsCancellationRequested ? 0 : ReadInputCore(buffer);
            }
            finally
            {
                // Waits for a running callback, so its sentinel is already queued when the state is read below.
                registration.Dispose();
            }
        }
        finally
        {
            woken = Interlocked.Exchange(ref _readState, ReadIdle) == ReadWakeRequested;
        }

        if (!woken)
            return read;

        // The bytes include the sentinel; any real input read with it is dropped with the read being cancelled.
        DiscardWakeRecords();
        return 0;
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
        CancelPendingRead();
        // With no reader left (attach joins its input thread first), drop a sentinel nobody consumed. A reader
        // still running removes its own sentinel when it wakes.
        if (Volatile.Read(ref _readState) == ReadIdle)
            DiscardWakeRecords();
        ConsoleWindowsInterop.SetConsoleMode(_stdin, _stdinModeSnapshot);
        ConsoleWindowsInterop.SetConsoleMode(_stdout, _stdoutModeSnapshot);
        if (_inputCodePageSnapshot != 0 && _inputCodePageSnapshot != ConsoleWindowsInterop.Utf8CodePage)
            ConsoleWindowsInterop.SetConsoleCP(_inputCodePageSnapshot);
    }

    private unsafe bool TryWriteWakeRecord()
    {
        var record = new ConsoleWindowsInterop.InputRecord
        {
            EventType = ConsoleWindowsInterop.KeyEvent,
            KeyEvent = new ConsoleWindowsInterop.KeyEventRecord
            {
                KeyDown = 1,
                RepeatCount = 1,
                UnicodeChar = WakeChar,
            },
        };

        return ConsoleWindowsInterop.WriteConsoleInput(_stdin, &record, 1, out var written) && written == 1;
    }

    /// <summary>
    /// Removes sentinel records left in the input queue, keeping every other record in order. A sentinel stays
    /// queued when the read it targeted returned before consuming it. Must run with no concurrent reader; input
    /// arriving during the few calls here may be queued ahead of the records written back.
    /// </summary>
    private unsafe void DiscardWakeRecords()
    {
        if (!ConsoleWindowsInterop.GetNumberOfConsoleInputEvents(_stdin, out var count) || count == 0)
            return;

        const int StackRecords = 16;
        ConsoleWindowsInterop.InputRecord[]? rented = null;
        Span<ConsoleWindowsInterop.InputRecord> records = count <= StackRecords
            ? stackalloc ConsoleWindowsInterop.InputRecord[StackRecords]
            : (rented = ArrayPool<ConsoleWindowsInterop.InputRecord>.Shared.Rent((int)count));
        try
        {
            fixed (ConsoleWindowsInterop.InputRecord* pointer = records)
            {
                if (!ConsoleWindowsInterop.PeekConsoleInput(_stdin, pointer, count, out var peeked) || peeked == 0)
                    return;

                var found = false;
                for (var i = 0; i < peeked && !found; i++)
                    found = IsWakeRecord(pointer[i]);
                if (!found)
                    return;

                // Take exactly the peeked records (nothing else reads concurrently), then put back the others.
                if (!ConsoleWindowsInterop.ReadConsoleInput(_stdin, pointer, peeked, out var taken) || taken == 0)
                    return;

                var kept = 0;
                for (var i = 0; i < taken; i++)
                {
                    if (!IsWakeRecord(pointer[i]))
                        pointer[kept++] = pointer[i];
                }

                if (kept > 0)
                    ConsoleWindowsInterop.WriteConsoleInput(_stdin, pointer, (uint)kept, out _);
            }
        }
        finally
        {
            if (rented is not null)
                ArrayPool<ConsoleWindowsInterop.InputRecord>.Shared.Return(rented);
        }
    }

    // conhost zeroes an injected record's scan code, so the sentinel is identified by key-down, no virtual key,
    // and a private-use character that a keyboard does not produce.
    private static bool IsWakeRecord(in ConsoleWindowsInterop.InputRecord record) =>
        record.EventType == ConsoleWindowsInterop.KeyEvent
        && record.KeyEvent.KeyDown != 0
        && record.KeyEvent.VirtualKeyCode == 0
        && record.KeyEvent.UnicodeChar == WakeChar;

    private unsafe int ReadInputCore(Span<byte> buffer)
    {
        // Let the console encode the entire input stream according to its negotiated VT modes. Mixing
        // ReadConsoleInput with ReadFile can discard queued mouse records or consume part of a VT report.
        fixed (byte* ptr = buffer)
        {
            if (!ConsoleWindowsInterop.ReadFile(_stdin, ptr, (uint)buffer.Length, out var read, IntPtr.Zero))
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == ConsoleWindowsInterop.ErrorOperationAborted)
                    return 0;

                throw new IOException($"ReadFile failed (Win32 {error})");
            }

            return read > 0 ? (int)read : 0;
        }
    }
}
