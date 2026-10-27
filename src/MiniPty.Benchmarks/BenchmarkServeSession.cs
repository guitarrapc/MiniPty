using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace MiniPty.Benchmarks;

/// <summary>
/// One long-lived session on <c>MiniPty.Benchmarks.Child --serve</c> for the steady-state benchmarks: the child is
/// spawned and ready before the first operation, so an operation measures only input, output, and the terminal.
/// </summary>
/// <remarks>
/// The protocol (ready marker, flood triggers, alternating end markers, echo into column 0) is described in the
/// child's <c>Serve.cs</c>; the constants here must match it. Output is read through one
/// <see cref="PtySession.ReadOutputAsync"/> enumeration that lives as long as the session, as a terminal frontend
/// reads it. Bytes after a marker in the same chunk are dropped; the protocol sends nothing after a marker that an
/// operation needs.
/// </remarks>
internal sealed class BenchmarkServeSession : IDisposable
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

    // What a nudge writes: a byte the child echoes into column 0 of the empty line below the end marker.
    private static readonly byte[] Nudge = [(byte)'%'];

    private readonly PtySession session;
    private readonly IAsyncEnumerator<PtyOutputChunk> output;
    private readonly Timer watchdog;
    private volatile bool stalled;
    private volatile bool nudgeArmed;

    // The last output bytes, for the stall report: what the terminal sent instead of the marker.
    private readonly byte[] tail = new byte[512];
    private int tailLength;

    private BenchmarkServeSession(PtySession session)
    {
        this.session = session;
        output = session.ReadOutputAsync().GetAsyncEnumerator();
        watchdog = new Timer(static state => ((BenchmarkServeSession)state!).OnWatchdog(), this, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Nudges a flood whose marker is overdue once, and fails the operation if the marker still does not arrive.
    /// </summary>
    /// <remarks>
    /// The in-box ConPTY occasionally sends nothing of a flood the child has finished writing until the screen changes
    /// again: with the child's own log showing the flood written, MiniPty had a zero-byte read posted, nothing unread,
    /// and nothing in the output pipe, and another input made the whole flood arrive at once. That happened about once
    /// in 5 to 10 runs of this class, and never on the out-of-band ConPTY in 15 runs. A nudge changes the screen, so
    /// the operation completes; it is reported on the console, and its time is an outlier that the summary drops.
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
            _ = session.WriteInputAsync(Nudge).AsTask();
            watchdog.Change(StallTimeout, Timeout.InfiniteTimeSpan);
            return;
        }

        stalled = true;
        session.Dispose();
    }

    /// <summary>Starts the child and waits until it has finished its setup and switched its input to raw.</summary>
    internal static BenchmarkServeSession Start(PtyStartInfo startInfo)
    {
        var serve = new BenchmarkServeSession(Pty.Start(startInfo));
        try
        {
            if (!serve.RoundTripAsync(ReadOnlyMemory<byte>.Empty, Ready).AsTask().Wait(TimeSpan.FromSeconds(30)))
                throw new TimeoutException("The serve child did not report ready within 30 seconds.");
        }
        catch
        {
            serve.Dispose();
            throw;
        }

        return serve;
    }

    /// <summary>
    /// Writes <paramref name="input"/>, then reads output until <paramref name="marker"/> has arrived; returns the
    /// bytes read, marker included.
    /// </summary>
    /// <remarks>
    /// The write and the read are one async method, which a benchmark returns as its own result, so a whole operation
    /// rents one pooled state-machine box on the thread that starts it. BenchmarkDotNet waits for an operation on that
    /// same thread, so the box goes back to that thread's cache and the next operation reuses it. An async read nested
    /// in an async operation completed on a thread-pool thread instead, which kept its box: on Linux every operation
    /// then allocated a new one (144 B per keystroke) that looked like MiniPty's cost.
    /// </remarks>
    /// <exception cref="EndOfStreamException">Output ended before the marker.</exception>
    /// <exception cref="TimeoutException">The marker did not arrive within <see cref="StallTimeout"/>.</exception>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    internal async ValueTask<long> RoundTripAsync(ReadOnlyMemory<byte> input, byte[] marker)
    {
        long total = 0;
        var matched = 0;

        // Only a flood is nudged: its end marker sits above the line a nudge's echo lands on. A nudge during a keystroke
        // would overwrite the very echo the operation waits for.
        var flood = ReferenceEquals(marker, EndA) || ReferenceEquals(marker, EndB);
        nudgeArmed = flood;
        watchdog.Change(flood ? NudgeAfter : StallTimeout, Timeout.InfiniteTimeSpan);
        try
        {
            if (!input.IsEmpty)
                await session.WriteInputAsync(input).ConfigureAwait(false);

            while (await output.MoveNextAsync().ConfigureAwait(false))
            {
                var data = output.Current.Data;
                total += data.Length;
                KeepTail(data.Span);
                if (Scan(data.Span, marker, ref matched))
                {
                    watchdog.Change(Timeout.Infinite, Timeout.Infinite);
                    nudgeArmed = false;
                    return total;
                }
            }
        }
        catch (ObjectDisposedException) when (stalled)
        {
            throw new TimeoutException(
                $"The serve child's output did not reach the marker \"{Encoding.ASCII.GetString(marker)}\" within " +
                $"{StallTimeout.TotalSeconds} seconds; {total} bytes arrived in this operation, ending with: {DescribeTail()}");
        }

        throw new EndOfStreamException("The serve child's output ended before the expected marker.");
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
        session.Dispose();
        try
        {
            output.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The enumeration ends with the disposed session; nothing is left to clean up.
        }
    }
}
