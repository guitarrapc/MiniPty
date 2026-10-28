namespace MiniPty.Tests;

public sealed class TuiDemoInputTests
{
    [Test]
    public async Task UnixAltPrefix_PreservesSplitMouseReport()
    {
        var parser = new DemoInputParser();
        await Assert.That(parser.Feed(new ConsoleKeyInfo('[', ConsoleKey.Oem4, shift: false, alt: true, control: false)).Command).IsEqualTo(DemoCommand.None);
        foreach (var character in "<0;50;7")
            await Assert.That(parser.Feed(new ConsoleKeyInfo(character, 0, false, false, false)).Command).IsEqualTo(DemoCommand.None);
        await Assert.That(parser.Feed(new ConsoleKeyInfo('M', ConsoleKey.M, false, false, false))).IsEqualTo(new DemoEvent(DemoCommand.Mouse, 50, 7, 0));
    }

    [Test]
    public async Task MouseReport_EmitsOnlyAfterCompletePress()
    {
        var parser = new DemoInputParser();
        foreach (var character in "\u001b[<0;50;7")
            await Assert.That(parser.Feed(character).Command).IsEqualTo(DemoCommand.None);
        await Assert.That(parser.Feed('M')).IsEqualTo(new DemoEvent(DemoCommand.Mouse, 50, 7, 0));
    }

    [Test]
    public async Task MouseReleaseMotionModifiersAndInvalidCoordinates_DoNotActivateControls()
    {
        foreach (var report in new[] { "\u001b[<0;36;17m", "\u001b[<32;50;7M", "\u001b[<4;50;7M", "\u001b[<0;0;7M", "\u001b[<0;50;0M", "\u001b[<0;999999999999;7M", "\u001b[<0;;7M" })
        {
            var parser = new DemoInputParser();
            foreach (var character in report)
                await Assert.That(parser.Feed(character).Command).IsEqualTo(DemoCommand.None);
            await Assert.That(parser.Feed('q').Command).IsEqualTo(DemoCommand.Quit);
        }
    }

    [Test]
    public async Task MouseWheelAndArrows_KeepTheirMeaningBetweenReports()
    {
        var parser = new DemoInputParser();
        var events = new List<DemoEvent>();
        foreach (var character in "\u001b[<64;50;7M\u001b[<65;50;7M\u001b[A\u001bOB\r p\u0003")
        {
            var input = parser.Feed(character);
            if (input.Command != DemoCommand.None)
                events.Add(input);
        }
        await Assert.That(events.Count).IsEqualTo(8);
        await Assert.That(events[0]).IsEqualTo(new DemoEvent(DemoCommand.Mouse, 50, 7, 64));
        await Assert.That(events[1]).IsEqualTo(new DemoEvent(DemoCommand.Mouse, 50, 7, 65));
        await Assert.That(events[2].Command).IsEqualTo(DemoCommand.Up);
        await Assert.That(events[3].Command).IsEqualTo(DemoCommand.Down);
        await Assert.That(events[4].Command).IsEqualTo(DemoCommand.Details);
        await Assert.That(events[5].Command).IsEqualTo(DemoCommand.Pause);
        await Assert.That(events[6].Command).IsEqualTo(DemoCommand.Pause);
        await Assert.That(events[7].Command).IsEqualTo(DemoCommand.Quit);
    }
}
