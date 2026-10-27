using System.Runtime.CompilerServices;

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
    internal static readonly byte[] EndB = "#MINIPTY-END-B#"u8.ToArray();

    // Echo bytes that no console output sequence or window title is likely to contain, so the only place they can
    // appear is the echo itself.
    internal static readonly byte[] KeyA = [(byte)'%'];
    internal static readonly byte[] KeyB = [(byte)'&'];

    private static readonly byte[] Ready = "#MINIPTY-READY#"u8.ToArray();

    // A wait for a marker that takes this long means the protocol broke (for example the child never saw its input),
    // so the run fails instead of hanging until the CI job's timeout.
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    private readonly PtySession session;
    private readonly IAsyncEnumerator<PtyOutputChunk> output;
    private readonly Timer watchdog;
    private volatile bool stalled;

    private BenchmarkServeSession(PtySession session)
    {
        this.session = session;
        output = session.ReadOutputAsync().GetAsyncEnumerator();
        watchdog = new Timer(static state => ((BenchmarkServeSession)state!).OnStalled(), this, Timeout.Infinite, Timeout.Infinite);
    }

    private void OnStalled()
    {
        stalled = true;
        session.Dispose();
    }

    /// <summary>Starts the child and waits until it has finished its setup and switched its input to raw.</summary>
    internal static BenchmarkServeSession Start(PtyStartInfo startInfo)
    {
        var serve = new BenchmarkServeSession(Pty.Start(startInfo));
        try
        {
            if (!serve.ReadUntilAsync(Ready).AsTask().Wait(TimeSpan.FromSeconds(30)))
                throw new TimeoutException("The serve child did not report ready within 30 seconds.");
        }
        catch
        {
            serve.Dispose();
            throw;
        }

        return serve;
    }

    internal ValueTask WriteAsync(ReadOnlyMemory<byte> bytes) => session.WriteInputAsync(bytes);

    /// <summary>Reads output until <paramref name="marker"/> has arrived; returns the bytes read, marker included.</summary>
    /// <exception cref="EndOfStreamException">Output ended before the marker.</exception>
    /// <exception cref="TimeoutException">The marker did not arrive within <see cref="StallTimeout"/>.</exception>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    internal async ValueTask<long> ReadUntilAsync(byte[] marker)
    {
        long total = 0;
        var matched = 0;
        watchdog.Change(StallTimeout, Timeout.InfiniteTimeSpan);
        try
        {
            while (await output.MoveNextAsync().ConfigureAwait(false))
            {
                var data = output.Current.Data;
                total += data.Length;
                if (Scan(data.Span, marker, ref matched))
                {
                    watchdog.Change(Timeout.Infinite, Timeout.Infinite);
                    return total;
                }
            }
        }
        catch (ObjectDisposedException) when (stalled)
        {
            throw new TimeoutException($"The serve child's output did not reach the expected marker within {StallTimeout.TotalSeconds} seconds.");
        }

        throw new EndOfStreamException("The serve child's output ended before the expected marker.");
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
