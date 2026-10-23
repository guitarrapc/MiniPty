using System.Buffers;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace MiniPty.Internal;

/// <summary>
/// Finds the file <c>CreateProcess</c> would start for a <see cref="PtyStartInfo.FileName"/>, searching the same
/// places in the same order, so that MiniPty can search a <c>PATH</c> of its own choosing: the child's, when
/// <see cref="PtyStartInfo.Environment"/> sets or removes it, where <c>CreateProcess</c> only ever searches the
/// host's.
/// </summary>
/// <remarks>
/// <para>
/// <c>CreateProcess</c> looks a name up from the calling process: the directory of its executable, its current
/// directory (unless <c>NeedCurrentDirectoryForExePath</c> says not to), the system directory, the 16-bit system
/// directory, the Windows directory, and the entries of the calling process's <c>PATH</c>. The environment block
/// passed for the child plays no part in it, so a host that sets the child's <c>PATH</c> to find a tool cannot
/// start that tool by name, while a Unix child resolves its name against its own <c>PATH</c>. The same search run
/// here with the child's <c>PATH</c> closes that gap; the file found is passed to <c>CreateProcessW</c> as the
/// application name, so it makes no lookup of its own.
/// </para>
/// <para>
/// A program name gets <c>.exe</c> appended when its last path component has no <c>.</c>, which is what
/// <c>CreateProcess</c> does, measured on Windows 11: <c>tool</c> found only <c>tool.exe</c>, also when an
/// extensionless <c>tool</c> sat beside it; <c>sub\tool</c> and <c>sub.d\tool</c> found only <c>sub\tool.exe</c> and
/// <c>sub.d\tool.exe</c>, although the documentation says a name with a path gets no extension; <c>tool.</c> and
/// <c>tool.foo</c> were taken as written, and Windows opens <c>tool.</c> as <c>tool</c>. A batch script's name
/// always carries its extension, so a script gets nothing appended.
/// </para>
/// <para>
/// A rooted name, and one that starts with <c>.\</c>, <c>..\</c>, <c>./</c>, or <c>../</c>, is not searched for at
/// all. Such a name is left to <c>CreateProcess</c> for a program, and taken as it is for a script. Any other name,
/// with a directory part or without, is searched for.
/// </para>
/// <para>
/// The child's <c>PATH</c> differs from <c>CreateProcess</c> in one rule (<see cref="ChildLocations"/>): only its
/// fully qualified entries are searched. <c>CreateProcess</c> reads an empty entry, <c>.</c>, and any relative entry
/// against the current directory, and does so also when <c>NoDefaultCurrentDirectoryInExePath</c> is set (measured on
/// Windows 11: a program planted in the current directory ran for <c>;dir</c>, <c>.;dir</c>, <c>dir;;x</c>, and
/// <c>dir\..</c> with the variable set). Applied to a <c>PATH</c> that the host's caller wrote, that would start a
/// program from the host's current directory, which is neither the child's <see cref="PtyStartInfo.WorkingDirectory"/>
/// nor anything the caller can see, and would undo the opt-out that protects the host's own lookups. An entry such as
/// <c>extra + ";" + path</c> with an empty <c>extra</c> produces such an entry by accident. The child still receives its
/// <c>PATH</c> unchanged and resolves its own commands by its own rules.
/// </para>
/// </remarks>
internal static partial class WindowsProgramLookup
{
    private const int ErrorFileNotFound = 2;
    private const int ErrorAccessDenied = 5;
    private const uint InvalidFileAttributes = 0xFFFFFFFF;
    private const uint FileAttributeDirectory = 0x10;
    private const string ExecutableExtension = ".exe";

    /// <summary>
    /// Measured on Windows 11 26200: a program path of 259 characters starts through <c>CreateProcess</c>'s own lookup,
    /// one of 260 does not.
    /// </summary>
    private const int MaxPath = 260;

    private static string? s_systemDirectory;
    private static string? s_windowsDirectory;
    private static string? s_processDirectory;

    internal static string SystemDirectory => s_systemDirectory ??= Environment.SystemDirectory;

    private static string WindowsDirectory => s_windowsDirectory ??= Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private static string? ProcessDirectory => s_processDirectory ??= Path.GetDirectoryName(Environment.ProcessPath);

    /// <summary>
    /// The places a lookup searches, in <c>CreateProcess</c>'s order. <see cref="ChildLocations"/> and
    /// <see cref="ResolveScriptOnHost"/> read them from the process.
    /// </summary>
    /// <param name="ProcessDirectory">The directory of the host executable, or null when unknown.</param>
    /// <param name="CurrentDirectory">The host's current directory.</param>
    /// <param name="SearchCurrentDirectory">Whether the current directory is searched, as
    /// <c>NeedCurrentDirectoryForExePath</c> decides for the name.</param>
    /// <param name="SystemDirectory">
    /// The system directory. The 16-bit one is <c>System</c> under the Windows directory.
    /// </param>
    /// <param name="WindowsDirectory">The Windows directory.</param>
    /// <param name="Path">
    /// The <c>PATH</c> whose entries are searched after the fixed locations, or null for none. The child's when the
    /// overlay sets or removes it; <see cref="ResolveScriptOnHost"/> leaves it null and reads the host's <c>PATH</c>
    /// only when the fixed locations miss.
    /// </param>
    /// <param name="SearchRelativePathEntries">
    /// Whether an empty, <c>.</c>, or other relative <c>PATH</c> entry is searched, against
    /// <paramref name="CurrentDirectory"/>, as <c>CreateProcess</c> searches the host's <c>PATH</c>. False for the
    /// child's <c>PATH</c>, which then searches only its fully qualified entries (see the type remarks).
    /// </param>
    internal readonly record struct SearchLocations(
        string? ProcessDirectory,
        string CurrentDirectory,
        bool SearchCurrentDirectory,
        string SystemDirectory,
        string WindowsDirectory,
        string? Path,
        bool SearchRelativePathEntries = true);

    /// <summary>
    /// Whether <c>CreateProcess</c> searches for <paramref name="fileName"/>: it is neither rooted nor relative to
    /// the current directory by a leading <c>.\</c>, <c>..\</c>, <c>./</c>, or <c>../</c>. A drive-relative or
    /// root-relative name such as <c>C:x.exe</c> or <c>\x.exe</c> counts as rooted.
    /// </summary>
    internal static bool IsSearched(string fileName) =>
        !Path.IsPathRooted(fileName) && !IsCurrentDirectoryRelative(fileName);

    /// <summary>
    /// This process's locations for a lookup of <paramref name="fileName"/> on the child's <paramref name="childPath"/>:
    /// <c>CreateProcess</c>'s fixed locations, then the fully qualified entries of <paramref name="childPath"/>.
    /// </summary>
    internal static SearchLocations ChildLocations(string fileName, string? childPath) =>
        new(ProcessDirectory, Environment.CurrentDirectory, NeedCurrentDirectoryForExePathW(fileName), SystemDirectory, WindowsDirectory, childPath, SearchRelativePathEntries: false);

    private static SearchLocations HostLocations(string fileName) =>
        new(ProcessDirectory, Environment.CurrentDirectory, NeedCurrentDirectoryForExePathW(fileName), SystemDirectory, WindowsDirectory, Path: null);

    /// <summary>
    /// Locates the program <c>CreateProcess</c> would start for <paramref name="fileName"/>, a name that
    /// <see cref="IsSearched"/>, with <c>.exe</c> appended when its last component has no <c>.</c>.
    /// </summary>
    /// <returns>The program's full path.</returns>
    /// <exception cref="Win32Exception">
    /// No such file (<c>ERROR_FILE_NOT_FOUND</c>), or a directory found first (<c>ERROR_ACCESS_DENIED</c>), as from
    /// <c>CreateProcess</c>.
    /// </exception>
    internal static string ResolveProgram(string fileName, in SearchLocations locations)
    {
        var extension = NeedsExecutableExtension(fileName) ? ExecutableExtension : "";
        if (TryFixedLocations(fileName, extension, locations, out var found) || TryPathEntries(fileName, extension, locations.Path, locations, out found))
            return Accept(found);

        throw new Win32Exception(ErrorFileNotFound, "CreateProcess failed");
    }

    /// <summary>Locates a batch script as <c>CreateProcess</c> locates a program, from this process's locations and <c>PATH</c>.</summary>
    /// <returns>The script's full path.</returns>
    /// <exception cref="Win32Exception">
    /// No such file (<c>ERROR_FILE_NOT_FOUND</c>), or a directory found first (<c>ERROR_ACCESS_DENIED</c>), as from
    /// <c>CreateProcess</c>.
    /// </exception>
    internal static string ResolveScriptOnHost(string fileName)
    {
        // A rooted or dot-relative name is not searched for, so it reads neither the current directory nor PATH.
        if (!IsSearched(fileName))
            return Accept(Path.GetFullPath(fileName));

        var locations = HostLocations(fileName);

        // PATH, the longest string the lookup reads, is read only when the fixed locations miss.
        if (TryFixedLocations(fileName, "", locations, out var found)
            || TryPathEntries(fileName, "", Environment.GetEnvironmentVariable("PATH"), locations, out found))
        {
            return Accept(found);
        }

        throw new Win32Exception(ErrorFileNotFound, "CreateProcess failed");
    }

    /// <summary>
    /// Locates a batch script as <c>CreateProcess</c> locates a program. A rooted name is taken as it is, and a name
    /// that starts with <c>.\</c>, <c>..\</c>, <c>./</c>, or <c>../</c> is relative to the current directory. Any
    /// other name, with a directory part or without, is searched for in the host executable's directory, the current
    /// directory when it is searched, the system directory, the 16-bit system directory, the Windows directory, and
    /// then each <c>PATH</c> entry as written. An empty entry stands for the current directory, except one after the
    /// last semicolon, and a relative entry is relative to the current directory. The first match decides, and a
    /// directory there fails the launch.
    /// </summary>
    /// <returns>The script's full path.</returns>
    /// <exception cref="Win32Exception">
    /// No such file (<c>ERROR_FILE_NOT_FOUND</c>), or a directory found first (<c>ERROR_ACCESS_DENIED</c>), as from
    /// <c>CreateProcess</c>.
    /// </exception>
    internal static string ResolveScript(string fileName, in SearchLocations locations)
    {
        if (Path.IsPathRooted(fileName))
            return Accept(Path.GetFullPath(fileName));

        if (IsCurrentDirectoryRelative(fileName))
            return Accept(Path.GetFullPath(fileName, locations.CurrentDirectory));

        if (TryFixedLocations(fileName, "", locations, out var found) || TryPathEntries(fileName, "", locations.Path, locations, out found))
            return Accept(found);

        throw new Win32Exception(ErrorFileNotFound, "CreateProcess failed");
    }

    /// <summary>
    /// The application name for a program <c>CreateProcess</c> does not search for (<see cref="IsSearched"/> is false)
    /// whose full path reaches <c>MAX_PATH</c>. <c>CreateProcess</c>'s own lookup of the command line, which is what a
    /// null application name gets, fails from there: <c>ERROR_FILENAME_EXCED_RANGE</c> in a host that opted into long
    /// paths, <c>ERROR_FILE_NOT_FOUND</c> in one that did not. Given as the application name, the same path starts in a
    /// host that opted in, and in no other way does it start at all.
    /// </summary>
    /// <remarks>
    /// Only from <c>MAX_PATH</c> on: below it the lookup works, and leaving it there costs a spawn nothing. The name
    /// gets <c>.exe</c> as the lookup would add it, and the file is checked first, so a missing file still fails with
    /// <c>ERROR_FILE_NOT_FOUND</c> and a directory with <c>ERROR_ACCESS_DENIED</c>, whatever <c>CreateProcess</c>
    /// reports for an application name (a missing directory there is <c>ERROR_PATH_NOT_FOUND</c>). In a host that did
    /// not opt into long paths the check cannot see the file and fails with <c>ERROR_FILE_NOT_FOUND</c>, as the spawn
    /// did before. The current directory is read only for a name that is not fully qualified, so a short rooted name
    /// costs nothing.
    /// </remarks>
    /// <param name="fileName">The program name as given.</param>
    /// <param name="program">The full path to pass as the application name, when the method returns true.</param>
    /// <returns><see langword="false"/> for a name that is searched for, or whose full path is shorter than
    /// <c>MAX_PATH</c>; such a name stays with <c>CreateProcess</c>.</returns>
    /// <exception cref="Win32Exception">
    /// No such file (<c>ERROR_FILE_NOT_FOUND</c>), or a directory (<c>ERROR_ACCESS_DENIED</c>), as from
    /// <c>CreateProcess</c>.
    /// </exception>
    internal static bool TryResolveLongProgramPath(string fileName, [NotNullWhen(true)] out string? program)
    {
        program = null;
        if (IsSearched(fileName))
            return false;

        var extension = NeedsExecutableExtension(fileName) ? ExecutableExtension : "";
        var length = Path.IsPathFullyQualified(fileName)
            ? fileName.Length
            : Environment.CurrentDirectory.Length + 1 + fileName.Length;
        if (length + extension.Length < MaxPath)
            return false;

        program = Accept(Path.GetFullPath(fileName) + extension);
        return true;
    }

    /// <summary>
    /// Whether <c>CreateProcess</c> appends <c>.exe</c> to <paramref name="fileName"/>: its last component, after the
    /// last <c>\</c> or <c>/</c>, holds no <c>.</c>. A trailing <c>.</c> counts, and Windows then opens the name
    /// without it.
    /// </summary>
    internal static bool NeedsExecutableExtension(string fileName)
    {
        var name = fileName.AsSpan();
        var component = name[(name.LastIndexOfAny('\\', '/') + 1)..];
        return !component.Contains('.');
    }

    private static bool TryFixedLocations(string fileName, string extension, in SearchLocations locations, out string found) =>
        (!string.IsNullOrEmpty(locations.ProcessDirectory) && TryCandidate(locations.ProcessDirectory, fileName, extension, locations, out found))
        || (locations.SearchCurrentDirectory && TryCandidate(locations.CurrentDirectory, fileName, extension, locations, out found))
        || TryCandidate(locations.SystemDirectory, fileName, extension, locations, out found)
        || TryCandidate(Path.Join(locations.WindowsDirectory, "System"), fileName, extension, locations, out found)
        || TryCandidate(locations.WindowsDirectory, fileName, extension, locations, out found);

    private static bool TryPathEntries(string fileName, string extension, string? path, in SearchLocations locations, out string found)
    {
        if (path is not null)
        {
            foreach (var range in path.AsSpan().Split(';'))
            {
                var entry = path.AsSpan(range);
                // The list ends at its last semicolon, so a final empty entry is not the current directory.
                if (entry.IsEmpty && range.End.GetOffset(path.Length) == path.Length)
                    break;

                // An empty entry, ".", "rel", "C:rel", and "\rel" all mean "relative to a current directory"; the
                // child's PATH searches none of them (see the type remarks).
                if (!locations.SearchRelativePathEntries && !Path.IsPathFullyQualified(entry))
                    continue;

                if (TryCandidate(entry.IsEmpty ? locations.CurrentDirectory : entry, fileName, extension, locations, out found))
                    return true;
            }
        }

        found = "";
        return false;
    }

    private static bool IsCurrentDirectoryRelative(string fileName) =>
        fileName.StartsWith(".\\", StringComparison.Ordinal)
        || fileName.StartsWith("./", StringComparison.Ordinal)
        || fileName.StartsWith("..\\", StringComparison.Ordinal)
        || fileName.StartsWith("../", StringComparison.Ordinal);

    /// <summary>
    /// Takes the file the lookup found, or fails as <c>CreateProcess</c> does there: it runs the first match and does
    /// not skip a directory to look for a later file.
    /// </summary>
    private static unsafe string Accept(string path)
    {
        uint attributes;
        fixed (char* p = path)
            attributes = GetFileAttributesW(p);

        if (attributes == InvalidFileAttributes)
            throw new Win32Exception(ErrorFileNotFound, "CreateProcess failed");
        if ((attributes & FileAttributeDirectory) != 0)
            throw new Win32Exception(ErrorAccessDenied, "CreateProcess failed");
        return path;
    }

    /// <summary>
    /// Whether <paramref name="fileName"/> with <paramref name="extension"/> appended exists, as a file or a
    /// directory, under <paramref name="directory"/>. The directory is relative to the lookup's current directory
    /// unless rooted.
    /// </summary>
    /// <remarks>
    /// The candidate is written into a pooled buffer and probed there, so a search through a long <c>PATH</c> allocates
    /// nothing until it finds the file.
    /// </remarks>
    private static unsafe bool TryCandidate(ReadOnlySpan<char> directory, string fileName, string extension, in SearchLocations locations, out string found)
    {
        var baseDirectory = Path.IsPathRooted(directory) ? ReadOnlySpan<char>.Empty : locations.CurrentDirectory.AsSpan();
        var buffer = ArrayPool<char>.Shared.Rent(baseDirectory.Length + directory.Length + fileName.Length + extension.Length + 3);
        try
        {
            var joined = baseDirectory.IsEmpty
                ? Path.TryJoin(directory, fileName, buffer, out var written)
                : Path.TryJoin(baseDirectory, directory, fileName, buffer, out written);
            if (joined && written + extension.Length < buffer.Length)
            {
                extension.CopyTo(0, buffer, written, extension.Length);
                written += extension.Length;
                buffer[written] = '\0';
                fixed (char* candidate = buffer)
                {
                    if (GetFileAttributesW(candidate) != InvalidFileAttributes)
                    {
                        found = Path.GetFullPath(new string(buffer, 0, written));
                        return true;
                    }
                }
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }

        found = "";
        return false;
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool NeedCurrentDirectoryForExePathW(string exeName);

    [LibraryImport("kernel32.dll")]
    private static unsafe partial uint GetFileAttributesW(char* fileName);
}
