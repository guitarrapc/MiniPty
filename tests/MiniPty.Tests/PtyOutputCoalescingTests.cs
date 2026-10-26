using System.Diagnostics;
using System.Reflection;
using MiniPty.Internal;

namespace MiniPty.Tests;

/// <summary>
/// Burst coalescing, the reading mode the bridges use (<c>.github/docs/specs/terminal.md</c>, "Output frames"). While
/// output arrives in a burst, a read waits for a larger chunk, up to a deadline. Output outside a burst, and every plain
/// read, is handed out as it is published. The test is the producer here: it publishes each read itself, so what the
/// reader holds back and what it hands out can be checked without timing a real transport.
/// </summary>
/// <remarks>
/// The deadline and the tick that serves it are process-wide, and several tests change them, so the class runs alone. A
/// test that must keep a wait open past scheduling delays raises the deadline. A test of what happens at a deadline holds
/// the tick and serves the deadline itself once it has passed, so neither a late tick nor a fast one can change the
/// outcome. Only <see cref="TheEndOfABurstGoesOutWithinTheBound"/> uses the real tick.
/// </remarks>
[NotInParallel]
public sealed class PtyOutputCoalescingTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly FieldInfo s_waiterMinimumData =
        typeof(PtyOutputPump).GetField("_waiterMinimumData", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo s_skipBytes =
        typeof(PtyCoalescingOutputReader).GetField("_skipBytes", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo s_waitDeadline =
        typeof(PtyCoalescingOutputReader).GetField("_waitDeadline", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo s_removeFromScheduler =
        typeof(PtyOutputCoalesceScheduler).GetMethod("Remove", BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>
    /// Output spaced further apart than the burst window never starts a burst, and each read is handed out at once. Five
    /// reads of 1 KiB would start one if the window did not restart.
    /// </summary>
    [Test]
    public async Task OutputOutsideABurstIsHandedOutAsItIsPublished()
    {
        using var deadline = DeadlineOverride.Long();
        var backend = new ManualOutputBackend();
        await using var session = new PtySession(backend);
        await using var output = session.ReadOutputCoalescedAsync().GetAsyncEnumerator();

        for (var i = 0; i < 5; i++)
        {
            var next = output.MoveNextAsync().AsTask();
            backend.Write(1024);
            await Assert.That(await next.WaitAsync(Timeout)).IsTrue();
            await Assert.That(output.Current.Data.Length).IsEqualTo(1024);
            await Assert.That(Reader(session).IsInBurst).IsFalse();
            await Task.Delay(5);
        }

        backend.End();
    }

    /// <summary>
    /// In a burst, a read waits until the pump holds the coalescing minimum, however many reads that takes, and takes it
    /// as one chunk. A read below the minimum does not complete the wait.
    /// </summary>
    [Test]
    public async Task InABurstAReadWaitsForTheMinimumAndTakesItAsOneChunk()
    {
        using var deadline = DeadlineOverride.Long();
        var backend = new ManualOutputBackend();
        await using var session = new PtySession(backend);
        await using var output = session.ReadOutputCoalescedAsync().GetAsyncEnumerator();

        var next = await EnterBurstAsync(backend, session, output);
        const int Read = PtyOutputCoalescing.MinimumBytes / 4;
        for (var i = 0; i < 3; i++)
        {
            backend.Write(Read);
            await Task.Delay(20);
            await Assert.That(next.IsCompleted).IsFalse();
        }

        backend.Write(Read);
        await Assert.That(await next.WaitAsync(Timeout)).IsTrue();
        await Assert.That(output.Current.Data.Length).IsEqualTo(PtyOutputCoalescing.MinimumBytes);
        backend.End();
    }

    /// <summary>
    /// The end of a burst is handed out at the deadline, below the minimum. That deadline found output, so the burst goes
    /// on: a slow stream would come next as well. The next deadline finds nothing, and the output after it, such as an
    /// echo, is handed out as it is published and ends the burst.
    /// </summary>
    [Test]
    public async Task TheEndOfABurstIsHandedOutAtTheDeadlineAndTheBurstEndsAtTheNextEmptyOne()
    {
        using var tick = new ManualTick();
        var backend = new ManualOutputBackend();
        await using var session = new PtySession(backend);
        await using var output = session.ReadOutputCoalescedAsync().GetAsyncEnumerator();

        var next = await EnterBurstAsync(backend, session, output);
        backend.Write(1024);
        await ManualTick.ServeAfterDeadlineAsync(Reader(session));
        await Assert.That(await next.WaitAsync(Timeout)).IsTrue();
        await Assert.That(output.Current.Data.Length).IsEqualTo(1024);

        next = output.MoveNextAsync().AsTask();
        await Assert.That(Reader(session).IsInBurst).IsTrue();
        await ManualTick.ServeAfterDeadlineAsync(Reader(session));
        backend.Write(10);
        await Assert.That(await next.WaitAsync(Timeout)).IsTrue();
        await Assert.That(output.Current.Data.Length).IsEqualTo(10);

        await AssertNextReadIsHandedOutAtOnceAsync(backend, session, output);
        backend.End();
    }

    /// <summary>
    /// A stream of small writes, each read on its own, stays in its burst while each deadline finds output: four chunks
    /// within the window start the burst, and each later chunk is taken at a deadline, whatever its size. 200 bytes per
    /// deadline of 1 ms or more is far below the rate that keeps a burst, so only the deadline's finding keeps it. The
    /// window is raised while the four chunks start the burst, so that scheduling delays between them cannot restart it,
    /// and back to 1 ms afterwards, since it also scales that rate.
    /// </summary>
    [Test]
    public async Task ASlowStreamStaysInItsBurstWhileEachDeadlineFindsOutput()
    {
        using var tick = new ManualTick();
        var backend = new ManualOutputBackend();
        await using var session = new PtySession(backend);
        await using var output = session.ReadOutputCoalescedAsync().GetAsyncEnumerator();

        using (new WindowOverride(Stopwatch.Frequency * 60))
        {
            for (var i = 0; i < PtyOutputCoalescing.BurstChunks; i++)
            {
                var read = output.MoveNextAsync().AsTask();
                backend.Write(100);
                await Assert.That(await read.WaitAsync(Timeout)).IsTrue();
                await Assert.That(output.Current.Data.Length).IsEqualTo(100);
            }

            // Releasing the fourth chunk starts the burst; the read it arms is the stream's first wait.
            var first = output.MoveNextAsync().AsTask();
            await Assert.That(Reader(session).IsInBurst).IsTrue();
            backend.Write(100);
            backend.Write(100);
            await ManualTick.ServeAfterDeadlineAsync(Reader(session));
            await Assert.That(await first.WaitAsync(Timeout)).IsTrue();
            await Assert.That(output.Current.Data.Length).IsEqualTo(200);
        }

        for (var i = 0; i < 3; i++)
        {
            var next = output.MoveNextAsync().AsTask();
            await Assert.That(Reader(session).IsInBurst).IsTrue();
            backend.Write(100);
            backend.Write(100);
            await Assert.That(next.IsCompleted).IsFalse();
            await ManualTick.ServeAfterDeadlineAsync(Reader(session));
            await Assert.That(await next.WaitAsync(Timeout)).IsTrue();
            await Assert.That(output.Current.Data.Length).IsEqualTo(200);
        }

        var last = output.MoveNextAsync().AsTask();
        await Assert.That(Reader(session).IsInBurst).IsTrue();
        backend.End();
        await Assert.That(await last.WaitAsync(Timeout)).IsFalse();
    }

    /// <summary>
    /// Chunks that add up to the burst size within the window start a burst, also fewer than the chunks that start one by
    /// their count. The window is raised so that scheduling delays between the chunks cannot restart it.
    /// </summary>
    [Test]
    public async Task ChunksThatAddUpToTheBurstSizeWithinTheWindowStartABurst()
    {
        using var deadline = DeadlineOverride.Long();
        using var window = new WindowOverride(Stopwatch.Frequency * 60);
        var backend = new ManualOutputBackend();
        await using var session = new PtySession(backend);
        await using var output = session.ReadOutputCoalescedAsync().GetAsyncEnumerator();

        // Fewer than BurstChunks, so the bytes alone start the burst.
        const int Chunks = 2;
        for (var i = 0; i < Chunks; i++)
        {
            var read = output.MoveNextAsync().AsTask();
            await Assert.That(Reader(session).IsInBurst).IsFalse();
            backend.Write(PtyOutputCoalescing.BurstBytes / Chunks);
            await Assert.That(await read.WaitAsync(Timeout)).IsTrue();
            await Assert.That(output.Current.Data.Length).IsEqualTo(PtyOutputCoalescing.BurstBytes / Chunks);
        }

        // Releasing the last of them completes the burst size.
        var next = output.MoveNextAsync().AsTask();
        await Assert.That(Reader(session).IsInBurst).IsTrue();
        await Assert.That(next.IsCompleted).IsFalse();
        backend.End();
        await Assert.That(await next.WaitAsync(Timeout)).IsFalse();
    }

    /// <summary>
    /// Four chunks within the window start a burst however small they are, as a stream of short lines read one by one
    /// does, and three do not. The window is raised so that scheduling delays between the chunks cannot restart it.
    /// </summary>
    [Test]
    public async Task FourChunksWithinTheWindowStartABurstAndThreeDoNot()
    {
        using var deadline = DeadlineOverride.Long();
        using var window = new WindowOverride(Stopwatch.Frequency * 60);
        var backend = new ManualOutputBackend();
        await using var session = new PtySession(backend);
        await using var output = session.ReadOutputCoalescedAsync().GetAsyncEnumerator();

        for (var i = 0; i < PtyOutputCoalescing.BurstChunks; i++)
        {
            var read = output.MoveNextAsync().AsTask();
            // Each read releases the chunk before it: after three, still no burst.
            await Assert.That(Reader(session).IsInBurst).IsFalse();
            backend.Write(10);
            await Assert.That(await read.WaitAsync(Timeout)).IsTrue();
            await Assert.That(output.Current.Data.Length).IsEqualTo(10);
        }

        // Releasing the fourth starts it.
        var next = output.MoveNextAsync().AsTask();
        await Assert.That(Reader(session).IsInBurst).IsTrue();
        await Assert.That(next.IsCompleted).IsFalse();
        backend.End();
        await Assert.That(await next.WaitAsync(Timeout)).IsFalse();
    }

    /// <summary>
    /// In a burst, a read that finds the minimum already unread takes it at once instead of waiting. The output arrives
    /// while the reader still holds its previous chunk, so the read that releases that chunk finds 8 KiB unread.
    /// </summary>
    [Test]
    public async Task InABurstAReadThatFindsTheMinimumUnreadTakesItAtOnce()
    {
        using var deadline = DeadlineOverride.Long();
        var backend = new ManualOutputBackend();
        await using var session = new PtySession(backend);
        await using var output = session.ReadOutputCoalescedAsync().GetAsyncEnumerator();
        try
        {
            var first = output.MoveNextAsync().AsTask();
            backend.Write(PtyOutputCoalescing.BurstBytes);
            await Assert.That(await first.WaitAsync(Timeout)).IsTrue();
            backend.Write(PtyOutputCoalescing.MinimumBytes / 2);
            backend.Write(PtyOutputCoalescing.MinimumBytes / 2);

            // On another thread, because a read that waited for the minimum it already has would spin until output ends.
            var read = Task.Run(() =>
            {
                var next = output.MoveNextAsync();
                return next.IsCompletedSuccessfully && next.Result;
            });
            await Assert.That(await read.WaitAsync(Timeout)).IsTrue();
            await Assert.That(Reader(session).IsInBurst).IsTrue();
            await Assert.That(output.Current.Data.Length).IsEqualTo(PtyOutputCoalescing.MinimumBytes);
        }
        finally
        {
            backend.End();
        }
    }

    /// <summary>
    /// The output that completes a wait after a deadline that found nothing does not start the next burst. It piled up over
    /// the whole wait, so its size says nothing about the rate. Counted, a chunk of 4 KiB that piled up over a wait of more
    /// than 2 ms, as after a pause, would start a burst again.
    /// </summary>
    [Test]
    public async Task OutputAfterAnEmptyDeadlineDoesNotStartTheNextBurst()
    {
        // The deadline is three windows long, so a burst's worth that comes after it came at no more than 4 KiB per 3 ms,
        // below the 2 KiB per millisecond that keeps a burst.
        using var tick = new ManualTick(deadlineMs: 3);
        var backend = new ManualOutputBackend();
        await using var session = new PtySession(backend);
        await using var output = session.ReadOutputCoalescedAsync().GetAsyncEnumerator();

        var next = await EnterBurstAsync(backend, session, output);
        await ManualTick.ServeAfterDeadlineAsync(Reader(session));
        backend.Write(PtyOutputCoalescing.BurstBytes);
        await Assert.That(await next.WaitAsync(Timeout)).IsTrue();
        await Assert.That(output.Current.Data.Length).IsEqualTo(PtyOutputCoalescing.BurstBytes);

        await AssertNextReadIsHandedOutAtOnceAsync(backend, session, output);
        backend.End();
    }

    /// <summary>
    /// The output a late wait leaves unread does not start the next burst when it wraps the ring's end either, where it
    /// comes out as two chunks. The burst starts at the ring's start, and an in-time chunk of 10 KiB leaves the head 2 KiB
    /// before the end. No tick serves the next deadline, and the minimum comes long after it, in 8 KiB that wrap.
    /// </summary>
    [Test]
    public async Task OutputALateWaitLeavesDoesNotStartTheNextBurstWhenItWrapsTheRing()
    {
        // Long enough that the in-time chunk beats its deadline on a loaded machine.
        using var tick = new ManualTick(deadlineMs: 200);
        var backend = new ManualOutputBackend();
        await using var session = new PtySession(backend);
        await using var output = session.ReadOutputCoalescedAsync().GetAsyncEnumerator();

        var next = await EnterBurstAsync(backend, session, output);
        backend.Write(2048);
        backend.Write(2048);
        backend.Write(2048);
        backend.Write(4096);
        await Assert.That(await next.WaitAsync(Timeout)).IsTrue();
        await Assert.That(output.Current.Data.Length).IsEqualTo(10240);

        next = output.MoveNextAsync().AsTask();
        await Assert.That(Reader(session).IsInBurst).IsTrue();
        await WriteMinimumLateAsync(backend, next);
        await Assert.That(await next.WaitAsync(Timeout)).IsTrue();
        await Assert.That(output.Current.Data.Length).IsEqualTo(2048);
        await Assert.That(await output.MoveNextAsync()).IsTrue();
        await Assert.That(output.Current.Data.Length).IsEqualTo(6144);

        await AssertNextReadIsHandedOutAtOnceAsync(backend, session, output);
        backend.End();
    }

    /// <summary>
    /// Output that comes after a late wait's leftovers counts toward the next burst, also in the chunk that holds the last of
    /// them. The leftovers wrap the ring as in the test above, and 4 KiB more arrives before the remainder is taken, so one
    /// chunk holds 6 KiB to skip and 4 KiB to count.
    /// </summary>
    [Test]
    public async Task OutputAfterTheLeftoversOfALateWaitCountsTowardTheNextBurst()
    {
        // Long enough that the in-time chunk beats its deadline on a loaded machine.
        using var tick = new ManualTick(deadlineMs: 200);
        var backend = new ManualOutputBackend();
        await using var session = new PtySession(backend);
        await using var output = session.ReadOutputCoalescedAsync().GetAsyncEnumerator();

        var next = await EnterBurstAsync(backend, session, output);
        backend.Write(2048);
        backend.Write(2048);
        backend.Write(2048);
        backend.Write(4096);
        await Assert.That(await next.WaitAsync(Timeout)).IsTrue();

        next = output.MoveNextAsync().AsTask();
        await WriteMinimumLateAsync(backend, next);
        await Assert.That(await next.WaitAsync(Timeout)).IsTrue();
        await Assert.That(output.Current.Data.Length).IsEqualTo(2048);

        backend.Write(4096);
        await Assert.That(await output.MoveNextAsync()).IsTrue();
        await Assert.That(output.Current.Data.Length).IsEqualTo(10240);
        await Assert.That(Reader(session).IsInBurst).IsFalse();

        // Releasing it counts the 4 KiB that came after the leftovers, a burst's worth.
        next = output.MoveNextAsync().AsTask();
        await Assert.That(Reader(session).IsInBurst).IsTrue();
        await Assert.That(next.IsCompleted).IsFalse();
        backend.End();
        await Assert.That(await next.WaitAsync(Timeout)).IsFalse();
    }

    /// <summary>
    /// A new enumeration counts its first output toward a burst, whatever the previous one left to skip. Canceling a wait
    /// ends the burst with 3 KiB unread, which the next enumeration takes first, so with the window raised that 3 KiB and 1
    /// KiB more start a burst.
    /// </summary>
    [Test]
    public async Task ANewEnumerationCountsWhatACanceledOneLeftUnread()
    {
        using var deadline = DeadlineOverride.Long();
        using var window = new WindowOverride(Stopwatch.Frequency * 60);
        var backend = new ManualOutputBackend();
        await using var session = new PtySession(backend);
        using var cancel = new CancellationTokenSource();

        await using (var output = session.ReadOutputCoalescedAsync(cancel.Token).GetAsyncEnumerator())
        {
            var next = await EnterBurstAsync(backend, session, output);
            backend.Write(3072);
            cancel.Cancel();
            await Assert.That(async () => await next.WaitAsync(Timeout)).Throws<OperationCanceledException>();
            await Assert.That((int)s_skipBytes.GetValue(Reader(session))!).IsEqualTo(3072);
        }

        await using var again = session.ReadOutputCoalescedAsync().GetAsyncEnumerator();
        await Assert.That(await again.MoveNextAsync()).IsTrue();
        await Assert.That(again.Current.Data.Length).IsEqualTo(3072);
        var read = again.MoveNextAsync().AsTask();
        backend.Write(1024);
        await Assert.That(await read.WaitAsync(Timeout)).IsTrue();

        var after = again.MoveNextAsync().AsTask();
        await Assert.That(Reader(session).IsInBurst).IsTrue();
        await Assert.That(after.IsCompleted).IsFalse();
        backend.End();
        await Assert.That(await after.WaitAsync(Timeout)).IsFalse();
    }

    /// <summary>
    /// A wait that output completes long after its deadline ends the burst however long the wait was. The test moves the
    /// wait's start back 2^52 ticks, about 52 days at the 1 GHz timestamp of macOS and Linux, past which the time multiplied
    /// by the rate that keeps a burst no longer fits in 64 bits.
    /// </summary>
    [Test]
    public async Task AWaitThatOutlastsItsDeadlineByDaysEndsTheBurst()
    {
        using var tick = new ManualTick();
        var backend = new ManualOutputBackend();
        await using var session = new PtySession(backend);
        await using var output = session.ReadOutputCoalescedAsync().GetAsyncEnumerator();

        var next = await EnterBurstAsync(backend, session, output);
        var reader = Reader(session);
        await ManualTick.ServeAfterDeadlineAsync(reader);
        s_waitDeadline.SetValue(reader, Stopwatch.GetTimestamp() - (1L << 52) - Stopwatch.Frequency);
        backend.Write(10);
        await Assert.That(await next.WaitAsync(Timeout)).IsTrue();
        await Assert.That(output.Current.Data.Length).IsEqualTo(10);

        await AssertNextReadIsHandedOutAtOnceAsync(backend, session, output);
        backend.End();
    }

    /// <summary>
    /// A wait that output completes only after its deadline, far slower than a burst, ends the burst, also when no tick has
    /// served the deadline yet, as where timers are coarse. Otherwise a stream slower than a burst would stay in one,
    /// each of its chunks held until the minimum.
    /// </summary>
    [Test]
    public async Task AWaitThatOutlastsItsDeadlineEndsTheBurstWhenTheTickIsLate()
    {
        using var tick = new ManualTick();
        var backend = new ManualOutputBackend();
        await using var session = new PtySession(backend);
        await using var output = session.ReadOutputCoalescedAsync().GetAsyncEnumerator();

        var next = await EnterBurstAsync(backend, session, output);
        await Task.Delay(20);
        // No tick serves the deadline, so the minimum completes the wait about 20 ms after it was armed, long past its 1 ms
        // deadline and at a tenth of a burst's rate.
        backend.Write(PtyOutputCoalescing.MinimumBytes / 2);
        backend.Write(PtyOutputCoalescing.MinimumBytes / 2);
        await Assert.That(await next.WaitAsync(Timeout)).IsTrue();
        await Assert.That(output.Current.Data.Length).IsEqualTo(PtyOutputCoalescing.MinimumBytes);

        await AssertNextReadIsHandedOutAtOnceAsync(backend, session, output);
        backend.End();
    }

    /// <summary>
    /// A wait that output completed only after its deadline, which no tick served, keeps the burst while what is unread when
    /// the consumer resumes amounts to at least 2 KiB per burst window since the read asked, half the rate that starts a
    /// burst: a flood whose deadline a late tick missed stays in it. The window and the deadline are both 200 ms here. The
    /// test writes the 8 KiB minimum 450 ms into the wait, 3.6 KiB per window: at least the rate that keeps a burst unless
    /// the consumer resumes 350 ms later still, and less than the 4 KiB per window that starts one, so a rule that kept the
    /// burst only at that rate would end it.
    /// </summary>
    [Test]
    public async Task ALateWaitKeepsTheBurstWhileOutputCameAtTheRateThatKeepsIt()
    {
        using var tick = new ManualTick(deadlineMs: 200);
        using var window = new WindowOverride(Stopwatch.Frequency / 5);
        var backend = new ManualOutputBackend();
        await using var session = new PtySession(backend);
        await using var output = session.ReadOutputCoalescedAsync().GetAsyncEnumerator();

        var next = await EnterBurstAsync(backend, session, output);
        await Task.Delay(450);
        backend.Write(PtyOutputCoalescing.MinimumBytes / 2);
        backend.Write(PtyOutputCoalescing.MinimumBytes / 2);
        await Assert.That(await next.WaitAsync(Timeout)).IsTrue();
        await Assert.That(output.Current.Data.Length).IsEqualTo(PtyOutputCoalescing.MinimumBytes);

        next = output.MoveNextAsync().AsTask();
        await Assert.That(Reader(session).IsInBurst).IsTrue();
        await Assert.That(Volatile.Read(ref Reader(session).Deadline)).IsNotEqualTo(0L);
        await Assert.That(next.IsCompleted).IsFalse();
        backend.End();
        await Assert.That(await next.WaitAsync(Timeout)).IsFalse();
    }

    /// <summary>
    /// The scheduler keeps a reader whose deadline it served for one more tick, so the next wait of its burst, which its
    /// consumer arms on another thread soon after, finds the reader held and skips the scheduler's lock. A tick that then
    /// finds no deadline lets the reader go.
    /// </summary>
    [Test]
    public async Task AServedDeadlineKeepsTheReaderForTheNextWait()
    {
        using var tick = new ManualTick();
        var backend = new ManualOutputBackend();
        await using var session = new PtySession(backend);
        await using var output = session.ReadOutputCoalescedAsync().GetAsyncEnumerator();

        var next = await EnterBurstAsync(backend, session, output);
        var reader = Reader(session);
        var deadline = Volatile.Read(ref reader.Deadline);
        while (Stopwatch.GetTimestamp() <= deadline)
            await Task.Delay(1);

        await Assert.That(reader.Expire(Stopwatch.GetTimestamp())).IsFalse();
        await Assert.That(Volatile.Read(ref reader.Deadline)).IsEqualTo(0L);
        await Assert.That(reader.Expire(Stopwatch.GetTimestamp())).IsTrue();
        backend.End();
        await Assert.That(await next.WaitAsync(Timeout)).IsFalse();
    }

    /// <summary>
    /// A wait that output completed before its deadline keeps the burst, also when the deadline is served before the
    /// consumer resumes: what counts is when the minimum arrived, not when the consumer got to it.
    /// </summary>
    [Test]
    public async Task AWaitCompletedBeforeItsDeadlineKeepsTheBurstWhenTheConsumerResumesLate()
    {
        // Long enough that the writes below always land before it, however the test thread is scheduled.
        using var tick = new ManualTick(deadlineMs: 200);
        var backend = new ManualOutputBackend();
        await using var session = new PtySession(backend);
        await using var output = session.ReadOutputCoalescedAsync().GetAsyncEnumerator();

        var first = output.MoveNextAsync().AsTask();
        backend.Write(PtyOutputCoalescing.BurstBytes);
        await Assert.That(await first.WaitAsync(Timeout)).IsTrue();
        // Not awaited yet, so the consumer resumes only after the deadline has passed and been served.
        var next = output.MoveNextAsync();
        await Assert.That(next.IsCompleted).IsFalse();
        backend.Write(PtyOutputCoalescing.MinimumBytes / 2);
        backend.Write(PtyOutputCoalescing.MinimumBytes / 2);
        await ManualTick.ServeAfterDeadlineAsync(Reader(session));

        await Assert.That(await next).IsTrue();
        await Assert.That(output.Current.Data.Length).IsEqualTo(PtyOutputCoalescing.MinimumBytes);
        await Assert.That(Reader(session).IsInBurst).IsTrue();
        backend.End();
    }

    /// <summary>
    /// The deadline completes its wait even when the wait's minimum is already down to 1 byte with output unread. A stale
    /// deadline never leaves a wait in that state: the arming re-check makes the read arm again at its own minimum, and
    /// once the wait is armed, the stale deadline's own signal or the next publish completes it. So the test sets the
    /// minimum itself. The deadline still completes such a wait, whatever lowered its minimum.
    /// </summary>
    [Test]
    public async Task TheDeadlineCompletesAWaitWhoseMinimumIsAlreadyOneByte()
    {
        using var tick = new ManualTick();
        var backend = new ManualOutputBackend();
        await using var session = new PtySession(backend);
        await using var output = session.ReadOutputCoalescedAsync().GetAsyncEnumerator();

        var next = await EnterBurstAsync(backend, session, output);
        backend.Write(3072);
        s_waiterMinimumData.SetValue(session.OutputPump, (short)1);
        await Task.Delay(20);
        await Assert.That(next.IsCompleted).IsFalse();

        await ManualTick.ServeAfterDeadlineAsync(Reader(session));
        await Assert.That(await next.WaitAsync(Timeout)).IsTrue();
        await Assert.That(output.Current.Data.Length).IsEqualTo(3072);
        backend.End();
    }

    /// <summary>
    /// A deadline that outlived its wait and lowers the next wait's minimum while that wait is being armed does not leave
    /// the wait at 1 byte with output unread. The arming re-check sees the lowered minimum, and the read arms again at its
    /// own. The test plays that deadline from inside the arming, the only place where it can land unseen.
    /// </summary>
    [Test]
    public async Task AStaleDeadlineThatLowersAWaitBeingArmedIsSeenByTheArming()
    {
        using var deadline = DeadlineOverride.Long();
        var backend = new ManualOutputBackend();
        await using var session = new PtySession(backend);
        await using var output = session.ReadOutputCoalescedAsync().GetAsyncEnumerator();

        var first = output.MoveNextAsync().AsTask();
        backend.Write(PtyOutputCoalescing.BurstBytes);
        await Assert.That(await first.WaitAsync(Timeout)).IsTrue();
        backend.Write(3072);

        var pump = session.OutputPump;
        var played = 0;
        Task<bool> next;
        PtyOutputPump.ArmWaitHookForTests = armed =>
        {
            if (armed == pump && Interlocked.Exchange(ref played, 1) == 0)
                armed.RelaxWaitMinimum();
        };
        try
        {
            next = output.MoveNextAsync().AsTask();
        }
        finally
        {
            PtyOutputPump.ArmWaitHookForTests = null;
        }

        await Assert.That(played).IsEqualTo(1);
        await Assert.That(Reader(session).IsInBurst).IsTrue();
        await Assert.That((short)s_waiterMinimumData.GetValue(pump)!).IsEqualTo((short)PtyOutputCoalescing.MinimumBytes);
        await Assert.That(next.IsCompleted).IsFalse();
        backend.End();
        await Assert.That(await next.WaitAsync(Timeout)).IsTrue();
        await Assert.That(output.Current.Data.Length).IsEqualTo(3072);
    }

    /// <summary>
    /// The scheduler keeps a reader that armed a new wait while a tick was dropping it for having no deadline. The consumer
    /// saw the reader still held and did not schedule it again, so dropping it would leave the new deadline unserved. The
    /// test plays the tick's two steps itself, with the consumer's arming between them.
    /// </summary>
    [Test]
    public async Task TheSchedulerKeepsAReaderThatArmedAWaitWhileATickDroppedIt()
    {
        using var tick = new ManualTick(deadlineMs: 60_000);
        var backend = new ManualOutputBackend();
        await using var session = new PtySession(backend);
        await using var output = session.ReadOutputCoalescedAsync().GetAsyncEnumerator();

        var next = await EnterBurstAsync(backend, session, output);
        backend.Write(PtyOutputCoalescing.MinimumBytes / 2);
        backend.Write(PtyOutputCoalescing.MinimumBytes / 2);
        await Assert.That(await next.WaitAsync(Timeout)).IsTrue();

        var reader = Reader(session);
        // The tick finds the reader between two waits, with no deadline to serve.
        await Assert.That(reader.Expire(Stopwatch.GetTimestamp())).IsTrue();
        next = output.MoveNextAsync().AsTask();
        await Assert.That(Volatile.Read(ref reader.Deadline)).IsNotEqualTo(0L);
        s_removeFromScheduler.Invoke(null, [reader]);

        await Assert.That(Volatile.Read(ref reader.Scheduled)).IsTrue();
        await Assert.That(reader.ScheduleIndex).IsGreaterThanOrEqualTo(0);
        backend.End();
        await Assert.That(await next.WaitAsync(Timeout)).IsFalse();
    }

    /// <summary>
    /// A deadline that finds nothing unread leaves the wait for the next output, of any size: the tail of a flood, such
    /// as a shell prompt, then goes out at once instead of waiting for a burst's worth.
    /// </summary>
    [Test]
    public async Task ADeadlineWithNothingUnreadLetsTheNextOutputThrough()
    {
        using var tick = new ManualTick();
        var backend = new ManualOutputBackend();
        await using var session = new PtySession(backend);
        await using var output = session.ReadOutputCoalescedAsync().GetAsyncEnumerator();

        var next = await EnterBurstAsync(backend, session, output);
        await ManualTick.ServeAfterDeadlineAsync(Reader(session));
        await Task.Delay(20);
        await Assert.That(next.IsCompleted).IsFalse();

        backend.Write(10);
        await Assert.That(await next.WaitAsync(Timeout)).IsTrue();
        await Assert.That(output.Current.Data.Length).IsEqualTo(10);
        await Assert.That(Reader(session).IsInBurst).IsFalse();
        backend.End();
    }

    /// <summary>
    /// With the real tick, a burst's tail goes out within the bound for every reader waiting at once, round after round:
    /// the scheduler holds several readers, drops each at the first tick that finds it without a deadline, and takes it
    /// back for the next burst.
    /// The bound allows for a loaded machine and a coarse timer, and still fails a deadline 100 times too long. The tick's
    /// own resolution is <see cref="TheTickFiresAboutEveryMillisecond"/>'s.
    /// </summary>
    [Test]
    public async Task TheEndOfABurstGoesOutWithinTheBound()
    {
        const int Sessions = 6;
        const int Rounds = 3;
        var backends = new ManualOutputBackend[Sessions];
        var sessions = new PtySession[Sessions];
        var outputs = new IAsyncEnumerator<PtyOutputChunk>[Sessions];
        var latencies = new List<double>();
        try
        {
            for (var i = 0; i < Sessions; i++)
            {
                backends[i] = new ManualOutputBackend();
                sessions[i] = new PtySession(backends[i]);
                outputs[i] = sessions[i].ReadOutputCoalescedAsync().GetAsyncEnumerator();
            }

            for (var round = 0; round < Rounds; round++)
            {
                var waits = new Task<bool>[Sessions];
                for (var i = 0; i < Sessions; i++)
                    waits[i] = await EnterBurstAsync(backends[i], sessions[i], outputs[i], expectDeadline: false);

                var started = Stopwatch.GetTimestamp();
                for (var i = 0; i < Sessions; i++)
                    backends[i].Write(1024);

                for (var i = 0; i < Sessions; i++)
                {
                    await Assert.That(await waits[i].WaitAsync(Timeout)).IsTrue();
                    latencies.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    await Assert.That(outputs[i].Current.Data.Length).IsEqualTo(1024);
                }
            }
        }
        finally
        {
            for (var i = 0; i < Sessions; i++)
            {
                backends[i]?.End();
                if (outputs[i] is { } output)
                    await output.DisposeAsync();
                if (sessions[i] is { } session)
                    await session.DisposeAsync();
            }
        }

        latencies.Sort();
        var median = latencies[latencies.Count / 2];
        await Assert.That(median).IsLessThan(50.0).Because($"median {median:F2} ms, slowest {latencies[^1]:F2} ms");
    }

    /// <summary>
    /// The tick that serves deadlines fires about every millisecond while a reader waits, where .NET timers do not: on
    /// Windows they fire at the system timer's resolution of about 15.6 ms, and on Linux at the kernel's tick rate, every
    /// 4 ms under a 250 Hz kernel. With them, this test counted 14 ticks in 200 ms on Windows and 48–49 on WSL2. It allows a
    /// mean interval of 4 ms, for a loaded machine.
    /// </summary>
    [Test]
    public async Task TheTickFiresAboutEveryMillisecond()
    {
        using var deadline = DeadlineOverride.Long();
        var backend = new ManualOutputBackend();
        await using var session = new PtySession(backend);
        await using var output = session.ReadOutputCoalescedAsync().GetAsyncEnumerator();

        // A wait whose deadline no test reaches keeps the scheduler ticking.
        var next = await EnterBurstAsync(backend, session, output);
        await Assert.That(PtyOutputCoalesceScheduler.TickSourceForTests)
            .IsEqualTo(OperatingSystem.IsWindows() ? PtyCoalesceTickSource.WaitableTimer : PtyCoalesceTickSource.UnixLoop);

        var ticksBefore = PtyOutputCoalesceScheduler.TicksForTests;
        var started = Stopwatch.GetTimestamp();
        await Task.Delay(200);
        var ticks = PtyOutputCoalesceScheduler.TicksForTests - ticksBefore;
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        await Assert.That(ticks * 4.0).IsGreaterThanOrEqualTo(elapsed).Because($"{ticks} ticks in {elapsed:F0} ms");
        backend.End();
        await Assert.That(await next.WaitAsync(Timeout)).IsFalse();
    }

    /// <summary>
    /// The tick runs only while a reader waits. Once the last wait ends, the tick stops at the next tick and fires no more:
    /// on Windows its thread waits without a period, and on Unix the I/O loop's wait gets its own timeout back, so nothing
    /// wakes once per millisecond while sessions are idle. 200 ms of a tick that went on would fire about 130 times on
    /// Windows and make about 190 loop passes on Linux.
    /// </summary>
    [Test]
    public async Task TheTickStopsOnceNoReaderWaits()
    {
        using var deadline = DeadlineOverride.Long();
        var backend = new ManualOutputBackend();
        await using var session = new PtySession(backend);
        await using var output = session.ReadOutputCoalescedAsync().GetAsyncEnumerator();

        var next = await EnterBurstAsync(backend, session, output);
        await Assert.That(PtyOutputCoalesceScheduler.TickSourceForTests).IsNotEqualTo(PtyCoalesceTickSource.None);
        backend.End();
        await Assert.That(await next.WaitAsync(Timeout)).IsFalse();

        var stopBy = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5;
        while (PtyOutputCoalesceScheduler.TickSourceForTests != PtyCoalesceTickSource.None)
        {
            if (Stopwatch.GetTimestamp() > stopBy)
                throw new TimeoutException("The tick did not stop.");

            await Task.Delay(5);
        }

        var ticks = PtyOutputCoalesceScheduler.TicksForTests;
        var passes = OperatingSystem.IsWindows() ? 0 : UnixPtyBackend.LoopPassesForTests;
        await Task.Delay(200);

        // A tick already on its way when the stop landed may still run.
        await Assert.That(PtyOutputCoalesceScheduler.TicksForTests - ticks).IsLessThanOrEqualTo(1);
        // Other tests' children, polled or hung up, wake the loop every 10 ms at most.
        if (!OperatingSystem.IsWindows())
            await Assert.That(UnixPtyBackend.LoopPassesForTests - passes).IsLessThan(100);
    }

    /// <summary>End of output completes a coalescing wait at once: the rest is handed out, then the end.</summary>
    [Test]
    public async Task EndOfOutputEndsACoalescingWaitAtOnce()
    {
        using var deadline = DeadlineOverride.Long();
        var backend = new ManualOutputBackend();
        await using var session = new PtySession(backend);
        await using var output = session.ReadOutputCoalescedAsync().GetAsyncEnumerator();

        var next = await EnterBurstAsync(backend, session, output);
        backend.Write(1024);
        backend.End();

        await Assert.That(await next.WaitAsync(Timeout)).IsTrue();
        await Assert.That(output.Current.Data.Length).IsEqualTo(1024);
        await Assert.That(await output.MoveNextAsync().AsTask().WaitAsync(Timeout)).IsFalse();
    }

    /// <summary>
    /// Canceling a coalescing wait ends it and its deadline, and a later enumeration starts outside a burst and is handed
    /// output at once.
    /// </summary>
    [Test]
    public async Task CancelingACoalescingWaitClearsItsDeadline()
    {
        using var deadline = DeadlineOverride.Long();
        var backend = new ManualOutputBackend();
        await using var session = new PtySession(backend);
        using var cancel = new CancellationTokenSource();

        await using (var output = session.ReadOutputCoalescedAsync(cancel.Token).GetAsyncEnumerator())
        {
            var next = await EnterBurstAsync(backend, session, output);
            cancel.Cancel();
            await Assert.That(async () => await next.WaitAsync(Timeout)).Throws<OperationCanceledException>();
            await Assert.That(Volatile.Read(ref Reader(session).Deadline)).IsEqualTo(0L);
        }

        await using var again = session.ReadOutputCoalescedAsync().GetAsyncEnumerator();
        var read = again.MoveNextAsync().AsTask();
        backend.Write(10);
        await Assert.That(await read.WaitAsync(Timeout)).IsTrue();
        await Assert.That(again.Current.Data.Length).IsEqualTo(10);
        backend.End();
    }

    /// <summary>Disposing the session ends a coalescing wait, as it ends any read.</summary>
    [Test]
    public async Task DisposalEndsACoalescingWait()
    {
        using var deadline = DeadlineOverride.Long();
        var backend = new ManualOutputBackend();
        var session = new PtySession(backend);
        var output = session.ReadOutputCoalescedAsync().GetAsyncEnumerator();

        var next = await EnterBurstAsync(backend, session, output);
        await session.DisposeAsync().AsTask().WaitAsync(Timeout);

        await Assert.That(async () => await next.WaitAsync(Timeout)).Throws<ObjectDisposedException>();
        await output.DisposeAsync();
    }

    /// <summary>A plain read is never held for a larger chunk, even after the same burst.</summary>
    [Test]
    public async Task APlainReadIsNeverHeld()
    {
        using var deadline = DeadlineOverride.Long();
        var backend = new ManualOutputBackend();
        await using var session = new PtySession(backend);
        await using var output = session.ReadOutputAsync().GetAsyncEnumerator();

        var next = output.MoveNextAsync().AsTask();
        backend.Write(PtyOutputCoalescing.BurstBytes);
        await Assert.That(await next.WaitAsync(Timeout)).IsTrue();

        next = output.MoveNextAsync().AsTask();
        backend.Write(10);
        await Assert.That(await next.WaitAsync(Timeout)).IsTrue();
        await Assert.That(output.Current.Data.Length).IsEqualTo(10);
        // A session read plainly keeps the plain reader, which holds no coalescing state.
        await Assert.That(session.OutputReader is PtyCoalescingOutputReader).IsFalse();
        backend.End();
    }

    /// <summary>
    /// A session read plainly first still coalesces when a later enumeration asks to: the plain reader, which carries no
    /// coalescing state, has ended, and a reader that does replaces it.
    /// </summary>
    [Test]
    public async Task ACoalescingReadAfterAPlainOneCoalesces()
    {
        using var deadline = DeadlineOverride.Long();
        var backend = new ManualOutputBackend();
        await using var session = new PtySession(backend);
        await using (var plain = session.ReadOutputAsync().GetAsyncEnumerator())
        {
            var read = plain.MoveNextAsync().AsTask();
            backend.Write(10);
            await Assert.That(await read.WaitAsync(Timeout)).IsTrue();
        }

        await Assert.That(session.OutputReader is PtyCoalescingOutputReader).IsFalse();
        await using var output = session.ReadOutputCoalescedAsync().GetAsyncEnumerator();
        var next = await EnterBurstAsync(backend, session, output);
        backend.Write(PtyOutputCoalescing.MinimumBytes / 2);
        backend.Write(PtyOutputCoalescing.MinimumBytes / 2);
        await Assert.That(await next.WaitAsync(Timeout)).IsTrue();
        await Assert.That(output.Current.Data.Length).IsEqualTo(PtyOutputCoalescing.MinimumBytes);
        backend.End();
    }

    /// <summary>
    /// Takes a chunk of the burst size, and returns the next read, which must then be a coalescing wait: the reader is in
    /// a burst, nothing is unread, and, unless the real tick may already have served it, the wait has a deadline.
    /// </summary>
    private static async Task<Task<bool>> EnterBurstAsync(
        ManualOutputBackend backend,
        PtySession session,
        IAsyncEnumerator<PtyOutputChunk> output,
        bool expectDeadline = true)
    {
        var first = output.MoveNextAsync().AsTask();
        backend.Write(PtyOutputCoalescing.BurstBytes);
        await Assert.That(await first.WaitAsync(Timeout)).IsTrue();
        await Assert.That(output.Current.Data.Length).IsEqualTo(PtyOutputCoalescing.BurstBytes);

        var next = output.MoveNextAsync().AsTask();
        await Assert.That(Reader(session).IsInBurst).IsTrue();
        if (expectDeadline)
            await Assert.That(Volatile.Read(ref Reader(session).Deadline)).IsNotEqualTo(0L);

        await Assert.That(next.IsCompleted).IsFalse();
        return next;
    }

    /// <summary>
    /// Completes <paramref name="next"/>, a wait of a burst whose 200 ms deadline no tick serves, late: past the deadline it
    /// writes the minimum as 2 KiB to the ring's end, 4 KiB, and 2 KiB. 8 KiB in 250 ms is far below the rate that keeps a
    /// burst, and the deadline found nothing, so the burst ends with all of it left to skip.
    /// </summary>
    private static async Task WriteMinimumLateAsync(ManualOutputBackend backend, Task<bool> next)
    {
        await Task.Delay(250);
        backend.Write(2048);
        backend.Write(4096);
        await Assert.That(next.IsCompleted).IsFalse();
        backend.Write(2048);
    }

    /// <summary>The burst has ended: the next read waits plainly and gets the next output as it is published.</summary>
    private static async Task AssertNextReadIsHandedOutAtOnceAsync(
        ManualOutputBackend backend,
        PtySession session,
        IAsyncEnumerator<PtyOutputChunk> output)
    {
        var next = output.MoveNextAsync().AsTask();
        await Assert.That(Reader(session).IsInBurst).IsFalse();
        await Assert.That(Volatile.Read(ref Reader(session).Deadline)).IsEqualTo(0L);
        backend.Write(10);
        await Assert.That(await next.WaitAsync(Timeout)).IsTrue();
        await Assert.That(output.Current.Data.Length).IsEqualTo(10);
    }

    private static PtyCoalescingOutputReader Reader(PtySession session) => (PtyCoalescingOutputReader)session.OutputReader!;

    /// <summary>Sets the coalescing deadline until disposed.</summary>
    private sealed class DeadlineOverride : IDisposable
    {
        private readonly long _previous = PtyOutputCoalescing.MaxDelayTicks;

        public DeadlineOverride(long ticks) => PtyOutputCoalescing.MaxDelayTicks = ticks;

        /// <summary>A deadline no test reaches, so a wait outlasts any scheduling delay.</summary>
        public static DeadlineOverride Long() => new(Stopwatch.Frequency * 60);

        public void Dispose() => PtyOutputCoalescing.MaxDelayTicks = _previous;
    }

    /// <summary>Sets the window a burst's bytes are counted over until disposed.</summary>
    private sealed class WindowOverride : IDisposable
    {
        private readonly long _previous = PtyOutputCoalescing.BurstWindowTicks;

        public WindowOverride(long ticks) => PtyOutputCoalescing.BurstWindowTicks = ticks;

        public void Dispose() => PtyOutputCoalescing.BurstWindowTicks = _previous;
    }

    /// <summary>
    /// A short deadline that no tick serves until disposed: the test serves it once it has passed, so whether the tick
    /// would have run early or late does not change what happens.
    /// </summary>
    private sealed class ManualTick : IDisposable
    {
        private readonly DeadlineOverride _deadline;

        public ManualTick(int deadlineMs = 1)
        {
            _deadline = new DeadlineOverride(Stopwatch.Frequency * deadlineMs / 1000);
            Volatile.Write(ref PtyOutputCoalesceScheduler.HoldTicksForTests, true);
        }

        /// <summary>Waits until the armed wait's deadline has passed, then serves it as a tick does.</summary>
        public static async Task ServeAfterDeadlineAsync(PtyCoalescingOutputReader reader)
        {
            var deadline = Volatile.Read(ref reader.Deadline);
            await Assert.That(deadline).IsNotEqualTo(0L);
            while (Stopwatch.GetTimestamp() <= deadline)
                await Task.Delay(1);

            reader.Expire(Stopwatch.GetTimestamp());
            await Assert.That(Volatile.Read(ref reader.Deadline)).IsEqualTo(0L);
        }

        public void Dispose()
        {
            Volatile.Write(ref PtyOutputCoalesceScheduler.HoldTicksForTests, false);
            _deadline.Dispose();
        }
    }

    /// <summary>
    /// A transport the test drives: it is the session's output driver, and each <see cref="Write"/> publishes one read the
    /// way a driver does. Disposal ends output, as a driver ends it when it sees the disposal.
    /// </summary>
    private sealed class ManualOutputBackend : PtyOutputPumpTests.FakeBackend, IPtyOutputDriver
    {
        private PtyOutputPump? _pump;
        private int _finished;

        // As the platform backends: a chunk may span the whole ring.
        public override int OutputChunkLimit => PtyOutputPump.Capacity;

        public override void StartOutput(PtyOutputPump pump)
        {
            _pump = pump;
            pump.AttachDriver(this);
        }

        /// <summary>Publishes one read of <paramref name="length"/> bytes, at most what the next read span offers.</summary>
        public void Write(int length)
        {
            var pump = _pump!;
            pump.HoldRing();
            var span = pump.NextReadSpan;
            span[..length].Fill((byte)'x');
            pump.OnOutputRead();
            pump.Publish(length, Stopwatch.GetTimestamp());
        }

        /// <summary>Ends output, as a transport does when the child's terminal closes.</summary>
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

        public override int ReadOutput(Span<byte> buffer, PtyReaderThread reader) => 0;

        public override int ReadMoreOutput(Span<byte> buffer, PtyReaderThread reader, out bool endOfOutput)
        {
            endOfOutput = false;
            return 0;
        }
    }
}
