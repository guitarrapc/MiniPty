using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MiniPty.Internal;

/// <summary>
/// Starts a batch file (<c>.bat</c>, <c>.cmd</c>) so that its arguments reach it as data, never as commands.
/// </summary>
/// <remarks>
/// <para>
/// <c>CreateProcess</c> runs a batch file by starting <c>cmd.exe</c> on the command line it was given, and
/// <c>cmd.exe</c> does not parse arguments by the MSVCRT rules every other program uses. An argument quoted by those
/// rules can close <c>cmd.exe</c>'s quoting and run a command of its own: <c>x" &amp; calc &amp; "</c>, quoted as
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
/// The registry can change all three defaults, so they are explicit.</item>
/// <item>The outer pair of quotes is the one <c>cmd.exe /c</c> strips when the command line holds more than two
/// quotes; the script path and every argument keep their own.</item>
/// <item>An argument is quoted when it is empty, ends with a backslash, or contains a control character or any ASCII
/// character other than a letter, a digit, or <c>#$*+-./:?@\_</c>, so every character <c>cmd.exe</c> treats specially
/// is inside quotes. A quote inside is doubled (<c>""</c>), which keeps <c>cmd.exe</c>'s quoting intact and is read
/// as one quote by a program that receives the arguments through the script (<c>%*</c>); backslashes before it, and
/// before the closing quote, are doubled for that program's MSVCRT parser.</item>
/// <item>Each <c>%</c>, also in the script path, is written as <c>%%cd:~,%</c>: <c>%cd:~,%</c> is an empty substring
/// of the current directory, so the first <c>%</c> survives and no <c>%name%</c> can expand.</item>
/// <item>An argument with a carriage return, a line feed, or a NUL is rejected: <c>cmd.exe</c> ends or cuts its
/// command line there, so no quoting can deliver it.</item>
/// </list>
/// <para>
/// A file is a batch file by its extension after trailing spaces and dots, which Windows drops from a file name: a
/// check on the name as given lets <c>script.cmd.</c> run unescaped. The script is located where <c>CreateProcess</c>
/// would have found it, because <c>cmd.exe</c>'s own lookup (its working directory, then <c>PATH</c> with
/// <c>PATHEXT</c>) differs from it.
/// </para>
/// <para>
/// What the script does with its arguments is its own: one that uses <c>%1</c> unquoted in a command can still be
/// made to run something, and no caller can prevent that. A raw <see cref="PtyStartInfo.CommandLine"/> is passed
/// unchanged, as its contract says.
/// </para>
/// </remarks>
internal static partial class WindowsBatchFile
{
    private const int ErrorFileNotFound = 2;
    private const string CommandPrefix = "cmd.exe /e:ON /v:OFF /d /c \"";
    private const string PercentEscape = "%%cd:~,";
    private const string UnquotedSymbols = "#$*+-./:?@\\_";

    /// <summary>Gets the interpreter that runs a batch file: <c>cmd.exe</c> in the system directory.</summary>
    internal static string CommandInterpreterPath => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    /// <summary>
    /// Whether <paramref name="fileName"/> names a batch file, judged as Windows opens it: by the extension after
    /// trailing spaces and dots.
    /// </summary>
    internal static bool IsBatchFile(string fileName)
    {
        var name = fileName.AsSpan().TrimEnd(" .");
        return name.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Locates the script as <c>CreateProcess</c> locates a program: a name with a directory is relative to the host's
    /// current directory, and a bare name is searched for in the host's own directory, the host's current directory
    /// (unless <c>NoDefaultCurrentDirectoryInExePath</c> is set), the system directories, the Windows directory, and
    /// the host's <c>PATH</c>.
    /// </summary>
    /// <returns>The script's full path.</returns>
    /// <exception cref="Win32Exception">A bare name was not found (<c>ERROR_FILE_NOT_FOUND</c>), as from <c>CreateProcess</c>.</exception>
    internal static string ResolveScript(string fileName)
    {
        if (Path.IsPathRooted(fileName) || fileName.AsSpan().IndexOfAny('\\', '/') >= 0)
            return Path.GetFullPath(fileName);

        var processDirectory = Path.GetDirectoryName(Environment.ProcessPath);
        if (TryCandidate(processDirectory, fileName, out var found))
            return found;

        if (NeedCurrentDirectoryForExePathW(fileName) && TryCandidate(Environment.CurrentDirectory, fileName, out found))
            return found;

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (TryCandidate(Environment.SystemDirectory, fileName, out found)
            || TryCandidate(Path.Combine(windows, "System"), fileName, out found)
            || TryCandidate(windows, fileName, out found))
        {
            return found;
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (path is not null)
        {
            foreach (var range in path.AsSpan().Split(';'))
            {
                var entry = path.AsSpan(range).Trim().Trim('"');
                if (!entry.IsEmpty && TryCandidate(entry.ToString(), fileName, out found))
                    return found;
            }
        }

        throw new Win32Exception(ErrorFileNotFound, "CreateProcess failed");
    }

    /// <summary>
    /// Builds the NUL-terminated <c>cmd.exe</c> command line that runs <paramref name="scriptPath"/> with
    /// <paramref name="arguments"/> delivered as data (see the type remarks).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The script path contains a quote or ends with a backslash, or an argument contains a carriage return, a line
    /// feed, or a NUL.
    /// </exception>
    internal static char[] BuildCommandLine(string scriptPath, IReadOnlyList<string> arguments)
    {
        if (scriptPath.Contains('"') || scriptPath.EndsWith('\\'))
            throw new ArgumentException("A batch file path cannot contain a quote or end with a backslash.", "startInfo");

        var length = CommandPrefix.Length + 2 + EscapedPercentLength(scriptPath) + 1;
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

    private static bool TryCandidate(string? directory, string fileName, out string found)
    {
        if (!string.IsNullOrEmpty(directory))
        {
            var candidate = Path.Join(directory, fileName);
            if (File.Exists(candidate))
            {
                found = Path.GetFullPath(candidate);
                return true;
            }
        }

        found = "";
        return false;
    }

    private static bool NeedsQuoting(string argument)
    {
        if (argument.Length == 0 || argument[^1] == '\\')
            return true;

        foreach (var c in argument)
        {
            if (char.IsControl(c))
                return true;
            if (char.IsAscii(c) && !char.IsAsciiLetterOrDigit(c) && !UnquotedSymbols.Contains(c))
                return true;
        }

        return false;
    }

    private static int EscapedPercentLength(string value) => value.Length + value.AsSpan().Count('%') * PercentEscape.Length;

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
    private static int ArgumentLength(string argument)
    {
        var quote = NeedsQuoting(argument);
        var length = quote ? 2 : 0;
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
                    // Backslashes before a quote double for the MSVCRT parser, and the quote doubles for cmd.exe.
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

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool NeedCurrentDirectoryForExePathW(string exeName);
}
