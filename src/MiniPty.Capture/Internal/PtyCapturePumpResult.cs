namespace MiniPty.Capture;

internal sealed class PtyCapturePumpResult(
    byte[] outputBytes,
    char[] outputChars,
    PtyCaptureByteChunk[] byteChunks,
    PtyCaptureChunk[] textChunks)
{
    internal ReadOnlyMemory<byte> OutputBytes => outputBytes;

    internal ReadOnlyMemory<char> Output => outputChars;

    internal IReadOnlyList<PtyCaptureByteChunk> ByteChunks => byteChunks;

    internal IReadOnlyList<PtyCaptureChunk> Chunks => textChunks;
}
