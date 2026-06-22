using System.Text;
using MiniPty.Internal;

namespace MiniPty.Capture;

internal sealed class PtyCapturePumpResult(
    byte[] outputBytes,
    char[]? outputChars,
    Encoding encoding,
    PtyCaptureChunk[] chunks,
    PtyCaptureTextChunk[]? textChunks)
{
    internal PtyPumpPayload ToPayload() => new(outputBytes, outputChars, encoding);

    internal PtyCaptureChunk[] Chunks { get; } = chunks;

    internal PtyCaptureTextChunk[]? TextChunks { get; } = textChunks;
}
