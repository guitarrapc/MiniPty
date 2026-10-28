using System.Text;
using TerminalEmulator;

namespace MiniPty.Tests;

public sealed class TuiDemoRendererTests
{
    [Test]
    public async Task SelectionUpdate_PreservesArtworkAndOnlyRepaintsAffectedBoxes()
    {
        using var output = new StringWriter();
        var renderer = new DemoRenderer(output);
        var screen = new ScreenBuffer(80, 24);
        var parser = new VtParser(screen);
        var view = new DemoView(80, 24, 0, false, 0, 0);
        renderer.Draw(view);
        var initial = output.ToString();
        parser.Feed(Encoding.UTF8.GetBytes(initial));
        var sky = screen.ViewLine(11).Cells[36].Attributes.Background;

        output.GetStringBuilder().Clear();
        renderer.Draw(view with { Selected = 1 });
        var update = output.ToString();
        parser.Feed(Encoding.UTF8.GetBytes(update));

        await Assert.That(update.Contains("\u001b[2J", StringComparison.Ordinal)).IsFalse();
        await Assert.That(update.Contains("Terminal resize", StringComparison.Ordinal)).IsFalse();
        await Assert.That(update.Contains("Block-art", StringComparison.Ordinal)).IsFalse();
        await Assert.That(Encoding.UTF8.GetByteCount(update)).IsLessThan(Encoding.UTF8.GetByteCount(initial) / 2);
        await Assert.That(screen.ViewLine(7).ToText().Contains("Click to select", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(6).Cells[0].Attributes.Background.Kind).IsEqualTo(ColorKind.Default);
        await Assert.That(screen.ViewLine(6).Cells[24].Attributes.Background.Kind).IsNotEqualTo(ColorKind.Default);
        await Assert.That(screen.ViewLine(11).Cells[36].Attributes.Background).IsEqualTo(sky);
        await Assert.That(screen.ViewLine(16).ToText()).IsEqualTo("Selected: 2/3   Toggle: OFF      [Toggle]");
    }

    [Test]
    public async Task ShorterMouseCoordinatesAndToggle_OverwriteOldTextWithoutRedrawingDiagram()
    {
        using var output = new StringWriter();
        var renderer = new DemoRenderer(output);
        var screen = new ScreenBuffer(100, 30);
        var parser = new VtParser(screen);
        var view = new DemoView(100, 30, 0, false, 100, 30);
        renderer.Draw(view);
        parser.Feed(Encoding.UTF8.GetBytes(output.ToString()));

        output.GetStringBuilder().Clear();
        renderer.Draw(view with { Enabled = true, MouseColumn = 2, MouseRow = 7 });
        var update = output.ToString();
        parser.Feed(Encoding.UTF8.GetBytes(update));

        await Assert.That(update.Contains("\u001b[2J", StringComparison.Ordinal)).IsFalse();
        await Assert.That(update.Contains("transport", StringComparison.Ordinal)).IsFalse();
        await Assert.That(screen.ViewLine(17).ToText()).IsEqualTo("Mouse: 2,7");
        await Assert.That(screen.ViewLine(16).ToText()).IsEqualTo("Selected: 1/3   Toggle: ON       [Toggle]");
        await Assert.That(screen.ViewLine(16).Cells[33].Attributes.Background.Kind).IsNotEqualTo(ColorKind.Default);
    }

    [Test]
    public async Task UnchangedView_ProducesNoOutput()
    {
        using var output = new StringWriter();
        var renderer = new DemoRenderer(output);
        var view = new DemoView(80, 24, 0, false, 0, 0);
        renderer.Draw(view);
        output.GetStringBuilder().Clear();
        renderer.Draw(view);
        await Assert.That(output.ToString()).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task NormalResize_OnlyUpdatesSizeLabelAndErasesTrailingDigits()
    {
        using var output = new StringWriter();
        var renderer = new DemoRenderer(output);
        var screen = new ScreenBuffer(100, 30);
        var parser = new VtParser(screen);
        var view = new DemoView(100, 30, 0, false, 0, 0);
        renderer.Draw(view);
        parser.Feed(Encoding.UTF8.GetBytes(output.ToString()));

        output.GetStringBuilder().Clear();
        screen.Resize(80, 24);
        renderer.Draw(view with { Width = 80, Height = 24 });
        var update = output.ToString();
        parser.Feed(Encoding.UTF8.GetBytes(update));

        await Assert.That(update.Contains("\u001b[2J", StringComparison.Ordinal)).IsFalse();
        await Assert.That(update.Contains("MiniPty TUI demo", StringComparison.Ordinal)).IsFalse();
        await Assert.That(screen.ViewLine(2).ToText()).IsEqualTo("Size: 80 x 24");
        await Assert.That(screen.ViewLine(0).ToText()).IsEqualTo("MiniPty TUI demo");
        await Assert.That(screen.ViewLine(11).Cells[36].Attributes.Background.Kind).IsNotEqualTo(ColorKind.Default);
    }

    [Test]
    public async Task CompactTransition_ClearsOldLayoutAndRestoresItOnGrowth()
    {
        using var output = new StringWriter();
        var renderer = new DemoRenderer(output);
        var screen = new ScreenBuffer(80, 24);
        var parser = new VtParser(screen);
        var view = new DemoView(80, 24, 2, true, 50, 7);
        renderer.Draw(view);
        parser.Feed(Encoding.UTF8.GetBytes(output.ToString()));

        output.GetStringBuilder().Clear();
        screen.Resize(40, 10);
        renderer.Draw(view with { Width = 40, Height = 10 });
        parser.Feed(Encoding.UTF8.GetBytes(output.ToString()));
        await Assert.That(screen.ViewLine(0).ToText()).IsEqualTo("Resize to at least 72 x 22 (Q quits).");
        await Assert.That(screen.ViewLine(5).ToText()).IsEqualTo(string.Empty);

        output.GetStringBuilder().Clear();
        screen.Resize(80, 24);
        renderer.Draw(view);
        parser.Feed(Encoding.UTF8.GetBytes(output.ToString()));
        await Assert.That(screen.ViewLine(0).ToText()).IsEqualTo("MiniPty TUI demo");
        await Assert.That(screen.ViewLine(16).ToText()).IsEqualTo("Selected: 3/3   Toggle: ON       [Toggle]");
        await Assert.That(screen.ViewLine(6).Cells[48].Attributes.Background.Kind).IsNotEqualTo(ColorKind.Default);
    }
}
