using System.Globalization;
using System.Text;

namespace MiniPty.Benchmarks;

/// <summary>A PTY library that the steady-state benchmarks run the serve child on.</summary>
internal enum ServeLibrary
{
    /// <summary>MiniPty, reading through one long-lived <see cref="PtySession.ReadOutputAsync"/> enumeration.</summary>
    MiniPty,

    /// <summary>Porta.Pty as it starts by default: blocking pipes, so each <c>ReadAsync</c> holds a thread-pool thread.</summary>
    PortaPty,

    /// <summary>Porta.Pty with <c>UseAsyncIo</c>: overlapped pipes on Windows, one shared poll loop on Unix.</summary>
    PortaPtyAsyncIo,
}

/// <summary>
/// One long-lived session on <c>MiniPty.Benchmarks.Child --serve</c> for the steady-state benchmarks: the child is
/// spawned and ready before the first operation, so an operation measures only input, output, and the terminal.
/// </summary>
/// <remarks>
/// <para>
/// The protocol (ready marker, flood triggers, alternating end markers, echo into column 0) is described in the
/// child's <c>Serve.cs</c>; the constants here must match it. Bytes after a marker in the same chunk are dropped; the
/// protocol sends nothing after a marker that an operation needs.
/// </para>
/// <para>
/// Each library has its own subclass whose <see cref="RoundTripAsync"/> writes and reads the way that library's
/// documentation shows, while the marker scan, the stall watchdog, and the output tail are shared here, so the
/// libraries differ only in their own write and read calls.
/// </para>
/// </remarks>
internal abstract class BenchmarkServeSession : IDisposable
{
    internal static readonly byte[] FloodA = [(byte)'g'];
    internal static readonly byte[] FloodB = [(byte)'h'];
    internal static readonly byte[] EndA = "#MINIPTY-END-A#"u8.ToArray();
    // Differs from EndA in every position, so ConPTY's screen diff cannot send part of it (see the child's Serve.cs).
    internal static readonly byte[] EndB = "@minipty_end_b@"u8.ToArray();

    // Echo bytes that no console output sequence or window title is likely to contain, so the only place they can
    // appear is the echo itself.
    internal static readonly byte[] KeyA = [(byte)'%'];
    internal static readonly byte[] KeyB = [(byte)'&'];

    private static readonly byte[] Ready = "#MINIPTY-READY#"u8.ToArray();

    // A flood whose end marker has not arrived after this long is nudged (see OnWatchdog).
    private static readonly TimeSpan NudgeAfter = TimeSpan.FromSeconds(5);

    // A wait for a marker that takes this long means the protocol broke (for example the child never saw its input),
    // so the run fails instead of hanging until the CI job's timeout.
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    /// <summary>What a nudge writes: a byte the child echoes into column 0 of the empty line below the end marker.</summary>
    protected static readonly byte[] Nudge = [(byte)'%'];

    private readonly Timer watchdog;
    private volatile bool stalled;
    private volatile bool nudgeArmed;

    // The last output bytes, for the stall report: what the terminal sent instead of the marker.
    private readonly byte[] tail = new byte[512];
    private int tailLength;

    protected BenchmarkServeSession()
    {
        watchdog = new Timer(static state => ((BenchmarkServeSession)state!).OnWatchdog(), this, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Whether the watchdog gave up and ended the session, which makes the pending read fail.</summary>
    protected bool Stalled => stalled;

    /// <summary>
    /// Starts the serve child on <paramref name="library"/> and waits until it has finished its setup and switched its
    /// input to raw.
    /// </summary>
    /// <param name="library">The PTY library to run the child on.</param>
    /// <param name="floodBytes">Bytes the child writes per flood.</param>
    /// <param name="conPty">The ConPTY implementation on Windows (a <c>BenchmarkPtyCommands.ConPty*</c> value).</param>
    internal static BenchmarkServeSession Start(ServeLibrary library, int floodBytes, string conPty)
    {
        BenchmarkServeSession serve = library == ServeLibrary.MiniPty
            ? new MiniPtyServeSession(Pty.Start(BenchmarkPtyCommands.Serve(floodBytes, conPty)))
            : PortaPtyServeSession.Start(floodBytes, conPty, asyncIo: library == ServeLibrary.PortaPtyAsyncIo);
        try
        {
            if (!serve.RoundTripAsync(ReadOnlyMemory<byte>.Empty, Ready).AsTask().Wait(TimeSpan.FromSeconds(30)))
                throw new TimeoutException("The serve child did not report ready within 30 seconds.");

            if (conPty == BenchmarkPtyCommands.ConPtyOutOfBand && !OutOfBandHostRuns())
            {
                throw new InvalidOperationException(
                    $"{library} reported the out-of-band ConPTY, but no OpenConsole.exe from the benchmark output runs; " +
                    "the session is on the in-box console host.");
            }
        }
        catch
        {
            serve.Dispose();
            throw;
        }

        return serve;
    }

    /// <summary>
    /// Whether an <c>OpenConsole.exe</c> from this benchmark's output directory is running, which proves an
    /// out-of-band session runs on that host: a <c>conpty.dll</c> that cannot find its host falls back to the in-box
    /// conhost without an error, and both libraries would then measure the in-box ConPTY under the out-of-band label.
    /// </summary>
    private static bool OutOfBandHostRuns()
    {
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("OpenConsole"))
        {
            using (process)
            {
                try
                {
                    if (process.MainModule?.FileName.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase) == true)
                        return true;
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // Another user's or an elevated process; not ours.
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Writes <paramref name="input"/>, then reads output until <paramref name="marker"/> has arrived; returns the
    /// bytes read, marker included.
    /// </summary>
    /// <remarks>
    /// An implementation is one pooled async method that a benchmark returns as its own result, and that calls
    /// <see cref="Arm"/> first and <see cref="Accept"/> on every chunk. See <see cref="MiniPtyServeSession"/> for why
    /// it must be a single method.
    /// </remarks>
    /// <exception cref="EndOfStreamException">Output ended before the marker.</exception>
    /// <exception cref="TimeoutException">The marker did not arrive within <see cref="StallTimeout"/>.</exception>
    internal abstract ValueTask<long> RoundTripAsync(ReadOnlyMemory<byte> input, byte[] marker);

    /// <summary>Writes <see cref="Nudge"/> without waiting for it; called from the watchdog's timer thread.</summary>
    protected abstract void WriteNudge();

    /// <summary>Ends the child and the library's session from the watchdog, which makes a pending read complete or fail.</summary>
    protected abstract void Abort();

    /// <summary>Releases the library's session and its reader once no operation is running.</summary>
    protected abstract void DisposeSession();

    /// <summary>Starts the watchdog for one operation that waits for <paramref name="marker"/>.</summary>
    protected void Arm(byte[] marker)
    {
        // Only a flood is nudged: its end marker sits above the line a nudge's echo lands on. A nudge during a keystroke
        // would overwrite the very echo the operation waits for.
        var flood = ReferenceEquals(marker, EndA) || ReferenceEquals(marker, EndB);
        nudgeArmed = flood;
        watchdog.Change(flood ? NudgeAfter : StallTimeout, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Takes one chunk of output: keeps its tail and advances the marker match; true once the marker is complete, which
    /// also stops the watchdog.
    /// </summary>
    protected bool Accept(ReadOnlySpan<byte> data, byte[] marker, ref int matched)
    {
        KeepTail(data);
        if (!Scan(data, marker, ref matched))
            return false;

        watchdog.Change(Timeout.Infinite, Timeout.Infinite);
        nudgeArmed = false;
        return true;
    }

    /// <summary>The error for an operation that the watchdog ended.</summary>
    protected TimeoutException StallException(byte[] marker, long total) =>
        new($"The serve child's output did not reach the marker \"{Encoding.ASCII.GetString(marker)}\" within " +
            $"{StallTimeout.TotalSeconds} seconds; {total} bytes arrived in this operation, ending with: {DescribeTail()}");

    /// <summary>
    /// Nudges a flood whose marker is overdue once, and fails the operation if the marker still does not arrive.
    /// </summary>
    /// <remarks>
    /// The in-box ConPTY occasionally sends nothing of a flood the child has finished writing until the screen changes
    /// again: with the child's own log showing the flood written, MiniPty had a zero-byte read posted, nothing unread,
    /// and nothing in the output pipe, and another input made the whole flood arrive at once. That happened about once
    /// in 5 to 10 runs of the throughput benchmarks, and never on the out-of-band ConPTY in 15 runs. A nudge changes
    /// the screen, so the operation completes; it is reported on the console, and its time is an outlier that the
    /// summary drops.
    /// </remarks>
    private void OnWatchdog()
    {
        if (nudgeArmed)
        {
            nudgeArmed = false;
            System.Console.WriteLine(
                $"// No end marker within {NudgeAfter.TotalSeconds} s of the trigger; nudging the terminal with a keystroke " +
                "(the in-box ConPTY at times withholds a finished flood until its screen changes). " +
                $"Last output: {DescribeTail()}");
            WriteNudge();
            watchdog.Change(StallTimeout, Timeout.InfiniteTimeSpan);
            return;
        }

        stalled = true;
        Abort();
    }

    /// <summary>Keeps the last <c>tail.Length</c> bytes of output; a copy of at most that many bytes per chunk.</summary>
    private void KeepTail(ReadOnlySpan<byte> data)
    {
        if (data.Length >= tail.Length)
        {
            data[^tail.Length..].CopyTo(tail);
            tailLength = tail.Length;
            return;
        }

        var keep = Math.Min(tailLength, tail.Length - data.Length);
        tail.AsSpan(tailLength - keep, keep).CopyTo(tail);
        data.CopyTo(tail.AsSpan(keep));
        tailLength = keep + data.Length;
    }

    private string DescribeTail()
    {
        var builder = new StringBuilder(tailLength * 2);
        foreach (var b in tail.AsSpan(0, tailLength))
        {
            if (b == 0x1b)
                builder.Append("\\e");
            else if (b == (byte)'\r')
                builder.Append("\\r");
            else if (b == (byte)'\n')
                builder.Append("\\n");
            else if (b is < 0x20 or >= 0x7f)
                builder.Append("\\x").Append(b.ToString("x2", CultureInfo.InvariantCulture));
            else
                builder.Append((char)b);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Advances a match of <paramref name="marker"/> across chunk boundaries; <paramref name="matched"/> carries the
    /// matched prefix length from one chunk to the next.
    /// </summary>
    /// <remarks>
    /// While nothing is matched, the scan jumps to the next occurrence of the marker's first byte with a vectorized
    /// search, so a large flood costs little. A mismatch restarts at the current byte, which is exact for markers whose
    /// first byte appears nowhere else but possibly at the end, as for every marker here.
    /// </remarks>
    private static bool Scan(ReadOnlySpan<byte> data, ReadOnlySpan<byte> marker, ref int matched)
    {
        var i = 0;
        while (i < data.Length)
        {
            if (matched == 0)
            {
                var next = data[i..].IndexOf(marker[0]);
                if (next < 0)
                    return false;

                i += next + 1;
                matched = 1;
            }
            else if (data[i] == marker[matched])
            {
                i++;
                matched++;
            }
            else
            {
                matched = 0;
                continue;
            }

            if (matched == marker.Length)
                return true;
        }

        return false;
    }

    public void Dispose()
    {
        watchdog.Dispose();
        DisposeSession();
    }
}
