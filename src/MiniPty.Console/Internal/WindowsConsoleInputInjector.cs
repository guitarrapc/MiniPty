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
    /// Leaves the console input queue empty with no orphaned read. After a blocked <c>ReadFile</c> is cancelled
    /// with <c>CancelIoEx</c>, conhost keeps that read registered and it silently consumes the next queued
    /// record. A throwaway key that yields a byte completes such a read; if none exists, the flush removes it.
    /// </summary>
    internal static void ResetInputQueue()
    {
        InjectUnicodeKeyDown('x');
        if (!ConsoleWindowsInterop.FlushConsoleInputBuffer(
                ConsoleWindowsInterop.GetStdHandle(ConsoleWindowsInterop.StdInputHandle)))
            throw new InvalidOperationException("FlushConsoleInputBuffer failed.");
    }
}
