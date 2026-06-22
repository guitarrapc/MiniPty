namespace MiniPty.Capture;

internal sealed class PtyCapturePumpResult(string output, PtyCaptureChunk[] chunks)
{
    internal string Output { get; } = output;

    internal IReadOnlyList<PtyCaptureChunk> Chunks { get; } = chunks;
}
