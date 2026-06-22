using System.Buffers;

namespace MiniPty.Internal;

internal static class PtyDisplayTextStripper
{
    private const char Esc = '\x1b';
    private const char Bell = '\a';

    internal static string Strip(ReadOnlySpan<char> text, PtyOutputDisplayMode mode)
    {
        if (text.IsEmpty)
            return string.Empty;

        var pool = ArrayPool<char>.Shared;
        var buffer = pool.Rent(text.Length);
        try
        {
            var written = StripTo(buffer, text, mode);
            return new string(buffer, 0, written);
        }
        finally
        {
            pool.Return(buffer);
        }
    }

    private static int StripTo(Span<char> destination, ReadOnlySpan<char> text, PtyOutputDisplayMode mode)
    {
        var written = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == Bell)
                continue;

            if (ch != Esc)
            {
                if (ch == '\r')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                        i++;

                    destination[written++] = '\n';
                }
                else
                {
                    destination[written++] = ch;
                }

                continue;
            }

            if (i + 1 >= text.Length)
            {
                written = AppendNormalized(destination, written, Esc);
                break;
            }

            var next = text[i + 1];
            if (next == '[')
            {
                var end = FindCsiEnd(text, i + 2);
                if (end < 0)
                {
                    written = AppendNormalized(destination, written, text[i..]);
                    break;
                }

                if (mode == PtyOutputDisplayMode.AnsiText && text[end] == 'm')
                    written = AppendNormalized(destination, written, text[i..(end + 1)]);

                i = end;
                continue;
            }

            if (next == ']')
            {
                var end = FindOscEnd(text, i + 2);
                if (end < 0)
                {
                    written = AppendNormalized(destination, written, text[i..]);
                    break;
                }

                i = end;
                continue;
            }

            written = AppendNormalized(destination, written, ch);
        }

        return written;
    }

    private static int AppendNormalized(Span<char> destination, int written, char ch)
    {
        destination[written++] = ch;
        return written;
    }

    private static int AppendNormalized(Span<char> destination, int written, ReadOnlySpan<char> text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch != '\r')
            {
                destination[written++] = ch;
                continue;
            }

            if (i + 1 < text.Length && text[i + 1] == '\n')
                i++;

            destination[written++] = '\n';
        }

        return written;
    }

    private static int FindCsiEnd(ReadOnlySpan<char> text, int start)
    {
        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch is >= '@' and <= '~')
                return i;
        }

        return -1;
    }

    private static int FindOscEnd(ReadOnlySpan<char> text, int start)
    {
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == Bell)
                return i;

            if (text[i] == Esc && i + 1 < text.Length && text[i + 1] == '\\')
                return i + 1;
        }

        return -1;
    }
}
