using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using MiniPty.Internal;

namespace MiniPty.Tests;

/// <summary>
/// The Windows output driver that holds no thread (<see cref="WindowsOutputDriver"/>), driven over a plain named pipe
/// whose writer the test controls, so each test decides when output arrives, stops, or floods. ConPTY sessions cover the
/// same driver end to end in the rest of the suite.
/// </summary>
public sealed class PtyWindowsOutputDriverTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>Output arrives whole and in order, and ends when the writer closes its end, as ConPTY's host does.</summary>
    [Test]
    public async Task DeliversOutputInOrderAndEndsWhenTheWriterCloses()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var expected = Pattern(256 * 1024);
        var backend = new PipeBackend();
        await using var session = new PtySession(backend);
        var writer = backend.WriteOnThread(expected, closeAfter: true);

        var received = await ReadAllAsync(session).WaitAsync(Timeout);
        writer.Join();

        await Assert.That(received.AsSpan().SequenceEqual(expected)).IsTrue();
    }

    /// <summary>
    /// While the ring is full the driver posts no read, so the pipe fills and the writer blocks, as ConPTY does under a
    /// consumer that stopped. Releasing the chunk resumes reading, and nothing is lost.
    /// </summary>
    [Test]
    public async Task AFullRingPostsNoReadAndResumesWhenTheConsumerReleases()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // The ring, the pipe's buffer, and a good deal more, so the writer must wait for the consumer.
        var expected = Pattern(PtyOutputPump.Capacity * 8);
        var backend = new PipeBackend();
        await using var session = new PtySession(backend);
        var writer = backend.WriteOnThread(expected, closeAfter: true);

        var received = new List<byte>(expected.Length);
        await using (var enumerator = session.ReadOutputAsync().GetAsyncEnumerator())
        {
            await Assert.That(await enumerator.MoveNextAsync().AsTask().WaitAsync(Timeout)).IsTrue();
            received.AddRange(enumerator.Current.Data.Span);

            // The first chunk is held, so the ring fills behind it and the driver parks with no read posted.
            await WaitUntilAsync(() => session.OutputPump.IsWaitingForFreeSpace);
            await Task.Delay(300);
            await Assert.That(session.OutputPump.IsWaitingForFreeSpace).IsTrue();
            await Assert.That(writer.IsAlive).IsTrue();

            while (await enumerator.MoveNextAsync().AsTask().WaitAsync(Timeout))
                received.AddRange(enumerator.Current.Data.Span);
        }

        writer.Join();
        await Assert.That(received.ToArray().AsSpan().SequenceEqual(expected)).IsTrue();
    }

    /// <summary>
    /// A driver that parked on a full ring and resumed counts as reading again, so the post-exit drain still ends output
    /// when the close does not end the pipe. A resumed driver that stayed marked as waiting kept the drain from ever
    /// seeing quiet, and the session never reached end of output.
    /// </summary>
    [Test]
    public async Task TheDrainEndsOutputAfterTheDriverParked()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var expected = Pattern(PtyOutputPump.Capacity * 2);
        var backend = new PipeBackend();
        await using var session = new PtySession(backend);
        // The writer's end stays open, as when another process holds it.
        var writer = backend.WriteOnThread(expected, closeAfter: false);

        var received = new List<byte>(expected.Length);
        await using var enumerator = session.ReadOutputAsync().GetAsyncEnumerator();
        await Assert.That(await enumerator.MoveNextAsync().AsTask().WaitAsync(Timeout)).IsTrue();
        received.AddRange(enumerator.Current.Data.Span);
        await WaitUntilAsync(() => session.OutputPump.IsWaitingForFreeSpace);

        while (received.Count < expected.Length && await enumerator.MoveNextAsync().AsTask().WaitAsync(Timeout))
            received.AddRange(enumerator.Current.Data.Span);

        writer.Join();
        backend.Exit(0);
        await Assert.That(await enumerator.MoveNextAsync().AsTask().WaitAsync(Timeout)).IsFalse();
        await Assert.That(received.ToArray().AsSpan().SequenceEqual(expected)).IsTrue();
    }

    /// <summary>
    /// While output is unread, the driver waits with a zero-byte read, not a read posted into the ring: such a read had
    /// to be canceled each time the consumer caught up, which cost a completion and a pool hop per ConPTY write. The wait
    /// still reads the next output while the first is unread, and the ring goes back once the consumer releases both.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task UnreadOutputLeavesTheDriverWaitingWithoutABuffer()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var backend = new PipeBackend();
        await using var session = new PtySession(backend);
        await using var enumerator = session.ReadOutputAsync().GetAsyncEnumerator();
        var before = PtyBufferPool.RentedCount;

        backend.Write("first"u8);
        await Assert.That(await enumerator.MoveNextAsync().AsTask().WaitAsync(Timeout)).IsTrue();
        await Assert.That(System.Text.Encoding.ASCII.GetString(enumerator.Current.Data.Span)).IsEqualTo("first");

        // The chunk is held, so its output is unread; the driver still waits without a buffer.
        var driver = (WindowsOutputDriver)session.OutputPump.DriverForTests!;
        await WaitUntilAsync(() => OperatingSystem.IsWindows() && driver.IsWaitingWithoutBufferForTests);
        await Assert.That(session.OutputPump.HasUnreadOutput).IsTrue();

        // The wait reads output that arrives while the first chunk is still held.
        backend.Write("second"u8);
        await WaitUntilAsync(() => session.OutputPump.UnreadCount == "firstsecond".Length);
        await WaitUntilAsync(() => OperatingSystem.IsWindows() && driver.IsWaitingWithoutBufferForTests);

        await Assert.That(await enumerator.MoveNextAsync().AsTask().WaitAsync(Timeout)).IsTrue();
        await Assert.That(System.Text.Encoding.ASCII.GetString(enumerator.Current.Data.Span)).IsEqualTo("second");

        // Releasing the last chunk gives the ring back, with no wake of the driver needed.
        var next = enumerator.MoveNextAsync().AsTask();
        await WaitUntilAsync(() => PtyBufferPool.RentedCount == before);

        backend.Write("third"u8);
        await Assert.That(await next.WaitAsync(Timeout)).IsTrue();
        await Assert.That(System.Text.Encoding.ASCII.GetString(enumerator.Current.Data.Span)).IsEqualTo("third");
    }

    /// <summary>
    /// Once the consumer has released everything, the ring goes back to the pool, so an idle session holds none. The
    /// zero-byte read the driver waits with still wakes it for the next output.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task AnIdleSessionGivesItsRingBackAndStillReadsTheNextOutput()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var backend = new PipeBackend();
        await using var session = new PtySession(backend);
        await using var enumerator = session.ReadOutputAsync().GetAsyncEnumerator();
        var before = PtyBufferPool.RentedCount;

        backend.Write("first"u8);
        await Assert.That(await enumerator.MoveNextAsync().AsTask().WaitAsync(Timeout)).IsTrue();
        await Assert.That(System.Text.Encoding.ASCII.GetString(enumerator.Current.Data.Span)).IsEqualTo("first");
        await Assert.That(PtyBufferPool.RentedCount).IsEqualTo(before + 1);

        // Asking for the next chunk releases this one; the ring then has nothing unread and goes back.
        var next = enumerator.MoveNextAsync().AsTask();
        await WaitUntilAsync(() => PtyBufferPool.RentedCount == before);

        backend.Write("second"u8);
        await Assert.That(await next.WaitAsync(Timeout)).IsTrue();
        await Assert.That(System.Text.Encoding.ASCII.GetString(enumerator.Current.Data.Span)).IsEqualTo("second");
    }

    /// <summary>
    /// When the close does not end the pipe (another process holds its write end), the drain forces end of output by
    /// canceling the posted zero-byte read, whether or not the consumer has taken the output yet; output it has not
    /// taken must still arrive.
    /// </summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TheForcedEndCancelsThePostedRead(bool outputUnread)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var backend = new PipeBackend();
        await using var session = new PtySession(backend);
        await using var enumerator = session.ReadOutputAsync().GetAsyncEnumerator();
        backend.Write("output"u8);
        if (!outputUnread)
        {
            await Assert.That(await enumerator.MoveNextAsync().AsTask().WaitAsync(Timeout)).IsTrue();
            // Releases the chunk; the driver then waits with a zero-byte read.
            var pending = enumerator.MoveNextAsync().AsTask();
            backend.Exit(0);
            await Assert.That(await pending.WaitAsync(Timeout)).IsFalse();
            return;
        }

        await WaitUntilAsync(() => session.OutputPump.HasUnreadOutput);
        var started = Stopwatch.GetTimestamp();
        backend.Exit(0);
        await WaitUntilAsync(() => session.OutputPump.IsProducerFinished);

        // Exit, 100 ms quiet, the close, 100 ms quiet, the forced end: well under the timeout.
        await Assert.That(Stopwatch.GetElapsedTime(started)).IsLessThan(TimeSpan.FromSeconds(5));
        await Assert.That(await enumerator.MoveNextAsync().AsTask().WaitAsync(Timeout)).IsTrue();
        await Assert.That(System.Text.Encoding.ASCII.GetString(enumerator.Current.Data.Span)).IsEqualTo("output");
        await Assert.That(await enumerator.MoveNextAsync().AsTask().WaitAsync(Timeout)).IsFalse();
    }

    /// <summary>
    /// Disposal in the middle of a flood stops the driver promptly, whether a read is posted or the driver is reading
    /// what the pipe holds, and the writer sees the pipe close instead of hanging.
    /// </summary>
    [Test]
    public async Task DisposalDuringAFloodStopsTheDriver()
    {
        if (!OperatingSystem.IsWindows())
            return;

        for (var round = 0; round < 200; round++)
        {
            var backend = new PipeBackend();
            var session = new PtySession(backend);
            var writer = backend.FloodOnThread();
            var consumer = ConsumeAsync(session);

            await Task.Delay(Random.Shared.Next(0, 5));
            await session.DisposeAsync().AsTask().WaitAsync(Timeout);

            await Assert.That(writer.Join(Timeout)).IsTrue();
            await consumer.WaitAsync(Timeout);
        }
    }

    /// <summary>
    /// Disposal stops the driver even when the pipe never runs empty, because the child writes faster than the driver
    /// reads. Each step that keeps finding output goes back to the pool after a bounded number of reads, which lets go of
    /// its pin on the pipe, so disposal can close it.
    /// </summary>
    /// <remarks>
    /// A step that kept reading held its pin, disposal could not close the pipe whose close ends the flood, and the
    /// flood never ended: disposal hung, in about one of a thousand rounds of the flood above. A pipe with a large buffer
    /// and a writer that fills it in large writes keeps output waiting at nearly every read once disposal has the driver
    /// discard it: nobody consumes, so the ring fills, the driver parks, and the pipe fills behind it before disposal.
    /// Without the bound, two of three runs of this test hung.
    /// </remarks>
    [Test]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task DisposalStopsTheDriverWhenThePipeNeverRunsEmpty()
    {
        if (!OperatingSystem.IsWindows())
            return;

        for (var round = 0; round < 5; round++)
        {
            var backend = new PipeBackend(pipeBufferSize: 4 * 1024 * 1024);
            var session = new PtySession(backend);
            var writer = backend.FloodOnThread(sliceSize: 256 * 1024);
            await WaitUntilAsync(() => session.OutputPump.IsWaitingForFreeSpace && backend.BytesWaiting > 1024 * 1024);

            await session.DisposeAsync().AsTask().WaitAsync(Timeout);

            await Assert.That(writer.Join(Timeout)).IsTrue();
        }
    }

    /// <summary>
    /// A completion that arrives after disposal never writes into a ring that went back to the pool: the ring stays held
    /// while a read is posted into it, and the driver finishes only after the last completion. Rings returned by disposed
    /// floods are rented again at once and checked for writes that should not reach them.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task ACompletionAfterDisposalNeverTouchesAReturnedRing()
    {
        if (!OperatingSystem.IsWindows())
            return;

        const byte Marker = 0xA5;
        for (var round = 0; round < 100; round++)
        {
            var backend = new PipeBackend();
            var session = new PtySession(backend);
            var writer = backend.FloodOnThread();
            var consumer = ConsumeAsync(session);
            await Task.Delay(Random.Shared.Next(0, 10));
            await session.DisposeAsync().AsTask().WaitAsync(Timeout);

            // The most recently returned rings come back first.
            var rings = new byte[4][];
            for (var i = 0; i < rings.Length; i++)
            {
                rings[i] = PtyBufferPool.Rent();
                rings[i].AsSpan().Fill(Marker);
            }

            writer.Join(Timeout);
            await consumer.WaitAsync(Timeout);
            await Task.Delay(5);
            try
            {
                foreach (var ring in rings)
                    await Assert.That(ring.AsSpan().IndexOfAnyExcept(Marker)).IsEqualTo(-1);
            }
            finally
            {
                foreach (var ring in rings)
                    PtyBufferPool.Return(ring);
            }
        }
    }

    /// <summary>Windows sessions read through I/O completions on the overlapped output pipe.</summary>
    [Test]
    public async Task SessionsReadThroughIoCompletions()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
        await using var session = Pty.Start(new PtyStartInfo { FileName = cmd, Arguments = ["/c", "echo driver-marker"] });
        await Assert.That(session.OutputPump.Driver).IsTypeOf<WindowsOutputDriver>();
        var text = System.Text.Encoding.UTF8.GetString(await ReadAllAsync(session).WaitAsync(Timeout));
        await Assert.That(text).Contains("driver-marker");
    }

    private static byte[] Pattern(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)(i * 31 + (i >> 8));

        return bytes;
    }

    private static async Task<byte[]> ReadAllAsync(PtySession session)
    {
        var received = new List<byte>();
        await foreach (var chunk in session.ReadOutputAsync())
            received.AddRange(chunk.Data.Span);

        return received.ToArray();
    }

    /// <summary>Reads until end of output or disposal, as a web host's consumer does.</summary>
    private static async Task ConsumeAsync(PtySession session)
    {
        try
        {
            await foreach (var _ in session.ReadOutputAsync())
            {
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 10;
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() > deadline)
                throw new TimeoutException("The condition did not hold within 10 s.");

            await Task.Delay(10);
        }
    }

    /// <summary>
    /// A child whose output is a named pipe created exactly like ConPTY's, read by <see cref="WindowsOutputDriver"/>. The
    /// test writes the other end. Closing the transport does not end the pipe, as when another process holds it; only
    /// the writer's close or disposal does.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private sealed class PipeBackend : PtyOutputPumpTests.FakeBackend
    {
        private readonly SafeFileHandle _reader;
        private readonly SafeFileHandle _writer;
        private readonly FileStream _writeStream;

        public PipeBackend(int pipeBufferSize = 4096)
        {
            WindowsPtyBackend.CreateOverlappedPipe(@"\\.\pipe\minipty-test-output-", inbound: true, pipeBufferSize, out _writer, out _reader);
            _writeStream = new FileStream(_writer, FileAccess.Write, bufferSize: 0, isAsync: false);
        }

        public override int OutputChunkLimit => PtyOutputPump.Capacity;

        public override void StartOutput(PtyOutputPump pump) => WindowsOutputDriver.Run(pump, _reader);

        public void Write(ReadOnlySpan<byte> bytes) => _writeStream.Write(bytes);

        /// <summary>Bytes written and not yet read by the driver.</summary>
        public unsafe int BytesWaiting
        {
            get
            {
                var pinned = false;
                try
                {
                    _reader.DangerousAddRef(ref pinned);
                    uint available;
                    return WindowsInterop.PeekNamedPipe(_reader.DangerousGetHandle(), IntPtr.Zero, 0, IntPtr.Zero, &available, IntPtr.Zero)
                        ? (int)available
                        : 0;
                }
                finally
                {
                    if (pinned)
                        _reader.DangerousRelease();
                }
            }
        }

        /// <summary>Writes <paramref name="bytes"/> on a thread of its own, which blocks while the pipe is full.</summary>
        public Thread WriteOnThread(byte[] bytes, bool closeAfter)
        {
            var thread = new Thread(() =>
            {
                try
                {
                    for (var offset = 0; offset < bytes.Length; offset += 1000)
                        _writeStream.Write(bytes, offset, Math.Min(1000, bytes.Length - offset));
                }
                catch (IOException)
                {
                }

                if (closeAfter)
                    _writeStream.Dispose();
            })
            {
                IsBackground = true,
            };
            thread.Start();
            return thread;
        }

        /// <summary>
        /// Writes until the pipe closes, in slices like ConPTY's, or with <paramref name="sliceSize"/> large, in writes the
        /// pipe refills from as fast as it is read, so it never runs empty while the driver reads.
        /// </summary>
        public Thread FloodOnThread(int sliceSize = 1700)
        {
            var thread = new Thread(() =>
            {
                var slice = new byte[sliceSize];
                slice.AsSpan().Fill((byte)'x');
                try
                {
                    while (true)
                        _writeStream.Write(slice);
                }
                catch (IOException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
            })
            {
                IsBackground = true,
            };
            thread.Start();
            return thread;
        }

        public override void Dispose()
        {
            base.Dispose();
            // As the backend closes its end of the output pipe at disposal, which ends a posted read.
            _reader.Dispose();
        }
    }
}
