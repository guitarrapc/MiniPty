using System.ComponentModel;
using MiniPty.Internal;

namespace MiniPty.Tests;

/// <summary>
/// <c>CreateProcessW</c> command-line construction. The command lines are pure functions and are tested on every
/// platform. The launch choice looks the script up through Windows APIs, so its tests run on Windows only.
/// </summary>
public sealed class WindowsCommandLineTests
{
    [Test]
    [Arguments("plain", "plain")]                       // nothing to quote
    [Arguments("", "\"\"")]                              // empty argument
    [Arguments("a b", "\"a b\"")]                        // whitespace
    [Arguments("a\tb", "\"a\tb\"")]                      // non-space whitespace
    [Arguments("a\"b", "\"a\\\"b\"")]                    // embedded quote
    [Arguments("C:\\dir\\file", "\"C:\\dir\\file\"")]    // backslashes not before a quote stay single
    [Arguments("C:\\dir\\", "\"C:\\dir\\\\\"")]          // trailing backslash doubles before the closing quote
    [Arguments("a\\\"b", "\"a\\\\\\\"b\"")]              // backslash before an embedded quote doubles, plus the escape
    [Arguments("a\\\\\"b", "\"a\\\\\\\\\\\"b\"")]        // two backslashes before a quote become four, plus the escape
    public async Task ArgumentIsQuotedWithMsvcrtRules(string argument, string expected)
    {
        var commandLine = Build(new PtyStartInfo { FileName = "app", Arguments = [argument] });

        await Assert.That(commandLine).IsEqualTo("app " + expected);
    }

    [Test]
    public async Task FileNameIsQuotedLikeAnArgument()
    {
        var commandLine = Build(new PtyStartInfo { FileName = "C:\\Program Files\\app.exe" });

        await Assert.That(commandLine).IsEqualTo("\"C:\\Program Files\\app.exe\"");
    }

    [Test]
    public async Task ArgumentsAreJoinedWithSingleSpaces()
    {
        var commandLine = Build(new PtyStartInfo { FileName = "app", Arguments = ["one", "two words", ""] });

        await Assert.That(commandLine).IsEqualTo("app one \"two words\" \"\"");
    }

    [Test]
    public async Task RawCommandLineIsAppendedVerbatim()
    {
        var commandLine = Build(new PtyStartInfo { FileName = "cmd.exe", CommandLine = "/c echo \"a b\" \\x" });

        await Assert.That(commandLine).IsEqualTo("cmd.exe /c echo \"a b\" \\x");
    }

    [Test]
    public async Task BufferIsExactlyTheCommandLinePlusOneNul()
    {
        var buffer = WindowsPtyBackend.BuildCommandLine(new PtyStartInfo { FileName = "app", Arguments = ["a\\\"b", "x y\\"] });

        await Assert.That(buffer[^1]).IsEqualTo('\0');
        await Assert.That(Array.IndexOf(buffer, '\0')).IsEqualTo(buffer.Length - 1);
    }

    [Test]
    public async Task MatchesStringBuilderReferenceForRandomArguments()
    {
        // Characters that drive every quoting branch: plain, whitespace, quote, backslash.
        const string Alphabet = "ab \t\"\\";
        var random = new Random(20261015);
        for (var iteration = 0; iteration < 2000; iteration++)
        {
            var arguments = new string[random.Next(0, 4)];
            for (var i = 0; i < arguments.Length; i++)
            {
                var chars = new char[random.Next(0, 8)];
                for (var c = 0; c < chars.Length; c++)
                    chars[c] = Alphabet[random.Next(Alphabet.Length)];
                arguments[i] = new string(chars);
            }

            var expected = "app";
            foreach (var argument in arguments)
                expected += " " + ReferenceQuote(argument);

            await Assert.That(Build(new PtyStartInfo { FileName = "app", Arguments = arguments })).IsEqualTo(expected);
        }
    }

    // ---------------------------------------------------------------- Batch files (WindowsBatchFile)

    [Test]
    [Arguments("x.bat", true)]
    [Arguments("X.CMD", true)]                 // the extension is case-insensitive
    [Arguments("C:\\dir\\x.Cmd", true)]        // with a directory
    [Arguments("x.cmd.", true)]                // Windows drops trailing dots...
    [Arguments("x.cmd ", true)]                // ...and spaces, so these open x.cmd
    [Arguments("x.cmd. .", true)]
    [Arguments("x.exe", false)]
    [Arguments("x.cmdx", false)]
    [Arguments("x.bat.txt", false)]
    [Arguments("C:\\x.cmd\\app.exe", false)]   // a directory named like a batch file
    [Arguments("cmd", false)]
    public async Task BatchFileIsRecognizedByItsEffectiveExtension(string fileName, bool expected)
    {
        await Assert.That(WindowsBatchFile.IsBatchFile(fileName)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("C:\\dir\\x.cmd\\.", true)]
    [Arguments("C:\\dir\\x.cmd/.", true)]
    [Arguments("C:\\dir\\x.cmd\\sub\\..", true)]
    [Arguments("C:\\dir\\x.cmd\\.\\.", true)]
    [Arguments("C:\\dir\\x.cmd\\. ", true)]
    [Arguments("x.cmd\\.", true)]
    [Arguments(".\\x.cmd\\.", true)]
    [Arguments("C:\\dir\\x.cmd \\.", true)]
    [Arguments("C:\\dir\\x.cmd\\sub\\.. ", true)]
    [Arguments("C:\\dir\\x.cmd\\", true)]               // no file matches, so the lookup fails
    [Arguments("a\\..\\..\\x.cmd", true)]
    [Arguments("C:\\dir\\x.cmd\\..", false)]           // the directory that holds x.cmd
    [Arguments("C:\\dir\\x.cmd\\.. ", false)]
    [Arguments("C:\\dir\\x.cmd\\..\\app.exe", false)]
    [Arguments("x.cmd\\..", false)]
    [Arguments("..", false)]
    public async Task BatchFileIsRecognizedByTheNameWindowsOpens(string fileName, bool expected)
    {
        // Each name ends in something other than .cmd, so only the last component left after the dot segments, with
        // its trailing dots and spaces dropped, shows what CreateProcess opens.
        await Assert.That(WindowsBatchFile.IsBatchFile(fileName)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("plain", "plain")]                              // letters only: unquoted
    [Arguments("C:\\dir\\file", "C:\\dir\\file")]              // backslash and colon need no quotes
    [Arguments("--name=1", "\"--name=1\"")]                    // '=' is a cmd delimiter: quoted
    [Arguments("", "\"\"")]                                    // empty
    [Arguments("a b", "\"a b\"")]                              // space
    [Arguments("a\tb", "\"a\tb\"")]                            // control character
    [Arguments("x\" & calc & \"", "\"x\"\" & calc & \"\"\"")]  // a quote doubles: cmd's quoting closes and reopens
    [Arguments("a\\\"b", "\"a\\\\\"\"b\"")]                    // a backslash before a quote doubles for MSVCRT
    [Arguments("C:\\dir\\", "\"C:\\dir\\\\\"")]                // a trailing backslash forces quotes and doubles
    [Arguments("50%", "\"50%%cd:~,%\"")]                       // percent cannot start an expansion
    [Arguments("%PATH%", "\"%%cd:~,%PATH%%cd:~,%\"")]
    [Arguments("a&b|c<d>e^f(g)", "\"a&b|c<d>e^f(g)\"")]        // cmd metacharacters stay inside quotes
    [Arguments("!PATH!", "\"!PATH!\"")]                        // inert under /v:OFF, quoted like any symbol
    [Arguments("日本語", "\"日本語\"")]                          // non-ASCII text: quoted, as cmd.exe splits on some of it
    [Arguments("a　b", "\"a　b\"")]                    // ideographic space
    [Arguments("a\u0085b", "\"a\u0085b\"")]                    // a C1 control character
    public async Task BatchArgumentIsEscapedForCmd(string argument, string expected)
    {
        var commandLine = BuildBatch("C:\\s\\x.cmd", [argument]);

        await Assert.That(commandLine).IsEqualTo("cmd.exe /e:ON /v:OFF /d /c \"\"C:\\s\\x.cmd\" " + expected + "\"");
    }

    [Test]
    public async Task EveryAsciiCharacterOutsideTheAllowListIsQuoted()
    {
        // The allow list is written out here apart from the code, and each character sits between letters, so it alone
        // decides whether the argument is quoted.
        const string AllowList = "#$*+-./:?@\\_";
        for (var c = (char)1; c < 128; c++)
        {
            if (c is '\r' or '\n' or '%' or '"')
                continue;

            var argument = "a" + c + "b";
            var quoted = !char.IsAsciiLetterOrDigit(c) && !AllowList.Contains(c);

            await Assert.That(BuildBatch("C:\\s\\x.cmd", [argument])).IsEqualTo(
                "cmd.exe /e:ON /v:OFF /d /c \"\"C:\\s\\x.cmd\" " + (quoted ? "\"" + argument + "\"" : argument) + "\"");
        }
    }

    [Test]
    public async Task BatchCommandLineWithoutArgumentsWrapsTheScriptInTheOuterQuotes()
    {
        await Assert.That(BuildBatch("C:\\s\\x.cmd", [])).IsEqualTo("cmd.exe /e:ON /v:OFF /d /c \"\"C:\\s\\x.cmd\"\"");
    }

    [Test]
    public async Task BatchScriptPathEscapesPercent()
    {
        var commandLine = BuildBatch("C:\\100%\\x.cmd", ["a"]);

        await Assert.That(commandLine).IsEqualTo("cmd.exe /e:ON /v:OFF /d /c \"\"C:\\100%%cd:~,%\\x.cmd\" a\"");
    }

    [Test]
    [Arguments("a\rb")]
    [Arguments("a\nb")]
    [Arguments("a\0b")]
    public async Task BatchArgumentThatCmdCannotReceiveIsRejected(string argument)
    {
        Assert.Throws<ArgumentException>(() => WindowsBatchFile.BuildCommandLine("C:\\s\\x.cmd", ["ok", argument]));
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("C:\\s\\x\".cmd")]
    [Arguments("C:\\s\\")]
    [Arguments("C:\\s\rt\\x.cmd")]
    [Arguments("C:\\s\nt\\x.cmd")]
    [Arguments("C:\\s\0t\\x.cmd")]
    public async Task BatchScriptPathThatCmdCannotReceiveIsRejected(string scriptPath)
    {
        // The full path joins the name with a PATH entry or the current directory, which ValidateName never saw, so
        // the characters cmd.exe cannot receive are checked again on the path cmd.exe gets.
        Assert.Throws<ArgumentException>(() => WindowsBatchFile.BuildCommandLine(scriptPath, []));
        await Task.CompletedTask;
    }

    // "cmd.exe /e:ON /v:OFF /d /c \"" (28) + "\"C:\s\x.cmd\"" (12) + the closing quote (1), then " " + each argument.
    private const int BatchOverheadWithOneArgument = 28 + 12 + 1 + 1;

    [Test]
    public async Task BatchCommandLineAtTheLimitIsAccepted()
    {
        var argument = new string('a', WindowsBatchFile.CommandLineLimit - BatchOverheadWithOneArgument);

        var buffer = WindowsBatchFile.BuildCommandLine("C:\\s\\x.cmd", [argument]);

        await Assert.That(buffer.Length).IsEqualTo(WindowsBatchFile.CommandLineLimit + 1);
    }

    [Test]
    public async Task BatchCommandLineOverTheLimitIsRejected()
    {
        var argument = new string('a', WindowsBatchFile.CommandLineLimit - BatchOverheadWithOneArgument + 1);

        var error = Assert.Throws<ArgumentException>(() => WindowsBatchFile.BuildCommandLine("C:\\s\\x.cmd", [argument]));

        await Assert.That(error.Message).Contains("8192");
    }

    [Test]
    public async Task EscapedPercentCountsTowardTheLimit()
    {
        // Written as is, the quoted argument would fit in 8185 characters. With the % escaped to 8 characters it needs
        // 8192. The message pins the rejection to the limit rather than to a buffer that came out too short.
        var argument = new string('a', 8140) + "%";

        var error = Assert.Throws<ArgumentException>(() => WindowsBatchFile.BuildCommandLine("C:\\s\\x.cmd", [argument]));
        await Assert.That(error.Message).Contains("8192");
        await Assert.That(BatchOverheadWithOneArgument + 2 + argument.Length).IsLessThanOrEqualTo(WindowsBatchFile.CommandLineLimit);
    }

    [Test]
    public async Task HugeArgumentsAreRejectedBeforeTheBufferIsWritten()
    {
        // Each reference to the string of 2^20 % characters takes 8,388,611 characters escaped, so the total never
        // fits. A length that came out short would fail in the writer with ParamName "destination" instead. The same
        // string is reused, so the test needs only 2 MB.
        var percents = new string('%', 1 << 20);
        var arguments = Enumerable.Repeat(percents, 512).ToArray();

        var error = Assert.Throws<ArgumentException>(() => WindowsBatchFile.BuildCommandLine("C:\\s\\x.cmd", arguments));

        await Assert.That(error.ParamName).IsEqualTo("startInfo");
        await Assert.That(error.Message).Contains("cmd.exe accepts");
    }

    [Test]
    public async Task LongScriptPathWithoutArgumentsIsRejected()
    {
        // With no arguments the check after each argument never runs, so the script path alone must be checked.
        var scriptPath = "C:\\" + new string('s', WindowsBatchFile.CommandLineLimit) + "\\x.cmd";

        var error = Assert.Throws<ArgumentException>(() => WindowsBatchFile.BuildCommandLine(scriptPath, []));

        await Assert.That(error.Message).Contains("cmd.exe accepts");
    }

    [Test]
    public async Task BatchBufferIsExactlyTheCommandLinePlusOneNul()
    {
        var buffer = WindowsBatchFile.BuildCommandLine("C:\\1%\\x.cmd", ["a\\\"b", "x y\\", "50%", ""]);

        await Assert.That(buffer[^1]).IsEqualTo('\0');
        await Assert.That(Array.IndexOf(buffer, '\0')).IsEqualTo(buffer.Length - 1);
    }

    [Test]
    public async Task BatchEscapingMatchesReferenceForRandomArguments()
    {
        // Characters that drive every branch: safe, unsafe ASCII, quote, backslash, percent, non-ASCII.
        const string Alphabet = "ab \t\"\\%&=!日";
        var random = new Random(20261020);
        for (var iteration = 0; iteration < 2000; iteration++)
        {
            var arguments = new string[random.Next(0, 4)];
            for (var i = 0; i < arguments.Length; i++)
            {
                var chars = new char[random.Next(0, 8)];
                for (var c = 0; c < chars.Length; c++)
                    chars[c] = Alphabet[random.Next(Alphabet.Length)];
                arguments[i] = new string(chars);
            }

            var expected = "cmd.exe /e:ON /v:OFF /d /c \"\"C:\\s\\x.cmd\"";
            foreach (var argument in arguments)
                expected += " " + ReferenceBatchQuote(argument);
            expected += "\"";

            await Assert.That(BuildBatch("C:\\s\\x.cmd", arguments)).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task BatchFileWithArgumentsLaunchesTheSystemCommandInterpreter()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var script = new TemporaryFile("x.cmd", "@echo off\r\n"u8);
        var (applicationName, commandLine) = WindowsPtyBackend.BuildLaunch(
            new PtyStartInfo { FileName = script.Path, Arguments = ["a b"] });

        await Assert.That(applicationName).IsEqualTo(Path.Combine(Environment.SystemDirectory, "cmd.exe"));
        await Assert.That(new string(commandLine, 0, commandLine.Length - 1))
            .IsEqualTo($"cmd.exe /e:ON /v:OFF /d /c \"\"{script.Path}\" \"a b\"\"");
    }

    [Test]
    public async Task BatchFileWithARawCommandLineIsLeftToCreateProcess()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // CommandLine is documented as passed without escaping, so the caller owns cmd's rules for it.
        var (applicationName, commandLine) = WindowsPtyBackend.BuildLaunch(
            new PtyStartInfo { FileName = "C:\\s\\x.cmd", CommandLine = "\"a b\"" });

        await Assert.That(applicationName).IsNull();
        await Assert.That(new string(commandLine, 0, commandLine.Length - 1)).IsEqualTo("\"C:\\s\\x.cmd\" \"a b\"");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BatchFileWithoutArgumentsLaunchesTheSystemCommandInterpreter(bool emptyCommandLine)
    {
        if (!OperatingSystem.IsWindows())
            return;

        // An empty raw command line counts as none, so both shapes take the escaping path and its fixed switches.
        using var script = new TemporaryFile("x.cmd", "@echo off\r\n"u8);
        var (applicationName, commandLine) = WindowsPtyBackend.BuildLaunch(
            new PtyStartInfo { FileName = script.Path, CommandLine = emptyCommandLine ? "" : null });

        await Assert.That(applicationName).IsEqualTo(Path.Combine(Environment.SystemDirectory, "cmd.exe"));
        await Assert.That(new string(commandLine, 0, commandLine.Length - 1)).IsEqualTo($"cmd.exe /e:ON /v:OFF /d /c \"\"{script.Path}\"\"");
    }

    [Test]
    public async Task ProgramImageNamedLikeABatchFileRunsFromTheFileFound()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // CreateProcess loads a file that starts with MZ as an image, or refuses it, and never hands it to cmd.exe.
        // It gets the file whose header was read as the application name, so no second lookup can pick another
        // file, and a file that becomes a script after the check cannot reach cmd.exe with program quoting.
        using var image = new TemporaryFile("x.cmd", "MZ"u8);
        var startInfo = new PtyStartInfo { FileName = image.Path, Arguments = ["a b"] };

        var (applicationName, commandLine) = WindowsPtyBackend.BuildLaunch(startInfo);

        await Assert.That(applicationName).IsEqualTo(image.Path);
        await Assert.That(new string(commandLine, 0, commandLine.Length - 1)).IsEqualTo(Build(startInfo));
    }

    [Test]
    [Arguments("M")]
    [Arguments("Mz")]
    public async Task ScriptThatIsNotAnImageRunsThroughTheCommandInterpreter(string content)
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var script = new TemporaryFile("x.cmd", System.Text.Encoding.ASCII.GetBytes(content));

        var (applicationName, _) = WindowsPtyBackend.BuildLaunch(new PtyStartInfo { FileName = script.Path, Arguments = ["a"] });

        await Assert.That(applicationName).IsEqualTo(Path.Combine(Environment.SystemDirectory, "cmd.exe"));
    }

    [Test]
    public async Task EmptyScriptFailsAsFromCreateProcess()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // CreateProcess cannot map an empty file and does not hand it to cmd.exe either.
        using var script = new TemporaryFile("x.cmd", []);

        var error = Assert.Throws<Win32Exception>(() => WindowsPtyBackend.BuildLaunch(new PtyStartInfo { FileName = script.Path, Arguments = ["a"] }));

        await Assert.That(error.NativeErrorCode).IsEqualTo(193);
    }

    [Test]
    [Arguments(FileAccess.Read, FileShare.None)]
    [Arguments(FileAccess.Write, FileShare.ReadWrite)]
    public async Task ScriptThatCannotBeOpenedFailsAsFromCreateProcess(FileAccess heldAccess, FileShare heldShare)
    {
        if (!OperatingSystem.IsWindows())
            return;

        // CreateProcess shares the file for reading only, so another handle that denies reading, or that writes, makes
        // it fail with ERROR_SHARING_VIOLATION.
        using var script = new TemporaryFile("x.cmd", "@echo off\r\n"u8);
        using var held = new FileStream(script.Path, FileMode.Open, heldAccess, heldShare);

        var error = Assert.Throws<Win32Exception>(() => WindowsPtyBackend.BuildLaunch(new PtyStartInfo { FileName = script.Path, Arguments = ["a"] }));

        await Assert.That(error.NativeErrorCode).IsEqualTo(32);
    }

    [Test]
    public async Task DanglingLinkFailsAsFromCreateProcess()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // The link itself exists, so only opening the script shows that its target does not.
        using var target = new TemporaryFile("target.cmd", "@echo off\r\n"u8);
        var link = Path.Combine(Path.GetDirectoryName(target.Path)!, "link.cmd");
        try
        {
            File.CreateSymbolicLink(link, target.Path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return; // Creating a symbolic link needs Developer Mode or the privilege, which this machine lacks.
        }

        File.Delete(target.Path);

        var error = Assert.Throws<Win32Exception>(() => WindowsPtyBackend.BuildLaunch(new PtyStartInfo { FileName = link, Arguments = ["a"] }));

        await Assert.That(error.NativeErrorCode).IsEqualTo(2);
    }

    [Test]
    public async Task ProgramIsLeftToCreateProcess()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var (applicationName, commandLine) = WindowsPtyBackend.BuildLaunch(
            new PtyStartInfo { FileName = "app.exe", Arguments = ["a b"] });

        await Assert.That(applicationName).IsNull();
        await Assert.That(new string(commandLine, 0, commandLine.Length - 1)).IsEqualTo("app.exe \"a b\"");
    }

    /// <summary>A file in a fresh temporary directory, removed with the directory on dispose.</summary>
    private sealed class TemporaryFile : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"minipty-launch-{Guid.NewGuid():N}");

        public TemporaryFile(string name, ReadOnlySpan<byte> content)
        {
            Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(_directory, name);
            File.WriteAllBytes(Path, content);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }

    private static string BuildBatch(string scriptPath, IReadOnlyList<string> arguments)
    {
        var buffer = WindowsBatchFile.BuildCommandLine(scriptPath, arguments);
        return new string(buffer, 0, buffer.Length - 1);
    }

    /// <summary>A straightforward StringBuilder version of the batch escaping, the oracle for the measured writer.</summary>
    private static string ReferenceBatchQuote(string arg)
    {
        var quote = arg.Length == 0 || arg[^1] == '\\';
        foreach (var c in arg)
            quote |= !char.IsAsciiLetterOrDigit(c) && !"#$*+-./:?@\\_".Contains(c);

        var sb = new System.Text.StringBuilder();
        if (quote)
            sb.Append('"');
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
            }
            else
            {
                if (c == '"')
                    sb.Append('\\', backslashes).Append('"');
                else if (c == '%')
                    sb.Append("%%cd:~,");
                backslashes = 0;
            }

            sb.Append(c);
        }

        if (quote)
            sb.Append('\\', backslashes).Append('"');
        return sb.ToString();
    }

    private static string Build(PtyStartInfo startInfo)
    {
        var buffer = WindowsPtyBackend.BuildCommandLine(startInfo);
        return new string(buffer, 0, buffer.Length - 1);
    }

    /// <summary>The previous StringBuilder-based quoting, kept as the oracle for the single-buffer writer.</summary>
    private static string ReferenceQuote(string arg)
    {
        if (arg.Length == 0)
            return "\"\"";

        var needsQuoting = false;
        foreach (var c in arg)
            needsQuoting |= char.IsWhiteSpace(c) || c is '"' or '\\';
        if (!needsQuoting)
            return arg;

        var sb = new System.Text.StringBuilder(arg.Length + 2);
        sb.Append('"');
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
                backslashes = 0;
                continue;
            }

            sb.Append('\\', backslashes);
            backslashes = 0;
            sb.Append(c);
        }

        sb.Append('\\', backslashes * 2);
        sb.Append('"');
        return sb.ToString();
    }
}
