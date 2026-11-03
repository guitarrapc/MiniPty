using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace MiniPty.Internal;

/// <summary>
/// MiniPty's instruments, on the <see cref="Meter"/> named <c>MiniPty</c>: what a host pays on the output path and at
/// start, which otherwise shows only as latency. A host collects them with OpenTelemetry (<c>AddMeter("MiniPty")</c>),
/// <c>dotnet-counters monitor --counters MiniPty</c>, or a <see cref="MeterListener"/>.
/// </summary>
/// <remarks>
/// <para>
/// When nothing listens, an instrument costs a check of its <see cref="Instrument.Enabled"/> flag. It takes no
/// timestamp and allocates nothing. Only <c>minipty.session.active</c> counts always, with an interlocked add at each
/// start and at each disposal, because a listener that attaches later must read the number of open sessions, not the
/// starts and disposals since it attached.
/// </para>
/// <para>
/// No measurement carries a tag that differs per session, so a host with thousands of sessions keeps one series per
/// instrument. Start failures carry <c>error.type</c>, the exception's type, which takes a handful of values.
/// </para>
/// <para>
/// Names follow the OpenTelemetry conventions: dot-separated, durations in seconds, and sizes in bytes.
/// </para>
/// </remarks>
internal static class PtyMetrics
{
    internal const string MeterName = "MiniPty";

    private static long s_activeSessions;

    private static readonly Meter s_meter = CreateMeter();

    // Each histogram advises bucket boundaries in seconds that span the range its instrument records. Without advice an
    // exporter falls back to boundaries meant for milliseconds, and every value here would land in the first bucket.

    private static readonly Histogram<double> s_startDuration = s_meter.CreateHistogram(
        "minipty.session.start.duration",
        "s",
        "Time Pty.Start took to start a child that started.",
        tags: null,
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.0005, 0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10] });

    private static readonly Counter<long> s_startFailures = s_meter.CreateCounter<long>(
        "minipty.session.start.failures",
        "{failure}",
        "Starts that failed, tagged with error.type. An ArgumentException, a mistake in what to start, is not counted.");

    private static readonly Counter<long> s_outputBytes = s_meter.CreateCounter<long>(
        "minipty.output.bytes",
        "By",
        "Bytes read from the terminals' output while their sessions were open.");

    private static readonly Histogram<double> s_outputStallDuration = s_meter.CreateHistogram(
        "minipty.output.stall.duration",
        "s",
        "Time a session's reading stood still because its output ring was full and nobody drained it. The child stays blocked meanwhile.",
        tags: null,
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.0001, 0.00025, 0.0005, 0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30] });

    private static readonly Histogram<double> s_consumerWaitDuration = s_meter.CreateHistogram(
        "minipty.output.consumer_wait.duration",
        "s",
        "Time a consumer's thread waited in native code for output (Unix), each wait bounded at 200 μs on Linux and 1 ms on macOS.",
        tags: null,
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.00001, 0.000025, 0.00005, 0.0001, 0.00015, 0.0002, 0.0003, 0.0005, 0.00075, 0.001, 0.0015, 0.002, 0.005, 0.01] });

    /// <summary>This load's meter. Another load of MiniPty, in another <c>AssemblyLoadContext</c>, has one of the same name.</summary>
    internal static Meter MeterForTests => s_meter;

    /// <summary>The meter, with the one instrument that is read, not recorded: the number of open sessions.</summary>
    private static Meter CreateMeter()
    {
        var meter = new Meter(MeterName);
        meter.CreateObservableUpDownCounter(
            "minipty.session.active",
            static () => Volatile.Read(ref s_activeSessions),
            "{session}",
            "Sessions started and not yet disposed.");
        return meter;
    }

    /// <summary>Counts a session as open. Called once, when its construction succeeded.</summary>
    internal static void SessionStarted() => Interlocked.Increment(ref s_activeSessions);

    /// <summary>Counts a session as closed. Called once, by the disposal that tears it down.</summary>
    internal static void SessionDisposed() => Interlocked.Decrement(ref s_activeSessions);

    /// <summary>A timestamp for <see cref="RecordStart"/>, or 0 when nothing listens to start durations.</summary>
    internal static long StartTimestamp() => s_startDuration.Enabled ? Stopwatch.GetTimestamp() : 0;

    /// <summary>Records a start that succeeded, timed from <paramref name="started"/> unless that is 0.</summary>
    internal static void RecordStart(long started)
    {
        if (started != 0)
            s_startDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
    }

    /// <summary>
    /// Counts a failed start. Meant for an exception filter, so it returns <see langword="false"/> and the exception
    /// propagates untouched.
    /// </summary>
    /// <remarks>
    /// An <see cref="ArgumentException"/> is the caller's mistake in what to start, not a start that failed, so it is
    /// not counted. Some arguments are checked only while the start builds the environment or the command line.
    /// </remarks>
    internal static bool RecordStartFailure(Exception error)
    {
        if (error is ArgumentException)
            return false;

        if (s_startFailures.Enabled)
            s_startFailures.Add(1, new KeyValuePair<string, object?>("error.type", error.GetType().FullName));

        return false;
    }

    /// <summary>Counts <paramref name="bytes"/> read from an open session's terminal.</summary>
    internal static void RecordOutputRead(int bytes)
    {
        if (s_outputBytes.Enabled)
            s_outputBytes.Add(bytes);
    }

    /// <summary>A timestamp for <see cref="RecordOutputStall"/>, or 0 when nothing listens to stalls.</summary>
    internal static long OutputStallTimestamp() => s_outputStallDuration.Enabled ? Stopwatch.GetTimestamp() : 0;

    /// <summary>Records a stall on a full ring that began at <paramref name="started"/>, unless that is 0.</summary>
    internal static void RecordOutputStall(long started)
    {
        if (started != 0)
            s_outputStallDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
    }

    /// <summary>A timestamp for <see cref="RecordConsumerWait"/>, or 0 when nothing listens to consumer waits.</summary>
    internal static long ConsumerWaitTimestamp() => s_consumerWaitDuration.Enabled ? Stopwatch.GetTimestamp() : 0;

    /// <summary>Records a consumer's native wait that began at <paramref name="started"/>, unless that is 0.</summary>
    internal static void RecordConsumerWait(long started)
    {
        if (started != 0)
            s_consumerWaitDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
    }
}
