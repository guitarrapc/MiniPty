using System.Text;
using MiniPty.Capture;

namespace MiniPty.Benchmarks;

/// <summary>
/// Synthetic PTY output samples for microbenchmarks.
/// </summary>
internal static class BenchmarkSamples
{
    private const string Red = "\u001b[31m";
    private const string Reset = "\u001b[0m";
    private const string Clear = "\u001b[2J";
    private const string Mode = "\u001b[?25l";
    private const string Osc = "\u001b]0;title\u0007";

    internal static string PlainLine => "hello world\n";

    internal static string AnsiLine =>
        $"{Mode}{Clear}{Red}line one{Reset}\r\n{Osc}line two\r";

    internal static string AnsiHeavy(int lineCount)
    {
        var builder = new System.Text.StringBuilder(lineCount * 40);
        for (var line = 0; line < lineCount; line++)
        {
            builder.Append(Clear)
                .Append(Red)
                .Append("row ")
                .Append(line)
                .Append(Reset)
                .Append("\r\n");
        }

        return builder.ToString();
    }

    internal static string PlainHeavy(int byteCount)
    {
        return string.Create(byteCount, byteCount, static (span, count) =>
        {
            for (var i = 0; i < count; i++)
                span[i] = (char)('a' + (i % 26));
        });
    }

    internal static (PtyCaptureChunk[] ByteChunks, PtyCaptureTextChunk[] TextChunks) ChunkedAnsi(
        int chunkCount,
        int charsPerChunk)
    {
        var merged = new System.Text.StringBuilder(chunkCount * charsPerChunk);
        for (var i = 0; i < chunkCount; i++)
        {
            var piece = $"{Red}chunk-{i,4}{Reset}".PadRight(charsPerChunk, 'x');
            merged.Append(piece);
        }

        var text = merged.ToString();
        var bytes = Encoding.UTF8.GetBytes(text);
        var chars = text.ToCharArray();
        var byteChunks = new PtyCaptureChunk[chunkCount];
        var textChunks = new PtyCaptureTextChunk[chunkCount];
        var charOffset = 0;
        for (var i = 0; i < chunkCount; i++)
        {
            // Benchmark samples are ASCII + ANSI escapes (1 UTF-8 byte per char); char and byte offsets align.
            byteChunks[i] = new PtyCaptureChunk(TimeSpan.FromMilliseconds(i), bytes.AsMemory(charOffset, charsPerChunk));
            textChunks[i] = new PtyCaptureTextChunk(TimeSpan.FromMilliseconds(i), chars.AsMemory(charOffset, charsPerChunk));
            charOffset += charsPerChunk;
        }

        return (byteChunks, textChunks);
    }
}
