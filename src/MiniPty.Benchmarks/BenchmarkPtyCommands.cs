using System.Runtime.InteropServices;

namespace MiniPty.Benchmarks;

/// <summary>
/// Cross-platform spawn helpers for integration benchmarks.
/// </summary>
internal static class BenchmarkPtyCommands
{
    internal static bool IsSupported => Pty.IsSupported;

    internal static PtyStartInfo Echo() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? WindowsCommand("echo minipty-bench-echo")
            : UnixShell("printf minipty-bench-echo");

    /// <summary>Child exits immediately with code 0 and no PTY output (spawn baseline).</summary>
    internal static PtyStartInfo Exit0() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? WindowsCommand("exit /b 0")
            : UnixShell("exit 0");

    /// <summary>
    /// Child writes exactly <paramref name="byteCount"/> bytes to stdout (no shell pipeline).
    /// </summary>
    /// <remarks>
    /// Avoids <c>yes x | head</c> on Unix: line-buffered PTY reads create one capture chunk per
    /// <c>"x\n"</c> pair (~16k chunks for 32 KiB) and inflate benchmark allocations.
    /// </remarks>
    internal static PtyStartInfo SmallStdout(int byteCount) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? WindowsPowerShell($"[Console]::Out.Write(([string]::new('x',{byteCount})))")
            : Spawn("head", ["-c", byteCount.ToString(), "/dev/zero"]);

    private static PtyStartInfo Spawn(string fileName, IReadOnlyList<string> arguments) =>
        new() { FileName = fileName, Arguments = arguments, Size = new(80, 24) };

    private static PtyStartInfo UnixShell(string command) => Spawn("sh", ["-c", command]);

    private static PtyStartInfo WindowsCommand(string command)
    {
        var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
        return Spawn(cmd, ["/c", command]);
    }

    private static PtyStartInfo WindowsPowerShell(string script)
    {
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");

        return Spawn(powershell, ["-NoLogo", "-NoProfile", "-Command", script]);
    }
}
