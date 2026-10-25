using System.Diagnostics;
using MiniPty.Internal;

namespace MiniPty.Tests;

/// <summary>
/// Burst coalescing, the reading mode the bridges use (<c>.github/docs/plans/plan_thread_free_io.md</c>, Phase 7): while
/// output arrives in a burst, a read waits for a larger chunk, at most a short bound; output outside a burst, and every
/// plain read, is handed out as it is published. The test is the producer here: it publishes each read itself, so what
/// the reader holds back and what it hands out can be checked without timing a real transport.
/// </summary>
/// <remarks>
/// The tests that must keep a wait open past scheduling noise raise the deadline, which is process-wide, so the class
/// runs alone.
/// </remarks>
[NotInParallel]
public sealed class PtyOutputCoalescingTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly long LongDeadlineTicks = Stopwatch.Frequency * 60;

    /// <summary>Output spaced out like keystroke echoes never starts a burst, and each read is handed out at once.</summary>
    [Test]
    public async Task OutputOutsideABurstIsHandedOutAsItIsPublished()
    {
        using var deadline = LongDeadline();
        var backend = new ManualOutputBackend();
        await using var session = new PtySession(backend);
        await using var output = session.ReadOutputCoalescedAsync().GetAsyncEnumerator();

        for (var i = 0; i < 5; i++)
        {
            var next = output.MoveNextAsync().AsTask();
            backend.Write(100);
            await Assert.That(await next.WaitAsync(Timeout)).IsTrue();
            await Assert.That(output.Current.Data.Length).IsEqualTo(100);
            await Assert.That(session.OutputReader!.Coalescer!.IsInBurst).IsFalse();
            // Past the burst window, so the next read starts a fresh one.
            await Task.Delay(5);
        }

        backend.End();
    }

    /// <summary>
    /// In a burst, a read waits until the pump holds the coalescing minimum, however many reads that takes, and takes it
    /// as one chunk; a read below the minimum does not complete the wait.
    /// </summary>
    [Test]
    public async Task InABurstAReadWaitsForTheMinimumAndTakesItAsOneChunk()
    {
        using var deadline = LongDeadline();
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
    /// The end of a burst is handed out at the deadline, below the minimum, and that ends the burst: the next read is
    /// handed out as it is published again.
    /// </summary>
    [Test]
    public async Task TheEndOfABurstIsHandedOutAtTheDeadlineAndEndsTheBurst()
    {
        var backend = new ManualOutputBackend();
        await using var session = new PtySession(backend);
        await using var output = session.ReadOutputCoalescedAsync().GetAsyncEnumerator();

        var next = await EnterBurstAsync(backend, session, output);
        backend.Write(1024);
        await Assert.That(await next.WaitAsync(Timeout)).IsTrue();
        await Assert.That(output.Current.Data.Length).IsEqualTo(1024);

        next = output.MoveNextAsync().AsTask();
        await Assert.That(session.OutputReader!.Coalescer!.IsInBurst).IsFalse();
        await Assert.That(Volatile.Read(ref session.OutputReader!.Coalescer!.Deadline)).IsEqualTo(0L);
        backend.Write(10);
        await Assert.That(await next.WaitAsync(Timeout)).IsTrue();
        await Assert.That(output.Current.Data.Length).IsEqualTo(10);
        backend.End();
    }

    /// <summary>End of output completes a coalescing wait at once: the rest is handed out, then the end.</summary>
    [Test]
    public async Task EndOfOutputEndsACoalescingWaitAtOnce()
    {
        using var deadline = LongDeadline();
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
        using var deadline = LongDeadline();
        var backend = new ManualOutputBackend();
        await using var session = new PtySession(backend);
        using var cancel = new CancellationTokenSource();

        await using (var output = session.ReadOutputCoalescedAsync(cancel.Token).GetAsyncEnumerator())
        {
            var next = await EnterBurstAsync(backend, session, output);
            cancel.Cancel();
            await Assert.That(async () => await next.WaitAsync(Timeout)).Throws<OperationCanceledException>();
            await Assert.That(Volatile.Read(ref session.OutputReader!.Coalescer!.Deadline)).IsEqualTo(0L);
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
        using var deadline = LongDeadline();
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
        using var deadline = LongDeadline();
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
        // A session read plainly never creates coalescing state: it pays one reference for it.
        await Assert.That(session.OutputReader!.Coalescer).IsNull();
        backend.End();
    }

    /// <summary>
    /// A session read plainly first still coalesces when a later enumeration asks to: the plain reader, which carries no
    /// coalescing state, is replaced once it has ended.
    /// </summary>
    [Test]
    public async Task ACoalescingReadAfterAPlainOneCoalesces()
    {
        using var deadline = LongDeadline();
        var backend = new ManualOutputBackend();
        await using var session = new PtySession(backend);
        await using (var plain = session.ReadOutputAsync().GetAsyncEnumerator())
        {
            var read = plain.MoveNextAsync().AsTask();
            backend.Write(10);
            await Assert.That(await read.WaitAsync(Timeout)).IsTrue();
        }

        await Assert.That(session.OutputReader!.Coalescer).IsNull();
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
    /// a burst, nothing is unread, and the wait has a deadline.
    /// </summary>
    private static async Task<Task<bool>> EnterBurstAsync(ManualOutputBackend backend, PtySession session, IAsyncEnumerator<PtyOutputChunk> output)
    {
        var first = output.MoveNextAsync().AsTask();
        backend.Write(PtyOutputCoalescing.BurstBytes);
        await Assert.That(await first.WaitAsync(Timeout)).IsTrue();
        await Assert.That(output.Current.Data.Length).IsEqualTo(PtyOutputCoalescing.BurstBytes);

        var next = output.MoveNextAsync().AsTask();
        await Assert.That(session.OutputReader!.Coalescer!.IsInBurst).IsTrue();
        await Assert.That(Volatile.Read(ref session.OutputReader!.Coalescer!.Deadline)).IsNotEqualTo(0L);
        await Assert.That(next.IsCompleted).IsFalse();
        return next;
    }

    /// <summary>Raises the coalescing deadline until disposed, so a wait outlasts any scheduling delay in the test.</summary>
    private static DeadlineOverride LongDeadline() => new(LongDeadlineTicks);

    private sealed class DeadlineOverride : IDisposable
    {
        private readonly long _previous = PtyOutputCoalescing.MaxDelayTicks;

        public DeadlineOverride(long ticks) => PtyOutputCoalescing.MaxDelayTicks = ticks;

        public void Dispose() => PtyOutputCoalescing.MaxDelayTicks = _previous;
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
