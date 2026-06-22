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

    internal static IReadOnlyList<PtyCaptureChunk> ChunkedAnsi(int chunkCount, int charsPerChunk)
    {
        var chunks = new PtyCaptureChunk[chunkCount];
        for (var i = 0; i < chunkCount; i++)
        {
            var piece = $"{Red}chunk-{i,4}{Reset}".PadRight(charsPerChunk, 'x');
            chunks[i] = new PtyCaptureChunk(TimeSpan.FromMilliseconds(i), piece);
        }

        return chunks;
    }
}
