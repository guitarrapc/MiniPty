using System.Diagnostics;
using MiniPty.Internal;

namespace MiniPty.Tests;

/// <summary>
/// Output pump behavior that does not depend on a real transport, driven through a scripted backend.
/// </summary>
public sealed class PtyOutputPumpTests
{
    /// <summary>
    /// Coalescing must not hold a chunk back while a child keeps trickling output faster than the micro-window:
    /// each continuation read restarted the window, so a slot was published only once it was full.
    /// </summary>
    [Test]
    public async Task TrickledOutputIsNotHeldUntilTheSlotFills()
    {
        var backend = new TrickleBackend(interval: TimeSpan.FromMilliseconds(0.3));
        await using var session = new PtySession(backend);

        await using var enumerator = session.ReadOutputAsync().GetAsyncEnumerator();
        await Assert.That(await enumerator.MoveNextAsync().AsTask().WaitAsync(Timeout)).IsTrue();

        // A full slot at this rate takes over a second to fill; a bounded window publishes a few bytes.
        await Assert.That(enumerator.Current.Data.Length).IsLessThan(PtyOutputPump.SlotSize / 4);
    }

    /// <summary>
    /// The Unix backend supplies the bound the pump applies above; an unbounded value brings the held slots back.
    /// </summary>
    [Test]
    public async Task UnixBackendBoundsCoalescing()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var backend = UnixPtyBackend.Start(new PtyStartInfo { FileName = "sh", Arguments = ["-c", "exit 0"] });

        await Assert.That(backend.CoalesceBudgetTicks).IsGreaterThan(0L);
        await Assert.That(backend.CoalesceBudgetTicks).IsLessThanOrEqualTo(Stopwatch.Frequency * 5 / 1000);
    }

    /// <summary>
    /// The ConPTY backend bounds coalescing too. Unbounded, each continuation window lasted until the next system
    /// clock tick, so output trickling in faster than about 16 ms kept a slot open and the reader spinning.
    /// </summary>
    [Test]
    public async Task WindowsBackendBoundsCoalescing()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
        using var backend = WindowsPtyBackend.Start(new PtyStartInfo { FileName = cmd, Arguments = ["/c", "exit 0"] });

        await Assert.That(backend.CoalesceBudgetTicks).IsGreaterThan(0L);
        await Assert.That(backend.CoalesceBudgetTicks).IsLessThanOrEqualTo(Stopwatch.Frequency * 5 / 1000);
    }

    /// <summary>
    /// End of output completes a pending read on the reader thread, which posts the reader's continuation to the
    /// context it captured. A context that throws from <c>Post</c> (a shut-down UI dispatcher) must not terminate
    /// the process; without the guard this test crashes the test host.
    /// </summary>
    [Test]
    public async Task PendingReadUnderThrowingContextDoesNotCrashTheReaderThread()
    {
        var backend = new SilentBackend();
        var session = new PtySession(backend);
        var enumerator = session.ReadOutputAsync().GetAsyncEnumerator();
        var context = new ThrowingSynchronizationContext();
        var pending = RunUnder(context, () => AwaitUnderCurrentContext(enumerator.MoveNextAsync()));
        await Assert.That(pending.IsCompleted).IsFalse();

        backend.EndOutput();

        await Assert.That((await context.WaitForPostAsync()).ThreadName).IsEqualTo(ReaderThreadName);
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
    /// <c>DisposeAsync</c> completes when the reader stops, on the reader thread. Awaiting it under a throwing
    /// context must not terminate the process from that thread.
    /// </summary>
    [Test]
    public async Task DisposeAsyncAwaitedUnderThrowingContextDoesNotCrashTheReaderThread()
    {
        var backend = new SilentBackend(holdEndOfOutput: true);
        var session = new PtySession(backend);
        // Disposing before the reader starts reading would end it without a read, so nothing would be held.
        await Assert.That(backend.Reading.WaitAsync(Timeout).ContinueWith(static t => t.IsCompletedSuccessfully)).IsTrue();
        var context = new ThrowingSynchronizationContext();
        var disposing = RunUnder(context, () => AwaitUnderCurrentContext(session.DisposeAsync()));
        await Assert.That(disposing.IsCompleted).IsFalse();

        backend.ReleaseEndOfOutput();

        await Assert.That((await context.WaitForPostAsync()).ThreadName).IsEqualTo(ReaderThreadName);
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
    [Test]
    public async Task ResumingAfterBackpressureDoesNotCloseTheTransport()
    {
        var backend = new StallThenSlowBackend();
        await using var session = new PtySession(backend);

        // No consumer: the ring fills and the reader parks. Exit is then observed, and the stall outlasts the quiet period.
        await Task.Delay(200);
        session.OutputPump.OnChildExited();
        await Task.Delay(300);

        // Each read after the stall takes 50 ms, below the quiet period, so the transport must stay open.
        await using var enumerator = session.ReadOutputAsync().GetAsyncEnumerator();
        for (var i = 0; i < PtyOutputPump.SlotCount + 4; i++)
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
        await Assert.That(stopwatch.ElapsedMilliseconds).IsLessThan(1500);
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
        var completion = session.CompleteAsync(new PtyCompleteOptions { OutputReaderCloseTimeout = TimeSpan.FromSeconds(3) });

        var stopwatch = Stopwatch.StartNew();
        backend.Exit(7);

        var result = await completion.WaitAsync(Timeout);
        await Assert.That(result.ExitCode).IsEqualTo(7);
        await Assert.That(stopwatch.ElapsedMilliseconds).IsLessThan(1500);
    }

    /// <summary>
    /// Output still draining out of the transport after the close is not quiet. Each read restarts the interval, so
    /// a slow final flush is delivered in full before the pump ends output on its own; measured from the close alone,
    /// the forced end would cut it short.
    /// </summary>
    [Test]
    public async Task OutputStillDrainingAfterTheCloseIsDelivered()
    {
        const int FlushReads = 8;
        var backend = new SlowFlushAfterCloseBackend(FlushReads, intervalMs: 40);
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

    private const string ReaderThreadName = "MiniPty output reader";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

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

        /// <summary>Completes once the reader thread is inside <see cref="ReadOutput"/>.</summary>
        public Task Reading => _reading.Task;

        public void EndOutput() => _ended.Set();

        public void ReleaseEndOfOutput() => _released.Set();

        public override int ReadOutput(Span<byte> buffer, PtyReaderThread reader)
        {
            _reading.TrySetResult();
            _ended.Wait();
            _released.Wait();
            return 0;
        }

        public override int ReadMoreOutput(Span<byte> buffer, PtyReaderThread reader, out bool endOfOutput)
        {
            endOfOutput = true;
            return 0;
        }

        public override void Dispose()
        {
            base.Dispose();
            _ended.Set();
        }
    }

    /// <summary>Emits one byte per read, each continuation read arriving after <c>interval</c>, until disposed.</summary>
    private sealed class TrickleBackend(TimeSpan interval) : FakeBackend
    {
        public override int ReadOutput(Span<byte> buffer, PtyReaderThread reader)
        {
            if (IsOutputClosed)
                return 0;

            buffer[0] = (byte)'x';
            return 1;
        }

        public override int ReadMoreOutput(Span<byte> buffer, PtyReaderThread reader, out bool endOfOutput)
        {
            endOfOutput = IsOutputClosed;
            if (endOfOutput)
                return 0;

            var until = Stopwatch.GetTimestamp() + (long)(interval.TotalSeconds * Stopwatch.Frequency);
            while (Stopwatch.GetTimestamp() < until)
                Thread.SpinWait(20);

            buffer[0] = (byte)'x';
            return 1;
        }
    }

    /// <summary>
    /// Fills the ring at once, then answers each read after 50 ms, as a transport that still holds output but
    /// produces it more slowly than the reader polls.
    /// </summary>
    private sealed class StallThenSlowBackend : FakeBackend
    {
        private int _fastReads = PtyOutputPump.SlotCount;

        public override int ReadOutput(Span<byte> buffer, PtyReaderThread reader)
        {
            if (IsOutputClosed)
                return 0;

            if (Interlocked.Decrement(ref _fastReads) < 0)
                Thread.Sleep(50);

            buffer[0] = (byte)'x';
            return 1;
        }

        public override int ReadMoreOutput(Span<byte> buffer, PtyReaderThread reader, out bool endOfOutput)
        {
            endOfOutput = false;
            return 0;
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

        /// <summary>Completes once the reader thread is inside <see cref="ReadOutput"/>.</summary>
        public Task Reading => _reading.Task;

        public override int ReadOutput(Span<byte> buffer, PtyReaderThread reader)
        {
            if (IsDisposed)
                return 0;

            _reading.TrySetResult();
            reader.WaitForWake(System.Threading.Timeout.Infinite);
            return -1;
        }

        public override int ReadMoreOutput(Span<byte> buffer, PtyReaderThread reader, out bool endOfOutput)
        {
            endOfOutput = false;
            return 0;
        }
    }

    /// <summary>
    /// Held open like <see cref="HeldOpenBackend"/>, but after the close it still yields one byte per read, each
    /// after <c>intervalMs</c>, for <c>flushReads</c> reads: a transport flushing its tail slowly.
    /// </summary>
    private sealed class SlowFlushAfterCloseBackend(int flushReads, int intervalMs) : FakeBackend
    {
        private readonly TaskCompletionSource _reading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _remaining = flushReads;

        public Task Reading => _reading.Task;

        public override int ReadOutput(Span<byte> buffer, PtyReaderThread reader)
        {
            if (IsDisposed)
                return 0;

            if (IsOutputClosed && _remaining > 0)
            {
                _remaining--;
                Thread.Sleep(intervalMs);
                buffer[0] = (byte)'x';
                return 1;
            }

            _reading.TrySetResult();
            reader.WaitForWake(System.Threading.Timeout.Infinite);
            return -1;
        }

        public override int ReadMoreOutput(Span<byte> buffer, PtyReaderThread reader, out bool endOfOutput)
        {
            endOfOutput = false;
            return 0;
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
        public void WaitWhileOutputBlocked(PtyReaderThread reader) => reader.WaitForWake(System.Threading.Timeout.Infinite);
        public int ProcessId => 0;
        public bool HasExited => _hasExited;
        public int ExitCode => 0;
        public int? ExitSignal => null;
        public PtySize Size => new(80, 24);
        public string? ActiveProcessName => null;
        public Task<PtyExitStatus> Exited => _exited.Task;
        public bool IsOutputClosed => _closed;
        public bool IsDisposed => _disposed;
        public long CoalesceBudgetTicks => Stopwatch.Frequency * 2 / 1000;

        /// <summary>Reports child exit the way the exit watcher does: records it, then notifies the session's listener.</summary>
        public void Exit(int exitCode)
        {
            _hasExited = true;
            _exited.TrySetResult(new PtyExitStatus(exitCode, null));
            Volatile.Read(ref _listener)?.OnChildExited();
        }

        public void Resize(int columns, int rows, int pixelWidth, int pixelHeight) { }
        public void SendEof() { }
        public void Kill() { }
        public void Kill(PtySignal signal) { }
        public ValueTask<int> WaitForExitCodeAsync() => new(_exited.Task.ContinueWith(static t => t.Result.ExitCode, TaskScheduler.Default));
        public void SetExitListener(IPtyExitListener listener) => Volatile.Write(ref _listener, listener);
        public bool TryBeginOutputRead() => !_closed;
        public void EndOutputRead() { }
        public void CloseOutputTransport() => _closed = true;

        public abstract int ReadOutput(Span<byte> buffer, PtyReaderThread reader);
        public abstract int ReadMoreOutput(Span<byte> buffer, PtyReaderThread reader, out bool endOfOutput);

        public virtual void Dispose()
        {
            _disposed = true;
            _closed = true;
            _exited.TrySetException(new ObjectDisposedException(GetType().Name));
        }
    }
}
