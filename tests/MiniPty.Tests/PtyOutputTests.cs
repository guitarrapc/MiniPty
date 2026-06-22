using MiniPty;
using MiniPty.Capture;
using TUnit.Assertions;
using TUnit.Core;

namespace MiniPty.Tests;

public sealed class PtyOutputTests
{
    private const string Red = "\u001b[31m";
    private const string Reset = "\u001b[0m";
    private const string Clear = "\u001b[2J";
    private const string Mode = "\u001b[?25l";
    private const string Osc = "\u001b]0;title\u0007";

    [Test]
    public async Task RawReturnsInputUnchanged()
    {
        const string input = $"{Clear}hello{Osc}";
        var output = PtyOutput.ToDisplayText(input, PtyOutputDisplayMode.Raw);
        await Assert.That(output).IsEqualTo(input);
    }

    [Test]
    public async Task PlainTextRemovesControlSequencesAndNormalizesNewlines()
    {
        var input = $"{Mode}{Clear}{Red}line one{Reset}\r\n{Osc}line two\r";
        var output = PtyOutput.ToDisplayText(input, PtyOutputDisplayMode.PlainText);

        await Assert.That(output).IsEqualTo("line one\nline two\n");
        await Assert.That(output.Contains(Red, StringComparison.Ordinal)).IsFalse();
        await Assert.That(output.Contains(Clear, StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task PlainTextRemovesBell()
    {
        var output = PtyOutput.ToDisplayText("a\ab", PtyOutputDisplayMode.PlainText);
        await Assert.That(output).IsEqualTo("ab");
    }

    [Test]
    public async Task AnsiTextKeepsSgrButRemovesLayoutSequences()
    {
        var input = $"{Mode}{Clear}{Red}red{Reset}{Osc}ok";
        var output = PtyOutput.ToDisplayText(input, PtyOutputDisplayMode.AnsiText);

        await Assert.That(output.Contains(Red, StringComparison.Ordinal)).IsTrue();
        await Assert.That(output.Contains(Reset, StringComparison.Ordinal)).IsTrue();
        await Assert.That(output.Contains("red", StringComparison.Ordinal)).IsTrue();
        await Assert.That(output.Contains("ok", StringComparison.Ordinal)).IsTrue();
        await Assert.That(output.Contains(Clear, StringComparison.Ordinal)).IsFalse();
        await Assert.That(output.Contains(Mode, StringComparison.Ordinal)).IsFalse();
        await Assert.That(output.Contains(Osc, StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task CaptureResultToDisplayTextMatchesMergedOutput()
    {
        const string merged = "\u001b[2Jhello\n";
        var chunks = new PtyCaptureChunk[]
        {
            new(TimeSpan.Zero, merged.AsMemory(0, 4)),
            new(TimeSpan.FromMilliseconds(1), merged.AsMemory(4, 6)),
        };

        var result = new PtyCaptureResult(
            System.Text.Encoding.UTF8.GetBytes(merged),
            merged.AsMemory(),
            0,
            [],
            chunks);
        var fromResult = result.ToDisplayText(PtyOutputDisplayMode.PlainText);
        var fromChunks = chunks.ToDisplayText(PtyOutputDisplayMode.PlainText);

        await Assert.That(fromResult).IsEqualTo("hello\n");
        await Assert.That(fromChunks).IsEqualTo("hello\n");
    }
}
