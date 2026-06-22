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
    /// Replays per-read byte chunks through a streaming decoder (allocates a new char buffer every call).
    /// </summary>
    internal static PtyCaptureTextChunk[] DecodeFromByteChunks(
        IReadOnlyList<PtyCaptureChunk> byteChunks,
        Encoding encoding)
    {
        if (byteChunks.Count == 0)
            return [];

        using var chars = PtyReadBuffer.RentChars(encoding);
        var decoder = encoding.GetDecoder();
        using var textBuffer = new PtyGrowingBuffer<char>();
        var meta = new List<TextChunkMeta>(byteChunks.Count);

        for (var i = 0; i < byteChunks.Count; i++)
        {
            var data = byteChunks[i].Data.Span;
            if (data.IsEmpty)
                continue;

            var charCount = decoder.GetChars(data, chars.Span, flush: false);
            if (charCount <= 0)
                continue;

            meta.Add(new TextChunkMeta(byteChunks[i].Time, textBuffer.Length, charCount));
            textBuffer.Append(chars.Span[..charCount]);
        }

        var trailing = decoder.GetChars(ReadOnlySpan<byte>.Empty, chars.Span, flush: true);
        if (trailing > 0)
        {
            meta.Add(new TextChunkMeta(byteChunks[^1].Time, textBuffer.Length, trailing));
            textBuffer.Append(chars.Span[..trailing]);
        }

        var outputChars = textBuffer.ToArray();
        var chunks = new PtyCaptureTextChunk[meta.Count];
        for (var i = 0; i < meta.Count; i++)
        {
            var item = meta[i];
            chunks[i] = new PtyCaptureTextChunk(item.Time, outputChars.AsMemory(item.Start, item.Length));
        }

        return chunks;
    }
}
