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
        using var release = new ManualResetEventSlim();
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

        public override int ReadOutput(Span<byte> buffer, PtyReaderThread reader) => throw new NotSupportedException();

        public override int ReadMoreOutput(Span<byte> buffer, PtyReaderThread reader, out bool endOfOutput) =>
            throw new NotSupportedException();
    }
}
