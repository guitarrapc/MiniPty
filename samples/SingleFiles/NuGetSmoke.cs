// NuGetSmoke: checks the packed NuGet packages, not the sources. It has no #: directives on purpose:
// scripts/run-nuget-smoke.sh compiles it in a temporary project that references the packages in a local feed, then
// runs it under `dotnet run` and, given a RID, after a publish. On Windows it also checks that the package's build
// targets staged OpenConsole.exe beside conpty.dll.

using System.Runtime.InteropServices;
using MiniPty;
using MiniPty.Capture;

const string marker = "minipty-nuget-smoke";

if (!Pty.IsSupported)
{
    Console.Error.WriteLine("FAIL NuGet smoke: PTY is not supported on this operating system.");
    return 1;
}

try
{
    var startInfo = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? new PtyStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe",
            Arguments = ["/c", $"echo {marker}"],
            Size = new PtySize(80, 24),
        }
        : new PtyStartInfo
        {
            FileName = "/bin/sh",
            Arguments = ["-c", $"printf {marker}"],
            Size = new PtySize(80, 24),
        };

    // Windows: the smoke project references the out-of-band ConPTY package, and the MiniPty package's build targets
    // must have staged OpenConsole.exe beside conpty.dll, in the portable layout under `dotnet run` and the
    // runtime-specific one after publish. conpty.dll without its host silently runs the in-box conhost, so the host
    // is required here, not just the DLL.
    var conPty = ConPtyLibrary.TryLoadFromApplicationDirectory();
    if (OperatingSystem.IsWindows())
    {
        if (conPty is null)
        {
            Console.Error.WriteLine($"FAIL NuGet smoke: conpty.dll was not staged under {AppContext.BaseDirectory}.");
            return 1;
        }

        if (!conPty.HasConsoleHost)
        {
            Console.Error.WriteLine($"FAIL NuGet smoke: OpenConsole.exe was not staged beside {conPty.Path}.");
            return 1;
        }

        startInfo = startInfo with { ConPty = conPty };
    }

    var result = await PtyCapture.RunAsync(startInfo);
    if (result.ExitCode != 0)
    {
        Console.Error.WriteLine($"FAIL NuGet smoke: unexpected exit code {result.ExitCode}.");
        return 1;
    }

    if (!result.Contains(marker))
    {
        Console.Error.WriteLine("FAIL NuGet smoke: expected marker missing from output.");
        return 1;
    }

    var host = conPty is null ? string.Empty : $" conpty={conPty.FileVersion} host={conPty.ConsoleHostPath}";
    Console.Error.WriteLine($"ok NuGet smoke: exit={result.ExitCode} bytes={result.Output.Length}{host}");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL NuGet smoke: {ex.GetType().Name}: {ex.Message}");
    return 1;
}
