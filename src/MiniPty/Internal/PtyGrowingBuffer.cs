using System.Buffers;

namespace MiniPty.Internal;

/// <summary>
/// Growable write buffer backed by <see cref="ArrayPool{T}"/> during I/O.
/// </summary>
/// <remarks>
/// Call <see cref="Detach"/> once to take ownership of the final array. When the rented capacity
/// exactly matches the written length, the pool array is returned without an extra copy.
/// Otherwise a right-sized array is allocated and the pool buffer is returned.
/// </remarks>
internal sealed class PtyGrowingBuffer<T> : IDisposable where T : struct
{
    private T[] buffer = [];
    private int length;

    internal int Length => length;

    internal ReadOnlySpan<T> WrittenSpan => buffer.AsSpan(0, length);

    internal void EnsureCapacity(int required)
    {
        if (required <= buffer.Length)
            return;

        Grow(required);
    }

    internal void Append(ReadOnlySpan<T> data)
    {
        if (data.IsEmpty)
            return;

        GrowIfNeeded(length + data.Length);
        data.CopyTo(buffer.AsSpan(length));
        length += data.Length;
    }

    /// <summary>
    /// Transfers written content to a caller-owned array and releases any pooled storage.
    /// </summary>
    internal T[] Detach()
    {
        if (length == 0)
        {
            ReleaseBuffer();
            return [];
        }

        if (buffer.Length == length)
        {
            var exact = buffer;
            buffer = [];
            length = 0;
            return exact;
        }

        var trimmed = GC.AllocateUninitializedArray<T>(length);
        buffer.AsSpan(0, length).CopyTo(trimmed);
        ReleaseBuffer();
        length = 0;
        return trimmed;
    }

    public void Dispose() => ReleaseBuffer();

    private void ReleaseBuffer()
    {
        if (buffer.Length > 0)
        {
            ArrayPool<T>.Shared.Return(buffer);
            buffer = [];
        }

        length = 0;
    }

    private void GrowIfNeeded(int required)
    {
        if (required <= buffer.Length)
            return;

        Grow(required);
    }

    private void Grow(int required)
    {
        var next = buffer.Length == 0 ? PtyReadBuffer.Size : buffer.Length * 2;
        while (next < required)
            next *= 2;

        var rented = ArrayPool<T>.Shared.Rent(next);
        if (length > 0)
            buffer.AsSpan(0, length).CopyTo(rented);

        if (buffer.Length > 0)
            ArrayPool<T>.Shared.Return(buffer);

        buffer = rented;
    }
}
