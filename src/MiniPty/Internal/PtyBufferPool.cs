using System.Diagnostics;

namespace MiniPty.Internal;

/// <summary>
/// Process-wide free list of the pinned buffers that hold session output rings.
/// </summary>
/// <remarks>
/// <para>
/// Not <see cref="System.Buffers.ArrayPool{T}.Shared"/>, which caches arrays per thread: an array returned on a thread
/// other than the next renter's stays in the returning thread's cache, and the next rent allocates. A ring is taken
/// when output arrives and given back by whichever party lets go of it last: the output driver (a reader thread, or an
/// I/O completion on a pool worker), a consumer, or a disposing thread. One lock-guarded stack has no thread affinity,
/// so a buffer returned anywhere serves the next session.
/// </para>
/// <para>
/// Buffers are allocated pinned, on the pinned object heap, so an overlapped read can be posted into one without a pin
/// of its own. Every returned buffer is kept: the list grows to the peak number of rings held at once and then
/// allocates nothing. That peak is the number of sessions with output unread at the same moment, not the number of open
/// sessions, wherever a driver waits without a buffer (Windows); a reader thread holds its ring for the session's whole
/// output. The list is not trimmed: a peak's worth of 16 KiB buffers is small next to what the sessions behind it cost.
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
