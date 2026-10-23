using System.Diagnostics;

namespace MiniPty.Internal;

/// <summary>
/// Process-wide free list of the pinned buffers that hold session output rings.
/// </summary>
/// <remarks>
/// <para>
/// Not <see cref="System.Buffers.ArrayPool{T}.Shared"/>, which caches arrays per thread: an array returned on a thread
/// other than the next renter's stays in the returning thread's cache, and the next rent allocates. A ring is taken
/// when a session starts and given back by whichever party lets go of it last: the reader thread, a consumer, or a
/// disposing thread. Once reads hold no thread, I/O completions and the Unix I/O loop join them. One lock-guarded
/// stack has no thread affinity, so a buffer returned anywhere serves the next session.
/// </para>
/// <para>
/// Buffers are allocated pinned, on the pinned object heap, so an overlapped read can later be posted into one without
/// a pin of its own. Every returned buffer is kept: the list grows to the peak number of rings held at once and then
/// allocates nothing. Whether to trim it is revisited once rings are taken on demand rather than for a session's whole
/// lifetime (Phases 5 and 6 of <c>.github/docs/plans/plan_thread_free_io.md</c>).
/// </para>
/// </remarks>
internal static class PtyBufferPool
{
    internal const int BufferSize = PtyOutputPump.Capacity;

    private static readonly Lock s_lock = new();
    private static byte[]?[] s_free = new byte[]?[8];
    private static int s_freeCount;
    private static int s_rented;

    /// <summary>Buffers rented and not yet returned, for tests that check what idle sessions hold.</summary>
    internal static int RentedCount => Volatile.Read(ref s_rented);

    internal static byte[] Rent()
    {
        Interlocked.Increment(ref s_rented);
        lock (s_lock)
        {
            if (s_freeCount > 0)
            {
                var buffer = s_free[--s_freeCount]!;
                s_free[s_freeCount] = null;
                return buffer;
            }
        }

        return GC.AllocateUninitializedArray<byte>(BufferSize, pinned: true);
    }

    internal static void Return(byte[] buffer)
    {
        Debug.Assert(buffer.Length == BufferSize);
        lock (s_lock)
        {
            if (s_freeCount == s_free.Length)
                Array.Resize(ref s_free, s_freeCount * 2);

            s_free[s_freeCount++] = buffer;
        }

        Interlocked.Decrement(ref s_rented);
    }
}
