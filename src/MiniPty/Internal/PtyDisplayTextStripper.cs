namespace MiniPty.Internal;

internal static class PtyDisplayTextStripper
{
    private const char Esc = '\x1b';
    private const char Bell = '\a';

    internal static string Strip(string text, PtyOutputDisplayMode mode)
    {
        if (text.Length == 0)
            return text;

        var builder = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == Bell)
                continue;

            if (ch != Esc)
            {
                builder.Append(ch);
                continue;
            }

            if (i + 1 >= text.Length)
            {
                builder.Append(Esc);
                break;
            }

            var next = text[i + 1];
            if (next == '[')
            {
                var end = FindCsiEnd(text, i + 2);
                if (end < 0)
                {
                    builder.Append(text.AsSpan(i));
                    break;
                }

                if (mode == PtyOutputDisplayMode.AnsiText && text[end] == 'm')
                    builder.Append(text.AsSpan(i, end - i + 1));

                i = end;
                continue;
            }

            if (next == ']')
            {
                var end = FindOscEnd(text, i + 2);
                if (end < 0)
                {
                    builder.Append(text.AsSpan(i));
                    break;
                }

                i = end;
                continue;
            }

            builder.Append(ch);
        }

        return NormalizeNewlines(builder.ToString());
    }

    private static int FindCsiEnd(string text, int start)
    {
        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch is >= '@' and <= '~')
                return i;
        }

        return -1;
    }

    private static int FindOscEnd(string text, int start)
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

    private static string NormalizeNewlines(string text)
    {
        if (text.Length == 0)
            return text;

        var builder = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch != '\r')
            {
                builder.Append(ch);
                continue;
            }

            if (i + 1 < text.Length && text[i + 1] == '\n')
                i++;

            builder.Append('\n');
        }

        return builder.ToString();
    }
}
