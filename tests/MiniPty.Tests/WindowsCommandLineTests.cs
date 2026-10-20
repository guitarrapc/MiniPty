using MiniPty.Internal;

namespace MiniPty.Tests;

/// <summary>
/// <c>CreateProcessW</c> command-line construction. A pure function, so it runs on every platform.
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
    [Arguments("plain", "plain")]                              // letters only: unquoted
    [Arguments("C:\\dir\\file", "C:\\dir\\file")]              // backslash and colon need no quotes
    [Arguments("--name=1", "\"--name=1\"")]                    // '=' is a cmd delimiter: quoted
    [Arguments("", "\"\"")]                                    // empty
    [Arguments("a b", "\"a b\"")]                              // space
    [Arguments("a\tb", "\"a\tb\"")]                            // control character
    [Arguments("x\" & calc & \"", "\"x\"\" & calc & \"\"\"")]  // a quote doubles, so cmd's quoting never closes
    [Arguments("a\\\"b", "\"a\\\\\"\"b\"")]                    // a backslash before a quote doubles for MSVCRT
    [Arguments("C:\\dir\\", "\"C:\\dir\\\\\"")]                // a trailing backslash forces quotes and doubles
    [Arguments("50%", "\"50%%cd:~,%\"")]                       // percent cannot start an expansion
    [Arguments("%PATH%", "\"%%cd:~,%PATH%%cd:~,%\"")]
    [Arguments("a&b|c<d>e^f(g)", "\"a&b|c<d>e^f(g)\"")]        // cmd metacharacters stay inside quotes
    [Arguments("!PATH!", "\"!PATH!\"")]                        // inert under /v:OFF, quoted like any symbol
    [Arguments("日本語", "日本語")]                              // non-ASCII text: unquoted
    public async Task BatchArgumentIsEscapedForCmd(string argument, string expected)
    {
        var commandLine = BuildBatch("C:\\s\\x.cmd", [argument]);

        await Assert.That(commandLine).IsEqualTo("cmd.exe /e:ON /v:OFF /d /c \"\"C:\\s\\x.cmd\" " + expected + "\"");
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
    public async Task BatchScriptPathThatCannotBeQuotedIsRejected(string scriptPath)
    {
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
        // Written as is, the argument would fit (8185 characters); each escaped % takes 8, quotes included 8192.
        var argument = new string('a', 8140) + "%";

        Assert.Throws<ArgumentException>(() => WindowsBatchFile.BuildCommandLine("C:\\s\\x.cmd", [argument]));
        await Assert.That(BatchOverheadWithOneArgument + 2 + argument.Length).IsLessThanOrEqualTo(WindowsBatchFile.CommandLineLimit);
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

        var (applicationName, commandLine) = WindowsPtyBackend.BuildLaunch(
            new PtyStartInfo { FileName = "C:\\s\\x.cmd", Arguments = ["a b"] });

        await Assert.That(applicationName).IsEqualTo(Path.Combine(Environment.SystemDirectory, "cmd.exe"));
        await Assert.That(new string(commandLine, 0, commandLine.Length - 1))
            .IsEqualTo("cmd.exe /e:ON /v:OFF /d /c \"\"C:\\s\\x.cmd\" \"a b\"\"");
    }

    [Test]
    public async Task BatchFileWithARawCommandLineIsLeftToCreateProcess()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // CommandLine is documented as passed without escaping; the caller owns cmd's rules for it.
        var (applicationName, commandLine) = WindowsPtyBackend.BuildLaunch(
            new PtyStartInfo { FileName = "C:\\s\\x.cmd", CommandLine = "\"a b\"" });

        await Assert.That(applicationName).IsNull();
        await Assert.That(new string(commandLine, 0, commandLine.Length - 1)).IsEqualTo("\"C:\\s\\x.cmd\" \"a b\"");
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
            quote |= char.IsControl(c) || (char.IsAscii(c) && !char.IsAsciiLetterOrDigit(c) && !"#$*+-./:?@\\_".Contains(c));

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
