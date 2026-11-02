using System.ComponentModel;
using System.Diagnostics.Metrics;

namespace MiniPty.Tests;

/// <summary>
/// MiniPty's meter: each instrument moves when what it measures happens, as a <see cref="MeterListener"/> sees it.
/// </summary>
/// <remarks>
/// Instruments are process-wide, so these tests run alone (<c>[NotInParallel]</c> without a key) and read differences
/// or their own sessions' measurements, never a total that another test's sessions could share.
/// </remarks>
[NotInParallel]
public sealed class PtyMetricsTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private static readonly string[] InstrumentNames =
    [
        "minipty.session.active",
        "minipty.session.start.duration",
        "minipty.session.start.failures",
        "minipty.output.bytes",
        "minipty.output.stall.duration",
        "minipty.output.consumer_wait.duration",
    ];

    [Test]
    public async Task TheMeterPublishesEveryInstrument()
    {
        using var metrics = new MetricsRecorder();
        await using (var session = Pty.Start(Echo("published")))
            await session.CompleteAsync();

        foreach (var name in InstrumentNames)
            await Assert.That(metrics.Published).Contains(name);
    }

    /// <summary>
    /// The histograms record seconds, so each advises bucket boundaries in seconds. OpenTelemetry's default boundaries
    /// are for milliseconds (0, 5, 10, ... 10000): with them every measurement fell in the first bucket, and the Aspire
    /// dashboard showed a P50 of 5 s for stalls of about 0.1 s.
    /// </summary>
    [Test]
    public async Task HistogramsAdviseBucketBoundariesInSeconds()
    {
        using var metrics = new MetricsRecorder();
        await using (var session = Pty.Start(Echo("buckets")))
            await session.CompleteAsync();

        string[] histograms = ["minipty.session.start.duration", "minipty.output.stall.duration", "minipty.output.consumer_wait.duration"];
        foreach (var name in histograms)
        {
            var bounds = ((Histogram<double>)metrics.Instrument(name)).Advice?.HistogramBucketBoundaries;
            await Assert.That(bounds).IsNotNull().Because(name);
            await Assert.That(bounds![0]).IsLessThan(0.001).Because(name);
            for (var i = 1; i < bounds.Count; i++)
                await Assert.That(bounds[i]).IsGreaterThan(bounds[i - 1]).Because(name);
        }
    }

    [Test]
    public async Task ActiveSessionsCountsASessionUntilItIsDisposed()
    {
        using var metrics = new MetricsRecorder();
        var before = metrics.ObserveActiveSessions();
        var session = Pty.Start(Echo("active"));
        long open;
        try
        {
            open = metrics.ObserveActiveSessions();
        }
        finally
        {
            await session.DisposeAsync();
        }

        var closed = metrics.ObserveActiveSessions();
        session.Dispose();
        var disposedTwice = metrics.ObserveActiveSessions();

        var counts = $"before {before}, open {open}, closed {closed}, disposed twice {disposedTwice}";
        await Assert.That(open - before).IsEqualTo(1L).Because(counts);
        await Assert.That(closed).IsEqualTo(before).Because(counts);
        await Assert.That(disposedTwice).IsEqualTo(before).Because(counts);
    }

    [Test]
    public async Task AStartIsTimedAndAFailedStartIsCounted()
    {
        using var metrics = new MetricsRecorder();
        await using (var session = Pty.Start(Echo("timed")))
            await session.CompleteAsync();

        var missing = OperatingSystem.IsWindows()
            ? Path.Combine(Path.GetTempPath(), $"minipty-missing-{Guid.NewGuid():N}.exe")
            : $"/minipty-missing-{Guid.NewGuid():N}";
        Assert.Throws<Win32Exception>(() => Pty.Start(new PtyStartInfo { FileName = missing }).Dispose());

        var starts = metrics.Values("minipty.session.start.duration");
        var failures = metrics.Measurements("minipty.session.start.failures");
        await Assert.That(starts.Count).IsEqualTo(1);
        await Assert.That(starts[0]).IsGreaterThan(0.0);
        await Assert.That(failures.Count).IsEqualTo(1);
        await Assert.That(failures[0].Value).IsEqualTo(1.0);
        await Assert.That(failures[0].ErrorType).IsEqualTo(typeof(Win32Exception).FullName);
    }

    [Test]
    public async Task OutputBytesCountWhatWasReadFromTheTerminal()
    {
        using var metrics = new MetricsRecorder();
        PtyResult result;
        await using (var session = Pty.Start(Flood()))
            result = await session.CompleteAsync(new PtyCompleteOptions { DecodeOutput = false });

        // Every byte delivered was read first; bytes read after the end of output was decided may be discarded.
        var read = metrics.Values("minipty.output.bytes").Sum();
        await Assert.That(result.Output.Length).IsGreaterThan(16_384);
        await Assert.That(read).IsGreaterThanOrEqualTo(result.Output.Length);
    }

    /// <summary>
    /// A flood nobody reads fills the ring and stops reading, which blocks the child; the stop is recorded with its
    /// length once a reader frees space.
    /// </summary>
    [Test]
    public async Task AStallOnAFullRingIsTimed()
    {
        using var metrics = new MetricsRecorder();
        await using var session = Pty.Start(Flood());
        await Assert.That(await WaitUntilAsync(() => session.OutputPump.IsWaitingForFreeSpace)).IsTrue();
        await Task.Delay(100);

        using var cts = new CancellationTokenSource(Timeout);
        await foreach (var _ in session.ReadOutputAsync(cts.Token))
        {
        }

        var stalls = metrics.Values("minipty.output.stall.duration");
        await Assert.That(stalls.Count).IsGreaterThanOrEqualTo(1);
        await Assert.That(stalls.Max()).IsGreaterThanOrEqualTo(0.09);
    }

    /// <summary>
    /// A plain consumer of a Unix flood waits for the master on its own thread, each wait bounded at 200 μs on Linux and
    /// 1 ms on macOS (<c>PtyUnixLoopTests.AConsumerWaitsForTheMasterOnItsOwnThread</c>). Windows has no such wait.
    /// </summary>
    [Test]
    public async Task AConsumersWaitInNativeCodeIsTimedOnUnix()
    {
        if (!(OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()))
            return;

        using var metrics = new MetricsRecorder();
        await using (var session = Pty.Start(new PtyStartInfo { FileName = "sh", Arguments = ["-c", "dd if=/dev/zero bs=1048576 count=1 2>/dev/null"] }))
        {
            using var cts = new CancellationTokenSource(Timeout);
            await foreach (var _ in session.ReadOutputAsync(cts.Token))
            {
            }
        }

        var waits = metrics.Values("minipty.output.consumer_wait.duration");
        await Assert.That(waits.Count).IsGreaterThanOrEqualTo(1);
        // Bounded by the wait's timeout; a loaded runner can deschedule the thread past it, hence the margin.
        await Assert.That(waits.Max()).IsLessThan(0.5);
    }

    /// <summary>
    /// The recording calls on the output path allocate nothing, also with a listener: no measurement there carries a tag
    /// to box. Without a listener they take no timestamp either.
    /// </summary>
    [Test]
    public async Task RecordingAllocatesNothingWithOrWithoutAListener()
    {
        var idleTimestamps = RecordAll();
        var idleAllocated = AllocatedBy(() => RecordAll());

        using var listener = new MeterListener
        {
            InstrumentPublished = static (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == "MiniPty")
                    meterListener.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>(static (_, _, _, _) => { });
        listener.SetMeasurementEventCallback<double>(static (_, _, _, _) => { });
        listener.Start();
        var listenedTimestamps = RecordAll();
        var listenedAllocated = AllocatedBy(() => RecordAll());

        await Assert.That(idleTimestamps).IsEqualTo(0L);
        await Assert.That(idleAllocated).IsEqualTo(0L);
        await Assert.That(listenedTimestamps).IsNotEqualTo(0L);
        await Assert.That(listenedAllocated).IsEqualTo(0L);

        // Every recording call on the output path and at start, once; returns the timestamps taken, OR-ed together.
        static long RecordAll()
        {
            MiniPty.Internal.PtyMetrics.RecordOutputRead(4096);
            var stall = MiniPty.Internal.PtyMetrics.OutputStallTimestamp();
            MiniPty.Internal.PtyMetrics.RecordOutputStall(stall);
            var wait = MiniPty.Internal.PtyMetrics.ConsumerWaitTimestamp();
            MiniPty.Internal.PtyMetrics.RecordConsumerWait(wait);
            var start = MiniPty.Internal.PtyMetrics.StartTimestamp();
            MiniPty.Internal.PtyMetrics.RecordStart(start);
            return stall | wait | start;
        }

        static long AllocatedBy(Action action)
        {
            action();
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++)
                action();
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }

    private static PtyStartInfo Echo(string text) =>
        OperatingSystem.IsWindows()
            ? new PtyStartInfo { FileName = "cmd.exe", Arguments = ["/c", "echo " + text] }
            : new PtyStartInfo { FileName = "sh", Arguments = ["-c", "echo " + text] };

    /// <summary>About 128 KB of output: eight times the ring.</summary>
    private static PtyStartInfo Flood() =>
        OperatingSystem.IsWindows()
            ? new PtyStartInfo
            {
                FileName = "cmd.exe",
                CommandLine = $"/s /c \"{WindowsFloodFile.TypeCommand(640, 200)}\"",
                Size = new PtySize(220, 24),
            }
            : new PtyStartInfo { FileName = "sh", Arguments = ["-c", "dd if=/dev/zero bs=131072 count=1 2>/dev/null"] };

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

    /// <summary>Listens to this load's <c>MiniPty</c> meter and keeps every measurement it reports.</summary>
    /// <remarks>
    /// The meter is matched by identity, not by name: <see cref="PtyLoadContextTests"/> loads MiniPty a second time, and
    /// that load has a meter named <c>MiniPty</c> of its own, which keeps counting its own sessions (none). Matched by
    /// name, the suite read the other load's open sessions, 0 throughout.
    /// </remarks>
    private sealed class MetricsRecorder : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly Lock _lock = new();
        private readonly List<(string Name, double Value, string? ErrorType)> _measurements = [];
        private readonly Dictionary<string, Instrument> _published = [];

        public MetricsRecorder()
        {
            var meter = MiniPty.Internal.PtyMetrics.MeterForTests;
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (!ReferenceEquals(instrument.Meter, meter))
                    return;

                lock (_lock)
                    _published[instrument.Name] = instrument;
                listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument.Name, value, tags));
            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Add(instrument.Name, value, tags));
            _listener.Start();
        }

        public string[] Published
        {
            get
            {
                lock (_lock)
                    return [.. _published.Keys];
            }
        }

        public Instrument Instrument(string name)
        {
            lock (_lock)
                return _published[name];
        }

        /// <summary>Reads <c>minipty.session.active</c> now.</summary>
        public long ObserveActiveSessions()
        {
            _listener.RecordObservableInstruments();
            lock (_lock)
                return (long)_measurements.Last(m => m.Name == "minipty.session.active").Value;
        }

        public List<double> Values(string name) => Measurements(name).Select(m => m.Value).ToList();

        public List<(double Value, string? ErrorType)> Measurements(string name)
        {
            lock (_lock)
                return _measurements.Where(m => m.Name == name).Select(m => (m.Value, m.ErrorType)).ToList();
        }

        public void Dispose() => _listener.Dispose();

        private void Add(string name, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            string? errorType = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "error.type")
                    errorType = tag.Value as string;
            }

            lock (_lock)
                _measurements.Add((name, value, errorType));
        }
    }
}
