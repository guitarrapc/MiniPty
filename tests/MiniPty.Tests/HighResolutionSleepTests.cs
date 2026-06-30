using System.Diagnostics;
using MiniPty.Internal;

namespace MiniPty.Tests;

public sealed class HighResolutionSleepTests
{
    [Test]
    public async Task ZeroMillisecondsSleepCompletes()
    {
        PtySleep.Sleep(0);
        await Task.CompletedTask;
    }

    [Test]
    public async Task SleepWaitsForRequestedDuration()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var stopwatch = Stopwatch.StartNew();
        PtySleep.Sleep(5);
        stopwatch.Stop();

        await Assert.That(stopwatch.ElapsedMilliseconds).IsGreaterThanOrEqualTo(4);
    }
}
