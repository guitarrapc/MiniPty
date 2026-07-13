using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MiniPty.Terminal;

namespace MiniPty.Tests;

public sealed class PtyStdioBridgeTests
{
    [Test]
    public async Task StdioBridgeFramesOutputThenNodePtyExit()
    {
        await using var input = new CancelableInputStream([]);
        await using var output = new MemoryStream();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var status = await PtyStdioBridge.RunAsync(
            EchoMarkerChild("STDIO_MARKER"),
            input,
            output,
            cancellationToken: cts.Token);

        var frames = ParseFrames(output.ToArray());
        await Assert.That(status.ExitCode).IsEqualTo(0);
        await Assert.That(Encoding.UTF8.GetString(JoinPayloads(frames, PtyStdioFrameType.Output))).Contains("STDIO_MARKER");
        var exit = frames.Last(frame => frame.Type == PtyStdioFrameType.Control).Payload;
        using var document = JsonDocument.Parse(exit);
        await Assert.That(document.RootElement.GetProperty("exitCode").GetInt32()).IsEqualTo(0);
    }

    [Test]
    public async Task StdioBridgeForwardsInput()
    {
        var inputPayload = Encoding.UTF8.GetBytes(OperatingSystem.IsWindows() ? "INPUT_MARKER\r" : "INPUT_MARKER\n");
        await using var input = new CancelableInputStream(CreateFrame(PtyStdioFrameType.Input, inputPayload));
        await using var output = new MemoryStream();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var status = await PtyStdioBridge.RunAsync(EchoInputThenExitChild(), input, output, cancellationToken: cts.Token);

        await Assert.That(status.ExitCode).IsEqualTo(0);
        var frames = ParseFrames(output.ToArray());
        await Assert.That(Encoding.UTF8.GetString(JoinPayloads(frames, PtyStdioFrameType.Output))).Contains("INPUT_MARKER");
    }

    [Test]
    public async Task BridgeExitJsonUsesNodePtySignalShape()
    {
        var payload = MiniPty.Terminal.Internal.BridgeJson.SerializeExit(new PtyExitStatus(143, 15));
        using var document = JsonDocument.Parse(payload);

        await Assert.That(document.RootElement.GetProperty("exitCode").GetInt32()).IsEqualTo(0);
        await Assert.That(document.RootElement.GetProperty("signal").GetInt32()).IsEqualTo(15);
    }

    [Test]
    public async Task StdioBridgeRejectsMalformedControl()
    {
        await using var input = new CancelableInputStream(CreateFrame(PtyStdioFrameType.Control, "{"u8));
        await using var output = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await PtyStdioBridge.RunAsync(StdinBlockingChild(), input, output));
    }

    [Test]
    public async Task StdioBridgeRejectsOversizeControlBeforeReadingPayload()
    {
        var header = new byte[5];
        header[0] = (byte)PtyStdioFrameType.Control;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(1), 4097);
        await using var input = new CancelableInputStream(header);
        await using var output = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await PtyStdioBridge.RunAsync(StdinBlockingChild(), input, output));
    }

    private static byte[] CreateFrame(PtyStdioFrameType type, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[5 + payload.Length];
        frame[0] = (byte)type;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(1), (uint)payload.Length);
        payload.CopyTo(frame.AsSpan(5));
        return frame;
    }

    private static List<(PtyStdioFrameType Type, byte[] Payload)> ParseFrames(ReadOnlySpan<byte> bytes)
    {
        var frames = new List<(PtyStdioFrameType, byte[])>();
        while (!bytes.IsEmpty)
        {
            var type = (PtyStdioFrameType)bytes[0];
            var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..5]));
            frames.Add((type, bytes.Slice(5, length).ToArray()));
            bytes = bytes[(5 + length)..];
        }

        return frames;
    }

    private static byte[] JoinPayloads(
        List<(PtyStdioFrameType Type, byte[] Payload)> frames,
        PtyStdioFrameType type)
    {
        using var joined = new MemoryStream();
        foreach (var frame in frames)
        {
            if (frame.Type == type)
                joined.Write(frame.Payload);
        }

        return joined.ToArray();
    }

    private static PtyStartInfo Spawn(string fileName, IReadOnlyList<string> arguments) =>
        new() { FileName = fileName, Arguments = arguments };

    private static PtyStartInfo EchoMarkerChild(string marker) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Spawn(WindowsComSpec(), ["/c", $"echo {marker}"])
            : Spawn("sh", ["-c", $"printf '{marker}\\n'"]);

    private static PtyStartInfo EchoInputThenExitChild() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Spawn(WindowsComSpec(), ["/v:on", "/c", "set /p LINE= & echo GOT:!LINE!"])
            : Spawn("sh", ["-c", "IFS= read -r line; printf 'GOT:%s\\n' \"$line\""]);

    private static PtyStartInfo StdinBlockingChild() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Spawn(WindowsComSpec(), ["/c", "set /p DUMMY="])
            : Spawn("sh", ["-c", "IFS= read -r _"]);

    private static string WindowsComSpec() =>
        Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";

    private sealed class CancelableInputStream(byte[] prefix) : Stream
    {
        private int _offset;

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
            if (_offset < prefix.Length)
            {
                var count = Math.Min(buffer.Length, prefix.Length - _offset);
                prefix.AsMemory(_offset, count).CopyTo(buffer);
                _offset += count;
                return count;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
