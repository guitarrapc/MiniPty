using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace MiniPty.Tests;

/// <summary>
/// Batch files on Windows (<c>WindowsBatchFile</c>): arguments reach the script, and a program the script forwards
/// them to, as data. <c>cmd.exe</c> parses them, so an argument quoted by the MSVCRT rules used to be able to close
/// the quoting and run a command of its own.
/// </summary>
/// <remarks>
/// The program behind the scripts is this test executable, which prints its arguments when
/// <see cref="ArgvChildVariable"/> is set (<see cref="RunAsArgvChildWhenAsked"/>). A script that forwards <c>%*</c>
/// to it is what tool shims such as <c>npm.cmd</c> do.
/// </remarks>
public sealed partial class WindowsBatchFileTests
{
    private const string ArgvChildVariable = "MINIPTY_TEST_ARGV_CHILD";
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
            await Assert.That(Argv(text)).IsEquivalentTo(Tricky, TUnit.Assertions.Enums.CollectionOrdering.Matching);
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

        // %1 and %* keep the quotes MiniPty wrote, so the script's own command line keeps them too.
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

            await Assert.That(Argv(text)).IsEquivalentTo(arguments, TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(HasLine(text, "INJECTED")).IsFalse();
        }
        finally
        {
            File.Delete(shim);
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

        // CreateProcess searches the host executable's directory first, and the batch path keeps that lookup. The
        // test host is this test executable, in its output directory.
        var hostDirectory = Path.GetDirectoryName(Environment.ProcessPath)!;
        await Assert.That(Path.TrimEndingDirectorySeparator(hostDirectory))
            .IsEqualTo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));

        var shim = WriteScript($"@\"{PtyBaselineTests.TestExecutable}\" %*");
        try
        {
            string[] arguments = ["a b", "50%"];
            var (text, _) = await RunAsync(new PtyStartInfo { FileName = Path.GetFileName(shim), Arguments = arguments, Environment = ArgvChild });

            await Assert.That(Argv(text)).IsEquivalentTo(arguments, TUnit.Assertions.Enums.CollectionOrdering.Matching);
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
    /// Makes this test executable print its arguments, one <c>ARGV:</c> line of UTF-8 hex each, when
    /// <see cref="ArgvChildVariable"/> is set. Runs before the test framework starts; in a test run the variable is
    /// unset and this returns at once.
    /// </summary>
    [ModuleInitializer]
    internal static void RunAsArgvChildWhenAsked()
    {
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
