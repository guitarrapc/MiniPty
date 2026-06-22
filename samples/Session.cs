#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property AllowUnsafeBlocks=true
#:property PublishAot=true
#:project ../src/MiniPty/MiniPty.csproj

using System.Runtime.InteropServices;
using System.Text;
using MiniPty;

if (!Pty.IsSupported)
{
    Console.Error.WriteLine("PTY is not supported on this operating system.");
    return 1;
}

try
{
    await RunStdinPipelineWithManualPumpAsync();
    await RunEchoWithCompleteAsync();
    await RunResizeAsync();
    Console.Error.WriteLine("ok Session sample: manual pump, CompleteAsync, Resize");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
    return 1;
}

static async Task RunStdinPipelineWithManualPumpAsync()
{
    Console.Error.WriteLine("=== Manual stream control (stdin pipeline) ===");

    await using var session = Pty.Start(CreateStdinPipelineStartInfo());
    var output = new StringBuilder();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var pump = PumpOutputAsync(session.Output, output, cts.Token);

    await session.WriteInputAsync(CreateStdinPipelineInput());
    session.SendEof();

    var exitCode = await session.WaitForExitAsync(cts.Token);
    await pump;

    Console.Error.WriteLine($"pid={session.ProcessId} size={session.Size.Columns}x{session.Size.Rows} exit={exitCode}");
    Console.Error.WriteLine("child output (raw, escaped):");
    Console.Error.WriteLine(EscapeForDisplay(output.ToString()));
    Console.Error.WriteLine("child output (plain):");
    Console.Error.WriteLine(PtyOutput.ToDisplayText(output.ToString(), PtyOutputDisplayMode.PlainText));

    ValidateStdinPipelineOutput(output.ToString(), exitCode);
}

static async Task RunEchoWithCompleteAsync()
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("=== One-shot completion (CompleteAsync) ===");

    await using var session = Pty.Start(CreateEchoStartInfo());
    var result = await session.CompleteAsync(new PtyCompleteOptions
    {
        ExitTimeout = TimeSpan.FromSeconds(15),
    });

    Console.Error.WriteLine($"exit={result.ExitCode}");
    Console.Error.WriteLine("child output (raw, escaped):");
    Console.Error.WriteLine(EscapeForDisplay(PtyMemory.ToString(result.Output).TrimEnd()));
    Console.Error.WriteLine("child output (plain):");
    Console.Error.WriteLine(PtyOutput.ToDisplayText(result.Output, PtyOutputDisplayMode.PlainText).TrimEnd());

    if (result.ExitCode != 0)
        throw new InvalidOperationException($"echo exited with {result.ExitCode}");

    if (!PtyMemory.Contains(result.Output, "minipty-session-sample"))
        throw new InvalidOperationException("expected marker missing from output");
}

static async Task RunResizeAsync()
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("=== Terminal resize ===");

    await using var session = Pty.Start(CreateResizeStartInfo());
    var output = new StringBuilder();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var pump = PumpOutputAsync(session.Output, output, cts.Token);

    session.Resize(new PtySize(100, 28));
    Console.Error.WriteLine($"parent reports {session.Size.Columns}x{session.Size.Rows}");

    await session.WriteInputAsync(CreateResizeWakeInput());
    session.SendEof();

    var exitCode = await session.WaitForExitAsync(cts.Token);
    await pump;

    Console.Error.WriteLine($"exit={exitCode}");
    Console.Error.WriteLine("child output (raw, escaped):");
    Console.Error.WriteLine(EscapeForDisplay(output.ToString().Trim()));
    Console.Error.WriteLine("child output (plain):");
    Console.Error.WriteLine(PtyOutput.ToDisplayText(output.ToString(), PtyOutputDisplayMode.PlainText).Trim());

    if (exitCode != 0)
        throw new InvalidOperationException($"resize probe exited with {exitCode}");

    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        return;

    if (!output.ToString().Contains("100", StringComparison.Ordinal)
        || !output.ToString().Contains("28", StringComparison.Ordinal))
        throw new InvalidOperationException("child did not report resized dimensions");
}

static async Task PumpOutputAsync(Stream output, StringBuilder sink, CancellationToken cancellationToken)
{
    var buffer = new byte[4096];
    while (true)
    {
        var read = await output.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read == 0)
            break;

        sink.Append(Encoding.UTF8.GetString(buffer, 0, read));
    }
}

static string EscapeForDisplay(string text)
{
    var builder = new StringBuilder(text.Length);
    foreach (var ch in text)
    {
        builder.Append(ch switch
        {
            '\r' => "\\r",
            '\n' => "\\n",
            '\t' => "\\t",
            < ' ' or > '~' => $"\\u{(int)ch:X4}",
            _ => ch.ToString(),
        });
    }

    return builder.ToString();
}

static void ValidateStdinPipelineOutput(string output, int exitCode)
{
    if (exitCode != 0)
        throw new InvalidOperationException($"stdin pipeline exited with {exitCode}");

    if (!output.Contains("minipty-stdin-pipeline", StringComparison.Ordinal))
        throw new InvalidOperationException("expected pipeline marker missing from output");
}

static PtyStartInfo CreateStdinPipelineStartInfo()
{
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
        return new PtyStartInfo
        {
            FileName = cmd,
            Arguments = ["/c", "find /v \"\" >nul & echo minipty-stdin-pipeline"],
            Size = new PtySize(80, 24),
        };
    }

    return new PtyStartInfo
    {
        FileName = "/bin/sh",
        Arguments = ["-c", "cat >/dev/null; printf 'minipty-stdin-pipeline\\n'"],
        Size = new PtySize(80, 24),
    };
}

static string CreateStdinPipelineInput() =>
    RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? "line 3\r\nline 1\r\nline 2\r\n"
        : "line 3\nline 1\nline 2\n";

static PtyStartInfo CreateEchoStartInfo()
{
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
        return new PtyStartInfo
        {
            FileName = cmd,
            Arguments = ["/c", "echo minipty-session-sample"],
            WorkingDirectory = Environment.CurrentDirectory,
            Size = new PtySize(100, 30),
        };
    }

    return new PtyStartInfo
    {
        FileName = "/bin/sh",
        Arguments = ["-c", "printf 'minipty-session-sample\\n'"],
        WorkingDirectory = Environment.CurrentDirectory,
        Size = new PtySize(100, 30),
    };
}

static PtyStartInfo CreateResizeStartInfo()
{
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
        return new PtyStartInfo
        {
            FileName = cmd,
            Arguments = ["/c", "echo resized"],
            Size = new PtySize(80, 24),
        };
    }

    return new PtyStartInfo
    {
        FileName = "/bin/sh",
        Arguments = ["-c", "read _; set -- $(stty size); echo $2 $1"],
        Size = new PtySize(80, 24),
    };
}

static string CreateResizeWakeInput() =>
    RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "\r\n" : "\n";
