namespace TerminalEmulator;

/// <summary>Prepares clipboard text for a program's input.</summary>
internal static class PasteText
{
    /// <summary>
    /// Terminals receive CR for a line break, so CRLF and LF become CR. With bracketed paste on, the text is
    /// wrapped in <c>ESC[200~</c> and <c>ESC[201~</c> so the program can tell a paste from typing.
    /// </summary>
    public static string Normalize(string text, bool bracketedPaste)
    {
        text = text.Replace("\r\n", "\r").Replace('\n', '\r');
        return bracketedPaste ? "\u001b[200~" + text + "\u001b[201~" : text;
    }
}
