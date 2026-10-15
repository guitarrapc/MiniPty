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

    /// <summary>A child that never exits on its own; subclasses script the output transport.</summary>
    private abstract class FakeBackend : IPtyBackend
    {
        private readonly TaskCompletionSource<PtyExitStatus> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _closed;

        public Stream Input => Stream.Null;
        public int ProcessId => 0;
        public bool HasExited => false;
        public int ExitCode => 0;
        public int? ExitSignal => null;
        public PtySize Size => new(80, 24);
        public string? ActiveProcessName => null;
        public Task<PtyExitStatus> Exited => _exited.Task;
        public bool IsOutputClosed => _closed;
        public long CoalesceBudgetTicks => Stopwatch.Frequency * 2 / 1000;

        public void Resize(int columns, int rows, int pixelWidth, int pixelHeight) { }
        public void SendEof() { }
        public void Kill() { }
        public void Kill(PtySignal signal) { }
        public ValueTask<int> WaitForExitCodeAsync() => new(_exited.Task.ContinueWith(static t => t.Result.ExitCode, TaskScheduler.Default));
        public void SetExitListener(IPtyExitListener listener) { }
        public bool TryBeginOutputRead() => !_closed;
        public void EndOutputRead() { }
        public void CloseOutputTransport() => _closed = true;

        public abstract int ReadOutput(Span<byte> buffer, PtyReaderThread reader);
        public abstract int ReadMoreOutput(Span<byte> buffer, PtyReaderThread reader, out bool endOfOutput);

        public virtual void Dispose()
        {
            _closed = true;
            _exited.TrySetException(new ObjectDisposedException(GetType().Name));
        }
    }
}
