namespace MiniPty.Console.Internal;

/// <summary>Test and diagnostic helper for injecting host console key events on Windows.</summary>
internal static class WindowsConsoleInputInjector
{
    internal static unsafe void InjectUnicodeKeyDown(char ch)
    {
        var stdin = ConsoleWindowsInterop.GetStdHandle(ConsoleWindowsInterop.StdInputHandle);
        var record = new ConsoleWindowsInterop.InputRecord
        {
            EventType = ConsoleWindowsInterop.KeyEvent,
            KeyEvent = new ConsoleWindowsInterop.KeyEventRecord
            {
                KeyDown = true,
                RepeatCount = 1,
                UnicodeChar = (ushort)ch,
            },
        };

        if (!ConsoleWindowsInterop.WriteConsoleInput(stdin, &record, 1, out _))
            throw new InvalidOperationException("WriteConsoleInput failed.");
    }

    internal static unsafe void InjectUtf8Byte(byte value)
    {
        var stdin = ConsoleWindowsInterop.GetStdHandle(ConsoleWindowsInterop.StdInputHandle);
        Span<byte> span = stackalloc byte[1] { value };
        fixed (byte* ptr = span)
        {
            if (!ConsoleWindowsInterop.WriteFile(stdin, ptr, 1, out _, IntPtr.Zero))
                throw new InvalidOperationException("WriteFile failed.");
        }
    }
}
