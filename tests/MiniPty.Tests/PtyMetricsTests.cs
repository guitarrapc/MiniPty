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
    /// The histograms record seconds, so each must advise ascending boundaries that start below a millisecond. Without
    /// them an exporter's default boundaries, meant for milliseconds, put every measurement in one bucket.
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
        // Checked only while the start builds the environment, so it reaches the failure filter, which must skip it.
        Assert.Throws<ArgumentException>(() => Pty.Start(Echo("bad") with { Environment = new Dictionary<string, string?> { ["A=B"] = "x" } }).Dispose());
        // Refused before the counted part of the start, so it must not count either.
        if (!OperatingSystem.IsWindows())
            Assert.Throws<PlatformNotSupportedException>(() => Pty.Start(new PtyStartInfo { FileName = "sh", CommandLine = "-c true" }).Dispose());

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

        // CompleteAsync returns after the last read, before the disposal, so each read counts once and the total is the
        // output delivered.
        var read = metrics.Values("minipty.output.bytes").Sum();
        await Assert.That(result.Output.Length).IsGreaterThan(16_384);
        await Assert.That(read).IsEqualTo(result.Output.Length);
    }

    /// <summary>
    /// A flood nobody reads parks the reading on a full ring, with output still to come from the transport. What the
    /// platform's driver reads after the disposal must not count. On Windows the driver reads then only to let the
    /// transport end, and only if it reaches the output pipe before the disposal closes it. When the close comes first,
    /// nothing is read, and the test passes either way. <see cref="AReadPublishedAfterTheDisposalIsNotCounted"/> makes
    /// that read every time. On Unix the reads after the disposal are discarded without being published.
    /// </summary>
    [Test]
    public async Task ReadsAfterTheDisposalAreNotCounted()
    {
        using var metrics = new MetricsRecorder();
        var session = Pty.Start(Flood());
        double beforeDisposal;
        try
        {
            await Assert.That(await WaitUntilAsync(() => session.OutputPump.IsWaitingForFreeSpace)).IsTrue();
            beforeDisposal = metrics.Values("minipty.output.bytes").Sum();
        }
        finally
        {
            await session.DisposeAsync();
        }

        await Assert.That(beforeDisposal).IsGreaterThan(0.0);
        await Assert.That(metrics.Values("minipty.output.bytes").Sum()).IsEqualTo(beforeDisposal);
    }

    /// <summary>
    /// A read published after the disposal is not counted. The test is the session's output driver and publishes as the
    /// Windows driver does when it reads after the disposal, so that read happens whatever the timing.
    /// </summary>
    [Test]
    public async Task AReadPublishedAfterTheDisposalIsNotCounted()
    {
        using var metrics = new MetricsRecorder();
        var session = new PtySession(new SelfDrivenBackend());
        var pump = session.OutputPump;

        pump.HoldRing();
        pump.Publish(100, 0);
        session.Dispose();
        pump.Publish(100, 0);
        pump.LetGoOfRing();
        pump.Finish(null);
        await session.DisposeAsync();

        await Assert.That(metrics.Values("minipty.output.bytes").Sum()).IsEqualTo(100.0);
    }

    /// <summary>
    /// A host's listener that throws on the start's duration makes <see cref="Pty.Start"/> throw after the session
    /// exists. The start must dispose that session, since no caller holds it.
    /// </summary>
    [Test]
    public async Task AListenerThatThrowsOnTheStartLeavesNoSessionOpen()
    {
        using var metrics = new MetricsRecorder();
        using var throwing = new MeterListener
        {
            InstrumentPublished = static (instrument, listener) =>
            {
                if (instrument.Name == "minipty.session.start.duration")
                    listener.EnableMeasurementEvents(instrument);
            },
        };
        throwing.SetMeasurementEventCallback<double>(static (_, _, _, _) => throw new InvalidOperationException("listener bug"));
        throwing.Start();

        var before = metrics.ObserveActiveSessions();
        Assert.Throws<InvalidOperationException>(() => Pty.Start(Echo("throwing")));
        var after = metrics.ObserveActiveSessions();

        await Assert.That(after).IsEqualTo(before);
    }

    /// <summary>
    /// A start that fails while the session is built, after its child exists, is counted with the exception's type and
    /// is not timed. The open-session count moves only once a session is built, so it stays where it was. That such a
    /// start also ends its child is checked by <c>PtyStartFaultTests</c>.
    /// </summary>
    [Test]
    public async Task AStartThatFailsInsideTheSessionIsCountedAsAFailureOnly()
    {
        using var metrics = new MetricsRecorder();
        var before = metrics.ObserveActiveSessions();
        MiniPty.Internal.PtyStartTestHook.Fault = static (stage, _) =>
        {
            if (stage == MiniPty.Internal.PtyStartStage.BackendBuilt)
                throw new TimeoutException("injected");
        };
        try
        {
            Assert.Throws<TimeoutException>(() => Pty.Start(Echo("fault")));
        }
        finally
        {
            MiniPty.Internal.PtyStartTestHook.Fault = null;
        }

        var after = metrics.ObserveActiveSessions();
        var failures = metrics.Measurements("minipty.session.start.failures");
        await Assert.That(after).IsEqualTo(before);
        await Assert.That(failures.Count).IsEqualTo(1);
        await Assert.That(failures[0].ErrorType).IsEqualTo(typeof(TimeoutException).FullName);
        await Assert.That(metrics.Values("minipty.session.start.duration").Count).IsEqualTo(0);
    }

    /// <summary>
    /// A flood nobody reads fills the ring and stops reading, which blocks the child. The stop is recorded with its
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
        // Each wait is bounded by its timeout. The margin covers a loaded runner that deschedules the thread past it.
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

        // Each recording call on the output path, and the one for the start's duration, once. Returns the timestamps
        // taken, OR-ed together.
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

    /// <summary>
    /// A backend whose output the test publishes itself: it is the session's output driver and never reads.
    /// </summary>
    private sealed class SelfDrivenBackend : PtyOutputPumpTests.FakeBackend, MiniPty.Internal.IPtyOutputDriver
    {
        public override void StartOutput(MiniPty.Internal.PtyOutputPump pump) => pump.AttachDriver(this);

        void MiniPty.Internal.IPtyOutputDriver.Wake(MiniPty.Internal.PtyOutputPump pump)
        {
        }

        void MiniPty.Internal.IPtyOutputDriver.CancelRead(MiniPty.Internal.PtyOutputPump pump)
        {
        }

        void MiniPty.Internal.IPtyOutputDriver.OnPumpFinished(MiniPty.Internal.PtyOutputPump pump)
        {
        }
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

    /// <summary>Listens to this load's <c>MiniPty</c> meter and keeps every measurement it reports.</summary>
    /// <remarks>
    /// The meter is matched by identity, not by name: <see cref="PtyLoadContextTests"/> loads MiniPty a second time, and
    /// that load has a meter named <c>MiniPty</c> of its own, which counts none of these tests' sessions.
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
