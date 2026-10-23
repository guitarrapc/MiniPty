using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using MiniPty.Internal;

namespace MiniPty.Tests;

/// <summary>
/// A program under a path of <c>MAX_PATH</c> (260 characters) or more starts in a host that opted into long paths
/// (<c>WindowsProgramLookup.TryResolveLongProgramPath</c>). <c>CreateProcess</c>'s own lookup of the command line,
/// which a null application name gets, fails from 260 characters with <c>ERROR_FILENAME_EXCED_RANGE</c> even in such
/// a host, while the same path given as the application name starts.
/// </summary>
/// <remarks>
/// The test host opts in through its manifest (<c>app.manifest</c>). Windows honors the opt-in only where the machine
/// allows long paths (<c>LongPathsEnabled</c>). Tests that need it return early elsewhere, and one test covers that case
/// instead.
/// </remarks>
public sealed partial class WindowsLongProgramPathTests
{
    private const int ErrorFileNotFound = 2;
    private const int ErrorAccessDenied = 5;
    private const int ErrorDirectory = 267;
    private const string Marker = "LONG_PATH_OK";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Below <c>MAX_PATH</c> the lookup works, and leaving the name to it costs a spawn nothing.</summary>
    [Test]
    public async Task ShortProgramPathStaysWithTheCreateProcessLookup()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var (applicationName, _) = WindowsPtyBackend.BuildLaunch(new PtyStartInfo { FileName = cmd, Arguments = ["/c", "exit"] });

        await Assert.That(applicationName).IsNull();
    }

    [Test]
    public async Task LongProgramPathBecomesTheApplicationName()
    {
        if (!LongPathsEnabled())
            return;

        using var tree = new LongTree();
        var (applicationName, commandLine) = WindowsPtyBackend.BuildLaunch(new PtyStartInfo { FileName = tree.Program, Arguments = ["/c", "exit"] });

        await Assert.That(applicationName).IsEqualTo(tree.Program);
        // The command line keeps the name as given (quoted, as a path with a backslash is), so argv[0] is unchanged.
        await Assert.That(new string(commandLine, 0, commandLine.Length - 1)).IsEqualTo($"\"{tree.Program}\" /c exit");
    }

    /// <summary>The lookup would have appended <c>.exe</c> to a name without an extension; so does the application name.</summary>
    [Test]
    public async Task LongProgramPathWithoutExtensionGetsExe()
    {
        if (!LongPathsEnabled())
            return;

        using var tree = new LongTree();
        var withoutExtension = tree.Program[..^".exe".Length];
        var (applicationName, _) = WindowsPtyBackend.BuildLaunch(new PtyStartInfo { FileName = withoutExtension });

        await Assert.That(applicationName).IsEqualTo(tree.Program);
    }

    /// <summary>
    /// A name relative to the current directory is measured, and passed, as the full path it resolves to; a short
    /// relative name can name a long path.
    /// </summary>
    [Test]
    public async Task RelativeNameOfALongPathStarts()
    {
        if (!LongPathsEnabled())
            return;

        using var tree = new LongTree(Environment.CurrentDirectory);
        var relative = "." + Path.DirectorySeparatorChar + Path.GetRelativePath(Environment.CurrentDirectory, tree.Program);
        var (applicationName, _) = WindowsPtyBackend.BuildLaunch(new PtyStartInfo { FileName = relative });

        await Assert.That(applicationName).IsEqualTo(tree.Program);

        PtyResult result;
        await using (var session = Pty.Start(new PtyStartInfo { FileName = relative, Arguments = ["/c", "echo " + Marker] }))
            result = await session.CompleteAsync().WaitAsync(Timeout);

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(Encoding.UTF8.GetString(result.Output.Span)).Contains(Marker);
    }

    [Test]
    public async Task ProgramUnderALongPathStarts()
    {
        if (!LongPathsEnabled())
            return;

        using var tree = new LongTree();
        PtyResult result;
        await using (var session = Pty.Start(new PtyStartInfo { FileName = tree.Program, Arguments = ["/c", "echo " + Marker] }))
            result = await session.CompleteAsync().WaitAsync(Timeout);

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(Encoding.UTF8.GetString(result.Output.Span)).Contains(Marker);
    }

    /// <summary>
    /// The file is checked before it becomes the application name, so failures keep the errors the lookup reports for a
    /// shorter path. Given as an application name, a missing directory would fail with <c>ERROR_PATH_NOT_FOUND</c>.
    /// </summary>
    [Test]
    public async Task LongProgramPathFailuresKeepTheLookupErrors()
    {
        if (!LongPathsEnabled())
            return;

        using var tree = new LongTree();

        await Assert.That(StartError(Path.Combine(tree.Directory, "missing.exe"))).IsEqualTo(ErrorFileNotFound);
        await Assert.That(StartError(Path.Combine(tree.Directory, "missing", "child.exe"))).IsEqualTo(ErrorFileNotFound);
        await Assert.That(StartError(tree.Folder)).IsEqualTo(ErrorAccessDenied);
    }

    /// <summary>
    /// Where the machine does not allow long paths, the host's opt-in is ignored and nothing can start the program: the
    /// spawn fails with <c>ERROR_FILE_NOT_FOUND</c>, as it did before long program paths were passed on.
    /// </summary>
    [Test]
    public async Task LongProgramPathFailsAsBeforeWithoutLongPathSupport()
    {
        if (!OperatingSystem.IsWindows() || LongPathsEnabled())
            return;

        using var tree = new LongTree();

        await Assert.That(StartError(tree.Program)).IsEqualTo(ErrorFileNotFound);
    }

    /// <summary>
    /// A working directory of <c>MAX_PATH</c> or more is refused by <c>CreateProcess</c> itself
    /// (<c>ERROR_DIRECTORY</c>), also in a host that opted into long paths: a child's current directory stays within
    /// <c>MAX_PATH</c>.
    /// </summary>
    [Test]
    public async Task LongWorkingDirectoryIsRefused()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var tree = new LongTree();
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

        await Assert.That(StartError(cmd, tree.Directory)).IsEqualTo(ErrorDirectory);
    }

    private static int StartError(string fileName, string? workingDirectory = null)
    {
        try
        {
            using var session = Pty.Start(new PtyStartInfo { FileName = fileName, Arguments = ["/c", "exit"], WorkingDirectory = workingDirectory });
            return 0;
        }
        catch (Win32Exception e)
        {
            return e.NativeErrorCode;
        }
    }

    private static bool LongPathsEnabled() => OperatingSystem.IsWindows() && RtlAreLongPathsEnabled() != 0;

    [LibraryImport("ntdll.dll")]
    private static partial byte RtlAreLongPathsEnabled();

    /// <summary>
    /// A directory, under the temporary directory unless given another, whose path is longer than <c>MAX_PATH</c>,
    /// holding a copy of <c>cmd.exe</c> and a directory named like a program. A copied <c>cmd.exe</c> runs and echoes
    /// as the original does.
    /// </summary>
    private sealed class LongTree : IDisposable
    {
        private readonly string _root;

        public LongTree()
            : this(System.IO.Path.GetTempPath())
        {
        }

        public LongTree(string under)
        {
            _root = System.IO.Path.Combine(under, "minipty-long-" + Guid.NewGuid().ToString("N")[..8]);
            var directory = _root;
            while (directory.Length < 300)
                directory = System.IO.Path.Combine(directory, new string('d', 100));

            System.IO.Directory.CreateDirectory(directory);
            Directory = directory;
            Program = System.IO.Path.Combine(directory, "cmd.exe");
            File.Copy(System.IO.Path.Combine(Environment.SystemDirectory, "cmd.exe"), Program);
            Folder = System.IO.Path.Combine(directory, "folder.exe");
            System.IO.Directory.CreateDirectory(Folder);
        }

        public string Directory { get; }

        public string Program { get; }

        public string Folder { get; }

        public void Dispose()
        {
            // A child that just exited can hold its image open for a moment.
            for (var attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    System.IO.Directory.Delete(_root, recursive: true);
                    return;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(100);
                }
            }
        }
    }
}
