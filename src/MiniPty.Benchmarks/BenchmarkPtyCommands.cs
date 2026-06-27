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
    /// Child writes exactly <paramref name="byteCount"/> zero bytes to stdout as a binary stream.
    /// </summary>
    /// <remarks>
    /// Uses the same benchmark child executable on every OS so integration benchmarks compare
    /// library cost rather than shell or runtime startup differences.
    /// </remarks>
    internal static PtyStartInfo SmallStdout(int byteCount) =>
        Spawn(ResolveBenchmarkChildPath(), ["--bytes", byteCount.ToString()]);

    private static string ResolveBenchmarkChildPath()
    {
        var baseDir = AppContext.BaseDirectory;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var exe = Path.Combine(baseDir, "MiniPty.Benchmarks.Child.exe");
            if (File.Exists(exe))
                return exe;
        }
        else
        {
            var host = Path.Combine(baseDir, "MiniPty.Benchmarks.Child");
            if (File.Exists(host))
                return host;
        }

        var expected = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Path.Combine(baseDir, "MiniPty.Benchmarks.Child.exe")
            : Path.Combine(baseDir, "MiniPty.Benchmarks.Child");

        throw new FileNotFoundException(
            "Benchmark child executable was not copied to the output directory. Rebuild MiniPty.Benchmarks.",
            expected);
    }

    private static PtyStartInfo Spawn(string fileName, IReadOnlyList<string> arguments) =>
        new() { FileName = fileName, Arguments = arguments, Size = new(80, 24) };

    private static PtyStartInfo UnixShell(string command) => Spawn("sh", ["-c", command]);

    private static PtyStartInfo WindowsCommand(string command)
    {
        var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
        return Spawn(cmd, ["/c", command]);
    }
}
