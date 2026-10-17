#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property AllowUnsafeBlocks=true
#:property PublishAot=true
#:project ../src/MiniPty/MiniPty.csproj
#:project ../src/MiniPty.Capture/MiniPty.Capture.csproj

using System.Runtime.InteropServices;
using MiniPty;
using MiniPty.Capture;

if (!Pty.IsSupported)
{
    Console.Error.WriteLine("FAIL Capture sample: PTY is not supported on this operating system.");
    return 1;
}

try
{
    var result = await PtyCapture.RunAsync(CreateEchoStartInfo());
    if (result.ExitCode != 0)
    {
        Console.Error.WriteLine($"FAIL Capture sample: unexpected exit code {result.ExitCode}.");
        return 1;
    }

    if (!result.Contains("minipty-capture-sample"))
    {
        Console.Error.WriteLine("FAIL Capture sample: expected marker missing from output.");
        return 1;
    }

    if (result.Chunks.Count < 1)
    {
        Console.Error.WriteLine("FAIL Capture sample: expected at least one capture chunk.");
        return 1;
    }

    Console.Error.WriteLine($"ok Capture sample: exit={result.ExitCode} chunks={result.Chunks.Count} bytes={result.Output.Length}");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL Capture sample: {ex.GetType().Name}: {ex.Message}");
    return 1;
}

static PtyStartInfo CreateEchoStartInfo()
{
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
        return new PtyStartInfo
        {
            FileName = cmd,
            Arguments = ["/c", "echo minipty-capture-sample"],
            Size = new PtySize(80, 24),
        };
    }

    return new PtyStartInfo
    {
        FileName = "/bin/sh",
        Arguments = ["-c", "printf minipty-capture-sample"],
        Size = new PtySize(80, 24),
    };
}
