using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using MiniPty.Internal;

namespace MiniPty.Tests;

/// <summary>
/// The Windows output driver (<see cref="WindowsOutputDriver"/>), which reads every session on one loop thread, driven over
/// a plain named pipe whose writer the test controls, so each test decides when output arrives, stops, or floods. ConPTY
/// sessions cover the same driver end to end in the rest of the suite. Tests that count the loop's passes or swap a
/// driver between sessions run alone, since the loop and the pool of drivers serve every session in the process.
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

    /// <summary>
    /// A write that a waiting consumer reads costs the thread pool one work item, the consumer's continuation: the
    /// completion of the driver's zero-byte read runs on MiniPty's loop. Bound to the runtime's completion port, the
    /// completion was a pool work item of its own, which the runtime's I/O poller thread handed to a worker, so each
    /// keystroke and each of ConPTY's writes cost two.
    /// </summary>
    [Test]
    [NotInParallel]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task AWriteCostsThePoolOnlyTheConsumersContinuation()
    {
        if (!OperatingSystem.IsWindows())
            return;

        const int Writes = 200;
        var backend = new PipeBackend();
        await using var session = new PtySession(backend);
        await using var enumerator = session.ReadOutputAsync().GetAsyncEnumerator();

        // The first write also starts the pool's threads; it is not counted.
        backend.Write("x"u8);
        await Assert.That(await enumerator.MoveNextAsync().AsTask().WaitAsync(Timeout)).IsTrue();

        var rounds = Task.Run(async () =>
        {
            var before = ThreadPool.CompletedWorkItemCount;
            for (var i = 0; i < Writes; i++)
            {
                // The consumer waits first, so the write completes the driver's zero-byte read.
                var next = enumerator.MoveNextAsync();
                backend.Write("x"u8);
                if (!await next)
                    throw new EndOfStreamException();
            }

            return ThreadPool.CompletedWorkItemCount - before;
        });

        var handOffs = await rounds.WaitAsync(Timeout);
        await Assert.That(handOffs).IsLessThan(Writes * 3L / 2).Because($"{handOffs} thread-pool work items for {Writes} writes");
    }

    /// <summary>
    /// Output stays whole and in order while the loop parks on a full ring and resumes many times, across two
    /// enumerations: the first stops partway, and the second pauses every few chunks long enough for the ring to fill.
    /// </summary>
    [Test]
    public async Task OutputStaysInOrderAcrossParksAndEnumerations()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var expected = Pattern(2 * 1024 * 1024);
        var backend = new PipeBackend(pipeBufferSize: 64 * 1024);
        await using var session = new PtySession(backend);
        var writer = backend.WriteOnThread(expected, closeAfter: true, sliceSize: 4096);

        var received = new List<byte>(expected.Length);
        await using (var first = session.ReadOutputAsync().GetAsyncEnumerator())
        {
            while (received.Count < 256 * 1024 && await first.MoveNextAsync().AsTask().WaitAsync(Timeout))
                received.AddRange(first.Current.Data.Span);
        }

        var chunks = 0;
        await foreach (var chunk in session.ReadOutputAsync())
        {
            received.AddRange(chunk.Data.Span);
            if (++chunks % 16 == 0)
                await Task.Delay(20);
        }

        writer.Join();
        await Assert.That(received.ToArray().AsSpan().SequenceEqual(expected)).IsTrue();
    }

    /// <summary>
    /// A failure while the loop serves one session ends that session's output with the failure; another session keeps its
    /// output, and the loop keeps running.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task AFailureInOneSessionEndsOnlyThatSession()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var failingBackend = new PipeBackend();
        await using var failing = new PtySession(failingBackend);
        var healthyBackend = new PipeBackend();
        await using var healthy = new PtySession(healthyBackend);
        await using var healthyOutput = healthy.ReadOutputAsync().GetAsyncEnumerator();

        WindowsOutputDriver.FailReadsOfPumpForTests = failing.OutputPump;
        try
        {
            failingBackend.Write("trigger"u8);
            var failure = await Assert.That(async () => await ReadAllAsync(failing).WaitAsync(Timeout)).Throws<IOException>();
            await Assert.That(failure!.Message).Contains("Injected read failure");
        }
        finally
        {
            WindowsOutputDriver.FailReadsOfPumpForTests = null;
        }

        healthyBackend.Write("still here"u8);
        await Assert.That(await healthyOutput.MoveNextAsync().AsTask().WaitAsync(Timeout)).IsTrue();
        await Assert.That(System.Text.Encoding.ASCII.GetString(healthyOutput.Current.Data.Span)).IsEqualTo("still here");
    }

    /// <summary>
    /// A step that fails with its zero-byte read posted ends output only once that read has completed. The read points
    /// into the driver, which goes back to the pool when output ends: ended at once, the driver served the next session
    /// while the old read was still posted, and that read's completion, when the failed session's pipe closed, would have
    /// reached the next session as its own.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task AFailureWithAReadPostedEndsOutputOnlyOnceTheReadCompleted()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var failingBackend = new PipeBackend();
        await using var failing = new PtySession(failingBackend);
        var driver = (WindowsOutputDriver)failing.OutputPump.DriverForTests!;
        WindowsOutputDriver.FailWithReadPostedOfPumpForTests = failing.OutputPump;
        try
        {
            failingBackend.Write("x"u8);
            var failure = await Assert.That(async () => await ReadAllAsync(failing).WaitAsync(Timeout)).Throws<IOException>();
            await Assert.That(failure!.Message).Contains("Injected failure with a read posted");
        }
        finally
        {
            WindowsOutputDriver.FailWithReadPostedOfPumpForTests = null;
        }

        // The pool hands the failed session's driver to the next session.
        var nextBackend = new PipeBackend();
        await using var next = new PtySession(nextBackend);
        await Assert.That(ReferenceEquals(next.OutputPump.DriverForTests, driver)).IsTrue();
        await using var output = next.ReadOutputAsync().GetAsyncEnumerator();
        nextBackend.Write("one"u8);
        await Assert.That(await output.MoveNextAsync().AsTask().WaitAsync(Timeout)).IsTrue();
        await Assert.That(System.Text.Encoding.ASCII.GetString(output.Current.Data.Span)).IsEqualTo("one");

        // Closing the failed session's pipe would complete a read it had left posted.
        await failing.DisposeAsync();
        await Task.Delay(100);
        nextBackend.Write("two"u8);
        await Assert.That(await output.MoveNextAsync().AsTask().WaitAsync(Timeout)).IsTrue();
        await Assert.That(System.Text.Encoding.ASCII.GetString(output.Current.Data.Span)).IsEqualTo("two");
    }

    /// <summary>
    /// A wait on the loop's port that fails without a completion, as a port closed under the loop would, makes the loop
    /// back off instead of waiting again at once, and the loop reads what arrived meanwhile once waits succeed again.
    /// Without the backoff the loop spun, millions of passes in the same time.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task AFailedPortWaitBacksOffInsteadOfSpinning()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var backend = new PipeBackend();
        await using var session = new PtySession(backend);
        await using var output = session.ReadOutputAsync().GetAsyncEnumerator();
        backend.Write("a"u8);
        await Assert.That(await output.MoveNextAsync().AsTask().WaitAsync(Timeout)).IsTrue();

        var next = output.MoveNextAsync().AsTask();
        long passes;
        WindowsOutputDriver.Loop.FailWaitsForTests = true;
        try
        {
            var before = WindowsOutputDriver.Loop.Passes;
            backend.Write("b"u8);
            await Task.Delay(500);
            passes = WindowsOutputDriver.Loop.Passes - before;
        }
        finally
        {
            WindowsOutputDriver.Loop.FailWaitsForTests = false;
        }

        await Assert.That(passes).IsLessThan(100L);
        await Assert.That(await next.WaitAsync(Timeout)).IsTrue();
        await Assert.That(System.Text.Encoding.ASCII.GetString(output.Current.Data.Span)).IsEqualTo("b");
    }

    /// <summary>
    /// Idle sessions, and a session parked on a full ring while its writer waits, cost the loop no passes: each waits with
    /// one zero-byte read posted, or with no read at all, and nothing reports until output arrives or the consumer reads.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task IdleAndParkedSessionsDoNotSpinTheLoop()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var idleBackends = new PipeBackend[4];
        var idle = new PtySession[idleBackends.Length];
        var waits = new Task<bool>[idleBackends.Length];
        var outputs = new IAsyncEnumerator<PtyOutputChunk>[idleBackends.Length];
        try
        {
            for (var i = 0; i < idle.Length; i++)
            {
                idleBackends[i] = new PipeBackend();
                idle[i] = new PtySession(idleBackends[i]);
                outputs[i] = idle[i].ReadOutputAsync().GetAsyncEnumerator();
                idleBackends[i].Write("x"u8);
                await Assert.That(await outputs[i].MoveNextAsync().AsTask().WaitAsync(Timeout)).IsTrue();
                waits[i] = outputs[i].MoveNextAsync().AsTask();
            }

            var expected = Pattern(PtyOutputPump.Capacity * 8);
            var parkedBackend = new PipeBackend();
            await using var parked = new PtySession(parkedBackend);
            var writer = parkedBackend.WriteOnThread(expected, closeAfter: true);
            var received = new List<byte>(expected.Length);
            await using (var enumerator = parked.ReadOutputAsync().GetAsyncEnumerator())
            {
                // The first chunk is held, so the ring fills behind it and the driver parks.
                await Assert.That(await enumerator.MoveNextAsync().AsTask().WaitAsync(Timeout)).IsTrue();
                received.AddRange(enumerator.Current.Data.Span);
                await WaitUntilAsync(() => parked.OutputPump.IsWaitingForFreeSpace);

                var before = WindowsOutputDriver.Loop.Passes;
                await Task.Delay(500);
                var passes = WindowsOutputDriver.Loop.Passes - before;
                await Assert.That(passes).IsLessThan(50L);

                while (await enumerator.MoveNextAsync().AsTask().WaitAsync(Timeout))
                    received.AddRange(enumerator.Current.Data.Span);
            }

            writer.Join();
            await Assert.That(received.ToArray().AsSpan().SequenceEqual(expected)).IsTrue();
            foreach (var wait in waits)
                await Assert.That(wait.IsCompleted).IsFalse();
        }
        finally
        {
            for (var i = 0; i < idle.Length; i++)
            {
                if (idle[i] is null)
                    continue;

                await idle[i].DisposeAsync();
                try
                {
                    // Disposal ends the wait each idle session's consumer left pending.
                    if (waits[i] is { } wait)
                        await wait.WaitAsync(Timeout);
                }
                catch (ObjectDisposedException)
                {
                }
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

        /// <summary>
        /// Writes <paramref name="bytes"/> on a thread of its own in writes of <paramref name="sliceSize"/>, blocking while
        /// the pipe is full.
        /// </summary>
        public Thread WriteOnThread(byte[] bytes, bool closeAfter, int sliceSize = 1000)
        {
            var thread = new Thread(() =>
            {
                try
                {
                    for (var offset = 0; offset < bytes.Length; offset += sliceSize)
                        _writeStream.Write(bytes, offset, Math.Min(sliceSize, bytes.Length - offset));
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
