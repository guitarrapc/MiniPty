using System.Buffers;
using System.ComponentModel;

namespace MiniPty.Internal;

/// <summary>
/// Starts a batch file (<c>.bat</c>, <c>.cmd</c>) so that <c>cmd.exe</c> reads its arguments as data, not as commands.
/// </summary>
/// <remarks>
/// <c>CreateProcess</c> runs a batch file by starting <c>cmd.exe</c> on the command line it was given, and
/// <c>cmd.exe</c> does not parse arguments by the MSVCRT rules, so an argument quoted by those rules can run a command
/// of its own. MiniPty therefore starts <c>cmd.exe</c> itself, as <c>cmd.exe /e:ON /v:OFF /d /c ""script" args"</c>,
/// with the script's full path and each argument escaped for <c>cmd.exe</c>. The rules and their reasons are in Batch
/// Files (Windows), <c>.github/docs/specs/core_session.md</c>.
/// </remarks>
internal static class WindowsBatchFile
{
    /// <summary>
    /// The longest command line <c>cmd.exe</c> runs, in characters without the terminating NUL. <c>cmd.exe</c> counts
    /// the whole line as written: its program name, its switches, and every escape.
    /// </summary>
    internal const int CommandLineLimit = 8191;

    private const int ErrorBadExeFormat = 193;

    // /d skips the registry's AutoRun commands, /v:OFF keeps !name! from expanding, and /e:ON enables the substring
    // syntax of the percent escape. The registry can change the defaults of the last two, so both are explicit. /c
    // strips the outer pair of quotes, so the script path and each argument keep their own.
    private const string CommandPrefix = "cmd.exe /e:ON /v:OFF /d /c \"";

    // %cd:~,% is an empty substring of the current directory, so the % before it survives and no %name% can expand.
    private const string PercentEscape = "%%cd:~,";

    // An argument made only of these needs no quotes: cmd.exe treats none of them specially or as a separator.
    private static readonly SearchValues<char> s_unquoted =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789#$*+-./:?@\\_");

    private static string? s_commandInterpreterPath;

    /// <summary>
    /// Gets the interpreter that runs a batch file: <c>cmd.exe</c> in the system directory, never <c>ComSpec</c>, which
    /// the environment controls.
    /// </summary>
    internal static string CommandInterpreterPath => s_commandInterpreterPath ??= Path.Combine(WindowsProgramLookup.SystemDirectory, "cmd.exe");

    /// <summary>
    /// Whether <paramref name="fileName"/> names a batch file: whether the last component left after <c>.</c> and
    /// <c>..</c> segments, with its trailing spaces and dots dropped, ends in <c>.bat</c> or <c>.cmd</c>.
    /// </summary>
    /// <remarks>
    /// The rule is lexical because <c>GetFullPathNameW</c> turns some names that <c>CreateProcess</c> starts as batch
    /// files, such as <c>x.cmd\. </c>, into names that end with a backslash.
    /// </remarks>
    internal static bool IsBatchFile(string fileName)
    {
        var name = fileName.AsSpan();
        var pendingParents = 0;
        while (true)
        {
            var separator = name.LastIndexOfAny('\\', '/');
            var component = name[(separator + 1)..];
            var trimmed = component.TrimEnd(" .");
            if (trimmed.IsEmpty)
            {
                // Only dots and spaces: ".." goes up a level, and any other form such as "." or ". " is dropped.
                if (component.TrimEnd(' ').SequenceEqual(".."))
                    pendingParents++;
            }
            else if (pendingParents > 0)
            {
                pendingParents--;
            }
            else
            {
                return trimmed.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
                    || trimmed.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
            }

            if (separator < 0)
                return false;

            name = name[..separator];
        }
    }

    /// <summary>Rejects a batch file name that <c>cmd.exe</c> cannot receive as written.</summary>
    /// <exception cref="ArgumentException">
    /// The name contains a quote, a carriage return, a line feed, or a NUL.
    /// </exception>
    internal static void ValidateName(string fileName)
    {
        if (fileName.AsSpan().IndexOfAny("\"\r\n\0") >= 0)
        {
            throw new ArgumentException(
                "A batch file name cannot contain a quote, a carriage return, a line feed, or a NUL character.",
                "startInfo");
        }
    }

    /// <summary>
    /// Whether the script at <paramref name="path"/> is a program image rather than a script, by its <c>MZ</c> header.
    /// </summary>
    /// <remarks>
    /// <c>CreateProcess</c> runs a file with this header as a program, or refuses it, and never hands it to
    /// <c>cmd.exe</c>. The file is opened as <c>CreateProcess</c> opens it, so a file it cannot open, or an empty one,
    /// fails here rather than only in <c>cmd.exe</c>'s output.
    /// </remarks>
    /// <exception cref="Win32Exception">
    /// The file cannot be opened, with the error <c>CreateProcess</c> would report, or it is empty
    /// (<c>ERROR_BAD_EXE_FORMAT</c>).
    /// </exception>
    internal static bool IsExecutableImage(string path)
    {
        int read;
        Span<byte> header = stackalloc byte[2];
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            read = RandomAccess.Read(handle, header, 0);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // An HRESULT from a Win32 error carries that error in its low 16 bits.
            var error = (e.HResult & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000) ? e.HResult & 0xFFFF : e.HResult;
            throw new Win32Exception(error, "CreateProcess failed");
        }

        if (read == 0)
            throw new Win32Exception(ErrorBadExeFormat, "CreateProcess failed");

        return read == 2 && header[0] == (byte)'M' && header[1] == (byte)'Z';
    }

    /// <summary>
    /// Builds the NUL-terminated <c>cmd.exe</c> command line that runs <paramref name="scriptPath"/> with
    /// <paramref name="arguments"/> delivered as data (see the type remarks).
    /// </summary>
    /// <remarks>
    /// The path is checked again here because the lookup joins the name with a <c>PATH</c> entry or the current
    /// directory, which <see cref="ValidateName"/> never saw.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// The script path contains a quote, a carriage return, a line feed, or a NUL, or ends with a backslash, an
    /// argument contains a carriage return, a line feed, or a NUL, or the command line would exceed
    /// <see cref="CommandLineLimit"/> characters.
    /// </exception>
    internal static char[] BuildCommandLine(string scriptPath, IReadOnlyList<string> arguments)
    {
        if (scriptPath.AsSpan().IndexOfAny("\"\r\n\0") >= 0 || scriptPath.EndsWith('\\'))
        {
            throw new ArgumentException(
                "A batch file path cannot contain a quote, a carriage return, a line feed, or a NUL character, or end with a backslash.",
                "startInfo");
        }

        // A long sum, checked after each argument: escaping takes up to 8 characters per character, so an int total
        // could wrap around to a length that passes the check.
        long length = CommandPrefix.Length + 2 + EscapedPercentLength(scriptPath) + 1;
        ThrowIfTooLong(length);
        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            if (argument.AsSpan().IndexOfAny('\r', '\n', '\0') >= 0)
            {
                throw new ArgumentException(
                    "An argument for a batch file cannot contain a carriage return, a line feed, or a NUL character: cmd.exe ends or cuts its command line there.",
                    "startInfo");
            }

            length += 1 + ArgumentLength(argument);
            ThrowIfTooLong(length);
        }

        var commandLine = new char[length + 1];
        var span = commandLine.AsSpan();
        CommandPrefix.CopyTo(span);
        var position = CommandPrefix.Length;
        span[position++] = '"';
        position += WriteEscapedPercent(span[position..], scriptPath);
        span[position++] = '"';
        for (var i = 0; i < arguments.Count; i++)
        {
            span[position++] = ' ';
            position += WriteArgument(span[position..], arguments[i]);
        }

        span[position++] = '"';
        span[position] = '\0';
        return commandLine;
    }

    private static void ThrowIfTooLong(long length)
    {
        if (length > CommandLineLimit)
        {
            throw new ArgumentException(
                $"The command line for the batch file is at least {length} characters, more than the {CommandLineLimit} cmd.exe accepts. Escaping counts: each % takes 8 characters, and the script's full path is included.",
                "startInfo");
        }
    }

    private static bool NeedsQuoting(string argument) =>
        argument.Length == 0 || argument[^1] == '\\' || argument.AsSpan().ContainsAnyExcept(s_unquoted);

    private static long EscapedPercentLength(string value) => value.Length + (long)value.AsSpan().Count('%') * PercentEscape.Length;

    private static int WriteEscapedPercent(Span<char> destination, string value)
    {
        var position = 0;
        foreach (var c in value)
        {
            if (c == '%')
            {
                PercentEscape.CopyTo(destination[position..]);
                position += PercentEscape.Length;
            }

            destination[position++] = c;
        }

        return position;
    }

    /// <summary>Length of <paramref name="argument"/> after <see cref="WriteArgument"/>.</summary>
    private static long ArgumentLength(string argument)
    {
        var quote = NeedsQuoting(argument);
        long length = quote ? 2 : 0;
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
            }
            else
            {
                if (c == '"')
                    length += backslashes + 1;
                else if (c == '%')
                    length += PercentEscape.Length;

                backslashes = 0;
            }

            length++;
        }

        return quote ? length + backslashes : length;
    }

    private static int WriteArgument(Span<char> destination, string argument)
    {
        var quote = NeedsQuoting(argument);
        var position = 0;
        if (quote)
            destination[position++] = '"';

        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
            }
            else
            {
                if (c == '"')
                {
                    // Backslashes before a quote double for the program's parser, and the quote doubles for cmd.exe.
                    destination.Slice(position, backslashes).Fill('\\');
                    position += backslashes;
                    destination[position++] = '"';
                }
                else if (c == '%')
                {
                    PercentEscape.CopyTo(destination[position..]);
                    position += PercentEscape.Length;
                }

                backslashes = 0;
            }

            destination[position++] = c;
        }

        if (quote)
        {
            // Backslashes before the closing quote double, so the quote stays a closing one.
            destination.Slice(position, backslashes).Fill('\\');
            position += backslashes;
            destination[position++] = '"';
        }

        return position;
    }
}
