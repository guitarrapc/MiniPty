using System.Buffers.Binary;
using System.Diagnostics;
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

    /// <summary>
    /// Input is sent after the child's ready marker. The writable case proves that the child can receive it before
    /// its wait expires. The control frame after the input detects a payload left unread.
    /// </summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task AReadOnlyStdioBridgeDropsInputAndStaysInFrame(bool readOnly)
    {
        var key = Encoding.UTF8.GetBytes(PtyWebSocketBridgeTests.KeyWaitKey + (OperatingSystem.IsWindows() ? "\r" : "\n"));
        var resize = Encoding.UTF8.GetBytes("{\"type\":\"resize\",\"cols\":100,\"rows\":40}");
        await using var output = new ReadyOutputStream();
        await using var input = new CancelableInputStream([
            .. CreateFrame(PtyStdioFrameType.Input, key),
            .. CreateFrame(PtyStdioFrameType.Control, resize),
        ], output.Ready);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var status = await PtyStdioBridge.RunAsync(
            PtyWebSocketBridgeTests.ReadyThenWaitForKeyChild(),
            input,
            output,
            new PtyBridgeOptions { ReadOnly = readOnly },
            cts.Token);

        await Assert.That(status.ExitCode).IsEqualTo(0);
        var text = Encoding.UTF8.GetString(JoinPayloads(ParseFrames(output.ToArray()), PtyStdioFrameType.Output));
        await Assert.That(text).Contains(readOnly ? PtyWebSocketBridgeTests.KeyWaitTimedOut : PtyWebSocketBridgeTests.KeyWaitGotKey);
        await Assert.That(text).DoesNotContain(readOnly ? PtyWebSocketBridgeTests.KeyWaitGotKey : PtyWebSocketBridgeTests.KeyWaitTimedOut);
    }

    /// <summary>A read-only stdio bridge refuses a start that inherits the cursor, as the WebSocket bridge does.</summary>
    [Test]
    public async Task AReadOnlyStdioBridgeRefusesAStartThatInheritsTheCursor()
    {
        await using var input = new CancelableInputStream([]);
        await using var output = new MemoryStream();
        var startInfo = EchoMarkerChild("NEVER_STARTED") with { InheritCursor = true };

        Assert.Throws<ArgumentException>(() => { _ = PtyStdioBridge.RunAsync(startInfo, input, output, new PtyBridgeOptions { ReadOnly = true }); });
        await Assert.That(output.Length).IsEqualTo(0);
    }

    /// <summary>
    /// A flood goes out in large output frames, as through the WebSocket bridge: the bridge joins reads that arrive in a
    /// burst. 256 KiB stays below the high watermark, so the flood needs no acknowledgement to go through. The test weighs
    /// frames by their bytes, runs on Unix only and alone, and is skipped on a host too slow to flood, for the reasons the
    /// WebSocket bridge's test gives.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task StdioBridgeSendsAFloodInLargeFrames()
    {
        if (OperatingSystem.IsWindows())
            return;

        const int Size = 256 * 1024;
        await using var input = new CancelableInputStream([]);
        await using var output = new TimedOutputStream();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await PtyStdioBridge.RunAsync(
            Spawn("sh", ["-c", $"head -c {Size} /dev/zero | tr '\\0' x"]),
            input,
            output,
            cancellationToken: cts.Token);

        var flood = output.OutputFrames();
        await Assert.That(flood.Sum(frame => frame.Bytes)).IsEqualTo(Size);
        await PtyWebSocketBridgeTests.AssertFloodFramesAreLargeAsync(flood);
    }

    /// <summary>
    /// An acknowledgement that the bridge handles before its write of that output returns is not lost, as through the
    /// WebSocket bridge. Here every output frame's write returns only once the bridge has handled the peer's
    /// acknowledgement of it. Bytes counted after the write then stayed unacknowledged for good, and the first frame
    /// above the high watermark paused the bridge until the timeout.
    /// </summary>
    [Test]
    public async Task AnAcknowledgementHandledBeforeItsWriteReturnsIsNotLost()
    {
        var peer = new AcknowledgingPeer();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        // Every frame of output is above the high watermark, so one lost acknowledgement pauses the bridge.
        var options = new PtyBridgeOptions { HighWatermark = 64, LowWatermark = 0 };
        var bridgeTask = PtyStdioBridge.RunAsync(
            PtyWebSocketBridgeTests.BulkOutputChild(lineCount: 256),
            peer.Input,
            peer.Output,
            options,
            cts.Token);

        try
        {
            var status = await bridgeTask.WaitAsync(TimeSpan.FromSeconds(20));

            await Assert.That(status.ExitCode).IsEqualTo(0);
        }
        finally
        {
            await cts.CancelAsync();
        }
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

    /// <summary>
    /// The bridge's output, with the time of each write, so that a test can tell when each frame had arrived whatever
    /// writes the bridge split it into.
    /// </summary>
    private sealed class TimedOutputStream : MemoryStream
    {
        // The stream's length after each write, and the write's Stopwatch timestamp.
        private readonly List<(long End, long Timestamp)> _writes = [];

        public override void Write(byte[] buffer, int offset, int count)
        {
            base.Write(buffer, offset, count);
            _writes.Add((Length, Stopwatch.GetTimestamp()));
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            base.Write(buffer);
            _writes.Add((Length, Stopwatch.GetTimestamp()));
        }

        /// <summary>Each output frame's arrival, the timestamp of the write that completed it, and its size, in order.</summary>
        public List<(long Timestamp, int Bytes)> OutputFrames()
        {
            var frames = new List<(long, int)>();
            var bytes = ToArray();
            var offset = 0;
            var write = 0;
            while (offset < bytes.Length)
            {
                var type = (PtyStdioFrameType)bytes[offset];
                var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 1, 4)));
                offset += 5 + length;
                while (_writes[write].End < offset)
                    write++;

                if (type == PtyStdioFrameType.Output)
                    frames.Add((_writes[write].Timestamp, length));
            }

            return frames;
        }
    }

    /// <summary>
    /// A peer that acknowledges each output frame as it arrives. The write that completes an output frame returns only
    /// once the bridge has read the acknowledgement and asked for its next frame, or after 2 s without that. The bridge
    /// handles a control frame before it reads the next header, so that read marks the acknowledgement handled.
    /// </summary>
    private sealed class AcknowledgingPeer
    {
        private readonly Lock _lock = new();
        private readonly List<byte> _written = [];
        private readonly Queue<byte> _toBridge = new();
        private readonly SemaphoreSlim _toBridgeAvailable = new(0);
        private readonly SemaphoreSlim _handled = new(0);
        private int _nextFrame;
        private bool _acknowledgementDelivered;

        public AcknowledgingPeer()
        {
            Input = new InputSide(this);
            Output = new OutputSide(this);
        }

        public Stream Input { get; }

        public Stream Output { get; }

        private async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            var acknowledged = false;
            lock (_lock)
            {
                _written.AddRange(buffer.Span);
                while (_written.Count - _nextFrame >= 5)
                {
                    var length = BinaryPrimitives.ReadInt32LittleEndian(CollectionsMarshal.AsSpan(_written).Slice(_nextFrame + 1, 4));
                    if (_written.Count - _nextFrame - 5 < length)
                        break;

                    if ((PtyStdioFrameType)_written[_nextFrame] == PtyStdioFrameType.Output && length > 0)
                    {
                        foreach (var b in CreateFrame(PtyStdioFrameType.Control, Encoding.UTF8.GetBytes($"{{\"type\":\"ack\",\"bytes\":{length}}}")))
                            _toBridge.Enqueue(b);

                        acknowledged = true;
                    }

                    _nextFrame += 5 + length;
                }
            }

            if (!acknowledged)
                return;

            _toBridgeAvailable.Release();
            await _handled.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
        }

        private async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            while (true)
            {
                lock (_lock)
                {
                    if (_toBridge.Count > 0)
                    {
                        var count = Math.Min(buffer.Length, _toBridge.Count);
                        for (var i = 0; i < count; i++)
                            buffer.Span[i] = _toBridge.Dequeue();

                        _acknowledgementDelivered = _toBridge.Count == 0;
                        return count;
                    }

                    if (_acknowledgementDelivered)
                    {
                        _acknowledgementDelivered = false;
                        _handled.Release();
                    }
                }

                await _toBridgeAvailable.WaitAsync(cancellationToken);
            }
        }

        private sealed class InputSide(AcknowledgingPeer peer) : Stream
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

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
                peer.ReadAsync(buffer, cancellationToken);
        }

        private sealed class OutputSide(AcknowledgingPeer peer) : Stream
        {
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
                peer.WriteAsync(buffer, cancellationToken);
        }
    }

    /// <summary>Releases input only after complete output frames contain the child's ready marker.</summary>
    private sealed class ReadyOutputStream : MemoryStream
    {
        private static readonly byte[] ReadyMarker = Encoding.UTF8.GetBytes(PtyWebSocketBridgeTests.KeyWaitReady);
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<byte> _payload = [];
        private int _nextFrame;

        public Task Ready => _ready.Task;

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            base.Write(buffer.Span);
            var written = GetBuffer().AsSpan(0, checked((int)Length));
            while (written.Length - _nextFrame >= 5)
            {
                var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(written.Slice(_nextFrame + 1, 4)));
                if (written.Length - _nextFrame - 5 < length)
                    break;

                if ((PtyStdioFrameType)written[_nextFrame] == PtyStdioFrameType.Output)
                    _payload.AddRange(written.Slice(_nextFrame + 5, length));
                _nextFrame += 5 + length;
            }

            if (CollectionsMarshal.AsSpan(_payload).IndexOf(ReadyMarker) >= 0)
                _ready.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CancelableInputStream(byte[] prefix, Task? ready = null) : Stream
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
            if (ready is not null)
                await ready.WaitAsync(cancellationToken);
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
