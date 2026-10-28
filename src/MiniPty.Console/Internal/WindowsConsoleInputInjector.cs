namespace MiniPty.Console.Internal;

/// <summary>Test and diagnostic helper for injecting host console key events on Windows.</summary>
/// <remarks>
/// Injection goes through <c>WriteConsoleInput</c>. <c>WriteFile</c> on the console input handle fails with
/// <c>ERROR_INVALID_HANDLE</c> on conhost, so raw UTF-8 bytes cannot be injected; non-ASCII text is injected as
/// Unicode key events and the console encodes it with the input code page when it is read.
/// </remarks>
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
                KeyDown = 1,
                RepeatCount = 1,
                UnicodeChar = ch,
            },
        };

        if (!ConsoleWindowsInterop.WriteConsoleInput(stdin, &record, 1, out _))
            throw new InvalidOperationException("WriteConsoleInput failed.");
    }

    /// <summary>
    /// Empties the console input queue. It deliberately does not absorb an orphaned read (a <c>ReadFile</c>
    /// cancelled with <c>CancelIoEx</c> stays registered in conhost and swallows the next record): the host
    /// terminal must never leave one behind, and a test that follows would fail if it did.
    /// </summary>
    internal static void ResetInputQueue()
    {
        if (!ConsoleWindowsInterop.FlushConsoleInputBuffer(
                ConsoleWindowsInterop.GetStdHandle(ConsoleWindowsInterop.StdInputHandle)))
            throw new InvalidOperationException("FlushConsoleInputBuffer failed.");
    }
}
