using System.Text;
using TerminalEmulator;

namespace MiniPty.Tests;

/// <summary>
/// The samples' shared terminal emulator core (VT parser, screen buffer, key encoding), driven headlessly. These found a real defect before the sample
/// was first used interactively: the first CSI parameter slot kept the previous sequence's digits, so
/// <c>ESC[2J</c> after <c>ESC[4;1H</c> arrived as <c>ESC[42J</c>, which broke every one-parameter sequence and
/// every 24-bit color.
/// </summary>
public sealed class TerminalEmulatorTests
{
    [Test]
    public async Task AlternateResizeKeepsMainScreenUsable()
    {
        foreach (var mode in new[] { 47, 1047, 1049 })
        {
            var (screen, parser) = Create(10, 3);
            Feed(parser, "main" + $"\u001b[?{mode}h");
            screen.Resize(10, 8);
            screen.Resize(10, 5);
            Feed(parser, $"\u001b[?{mode}l\u001b[HX");
            await Assert.That(Row(screen, 0)).IsEqualTo("Xain");
            await Assert.That(screen.ScrollbackCount).IsEqualTo(0);
            screen.ScrollView(1);
            Feed(parser, "\u001b[5;1Hlast");
            await Assert.That(Row(screen, 4)).IsEqualTo("last");
        }
    }

    [Test]
    public async Task EditingPreservesWideCellPairs()
    {
        foreach (var sequence in new[] { "a漢b\r\u001b[2P", "ab漢\r\u001b[@", "a漢b\u001b[3G\u001b[@", "a漢b\u001b[3G\u001b[4hX" })
        {
            var (screen, parser) = Create(4, 2);
            Feed(parser, sequence);
            var cells = screen.ViewLine(0).Cells;
            for (var column = 0; column < cells.Length; column++)
            {
                if (cells[column].Width == 2)
                    await Assert.That(column + 1 < cells.Length && cells[column + 1].IsContinuation).IsTrue();
                if (cells[column].IsContinuation)
                    await Assert.That(column > 0 && cells[column - 1].Width == 2).IsTrue();
            }
        }
    }

    [Test]
    public async Task EditingKeepsWholeWideGlyphsAndBlanksOnlySplitGlyphs()
    {
        foreach (var (sequence, expected) in new[]
        {
            ("a漢b\r\u001b[2P", " b"),
            ("ab漢\r\u001b[@", " ab"),
            ("漢ab\r\u001b[@", " 漢a"),
            ("a漢b\r\u001b[P", "漢b"),
            ("a漢b\u001b[3G\u001b[@", "a"),
            ("a漢b\u001b[3G\u001b[4hX", "a X"),
        })
        {
            var (screen, parser) = Create(4, 2);
            Feed(parser, sequence);
            await Assert.That(Row(screen, 0)).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task AlternateResizeRestoresCursorOnItsOriginalContent()
    {
        var (screen, parser) = Create(10, 3);
        Feed(parser, "one\r\ntwo\r\nthree\u001b[?1049h");
        screen.Resize(10, 2);
        screen.Resize(10, 5);
        Feed(parser, "\u001b[?1049lX");
        await Assert.That(Row(screen, screen.CursorRow)).IsEqualTo("threeX");
        await Assert.That(Row(screen, 0)).IsEqualTo("one");
    }

    [Test]
    public async Task EveryEditBoundaryMaintainsWideCellPairs()
    {
        foreach (var text in new[] { "漢漢ab", "a漢b漢", "ab漢漢" })
        foreach (var operation in new[] { '@', 'P' })
        for (var column = 1; column <= 6; column++)
        foreach (var count in new[] { 1, 2, 6, 65535 })
        {
            var (screen, parser) = Create(6, 2);
            Feed(parser, $"{text}\u001b[{column}G\u001b[{count}{operation}");
            var cells = screen.ViewLine(0).Cells;
            for (var i = 0; i < cells.Length; i++)
            {
                if (cells[i].Width == 2)
                    await Assert.That(i + 1 < cells.Length && cells[i + 1].IsContinuation).IsTrue();
                if (cells[i].IsContinuation)
                    await Assert.That(i > 0 && cells[i - 1].Width == 2).IsTrue();
            }
        }
    }

    private static (ScreenBuffer Screen, VtParser Parser) Create(int columns, int rows)
    {
        var screen = new ScreenBuffer(columns, rows);
        return (screen, new VtParser(screen));
    }

    private static void Feed(VtParser parser, string text) => parser.Feed(Encoding.UTF8.GetBytes(text));

    private static string Row(ScreenBuffer screen, int row) => screen.ViewLine(row).ToText();

    [Test]
    public async Task PrintsLinesWrapsAndScrollsIntoScrollback()
    {
        var (screen, parser) = Create(10, 3);
        Feed(parser, "hello\r\nworld\r\n0123456789ABC");

        await Assert.That(Row(screen, 0)).IsEqualTo("world");
        await Assert.That(Row(screen, 1)).IsEqualTo("0123456789");
        await Assert.That(Row(screen, 2)).IsEqualTo("ABC");
        await Assert.That(screen.ViewLine(1).Wrapped).IsTrue();
        await Assert.That(screen.ScrollbackCount).IsEqualTo(1);
        await Assert.That(screen.AbsoluteLine(0)!.ToText()).IsEqualTo("hello");
        await Assert.That((screen.CursorRow, screen.CursorColumn)).IsEqualTo((2, 3));
    }

    [Test]
    public async Task WrapIsDeferredUntilTheNextCharacter()
    {
        var (screen, parser) = Create(5, 2);
        Feed(parser, "abcde");
        await Assert.That((screen.CursorRow, screen.CursorColumn)).IsEqualTo((0, 4));

        Feed(parser, "\r\n");
        await Assert.That(screen.CursorRow).IsEqualTo(1);
        await Assert.That(Row(screen, 1)).IsEqualTo("");
    }

    [Test]
    public async Task WideCharactersCombiningMarksAndSplitUtf8()
    {
        var (screen, parser) = Create(10, 2);
        var bytes = Encoding.UTF8.GetBytes("漢字a");
        parser.Feed(bytes.AsSpan(0, 2));
        parser.Feed(bytes.AsSpan(2));

        var cells = screen.ViewLine(0).Cells;
        await Assert.That(cells[0].Width).IsEqualTo((byte)2);
        await Assert.That(cells[1].IsContinuation).IsTrue();
        await Assert.That(cells[4].Text).IsEqualTo("a");
        await Assert.That(Row(screen, 0)).IsEqualTo("漢字a");
        await Assert.That(screen.CursorColumn).IsEqualTo(5);

        Feed(parser, "é");
        await Assert.That(cells[5].Text).IsEqualTo("é");
        await Assert.That(screen.CursorColumn).IsEqualTo(6);
    }

    [Test]
    public async Task CursorMovementAndErasing()
    {
        var (screen, parser) = Create(10, 4);
        Feed(parser, "\u001b[2;3Hx\u001b[4;1Hbottom\u001b[Hy");
        await Assert.That(Row(screen, 1)).IsEqualTo("  x");
        await Assert.That(Row(screen, 3)).IsEqualTo("bottom");
        await Assert.That(Row(screen, 0)).IsEqualTo("y");

        Feed(parser, "\u001b[4;3H\u001b[K");
        await Assert.That(Row(screen, 3)).IsEqualTo("bo");

        // One-parameter sequences after multi-parameter ones: the first-slot reset regression.
        Feed(parser, "\u001b[2J");
        await Assert.That(Row(screen, 0) + Row(screen, 1) + Row(screen, 3)).IsEqualTo("");

        Feed(parser, "\u001b[Habc\u001b[2D\u001b[P");
        await Assert.That(Row(screen, 0)).IsEqualTo("ac");
        Feed(parser, "\u001b[G\u001b[2@");
        await Assert.That(Row(screen, 0)).IsEqualTo("  ac");
    }

    [Test]
    public async Task GraphicRenditionsIncludingColonForms()
    {
        var (screen, parser) = Create(20, 1);
        Feed(parser, "\u001b[1;31mA\u001b[38;2;10;20;30mB\u001b[38:5:200mC\u001b[4:3mD\u001b[0mE");
        var cells = screen.ViewLine(0).Cells;

        await Assert.That(cells[0].Attributes.Has(CellFlags.Bold)).IsTrue();
        await Assert.That(cells[0].Attributes.Foreground).IsEqualTo(TermColor.Indexed(1));
        await Assert.That(cells[1].Attributes.Foreground).IsEqualTo(TermColor.FromRgb(10, 20, 30));
        await Assert.That(cells[2].Attributes.Foreground).IsEqualTo(TermColor.Indexed(200));
        await Assert.That(cells[3].Attributes.Has(CellFlags.Underline)).IsTrue();
        await Assert.That(cells[3].Attributes.Has(CellFlags.Italic)).IsFalse();
        await Assert.That(cells[4].Attributes).IsEqualTo(CellAttributes.Plain);
        await Assert.That(Palette.Resolve(TermColor.Indexed(1), isForeground: true, bold: true)).IsEqualTo(0xE74856u);
        await Assert.That(Palette.Resolve(TermColor.Indexed(196), isForeground: true, bold: false)).IsEqualTo(0xFF0000u);
    }

    [Test]
    public async Task ScrollRegionInsertDeleteAndReverseIndex()
    {
        var (screen, parser) = Create(5, 4);
        Feed(parser, "1\r\n2\r\n3\r\n4\u001b[2;3r\u001b[3;1H\n");
        await Assert.That(Row(screen, 0) + "|" + Row(screen, 1) + "|" + Row(screen, 2) + "|" + Row(screen, 3)).IsEqualTo("1|3||4");

        Feed(parser, "\u001b[2;1H\u001bM");
        await Assert.That(Row(screen, 1) + "|" + Row(screen, 2)).IsEqualTo("|3");

        Feed(parser, "\u001b[r\u001b[1;1H\u001b[L");
        await Assert.That(Row(screen, 0) + "|" + Row(screen, 1)).IsEqualTo("|1");
        Feed(parser, "\u001b[M");
        await Assert.That(Row(screen, 0)).IsEqualTo("1");
    }

    [Test]
    public async Task AlternateScreenModesAndTitle()
    {
        var (screen, parser) = Create(10, 3);
        string? title = null;
        screen.TitleChanged += t => title = t;

        Feed(parser, "main\u001b]0;My Title\u0007\u001b[?1049h\u001b[?25lalt");
        await Assert.That(screen.IsAlternateScreen).IsTrue();
        await Assert.That(Row(screen, 0)).IsEqualTo("alt");
        await Assert.That(screen.CursorVisible).IsFalse();
        await Assert.That(title).IsEqualTo("My Title");

        Feed(parser, "\u001b[?25h\u001b[?1049l");
        await Assert.That(screen.IsAlternateScreen).IsFalse();
        await Assert.That(Row(screen, 0)).IsEqualTo("main");
        await Assert.That(screen.CursorColumn).IsEqualTo(4);

        Feed(parser, "\u001b[?1h\u001b[?2004h\u001b[?1006h\u001b[?1002h\u001b[?2026h");
        await Assert.That(screen.ApplicationCursorKeys && screen.BracketedPaste && screen.MouseSgr && screen.SynchronizedOutput).IsTrue();
        await Assert.That(screen.Mouse).IsEqualTo(MouseTracking.Button);
        Feed(parser, "\u001b[?2026l");
        await Assert.That(screen.SynchronizedOutput).IsFalse();
    }

    [Test]
    public async Task ReportsAndStringSequences()
    {
        var (screen, parser) = Create(10, 3);
        var replies = new StringBuilder();
        screen.Response += r => replies.Append(r);

        Feed(parser, "\u001b[2;4H\u001b[6n\u001b[c\u001b[5n");
        await Assert.That(replies.ToString()).IsEqualTo("\u001b[2;4R\u001b[?62;22c\u001b[0n");

        // OSC ended by ST, a DCS that is skipped, and a CSI split across feeds.
        Feed(parser, "\u001b[H\u001b]2;abc\u001b\\x\u001bP+q544e\u001b\\y");
        await Assert.That(Row(screen, 0)).IsEqualTo("xy");
        await Assert.That(screen.Title).IsEqualTo("abc");
        Feed(parser, "\u001b[");
        Feed(parser, "1;");
        Feed(parser, "1Hz");
        await Assert.That(Row(screen, 0)).IsEqualTo("zy");
    }

    [Test]
    public async Task ResizeKeepsTheCursorLineAndDropsBlankLinesFirst()
    {
        var (screen, parser) = Create(10, 5);
        Feed(parser, "a\r\nb");
        screen.Resize(10, 3);
        await Assert.That(Row(screen, 0) + "|" + Row(screen, 1)).IsEqualTo("a|b");
        await Assert.That(screen.CursorRow).IsEqualTo(1);

        screen.Resize(4, 3);
        await Assert.That(screen.Columns).IsEqualTo(4);
        await Assert.That(Row(screen, 0)).IsEqualTo("a");
    }

    [Test]
    public async Task KeyEncodingFollowsXterm()
    {
        await Assert.That(KeyEncoder.Encode(TermKey.Up, false, false, false, false)).IsEqualTo("\u001b[A");
        await Assert.That(KeyEncoder.Encode(TermKey.Up, false, false, false, true)).IsEqualTo("\u001bOA");
        await Assert.That(KeyEncoder.Encode(TermKey.Right, false, true, false, false)).IsEqualTo("\u001b[1;5C");
        await Assert.That(KeyEncoder.Encode(TermKey.Tab, true, false, false, false)).IsEqualTo("\u001b[Z");
        await Assert.That(KeyEncoder.Encode(TermKey.Backspace, false, false, false, false)).IsEqualTo("\u007f");
        await Assert.That(KeyEncoder.Encode(TermKey.Backspace, false, true, false, false)).IsEqualTo("\b");
        await Assert.That(KeyEncoder.Encode(TermKey.C, false, true, false, false)).IsEqualTo("\u0003");
        await Assert.That(KeyEncoder.Encode(TermKey.X, false, false, true, false)).IsEqualTo("\u001bx");
        await Assert.That(KeyEncoder.Encode(TermKey.F1, false, false, false, false)).IsEqualTo("\u001bOP");
        await Assert.That(KeyEncoder.Encode(TermKey.F5, false, false, false, false)).IsEqualTo("\u001b[15~");
        await Assert.That(KeyEncoder.Encode(TermKey.Delete, true, false, false, false)).IsEqualTo("\u001b[3;2~");
        await Assert.That(KeyEncoder.Encode(TermKey.Space, false, true, false, false)).IsEqualTo("\0");
        await Assert.That(KeyEncoder.Encode(TermKey.A, false, false, false, false)).IsNull();
        await Assert.That(KeyEncoder.ScrollArrow(up: true, applicationCursorKeys: true)).IsEqualTo("\u001bOA");
    }

    [Test]
    public async Task MouseReportsAndPaste()
    {
        await Assert.That(MouseReport.Encode(MouseTracking.Normal, sgr: true, MouseReport.LeftButton, row: 4, column: 9, release: false, motion: false, false, false, false))
            .IsEqualTo("\u001b[<0;10;5M");
        await Assert.That(MouseReport.Encode(MouseTracking.Normal, sgr: true, MouseReport.LeftButton, 4, 9, release: true, motion: false, shift: true, false, false))
            .IsEqualTo("\u001b[<4;10;5m");
        await Assert.That(MouseReport.Encode(MouseTracking.Normal, sgr: false, MouseReport.RightButton, 0, 0, release: false, motion: false, false, false, false))
            .IsEqualTo("\u001b[M" + '"' + "!!");
        await Assert.That(MouseReport.Encode(MouseTracking.Normal, sgr: true, MouseReport.LeftButton, 0, 0, release: false, motion: true, false, false, false)).IsNull();
        await Assert.That(MouseReport.Encode(MouseTracking.X10, sgr: true, MouseReport.LeftButton, 0, 0, release: true, motion: false, false, false, false)).IsNull();
        await Assert.That(MouseReport.Encode(MouseTracking.None, sgr: true, MouseReport.LeftButton, 0, 0, false, false, false, false, false)).IsNull();

        await Assert.That(PasteText.Normalize("a\r\nb\nc", bracketedPaste: false)).IsEqualTo("a\rb\rc");
        await Assert.That(PasteText.Normalize("x", bracketedPaste: true)).IsEqualTo("\u001b[200~x\u001b[201~");
    }

    [Test]
    public async Task SelectionCopiesRowsAndJoinsWrappedOnes()
    {
        var (screen, parser) = Create(5, 4);
        Feed(parser, "abcdefg\r\nhi");
        var selection = new TerminalSelection();

        selection.Begin(0, 1);
        await Assert.That(selection.HasSelection).IsFalse();
        await Assert.That(selection.Extend(2, 0)).IsTrue();
        selection.EndDrag();
        // Row 0 wrapped into row 1, so no newline between them; row 1 to row 2 is a real line break.
        await Assert.That(selection.GetText(screen)).IsEqualTo("bcdefg\r\nh");
        await Assert.That(selection.TryGetRowRange(1, 5, out var from, out var to)).IsTrue();
        await Assert.That((from, to)).IsEqualTo((0, 5));

        selection.SelectWord(screen, 2, 1);
        await Assert.That(selection.GetText(screen)).IsEqualTo("hi");
        selection.Clear();
        await Assert.That(selection.GetText(screen)).IsEqualTo("");
    }

    [Test]
    public async Task DecGraphicsAndCharacterWidths()
    {
        var (screen, parser) = Create(10, 1);
        Feed(parser, "\u001b(0qx\u001b(Bq");
        await Assert.That(Row(screen, 0)).IsEqualTo("─│q");

        await Assert.That(CharWidth.Of('a')).IsEqualTo(1);
        await Assert.That(CharWidth.Of('漢')).IsEqualTo(2);
        await Assert.That(CharWidth.Of('ｱ')).IsEqualTo(1);
        await Assert.That(CharWidth.Of(0x1F600)).IsEqualTo(2);
        await Assert.That(CharWidth.Of(0x301)).IsEqualTo(0);
        await Assert.That(CharWidth.Of('─')).IsEqualTo(1);
    }

    [Test]
    public async Task WheelNotchesScrollWholeStepsAndTrackpadDeltasAccumulate()
    {
        // A mouse notch (delta 1, three lines) scrolls three lines at once, as before.
        var wheel = new WheelAccumulator();
        await Assert.That(wheel.Add(3)).IsEqualTo(3);
        await Assert.That(wheel.Add(-3)).IsEqualTo(-3);

        // A trackpad swipe: forty events of a quarter line each is ten lines, not forty notches.
        wheel = new WheelAccumulator();
        var total = 0;
        for (var i = 0; i < 40; i++)
            total += wheel.Add(0.25);
        await Assert.That(total).IsEqualTo(10);

        // Reversing drops the carry: 0.9 lines up, then 0.5 down, scrolls nothing and keeps nothing of the 0.9.
        wheel = new WheelAccumulator();
        await Assert.That(wheel.Add(0.9)).IsEqualTo(0);
        await Assert.That(wheel.Add(-0.5)).IsEqualTo(0);
        await Assert.That(wheel.Add(-0.5)).IsEqualTo(-1);

        // Mouse reports go one per notch's worth of lines.
        wheel = new WheelAccumulator();
        await Assert.That(wheel.Add(2, step: 3)).IsEqualTo(0);
        await Assert.That(wheel.Add(2, step: 3)).IsEqualTo(1);
        await Assert.That(wheel.Add(5, step: 3)).IsEqualTo(2);
    }
}
