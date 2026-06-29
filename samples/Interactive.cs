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

const int SessionTimeoutSeconds = 30;

if (!Pty.IsSupported)
{
    Console.Error.WriteLine("PTY is not supported on this operating system.");
    return 1;
}

try
{
    await RunPersistentCommandLoopAsync();
    Console.Error.WriteLine("ok Interactive sample: ReadOutputAsync command loop, Resize, natural exit");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
    return 1;
}

static async Task RunPersistentCommandLoopAsync()
{
    Console.Error.WriteLine("=== Persistent ReadOutputAsync command loop ===");

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(SessionTimeoutSeconds));
    await using var session = Pty.Start(CreateInteractiveStartInfo());

    var output = new StringBuilder();
    var outputLock = new object();
    var pumpTask = PumpReadOutputAsync(session, output, outputLock, cts.Token);

    var newline = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "\r\n" : "\n";

    await WaitForMarkerAsync(output, outputLock, pumpTask, "ready", cts.Token);
    Console.Error.WriteLine("marker ready");

    await session.WriteInputAsync("alpha" + newline, cancellationToken: cts.Token);

    await WaitForMarkerAsync(output, outputLock, pumpTask, "first:alpha", cts.Token);
    Console.Error.WriteLine("marker first:alpha");

    session.Resize(new PtySize(100, 28));
    Console.Error.WriteLine($"parent reports {session.Size.Columns}x{session.Size.Rows}");

    await session.WriteInputAsync("beta" + newline, cancellationToken: cts.Token);

    var finalMarker = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? "second:beta"
        : "second:beta size:28 100";
    await WaitForMarkerAsync(output, outputLock, pumpTask, finalMarker, cts.Token);
    Console.Error.WriteLine($"marker {finalMarker}");

    await pumpTask.ConfigureAwait(false);
    var exitCode = await session.WaitForExitAsync(cts.Token).ConfigureAwait(false);

    var text = GetOutputText(output, outputLock);
    Console.Error.WriteLine($"pid={session.ProcessId} exit={exitCode}");
    Console.Error.WriteLine("child output (plain):");
    Console.Error.WriteLine(PtyOutput.ToDisplayText(text, PtyOutputDisplayMode.PlainText).TrimEnd());

    if (exitCode != 0)
        throw new InvalidOperationException($"interactive child exited with {exitCode}");

    if (!text.Contains("first:alpha", StringComparison.Ordinal))
        throw new InvalidOperationException("expected marker missing: first:alpha");

    if (!text.Contains(finalMarker, StringComparison.Ordinal))
        throw new InvalidOperationException($"expected marker missing: {finalMarker}");
}

static async Task PumpReadOutputAsync(
    PtySession session,
    StringBuilder sink,
    object outputLock,
    CancellationToken cancellationToken)
{
    await foreach (var chunk in session.ReadOutputAsync(cancellationToken).ConfigureAwait(false))
    {
        var text = Encoding.UTF8.GetString(chunk.Data.Span);
        lock (outputLock)
            sink.Append(text);
    }
}

static async Task WaitForMarkerAsync(
    StringBuilder output,
    object outputLock,
    Task pumpTask,
    string marker,
    CancellationToken cancellationToken)
{
    try
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (ContainsMarker(output, outputLock, marker))
                return;

            if (pumpTask.IsCompleted)
            {
                if (pumpTask.IsFaulted)
                    await pumpTask.ConfigureAwait(false);

                if (pumpTask.IsCanceled)
                    throw new OperationCanceledException(cancellationToken);

                var text = GetOutputText(output, outputLock);
                throw new InvalidOperationException(
                    $"PTY output ended before marker '{marker}' appeared. " +
                    $"Output so far ({text.Length} chars): {EscapeForDisplay(text)}");
            }

            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        var text = GetOutputText(output, outputLock);
        throw new InvalidOperationException(
            $"Timed out after {SessionTimeoutSeconds}s waiting for PTY marker '{marker}'. " +
            $"Output so far ({text.Length} chars): {EscapeForDisplay(text)}");
    }
}

static bool ContainsMarker(StringBuilder output, object outputLock, string marker)
{
    lock (outputLock)
        return output.ToString().Contains(marker, StringComparison.Ordinal);
}

static string GetOutputText(StringBuilder output, object outputLock)
{
    lock (outputLock)
        return output.ToString();
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

static PtyStartInfo CreateInteractiveStartInfo()
{
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        if (!TryResolveWindowsPowerShell(out var powershell))
            throw new InvalidOperationException("Windows PowerShell is required for the Interactive sample.");

        return new PtyStartInfo
        {
            FileName = powershell,
            Arguments =
            [
                "-NoLogo",
                "-NoProfile",
                "-Command",
                "[Console]::Out.WriteLine('ready'); $a=[Console]::In.ReadLine(); [Console]::Out.WriteLine('first:' + $a); $b=[Console]::In.ReadLine(); [Console]::Out.WriteLine('second:' + $b)",
            ],
            Size = new PtySize(80, 24),
        };
    }

    return new PtyStartInfo
    {
        FileName = "/bin/sh",
        Arguments =
        [
            "-c",
            "printf 'ready\\n'; IFS= read -r a; printf 'first:%s\\n' \"$a\"; IFS= read -r b; set -- $(stty size); printf 'second:%s size:%s %s\\n' \"$b\" \"$1\" \"$2\"",
        ],
        Size = new PtySize(80, 24),
    };
}

static bool TryResolveWindowsPowerShell(out string path)
{
    path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell",
        "v1.0",
        "powershell.exe");
    return File.Exists(path);
}
