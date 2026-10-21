using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using MiniPty.Internal;

namespace MiniPty.Tests;

/// <summary>
/// Batch files on Windows (<c>WindowsBatchFile</c>): arguments reach the script, and a program the script forwards
/// them to, as data. <c>cmd.exe</c> parses them by its own rules, under which an argument quoted by the MSVCRT rules
/// can close the quoting and run a command of its own.
/// </summary>
/// <remarks>
/// The program behind the scripts is this test executable, which prints its arguments when
/// <see cref="ArgvChildVariable"/> is set (<see cref="RunAsChildWhenAsked"/>). A script that forwards <c>%*</c>
/// to it is what a tool's batch shim does.
/// </remarks>
public sealed partial class WindowsBatchFileTests
{
    private const string ArgvChildVariable = "MINIPTY_TEST_ARGV_CHILD";
    private const string LaunchChildVariable = "MINIPTY_TEST_LAUNCH_CHILD";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>One argument per equivalence class of the escaping, all in one run.</summary>
    private static readonly string[] Tricky =
    [
        "plain",
        "",
        "two words",
        "x\" & echo INJECTED & \"",   // closes MSVCRT quoting under cmd.exe without the escaping
        "a&b",
        "a|b",
        "a<b>c",
        "a^b",
        "(a)",
        "50%",
        "%PATH%",
        "!PATH!",
        "C:\\dir\\",
        "a\\\"b",
        "\"\"",
        "-x=1,2;3",
        "a\tb",
        "日本語",
        "#$*+-./:?@_x",                // the symbols an argument may hold without quotes
        "a　b",                   // cmd.exe splits a script's parameters on it outside quotes
    ];

    [Test]
    public async Task ArgumentsReachAProgramBehindABatchShimUnchanged()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var shim = WriteScript($"@\"{PtyBaselineTests.TestExecutable}\" %*");
        try
        {
            var (text, exitCode) = await RunAsync(new PtyStartInfo { FileName = shim, Arguments = Tricky, Environment = ArgvChild });

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(Argv(text)).IsEquivalentTo(Tricky, StringComparer.Ordinal, TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(HasLine(text, "INJECTED")).IsFalse();
        }
        finally
        {
            File.Delete(shim);
        }
    }

    [Test]
    [Arguments("x\" & echo INJECTED & \"")]
    [Arguments("\"&echo INJECTED&\"")]
    [Arguments("&echo INJECTED")]
    [Arguments("^\"&echo INJECTED")]
    [Arguments("|echo INJECTED")]
    public async Task ArgumentsUsedByTheScriptDoNotRunCommands(string argument)
    {
        if (!OperatingSystem.IsWindows())
            return;

        // %1 and %* keep the quotes MiniPty wrote, so the script's own command line keeps them too. The payloads with
        // a quote close quoting written by the MSVCRT rules. The bare & and | are inside quotes under either rule, so
        // they pass without the cmd.exe escaping.
        var script = WriteScript("@echo ONE=%1\r\n@echo ALL=%*");
        try
        {
            var (text, exitCode) = await RunAsync(new PtyStartInfo { FileName = script, Arguments = [argument, "second"] });

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(HasLine(text, "INJECTED")).IsFalse();
            await Assert.That(text).Contains("ALL=");
        }
        finally
        {
            File.Delete(script);
        }
    }

    [Test]
    [Arguments(".")]
    [Arguments(" ")]
    [Arguments(". .")]
    public async Task TrailingDotsAndSpacesStillMakeABatchFile(string suffix)
    {
        if (!OperatingSystem.IsWindows())
            return;

        // Windows drops them and opens the .cmd file, so the escaping must apply too.
        var shim = WriteScript($"@\"{PtyBaselineTests.TestExecutable}\" %*");
        try
        {
            string[] arguments = ["x\" & echo INJECTED & \""];
            var (text, _) = await RunAsync(new PtyStartInfo { FileName = shim + suffix, Arguments = arguments, Environment = ArgvChild });

            await Assert.That(Argv(text)).IsEquivalentTo(arguments, StringComparer.Ordinal, TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(HasLine(text, "INJECTED")).IsFalse();
        }
        finally
        {
            File.Delete(shim);
        }
    }

    [Test]
    [Arguments("\\.")]
    [Arguments("/.")]
    [Arguments("\\missing\\..")]
    public async Task NameThatWindowsNormalizesToABatchFileStillMakesABatchFile(string suffix)
    {
        if (!OperatingSystem.IsWindows())
            return;

        // The name as given ends with neither .cmd nor a dot or space, yet CreateProcess opens the .cmd file and starts
        // cmd.exe for it. Without the escaping the injected echo runs.
        var shim = WriteScript($"@\"{PtyBaselineTests.TestExecutable}\" %*");
        try
        {
            string[] arguments = ["x\" & echo INJECTED & \""];
            var (text, _) = await RunAsync(new PtyStartInfo { FileName = shim + suffix, Arguments = arguments, Environment = ArgvChild });

            await Assert.That(Argv(text)).IsEquivalentTo(arguments, StringComparer.Ordinal, TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(HasLine(text, "INJECTED")).IsFalse();
        }
        finally
        {
            File.Delete(shim);
        }
    }

    [Test]
    [Arguments("\\. ")]
    [Arguments("\\sub\\.. ")]
    [Arguments("\\")]
    public async Task NameThatEndsAsADirectoryFailsBeforeAnythingStarts(string suffix)
    {
        if (!OperatingSystem.IsWindows())
            return;

        // CreateProcess starts cmd.exe for the first two, and an argument would reach it unescaped. Windows opens none
        // of them as the script, so the lookup fails instead.
        var shim = WriteScript($"@\"{PtyBaselineTests.TestExecutable}\" %*");
        try
        {
            var error = Assert.Throws<Win32Exception>(() =>
                Pty.Start(new PtyStartInfo { FileName = shim + suffix, Arguments = ["x\" & echo INJECTED & \""] }));

            await Assert.That(error.NativeErrorCode).IsEqualTo(2);
        }
        finally
        {
            File.Delete(shim);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BareNameIsLookedUpFromTheHostsCurrentDirectoryAndPath(bool noDefaultCurrentDirectory)
    {
        if (!OperatingSystem.IsWindows())
            return;

        // The lookup runs in a child test host whose current directory and PATH each hold a script of the name, so the
        // host's real locations decide which one is found. NoDefaultCurrentDirectoryInExePath takes the current
        // directory out of the search, which leaves the PATH copy.
        var root = Path.Combine(AppContext.BaseDirectory, $"minipty-lookup-{Guid.NewGuid():N}");
        var work = Path.Combine(root, "work");
        var pathEntry = Path.Combine(root, "path");
        var name = $"minipty-lookup-{Guid.NewGuid():N}.cmd";
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(pathEntry);
        File.WriteAllText(Path.Combine(work, name), "@echo off\r\n");
        File.WriteAllText(Path.Combine(pathEntry, name), "@echo off\r\n");
        try
        {
            var (text, _) = await RunAsync(new PtyStartInfo
            {
                FileName = PtyBaselineTests.TestExecutable,
                WorkingDirectory = work,
                Environment = new Dictionary<string, string?>
                {
                    [LaunchChildVariable] = name,
                    ["PATH"] = pathEntry,
                    ["NoDefaultCurrentDirectoryInExePath"] = noDefaultCurrentDirectory ? "1" : null,
                },
            });

            var expected = Path.Combine(noDefaultCurrentDirectory ? pathEntry : work, name);
            await Assert.That(HasLine(text, "RESOLVED:" + expected)).IsTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task InterpreterIsNotTakenFromComSpec()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // ComSpec names another program in the child test host, so only an interpreter path that ignores it matches.
        var (text, _) = await RunAsync(new PtyStartInfo
        {
            FileName = PtyBaselineTests.TestExecutable,
            Environment = new Dictionary<string, string?>
            {
                [LaunchChildVariable] = "x.cmd",
                ["ComSpec"] = Path.Combine(Environment.SystemDirectory, "whoami.exe"),
            },
        });

        await Assert.That(HasLine(text, "INTERPRETER:" + Path.Combine(Environment.SystemDirectory, "cmd.exe"))).IsTrue();
    }

    [Test]
    public async Task ScriptUnderADirectoryWithCmdSpecialCharactersRuns()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // Every character here is legal in a Windows path and special to cmd.exe, so the script path must be quoted and
        // its % escaped like an argument's. %OS% is a variable every Windows defines, so a path left unescaped would
        // expand to another directory. A lone % would not expand.
        var directory = Path.Combine(AppContext.BaseDirectory, $"minipty R&D %OS% 100% !x! (a)^b {Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var shim = Path.Combine(directory, "my shim.cmd");
        File.WriteAllText(shim, $"@\"{PtyBaselineTests.TestExecutable}\" %*\r\n", Encoding.ASCII);
        try
        {
            string[] arguments = ["x\" & echo INJECTED & \"", "%PATH%", "a b", ""];
            var (text, exitCode) = await RunAsync(new PtyStartInfo { FileName = shim, Arguments = arguments, Environment = ArgvChild });

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(Argv(text)).IsEquivalentTo(arguments, StringComparer.Ordinal, TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(HasLine(text, "INJECTED")).IsFalse();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task CommandLineAtCmdsLimitRunsAndOneMoreIsRejected()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // The script runs at the limit, so the constant is not above what this machine's cmd.exe accepts. One more
        // character is MiniPty's own rejection, which CmdRunsTheLimitAndRefusesOneMore shows cmd.exe would make too.
        var script = WriteScript("@echo OK");
        try
        {
            // "cmd.exe /e:ON /v:OFF /d /c \"" (28), the quoted script, a space, the argument, the closing quote.
            var atLimit = WindowsBatchFile.CommandLineLimit - (28 + script.Length + 2 + 1 + 1);
            var (text, exitCode) = await RunAsync(new PtyStartInfo { FileName = script, Arguments = [new string('a', atLimit)] });

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(HasLine(text, "OK")).IsTrue();

            var error = Assert.Throws<ArgumentException>(() => Pty.Start(new PtyStartInfo { FileName = script, Arguments = [new string('a', atLimit + 1)] }));
            await Assert.That(error.ParamName).IsEqualTo("startInfo");
        }
        finally
        {
            File.Delete(script);
        }
    }

    [Test]
    [Arguments('　')]   // ideographic space, common in Japanese text
    [Arguments(' ')]   // no-break space
    [Arguments(' ')]   // em space
    [Arguments('᠎')]   // not white space to .NET, yet cmd.exe splits on it
    public async Task NonAsciiSpaceDoesNotSplitAScriptsParameters(char space)
    {
        if (!OperatingSystem.IsWindows())
            return;

        // cmd.exe splits %1, %2, ... on these characters outside quotes, so only quoting keeps the argument whole.
        var script = WriteScript("@echo [1=%1] [2=%2] [3=%3]");
        try
        {
            var (text, exitCode) = await RunAsync(new PtyStartInfo { FileName = script, Arguments = ["a" + space + "b", "second"] });

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(text).Contains("[2=second]");
            await Assert.That(text).Contains("[3=]");
        }
        finally
        {
            File.Delete(script);
        }
    }

    [Test]
    [Arguments("missing-directory")]
    [Arguments("existing-directory")]
    public async Task MissingBatchFileWithADirectoryFailsAsFromCreateProcess(string shape)
    {
        if (!OperatingSystem.IsWindows())
            return;

        // A rooted name is not searched for, so only MiniPty's own check of the file can fail it before cmd.exe starts.
        var directory = Path.Combine(AppContext.BaseDirectory, $"minipty-missing-{Guid.NewGuid():N}");
        if (shape == "existing-directory")
            Directory.CreateDirectory(directory);
        try
        {
            var error = Assert.Throws<Win32Exception>(() => Pty.Start(new PtyStartInfo { FileName = Path.Combine(directory, "x.cmd"), Arguments = ["a"] }));

            await Assert.That(error.NativeErrorCode).IsEqualTo(2);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task ExecutableNamedLikeABatchFileRunsAsAProgram()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // CreateProcess loads an image by its content and checks the extension only for a file that is not one, so a
        // program named .cmd runs as that program. Handed to cmd.exe instead, it would be read as batch lines.
        var program = Path.Combine(AppContext.BaseDirectory, $"minipty-pe-{Guid.NewGuid():N}.cmd");
        File.Copy(Path.Combine(Environment.SystemDirectory, "hostname.exe"), program);
        try
        {
            var (text, exitCode) = await RunAsync(new PtyStartInfo { FileName = program });

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(text).Contains(Environment.MachineName, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(program);
        }
    }

    [Test]
    [Arguments("\r")]
    [Arguments("\n")]
    [Arguments("\0")]
    [Arguments("\"")]
    public async Task BatchFileNameThatCmdCannotReceiveIsRejected(string character)
    {
        if (!OperatingSystem.IsWindows())
            return;

        // No file has this name. A lookup made first would fail with Win32Exception, or for NUL with an
        // ArgumentException for another parameter, so the ParamName shows that the name check rejected it.
        var script = WriteScript("@exit /b 0");
        var name = Path.Combine(Path.GetDirectoryName(script)!, "t" + character + Path.GetFileName(script));
        try
        {
            var error = Assert.Throws<ArgumentException>(() => Pty.Start(new PtyStartInfo { FileName = name, Arguments = ["a"] }));

            await Assert.That(error.ParamName).IsEqualTo("startInfo");
        }
        finally
        {
            File.Delete(script);
        }
    }

    [Test]
    public async Task CmdRunsTheLimitAndRefusesOneMore()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // Starts cmd.exe as a program with a raw command line, which MiniPty does not check, so the refusal at one more
        // character is cmd.exe's own. That pins CommandLineLimit to cmd.exe's real limit from both sides.
        var script = WriteScript("@echo OK");
        try
        {
            var interpreter = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            // cmd.exe counts its whole command line: the quoted interpreter path MiniPty writes first, then this.
            var head = $"/e:ON /v:OFF /d /c \"\"{script}\" ";
            var room = WindowsBatchFile.CommandLineLimit - (interpreter.Length + 2 + 1 + head.Length + 1);
            string Line(int letters) => head + new string('a', letters) + "\"";

            var (atLimit, atLimitExit) = await RunAsync(new PtyStartInfo { FileName = interpreter, CommandLine = Line(room) });
            var (overLimit, overLimitExit) = await RunAsync(new PtyStartInfo { FileName = interpreter, CommandLine = Line(room + 1) });

            // cmd.exe's message is translated, so the refusal is told by its exit code and the script's missing line.
            await Assert.That(atLimitExit).IsEqualTo(0);
            await Assert.That(HasLine(atLimit, "OK")).IsTrue();
            await Assert.That(overLimitExit).IsEqualTo(1);
            await Assert.That(HasLine(overLimit, "OK")).IsFalse();
        }
        finally
        {
            File.Delete(script);
        }
    }

    [Test]
    public async Task BatchFileExitCodeIsReported()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var script = WriteScript("@exit /b 3");
        try
        {
            var (_, exitCode) = await RunAsync(new PtyStartInfo { FileName = script, Arguments = ["a b"] });

            await Assert.That(exitCode).IsEqualTo(3);
        }
        finally
        {
            File.Delete(script);
        }
    }

    [Test]
    [Arguments("a\rb")]
    [Arguments("a\nb")]
    [Arguments("a\0b")]
    public async Task ArgumentThatCmdCannotReceiveIsRejectedBeforeAnythingStarts(string argument)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var script = WriteScript("@exit /b 0");
        try
        {
            var error = Assert.Throws<ArgumentException>(() => Pty.Start(new PtyStartInfo { FileName = script, Arguments = [argument] }));

            await Assert.That(error.ParamName).IsEqualTo("startInfo");
        }
        finally
        {
            File.Delete(script);
        }
    }

    [Test]
    public async Task BareBatchFileNameIsFoundInTheHostsDirectory()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // A bare name is looked up in this process's real locations. The test host is this test executable, so the
        // script is in the host executable's directory. WindowsBatchFileLookupTests tests the order of the locations.
        var hostDirectory = Path.GetDirectoryName(Environment.ProcessPath)!;
        await Assert.That(Path.TrimEndingDirectorySeparator(hostDirectory))
            .IsEqualTo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));

        var shim = WriteScript($"@\"{PtyBaselineTests.TestExecutable}\" %*");
        try
        {
            string[] arguments = ["a b", "50%"];
            var (text, _) = await RunAsync(new PtyStartInfo { FileName = Path.GetFileName(shim), Arguments = arguments, Environment = ArgvChild });

            await Assert.That(Argv(text)).IsEquivalentTo(arguments, StringComparer.Ordinal, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        }
        finally
        {
            File.Delete(shim);
        }
    }

    [Test]
    public async Task MissingBareBatchFileFailsAsFromCreateProcess()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var error = Assert.Throws<Win32Exception>(() => Pty.Start(new PtyStartInfo { FileName = $"minipty-missing-{Guid.NewGuid():N}.cmd", Arguments = ["a"] }));

        await Assert.That(error.NativeErrorCode).IsEqualTo(2);
    }

    /// <summary>
    /// Makes this test executable act as a child for these tests, then exit. With <see cref="ArgvChildVariable"/> set
    /// it prints its arguments, one <c>ARGV:</c> line of UTF-8 hex each. With <see cref="LaunchChildVariable"/> set it
    /// prints where its own batch-file lookup finds the named script and which interpreter it would start. Runs before
    /// the test framework starts. In a test run both variables are unset, and this returns at once.
    /// </summary>
    [ModuleInitializer]
    internal static void RunAsChildWhenAsked()
    {
        if (Environment.GetEnvironmentVariable(LaunchChildVariable) is { } scriptName)
        {
            string resolved;
            try
            {
                resolved = WindowsBatchFile.ResolveScript(scriptName);
            }
            catch (Win32Exception e)
            {
                resolved = "ERROR " + e.NativeErrorCode;
            }

            System.Console.Out.Write($"RESOLVED:{resolved}\r\nINTERPRETER:{WindowsBatchFile.CommandInterpreterPath}\r\n");
            System.Console.Out.Flush();
            Environment.Exit(0);
        }

        if (Environment.GetEnvironmentVariable(ArgvChildVariable) is null)
            return;

        var output = new StringBuilder();
        foreach (var arg in Environment.GetCommandLineArgs().AsSpan(1))
            output.Append("ARGV:").Append(Convert.ToHexString(Encoding.UTF8.GetBytes(arg))).Append("\r\n");
        output.Append("ARGV:END\r\n");
        System.Console.Out.Write(output.ToString());
        System.Console.Out.Flush();
        Environment.Exit(0);
    }

    private static readonly IReadOnlyDictionary<string, string?> ArgvChild = new Dictionary<string, string?> { [ArgvChildVariable] = "1" };

    private static string WriteScript(string body)
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"minipty-batch-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(path, body + "\r\n", Encoding.ASCII);
        return path;
    }

    private static async Task<(string Text, int ExitCode)> RunAsync(PtyStartInfo startInfo)
    {
        // Wide enough that no ARGV line wraps.
        await using var session = Pty.Start(startInfo with { Size = new PtySize(500, 50) });
        var result = await session.CompleteAsync().WaitAsync(Timeout);
        return (PtyOutput.ToDisplayText(result.GetText(), PtyOutputDisplayMode.PlainText), result.ExitCode);
    }

    private static List<string> Argv(string text)
    {
        var argv = new List<string>();
        foreach (Match match in ArgvLine().Matches(text))
        {
            if (match.Groups[1].Value == "END")
                return argv;

            argv.Add(Encoding.UTF8.GetString(Convert.FromHexString(match.Groups[1].Value)));
        }

        throw new InvalidOperationException("The argument printer did not finish: " + text);
    }

    /// <summary>
    /// Whether a line starts with <paramref name="prefix"/>: the output of an injected <c>echo</c>, which may carry a
    /// stray quote after it. The scripts' own lines start with their labels.
    /// </summary>
    private static bool HasLine(string text, string prefix)
    {
        foreach (var candidate in text.Split('\n'))
        {
            if (candidate.TrimStart().StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    // END first: the hex branch would otherwise match its E.
    [GeneratedRegex("ARGV:(END|[0-9A-F]*)")]
    private static partial Regex ArgvLine();
}
