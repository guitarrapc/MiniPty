using System.Diagnostics;
using MiniPty.Internal;

namespace MiniPty.Tests;

/// <summary>
/// The output driver of the scripted test backends (<see cref="PtyOutputPumpTests.FakeBackend"/>): a thread of its own
/// per session that calls the backend's script as a blocking transport read, so a test scripts reads, waits, and end of
/// output with plain blocking code. It follows the pump's producer contract as the platform drivers do: delivers the
/// backlog to a sink, waits on a full ring for the consumer's wake, publishes each read as it completes, and once the end
/// of output is forced takes only what the script holds now.
/// </summary>
/// <remarks>
/// It holds the ring from its first read until end of output, since a blocking read needs its buffer while it waits.
/// That makes it unfit for tests of the ring's return to the pool, which the platform drivers cover.
/// </remarks>
internal sealed class ScriptedOutputDriver : IPtyOutputDriver
{
    /// <summary>The name of the driver's thread, for tests that check where the pump completed a consumer's wait.</summary>
    internal const string ThreadName = "MiniPty scripted output";

    private readonly PtyOutputPump _pump;
    private readonly PtyOutputPumpTests.FakeBackend _source;
    // A monitor rather than an event, so a session holds no kernel handle that leak checks would count.
    private readonly object _gate = new();
    private bool _woken;

    private ScriptedOutputDriver(PtyOutputPump pump, PtyOutputPumpTests.FakeBackend source)
    {
        _pump = pump;
        _source = source;
    }

    /// <summary>Runs <paramref name="pump"/> over <paramref name="source"/>'s script on a new thread.</summary>
    internal static void Run(PtyOutputPump pump, PtyOutputPumpTests.FakeBackend source)
    {
        var driver = new ScriptedOutputDriver(pump, source);
        pump.AttachDriver(driver);
        var thread = new Thread(driver.Loop)
        {
            IsBackground = true,
            Name = ThreadName,
        };
        thread.UnsafeStart();
    }

    public void Wake(PtyOutputPump pump)
    {
        if (pump != _pump)
            return;

        lock (_gate)
        {
            _woken = true;
            Monitor.Pulse(_gate);
        }
    }

    // A script's read that waits does so through WaitForWake, so the wake releases it.
    public void CancelRead(PtyOutputPump pump) => Wake(pump);

    public void OnPumpFinished(PtyOutputPump pump)
    {
    }

    /// <summary>
    /// Blocks the script until the pump wakes the driver or <paramref name="timeoutMs"/> elapses
    /// (<see cref="Timeout.Infinite"/> waits indefinitely). Callers re-check state afterwards.
    /// </summary>
    internal bool WaitForWake(int timeoutMs)
    {
        lock (_gate)
        {
            if (!_woken)
                Monitor.Wait(_gate, timeoutMs);

            var woken = _woken;
            _woken = false;
            return woken;
        }
    }

    private void Loop()
    {
        Exception? error = null;
        try
        {
            // A transport closed before the driver started has nothing to read, as a closed master or pipe.
            if (!_source.IsOutputClosed)
            {
                _pump.HoldRing();
                Produce();
            }
        }
        catch (Exception ex)
        {
            error = ex;
        }

        _pump.Finish(error);
    }

    private void Produce()
    {
        while (true)
        {
            if (!_pump.IsSessionDisposed)
            {
                _pump.DeliverBacklogToSink();
                if (_pump.IsFull)
                {
                    if (_pump.BeginWaitForFreeSpace())
                        WaitForWake(Timeout.Infinite);

                    _pump.EndWaitForFreeSpace();
                    continue;
                }
            }

            var buffer = _pump.NextReadSpan;
            int read;
            if (_pump.IsEndOfOutputForced)
            {
                // Take what the transport holds without blocking, then report end of output.
                read = _source.ReadOutputNow(buffer);
                if (read <= 0)
                    return;
            }
            else
            {
                read = _source.ReadOutput(buffer, this);
                if (read < 0)
                    continue; // Woken: re-check state.
                if (read == 0)
                    return;
            }

            var timestamp = Stopwatch.GetTimestamp();
            _pump.OnOutputRead();
            _pump.Publish(read, timestamp);
        }
    }
}
