using System.Runtime.InteropServices;

namespace MiniPty.Tests;

/// <summary>
/// A NUL in <see cref="PtyStartInfo.FileName"/>, an argument, <see cref="PtyStartInfo.WorkingDirectory"/>, or
/// <see cref="PtyStartInfo.CommandLine"/> fails <see cref="Pty.Start"/> on every platform. The operating system reads
/// each as a NUL-terminated string, so without the check the string started something other than what it named: on
/// Windows <c>"...\cmd.exe\0C:\allowed\safe.exe"</c> started <c>cmd.exe</c>, and on Linux
/// <c>"/bin/sh\0/usr/bin/safe"</c> started <c>/bin/sh</c>, while <see cref="Path.GetFileName(string)"/> read the
/// allowed name.
/// </summary>
/// <remarks>
/// Each case uses a program that starts on its platform, so the spawn would succeed without the check.
/// </remarks>
public sealed class PtyStartInfoNulTests
{
    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private static string Program => IsWindows ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : "/bin/sh";

    private static string[] ExitArguments => IsWindows ? ["/c", "exit 0"] : ["-c", "exit 0"];

    [Test]
    public async Task FileNameWithNulIsRejected()
    {
        var allowed = IsWindows ? @"C:\allowed\safe.exe" : "/usr/bin/safe";
        var fileName = Program + "\0" + allowed;

        await AssertRejectedAsync(new PtyStartInfo { FileName = fileName, Arguments = ExitArguments }, nameof(PtyStartInfo.FileName));
    }

    [Test]
    public async Task ArgumentWithNulIsRejected()
    {
        string[] arguments = IsWindows ? ["/c", "exit 0\0& echo dropped"] : ["-c", "exit 0\0; echo dropped"];

        await AssertRejectedAsync(new PtyStartInfo { FileName = Program, Arguments = arguments }, nameof(PtyStartInfo.Arguments));
    }

    [Test]
    public async Task WorkingDirectoryWithNulIsRejected()
    {
        var workingDirectory = Path.GetTempPath() + "\0" + "elsewhere";

        await AssertRejectedAsync(new PtyStartInfo { FileName = Program, Arguments = ExitArguments, WorkingDirectory = workingDirectory }, nameof(PtyStartInfo.WorkingDirectory));
    }

    [Test]
    public async Task CommandLineWithNulIsRejected()
    {
        if (!IsWindows)
            return;

        await AssertRejectedAsync(new PtyStartInfo { FileName = Program, CommandLine = "/c exit 0\0& echo dropped" }, nameof(PtyStartInfo.CommandLine));
    }

    private static async Task AssertRejectedAsync(PtyStartInfo startInfo, string member)
    {
        var error = Assert.Throws<ArgumentException>(() => Pty.Start(startInfo).Dispose());

        await Assert.That(error.ParamName).IsEqualTo("startInfo");
        await Assert.That(error.Message).Contains("PtyStartInfo." + member);
    }
}
