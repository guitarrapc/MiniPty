using System.Runtime.CompilerServices;

namespace MiniPty.Benchmarks;

/// <summary>The serve child on MiniPty, read through one enumeration that lives as long as the session.</summary>
/// <remarks>
/// Output is read through one <see cref="PtySession.ReadOutputAsync"/> enumeration, as a terminal frontend reads it.
/// </remarks>
internal sealed class MiniPtyServeSession : BenchmarkServeSession
{
    private readonly PtySession session;
    private readonly IAsyncEnumerator<PtyOutputChunk> output;

    internal MiniPtyServeSession(PtySession session)
    {
        this.session = session;
        output = session.ReadOutputAsync().GetAsyncEnumerator();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The write and the read are one async method, which a benchmark returns as its own result, so a whole operation
    /// rents one pooled state-machine box on the thread that starts it. BenchmarkDotNet waits for an operation on that
    /// same thread, so the box goes back to that thread's cache and the next operation reuses it. An async read nested
    /// in an async operation completed on a thread-pool thread instead, which kept its box: on Linux every operation
    /// then allocated a new one (144 B per keystroke) that looked like MiniPty's cost.
    /// </remarks>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    internal override async ValueTask<long> RoundTripAsync(ReadOnlyMemory<byte> input, byte[] marker)
    {
        long total = 0;
        var matched = 0;
        Arm(marker);
        try
        {
            if (!input.IsEmpty)
                await session.WriteInputAsync(input).ConfigureAwait(false);

            while (await output.MoveNextAsync().ConfigureAwait(false))
            {
                var data = output.Current.Data;
                total += data.Length;
                if (Accept(data.Span, marker, ref matched))
                    return total;
            }
        }
        catch (ObjectDisposedException) when (Stalled)
        {
            throw StallException(marker, total);
        }

        throw new EndOfStreamException("The serve child's output ended before the expected marker.");
    }

    protected override void WriteNudge() => _ = session.WriteInputAsync(Nudge).AsTask();

    protected override void Abort() => session.Dispose();

    protected override void DisposeSession()
    {
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
