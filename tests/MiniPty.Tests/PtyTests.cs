using System.Diagnostics;
using System.Runtime.InteropServices;
using MiniPty;

var failures = 0;

failures += Run("PtyEchoOutput", PtyEchoOutput);
failures += Run("PtyTtyCheck", PtyTtyCheck);
failures += Run("PtyStdinEof", PtyStdinEof);
failures += Run("PtyHasExitedPolls", PtyHasExitedPolls);
failures += Run("PtyCancellationKill", PtyCancellationKill);
failures += Run("PtyCancellationWait", PtyCancellationWait);
if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && TryResolvePwsh(out var pwshPath))
    failures += Run("PtyMatrixPwsh", () => PtyMatrixPwsh(pwshPath));

return failures == 0 ? 0 : 1;

static PtyOptions Spawn(string fileName, IReadOnlyList<string> arguments) =>
    new() { FileName = fileName, Arguments = arguments, Columns = 40, Rows = 8 };

static int Run(string name, Func<bool> test)
{
    try
    {
        if (test())
        {
            Console.Error.WriteLine($"ok {name}");
            return 0;
        }

        Console.Error.WriteLine($"FAIL {name}");
        return 1;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"FAIL {name}: {ex.Message}");
        return 1;
    }
}

static bool PtyEchoOutput()
{
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
        var result = Pty.Run(Spawn(cmd, ["/c", "echo pty-layer-echo"])).GetAwaiter().GetResult();
        return result.ExitCode == 0 && result.Output.Contains("pty-layer-echo", StringComparison.Ordinal);
    }

    var shell = Environment.GetEnvironmentVariable("SHELL") ?? "/bin/bash";
    var unix = Pty.Run(Spawn(shell, ["-lc", "printf pty-layer-echo"])).GetAwaiter().GetResult();
    return unix.ExitCode == 0 && unix.Output.Contains("pty-layer-echo", StringComparison.Ordinal);
}

static bool PtyStdinEof()
{
    const string marker = "pty-stdin-eof";

    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        var sort = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sort.exe");
        var spawn = Spawn(sort, []);
        var result = Pty.Run(spawn with { Input = $"zzz\r\n{marker}\r\naaa\r\n" }).GetAwaiter().GetResult();
        return result.Output.Contains(marker, StringComparison.Ordinal)
            && result.Output.Contains("aaa", StringComparison.Ordinal);
    }

    var shell = Environment.GetEnvironmentVariable("SHELL") ?? "/bin/bash";
    var unix = Pty.Run(Spawn(shell, ["-lc", "cat"]) with { Input = marker }).GetAwaiter().GetResult();
    return unix.ExitCode == 0 && unix.Output.Contains(marker, StringComparison.Ordinal);
}

static bool PtyHasExitedPolls()
{
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
        using var session = Pty.Start(Spawn(cmd, ["/c", "exit 0"]));
        for (var i = 0; i < 50 && !session.HasExited; i++)
            Thread.Sleep(20);
        return session.HasExited;
    }

    var shell = Environment.GetEnvironmentVariable("SHELL") ?? "/bin/bash";
    using var unixSession = Pty.Start(Spawn(shell, ["-lc", "exit 0"]));
    for (var i = 0; i < 50 && !unixSession.HasExited; i++)
        Thread.Sleep(20);
    return unixSession.HasExited;
}

static bool PtyCancellationKill()
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
        using var session = Pty.Start(Spawn(cmd, ["/c", "ping -n 30 127.0.0.1 >nul"]));
        try
        {
            session.WaitForExitOrKillAsync(cts.Token).GetAwaiter().GetResult();
            return false;
        }
        catch (OperationCanceledException)
        {
            Thread.Sleep(200);
            return session.HasExited;
        }
    }

    var shell = Environment.GetEnvironmentVariable("SHELL") ?? "/bin/bash";
    using var unixSession = Pty.Start(Spawn(shell, ["-lc", "sleep 30"]));
    try
    {
        unixSession.WaitForExitOrKillAsync(cts.Token).GetAwaiter().GetResult();
        return false;
    }
    catch (OperationCanceledException)
    {
        Thread.Sleep(200);
        return unixSession.HasExited;
    }
}

static bool PtyCancellationWait()
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
        using var session = Pty.Start(Spawn(cmd, ["/c", "ping -n 30 127.0.0.1 >nul"]));
        try
        {
            session.WaitForExitAsync(cts.Token).GetAwaiter().GetResult();
            return false;
        }
        catch (OperationCanceledException)
        {
            return !session.HasExited;
        }
    }

    var shell = Environment.GetEnvironmentVariable("SHELL") ?? "/bin/bash";
    using var unixSession = Pty.Start(Spawn(shell, ["-lc", "sleep 30"]));
    try
    {
        unixSession.WaitForExitAsync(cts.Token).GetAwaiter().GetResult();
        return false;
    }
    catch (OperationCanceledException)
    {
        return !unixSession.HasExited;
    }
}

static bool PtyTtyCheck()
{
    if (!TryResolvePwsh(out var pwsh))
    {
        Console.Error.WriteLine("skip PtyTtyCheck: pwsh not found");
        return true;
    }

    var result = Pty.Run(Spawn(pwsh, ["-NoLogo", "-NoProfile", "-Command", "Write-Output (\"redirected=$([Console]::IsOutputRedirected)\")"])).GetAwaiter().GetResult();
    return result.ExitCode == 0 && result.Output.Contains("redirected=False", StringComparison.OrdinalIgnoreCase);
}

static bool PtyMatrixPwsh(string pwshPath)
{
    var result = Pty.Run(Spawn(pwshPath, ["-NoLogo", "-NoProfile", "-Command", "matrix -c 120 -s 2"])).GetAwaiter().GetResult();
    return result.ExitCode == 0 && result.Chunks.Count > 1;
}

static bool TryResolvePwsh(out string path)
{
    path = "";
    var env = Environment.GetEnvironmentVariable("PWSH");
    if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
    {
        path = env;
        return true;
    }

    var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    var candidate = Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");
    if (File.Exists(candidate))
    {
        path = candidate;
        return true;
    }

    return false;
}
