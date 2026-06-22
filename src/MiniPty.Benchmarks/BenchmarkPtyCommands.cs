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

    internal static PtyStartInfo SmallStdout(int byteCount) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? WindowsPowerShell($"[Console]::Out.Write(('x' * {byteCount}))")
            : UnixShell($"yes x | head -c {byteCount}");

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
