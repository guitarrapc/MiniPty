using System.Text;
using TerminalEmulator;

namespace MiniPty.Tests;

/// <summary>
/// Checks the TuiDemo dashboard's emitted output before any PTY: what it draws, and that ticks and interactions
/// overwrite only the cells that changed. ConPTY may add its own repaint operations, so the smoke covers the PTY.
/// </summary>
public sealed class TuiDemoRendererTests
{
    private static readonly DemoView Initial = new(80, 24, 0, false, false, 0, 0, 0);

    [Test]
    public async Task InitialFrame_DrawsColoredDashboard()
    {
        var (_, screen, _, _) = Start(Initial);

        await Assert.That(screen.ViewLine(0).ToText().Contains("MiniPty TUI demo", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(0).ToText().Contains("Size: 80 x 24", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(0).Cells[0].Attributes.Background.Kind).IsNotEqualTo(ColorKind.Default);
        await Assert.That(screen.ViewLine(1).ToText().StartsWith("Unicode: 日本語 / café", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(1).Cells[32].Attributes.Background.Kind).IsEqualTo(ColorKind.Rgb);
        await Assert.That(screen.ViewLine(1).Cells[32].Attributes.Background).IsNotEqualTo(screen.ViewLine(1).Cells[71].Attributes.Background);
        await Assert.That(screen.ViewLine(2).ToText()).IsEqualTo("┌ Spinners ──────────┐ ┌ Jobs ─────────────────────────────────────────┐");
        await Assert.That(screen.ViewLine(3).ToText().Contains("braille", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(3).ToText().Contains(">⠋ Build", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(4).ToText().Contains(" . Test", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(4).ToText().Contains("  0% queued", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(3).Cells[30].Attributes.Background.Kind).IsNotEqualTo(ColorKind.Default);
        await Assert.That(screen.ViewLine(4).Cells[30].Attributes.Background.Kind).IsEqualTo(ColorKind.Default);
        await Assert.That(screen.ViewLine(7).ToText().Contains("[ Pause  ]  [ Details ]", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(17).ToText().Contains("00:00  Build   started (run #1)", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(19).ToText()).IsEqualTo("Selected: 1/3   Paused: no      Mouse: 0,0");
    }

    [Test]
    public async Task AnimationTick_RewritesOnlyAnimatedCells()
    {
        var (renderer, screen, parser, output) = Start(Initial);
        var initialBytes = Encoding.UTF8.GetByteCount(output.ToString());
        var spinner = screen.ViewLine(3).Cells[2].Text;

        var update = Draw(renderer, parser, output, Initial with { Frame = 1 });

        await Assert.That(update.Contains("\u001b[2J", StringComparison.Ordinal)).IsFalse();
        await Assert.That(update.Contains("Spinners", StringComparison.Ordinal)).IsFalse();
        await Assert.That(update.Contains("Unicode", StringComparison.Ordinal)).IsFalse();
        await Assert.That(update.Contains("Details", StringComparison.Ordinal)).IsFalse();
        await Assert.That(Encoding.UTF8.GetByteCount(update)).IsLessThan(initialBytes / 4);
        await Assert.That(screen.ViewLine(3).Cells[2].Text).IsNotEqualTo(spinner);
        await Assert.That(screen.ViewLine(2).ToText().StartsWith("┌ Spinners", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task LogEntry_ScrollsThePreviousEntryUp()
    {
        var (renderer, screen, parser, output) = Start(Initial);
        var updates = new StringBuilder();
        for (var frame = 1; frame <= 15; frame++)
            updates.Append(Draw(renderer, parser, output, Initial with { Frame = frame }));

        // Build starts at frame 0 and Test at frame 15; Test's line enters at the bottom and pushes Build's up.
        await Assert.That(updates.ToString().Contains("\u001b[2J", StringComparison.Ordinal)).IsFalse();
        await Assert.That(updates.ToString().Contains("\u001b[12;18r", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(16).ToText().Contains("00:00  Build   started (run #1)", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(17).ToText().Contains("00:01  Test    started (run #1)", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(18).ToText().StartsWith("└──", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(10).ToText().StartsWith("┌ Log ", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task SelectionUpdate_MovesHighlightWithoutRedrawingStatic()
    {
        var (renderer, screen, parser, output) = Start(Initial);
        var initialBytes = Encoding.UTF8.GetByteCount(output.ToString());

        var update = Draw(renderer, parser, output, Initial with { Selected = 2 });

        await Assert.That(update.Contains("\u001b[2J", StringComparison.Ordinal)).IsFalse();
        await Assert.That(update.Contains("Spinners", StringComparison.Ordinal)).IsFalse();
        await Assert.That(update.Contains("Unicode", StringComparison.Ordinal)).IsFalse();
        await Assert.That(Encoding.UTF8.GetByteCount(update)).IsLessThan(initialBytes / 2);
        await Assert.That(screen.ViewLine(3).Cells[30].Attributes.Background.Kind).IsEqualTo(ColorKind.Default);
        await Assert.That(screen.ViewLine(5).Cells[30].Attributes.Background.Kind).IsNotEqualTo(ColorKind.Default);
        await Assert.That(screen.ViewLine(19).ToText().StartsWith("Selected: 3/3   Paused: no ", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task Pause_UpdatesStatusAndButtonOnly()
    {
        var (renderer, screen, parser, output) = Start(Initial);

        var update = Draw(renderer, parser, output, Initial with { Paused = true });

        await Assert.That(update.Contains("\u001b[2J", StringComparison.Ordinal)).IsFalse();
        await Assert.That(update.Contains("Build", StringComparison.Ordinal)).IsFalse();
        await Assert.That(screen.ViewLine(7).ToText().Contains("[ Resume ]  [ Details ]", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(19).ToText().StartsWith("Selected: 1/3   Paused: yes", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task Dialog_OpensOverPanelsAndClosesWithoutClearing()
    {
        var view = Initial with { Selected = 1, Frame = 300 };
        var (renderer, screen, parser, output) = Start(view);
        var before = new string[14];
        for (var row = 5; row < 14; row++)
            before[row] = screen.ViewLine(row).ToText();

        var open = Draw(renderer, parser, output, view with { Dialog = true });
        await Assert.That(open.Contains("\u001b[2J", StringComparison.Ordinal)).IsFalse();
        await Assert.That(screen.ViewLine(5).ToText().Contains("┌ Test ─", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(11).ToText().Contains("[ Close ]", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(13).Cells[20].Attributes.Background.Kind).IsNotEqualTo(ColorKind.Default);

        var close = Draw(renderer, parser, output, view);
        await Assert.That(close.Contains("\u001b[2J", StringComparison.Ordinal)).IsFalse();
        for (var row = 5; row < 14; row++)
            await Assert.That(screen.ViewLine(row).ToText()).IsEqualTo(before[row]);
        await Assert.That(screen.ViewLine(13).Cells[20].Attributes.Background.Kind).IsEqualTo(ColorKind.Default);
    }

    [Test]
    public async Task UnchangedView_ProducesNoOutput()
    {
        var (renderer, _, parser, output) = Start(Initial);
        await Assert.That(Draw(renderer, parser, output, Initial)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task ShorterMouseCoordinates_OverwriteOldText()
    {
        var view = Initial with { MouseColumn = 100, MouseRow = 30 };
        var (renderer, screen, parser, output) = Start(view);

        var update = Draw(renderer, parser, output, view with { MouseColumn = 2, MouseRow = 7 });

        await Assert.That(update.Contains("\u001b[2J", StringComparison.Ordinal)).IsFalse();
        await Assert.That(update.Contains("Selected", StringComparison.Ordinal)).IsFalse();
        await Assert.That(screen.ViewLine(19).ToText()).IsEqualTo("Selected: 1/3   Paused: no      Mouse: 2,7");
    }

    [Test]
    public async Task NormalResize_OnlyUpdatesSizeLabelAndErasesTrailingDigits()
    {
        var view = Initial with { Width = 100, Height = 30 };
        var (renderer, screen, parser, output) = Start(view, 100, 30);

        screen.Resize(80, 24);
        var update = Draw(renderer, parser, output, view with { Width = 80, Height = 24 });

        await Assert.That(update.Contains("\u001b[2J", StringComparison.Ordinal)).IsFalse();
        await Assert.That(update.Contains("MiniPty TUI demo", StringComparison.Ordinal)).IsFalse();
        await Assert.That(screen.ViewLine(0).ToText().Contains("Size: 80 x 24  ", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(0).ToText().Contains("MiniPty TUI demo", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task CompactTransition_ClearsOldLayoutAndRestoresItOnGrowth()
    {
        var view = Initial with { Selected = 2, Paused = true, MouseColumn = 50, MouseRow = 7 };
        var (renderer, screen, parser, output) = Start(view);

        screen.Resize(40, 10);
        Draw(renderer, parser, output, view with { Width = 40, Height = 10 });
        await Assert.That(screen.ViewLine(0).ToText()).IsEqualTo("Resize to at least 72 x 22 (Q quits).");
        await Assert.That(screen.ViewLine(1).ToText()).IsEqualTo("Mouse: 50,7");
        await Assert.That(screen.ViewLine(5).ToText()).IsEqualTo(string.Empty);

        screen.Resize(80, 24);
        Draw(renderer, parser, output, view);
        await Assert.That(screen.ViewLine(0).ToText().Contains("MiniPty TUI demo", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(19).ToText().StartsWith("Selected: 3/3   Paused: yes", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(5).Cells[30].Attributes.Background.Kind).IsNotEqualTo(ColorKind.Default);
    }

    [Test]
    public async Task Jobs_LoopThroughQueuedRunningDoneAndFailed()
    {
        await Assert.That(DemoJobs.Get(1, 0)).IsEqualTo(new JobState(JobStatus.Queued, 0, 0));
        await Assert.That(DemoJobs.Get(0, 40)).IsEqualTo(new JobState(JobStatus.Running, 50, 1));
        await Assert.That(DemoJobs.Get(0, 80)).IsEqualTo(new JobState(JobStatus.Done, 100, 1));
        await Assert.That(DemoJobs.Get(0, 100)).IsEqualTo(new JobState(JobStatus.Running, 0, 2));
        // Test fails at 70 % on its second run (start 15, 120 running + 20 held frames per run).
        await Assert.That(DemoJobs.Get(1, 15 + 140 + 84)).IsEqualTo(new JobState(JobStatus.Failed, 70, 2));
        await Assert.That(DemoJobs.TryGetEvent(1, 15 + 140 + 84, out var failed)).IsTrue();
        await Assert.That(failed).IsEqualTo(new DemoLogEntry(15 + 140 + 84, 1, DemoLogKind.Failed, 70));
        await Assert.That(DemoJobs.TryGetEvent(0, 41, out _)).IsFalse();
    }

    private static (DemoRenderer Renderer, ScreenBuffer Screen, VtParser Parser, StringWriter Output) Start(DemoView view, int columns = 80, int rows = 24)
    {
        var output = new StringWriter();
        var renderer = new DemoRenderer(output);
        var screen = new ScreenBuffer(columns, rows);
        var parser = new VtParser(screen);
        renderer.Draw(view);
        parser.Feed(Encoding.UTF8.GetBytes(output.ToString()));
        return (renderer, screen, parser, output);
    }

    /// <summary>Draws <paramref name="view"/>, feeds only this update to the screen, and returns it.</summary>
    private static string Draw(DemoRenderer renderer, VtParser parser, StringWriter output, DemoView view)
    {
        output.GetStringBuilder().Clear();
        renderer.Draw(view);
        var update = output.ToString();
        parser.Feed(Encoding.UTF8.GetBytes(update));
        return update;
    }
}
