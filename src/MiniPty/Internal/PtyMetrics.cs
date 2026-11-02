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
/// When nothing listens, an instrument costs a branch on its <see cref="Instrument.Enabled"/> flag and allocates
/// nothing; no timestamp is taken either. Only <c>minipty.session.active</c> counts always, because a listener that
/// attaches later must read the number of open sessions, not the starts and disposals since it attached: an
/// interlocked add at each start and at each disposal.
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

    private static readonly Histogram<double> s_startDuration = s_meter.CreateHistogram<double>(
        "minipty.session.start.duration",
        "s",
        "Time Pty.Start took to start a child that started.");

    private static readonly Counter<long> s_startFailures = s_meter.CreateCounter<long>(
        "minipty.session.start.failures",
        "{failure}",
        "Starts that failed after their arguments were accepted, tagged with error.type.");

    private static readonly Counter<long> s_outputBytes = s_meter.CreateCounter<long>(
        "minipty.output.bytes",
        "By",
        "Bytes read from the terminals' output.");

    private static readonly Histogram<double> s_outputStallDuration = s_meter.CreateHistogram<double>(
        "minipty.output.stall.duration",
        "s",
        "Time a session's reading stood still because its output ring was full and nobody drained it; the child stays blocked meanwhile.");

    private static readonly Histogram<double> s_consumerWaitDuration = s_meter.CreateHistogram<double>(
        "minipty.output.consumer_wait.duration",
        "s",
        "Time a consumer's thread waited in native code for output (Unix), each wait bounded at 200 μs on Linux and 1 ms on macOS.");

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
    internal static bool RecordStartFailure(Exception error)
    {
        if (s_startFailures.Enabled)
            s_startFailures.Add(1, new KeyValuePair<string, object?>("error.type", error.GetType().FullName));

        return false;
    }

    /// <summary>Counts <paramref name="bytes"/> read from a terminal's output.</summary>
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
