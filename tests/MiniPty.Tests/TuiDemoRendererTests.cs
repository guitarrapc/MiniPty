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
        await Assert.That(screen.ViewLine(2).ToText()).IsEqualTo("┌ Spinners ──────────┐ ┌ Jobs ─ 00:00 ─────────────────────────────────┐");
        await Assert.That(screen.ViewLine(0).ToText().Contains(" 1 Dashboard  2 Files  3 Charts ", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(0).Cells[20].Attributes.Background).IsNotEqualTo(screen.ViewLine(0).Cells[33].Attributes.Background);
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

    [Test]
    public async Task PageSwitch_RewritesThePageWithoutClearingAndRestoresIt()
    {
        var dashboard = Initial with { Files = FileBrowser.Initial };
        var (renderer, screen, parser, output) = Start(dashboard);
        var before = Rows(screen, 0, 21);

        var files = Draw(renderer, parser, output, dashboard with { Page = DemoLayout.FilesPage });
        await Assert.That(files.Contains("\u001b[2J", StringComparison.Ordinal)).IsFalse();
        await Assert.That(Contains(screen, "Spinners")).IsFalse();
        await Assert.That(screen.ViewLine(2).ToText().StartsWith("┌ Files ", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(19).ToText().StartsWith("Item: 1/20 ", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(20).ToText().StartsWith(" Up/Down/PgUp/PgDn/Home/End: move", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(0).Cells[33].Attributes.Background).IsNotEqualTo(screen.ViewLine(0).Cells[20].Attributes.Background);

        var charts = Draw(renderer, parser, output, dashboard with { Page = DemoLayout.ChartsPage });
        await Assert.That(charts.Contains("\u001b[2J", StringComparison.Ordinal)).IsFalse();
        await Assert.That(Contains(screen, "┌ Files ")).IsFalse();
        await Assert.That(screen.ViewLine(2).ToText().StartsWith("┌ Signals sine mix ", StringComparison.Ordinal)).IsTrue();

        Draw(renderer, parser, output, dashboard);
        var after = Rows(screen, 0, 21);
        for (var row = 0; row < before.Length; row++)
            await Assert.That(after[row]).IsEqualTo(before[row]);
    }

    [Test]
    public async Task FilesCursorMove_RewritesOnlyTheTwoRowsAndTheDetails()
    {
        var view = Initial with { Page = DemoLayout.FilesPage, Files = FileBrowser.Initial };
        var (renderer, screen, parser, output) = Start(view);

        var update = Draw(renderer, parser, output, view with { Files = DemoFiles.Move(view.Files, 1) });

        await Assert.That(update.Contains("\u001b[2J", StringComparison.Ordinal)).IsFalse();
        await Assert.That(update.Contains("PtySession", StringComparison.Ordinal)).IsFalse();
        await Assert.That(update.Contains("┌ Files", StringComparison.Ordinal)).IsFalse();
        await Assert.That(screen.ViewLine(3).Cells[5].Attributes.Background.Kind).IsEqualTo(ColorKind.Default);
        await Assert.That(screen.ViewLine(4).Cells[5].Attributes.Background.Kind).IsNotEqualTo(ColorKind.Default);
        await Assert.That(screen.ViewLine(3).ToText().Contains("Name: .github/ ", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(4).ToText().Contains("Type: folder, 2 items", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(19).ToText().StartsWith("Item: 2/20 ", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task FilesScroll_KeepsTheCursorVisibleAndAlignsWideNames()
    {
        var view = Initial with { Page = DemoLayout.FilesPage, Files = FileBrowser.Initial };
        var (renderer, screen, parser, output) = Start(view);

        var end = DemoFiles.Move(view.Files, DemoFiles.Count);
        var update = Draw(renderer, parser, output, view with { Files = end });

        await Assert.That(end).IsEqualTo(new FileBrowser(19, 5, DemoFiles.InitiallyExpanded));
        await Assert.That(update.Contains("\u001b[2J", StringComparison.Ordinal)).IsFalse();
        await Assert.That(screen.ViewLine(3).ToText().Contains("DemoFiles.cs", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(17).ToText().Contains("global.json", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(17).Cells[5].Attributes.Background.Kind).IsNotEqualTo(ColorKind.Default);
        // 日本語メモ.txt takes 14 cells, 10 for its five wide characters, yet its size ends in the same column as the others.
        await Assert.That(screen.ViewLine(14).ToText().Contains("├─ 日本語メモ.txt", StringComparison.Ordinal)).IsTrue();
        await Assert.That(Cells(screen.ViewLine(14), 32, 6)).IsEqualTo("  412B");
        await Assert.That(Cells(screen.ViewLine(15), 32, 6)).IsEqualTo("   96B");
        await Assert.That(screen.ViewLine(14).Cells[39].Text).IsEqualTo("│");
        await Assert.That(screen.ViewLine(19).ToText().StartsWith("Item: 20/20 ", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task FilesFold_ShowsAndHidesChildrenWithoutClearing()
    {
        var view = Initial with { Page = DemoLayout.FilesPage, Files = DemoFiles.Move(FileBrowser.Initial, 1) };
        var (renderer, screen, parser, output) = Start(view);

        var open = Draw(renderer, parser, output, view with { Files = DemoFiles.Toggle(view.Files) });
        await Assert.That(open.Contains("\u001b[2J", StringComparison.Ordinal)).IsFalse();
        await Assert.That(screen.ViewLine(4).ToText().Contains("├─ - .github/", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(5).ToText().Contains("│  ├─ + workflows/", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(19).ToText().StartsWith("Item: 2/22 ", StringComparison.Ordinal)).IsTrue();

        Draw(renderer, parser, output, view);
        await Assert.That(screen.ViewLine(5).ToText().Contains("├─ - samples/", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(19).ToText().StartsWith("Item: 2/20 ", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task FileTree_VisibleRowsFollowFolding()
    {
        var visible = new int[DemoFiles.Count];
        await Assert.That(DemoFiles.Visible(DemoFiles.InitiallyExpanded, visible)).IsEqualTo(20);
        await Assert.That(DemoFiles.Visible(0, visible)).IsEqualTo(1);
        await Assert.That(DemoFiles.Prefix(9)).IsEqualTo("│  │  ├─ ");
        await Assert.That(DemoFiles.Prefix(12)).IsEqualTo("│     ├─ ");
        await Assert.That(DemoFiles.Prefix(28)).IsEqualTo("└─ ");
        await Assert.That(DemoFiles.NameWidth(25)).IsEqualTo(14);

        // Left on a file goes to its folder; Left again folds it; a click on the selected folder unfolds it.
        var onFile = DemoFiles.Move(FileBrowser.Initial, 5);
        var onFolder = DemoFiles.Collapse(onFile);
        await Assert.That(onFolder.Cursor).IsEqualTo(4);
        var folded = DemoFiles.Collapse(onFolder);
        await Assert.That(DemoFiles.IsExpanded(folded.Expanded, 11)).IsFalse();
        await Assert.That(DemoFiles.Click(folded, 4)).IsEqualTo(onFolder);
        await Assert.That(DemoFiles.Click(folded, 14).Cursor).IsEqualTo(14);
        await Assert.That(DemoFiles.Click(folded, 16)).IsEqualTo(folded);
    }

    [Test]
    public async Task ChartsTick_WritesBrailleAndOnlyChangedCells()
    {
        var view = Initial with { Page = DemoLayout.ChartsPage };
        var (renderer, screen, parser, output) = Start(view);
        var initialBytes = Encoding.UTF8.GetByteCount(output.ToString());
        var before = Rows(screen, 3, 8);

        await Assert.That(CountBraille(screen)).IsGreaterThan(60);
        await Assert.That(screen.ViewLine(17).ToText().Contains("GET   POST  PUT   PATCH DEL", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(13).ToText().Contains("cpu ", StringComparison.Ordinal)).IsTrue();

        var update = Draw(renderer, parser, output, view with { Frame = 1 });
        await Assert.That(update.Contains("\u001b[2J", StringComparison.Ordinal)).IsFalse();
        await Assert.That(update.Contains("Signals", StringComparison.Ordinal)).IsFalse();
        await Assert.That(update.Contains("Requests", StringComparison.Ordinal)).IsFalse();
        await Assert.That(Encoding.UTF8.GetByteCount(update)).IsLessThan(initialBytes);
        await Assert.That(Rows(screen, 3, 8).SequenceEqual(before)).IsFalse();
        await Assert.That(screen.ViewLine(19).ToText().StartsWith("Frame: 1   Paused: no ", StringComparison.Ordinal)).IsTrue();

        // A paused clock means an unchanged view: nothing is written.
        await Assert.That(Draw(renderer, parser, output, view with { Frame = 1 })).IsEqualTo(string.Empty);
    }

    private static string[] Rows(ScreenBuffer screen, int first, int count)
    {
        var rows = new string[count];
        for (var i = 0; i < count; i++)
            rows[i] = screen.ViewLine(first + i).ToText();
        return rows;
    }

    private static bool Contains(ScreenBuffer screen, string text)
    {
        for (var row = 0; row < screen.Rows; row++)
        {
            if (screen.ViewLine(row).ToText().Contains(text, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>The text of <paramref name="length"/> cells from a 0-based column, skipping wide characters' continuation cells.</summary>
    private static string Cells(TermLine line, int column, int length)
    {
        var text = new StringBuilder();
        for (var i = column; i < column + length; i++)
        {
            if (!line.Cells[i].IsContinuation)
                text.Append(line.Cells[i].Text ?? " ");
        }
        return text.ToString();
    }

    private static int CountBraille(ScreenBuffer screen)
    {
        var count = 0;
        for (var row = 3; row < 11; row++)
        {
            foreach (var cell in screen.ViewLine(row).Cells)
            {
                if (cell.Text is { Length: 1 } text && text[0] is >= '⠁' and <= '⣿')
                    count++;
            }
        }
        return count;
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
