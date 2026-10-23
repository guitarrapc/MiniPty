using System.Text;

namespace MiniPty.Internal;

/// <summary>
/// Append-only buffer of fixed-size segments from <see cref="PtySegmentPool{T}"/>: what one-shot sinks collect output
/// and decoded text in.
/// </summary>
/// <remarks>
/// <para>
/// Growing never copies what was already written, so the work of a write is proportional to the write wherever it runs:
/// on a reader thread, a pool worker, or a loop shared by every session. The single result array is built once, by
/// <see cref="ToArray"/>, on the thread that takes the result; until then the elements stay in their segments.
/// </para>
/// <para>
/// A mutable struct of one reference and a length, so a sink holds it inline and pays no object for it; the segment
/// table is pooled with the segments. Keep it in a field or a local and pass it by reference: a copy shares the
/// original's segments, and either one's release would hand them back to the pool under the other.
/// </para>
/// <para>
/// Every segment but the last is full, so element <c>i</c> lives in segment <c>i / SegmentLength</c>. Table entries are
/// filled from the start, so the first empty entry ends the list.
/// </para>
/// </remarks>
internal struct PtySegmentedBuffer<T> where T : unmanaged
{
    // Rented on the first write and returned with the segments; null while the buffer holds none.
    private T[]?[]? _table;
    private int _length;

    /// <summary>Elements written.</summary>
    internal readonly int Length => _length;

    /// <summary>
    /// The writable rest of the last segment, at least one element long; a new segment when the last one is full.
    /// Follow a write into it with <see cref="Advance"/>.
    /// </summary>
    internal Span<T> GetSpan()
    {
        var segmentLength = PtySegmentPool<T>.SegmentLength;
        var index = _length / segmentLength;
        var table = _table ??= PtySegmentPool<T>.RentTable();
        if (index == table.Length)
            table = GrowTable(table);

        var segment = table[index] ??= PtySegmentPool<T>.Rent();
        return segment.AsSpan(_length - index * segmentLength);
    }

    /// <summary>Counts <paramref name="count"/> elements written into the span <see cref="GetSpan"/> returned.</summary>
    internal void Advance(int count) => _length += count;

    internal void Append(ReadOnlySpan<T> data)
    {
        while (!data.IsEmpty)
        {
            var span = GetSpan();
            var take = Math.Min(span.Length, data.Length);
            data[..take].CopyTo(span);
            _length += take;
            data = data[take..];
        }
    }

    /// <summary>
    /// Copies everything written into one array of exactly <see cref="Length"/> elements, then returns the segments to
    /// the pool. The buffer is empty afterwards.
    /// </summary>
    internal T[] ToArray()
    {
        if (_length == 0)
        {
            Release();
            return [];
        }

        var result = GC.AllocateUninitializedArray<T>(_length);
        var table = _table!;
        var segmentLength = PtySegmentPool<T>.SegmentLength;
        var copied = 0;
        for (var i = 0; copied < _length; i++)
        {
            var take = Math.Min(segmentLength, _length - copied);
            table[i]!.AsSpan(0, take).CopyTo(result.AsSpan(copied));
            copied += take;
        }

        Release();
        return result;
    }

    /// <summary>Returns the segments and the table to the pool and empties the buffer. Safe to repeat.</summary>
    internal void Release()
    {
        var table = _table;
        _table = null;
        _length = 0;
        if (table is null)
            return;

        for (var i = 0; i < table.Length && table[i] is { } segment; i++)
        {
            PtySegmentPool<T>.Return(segment);
            table[i] = null;
        }

        PtySegmentPool<T>.ReturnTable(table);
    }

    // Output past what a pooled table lists: a larger table, which the pool does not keep, replaces it.
    private T[]?[] GrowTable(T[]?[] table)
    {
        var larger = new T[]?[table.Length * 2];
        table.AsSpan().CopyTo(larger);
        table.AsSpan().Clear();
        PtySegmentPool<T>.ReturnTable(table);
        _table = larger;
        return larger;
    }
}

/// <summary>Decoding into a <see cref="PtySegmentedBuffer{T}"/> of characters, without a scratch buffer.</summary>
internal static class PtyTextDecoding
{
    /// <summary>Largest stack buffer the last few elements of a segment are decoded through.</summary>
    private const int StackChars = 64;

    /// <summary>
    /// Decodes <paramref name="bytes"/> straight into <paramref name="chars"/>, across as many segments as the text
    /// needs. Any number of bytes is accepted, and a character split between calls decodes whole once its last byte
    /// arrives; <paramref name="flush"/> ends the stream.
    /// </summary>
    /// <remarks>
    /// <see cref="Decoder.Convert(ReadOnlySpan{byte}, Span{char}, bool, out int, out int, out bool)"/> converts as much
    /// as fits and refuses a buffer that cannot hold the characters of a single byte (two for UTF-8: a surrogate pair).
    /// When the rest of a segment is shorter than that, the next characters go through a stack buffer and are appended
    /// across the segment boundary, so every segment but the last stays full.
    /// </remarks>
    internal static void Decode(Decoder decoder, Encoding encoding, ReadOnlySpan<byte> bytes, bool flush, ref PtySegmentedBuffer<char> chars)
    {
        if (bytes.IsEmpty && !flush)
            return;

        var minimumChars = encoding.GetMaxCharCount(1);
        Span<char> small = minimumChars <= StackChars ? stackalloc char[StackChars] : new char[minimumChars];
        bool completed;
        do
        {
            var span = chars.GetSpan();
            int bytesUsed;
            int charsUsed;
            if (span.Length >= minimumChars)
            {
                decoder.Convert(bytes, span, flush, out bytesUsed, out charsUsed, out completed);
                chars.Advance(charsUsed);
            }
            else
            {
                decoder.Convert(bytes, small, flush, out bytesUsed, out charsUsed, out completed);
                chars.Append(small[..charsUsed]);
            }

            bytes = bytes[bytesUsed..];
            if (bytesUsed == 0 && charsUsed == 0 && !completed)
                throw new InvalidOperationException("The decoder made no progress.");
        }
        while (!completed || !bytes.IsEmpty);
    }
}
