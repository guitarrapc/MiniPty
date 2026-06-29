using System.Text;

namespace MiniPty.Console.Internal;

internal static class WindowsConsoleInputEncoder
{
    internal static int EncodeKeyEvent(
        in ConsoleWindowsInterop.KeyEventRecord keyEvent,
        Span<byte> destination)
    {
        if (!keyEvent.KeyDown)
            return 0;

        if (keyEvent.UnicodeChar != 0)
            return EncodeUnicodeChar(keyEvent.UnicodeChar, destination);

        return keyEvent.VirtualKeyCode switch
        {
            ConsoleWindowsInterop.VkUp => WriteSequence(destination, "\x1b[A"u8),
            ConsoleWindowsInterop.VkDown => WriteSequence(destination, "\x1b[B"u8),
            ConsoleWindowsInterop.VkRight => WriteSequence(destination, "\x1b[C"u8),
            ConsoleWindowsInterop.VkLeft => WriteSequence(destination, "\x1b[D"u8),
            ConsoleWindowsInterop.VkHome => WriteSequence(destination, "\x1b[H"u8),
            ConsoleWindowsInterop.VkEnd => WriteSequence(destination, "\x1b[F"u8),
            ConsoleWindowsInterop.VkInsert => WriteSequence(destination, "\x1b[2~"u8),
            ConsoleWindowsInterop.VkDelete => WriteSequence(destination, "\x1b[3~"u8),
            ConsoleWindowsInterop.VkPrior => WriteSequence(destination, "\x1b[5~"u8),
            ConsoleWindowsInterop.VkNext => WriteSequence(destination, "\x1b[6~"u8),
            _ => 0,
        };
    }

    private static int EncodeUnicodeChar(ushort unicodeChar, Span<byte> destination)
    {
        Span<char> chars = stackalloc char[1];
        chars[0] = (char)unicodeChar;
        return Encoding.UTF8.GetBytes(chars, destination);
    }

    private static int WriteSequence(Span<byte> destination, ReadOnlySpan<byte> sequence)
    {
        if (destination.Length < sequence.Length)
            return 0;

        sequence.CopyTo(destination);
        return sequence.Length;
    }
}
