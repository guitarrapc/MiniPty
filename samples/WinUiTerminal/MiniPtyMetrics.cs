using System.Diagnostics.Metrics;

namespace WinUiTerminal;

/// <summary>
/// Running totals of MiniPty's instruments (the meter named <c>MiniPty</c>), for the status line.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="MeterListener"/> gets each measurement on the thread that records it: MiniPty's I/O thread for output
/// bytes and stalls, the starting thread for a start. Its callbacks therefore only add to interlocked totals, and the
/// window reads them on a timer of its own, on the UI thread. Each instrument's total is the state the listener hands
/// back with every measurement, so a measurement costs a cast and three interlocked operations, with no lookup by name.
/// </para>
/// <para>
/// An application that already exports metrics with OpenTelemetry adds <c>AddMeter("MiniPty")</c> instead.
/// </para>
/// </remarks>
internal sealed class MiniPtyMetrics : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly Total _activeSessions = new();
    private readonly Total _startDuration = new();
    private readonly Total _startFailures = new();
    private readonly Total _outputBytes = new();
    private readonly Total _stallDuration = new();
    private readonly Total _consumerWaitDuration = new();

    public MiniPtyMetrics()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name != "MiniPty")
                return;

            Total? total = instrument.Name switch
            {
                "minipty.session.active" => _activeSessions,
                "minipty.session.start.duration" => _startDuration,
                "minipty.session.start.failures" => _startFailures,
                "minipty.output.bytes" => _outputBytes,
                "minipty.output.stall.duration" => _stallDuration,
                "minipty.output.consumer_wait.duration" => _consumerWaitDuration,
                _ => null,
            };
            if (total is not null)
                listener.EnableMeasurementEvents(instrument, total);
        };
        _listener.SetMeasurementEventCallback<long>(static (_, value, _, state) => ((Total)state!).Add(value));
        // Durations arrive in seconds; the totals keep microseconds, so they stay integers.
        _listener.SetMeasurementEventCallback<double>(static (_, seconds, _, state) => ((Total)state!).Add((long)(seconds * 1_000_000)));
        _listener.Start();
    }

    /// <summary>The totals now. The open-session count is read at this call.</summary>
    public Snapshot Read()
    {
        _listener.RecordObservableInstruments();
        return new Snapshot(
            ActiveSessions: _activeSessions.Last,
            Starts: _startDuration.Count,
            StartTime: TimeSpan.FromMicroseconds(_startDuration.Sum),
            StartFailures: _startFailures.Sum,
            OutputBytes: _outputBytes.Sum,
            Stalls: _stallDuration.Count,
            StallTime: TimeSpan.FromMicroseconds(_stallDuration.Sum),
            ConsumerWaits: _consumerWaitDuration.Count,
            ConsumerWaitTime: TimeSpan.FromMicroseconds(_consumerWaitDuration.Sum));
    }

    public void Dispose() => _listener.Dispose();

    /// <summary>Totals since this listener started; <see cref="StartTime"/> and the other times are summed.</summary>
    internal readonly record struct Snapshot(
        long ActiveSessions,
        long Starts,
        TimeSpan StartTime,
        long StartFailures,
        long OutputBytes,
        long Stalls,
        TimeSpan StallTime,
        long ConsumerWaits,
        TimeSpan ConsumerWaitTime);

    /// <summary>One instrument's measurements: how many, their sum, and the last one.</summary>
    private sealed class Total
    {
        private long _count;
        private long _sum;
        private long _last;

        public long Count => Volatile.Read(ref _count);

        public long Sum => Volatile.Read(ref _sum);

        public long Last => Volatile.Read(ref _last);

        public void Add(long value)
        {
            Interlocked.Increment(ref _count);
            Interlocked.Add(ref _sum, value);
            Volatile.Write(ref _last, value);
        }
    }
}
