using System.Diagnostics;
using MiniPty.Internal;

namespace MiniPty.Tests;

/// <summary>
/// Output pump behavior that does not depend on a real transport, driven through a scripted backend.
/// </summary>
public sealed class PtyOutputPumpTests
{
    /// <summary>
    /// End of output completes a pending read on the output driver's thread, which posts the reader's continuation to
    /// the context it captured. A context that throws from <c>Post</c> (a shut-down UI dispatcher) must not terminate
    /// the process; without the guard this test crashes the test host.
    /// </summary>
    [Test]
    public async Task PendingReadUnderThrowingContextDoesNotCrashTheDriverThread()
    {
        var backend = new SilentBackend();
        var session = new PtySession(backend);
        var enumerator = session.ReadOutputAsync().GetAsyncEnumerator();
        var context = new ThrowingSynchronizationContext();
        var pending = RunUnder(context, () => AwaitUnderCurrentContext(enumerator.MoveNextAsync()));
        await Assert.That(pending.IsCompleted).IsFalse();

        backend.EndOutput();

        await Assert.That((await context.WaitForPostAsync()).ThreadName).IsEqualTo(ScriptedOutputDriver.ThreadName);
        await session.DisposeAsync().AsTask().WaitAsync(Timeout);
        await Assert.That(pending.IsCompleted).IsFalse();
    }

    /// <summary>
    /// Disposal wakes a pending read on the disposing thread; a throwing context must not make disposal throw.
    /// </summary>
    [Test]
    public async Task DisposeWithPendingReadUnderThrowingContextDoesNotThrow()
    {
        var backend = new SilentBackend();
        var session = new PtySession(backend);
        var enumerator = session.ReadOutputAsync().GetAsyncEnumerator();
        var context = new ThrowingSynchronizationContext();
        var pending = RunUnder(context, () => AwaitUnderCurrentContext(enumerator.MoveNextAsync()));

        var disposingThread = Environment.CurrentManagedThreadId;
        var disposed = session.DisposeAsync();

        await Assert.That((await context.WaitForPostAsync()).ThreadId).IsEqualTo(disposingThread);
        await disposed.AsTask().WaitAsync(Timeout);
        await Assert.That(pending.IsCompleted).IsFalse();
    }

    /// <summary>
    /// <c>DisposeAsync</c> completes when output reading stops, on the output driver's thread. Awaiting it under a
    /// throwing context must not terminate the process from that thread.
    /// </summary>
    [Test]
    public async Task DisposeAsyncAwaitedUnderThrowingContextDoesNotCrashTheDriverThread()
    {
        var backend = new SilentBackend(holdEndOfOutput: true);
        var session = new PtySession(backend);
        // Disposing before the reader starts reading would end it without a read, so nothing would be held.
        await Assert.That(backend.Reading.WaitAsync(Timeout).ContinueWith(static t => t.IsCompletedSuccessfully)).IsTrue();
        var context = new ThrowingSynchronizationContext();
        var disposing = RunUnder(context, () => AwaitUnderCurrentContext(session.DisposeAsync()));
        await Assert.That(disposing.IsCompleted).IsFalse();

        backend.ReleaseEndOfOutput();

        await Assert.That((await context.WaitForPostAsync()).ThreadName).IsEqualTo(ScriptedOutputDriver.ThreadName);
        await Assert.That(disposing.IsCompleted).IsFalse();
    }

    /// <summary>
    /// Cancellation completes a pending read on the canceling thread (a timer thread for <c>CancelAfter</c>). A
    /// throwing context must not make <c>Cancel</c> throw or terminate the process from a timer thread.
    /// </summary>
    [Test]
    public async Task CancelingReadOutputUnderThrowingContextDoesNotThrow()
    {
        var backend = new SilentBackend();
        await using var session = new PtySession(backend);
        using var cts = new CancellationTokenSource();
        var enumerator = session.ReadOutputAsync(cts.Token).GetAsyncEnumerator();
        var context = new ThrowingSynchronizationContext();
        var pending = RunUnder(context, () => AwaitUnderCurrentContext(enumerator.MoveNextAsync()));

        cts.Cancel();

        await Assert.That((await context.WaitForPostAsync()).ThreadId).IsEqualTo(Environment.CurrentManagedThreadId);
        await Assert.That(pending.IsCompleted).IsFalse();
    }

    /// <summary>The <c>Output</c> stream's pending read, canceled under a throwing context.</summary>
    [Test]
    public async Task CancelingOutputStreamReadUnderThrowingContextDoesNotThrow()
    {
        var backend = new SilentBackend();
        await using var session = new PtySession(backend);
        using var cts = new CancellationTokenSource();
        var buffer = new byte[16];
        var context = new ThrowingSynchronizationContext();
        var pending = RunUnder(context, () => AwaitUnderCurrentContext(session.Output.ReadAsync(buffer, cts.Token)));

        cts.Cancel();

        await Assert.That((await context.WaitForPostAsync()).ThreadId).IsEqualTo(Environment.CurrentManagedThreadId);
        await Assert.That(pending.IsCompleted).IsFalse();
    }

    /// <summary>
    /// Time the reader spends parked on a full ring is not transport silence. After a stall that outlasts the quiet
    /// period, resuming must restart it; otherwise a drain tick before the next read closes the transport and the
    /// output still waiting in it is lost.
    /// </summary>
    /// <remarks>Paced against the quiet period, so it runs alone (see <see cref="PacedQuietMs"/>).</remarks>
    [Test]
    [NotInParallel]
    public async Task ResumingAfterBackpressureDoesNotCloseTheTransport()
    {
        var backend = new StallThenSlowBackend();
        await using var session = new PtySession(backend);

        // No consumer: the ring fills and the reader parks. Exit is then observed, and the stall outlasts the quiet period.
        await Task.Delay(200);
        session.OutputPump.OnChildExited();
        await Task.Delay(PacedQuietMs + 200);

        // Each read after the stall takes a tenth of the quiet period, so the transport must stay open.
        await using var enumerator = session.ReadOutputAsync().GetAsyncEnumerator();
        for (var i = 0; i < ReadsThatFillTheRing + 4; i++)
            await Assert.That(await enumerator.MoveNextAsync().AsTask().WaitAsync(Timeout)).IsTrue();

        await Assert.That(backend.IsOutputClosed).IsFalse();
    }

    /// <summary>
    /// A completion's exit wait resumes before the exit listener runs, and can schedule drain ticks then. Without
    /// the exit tick, the quiet and grace deadlines read as long past and the transport was closed at once.
    /// </summary>
    [Test]
    public async Task DrainTicksBeforeExitIsObservedDoNotCloseTheTransport()
    {
        var backend = new SilentBackend();
        await using var session = new PtySession(backend);
        await backend.Reading.WaitAsync(Timeout);

        PtyOutputDrainScheduler.Schedule(session.OutputPump);
        await Task.Delay(300);

        await Assert.That(backend.IsOutputClosed).IsFalse();
    }

    /// <summary>
    /// The array overload of <c>Output.ReadAsync</c> waits like the memory overload. Stream's default ran the
    /// blocking read on a thread-pool worker, where cancellation could not reach it.
    /// </summary>
    [Test]
    public async Task OutputArrayReadAsyncWaitsWithoutBlockingAndCancels()
    {
        var backend = new SilentBackend();
        await using var session = new PtySession(backend);
        using var cts = new CancellationTokenSource();
        var buffer = new byte[16];

        var read = session.Output.ReadAsync(buffer, 0, buffer.Length, cts.Token);
        await Assert.That(read.IsCompleted).IsFalse();

        await cts.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await read.WaitAsync(Timeout));
    }

    /// <summary>
    /// Disposal having started does not mean the reader has stopped: <c>DisposeAsync</c> after <c>Dispose</c>, and
    /// a concurrent second <c>DisposeAsync</c>, must both wait for it.
    /// </summary>
    [Test]
    public async Task EveryDisposeAsyncWaitsForTheReader()
    {
        var backend = new SilentBackend(holdEndOfOutput: true);
        var session = new PtySession(backend);
        await backend.Reading.WaitAsync(Timeout);

        session.Dispose();
        var first = session.DisposeAsync();
        var second = session.DisposeAsync();
        await Assert.That(first.IsCompleted).IsFalse();
        await Assert.That(second.IsCompleted).IsFalse();

        backend.ReleaseEndOfOutput();
        await first.AsTask().WaitAsync(Timeout);
        await second.AsTask().WaitAsync(Timeout);
    }

    /// <summary>
    /// Closing the transport does not always end it: on Windows a process that inherited ConPTY's output pipe keeps
    /// it open until it exits. Once reads have been quiet since the close, the pump ends output itself rather than
    /// waiting for that process. Without the forced end, the read below never completes.
    /// </summary>
    [Test]
    public async Task OutputEndsAfterQuietWhenTheClosedTransportStaysOpen()
    {
        var backend = new HeldOpenBackend();
        await using var session = new PtySession(backend);
        await using var enumerator = session.ReadOutputAsync().GetAsyncEnumerator();
        var next = enumerator.MoveNextAsync().AsTask();
        await backend.Reading.WaitAsync(Timeout);

        var stopwatch = Stopwatch.StartNew();
        backend.Exit(0);

        await Assert.That(await next.WaitAsync(Timeout)).IsFalse();
        // Quiet after exit closes the transport, and quiet after the close ends output: two intervals, no OS event.
        await Assert.That(stopwatch.ElapsedMilliseconds).IsLessThan(ForcedEndBoundMs);
        await Assert.That(backend.IsOutputClosed).IsTrue();
    }

    /// <summary>
    /// The same held-open transport under one-shot completion: it must complete, not fail with the close timeout
    /// (<see cref="PtyCompleteOptions.OutputReaderCloseTimeout"/>) although every byte had been read.
    /// </summary>
    [Test]
    public async Task CompleteAsyncDoesNotTimeOutWhenTheClosedTransportStaysOpen()
    {
        var backend = new HeldOpenBackend();
        await using var session = new PtySession(backend);
        await backend.Reading.WaitAsync(Timeout);
        // The close timeout outlasts the bound below, so an end that waited for it cannot pass as the forced end.
        var completion = session.CompleteAsync(new PtyCompleteOptions { OutputReaderCloseTimeout = TimeSpan.FromMilliseconds(ForcedEndBoundMs * 2) });

        var stopwatch = Stopwatch.StartNew();
        backend.Exit(7);

        var result = await completion.WaitAsync(Timeout);
        await Assert.That(result.ExitCode).IsEqualTo(7);
        await Assert.That(stopwatch.ElapsedMilliseconds).IsLessThan(ForcedEndBoundMs);
    }

    /// <summary>
    /// A one-shot completion does not close on quiet before its first read, since ConPTY can send its first frame after
    /// a fast child has exited: a transport that sends nothing until its close is closed only at the grace, and its frame
    /// then still arrives.
    /// </summary>
    [Test]
    public async Task CompleteAsyncClosesASilentTransportAtItsGrace()
    {
        var grace = TimeSpan.FromSeconds(1);
        var backend = new SilentUntilTheCloseBackend(closeSendsHeldOutput: false);
        await using var session = new PtySession(backend);
        await backend.Reading.WaitAsync(Timeout);
        var completion = session.CompleteAsync(new PtyCompleteOptions { DecodeOutput = false, OutputDrainGrace = grace });

        var exited = Stopwatch.GetTimestamp();
        backend.Exit(0);

        var result = await completion.WaitAsync(Timeout);
        // When the close came, not whether it had come after a fixed wait: on a loaded CI runner a 300 ms wait overran the
        // 1 s grace, and the close it found was the grace's. A close on quiet comes one quiet interval after the exit. The
        // grace's never comes earlier than the grace, apart from the tick count's granularity, which that interval covers.
        var closedAfter = backend.ClosedAfter(exited);
        await Assert.That(closedAfter).IsGreaterThanOrEqualTo(grace - TimeSpan.FromMilliseconds(PtyOutputPump.DefaultPostExitQuietMs));
        var hasFrame = result.Contains(SilentUntilTheCloseBackend.Frame);
        await Assert.That(hasFrame).IsTrue();
    }

    /// <summary>
    /// Where the close sends what the transport holds, as an in-box ConPTY before Windows 11 24H2 does, a one-shot
    /// completion closes a silent transport on quiet, and gets the frame the close sends. That host writes nothing for a
    /// child that writes nothing until the close, so waiting for the first read cost such a child the whole grace there.
    /// </summary>
    [Test]
    public async Task CompleteAsyncClosesASilentTransportOnQuietWhereTheCloseSendsWhatItHolds()
    {
        var backend = new SilentUntilTheCloseBackend(closeSendsHeldOutput: true);
        await using var session = new PtySession(backend);
        await backend.Reading.WaitAsync(Timeout);
        // The grace outlasts the bound below, so a close at the grace cannot pass as the quiet close.
        var completion = session.CompleteAsync(new PtyCompleteOptions { DecodeOutput = false, OutputDrainGrace = TimeSpan.FromMilliseconds(ForcedEndBoundMs * 2) });

        var stopwatch = Stopwatch.StartNew();
        backend.Exit(0);

        var result = await completion.WaitAsync(Timeout);
        await Assert.That(stopwatch.ElapsedMilliseconds).IsLessThan(ForcedEndBoundMs);
        var hasFrame = result.Contains(SilentUntilTheCloseBackend.Frame);
        await Assert.That(hasFrame).IsTrue();
    }

    /// <summary>
    /// Where the close waits for the transport's host, as an in-box ConPTY's without <c>ReleasePseudoConsole</c> does, the
    /// host writes its frame during the close. The close here returns three quiet intervals after it starts, so a forced
    /// end counted from the request would come first and find nothing to read.
    /// </summary>
    /// <remarks>Timed against the wait, so it runs alone, as the tests paced against <see cref="PacedQuietMs"/> do.</remarks>
    [Test]
    [NotInParallel]
    public async Task AFrameTheHostWritesAsASlowCloseReturnsIsDelivered()
    {
        // The close returns three quiet intervals after it starts: later than a forced end counted from the request would
        // come, and well inside the wait.
        var backend = new SlowClosingHostBackend(closeReturnsAfterMs: PtyOutputPump.DefaultPostExitQuietMs * 3);
        await using var session = new PtySession(backend);
        await backend.Reading.WaitAsync(Timeout);
        var completion = session.CompleteAsync(new PtyCompleteOptions { DecodeOutput = false });

        backend.Exit(0);

        var result = await completion.WaitAsync(Timeout);
        var hasFrame = result.Contains(SlowClosingHostBackend.Frame);
        await Assert.That(hasFrame).IsTrue();
    }

    /// <summary>
    /// A close that waits for its host and never returns does not keep output open: output ends, and never before
    /// <see cref="PtyOutputPump.CloseReturnWaitMs"/>.
    /// </summary>
    [Test]
    public async Task OutputEndsWhenACloseThatWaitsForItsHostNeverReturns()
    {
        var backend = new SlowClosingHostBackend(closeReturnsAfterMs: System.Threading.Timeout.Infinite);
        await using var session = new PtySession(backend);
        await using var enumerator = session.ReadOutputAsync().GetAsyncEnumerator();
        var next = enumerator.MoveNextAsync().AsTask();
        await backend.Reading.WaitAsync(Timeout);

        var stopwatch = Stopwatch.StartNew();
        backend.Exit(0);

        await Assert.That(await next.WaitAsync(Timeout)).IsFalse();
        // Never earlier: the quiet before the close and the wait for its return both come first.
        await Assert.That(stopwatch.ElapsedMilliseconds).IsGreaterThanOrEqualTo(PtyOutputPump.CloseReturnWaitMs);
    }

    /// <summary>
    /// A completion's close timeout shorter than a close that waits for its host does not fail the completion. The close
    /// here returns 600 ms after it starts and the timeout is 450 ms, so a timeout counted from the request fires, whether
    /// it is checked while the close is pending or once it is done. Output ends 50 ms after the frame, which gives drain
    /// ticks time to check the timeout once the close is done. Counted from the return, it leaves 450 ms.
    /// </summary>
    /// <remarks>Timed against the close timeout, so it runs alone, as the tests paced against <see cref="PacedQuietMs"/> do.</remarks>
    // Retried: a runner that holds the scripted return thread back by about half a second loses the frame. A timeout
    // counted from the request fails every attempt.
    [Test]
    [NotInParallel]
    [Retry(3)]
    public async Task ACloseTimeoutShorterThanASlowCloseDoesNotFailTheCompletion()
    {
        var backend = new SlowClosingHostBackend(closeReturnsAfterMs: 600, endsAfterFrameMs: 50);
        await using var session = new PtySession(backend);
        await backend.Reading.WaitAsync(Timeout);
        var completion = session.CompleteAsync(new PtyCompleteOptions
        {
            DecodeOutput = false,
            OutputReaderCloseTimeout = TimeSpan.FromMilliseconds(450),
        });

        backend.Exit(0);

        var result = await completion.WaitAsync(Timeout);
        var hasFrame = result.Contains(SlowClosingHostBackend.Frame);
        await Assert.That(hasFrame).IsTrue();
    }

    /// <summary>
    /// A close that returns after <see cref="PtyOutputPump.CloseReturnWaitMs"/> does not move the point it already counted
    /// as done. Output keeps arriving here, so only the close timeout can end the completion. Counted from the end of the
    /// wait it fires about 3 s after the close starts, and counted from the late return about 4.5 s.
    /// </summary>
    /// <remarks>Paced against the quiet period, so it runs alone (see <see cref="PacedQuietMs"/>).</remarks>
    [Test]
    [NotInParallel]
    public async Task ACloseThatReturnsAfterTheWaitDoesNotPostponeTheCloseTimeout()
    {
        const int CloseTimeoutMs = 2000;
        // The close returns after the wait, and before a timeout counted from the wait's end would fire.
        const int ReturnMs = PtyOutputPump.CloseReturnWaitMs + 1500;
        var backend = new TrickleAfterCloseBackend(closeReturnsAfterMs: ReturnMs);
        await using var session = new PtySession(backend);
        await backend.Reading.WaitAsync(Timeout);
        var completion = session.CompleteAsync(new PtyCompleteOptions
        {
            DecodeOutput = false,
            OutputReaderCloseTimeout = TimeSpan.FromMilliseconds(CloseTimeoutMs),
        });

        backend.Exit(0);

        await Assert.ThrowsAsync<TimeoutException>(async () => await completion.WaitAsync(Timeout));
        var afterCloseStarted = Stopwatch.GetElapsedTime(backend.CloseStartedAt).TotalMilliseconds;
        // A timeout can fire late but never early. So the upper bound sits just short of about 4.5 s, where a late return
        // would move the done point, and the lower bound just under the right answer, about 3 s. That also rules out
        // about 2.5 s, where the return would leave the done point at the request.
        await Assert.That(afterCloseStarted).IsLessThan(ReturnMs + CloseTimeoutMs - 250);
        await Assert.That(afterCloseStarted).IsGreaterThanOrEqualTo(PtyOutputPump.CloseReturnWaitMs + CloseTimeoutMs - 100);
    }

    /// <summary>
    /// Once a close that waits for its host has returned, the forced end counts its quiet from the return. The transport
    /// here stays open after the frame, as when another process holds the pipe, so only the forced end can end output.
    /// A pump that ignored the return would hold the end until <see cref="PtyOutputPump.CloseReturnWaitMs"/> after the
    /// close started, and one quiet interval more.
    /// </summary>
    /// <remarks>Timed against the wait, so it runs alone, as the tests paced against <see cref="PacedQuietMs"/> do.</remarks>
    // Retried: a runner that stalls the reader or the return thread by about 0.7 s passes the bound. A pump that ignores
    // the return fails every attempt.
    [Test]
    [NotInParallel]
    [Retry(3)]
    public async Task OutputHeldOpenEndsOneQuietIntervalAfterASlowCloseReturns()
    {
        var backend = new SlowClosingHostBackend(closeReturnsAfterMs: PtyOutputPump.DefaultPostExitQuietMs * 2, holdOpenAfterFrame: true);
        await using var session = new PtySession(backend);
        await using var enumerator = session.ReadOutputAsync().GetAsyncEnumerator();
        var next = enumerator.MoveNextAsync().AsTask();
        await backend.Reading.WaitAsync(Timeout);

        backend.Exit(0);

        using var output = new MemoryStream();
        while (await next.WaitAsync(Timeout))
        {
            output.Write(enumerator.Current.Data.Span);
            next = enumerator.MoveNextAsync().AsTask();
        }

        var endedAt = Stopwatch.GetTimestamp();
        var hasFrame = output.ToArray().AsSpan().SequenceEqual(SlowClosingHostBackend.Frame);
        await Assert.That(hasFrame).IsTrue();
        // The end is measured from the close's start, which does not depend on when the return thread runs. It comes about
        // 300 ms after that start when the return counts, and the whole wait or later when it does not.
        var endedAfterCloseStarted = Stopwatch.GetElapsedTime(backend.CloseStartedAt, endedAt);
        await Assert.That(endedAfterCloseStarted.TotalMilliseconds).IsLessThan(PtyOutputPump.CloseReturnWaitMs);
    }

    /// <summary>
    /// Output still draining out of the transport after the close is not quiet. Each read restarts the interval, so
    /// a slow final flush is delivered in full before the pump ends output on its own; measured from the close alone,
    /// the forced end would cut it short.
    /// </summary>
    /// <remarks>
    /// Paced against the quiet period, so it runs alone (see <see cref="PacedQuietMs"/>). The flush lasts one and a half
    /// quiet periods: with 8 reads it ended before one, and the test passed with the forced end measured from the close.
    /// </remarks>
    [Test]
    [NotInParallel]
    public async Task OutputStillDrainingAfterTheCloseIsDelivered()
    {
        const int FlushReads = 15;
        var backend = new SlowFlushAfterCloseBackend(FlushReads, intervalMs: PacedQuietMs / 10);
        await using var session = new PtySession(backend);
        await using var enumerator = session.ReadOutputAsync().GetAsyncEnumerator();
        var next = enumerator.MoveNextAsync().AsTask();
        await backend.Reading.WaitAsync(Timeout);

        backend.Exit(0);

        var bytes = 0;
        if (await next.WaitAsync(Timeout))
        {
            bytes += enumerator.Current.Data.Length;
            while (await enumerator.MoveNextAsync().AsTask().WaitAsync(Timeout))
                bytes += enumerator.Current.Data.Length;
        }

        await Assert.That(bytes).IsEqualTo(FlushReads);
    }

    /// <summary>
    /// The transport may still hold output when the post-exit close finds the ring full. The pump must park until
    /// the consumer frees space and then read on, not end output with that output unread. The flush is made of full-size
    /// reads, so that it fills the ring.
    /// </summary>
    [Test]
    public async Task FullRingAfterTheCloseDoesNotEndOutput()
    {
        const int FlushReads = 8;
        var backend = new SlowFlushAfterCloseBackend(FlushReads, intervalMs: 0, readSize: PtyOutputPump.PublishSize);
        await using var session = new PtySession(backend);
        await using var enumerator = session.ReadOutputAsync().GetAsyncEnumerator();
        var next = enumerator.MoveNextAsync().AsTask();
        await backend.Reading.WaitAsync(Timeout);

        backend.Exit(0);
        // The flush fills the ring while the consumer holds its first chunk; the reader is parked, not finished.
        await Assert.That(await next.WaitAsync(Timeout)).IsTrue();
        await backend.OutputClosed.WaitAsync(Timeout);
        await Task.Delay(PacedQuietMs + 200);
        await Assert.That(session.OutputPump.IsProducerFinished).IsFalse();

        var bytes = enumerator.Current.Data.Length;
        while (await enumerator.MoveNextAsync().AsTask().WaitAsync(Timeout))
            bytes += enumerator.Current.Data.Length;

        await Assert.That(bytes).IsEqualTo(FlushReads * PtyOutputPump.PublishSize);
    }

    /// <summary>
    /// A ring that fills after the close is not end of output. When the frame ConPTY flushes after the close fills the
    /// ring while nobody reads, the reader parks with output still in the transport; a reader that starts only then
    /// still gets every frame, and then end of output. The frames here are full-size reads, so that the ring fills.
    /// </summary>
    [Test]
    public async Task FramesThatFillTheRingAfterTheCloseReachALateReader()
    {
        const int BeforeClose = ReadsThatFillTheRing - 1;
        const int AfterClose = 2;
        var backend = new FramesAroundTheCloseBackend(BeforeClose, AfterClose, PtyOutputPump.PublishSize);
        await using var session = new PtySession(backend);
        await backend.WaitingForTheClose.WaitAsync(Timeout);

        backend.Exit(0);
        await backend.OutputClosed.WaitAsync(Timeout);
        await Assert.That(await WaitUntilAsync(() => session.OutputPump.IsWaitingForFreeSpace)).IsTrue();
        // Past the quiet period after the close: a parked reader is not silence, so the drain does not end output.
        await Task.Delay(PtyOutputPump.DefaultPostExitQuietMs * 3);
        await Assert.That(session.OutputPump.IsProducerFinished).IsFalse();

        var bytes = 0;
        using var cts = new CancellationTokenSource(Timeout);
        await foreach (var chunk in session.ReadOutputAsync(cts.Token))
            bytes += chunk.Data.Length;

        await Assert.That(bytes).IsEqualTo((BeforeClose + AfterClose) * PtyOutputPump.PublishSize);
    }

    /// <summary>
    /// Small frames do not fill the ring. With a ring of four slots, four separate frames of a few bytes each filled it,
    /// as ConPTY's frames for a single <c>echo</c> did under CPU contention, and the frame flushed after the close then
    /// waited for a consumer: output nobody read did not end after exit. The ring now holds bytes, so the same frames
    /// leave room, and output ends without a consumer.
    /// </summary>
    [Test]
    public async Task SmallFramesDoNotFillTheRing()
    {
        const int BeforeClose = ReadsThatFillTheRing * 2;
        const int AfterClose = 2;
        var backend = new FramesAroundTheCloseBackend(BeforeClose, AfterClose);
        await using var session = new PtySession(backend);
        await backend.WaitingForTheClose.WaitAsync(Timeout);

        backend.Exit(0);
        await Assert.That(await WaitUntilAsync(() => session.OutputPump.IsProducerFinished)).IsTrue();

        var bytes = 0;
        using var cts = new CancellationTokenSource(Timeout);
        await foreach (var chunk in session.ReadOutputAsync(cts.Token))
            bytes += chunk.Data.Length;

        await Assert.That(bytes).IsEqualTo(BeforeClose + AfterClose);
    }

    /// <summary>
    /// A consumer that fell behind takes all contiguous output at once: four full-size reads arrive as one chunk when the
    /// backend allows chunks as large as the ring, as the platform backends do. A WebSocket consumer then sends one frame
    /// where it sent four.
    /// </summary>
    [Test]
    public async Task AConsumerThatFellBehindTakesContiguousReadsAsOneChunk()
    {
        int[] reads = [PtyOutputPump.PublishSize, PtyOutputPump.PublishSize, PtyOutputPump.PublishSize, PtyOutputPump.PublishSize];
        var backend = new ScriptedReadsBackend(reads, PtyOutputPump.Capacity, endAfter: false);
        await using var session = new PtySession(backend);
        await Assert.That(await WaitUntilAsync(() => session.OutputPump.IsWaitingForFreeSpace)).IsTrue();

        await using var enumerator = session.ReadOutputAsync().GetAsyncEnumerator();
        await Assert.That(await enumerator.MoveNextAsync().AsTask().WaitAsync(Timeout)).IsTrue();
        await Assert.That(enumerator.Current.Data.Length).IsEqualTo(PtyOutputPump.Capacity);
        await Assert.That(ScriptedReadsBackend.IsSequence(enumerator.Current.Data.Span, start: 0)).IsTrue();
    }

    /// <summary>
    /// A claim on one wait never completes a later one. The output driver publishes, sees the consumer waiting, and
    /// claims the wait. If the consumer meanwhile saw the output on its own, withdrew, consumed it, and waited again, a
    /// claim that only checked that some wait was armed completed the new wait with nothing to read, and the enumeration
    /// failed with "signaled without a state change": a 32 MiB flood through the WebSocket bridge lost the rest of its
    /// output in about one of ten runs on Linux. Here several sessions stream small reads to consumers that do nothing,
    /// so the consumers keep catching up and waiting again while the drivers keep signaling.
    /// </summary>
    [Test]
    public async Task ASignalClaimedForOneWaitNeverCompletesALaterOne()
    {
        var sessions = Math.Max(4, Environment.ProcessorCount / 2);
        var until = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 3;
        var runs = new Task<long>[sessions];
        for (var i = 0; i < sessions; i++)
        {
            runs[i] = Task.Run(async () =>
            {
                await using var session = new PtySession(new EndlessBackend());
                await using var enumerator = session.ReadOutputAsync().GetAsyncEnumerator();
                long chunks = 0;
                while (Stopwatch.GetTimestamp() < until && await enumerator.MoveNextAsync())
                    chunks++;

                return chunks;
            });
        }

        var counts = await Task.WhenAll(runs).WaitAsync(Timeout);
        foreach (var count in counts)
            await Assert.That(count).IsGreaterThan(0L);
    }

    /// <summary>
    /// One-shot completion that attaches after output has piled up takes that backlog as one contiguous write of
    /// several reads, and decodes it. Decoding sized its buffer for one read, so <c>CompleteAsync</c> with
    /// <see cref="PtyCompleteOptions.DecodeOutput"/> failed once more than about 4 KiB was waiting.
    /// </summary>
    [Test]
    public async Task CompleteAsyncDecodesABacklogOfSeveralReads()
    {
        int[] reads = [PtyOutputPump.PublishSize, PtyOutputPump.PublishSize, PtyOutputPump.PublishSize];
        var backend = new ScriptedReadsBackend(reads, PtyOutputPump.Capacity, endAfter: false);
        await using var session = new PtySession(backend);
        await Assert.That(await WaitUntilAsync(() => backend.HasReadAll)).IsTrue();
        // The last read is published right after it returns.
        await Task.Delay(100);

        backend.Exit(0);
        var result = await session.CompleteAsync(new PtyCompleteOptions { DecodeOutput = true }).WaitAsync(Timeout);

        await Assert.That(result.Output.Length).IsEqualTo(reads.Sum());
        await Assert.That(ScriptedReadsBackend.IsSequence(result.Output.Span, start: 0)).IsTrue();
        await Assert.That(result.GetText().Length).IsGreaterThan(0);
    }

    /// <summary>
    /// A consumer that still holds a chunk when the session is disposed releases it on its next <c>MoveNextAsync</c>,
    /// after the ring went back to the pool. The release only moves the ring's positions and never touches the buffer,
    /// so the call fails with <see cref="ObjectDisposedException"/>, as every read after disposal does.
    /// </summary>
    [Test]
    public async Task ReleasingAChunkAfterDisposalFailsAsDisposed()
    {
        int[] reads = [PtyOutputPump.PublishSize, PtyOutputPump.PublishSize];
        var backend = new ScriptedReadsBackend(reads, PtyOutputPump.Capacity, endAfter: false);
        var session = new PtySession(backend);
        var enumerator = session.ReadOutputAsync().GetAsyncEnumerator();
        await Assert.That(await enumerator.MoveNextAsync().AsTask().WaitAsync(Timeout)).IsTrue();

        await session.DisposeAsync().AsTask().WaitAsync(Timeout);

        Exception? failure = null;
        try
        {
            await enumerator.MoveNextAsync();
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        await Assert.That(failure).IsTypeOf<ObjectDisposedException>();
        await enumerator.DisposeAsync();
    }

    /// <summary>
    /// Output that wraps around the end of the ring reaches the consumer complete and in order, whether it is handed
    /// out as chunks or copied out by the <c>Output</c> stream in pieces that do not line up with the reads.
    /// </summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OutputThatWrapsAroundTheRingArrivesInOrder(bool throughStream)
    {
        // Three full reads and a short one leave less than a publish free, so the reader waits for the consumer. The
        // next read then stops at the end of the buffer, and the rest of it continues at the start.
        int[] reads = [PtyOutputPump.PublishSize, PtyOutputPump.PublishSize, PtyOutputPump.PublishSize, 100,
            PtyOutputPump.PublishSize, PtyOutputPump.PublishSize, 777, PtyOutputPump.PublishSize * 3, 1, 2, 3];
        var backend = new ScriptedReadsBackend(reads, PtyOutputPump.Capacity, endAfter: true);
        await using var session = new PtySession(backend);

        var received = new List<byte>();
        using var cts = new CancellationTokenSource(Timeout);
        if (throughStream)
        {
            var buffer = new byte[1000];
            int read;
            while ((read = await session.Output.ReadAsync(buffer, cts.Token)) > 0)
                received.AddRange(buffer.AsSpan(0, read));
        }
        else
        {
            await foreach (var chunk in session.ReadOutputAsync(cts.Token))
                received.AddRange(chunk.Data.Span);
        }

        await Assert.That(received.Count).IsEqualTo(reads.Sum());
        await Assert.That(ScriptedReadsBackend.IsSequence(received.ToArray(), start: 0)).IsTrue();
    }

    /// <summary>
    /// One-shot completion attached after output has ended takes the buffered output on the attaching thread and
    /// finishes at once. A real child cannot be relied on to reach this state without a consumer, because its frames can
    /// fill the ring first (<see cref="FramesThatFillTheRingAfterTheCloseReachALateReader"/>).
    /// </summary>
    [Test]
    public async Task CompleteAsyncAfterOutputEndedTakesTheBufferedOutput()
    {
        var backend = new FramesAroundTheCloseBackend(beforeClose: 2, afterClose: 1);
        await using var session = new PtySession(backend);
        await backend.WaitingForTheClose.WaitAsync(Timeout);

        backend.Exit(7);
        await Assert.That(await WaitUntilAsync(() => session.OutputPump.IsProducerFinished)).IsTrue();

        var result = await session.CompleteAsync(new PtyCompleteOptions { DecodeOutput = false }).WaitAsync(Timeout);

        await Assert.That(result.ExitCode).IsEqualTo(7);
        await Assert.That(result.Output.Length).IsEqualTo(3);
    }

    /// <summary>
    /// The bytes of a read stay where it put them until they are published, also when the consumer releases everything
    /// in between. The second read lands after unread output, and the release then empties the ring, which is when
    /// <see cref="PtyOutputPump.NextReadSpan"/> starts over and would show the first read's bytes instead.
    /// </summary>
    [Test]
    public async Task AReadIsPublishedAsReadAfterTheConsumerReleasedEverything()
    {
        var backend = new ManualRingBackend();
        await using var session = new PtySession(backend);
        var pump = session.OutputPump;

        pump.HoldRing();
        "AB"u8.CopyTo(pump.NextReadSpan);
        pump.Publish(2, 0);
        "CD"u8.CopyTo(pump.NextReadSpan);
        await Assert.That(pump.TryPeek(out _)).IsTrue();
        pump.Release();

        await Assert.That(pump.UnpublishedRead(2).SequenceEqual("CD"u8)).IsTrue();
        pump.Publish(2, 0);
        await Assert.That(pump.TryPeek(out var chunk)).IsTrue();
        await Assert.That(chunk.Span.SequenceEqual("CD"u8)).IsTrue();
        pump.Release();
        pump.LetGoOfRing();
        backend.End();
    }

    /// <summary>
    /// A ring with nothing unread starts its next read at its beginning, so a read never has to fit the few bytes left
    /// before the end: a Linux master hands out 4,095 bytes per read, and four of those left 4 bytes, which the fifth read
    /// took alone, a read and a publish per 16 KiB for nothing. A ring with unread output keeps its place, so the next read
    /// takes what is left before the end and the consumer's chunk stays contiguous.
    /// </summary>
    [Test]
    public async Task AnEmptyRingStartsOverAtItsBeginning()
    {
        const int LinuxRead = 4095;
        var backend = new ManualRingBackend();
        await using var session = new PtySession(backend);
        var pump = session.OutputPump;

        pump.HoldRing();
        for (var i = 0; i < 8; i++)
        {
            await Assert.That(pump.NextReadSpan.Length).IsEqualTo(PtyOutputPump.PublishSize).Because($"read {i}, {pump.UnreadCount} unread");
            pump.Publish(LinuxRead, 0);
            await Assert.That(pump.TryPeek(out var chunk)).IsTrue();
            await Assert.That(chunk.Length).IsEqualTo(LinuxRead);
            pump.Release();
        }

        // Unread output keeps the ring's place: three reads leave 4,099 bytes before the end, a full read then 3, and the
        // next span is those 3, until the consumer takes everything so far as one chunk. A driver takes the span before
        // every read, which is where an empty ring starts over.
        for (var i = 0; i < 3; i++)
        {
            await Assert.That(pump.NextReadSpan.Length).IsEqualTo(PtyOutputPump.PublishSize);
            pump.Publish(LinuxRead, 0);
        }

        await Assert.That(pump.NextReadSpan.Length).IsEqualTo(PtyOutputPump.PublishSize);
        pump.Publish(PtyOutputPump.PublishSize, 0);
        await Assert.That(pump.NextReadSpan.Length).IsEqualTo(PtyOutputPump.Capacity - 3 * LinuxRead - PtyOutputPump.PublishSize);
        await Assert.That(pump.TryPeek(out var all)).IsTrue();
        await Assert.That(all.Length).IsEqualTo(3 * LinuxRead + PtyOutputPump.PublishSize);
        pump.Release();
        await Assert.That(pump.NextReadSpan.Length).IsEqualTo(PtyOutputPump.PublishSize);
        pump.LetGoOfRing();
        backend.End();
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + (long)Timeout.TotalMilliseconds;
        while (!condition())
        {
            if (Environment.TickCount64 >= deadline)
                return false;

            await Task.Delay(10);
        }

        return true;
    }


    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>Full-size reads (<see cref="PtyOutputPump.PublishSize"/> each) that fill the ring.</summary>
    internal const int ReadsThatFillTheRing = PtyOutputPump.Capacity / PtyOutputPump.PublishSize;

    /// <summary>
    /// Quiet period of the scripted backends whose reads are paced by sleeps. The default 100 ms left a 50 ms sleep a
    /// 2× margin, which loaded hosted runners (3–4 cores running the suite in parallel) exceeded on every macOS run:
    /// a reader delayed past the period looked quiet, and the transport was closed or output ended early.
    /// </summary>
    /// <remarks>
    /// The tests that pace reads against it (a sleep of a tenth of it between reads) also run alone. Beside the parallel
    /// suite, windows-11-arm once left the reader idle past the whole period in each of them, in the same run: after two
    /// of eight flush reads, and in the reads after the stall. Neither failed in 25 runs on a desktop under six concurrent
    /// suites pinned to four cores.
    /// </remarks>
    private const int PacedQuietMs = 500;

    /// <summary>
    /// Upper bound for an end that takes two default quiet intervals (about 200 ms) with no OS event. A starved runner
    /// stretched it past the former 1.5 s bound, so it is wide: it only has to tell the forced end apart from a reader
    /// that never ends, which hangs until <see cref="Timeout"/> or the close timeout.
    /// </summary>
    private const int ForcedEndBoundMs = 5000;

    private static T RunUnder<T>(SynchronizationContext context, Func<T> action)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            return action();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private static async Task<bool> AwaitUnderCurrentContext(ValueTask<bool> pending) => await pending;

    private static async Task AwaitUnderCurrentContext(ValueTask pending) => await pending;

    private static async Task<int> AwaitUnderCurrentContext(ValueTask<int> pending) => await pending;

    /// <summary>A context whose dispatcher has shut down: every post throws. Records the first posting thread.</summary>
    private sealed class ThrowingSynchronizationContext : SynchronizationContext
    {
        private readonly TaskCompletionSource<(int ThreadId, string? ThreadName)> _posted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<(int ThreadId, string? ThreadName)> WaitForPostAsync() => _posted.Task.WaitAsync(Timeout);

        public override void Post(SendOrPostCallback d, object? state)
        {
            _posted.TrySetResult((Environment.CurrentManagedThreadId, Thread.CurrentThread.Name));
            throw new InvalidOperationException("The dispatcher has shut down.");
        }
    }

    /// <summary>
    /// Produces no output and reports end of output once <see cref="EndOutput"/> or disposal ends it; with
    /// <c>holdEndOfOutput</c> the reader also waits for <see cref="ReleaseEndOfOutput"/>.
    /// </summary>
    private sealed class SilentBackend(bool holdEndOfOutput = false) : FakeBackend
    {
        private readonly ManualResetEventSlim _ended = new();
        private readonly ManualResetEventSlim _released = new(initialState: !holdEndOfOutput);
        private readonly TaskCompletionSource _reading = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once the driver thread is inside <see cref="ReadOutput"/>.</summary>
        public Task Reading => _reading.Task;

        public void EndOutput() => _ended.Set();

        public void ReleaseEndOfOutput() => _released.Set();

        public override int ReadOutput(Span<byte> buffer, ScriptedOutputDriver driver)
        {
            _reading.TrySetResult();
            _ended.Wait();
            _released.Wait();
            return 0;
        }

        public override void Dispose()
        {
            base.Dispose();
            _ended.Set();
        }
    }

    /// <summary>
    /// Fills the ring at once, then answers each read after a tenth of the quiet period, as a transport that still
    /// holds output but produces it more slowly than the reader polls.
    /// </summary>
    private sealed class StallThenSlowBackend : FakeBackend
    {
        private int _fastReads = ReadsThatFillTheRing;

        public override int PostExitQuietMs => PacedQuietMs;

        public override int ReadOutput(Span<byte> buffer, ScriptedOutputDriver driver)
        {
            if (IsOutputClosed)
                return 0;

            if (Interlocked.Decrement(ref _fastReads) >= 0)
            {
                buffer.Fill((byte)'x');
                return buffer.Length;
            }

            Thread.Sleep(PacedQuietMs / 10);
            buffer[0] = (byte)'x';
            return 1;
        }
    }

    /// <summary>
    /// A transport that the close does not end, as a ConPTY output pipe whose write end another process inherited:
    /// reads block until woken or canceled and then report "re-check" rather than end of output, whether or not the
    /// transport was closed. Only disposal ends reads, as the disposed pipe handle does on Windows.
    /// </summary>
    private sealed class HeldOpenBackend : FakeBackend
    {
        private readonly TaskCompletionSource _reading = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once the driver thread is inside <see cref="ReadOutput"/>.</summary>
        public Task Reading => _reading.Task;

        public override int ReadOutput(Span<byte> buffer, ScriptedOutputDriver driver)
        {
            if (IsDisposed)
                return 0;

            _reading.TrySetResult();
            driver.WaitForWake(System.Threading.Timeout.Infinite);
            return -1;
        }
    }

    /// <summary>
    /// A transport that sends nothing until it is closed, then one frame and end of output, as build 20348's in-box
    /// ConPTY host does for a child that writes nothing.
    /// </summary>
    private sealed class SilentUntilTheCloseBackend(bool closeSendsHeldOutput) : FakeBackend
    {
        private readonly TaskCompletionSource _reading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // Touched only by the driver's thread.
        private bool _frameSent;
        private long _closedAt;

        /// <summary>The frame the close sends.</summary>
        public static ReadOnlySpan<byte> Frame => "frame-at-close"u8;

        /// <summary>
        /// How long after <paramref name="start"/>, a <see cref="Stopwatch"/> timestamp, the transport was closed; negative
        /// when it was never closed.
        /// </summary>
        public TimeSpan ClosedAfter(long start)
        {
            var closedAt = Volatile.Read(ref _closedAt);
            return closedAt == 0 ? TimeSpan.MinValue : Stopwatch.GetElapsedTime(start, closedAt);
        }

        public override void CloseOutputTransport()
        {
            Interlocked.CompareExchange(ref _closedAt, Stopwatch.GetTimestamp(), 0);
            base.CloseOutputTransport();
        }

        /// <summary>Completes once the driver thread is inside <see cref="ReadOutput"/>.</summary>
        public Task Reading => _reading.Task;

        public override bool CloseSendsHeldOutput => closeSendsHeldOutput;

        public override int ReadOutput(Span<byte> buffer, ScriptedOutputDriver driver)
        {
            if (IsDisposed)
                return 0;

            if (IsOutputClosed)
            {
                if (_frameSent)
                    return 0;

                _frameSent = true;
                Frame.CopyTo(buffer);
                return Frame.Length;
            }

            _reading.TrySetResult();
            driver.WaitForWake(System.Threading.Timeout.Infinite);
            return -1;
        }
    }

    /// <summary>
    /// A transport whose close waits for its host, as an in-box ConPTY's without <c>ReleasePseudoConsole</c> does. It sends
    /// nothing until the close returns, <c>closeReturnsAfterMs</c> after the close starts (never, for
    /// <see cref="System.Threading.Timeout.Infinite"/>). It then sends the frame the host wrote during the close, and ends
    /// <c>endsAfterFrameMs</c> later, or with <c>holdOpenAfterFrame</c> stays open, as when another process holds the pipe.
    /// </summary>
    private sealed class SlowClosingHostBackend(int closeReturnsAfterMs, bool holdOpenAfterFrame = false, int endsAfterFrameMs = 0) : FakeBackend
    {
        private readonly TaskCompletionSource _reading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private PtyOutputPump? _pump;
        private ScriptedOutputDriver? _driver;
        private volatile bool _returned;
        private long _closeStartedAt;
        // Touched only by the driver's thread.
        private bool _frameSent;

        /// <summary>The frame the host writes during the close.</summary>
        public static ReadOnlySpan<byte> Frame => "frame-during-the-close"u8;

        /// <summary>Completes once the driver thread is inside <see cref="ReadOutput"/>.</summary>
        public Task Reading => _reading.Task;

        /// <summary>The <see cref="Stopwatch"/> timestamp of the close's start, or 0 before it.</summary>
        public long CloseStartedAt => Volatile.Read(ref _closeStartedAt);

        public override bool CloseSendsHeldOutput => true;

        public override bool CloseWaitsForHost => true;

        public override void StartOutput(PtyOutputPump pump)
        {
            _pump = pump;
            base.StartOutput(pump);
        }

        public override void CloseOutputTransport()
        {
            Volatile.Write(ref _closeStartedAt, Stopwatch.GetTimestamp());
            base.CloseOutputTransport();
            if (closeReturnsAfterMs == System.Threading.Timeout.Infinite)
                return;

            var pump = _pump!;
            // The return comes from a thread of its own, as a legacy host's close does, not from the drain tick that
            // requested the close.
            new Thread(() =>
            {
                Thread.Sleep(closeReturnsAfterMs);
                _returned = true;
                pump.OnTransportCloseReturned();
                Volatile.Read(ref _driver)?.Wake(pump);
            })
            {
                IsBackground = true,
            }.Start();
        }

        public override int ReadOutput(Span<byte> buffer, ScriptedOutputDriver driver)
        {
            if (IsDisposed)
                return 0;

            // Stored before the check, so a return after it wakes the wait below.
            Volatile.Write(ref _driver, driver);
            if (_returned && !_frameSent)
                return SendFrame(buffer);

            // The host has exited, so the pipe breaks unless another process holds it.
            if (_returned && !holdOpenAfterFrame)
            {
                Thread.Sleep(endsAfterFrameMs);
                return 0;
            }

            _reading.TrySetResult();
            driver.WaitForWake(System.Threading.Timeout.Infinite);
            return -1;
        }

        public override int ReadOutputNow(Span<byte> buffer) => _returned && !_frameSent ? SendFrame(buffer) : 0;

        private int SendFrame(Span<byte> buffer)
        {
            _frameSent = true;
            Frame.CopyTo(buffer);
            return Frame.Length;
        }
    }

    /// <summary>
    /// A transport whose close waits for its host and returns <c>closeReturnsAfterMs</c> after it starts. It sends nothing
    /// until the close, then a byte every 20 ms until disposal, so its reads never go quiet for <see cref="PacedQuietMs"/>
    /// and the drain never forces the end.
    /// </summary>
    private sealed class TrickleAfterCloseBackend(int closeReturnsAfterMs) : FakeBackend
    {
        private readonly TaskCompletionSource _reading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private PtyOutputPump? _pump;
        private long _closeStartedAt;

        /// <summary>Completes once the driver thread is inside <see cref="ReadOutput"/>.</summary>
        public Task Reading => _reading.Task;

        /// <summary>The <see cref="Stopwatch"/> timestamp of the close's start, or 0 before it.</summary>
        public long CloseStartedAt => Volatile.Read(ref _closeStartedAt);

        public override int PostExitQuietMs => PacedQuietMs;

        public override bool CloseSendsHeldOutput => true;

        public override bool CloseWaitsForHost => true;

        public override void StartOutput(PtyOutputPump pump)
        {
            _pump = pump;
            base.StartOutput(pump);
        }

        public override void CloseOutputTransport()
        {
            Volatile.Write(ref _closeStartedAt, Stopwatch.GetTimestamp());
            base.CloseOutputTransport();
            var pump = _pump!;
            new Thread(() =>
            {
                Thread.Sleep(closeReturnsAfterMs);
                pump.OnTransportCloseReturned();
            })
            {
                IsBackground = true,
            }.Start();
        }

        public override int ReadOutput(Span<byte> buffer, ScriptedOutputDriver driver)
        {
            if (IsDisposed)
                return 0;

            if (!IsOutputClosed)
            {
                _reading.TrySetResult();
                driver.WaitForWake(System.Threading.Timeout.Infinite);
                return -1;
            }

            Thread.Sleep(20);
            buffer[0] = (byte)'.';
            return 1;
        }
    }

    /// <summary>
    /// Held open like <see cref="HeldOpenBackend"/>, but after the close it still yields one byte per read, each
    /// after <c>intervalMs</c>, for <c>flushReads</c> reads: a transport flushing its tail slowly.
    /// </summary>
    private sealed class SlowFlushAfterCloseBackend(int flushReads, int intervalMs, int readSize = 1) : FakeBackend
    {
        private readonly TaskCompletionSource _reading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _remaining = flushReads;

        public Task Reading => _reading.Task;

        /// <summary>Completes once the post-exit drain has closed the transport.</summary>
        public Task OutputClosed => _closed.Task;

        public override int PostExitQuietMs => PacedQuietMs;

        public override int ReadOutput(Span<byte> buffer, ScriptedOutputDriver driver)
        {
            if (IsDisposed)
                return 0;

            if (IsOutputClosed && _remaining > 0)
            {
                _remaining--;
                if (intervalMs > 0)
                    Thread.Sleep(intervalMs);
                buffer[..readSize].Fill((byte)'x');
                return readSize;
            }

            _reading.TrySetResult();
            driver.WaitForWake(System.Threading.Timeout.Infinite);
            return -1;
        }

        public override void CloseOutputTransport()
        {
            base.CloseOutputTransport();
            _closed.TrySetResult();
        }
    }

    /// <summary>
    /// One byte per read, without end until disposal, yielding the thread between reads so that a consumer doing nothing
    /// keeps catching up and waiting again.
    /// </summary>
    private sealed class EndlessBackend : FakeBackend
    {
        public override int ReadOutput(Span<byte> buffer, ScriptedOutputDriver driver)
        {
            if (IsDisposed)
                return 0;

            Thread.Yield();
            buffer[0] = (byte)'x';
            return 1;
        }
    }

    /// <summary>A backend whose ring the test fills itself: it is the session's output driver and never reads.</summary>
    private sealed class ManualRingBackend : FakeBackend, IPtyOutputDriver
    {
        private PtyOutputPump? _pump;
        private int _finished;

        public override int OutputChunkLimit => PtyOutputPump.Capacity;

        public override void StartOutput(PtyOutputPump pump)
        {
            _pump = pump;
            pump.AttachDriver(this);
        }

        public void End()
        {
            if (Interlocked.Exchange(ref _finished, 1) == 0)
                _pump!.Finish(null);
        }

        void IPtyOutputDriver.Wake(PtyOutputPump pump)
        {
            if (pump.IsSessionDisposed)
                End();
        }

        void IPtyOutputDriver.CancelRead(PtyOutputPump pump)
        {
            if (pump.IsSessionDisposed)
                End();
        }

        void IPtyOutputDriver.OnPumpFinished(PtyOutputPump pump)
        {
        }
    }

    /// <summary>
    /// Reads of the scripted lengths, each byte the next value of a sequence so that order can be checked. A read longer
    /// than the space the pump offers is split, and its rest is returned by the next read. Afterwards end of output, or
    /// nothing until disposal.
    /// </summary>
    private sealed class ScriptedReadsBackend(int[] reads, int chunkLimit, bool endAfter) : FakeBackend
    {
        // Read and written only by the driver thread.
        private int _read;
        private int _offset;
        private int _sequence;

        public override int OutputChunkLimit => chunkLimit;

        /// <summary>Whether every scripted read has been returned.</summary>
        public bool HasReadAll => Volatile.Read(ref _read) >= reads.Length;

        public static bool IsSequence(ReadOnlySpan<byte> data, int start)
        {
            for (var i = 0; i < data.Length; i++)
            {
                if (data[i] != ValueAt(start + i))
                    return false;
            }

            return true;
        }

        private static byte ValueAt(int index) => (byte)(index % 251);

        public override int ReadOutput(Span<byte> buffer, ScriptedOutputDriver driver)
        {
            if (IsDisposed)
                return 0;

            if (_read < reads.Length)
            {
                var length = Math.Min(reads[_read] - _offset, buffer.Length);
                for (var i = 0; i < length; i++)
                    buffer[i] = ValueAt(_sequence++);

                _offset += length;
                if (_offset == reads[_read])
                {
                    _read++;
                    _offset = 0;
                }

                return length;
            }

            if (endAfter)
                return 0;

            driver.WaitForWake(System.Threading.Timeout.Infinite);
            return -1;
        }
    }

    /// <summary>
    /// Each read its own frame of <c>frameSize</c> bytes: <c>beforeClose</c> of them at once, then nothing until the
    /// transport is closed, then <c>afterClose</c> more and end of output, as ConPTY flushes its final frame after the
    /// close and then breaks the pipe.
    /// </summary>
    private sealed class FramesAroundTheCloseBackend(int beforeClose, int afterClose, int frameSize = 1) : FakeBackend
    {
        private readonly TaskCompletionSource _waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // Read and written only by the driver thread.
        private int _beforeClose = beforeClose;
        private int _afterClose = afterClose;

        /// <summary>Completes once every frame before the close has been read and the reader waits for the close.</summary>
        public Task WaitingForTheClose => _waiting.Task;

        /// <summary>Completes once the post-exit drain has closed the transport.</summary>
        public Task OutputClosed => _closed.Task;

        public override int ReadOutput(Span<byte> buffer, ScriptedOutputDriver driver)
        {
            if (IsDisposed)
                return 0;

            if (_beforeClose > 0)
            {
                _beforeClose--;
                buffer[..frameSize].Fill((byte)'x');
                return frameSize;
            }

            if (!IsOutputClosed)
            {
                _waiting.TrySetResult();
                driver.WaitForWake(System.Threading.Timeout.Infinite);
                return -1;
            }

            if (_afterClose == 0)
                return 0;

            _afterClose--;
            buffer[..frameSize].Fill((byte)'y');
            return frameSize;
        }

        public override void CloseOutputTransport()
        {
            base.CloseOutputTransport();
            _closed.TrySetResult();
        }
    }

    /// <summary>
    /// A child that exits only through <see cref="Exit"/>. Subclasses script the output transport, and may script span
    /// writes to the input queue.
    /// </summary>
    internal abstract class FakeBackend : IPtyBackend
    {
        private readonly TaskCompletionSource<PtyExitStatus> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _closed;
        private volatile bool _disposed;
        private volatile bool _hasExited;
        private IPtyExitListener? _listener;

        public Stream Input => Stream.Null;
        public virtual PtyPendingInput? QueueInput(ReadOnlySpan<byte> bytes) => null;
        public PtyPendingInput? QueueInput(ReadOnlyMemory<byte> bytes, byte[]? ownedBuffer) => null;
        public void SetOutputPump(PtyOutputPump pump) { }
        public virtual void StartOutput(PtyOutputPump pump) => ScriptedOutputDriver.Run(pump, this);
        public int ProcessId => 0;
        public bool HasExited => _hasExited;
        public int ExitCode => 0;
        public int? ExitSignal => null;
        public PtySize Size => new(80, 24);
        public string? ActiveProcessName => null;
        public PtyInputMode? InputMode => null;
        public Task<PtyExitStatus> Exited => _exited.Task;
        public bool IsOutputClosed => _closed;
        public bool IsDisposed => _disposed;

        public virtual int OutputChunkLimit => PtyOutputPump.PublishSize;
        public virtual int PostExitQuietMs => PtyOutputPump.DefaultPostExitQuietMs;
        public virtual bool CloseSendsHeldOutput => false;
        public virtual bool CloseWaitsForHost => false;

        /// <summary>Reports child exit the way the exit watcher does: records it, then notifies the session's listener.</summary>
        public void Exit(int exitCode)
        {
            _hasExited = true;
            _exited.TrySetResult(new PtyExitStatus(exitCode, null));
            Volatile.Read(ref _listener)?.OnChildExited();
        }

        public void Resize(int columns, int rows, int pixelWidth, int pixelHeight) { }
        public void Clear() { }
        public void SendEof() { }
        public void Kill() { }
        public void Kill(PtySignal signal) { }
        public ValueTask<int> WaitForExitCodeAsync() => new(_exited.Task.ContinueWith(static t => t.Result.ExitCode, TaskScheduler.Default));
        public void SetExitListener(IPtyExitListener listener) => Volatile.Write(ref _listener, listener);

        public virtual void CloseOutputTransport() => _closed = true;

        /// <summary>
        /// The script's blocking read, on the <see cref="ScriptedOutputDriver"/> thread: the byte count, 0 at end of
        /// output, or -1 after a <see cref="ScriptedOutputDriver.WaitForWake"/>, so the driver re-checks the pump's state.
        /// Backends that bring their own driver leave it.
        /// </summary>
        public virtual int ReadOutput(Span<byte> buffer, ScriptedOutputDriver driver) => throw new NotSupportedException();

        /// <summary>
        /// What the script holds now, without waiting: the driver's only read once the post-exit drain has forced the end
        /// of output. 0 when nothing is left.
        /// </summary>
        public virtual int ReadOutputNow(Span<byte> buffer) => 0;

        public virtual void Dispose()
        {
            _disposed = true;
            _closed = true;
            // The platform backends create the exit task only when someone waits on it, so a disposal nobody waits for
            // faults nothing. This one exists from the start: mark the fault observed, or the finalizer reports it as an
            // unobserved task exception on the test host's error output, once per fake session disposed before exit.
            if (_exited.TrySetException(new ObjectDisposedException(GetType().Name)))
                _ = _exited.Task.Exception;
        }
    }
}
