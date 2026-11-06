using System.Diagnostics;

namespace MiniPty.Internal;

/// <summary>
/// Temporary (improvement plan I3): a timeline of one Unix session's echo path, both the consumer's side and the
/// watcher loop's, for the investigation of echoes delayed on macOS during another session's flood. Only
/// <c>PtyUnixLoopTests.AFloodDoesNotDelayAnotherSessionsEcho</c> starts it. Remove this file, and every line marked
/// <c>Temporary</c>, before the PR.
/// </summary>
/// <remarks>
/// Events of the traced session are kept with its pid. The loop's own events and its services of other sessions are
/// kept with pid 0. Each event records a Stopwatch timestamp, a code, one argument and the managed thread id. Recording
/// stops when the buffer is full, and does nothing while no trace runs.
/// </remarks>
internal static class TemporaryEchoTrace
{
    // The session's input.
    internal const int InputWritten = 1;
    internal const int InputFlushed = 2;

    // The consumer's side (TryReadInline).
    internal const int ConsumerTry = 10;
    internal const int ConsumerTurnBusy = 11;
    internal const int ConsumerRead = 12;
    internal const int ConsumerLeftToParkedLoop = 13;
    internal const int ConsumerWaited = 14;
    internal const int ConsumerParks = 15;
    internal const int ConsumerArm = 16;

    // The loop's side.
    internal const int LoopWaitBegin = 20;
    internal const int LoopWaitEnd = 21;
    internal const int LoopService = 22;
    internal const int LoopServiceOther = 23;
    internal const int LoopTurnBusy = 24;
    internal const int LoopRead = 25;
    internal const int LoopArm = 26;
    internal const int LoopArmSkipped = 27;
    internal const int LoopTakeOver = 28;
    internal const int LoopResumeInline = 29;
    internal const int LoopRequest = 30;

    private static int s_pid;
    private static int s_count;
    private static long[] s_timestamps = [];
    private static int[] s_pids = [];
    private static int[] s_codes = [];
    private static int[] s_args = [];
    private static int[] s_threads = [];

    /// <summary>Starts a trace of the session with <paramref name="pid"/>, dropping any earlier one.</summary>
    internal static void Start(int pid, int capacity)
    {
        Volatile.Write(ref s_pid, 0);
        s_timestamps = new long[capacity];
        s_pids = new int[capacity];
        s_codes = new int[capacity];
        s_args = new int[capacity];
        s_threads = new int[capacity];
        Volatile.Write(ref s_count, 0);
        Volatile.Write(ref s_pid, pid);
    }

    /// <summary>Stops recording; the events stay for <see cref="Snapshot"/>.</summary>
    internal static void Stop() => Volatile.Write(ref s_pid, 0);

    /// <summary>Records an event of the session with <paramref name="pid"/>, or of the loop when it is 0.</summary>
    internal static void Record(int pid, int code, int arg)
    {
        var traced = Volatile.Read(ref s_pid);
        if (traced == 0 || (pid != traced && pid != 0))
            return;

        Write(pid, code, arg);
    }

    /// <summary>Records a loop service: of the traced session, or of another one, kept as the loop's own event.</summary>
    internal static void RecordService(int pid, int report)
    {
        var traced = Volatile.Read(ref s_pid);
        if (traced == 0)
            return;

        if (pid == traced)
            Write(pid, LoopService, report);
        else
            Write(0, LoopServiceOther, report);
    }

    private static void Write(int pid, int code, int arg)
    {
        var timestamps = s_timestamps;
        var i = Interlocked.Increment(ref s_count) - 1;
        if ((uint)i >= (uint)timestamps.Length)
            return;

        s_pids[i] = pid;
        s_codes[i] = code;
        s_args[i] = arg;
        s_threads[i] = Environment.CurrentManagedThreadId;
        Volatile.Write(ref timestamps[i], Stopwatch.GetTimestamp());
    }

    /// <summary>The events recorded, in the order their slots were taken, and whether the buffer filled up.</summary>
    internal static (long[] Timestamps, int[] Pids, int[] Codes, int[] Args, int[] Threads, int Count, bool Full) Snapshot()
    {
        var count = Volatile.Read(ref s_count);
        var full = count >= s_timestamps.Length;
        return (s_timestamps, s_pids, s_codes, s_args, s_threads, Math.Min(count, s_timestamps.Length), full);
    }

    /// <summary>A short name for <paramref name="code"/>, test codes included.</summary>
    internal static string Name(int code) => code switch
    {
        InputWritten => "InputWritten",
        InputFlushed => "InputFlushed",
        ConsumerTry => "ConsumerTry",
        ConsumerTurnBusy => "ConsumerTurnBusy",
        ConsumerRead => "ConsumerRead",
        ConsumerLeftToParkedLoop => "ConsumerLeftToParkedLoop",
        ConsumerWaited => "ConsumerWaited",
        ConsumerParks => "ConsumerParks",
        ConsumerArm => "ConsumerArm",
        LoopWaitBegin => "LoopWaitBegin",
        LoopWaitEnd => "LoopWaitEnd",
        LoopService => "LoopService",
        LoopServiceOther => "LoopServiceOther",
        LoopTurnBusy => "LoopTurnBusy",
        LoopRead => "LoopRead",
        LoopArm => "LoopArm",
        LoopArmSkipped => "LoopArmSkipped",
        LoopTakeOver => "LoopTakeOver",
        LoopResumeInline => "LoopResumeInline",
        LoopRequest => "LoopRequest",
        _ => "code " + code,
    };
}
