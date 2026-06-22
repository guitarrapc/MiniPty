using System.Buffers;

namespace MiniPty.Internal;

internal sealed class PtyGrowingBuffer<T> : IDisposable where T : struct
{
    private T[] buffer = [];
    private int length;

    internal int Length => length;

    internal ReadOnlySpan<T> WrittenSpan => buffer.AsSpan(0, length);

    internal void Append(ReadOnlySpan<T> data)
    {
        if (data.IsEmpty)
            return;

        EnsureCapacity(length + data.Length);
        data.CopyTo(buffer.AsSpan(length));
        length += data.Length;
    }

    internal T[] ToArray()
    {
        if (length == 0)
            return [];

        var result = new T[length];
        buffer.AsSpan(0, length).CopyTo(result);
        return result;
    }

    public void Dispose()
    {
        if (buffer.Length > 0)
        {
            ArrayPool<T>.Shared.Return(buffer);
            buffer = [];
        }

        length = 0;
    }

    private void EnsureCapacity(int required)
    {
        if (required <= buffer.Length)
            return;

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
