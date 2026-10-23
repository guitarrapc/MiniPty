using System.Diagnostics;
using MiniPty.Internal;

namespace MiniPty.Tests;

/// <summary>
/// The synchronous-completion budget (<see cref="PtyConsumerYield"/>): a consumer on a UI context that keeps finding
/// output buffered must hand the thread back to its dispatcher once the budget is spent, and nothing else changes.
/// </summary>
/// <remarks>
/// <para>
/// A scripted backend fills the ring before the consumer starts, so every read finds output buffered and the only
/// reason a read can complete asynchronously is a yield. A test spends the budget by waiting past it on the
/// consumer's thread, which is what a slow chunk handler does.
/// </para>
/// <para>
/// Equivalence classes: budget spent or not; a <see cref="SynchronizationContext"/>, a non-default
/// <see cref="TaskScheduler"/>, or neither; a chunk or end of output next; the yield awaited with the context, with
/// <c>ConfigureAwait(false)</c>, or blocked on; the session disposed during the yield. Each is covered for
/// <c>ReadOutputAsync</c> and, where the stream differs, for <c>Output.ReadAsync</c>.
/// </para>
/// </remarks>
public sealed class PtyOutputYieldTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly long BudgetTicks = Stopwatch.Frequency * PtyConsumerYield.BudgetMs / 1000;

    [Test]
    public async Task ReadOutputAsyncYieldsToTheContextOnceTheBudgetIsSpent()
    {
        await using var session = await StartWithFullRingAsync();
        using var ui = new SingleThreadSynchronizationContext();

        var result = await ui.RunAsync(async () =>
        {
            await using var enumerator = session.ReadOutputAsync().GetAsyncEnumerator();
            await enumerator.MoveNextAsync();
            SpendBudget();

            var order = new List<string>();
            SynchronizationContext.Current!.Post(_ => order.Add("queued"), null);
            var next = enumerator.MoveNextAsync();
            var yielded = !next.IsCompleted;
            var hasChunk = await next;
            order.Add("resumed");
            return (yielded, hasChunk, order, Intact(enumerator.Current.Data.Span));
        }).WaitAsync(Timeout);

        await Assert.That(result.yielded).IsTrue();
        await Assert.That(result.hasChunk).IsTrue();
        await Assert.That(result.Item4).IsTrue();
        // The callback queued before the yield ran before the loop resumed: the dispatcher got the thread back.
        await Assert.That(string.Join(",", result.order)).IsEqualTo("queued,resumed");
    }

    [Test]
    public async Task ReadOutputAsyncCompletesSynchronouslyWhileTheBudgetLasts()
    {
        await using var session = await StartWithFullRingAsync();
        using var ui = new SingleThreadSynchronizationContext();

        var reads = await ui.RunAsync(async () =>
        {
            var completions = new List<(bool Synchronous, long Ticks)>();
            // Taken before the enumeration starts its run, so the measured time can only overstate the reader's.
            var runStart = Stopwatch.GetTimestamp();
            await using var enumerator = session.ReadOutputAsync().GetAsyncEnumerator();
            for (var i = 0; i < PtyOutputPumpTests.ReadsThatFillTheRing; i++)
            {
                var next = enumerator.MoveNextAsync();
                completions.Add((next.IsCompleted, Stopwatch.GetTimestamp() - runStart));
                await next;
            }

            return completions;
        }).WaitAsync(Timeout);

        // Every buffered chunk is handed out in place. A preempted test thread can spend the budget on its own,
        // so only a read that started inside the budget is required to have completed synchronously.
        foreach (var (synchronous, ticks) in reads)
            await Assert.That(synchronous || ticks >= BudgetTicks).IsTrue();
    }

    [Test]
    public async Task ReadOutputAsyncStartsANewRunAfterAYield()
    {
        await using var session = await StartWithFullRingAsync();
        using var ui = new SingleThreadSynchronizationContext();

        var (yielded, afterYield, ticks) = await ui.RunAsync(async () =>
        {
            await using var enumerator = session.ReadOutputAsync().GetAsyncEnumerator();
            await enumerator.MoveNextAsync();
            SpendBudget();
            var next = enumerator.MoveNextAsync();
            var yielded = !next.IsCompleted;
            await next;

            var resumedAt = Stopwatch.GetTimestamp();
            var following = enumerator.MoveNextAsync();
            var afterYield = following.IsCompleted;
            var ticks = Stopwatch.GetTimestamp() - resumedAt;
            await following;
            return (yielded, afterYield, ticks);
        }).WaitAsync(Timeout);

        await Assert.That(yielded).IsTrue();
        await Assert.That(afterYield || ticks >= BudgetTicks).IsTrue();
    }

    [Test]
    public async Task ReadOutputAsyncDoesNotYieldWithoutASchedulingContext()
    {
        await using var session = await StartWithFullRingAsync();

        var synchronous = await Task.Run(async () =>
        {
            await using var enumerator = session.ReadOutputAsync().GetAsyncEnumerator();
            var completions = new List<bool>();
            for (var i = 0; i < PtyOutputPumpTests.ReadsThatFillTheRing; i++)
            {
                var next = enumerator.MoveNextAsync();
                completions.Add(next.IsCompleted);
                await next;
                SpendBudget();
            }

            return completions;
        }).WaitAsync(Timeout);

        // On a pool thread there is no dispatcher to starve, so a spent budget changes nothing.
        foreach (var completed in synchronous)
            await Assert.That(completed).IsTrue();
    }

    [Test]
    public async Task ReadOutputAsyncYieldsUnderANonDefaultTaskScheduler()
    {
        await using var session = await StartWithFullRingAsync();
        var scheduler = new ConcurrentExclusiveSchedulerPair().ExclusiveScheduler;

        var result = await Task.Factory.StartNew(async () =>
        {
            await using var enumerator = session.ReadOutputAsync().GetAsyncEnumerator();
            await enumerator.MoveNextAsync();
            SpendBudget();

            var order = new List<string>();
            _ = Task.Factory.StartNew(() => order.Add("queued"), CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Current);
            var next = enumerator.MoveNextAsync();
            var yielded = !next.IsCompleted;
            await next;
            order.Add("resumed");
            return (yielded, order);
        }, CancellationToken.None, TaskCreationOptions.None, scheduler).Unwrap().WaitAsync(Timeout);

        await Assert.That(result.yielded).IsTrue();
        await Assert.That(string.Join(",", result.order)).IsEqualTo("queued,resumed");
    }

    [Test]
    public async Task ReadOutputAsyncDoesNotDelayEndOfOutputWithASpentBudget()
    {
        var backend = new BufferedBackend(reads: 1, endAfter: true);
        await using var session = new PtySession(backend);
        await Assert.That(await WaitUntilAsync(() => session.OutputPump.IsProducerFinished)).IsTrue();
        using var ui = new SingleThreadSynchronizationContext();

        var (completed, hasChunk) = await ui.RunAsync(async () =>
        {
            await using var enumerator = session.ReadOutputAsync().GetAsyncEnumerator();
            await enumerator.MoveNextAsync();
            SpendBudget();
            var end = enumerator.MoveNextAsync();
            var completed = end.IsCompleted;
            return (completed, await end);
        }).WaitAsync(Timeout);

        await Assert.That(completed).IsTrue();
        await Assert.That(hasChunk).IsFalse();
    }

    [Test]
    public async Task ReadOutputAsyncYieldResumesOffTheContextUnderConfigureAwaitFalse()
    {
        await using var session = await StartWithFullRingAsync();
        using var ui = new SingleThreadSynchronizationContext();

        var (yielded, uiThread, resumedThread) = await ui.RunAsync(async () =>
        {
            await using var enumerator = session.ReadOutputAsync().GetAsyncEnumerator();
            await enumerator.MoveNextAsync();
            SpendBudget();
            var uiThread = Environment.CurrentManagedThreadId;
            var next = enumerator.MoveNextAsync();
            var yielded = !next.IsCompleted;
            await next.ConfigureAwait(false);
            return (yielded, uiThread, Environment.CurrentManagedThreadId);
        }).WaitAsync(Timeout);

        // The yield never runs the continuation inline; without a captured context it goes to the thread pool.
        await Assert.That(yielded).IsTrue();
        await Assert.That(resumedThread).IsNotEqualTo(uiThread);
    }

    [Test]
    public async Task ReadOutputAsyncYieldBlockedOnCompletesWithTheChunk()
    {
        await using var session = await StartWithFullRingAsync();
        using var ui = new SingleThreadSynchronizationContext();

        var (yielded, hasChunk, intact) = await ui.RunAsync(async () =>
        {
            await using var enumerator = session.ReadOutputAsync().GetAsyncEnumerator();
            await enumerator.MoveNextAsync();
            SpendBudget();
            var next = enumerator.MoveNextAsync();
            var yielded = !next.IsCompleted;
            // Blocking on the value task instead of awaiting it, as sync-over-async code does.
            var hasChunk = next.GetAwaiter().GetResult();
            return (yielded, hasChunk, Intact(enumerator.Current.Data.Span));
        }).WaitAsync(Timeout);

        await Assert.That(yielded).IsTrue();
        await Assert.That(hasChunk).IsTrue();
        await Assert.That(intact).IsTrue();
    }

    [Test]
    public async Task ReadOutputAsyncYieldFailsWhenTheSessionIsDisposedBeforeItResumes()
    {
        var session = await StartWithFullRingAsync();
        using var ui = new SingleThreadSynchronizationContext();

        var (yielded, failure) = await ui.RunAsync(async () =>
        {
            var enumerator = session.ReadOutputAsync().GetAsyncEnumerator();
            await enumerator.MoveNextAsync();
            SpendBudget();
            var next = enumerator.MoveNextAsync();
            var yielded = !next.IsCompleted;
            session.Dispose();
            try
            {
                await next;
                return (yielded, (Exception?)null);
            }
            catch (Exception ex)
            {
                return (yielded, ex);
            }
        }).WaitAsync(Timeout);

        await Assert.That(yielded).IsTrue();
        await Assert.That(failure).IsTypeOf<ObjectDisposedException>();
    }

    [Test]
    public async Task OutputReadAsyncYieldsToTheContextWithTheCopiedBytes()
    {
        await using var session = await StartWithFullRingAsync();
        using var ui = new SingleThreadSynchronizationContext();

        var result = await ui.RunAsync(async () =>
        {
            var buffer = new byte[PtyOutputPump.PublishSize];
            await TakeFirstSlotAsync(session.Output, buffer);
            SpendBudget();

            Array.Clear(buffer);
            var order = new List<string>();
            SynchronizationContext.Current!.Post(_ => order.Add("queued"), null);
            var next = session.Output.ReadAsync(buffer);
            var yielded = !next.IsCompleted;
            var read = await next;
            order.Add("resumed");
            return (yielded, read, order, Intact(buffer.AsSpan(0, read)));
        }).WaitAsync(Timeout);

        await Assert.That(result.yielded).IsTrue();
        await Assert.That(result.read).IsEqualTo(PtyOutputPump.PublishSize);
        await Assert.That(result.Item4).IsTrue();
        await Assert.That(string.Join(",", result.order)).IsEqualTo("queued,resumed");
    }

    [Test]
    public async Task OutputReadAsyncDoesNotYieldWithoutASchedulingContext()
    {
        await using var session = await StartWithFullRingAsync();

        var synchronous = await Task.Run(async () =>
        {
            var buffer = new byte[PtyOutputPump.PublishSize];
            var completions = new List<bool>();
            for (var i = 0; i < PtyOutputPumpTests.ReadsThatFillTheRing; i++)
            {
                var next = session.Output.ReadAsync(buffer);
                completions.Add(next.IsCompleted);
                await next;
                SpendBudget();
            }

            return completions;
        }).WaitAsync(Timeout);

        foreach (var completed in synchronous)
            await Assert.That(completed).IsTrue();
    }

    [Test]
    public async Task OutputReadAsyncDoesNotDelayEndOfOutputWithASpentBudget()
    {
        var backend = new BufferedBackend(reads: 1, endAfter: true);
        await using var session = new PtySession(backend);
        await Assert.That(await WaitUntilAsync(() => session.OutputPump.IsProducerFinished)).IsTrue();
        using var ui = new SingleThreadSynchronizationContext();

        var (completed, read) = await ui.RunAsync(async () =>
        {
            var buffer = new byte[PtyOutputPump.PublishSize];
            await TakeFirstSlotAsync(session.Output, buffer);
            SpendBudget();
            var end = session.Output.ReadAsync(buffer);
            var completed = end.IsCompleted;
            return (completed, await end);
        }).WaitAsync(Timeout);

        await Assert.That(completed).IsTrue();
        await Assert.That(read).IsEqualTo(0);
    }

    [Test]
    public async Task OutputReadAsyncYieldBlockedOnCompletesWithTheCopiedBytes()
    {
        await using var session = await StartWithFullRingAsync();
        using var ui = new SingleThreadSynchronizationContext();

        var (yielded, read) = await ui.RunAsync(async () =>
        {
            var buffer = new byte[PtyOutputPump.PublishSize];
            await TakeFirstSlotAsync(session.Output, buffer);
            SpendBudget();
            var next = session.Output.ReadAsync(buffer);
            var yielded = !next.IsCompleted;
            return (yielded, next.GetAwaiter().GetResult());
        }).WaitAsync(Timeout);

        await Assert.That(yielded).IsTrue();
        await Assert.That(read).IsEqualTo(PtyOutputPump.PublishSize);
    }

    [Test]
    public async Task OutputReadAsyncYieldStillReportsTheCopiedBytesWhenTheSessionIsDisposed()
    {
        var session = await StartWithFullRingAsync();
        using var ui = new SingleThreadSynchronizationContext();

        var (yielded, read, intact) = await ui.RunAsync(async () =>
        {
            var buffer = new byte[PtyOutputPump.PublishSize];
            await TakeFirstSlotAsync(session.Output, buffer);
            SpendBudget();
            var next = session.Output.ReadAsync(buffer);
            var yielded = !next.IsCompleted;
            session.Dispose();
            // The bytes were copied into the caller's buffer before the disposal; only reporting them was delayed.
            var read = await next;
            return (yielded, read, Intact(buffer.AsSpan(0, read)));
        }).WaitAsync(Timeout);

        await Assert.That(yielded).IsTrue();
        await Assert.That(read).IsEqualTo(PtyOutputPump.PublishSize);
        await Assert.That(intact).IsTrue();
    }

    /// <summary>A session whose ring is full of published output, with no more to come until disposal.</summary>
    private static async Task<PtySession> StartWithFullRingAsync()
    {
        var session = new PtySession(new BufferedBackend(reads: PtyOutputPumpTests.ReadsThatFillTheRing, endAfter: false));
        if (!await WaitUntilAsync(() => session.OutputPump.IsWaitingForFreeSpace))
        {
            session.Dispose();
            throw new TimeoutException("The output ring did not fill.");
        }

        return session;
    }

    /// <summary>
    /// Takes the first slot through <paramref name="output"/>, which makes the stream the session's output consumer and
    /// starts the current thread's run, and checks that the full ring handed over a whole slot.
    /// </summary>
    /// <remarks>
    /// Throws instead of asserting: the caller runs on the test's context, and an awaited assertion could resume off it.
    /// </remarks>
    private static async Task TakeFirstSlotAsync(Stream output, byte[] buffer)
    {
        var read = await output.ReadAsync(buffer);
        if (read != PtyOutputPump.PublishSize)
            throw new InvalidOperationException($"The first read took {read} bytes instead of a whole read.");
    }

    /// <summary>Waits on the current thread until more than the budget has passed, as a slow chunk handler does.</summary>
    private static void SpendBudget()
    {
        var until = Stopwatch.GetTimestamp() + BudgetTicks + Stopwatch.Frequency * 4 / 1000;
        while (Stopwatch.GetTimestamp() < until)
            Thread.Sleep(1);
    }

    private static bool Intact(ReadOnlySpan<byte> data) =>
        data.Length > 0 && !data.ContainsAnyExcept(BufferedBackend.Fill);

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

    /// <summary>
    /// Fills a whole slot on each of its first <c>reads</c> reads; then ends output (<c>endAfter</c>) or produces
    /// nothing more until disposal.
    /// </summary>
    private sealed class BufferedBackend(int reads, bool endAfter) : PtyOutputPumpTests.FakeBackend
    {
        internal const byte Fill = (byte)'x';

        private int _reads;

        public override int ReadOutput(Span<byte> buffer, PtyReaderThread reader)
        {
            if (IsDisposed)
                return 0;

            if (_reads < reads)
            {
                _reads++;
                buffer.Fill(Fill);
                return buffer.Length;
            }

            if (endAfter)
                return 0;

            reader.WaitForWake(System.Threading.Timeout.Infinite);
            return -1;
        }

        public override int ReadMoreOutput(Span<byte> buffer, PtyReaderThread reader, out bool endOfOutput)
        {
            endOfOutput = false;
            return 0;
        }
    }
}
