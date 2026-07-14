#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property PublishAot=true
#:project ../src/MiniPty/MiniPty.csproj
#:project ../src/MiniPty.Terminal/MiniPty.Terminal.csproj

// Reference helper for a VS Code Pseudoterminal extension.
// stdout is protocol-only. Diagnostics and usage errors go to stderr.
//
// Run a shell:
//   dotnet samples/VsCodeTerminalHelper.cs
// Run a specific command:
//   dotnet samples/VsCodeTerminalHelper.cs -- pwsh -NoLogo

using System.Runtime.InteropServices;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using MiniPty;
using MiniPty.Terminal;

if (args.Contains("--smoke"))
    return await RunSmokeAsync();

var command = CreateStartInfo(args);
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

try
{
    await using var input = Console.OpenStandardInput();
    await using var output = Console.OpenStandardOutput();
    var status = await PtyStdioBridge.RunAsync(
        command,
        input,
        output,
        cancellationToken: cancellation.Token);
    return status.NodePtyExitCode;
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"{exception.GetType().Name}: {exception.Message}");
    return 1;
}

static PtyStartInfo CreateStartInfo(string[] arguments)
{
    if (arguments.Length > 0)
    {
        return new PtyStartInfo
        {
            FileName = arguments[0],
            Arguments = arguments[1..],
            TerminalName = "xterm-256color",
        };
    }

    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        return new PtyStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe",
        };
    }

    return new PtyStartInfo
    {
        FileName = Environment.GetEnvironmentVariable("SHELL") ?? "/bin/sh",
        TerminalName = "xterm-256color",
    };
}

static async Task<int> RunSmokeAsync()
{
    var marker = "VSCODE_HELPER_MARKER";
    var startInfo = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? new PtyStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe",
            Arguments = ["/c", $"echo {marker}"],
        }
        : new PtyStartInfo
        {
            FileName = "sh",
            Arguments = ["-c", $"printf '{marker}\\n'"],
        };

    await using var input = new CancelableInputStream();
    await using var output = new MemoryStream();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var status = await PtyStdioBridge.RunAsync(startInfo, input, output, cancellationToken: timeout.Token);
    var bytes = output.ToArray().AsSpan();
    using var outputPayload = new MemoryStream();
    var sawExit = false;
    while (!bytes.IsEmpty)
    {
        if (bytes.Length < 5)
            throw new InvalidDataException("Truncated smoke frame header.");
        var type = (PtyStdioFrameType)bytes[0];
        var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..5]));
        if (bytes.Length < 5 + length)
            throw new InvalidDataException("Truncated smoke frame payload.");
        var payload = bytes.Slice(5, length);
        if (type == PtyStdioFrameType.Output)
            outputPayload.Write(payload);
        if (type == PtyStdioFrameType.Control)
        {
            using var document = JsonDocument.Parse(payload.ToArray());
            sawExit |= document.RootElement.TryGetProperty("type", out var messageType)
                && messageType.GetString() == "exit";
        }
        bytes = bytes[(5 + length)..];
    }

    var sawMarker = Encoding.UTF8.GetString(outputPayload.ToArray()).Contains(marker, StringComparison.Ordinal);
    if (status.ExitCode != 0 || !sawMarker || !sawExit)
        throw new InvalidDataException("VS Code helper smoke did not observe ordered output and exit frames.");

    // Root the persistent manager surface in NativeAOT sample publishing as well as the stdio
    // helper path. The null socket validates before authentication and does not attach.
    await using var persistentManager = new PtyWebSocketSessionManager();
    var credentials = persistentManager.CreateSession(startInfo);
    try
    {
        await persistentManager.ConnectAsync(
            credentials.SessionId,
            credentials.AuthenticationToken,
            0,
            null!,
            timeout.Token);
        throw new InvalidOperationException("Persistent manager accepted a null WebSocket.");
    }
    catch (ArgumentNullException)
    {
    }
    await persistentManager.TerminateAsync(credentials.SessionId, credentials.AuthenticationToken);

    Console.Error.WriteLine("VsCodeTerminalHelper smoke passed.");
    return 0;
}

sealed class CancelableInputStream : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return 0;
    }
}
