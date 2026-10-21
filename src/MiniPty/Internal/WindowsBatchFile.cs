using System.Buffers;
using System.ComponentModel;

namespace MiniPty.Internal;

/// <summary>
/// Starts a batch file (<c>.bat</c>, <c>.cmd</c>) so that <c>cmd.exe</c> reads its arguments as data, not as commands.
/// </summary>
/// <remarks>
/// <para>
/// <c>CreateProcess</c> runs a batch file by starting <c>cmd.exe</c> on the command line it was given, and
/// <c>cmd.exe</c> does not parse arguments by the MSVCRT rules. An argument quoted by those rules can close
/// <c>cmd.exe</c>'s quoting and run a command of its own: <c>x" &amp; calc &amp; "</c>, quoted as
/// <c>"x\" &amp; calc &amp; \""</c>, runs <c>calc</c>. A host that passes user input as an argument to a batch file,
/// such as a tool's <c>.cmd</c> shim, would run that user's commands.
/// </para>
/// <para>
/// MiniPty therefore starts the interpreter itself, from the system directory (never through <c>ComSpec</c>, which
/// the environment controls), as <c>cmd.exe /e:ON /v:OFF /d /c ""script" args"</c>:
/// </para>
/// <list type="bullet">
/// <item><c>/d</c> skips the AutoRun commands from the registry, <c>/v:OFF</c> turns delayed expansion off so
/// <c>!name!</c> is not expanded, and <c>/e:ON</c> turns on the command extensions the percent escape relies on.
/// The registry can add AutoRun commands and change the other two defaults, so all three switches are explicit.</item>
/// <item><c>cmd.exe /c</c> strips the first and last quote of a line that starts with a quote and holds more than
/// two. The outer pair is there to be stripped, so the script path and every argument keep their own.</item>
/// <item>An argument is quoted when it is empty, ends with a backslash, or holds any character other than an ASCII
/// letter, an ASCII digit, or one of <c>#$*+-./:?@\_</c>. Every character <c>cmd.exe</c> treats specially is then
/// inside quotes, and so is every non-ASCII space it splits parameters on.</item>
/// <item>A quote inside an argument is doubled, so <c>cmd.exe</c>'s quoting closes and reopens around it with nothing
/// in between. A program the script forwards <c>%*</c> to reads the pair as one quote when it parses its command line
/// with the UCRT. The pre-2008 rules, which <c>CommandLineToArgvW</c> and <c>msvcrt.dll</c> follow, read the pair as
/// a quote that also ends the quoting, so the argument boundaries after it move. Backslashes before a quote, and
/// before the closing quote, are doubled for the program's parser.</item>
/// <item>Each <c>%</c>, also in the script path, is written as <c>%%cd:~,%</c>. <c>%cd:~,%</c> is an empty substring
/// of the current directory, so the first <c>%</c> survives and no <c>%name%</c> can expand.</item>
/// <item>A carriage return, a line feed, or a NUL in an argument is rejected, and so is any of them or a quote in the
/// file name and again in the full path the lookup produces. <c>cmd.exe</c> drops a carriage return and ends its
/// command line at a line feed or a NUL, so no quoting can deliver them.</item>
/// <item>A command line longer than <see cref="CommandLineLimit"/> characters is rejected. Otherwise the session would
/// start and <c>cmd.exe</c> would fail with "The command line is too long", with nothing pointing at the cause.</item>
/// </list>
/// <para>
/// A file is a batch file by the extension of the name Windows opens (<see cref="IsBatchFile"/>). A check on the name
/// as given would let <c>script.cmd.</c> or <c>script.cmd\.</c> run unescaped. The script is the file
/// <c>CreateProcess</c> would find for the name, and <c>cmd.exe</c> gets its full path. So <c>cmd.exe</c>'s own lookup,
/// which starts in the child's working directory, never picks another file. A file that starts with the <c>MZ</c>
/// header is a program image, which <c>CreateProcess</c> runs or refuses itself, from that same full path: a second
/// lookup by <c>CreateProcess</c> could find another file, and the file could change between the header check and
/// the start.
/// </para>
/// <para>
/// What the script does with its arguments is up to the script. One that hands an argument to <c>cmd /c</c>, or uses
/// it without its quotes as <c>%~1</c>, can still be made to run something, and no caller can prevent that. A raw
/// <see cref="PtyStartInfo.CommandLine"/> is passed unchanged, as its contract says.
/// </para>
/// </remarks>
internal static class WindowsBatchFile
{
    /// <summary>
    /// The longest command line <c>cmd.exe</c> runs, in characters without the terminating NUL. <c>cmd.exe</c> counts
    /// the whole line as written: its program name, its switches, and every escape.
    /// </summary>
    internal const int CommandLineLimit = 8191;

    private const int ErrorBadExeFormat = 193;
    private const string CommandPrefix = "cmd.exe /e:ON /v:OFF /d /c \"";
    private const string PercentEscape = "%%cd:~,";

    // An argument made only of these needs no quotes: cmd.exe treats none of them specially or as a separator.
    private static readonly SearchValues<char> s_unquoted =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789#$*+-./:?@\\_");

    private static string? s_commandInterpreterPath;

    /// <summary>Gets the interpreter that runs a batch file: <c>cmd.exe</c> in the system directory.</summary>
    internal static string CommandInterpreterPath => s_commandInterpreterPath ??= Path.Combine(WindowsProgramLookup.SystemDirectory, "cmd.exe");

    /// <summary>
    /// Whether <paramref name="fileName"/> names a batch file: whether the last component left after <c>.</c> and
    /// <c>..</c> segments, with its trailing spaces and dots dropped, ends in <c>.bat</c> or <c>.cmd</c>.
    /// </summary>
    /// <remarks>
    /// Windows removes those segments and trailing characters before it opens a name, and <c>CreateProcess</c> starts
    /// <c>cmd.exe</c> for <c>x.cmd\.</c>, <c>x.cmd\sub\..</c> and <c>x.cmd\. </c>. <c>GetFullPathNameW</c> turns the
    /// last one into <c>x.cmd\</c>, so it cannot decide this, and the rule is lexical instead. It also counts a name
    /// such as <c>x.cmd\</c>, which no file can match. The lookup then fails, where the name taken as a program could
    /// have reached <c>cmd.exe</c> unescaped.
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
    /// <c>CreateProcess</c> hands a <c>.bat</c> or <c>.cmd</c> file to <c>cmd.exe</c> only when the file is not empty
    /// and does not start with this header. It runs a file that does as a program, or refuses it when it is not a
    /// valid one. It fails when another handle denies reading the file or holds it for writing, so the file is opened
    /// the same way here. A file that cannot be opened, or an empty one, then fails before the session starts, where
    /// <c>cmd.exe</c> would report it only in its output.
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
    /// The script path is checked here as well as in <see cref="ValidateName"/>: the lookup
    /// (<see cref="WindowsProgramLookup"/>) joins the name with directories from a <c>PATH</c> and the current
    /// directory, so this is the one place that sees every character <c>cmd.exe</c> receives.
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
