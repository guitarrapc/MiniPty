using System.Runtime.CompilerServices;

namespace MiniPty.Internal;

/// <summary>
/// Process-wide free lists of the fixed-size segments that one-shot sinks collect output in
/// (<see cref="PtySegmentedBuffer{T}"/>), 16 KiB each, for bytes and for decoded characters, and of the tables that
/// list a buffer's segments.
/// </summary>
/// <remarks>
/// <para>
/// Not <see cref="System.Buffers.ArrayPool{T}.Shared"/>, which caches arrays per thread. A sink is written by whichever
/// thread delivers output (a reader thread today, I/O completions on pool workers or a loop shared by every session
/// once reads hold no thread) and its result is built on the caller's thread, so arrays returned to a per-thread cache
/// stranded there when the thread ended, and the next run allocated its buffers anew. One lock-guarded stack has no
/// thread affinity.
/// </para>
/// <para>
/// At most <see cref="MaxRetained"/> segments of each element type are kept, 1 MiB each, so that one large capture does
/// not hold its memory for the rest of the process. Output beyond what the pool holds allocates its extra segments, and
/// the garbage collector reclaims them; outputs up to 1 MiB, and up to that much output in flight across concurrent
/// one-shot sessions, allocate nothing here once the pool has filled. Tables of <see cref="TableLength"/> entries are
/// kept the same way, one per buffer in flight; a buffer with more segments replaces its table with a larger one that
/// is not kept. The ring buffers of <see cref="PtyBufferPool"/> are a separate list: they are pinned and kept up to the
/// peak number of open sessions.
/// </para>
/// </remarks>
internal static class PtySegmentPool<T> where T : unmanaged
{
    /// <summary>Most segments of this element type the pool keeps.</summary>
    internal const int MaxRetained = 64;

    /// <summary>Entries in a pooled segment table: 256 KiB of output before a buffer needs a larger table.</summary>
    internal const int TableLength = 16;

    private const int MaxRetainedTables = 16;
    private const int SegmentBytes = 16 * 1024;

    private static readonly Lock s_lock = new();
    private static readonly T[]?[] s_free = new T[]?[MaxRetained];
    private static readonly T[]?[]?[] s_freeTables = new T[]?[]?[MaxRetainedTables];
    private static int s_freeCount;
    private static int s_freeTableCount;

    /// <summary>Elements per segment: 16 KiB of them.</summary>
    internal static readonly int SegmentLength = SegmentBytes / Unsafe.SizeOf<T>();

    internal static T[] Rent()
    {
        lock (s_lock)
        {
            if (s_freeCount > 0)
            {
                var segment = s_free[--s_freeCount]!;
                s_free[s_freeCount] = null;
                return segment;
            }
        }

        return GC.AllocateUninitializedArray<T>(SegmentLength);
    }

    /// <summary>Keeps <paramref name="segment"/> for the next rent, or leaves it to the collector once the pool is full.</summary>
    internal static void Return(T[] segment)
    {
        lock (s_lock)
        {
            if (s_freeCount < MaxRetained)
                s_free[s_freeCount++] = segment;
        }
    }

    /// <summary>An empty table of <see cref="TableLength"/> entries.</summary>
    internal static T[]?[] RentTable()
    {
        lock (s_lock)
        {
            if (s_freeTableCount > 0)
            {
                var table = s_freeTables[--s_freeTableCount]!;
                s_freeTables[s_freeTableCount] = null;
                return table;
            }
        }

        return new T[]?[TableLength];
    }

    /// <summary>
    /// Keeps an emptied <paramref name="table"/> for the next rent. Tables of another length, and tables beyond what
    /// the pool keeps, are left to the collector.
    /// </summary>
    internal static void ReturnTable(T[]?[] table)
    {
        if (table.Length != TableLength)
            return;

        lock (s_lock)
        {
            if (s_freeTableCount < MaxRetainedTables)
                s_freeTables[s_freeTableCount++] = table;
        }
    }
}
