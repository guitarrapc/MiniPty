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

    Console.Error.WriteLine($"ok NuGet smoke: exit={result.ExitCode} bytes={result.Output.Length}");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL NuGet smoke: {ex.GetType().Name}: {ex.Message}");
    return 1;
}
