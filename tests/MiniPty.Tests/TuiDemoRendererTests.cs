using System.Text;
using TerminalEmulator;

namespace MiniPty.Tests;

/// <summary>
/// Checks the TuiDemo screen's emitted output before any PTY: what it draws, and that ticks and interactions overwrite
/// only the cells that changed. ConPTY may add its own repaint operations, so the smoke covers the PTY. Most tests use
/// the minimum 72 × 22, where the stretching layout equals the original fixed one; one test checks a larger size.
/// </summary>
public sealed class TuiDemoRendererTests
{
    private static readonly DemoView Initial = new(72, 22, 0, false, false, 0, 0, 0);
    private static readonly int Rows = new DemoGeometry(72, 22).FileRows;

    [Test]
    public async Task InitialFrame_DrawsColoredDashboard()
    {
        var (_, screen, _, _) = Start(Initial);

        await Assert.That(screen.ViewLine(0).ToText().Contains("MiniPty TUI demo", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(0).ToText().Contains("Size: 72 x 22", StringComparison.Ordinal)).IsTrue();
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
        await Assert.That(screen.ViewLine(20).ToText().StartsWith(" Click/Wheel/Up/Down: select", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task BottomBar_ShowsQuitKeysAndFillsTheRowWithoutWritingItsLastCell()
    {
        var (_, screen, _, _) = Start(Initial);

        await Assert.That(screen.ViewLine(21).ToText()).IsEqualTo(" Q/Ctrl+C: quit  1-3/Tab: switch tabs  Shift+drag: select text");
        // The color reaches the right edge through EL; the last cell of the screen is never written.
        await Assert.That(screen.ViewLine(21).Cells[71].Text).IsNull();
        await Assert.That(screen.ViewLine(21).Cells[71].Attributes.Background).IsEqualTo(screen.ViewLine(21).Cells[0].Attributes.Background);
        await Assert.That(screen.ViewLine(21).Cells[71].Attributes.Background.Kind).IsNotEqualTo(ColorKind.Default);
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
    public async Task TallLog_RedrawRepaintsEveryLineScrollingBroughtIn()
    {
        // 75 log rows: more than the renderer kept at first. Ticks scroll entries in one by one; closing the dialog
        // then redraws the log from the kept entries, which must cover every row the scrolls filled.
        var view = Initial with { Width = 100, Height = 90 };
        var (renderer, screen, parser, output) = Start(view);
        for (var frame = 1; frame <= 1500; frame++)
            Draw(renderer, parser, output, view with { Frame = frame });
        view = view with { Frame = 1500 };
        var g = new DemoGeometry(100, 90);
        var before = string.Join('\n', RowsOf(screen, DemoLayout.LogTop - 1, g.LogLines));
        await Assert.That(before.StartsWith("│ 0", StringComparison.Ordinal)).IsTrue();

        Draw(renderer, parser, output, view with { Dialog = true });
        Draw(renderer, parser, output, view);

        await Assert.That(string.Join('\n', RowsOf(screen, DemoLayout.LogTop - 1, g.LogLines))).IsEqualTo(before);
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
    public async Task Resize_StretchesTheLayoutInPlaceWithoutClearing()
    {
        var (renderer, screen, parser, output) = Start(Initial);

        screen.Resize(100, 30);
        var grow = Draw(renderer, parser, output, Initial with { Width = 100, Height = 30 });
        await Assert.That(grow.Contains("\u001b[2J", StringComparison.Ordinal)).IsFalse();
        await Assert.That(screen.ViewLine(0).ToText().TrimEnd().EndsWith("Size: 100 x 30", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(1).Cells[99].Attributes.Background.Kind).IsEqualTo(ColorKind.Rgb);
        // A 50-cell progress bar ends the job row at the new right edge.
        await Assert.That(screen.ViewLine(3).Cells[99].Text).IsEqualTo("│");
        await Assert.That(Cells(screen.ViewLine(3), 35, 50)).IsEqualTo(new string('.', 50));
        await Assert.That(Cells(screen.ViewLine(3), 86, 4)).IsEqualTo("  0%");
        // The log takes the extra rows: lines 12–26, its bottom border on row 27.
        await Assert.That(screen.ViewLine(25).ToText().Contains("00:00  Build   started (run #1)", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(26).ToText().StartsWith("└", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(26).Cells[99].Text).IsEqualTo("┘");
        await Assert.That(screen.ViewLine(27).ToText().StartsWith("Selected: 1/3", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(29).ToText().StartsWith(" Q/Ctrl+C: quit", StringComparison.Ordinal)).IsTrue();

        screen.Resize(72, 22);
        var shrink = Draw(renderer, parser, output, Initial);
        await Assert.That(shrink.Contains("\u001b[2J", StringComparison.Ordinal)).IsFalse();
        await Assert.That(screen.ViewLine(3).Cells[71].Text).IsEqualTo("│");
        await Assert.That(screen.ViewLine(18).ToText().StartsWith("└", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(19).ToText().StartsWith("Selected: 1/3", StringComparison.Ordinal)).IsTrue();
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

        screen.Resize(72, 22);
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
        var before = RowsOf(screen, 0, 22);

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
        var after = RowsOf(screen, 0, 22);
        for (var row = 0; row < before.Length; row++)
            await Assert.That(after[row]).IsEqualTo(before[row]);
    }

    [Test]
    public async Task FilesCursorMove_RewritesOnlyTheTwoRowsAndTheDetails()
    {
        var view = Initial with { Page = DemoLayout.FilesPage, Files = FileBrowser.Initial };
        var (renderer, screen, parser, output) = Start(view);

        var update = Draw(renderer, parser, output, view with { Files = DemoFiles.Move(view.Files, 1, Rows) });

        await Assert.That(update.Contains("\u001b[2J", StringComparison.Ordinal)).IsFalse();
        await Assert.That(update.Contains("PtySession", StringComparison.Ordinal)).IsFalse();
        await Assert.That(update.Contains("┌ Files", StringComparison.Ordinal)).IsFalse();
        await Assert.That(update.Contains("press / to filter", StringComparison.Ordinal)).IsFalse();
        await Assert.That(screen.ViewLine(4).Cells[5].Attributes.Background.Kind).IsEqualTo(ColorKind.Default);
        await Assert.That(screen.ViewLine(5).Cells[5].Attributes.Background.Kind).IsNotEqualTo(ColorKind.Default);
        await Assert.That(screen.ViewLine(3).ToText().Contains("Name: .github/ ", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(4).ToText().Contains("Type: folder, 2 items", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(19).ToText().StartsWith("Item: 2/20 ", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task FilesScroll_KeepsTheCursorVisibleAndAlignsWideNames()
    {
        var view = Initial with { Page = DemoLayout.FilesPage, Files = FileBrowser.Initial };
        var (renderer, screen, parser, output) = Start(view);

        var end = DemoFiles.Move(view.Files, DemoFiles.Count, Rows);
        var update = Draw(renderer, parser, output, view with { Files = end });

        await Assert.That(end).IsEqualTo(new FileBrowser(19, 20 - Rows, DemoFiles.InitiallyExpanded));
        await Assert.That(update.Contains("\u001b[2J", StringComparison.Ordinal)).IsFalse();
        await Assert.That(screen.ViewLine(4).ToText().Contains("DemoJobs.cs", StringComparison.Ordinal)).IsTrue();
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
        var view = Initial with { Page = DemoLayout.FilesPage, Files = DemoFiles.Move(FileBrowser.Initial, 1, Rows) };
        var (renderer, screen, parser, output) = Start(view);

        var open = Draw(renderer, parser, output, view with { Files = DemoFiles.Toggle(view.Files, Rows) });
        await Assert.That(open.Contains("\u001b[2J", StringComparison.Ordinal)).IsFalse();
        await Assert.That(screen.ViewLine(5).ToText().Contains("├─ - .github/", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(6).ToText().Contains("│  ├─ + workflows/", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(19).ToText().StartsWith("Item: 2/22 ", StringComparison.Ordinal)).IsTrue();

        Draw(renderer, parser, output, view);
        await Assert.That(screen.ViewLine(6).ToText().Contains("├─ - samples/", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(19).ToText().StartsWith("Item: 2/20 ", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task FilterBox_TakesTextShowsTheCursorAndFiltersTheTree()
    {
        var view = Initial with { Page = DemoLayout.FilesPage, Files = FileBrowser.Initial };
        var (renderer, screen, parser, output) = Start(view);
        await Assert.That(screen.ViewLine(3).ToText().Contains("/ press / to filter", StringComparison.Ordinal)).IsTrue();
        // The TUI hides the cursor at startup; the renderer shows it only while the box takes text.
        await Assert.That(output.ToString().Contains("\u001b[?25h", StringComparison.Ordinal)).IsFalse();

        var typing = view with { Files = view.Files with { Editing = true } };
        var start = Draw(renderer, parser, output, typing);
        await Assert.That(start.Contains("\u001b[?25h", StringComparison.Ordinal)).IsTrue();
        await Assert.That(start.Contains("├─", StringComparison.Ordinal)).IsFalse();
        await Assert.That((screen.CursorVisible, screen.CursorRow, screen.CursorColumn)).IsEqualTo((true, 3, 4));
        await Assert.That(screen.ViewLine(20).ToText().StartsWith(" Type to filter  Backspace: delete", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(21).ToText().StartsWith(" Ctrl+C: quit  Enter: stop typing", StringComparison.Ordinal)).IsTrue();

        // Wide characters move the cursor two cells each; the tree keeps the match and its folder.
        var japanese = typing with { Files = DemoFiles.WithFilter(typing.Files, "日本", Rows) };
        var update = Draw(renderer, parser, output, japanese);
        await Assert.That(update.Contains("\u001b[2J", StringComparison.Ordinal)).IsFalse();
        await Assert.That(screen.ViewLine(3).ToText().StartsWith("│ / 日本 ", StringComparison.Ordinal)).IsTrue();
        await Assert.That((screen.CursorRow, screen.CursorColumn)).IsEqualTo((3, 8));
        await Assert.That(screen.ViewLine(4).ToText().Contains("- MiniPty/", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(5).ToText().Contains("├─ 日本語メモ.txt", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(6).ToText().StartsWith("│      ", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(19).ToText().StartsWith("Item: 1/2 ", StringComparison.Ordinal)).IsTrue();

        var miss = japanese with { Files = DemoFiles.WithFilter(japanese.Files, "日本q", Rows) };
        Draw(renderer, parser, output, miss);
        await Assert.That(screen.ViewLine(3).ToText().Contains("No item matches the filter.", StringComparison.Ordinal)).IsTrue();
        await Assert.That(Contains(screen, "MiniPty/")).IsFalse();
        await Assert.That(screen.ViewLine(19).ToText().StartsWith("Item: 0/0 ", StringComparison.Ordinal)).IsTrue();

        // Enter keeps the filter, hides the cursor, and restores the help and the bottom bar.
        var done = Draw(renderer, parser, output, miss with { Files = miss.Files with { Editing = false } });
        await Assert.That(done.Contains("\u001b[?25l", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.CursorVisible).IsFalse();
        await Assert.That(screen.ViewLine(3).ToText().StartsWith("│ / 日本q ", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(21).ToText().StartsWith(" Q/Ctrl+C: quit", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task FileTree_VisibleRowsFollowFoldingAndFilter()
    {
        var visible = new int[DemoFiles.Count];
        await Assert.That(DemoFiles.Visible(DemoFiles.InitiallyExpanded, null, visible)).IsEqualTo(20);
        await Assert.That(DemoFiles.Visible(0, null, visible)).IsEqualTo(1);
        await Assert.That(DemoFiles.Prefix(9)).IsEqualTo("│  │  ├─ ");
        await Assert.That(DemoFiles.Prefix(12)).IsEqualTo("│     ├─ ");
        await Assert.That(DemoFiles.Prefix(28)).IsEqualTo("└─ ");
        await Assert.That(DemoFiles.NameWidth(25)).IsEqualTo(14);

        // A filter shows matches with their ancestors, open even when folded, and ignores case.
        await Assert.That(DemoFiles.Visible(0, "demo", visible)).IsEqualTo(6);
        await Assert.That(string.Join(",", visible.AsSpan(0, 6).ToArray())).IsEqualTo("0,7,11,12,13,14");
        await Assert.That(DemoFiles.Visible(0, "日本", visible)).IsEqualTo(2);
        await Assert.That(DemoFiles.Visible(0, "zzz", visible)).IsEqualTo(0);

        // Left on a file goes to its folder; Left again folds it; a click on the selected folder unfolds it.
        var onFile = DemoFiles.Move(FileBrowser.Initial, 5, Rows);
        var onFolder = DemoFiles.Collapse(onFile, Rows);
        await Assert.That(onFolder.Cursor).IsEqualTo(4);
        var folded = DemoFiles.Collapse(onFolder, Rows);
        await Assert.That(DemoFiles.IsExpanded(folded.Expanded, 11)).IsFalse();
        await Assert.That(DemoFiles.Click(folded, 4, Rows)).IsEqualTo(onFolder);
        await Assert.That(DemoFiles.Click(folded, Rows - 1, Rows).Cursor).IsEqualTo(Rows - 1);
        await Assert.That(DemoFiles.Click(folded, Rows, Rows)).IsEqualTo(folded);

        // Filtering clamps the cursor, folding does nothing while it is active, and an empty list moves nowhere.
        var filtered = DemoFiles.WithFilter(DemoFiles.Move(FileBrowser.Initial, 19, Rows), "demo", Rows);
        await Assert.That((filtered.Cursor, filtered.Top)).IsEqualTo((5, 0));
        await Assert.That(DemoFiles.Toggle(filtered with { Cursor = 2 }, Rows)).IsEqualTo(filtered with { Cursor = 2 });
        var empty = DemoFiles.WithFilter(filtered, "zzz", Rows);
        await Assert.That((empty.Cursor, empty.Top)).IsEqualTo((0, 0));
        await Assert.That(DemoFiles.Move(empty, 3, Rows)).IsEqualTo(empty);
        await Assert.That(DemoFiles.Collapse(empty, Rows)).IsEqualTo(empty);
    }

    [Test]
    public async Task ChartsTick_WritesBrailleAndOnlyChangedCells()
    {
        var view = Initial with { Page = DemoLayout.ChartsPage };
        var (renderer, screen, parser, output) = Start(view);
        var initialBytes = Encoding.UTF8.GetByteCount(output.ToString());
        var before = RowsOf(screen, 3, 8);

        await Assert.That(CountBraille(screen, 3, 11)).IsGreaterThan(60);
        await Assert.That(screen.ViewLine(17).ToText().Contains("GET   POST  PUT   PATCH DEL", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(13).ToText().Contains("cpu ", StringComparison.Ordinal)).IsTrue();

        var update = Draw(renderer, parser, output, view with { Frame = 1 });
        await Assert.That(update.Contains("\u001b[2J", StringComparison.Ordinal)).IsFalse();
        await Assert.That(update.Contains("Signals", StringComparison.Ordinal)).IsFalse();
        await Assert.That(update.Contains("Requests", StringComparison.Ordinal)).IsFalse();
        await Assert.That(Encoding.UTF8.GetByteCount(update)).IsLessThan(initialBytes);
        await Assert.That(RowsOf(screen, 3, 8).SequenceEqual(before)).IsFalse();
        await Assert.That(screen.ViewLine(19).ToText().StartsWith("Frame: 1   Paused: no ", StringComparison.Ordinal)).IsTrue();

        // A paused clock means an unchanged view: nothing is written.
        await Assert.That(Draw(renderer, parser, output, view with { Frame = 1 })).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task ChartsOnALargerTerminal_UseTheExtraCellsForThePlot()
    {
        var view = new DemoView(100, 30, 0, false, false, 0, 0, 0, DemoLayout.ChartsPage);
        var (_, screen, _, _) = Start(view);
        var c = new DemoGeometry(100, 30).Charts(default);

        await Assert.That(c.PlotColumns).IsEqualTo(88);
        await Assert.That(c.PlotRows).IsEqualTo(16);
        await Assert.That(screen.ViewLine(3).Cells[99].Text).IsEqualTo("│");
        await Assert.That(screen.ViewLine(c.PlotBottom - 1).ToText().StartsWith("└─────┴", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(c.Top - 1).ToText().StartsWith("┌ Requests/s ", StringComparison.Ordinal)).IsTrue();
        await Assert.That(CountBraille(screen, 3, 3 + c.PlotRows)).IsGreaterThan(CountBrailleAt72());
        // A 50-cell sparkline, then its percent and the border at the right edge.
        await Assert.That(Cells(screen.ViewLine(c.Top), c.LoadLeft + 6, c.SparkLength).Trim().Length).IsEqualTo(50);
        await Assert.That(screen.ViewLine(c.Top).Cells[97].Text).IsEqualTo("%");
        await Assert.That(screen.ViewLine(c.Top).Cells[99].Text).IsEqualTo("│");
        await Assert.That(screen.ViewLine(29).ToText().StartsWith(" Q/Ctrl+C: quit", StringComparison.Ordinal)).IsTrue();

        static int CountBrailleAt72()
        {
            var (_, small, _, _) = Start(Initial with { Page = DemoLayout.ChartsPage });
            return CountBraille(small, 3, 11);
        }
    }

    [Test]
    public async Task ChartsSplit_MovesTheBordersAndScalesTheChartsInPlace()
    {
        var view = Initial with { Page = DemoLayout.ChartsPage, Frame = 40 };
        var (renderer, screen, parser, output) = Start(view);

        var split = Draw(renderer, parser, output, view with { Charts = new ChartsLayout(10, 45) });
        var c = new DemoGeometry(72, 22).Charts(new ChartsLayout(10, 45));

        await Assert.That(split.Contains("\u001b[2J", StringComparison.Ordinal)).IsFalse();
        await Assert.That((c.PlotRows, c.BarRows, c.BarSlot, c.SparkLength)).IsEqualTo((5, 7, 8, 12));
        await Assert.That(screen.ViewLine(c.PlotBottom - 1).ToText()).IsEqualTo("└─────┴" + new string('─', 64) + "┘");
        await Assert.That(screen.ViewLine(c.Top - 1).ToText().StartsWith("┌ Requests/s ", StringComparison.Ordinal)).IsTrue();
        await Assert.That(screen.ViewLine(c.Top - 1).Cells[44].Text).IsEqualTo("┐");
        await Assert.That(screen.ViewLine(c.Top - 1).Cells[46].Text).IsEqualTo("┌");
        await Assert.That(screen.ViewLine(c.Top - 1).Cells[71].Text).IsEqualTo("┐");
        // Five 5-cell bars over seven rows, labels in 8-cell slots, and a 12-cell sparkline before the percent.
        // Rows are 1-based; ViewLine is 0-based. The last bar row sits above the labels and the bottom border.
        var lastBarRow = screen.ViewLine(c.Top + c.BarRows - 1);
        await Assert.That(Cells(lastBarRow, 3, 5)).IsEqualTo("█████");
        await Assert.That(lastBarRow.ToText().StartsWith("│  █████   █████   █████   █████   █████", StringComparison.Ordinal)).IsTrue();
        await Assert.That(lastBarRow.Cells[44].Text).IsEqualTo("│");
        await Assert.That(screen.ViewLine(c.PageBottom - 2).ToText().StartsWith("│  GET     POST    PUT     PATCH   DEL", StringComparison.Ordinal)).IsTrue();
        await Assert.That(Cells(screen.ViewLine(c.Top), c.LoadLeft + 6, c.SparkLength).Trim().Length).IsEqualTo(12);
        await Assert.That(screen.ViewLine(c.Top).Cells[71].Text).IsEqualTo("│");
        await Assert.That(screen.ViewLine(c.PageBottom - 1).ToText()).IsEqualTo("└" + new string('─', 43) + "┘ └" + new string('─', 24) + "┘");

        // A tick on the new split still writes only the animated cells.
        var tick = Draw(renderer, parser, output, view with { Charts = new ChartsLayout(10, 45), Frame = 41 });
        await Assert.That(tick.Contains("Requests", StringComparison.Ordinal)).IsFalse();
        await Assert.That(tick.Contains("\u001b[2J", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task ChartsSplit_ClampsToTheSizeAndHighlightsTheDraggedBorder()
    {
        var g = new DemoGeometry(72, 22);
        await Assert.That(g.Charts(new ChartsLayout(100, 100)).Layout).IsEqualTo(new ChartsLayout(12, 49));
        await Assert.That(g.Charts(new ChartsLayout(1, 1)).Layout).IsEqualTo(new ChartsLayout(7, 30));
        await Assert.That(g.Charts(default).Layout).IsEqualTo(new ChartsLayout(7, 35));
        // A split that fit a larger terminal is clamped again when the terminal shrinks.
        var large = new DemoGeometry(120, 40).Charts(new ChartsLayout(25, 90));
        await Assert.That(large.Layout).IsEqualTo(new ChartsLayout(25, 90));
        await Assert.That(g.Charts(large.Layout).Layout).IsEqualTo(new ChartsLayout(12, 49));

        var view = Initial with { Page = DemoLayout.ChartsPage };
        var (renderer, screen, parser, output) = Start(view);
        var c = g.Charts(default);
        await Assert.That(screen.ViewLine(c.Top - 1).Cells[0].Attributes.Foreground.Kind).IsEqualTo(ColorKind.Default);

        Draw(renderer, parser, output, view with { Dragging = DemoDrag.Rows });
        await Assert.That(screen.ViewLine(c.Top - 1).Cells[0].Attributes.Foreground.Kind).IsNotEqualTo(ColorKind.Default);
        await Assert.That(screen.ViewLine(c.PlotBottom - 1).Cells[10].Attributes.Foreground.Kind).IsNotEqualTo(ColorKind.Default);
        await Assert.That(screen.ViewLine(c.Top).Cells[c.BarWidth - 1].Attributes.Foreground.Kind).IsEqualTo(ColorKind.Default);

        Draw(renderer, parser, output, view with { Dragging = DemoDrag.Columns });
        await Assert.That(screen.ViewLine(c.Top - 1).Cells[0].Attributes.Foreground.Kind).IsEqualTo(ColorKind.Default);
        await Assert.That(screen.ViewLine(c.Top).Cells[c.BarWidth - 1].Attributes.Foreground.Kind).IsNotEqualTo(ColorKind.Default);
        await Assert.That(screen.ViewLine(c.Top + 2).Cells[c.LoadLeft - 1].Attributes.Foreground.Kind).IsNotEqualTo(ColorKind.Default);

        Draw(renderer, parser, output, view);
        await Assert.That(screen.ViewLine(c.Top).Cells[c.BarWidth - 1].Attributes.Foreground.Kind).IsEqualTo(ColorKind.Default);
    }

    private static string[] RowsOf(ScreenBuffer screen, int first, int count)
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

    private static int CountBraille(ScreenBuffer screen, int firstRow, int endRow)
    {
        var count = 0;
        for (var row = firstRow; row < endRow; row++)
        {
            foreach (var cell in screen.ViewLine(row).Cells)
            {
                if (cell.Text is { Length: 1 } text && text[0] is >= '⠁' and <= '⣿')
                    count++;
            }
        }
        return count;
    }

    private static (DemoRenderer Renderer, ScreenBuffer Screen, VtParser Parser, StringWriter Output) Start(DemoView view)
    {
        var output = new StringWriter();
        var renderer = new DemoRenderer(output);
        var screen = new ScreenBuffer(view.Width, view.Height);
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
