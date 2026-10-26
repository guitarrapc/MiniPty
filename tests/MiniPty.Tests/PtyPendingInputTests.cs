using MiniPty.Internal;

namespace MiniPty.Tests;

/// <summary>The queued-write entry both backends share, driven through a fake queue.</summary>
public sealed class PtyPendingInputTests
{
    /// <summary>
    /// A write that completes drops its cancellation registration even when nobody awaits it. Otherwise the token
    /// keeps the entry, and through it the session, alive, and a later cancellation reaches the queue.
    /// </summary>
    [Test]
    public async Task ACompletedWriteDropsItsRegistrationWithoutBeingAwaited()
    {
        var queue = new RecordingQueue();
        using var cts = new CancellationTokenSource();
        var entry = PtyPendingInput.Rent(queue, ReadOnlyMemory<byte>.Empty, owned: null);
        _ = entry.ToValueTask(cts.Token);

        entry.Complete(error: null);
        await cts.CancelAsync();

        await Assert.That(queue.Cancellations).IsEqualTo(0);
    }

    /// <summary>
    /// A write that completes before its task is created, as an I/O completion can, also drops the registration made
    /// afterwards, because completion could not drop a registration that did not exist yet.
    /// </summary>
    [Test]
    public async Task AWriteCompletedBeforeItsTaskDropsTheLaterRegistration()
    {
        var queue = new RecordingQueue();
        using var cts = new CancellationTokenSource();
        var entry = PtyPendingInput.Rent(queue, ReadOnlyMemory<byte>.Empty, owned: null);
        entry.Complete(error: null);

        var write = entry.ToValueTask(cts.Token);
        await cts.CancelAsync();

        await Assert.That(queue.Cancellations).IsEqualTo(0);
        await write;
    }

    /// <summary>A write canceled while queued asks its queue to remove it, once.</summary>
    [Test]
    public async Task CancelingAQueuedWriteAsksTheQueueToRemoveIt()
    {
        var queue = new RecordingQueue();
        using var cts = new CancellationTokenSource();
        var entry = PtyPendingInput.Rent(queue, ReadOnlyMemory<byte>.Empty, owned: null);
        var write = entry.ToValueTask(cts.Token);

        await cts.CancelAsync();

        await Assert.That(queue.Cancellations).IsEqualTo(1);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await write);
    }

    /// <summary>
    /// A blocking stream write that has to wait is woken by the thread that completes it, so it does not need a
    /// thread-pool worker to resume. The write goes through the same path as <c>Input.Write</c>, and it completes on a
    /// dedicated thread while every worker the pool may run is busy. A probe queued behind the blockers proves that.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task ABlockingWriteResumesWithoutTheThreadPool()
    {
        var entry = PtyPendingInput.Rent(new RecordingQueue(), ReadOnlyMemory<byte>.Empty, owned: null);
        var backend = new QueuingBackend(entry);
        var writer = new Thread(() => PtyInputWrites.Write(backend, "x"u8)) { IsBackground = true };
        writer.Start();

        ThreadPool.GetMinThreads(out var minWorkers, out _);
        ThreadPool.GetMaxThreads(out var maxWorkers, out var maxIo);
        var cap = Math.Max(Environment.ProcessorCount, minWorkers);
        // Not disposed: the extra blocker can start after the test returns, and Wait on a disposed event throws on a pool
        // thread, which ends the test process.
        var release = new ManualResetEventSlim();
        var probeRan = 0;
        bool capped;
        bool resumed;
        bool poolWasBusy;
        try
        {
            capped = ThreadPool.SetMaxThreads(cap, maxIo);
            // One more blocker than the pool may run, then the probe: the probe runs only once a worker is free.
            for (var i = 0; i <= cap; i++)
                ThreadPool.UnsafeQueueUserWorkItem(static state => ((ManualResetEventSlim)state!).Wait(), release);

            ThreadPool.UnsafeQueueUserWorkItem(_ => Interlocked.Exchange(ref probeRan, 1), null);
            // Gives the blockers time to take the workers before the completion runs.
            Thread.Sleep(200);

            var completer = new Thread(() => entry.Complete(error: null)) { IsBackground = true };
            completer.Start();

            // Joined on this thread: anything awaited here would need a pool worker too.
            resumed = writer.Join(TimeSpan.FromSeconds(5));
            poolWasBusy = Volatile.Read(ref probeRan) == 0;
        }
        finally
        {
            release.Set();
            ThreadPool.SetMaxThreads(maxWorkers, maxIo);
        }

        await Assert.That(capped).IsTrue();
        await Assert.That(poolWasBusy).IsTrue();
        await Assert.That(resumed).IsTrue();
    }

    /// <summary>
    /// An entry leaves the queue from the head, the middle, or the tail, and the links of the rest stay whole. An entry
    /// that already left, alone or with the whole queue, is not removed a second time. Without that check, a late
    /// cancellation would write a detached entry's links into the live head and tail, and cut the detached chain before
    /// it is completed.
    /// </summary>
    [Test]
    public async Task QueuedEntriesLeaveFromAnyPositionOnce()
    {
        var owner = new RecordingQueue();
        var entries = new PtyPendingInput[5];
        for (var i = 0; i < entries.Length; i++)
            entries[i] = PtyPendingInput.Rent(owner, ReadOnlyMemory<byte>.Empty, owned: null);

        PtyPendingInput? head = null;
        PtyPendingInput? tail = null;
        foreach (var entry in entries)
            PtyPendingInput.Enqueue(ref head, ref tail, entry);

        var middle = PtyPendingInput.Remove(ref head, ref tail, entries[2]);
        var middleAgain = PtyPendingInput.Remove(ref head, ref tail, entries[2]);
        var last = PtyPendingInput.Remove(ref head, ref tail, entries[4]);
        var first = PtyPendingInput.Remove(ref head, ref tail, entries[0]);
        var afterRemovals = Order(head, tail, entries);

        var dequeued = PtyPendingInput.DequeueHead(ref head, ref tail);
        var dequeuedAgain = PtyPendingInput.Remove(ref head, ref tail, dequeued);
        var afterDequeue = Order(head, tail, entries);

        PtyPendingInput.Enqueue(ref head, ref tail, entries[0]);
        var chain = PtyPendingInput.DetachAll(ref head, ref tail);
        var detachedAgain = PtyPendingInput.Remove(ref head, ref tail, entries[3]);
        var chainOrder = Order(chain, null, entries);

        await Assert.That(middle && last && first).IsTrue();
        await Assert.That(middleAgain).IsFalse();
        await Assert.That(afterRemovals).IsEqualTo("1,3 tail 3");
        await Assert.That(dequeued == entries[1]).IsTrue();
        await Assert.That(dequeuedAgain).IsFalse();
        await Assert.That(afterDequeue).IsEqualTo("3 tail 3");
        await Assert.That(detachedAgain).IsFalse();
        await Assert.That(head is null && tail is null).IsTrue();
        await Assert.That(chainOrder).IsEqualTo("3,0 tail none");
    }

    /// <summary>
    /// Removal relies on each entry's link to the one before it, so every dequeue and removal must update the link of
    /// the entry behind. A stale link would make removing the new head write into an entry that already left, such as
    /// one in a chain being completed, and leave the head in place.
    /// </summary>
    [Test]
    public async Task RemovalAfterAnEarlierRemovalKeepsTheQueueWhole()
    {
        var owner = new RecordingQueue();
        var entries = new PtyPendingInput[3];
        for (var i = 0; i < entries.Length; i++)
            entries[i] = PtyPendingInput.Rent(owner, ReadOnlyMemory<byte>.Empty, owned: null);

        PtyPendingInput? head = null;
        PtyPendingInput? tail = null;
        foreach (var entry in entries)
            PtyPendingInput.Enqueue(ref head, ref tail, entry);

        // An entry stays behind the removed head, so a stale link would show as the dequeued entry's Next.
        var dequeued = PtyPendingInput.DequeueHead(ref head, ref tail);
        var newHeadRemoved = PtyPendingInput.Remove(ref head, ref tail, entries[1]);
        var afterDequeue = Order(head, tail, entries);
        var dequeuedNext = dequeued.Next;
        PtyPendingInput.Remove(ref head, ref tail, entries[2]);

        foreach (var entry in entries)
            PtyPendingInput.Enqueue(ref head, ref tail, entry);

        var middleRemoved = PtyPendingInput.Remove(ref head, ref tail, entries[1]);
        var tailRemoved = PtyPendingInput.Remove(ref head, ref tail, entries[2]);
        var afterRemovals = Order(head, tail, entries);

        await Assert.That(newHeadRemoved && middleRemoved && tailRemoved).IsTrue();
        await Assert.That(afterDequeue).IsEqualTo("2 tail 2");
        await Assert.That(dequeuedNext is null).IsTrue();
        await Assert.That(afterRemovals).IsEqualTo("0 tail 0");
    }

    /// <summary>Lists a queue by position in <paramref name="entries"/>, following <see cref="PtyPendingInput.Next"/>.</summary>
    private static string Order(PtyPendingInput? head, PtyPendingInput? tail, PtyPendingInput[] entries)
    {
        var positions = new List<int>();
        for (var entry = head; entry is not null; entry = entry.Next)
            positions.Add(Array.IndexOf(entries, entry));

        return $"{string.Join(',', positions)} tail {(tail is null ? "none" : Array.IndexOf(entries, tail))}";
    }

    private sealed class RecordingQueue : IPtyInputQueue
    {
        private int _cancellations;

        public int Cancellations => Volatile.Read(ref _cancellations);

        public void CancelInput(PtyPendingInput entry)
        {
            Interlocked.Increment(ref _cancellations);
            entry.Complete(new OperationCanceledException(entry.Token));
        }
    }

    /// <summary>A backend whose transport takes no span write, so each one waits on the given entry.</summary>
    private sealed class QueuingBackend(PtyPendingInput entry) : PtyOutputPumpTests.FakeBackend
    {
        public override PtyPendingInput? QueueInput(ReadOnlySpan<byte> bytes) => entry;
    }
}
