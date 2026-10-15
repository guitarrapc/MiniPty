using System.Collections.Concurrent;
using System.Diagnostics;

namespace MiniPty.Tests;

/// <summary>
/// Single-threaded message loop standing in for a GUI dispatcher (WinUI <c>DispatcherQueue</c>, WPF
/// <c>Dispatcher</c>, WinForms message loop).
/// </summary>
/// <remarks>
/// <para>
/// Continuations captured on this context run only on its thread, so a blocking call that lands there
/// stalls every later callback, exactly like a frozen UI thread. Test runners have no
/// <see cref="SynchronizationContext"/>, which is why caller-context blocking is invisible to ordinary
/// tests: <c>Task.Yield</c> and captured awaits silently resume on the thread pool instead.
/// </para>
/// <para>
/// The loop thread is a background thread so a test that deadlocks it cannot keep the test process alive.
/// </para>
/// </remarks>
internal sealed class SingleThreadSynchronizationContext : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
    private long _busyTicks;
    private long _runningSince;

    public SingleThreadSynchronizationContext()
    {
        var thread = new Thread(Run) { IsBackground = true, Name = "test-ui-thread" };
        thread.Start();
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        // Late continuations from a test that already failed are dropped after Dispose rather than thrown
        // on whichever thread completed them.
        try
        {
            _queue.Add((d, state));
        }
        catch (InvalidOperationException)
        {
        }
    }

    public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

    public override SynchronizationContext CreateCopy() => this;

    /// <summary>Starts <paramref name="operation"/> on the UI thread and observes it from the caller.</summary>
    public Task RunAsync(Func<Task> operation) =>
        RunAsync(async () =>
        {
            await operation();
            return true;
        });

    /// <summary>Starts <paramref name="operation"/> on the UI thread and observes its result from the caller.</summary>
    public Task<T> RunAsync<T>(Func<Task<T>> operation)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(_ => Start(operation, completion), null);
        return completion.Task;

        // async void is safe here: every exception is routed into the completion source.
        static async void Start(Func<Task<T>> operation, TaskCompletionSource<T> completion)
        {
            try
            {
                completion.TrySetResult(await operation());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when a probe posted to the UI thread runs within
    /// <paramref name="timeout"/>, that is, when nothing is blocking the UI thread.
    /// </summary>
    /// <remarks>
    /// The latency is timestamped on the UI thread itself, so only the UI thread's own delay counts. Observing
    /// the result needs thread-pool continuations, and a starved pool in a parallel test run must not make a
    /// free UI thread look blocked, so the observation gets a generous allowance of its own.
    /// </remarks>
    public async Task<bool> IsResponsiveAsync(TimeSpan timeout)
    {
        var postedAt = Stopwatch.GetTimestamp();
        var probe = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(static state => ((TaskCompletionSource<long>)state!).TrySetResult(Stopwatch.GetTimestamp()), probe);
        try
        {
            var ranAt = await probe.Task.WaitAsync(timeout + TimeSpan.FromSeconds(10));
            return Stopwatch.GetElapsedTime(postedAt, ranAt) <= timeout;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Measures how long the UI thread spent running callbacks during <paramref name="window"/>,
    /// including a callback that is still running when the window ends.
    /// </summary>
    /// <remarks>
    /// A probe alone can miss a thread that blocks in slices and briefly yields between them (for example
    /// a 100 ms poll followed by <c>Task.Yield</c>): each probe still runs within a slice. Busy time sees
    /// the slices add up to the whole window.
    /// </remarks>
    public async Task<TimeSpan> MeasureBusyTimeAsync(TimeSpan window)
    {
        var start = ReadBusyTicks();
        await Task.Delay(window);
        var end = ReadBusyTicks();
        return TimeSpan.FromSeconds((end - start) / (double)Stopwatch.Frequency);
    }

    /// <summary>Gets the first exception thrown by a callback, which would otherwise kill the loop thread.</summary>
    public Exception? CallbackException { get; private set; }

    public void Dispose() => _queue.CompleteAdding();

    private long ReadBusyTicks()
    {
        var accumulated = Volatile.Read(ref _busyTicks);
        var runningSince = Volatile.Read(ref _runningSince);
        return runningSince == 0 ? accumulated : accumulated + (Stopwatch.GetTimestamp() - runningSince);
    }

    private void Run()
    {
        SetSynchronizationContext(this);
        foreach (var (callback, state) in _queue.GetConsumingEnumerable())
        {
            Volatile.Write(ref _runningSince, Stopwatch.GetTimestamp());
            try
            {
                callback(state);
            }
            catch (Exception ex)
            {
                CallbackException ??= ex;
            }
            finally
            {
                var startedAt = Interlocked.Exchange(ref _runningSince, 0);
                Interlocked.Add(ref _busyTicks, Stopwatch.GetTimestamp() - startedAt);
            }
        }
    }
}
