using System.Text;
using MiniPty.Internal;

namespace MiniPty.Capture;

/// <summary>
/// On-demand text chunk decoding for <see cref="PtyCaptureResult.GetTextChunks"/>.
/// </summary>
internal static class PtyCaptureTextDecode
{
    private readonly record struct TextChunkMeta(TimeSpan Time, int Start, int Length);

    /// <summary>
    /// Replays per-read byte chunks through a streaming decoder (allocates a new char buffer every call). A byte chunk
    /// may hold several reads, so the text of one chunk can be any length.
    /// </summary>
    internal static PtyCaptureTextChunk[] DecodeFromByteChunks(
        IReadOnlyList<PtyCaptureChunk> byteChunks,
        Encoding encoding)
    {
        if (byteChunks.Count == 0)
            return [];

        var decoder = encoding.GetDecoder();
        var text = default(PtySegmentedBuffer<char>);
        var meta = new List<TextChunkMeta>(byteChunks.Count);

        for (var i = 0; i < byteChunks.Count; i++)
        {
            var data = byteChunks[i].Data.Span;
            if (data.IsEmpty)
                continue;

            var start = text.Length;
            PtyTextDecoding.Decode(decoder, encoding, data, flush: false, ref text);
            if (text.Length > start)
                meta.Add(new TextChunkMeta(byteChunks[i].Time, start, text.Length - start));
        }

        var trailingStart = text.Length;
        PtyTextDecoding.Decode(decoder, encoding, ReadOnlySpan<byte>.Empty, flush: true, ref text);
        if (text.Length > trailingStart)
            meta.Add(new TextChunkMeta(byteChunks[^1].Time, trailingStart, text.Length - trailingStart));

        var outputChars = text.ToArray();
        var chunks = new PtyCaptureTextChunk[meta.Count];
        for (var i = 0; i < meta.Count; i++)
        {
            var item = meta[i];
            chunks[i] = new PtyCaptureTextChunk(item.Time, outputChars.AsMemory(item.Start, item.Length));
        }

        return chunks;
    }
}
