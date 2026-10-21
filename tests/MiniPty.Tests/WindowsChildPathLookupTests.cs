using System.ComponentModel;
using MiniPty.Internal;

namespace MiniPty.Tests;

/// <summary>
/// A program name is found on the child's <c>PATH</c> when <see cref="PtyStartInfo.Environment"/> sets or removes
/// it (<c>WindowsPtyBackend.BuildLaunch</c>). <c>CreateProcess</c> searches only the host's <c>PATH</c>, so without
/// this a host could not start by name a tool it put on the child's <c>PATH</c>, as it can on Unix.
/// </summary>
public sealed class WindowsChildPathLookupTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Test]
    public async Task ProgramNameIsFoundOnTheChildPath()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var tools = new ToolDirectory();
        var startInfo = new PtyStartInfo
        {
            FileName = tools.Name,
            Arguments = ["a b"],
            Environment = new Dictionary<string, string?> { ["PATH"] = tools.Path },
        };

        var (applicationName, commandLine) = WindowsPtyBackend.BuildLaunch(startInfo);

        // The file found is the application name; the command line keeps the name as given, so argv[0] is unchanged.
        await Assert.That(applicationName).IsEqualTo(tools.Program);
        await Assert.That(new string(commandLine, 0, commandLine.Length - 1)).IsEqualTo($"{tools.Name} \"a b\"");
    }

    [Test]
    public async Task ScriptNameIsFoundOnTheChildPath()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var tools = new ToolDirectory();
        var script = Path.Combine(tools.Path, tools.Name + ".cmd");
        File.WriteAllText(script, "@echo off\r\n");
        var startInfo = new PtyStartInfo
        {
            FileName = tools.Name + ".cmd",
            Environment = new Dictionary<string, string?> { ["PATH"] = tools.Path },
        };

        var (applicationName, commandLine) = WindowsPtyBackend.BuildLaunch(startInfo);

        await Assert.That(applicationName).IsEqualTo(Path.Combine(Environment.SystemDirectory, "cmd.exe"));
        await Assert.That(new string(commandLine, 0, commandLine.Length - 1)).IsEqualTo($"cmd.exe /e:ON /v:OFF /d /c \"\"{script}\"\"");
    }

    [Test]
    public async Task OverlayThatLeavesPathAloneLeavesTheLookupToCreateProcess()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // The child's PATH is then the host's, which CreateProcess searches itself.
        using var tools = new ToolDirectory();
        var startInfo = new PtyStartInfo
        {
            FileName = tools.Name,
            Environment = new Dictionary<string, string?> { ["MINIPTY_TEST_OTHER"] = "1" },
        };

        var (applicationName, _) = WindowsPtyBackend.BuildLaunch(startInfo);

        await Assert.That(applicationName).IsNull();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    public async Task RemovedOrEmptyPathSearchesTheFixedLocationsOnly(string? path)
    {
        if (!OperatingSystem.IsWindows())
            return;

        // The system directory is still searched, as CreateProcess does with no PATH; a tool on the host's PATH alone
        // is not. An empty PATH reaches a Windows child as none, and is searched as none.
        using var tools = new ToolDirectory();
        var environment = new Dictionary<string, string?> { ["PATH"] = path };

        var (applicationName, _) = WindowsPtyBackend.BuildLaunch(new PtyStartInfo { FileName = "whoami", Environment = environment });
        await Assert.That(applicationName).IsEqualTo(Path.Combine(Environment.SystemDirectory, "whoami.exe"));

        var error = Assert.Throws<Win32Exception>(() => WindowsPtyBackend.BuildLaunch(new PtyStartInfo { FileName = tools.Name, Environment = environment }));
        await Assert.That(error.NativeErrorCode).IsEqualTo(2);
    }

    [Test]
    public async Task OverlayPathKeyIsMatchedIgnoringCase()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var tools = new ToolDirectory();
        var startInfo = new PtyStartInfo
        {
            FileName = tools.Name,
            Environment = new Dictionary<string, string?> { ["path"] = tools.Path },
        };

        var (applicationName, _) = WindowsPtyBackend.BuildLaunch(startInfo);

        await Assert.That(applicationName).IsEqualTo(tools.Program);
    }

    [Test]
    [Arguments("rooted")]
    [Arguments(".\\")]
    [Arguments("..\\")]
    public async Task RootedAndDotRelativeNamesAreLeftToCreateProcess(string shape)
    {
        if (!OperatingSystem.IsWindows())
            return;

        // CreateProcess does not search for these, so the child's PATH plays no part and nothing is resolved here.
        using var tools = new ToolDirectory();
        var startInfo = new PtyStartInfo
        {
            FileName = shape == "rooted" ? tools.Program : shape + tools.Name + ".exe",
            Environment = new Dictionary<string, string?> { ["PATH"] = tools.Path },
        };

        var (applicationName, _) = WindowsPtyBackend.BuildLaunch(startInfo);

        await Assert.That(applicationName).IsNull();
    }

    [Test]
    public async Task BatchFileWithARawCommandLineIsLeftToCreateProcessEvenWithAChildPath()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // Given as an application name, CreateProcess rebuilds the command line for cmd.exe and breaks its quoting, so
        // the raw-command-line contract keeps the batch file with CreateProcess, which searches the host's PATH.
        using var tools = new ToolDirectory();
        File.WriteAllText(Path.Combine(tools.Path, tools.Name + ".cmd"), "@echo off\r\n");
        var startInfo = new PtyStartInfo
        {
            FileName = tools.Name + ".cmd",
            CommandLine = "a",
            Environment = new Dictionary<string, string?> { ["PATH"] = tools.Path },
        };

        var (applicationName, _) = WindowsPtyBackend.BuildLaunch(startInfo);

        await Assert.That(applicationName).IsNull();
    }

    [Test]
    public async Task MissingProgramFailsBeforeAnythingStarts()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var tools = new ToolDirectory();
        var startInfo = new PtyStartInfo
        {
            FileName = tools.Name + "-missing",
            Environment = new Dictionary<string, string?> { ["PATH"] = tools.Path },
        };

        var error = Assert.Throws<Win32Exception>(() => Pty.Start(startInfo));

        await Assert.That(error.NativeErrorCode).IsEqualTo(2);
    }

    [Test]
    public async Task ChildRunsAProgramFoundOnItsOwnPath()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // hostname.exe prints the computer name and exits 0; the name has no extension, as a host would write it.
        using var tools = new ToolDirectory();
        await using var session = Pty.Start(new PtyStartInfo
        {
            FileName = tools.Name,
            Environment = new Dictionary<string, string?> { ["PATH"] = tools.Path },
        });

        var result = await session.CompleteAsync().WaitAsync(Timeout);

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(PtyOutput.ToDisplayText(result.GetText(), PtyOutputDisplayMode.PlainText))
            .Contains(Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    }

    [Test]
    [NotInParallel]
    public async Task HostPathIsNotSearchedWhenTheChildPathIsSet()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // The tool is on the host's PATH for the duration of this test and not on the child's. Without the overlay
        // CreateProcess finds it there; with the overlay the child's PATH decides, and it is not found.
        using var tools = new ToolDirectory();
        var hostPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", tools.Path + ";" + hostPath);
        try
        {
            await using (var session = Pty.Start(new PtyStartInfo { FileName = tools.Name }))
            {
                var result = await session.CompleteAsync().WaitAsync(Timeout);
                await Assert.That(result.ExitCode).IsEqualTo(0);
            }

            var error = Assert.Throws<Win32Exception>(() => Pty.Start(new PtyStartInfo
            {
                FileName = tools.Name,
                Environment = new Dictionary<string, string?> { ["PATH"] = Path.Combine(tools.Path, "empty") },
            }));
            await Assert.That(error.NativeErrorCode).IsEqualTo(2);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", hostPath);
        }
    }

    [Test]
    [NotInParallel]
    public async Task ChildPathEntriesRelativeToTheCurrentDirectoryDoNotStartAPlantedProgram()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // The host runs with its current directory where a program was planted, and with the implicit current-directory
        // search turned off. CreateProcess would still start the planted program for each of these PATH entries, since
        // they all point at the current directory; MiniPty's child-PATH lookup searches only fully qualified entries.
        using var tools = new ToolDirectory();
        var currentDirectory = Environment.CurrentDirectory;
        var noDefault = Environment.GetEnvironmentVariable("NoDefaultCurrentDirectoryInExePath");
        var drive = Path.GetPathRoot(tools.Path)!.TrimEnd('\\');
        var rootRelative = tools.Path[drive.Length..];
        string[] paths =
        [
            ";" + tools.EmptyPath,             // leading empty entry, as extra + ";" + PATH with an empty extra
            ".;" + tools.EmptyPath,
            tools.EmptyPath + ";;" + tools.EmptyPath,
            "empty\\..",                       // relative
            drive + "empty\\..",               // drive-relative: relative to the current directory of that drive
            rootRelative,                      // root-relative: the same directory on the current drive
        ];
        try
        {
            Environment.CurrentDirectory = tools.Path;
            Environment.SetEnvironmentVariable("NoDefaultCurrentDirectoryInExePath", "1");

            // Every form is tried before asserting, so a failure names each one that started the planted program.
            var outcomes = new List<string>();
            foreach (var path in paths)
            {
                try
                {
                    var (found, _) = WindowsPtyBackend.BuildLaunch(new PtyStartInfo
                    {
                        FileName = tools.Name,
                        Environment = new Dictionary<string, string?> { ["PATH"] = path },
                    });
                    outcomes.Add($"{path}: found {found}");
                }
                catch (Win32Exception e)
                {
                    outcomes.Add($"{path}: error {e.NativeErrorCode}");
                }
            }

            await Assert.That(string.Join(" | ", outcomes)).IsEqualTo(string.Join(" | ", paths.Select(static path => $"{path}: error 2")));

            // The same directory as a fully qualified entry is searched.
            var (applicationName, _) = WindowsPtyBackend.BuildLaunch(new PtyStartInfo
            {
                FileName = tools.Name,
                Environment = new Dictionary<string, string?> { ["PATH"] = tools.Path },
            });
            await Assert.That(applicationName).IsEqualTo(tools.Program);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NoDefaultCurrentDirectoryInExePath", noDefault);
            Environment.CurrentDirectory = currentDirectory;
        }
    }

    /// <summary>
    /// A fresh directory holding a copy of <c>hostname.exe</c> under a unique name, so that no fixed location and no
    /// entry of the host's <c>PATH</c> holds a file of that name.
    /// </summary>
    private sealed class ToolDirectory : IDisposable
    {
        public ToolDirectory()
        {
            Name = $"minipty-tool-{Guid.NewGuid():N}";
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Name);
            Directory.CreateDirectory(Path);
            Directory.CreateDirectory(System.IO.Path.Combine(Path, "empty"));
            Program = System.IO.Path.Combine(Path, Name + ".exe");
            File.Copy(System.IO.Path.Combine(Environment.SystemDirectory, "hostname.exe"), Program);
        }

        /// <summary>The program's name without its extension.</summary>
        public string Name { get; }

        /// <summary>The directory, for the child's <c>PATH</c>.</summary>
        public string Path { get; }

        /// <summary>A fully qualified directory under <see cref="Path"/> that holds nothing.</summary>
        public string EmptyPath => System.IO.Path.Combine(Path, "empty");

        /// <summary>The full path of the program.</summary>
        public string Program { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
