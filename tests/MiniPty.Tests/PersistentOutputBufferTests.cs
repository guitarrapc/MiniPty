using System.Text;
using MiniPty.Terminal.Internal;

namespace MiniPty.Tests;

public sealed class PersistentOutputBufferTests
{
    [Test]
    public async Task PersistentOutputBufferReplaysAcrossRingWrap()
    {
        using var buffer = new PersistentOutputBuffer(8);
        await buffer.WriteAsync("abcdef"u8.ToArray(), CancellationToken.None);
        await Assert.That(buffer.TryAcknowledge(4, 6)).IsTrue();
        await buffer.WriteAsync("ghijkl"u8.ToArray(), CancellationToken.None);

        var output = new MemoryStream();
        var offset = 4L;
        while (offset < 12)
        {
            var read = await buffer.ReadAsync(offset, 8, CancellationToken.None);
            output.Write(read.Data.Span);
            offset += read.Data.Length;
        }

        await Assert.That(Encoding.UTF8.GetString(output.ToArray())).IsEqualTo("efghijkl");
    }

    [Test]
    public async Task PersistentOutputBufferBackpressuresUntilAcknowledged()
    {
        using var buffer = new PersistentOutputBuffer(4);
        await buffer.WriteAsync("abcd"u8.ToArray(), CancellationToken.None);
        var blockedWrite = buffer.WriteAsync("e"u8.ToArray(), CancellationToken.None).AsTask();
        await Task.Delay(50);
        await Assert.That(blockedWrite.IsCompleted).IsFalse();

        await Assert.That(buffer.TryAcknowledge(4, 4)).IsTrue();
        await blockedWrite.WaitAsync(TimeSpan.FromSeconds(1));
        var read = await buffer.ReadAsync(4, 4, CancellationToken.None);
        await Assert.That(Encoding.UTF8.GetString(read.Data.Span)).IsEqualTo("e");
    }

    [Test]
    public async Task PersistentOutputBufferRejectsAcknowledgementBeyondSentOffset()
    {
        using var buffer = new PersistentOutputBuffer(8);
        await buffer.WriteAsync("abcd"u8.ToArray(), CancellationToken.None);

        await Assert.That(buffer.TryAcknowledge(4, 3)).IsFalse();
        await Assert.That(buffer.OffsetRange.Start).IsEqualTo(0);
    }

    [Test]
    public async Task PersistentOutputBufferDrainsBufferedBytesBeforeSurfacingFault()
    {
        using var buffer = new PersistentOutputBuffer(8);
        await buffer.WriteAsync("tail"u8.ToArray(), CancellationToken.None);
        buffer.Fault(new IOException("transport failed"));

        var tail = await buffer.ReadAsync(0, 8, CancellationToken.None);
        await Assert.That(Encoding.UTF8.GetString(tail.Data.Span)).IsEqualTo("tail");
        await Assert.ThrowsAsync<IOException>(async () =>
            await buffer.ReadAsync(4, 8, CancellationToken.None));
    }
}
