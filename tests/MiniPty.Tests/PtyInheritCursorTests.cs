using System.Text;

namespace MiniPty.Tests;

/// <summary>
/// <see cref="PtyStartInfo.InheritCursor"/>. On Windows, ConPTY either clears the screen as its first output (off) or
/// asks the frontend for the cursor position and continues from the reported position (on). Unix ignores the option.
/// </summary>
/// <remarks>
/// The tests act as the frontend: they answer ConPTY's cursor position query (<c>ESC[6n</c>) by writing a report
/// (<c>ESC[row;columnR</c>) to the session input, as xterm.js does.
/// </remarks>
public sealed class PtyInheritCursorTests
{
    private const string CursorPositionQuery = "\e[6n";
    private const string ClearScreen = "\e[2J";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Test]
    public async Task DefaultStartClearsTheScreenWithoutQueryingTheCursor()
    {
        if (!OperatingSystem.IsWindows())
            return;

        await using var session = Pty.Start(WindowsCommand("echo DEFAULT_MARKER"));
        var result = await session.CompleteAsync().WaitAsync(Timeout);

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Contains("DEFAULT_MARKER")).IsTrue();
        await Assert.That(result.Contains(CursorPositionQuery)).IsFalse();
        // The clear is what InheritCursor avoids; asserting it here keeps the no-clear assertion below meaningful.
        await Assert.That(result.Contains(ClearScreen)).IsTrue();
    }

    [Test]
    public async Task InheritCursorQueriesFirstThenContinuesWithoutClearing()
    {
        if (!OperatingSystem.IsWindows())
            return;

        await using var session = Pty.Start(WindowsCommand("echo INHERIT_MARKER") with { InheritCursor = true });
        var output = await ReadAnsweringCursorQueryAsync(session, input: null);
        var status = await session.WaitForExitAsync().WaitAsync(Timeout);

        await Assert.That(status.ExitCode).IsEqualTo(0);
        await Assert.That(output.StartsWith(CursorPositionQuery, StringComparison.Ordinal)).IsTrue();
        await Assert.That(output).Contains("INHERIT_MARKER");
        await Assert.That(output).DoesNotContain(ClearScreen);
    }

    [Test]
    public async Task InheritCursorReportDoesNotReachTheChild()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // Delayed expansion, because %LINE% would be expanded before set /p runs; CR submits a ConPTY line.
        await using var session = Pty.Start(
            Spawn(WindowsComSpec(), ["/v:on", "/c", "set /p LINE= & echo GOT:!LINE!"]) with { InheritCursor = true });
        var output = await ReadAnsweringCursorQueryAsync(session, input: "abc\r");
        var status = await session.WaitForExitAsync().WaitAsync(Timeout);

        await Assert.That(status.ExitCode).IsEqualTo(0);
        await Assert.That(output).Contains("GOT:abc");
    }

    [Test]
    public async Task UnansweredInheritCursorStallsTheChildButStillTearsDown()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var session = Pty.Start(WindowsCommand("echo NEVER_SHOWN") with { InheritCursor = true });
        try
        {
            using var cts = new CancellationTokenSource(Timeout);
            await using var reader = session.ReadOutputAsync(cts.Token).GetAsyncEnumerator();
            var output = new StringBuilder();
            while (output.ToString().IndexOf(CursorPositionQuery, StringComparison.Ordinal) < 0)
            {
                await Assert.That(await reader.MoveNextAsync()).IsTrue();
                output.Append(Encoding.UTF8.GetString(reader.Current.Data.Span));
            }

            // Without the stall, cmd /c echo exits within tens of milliseconds.
            await Task.Delay(TimeSpan.FromSeconds(1));
            await Assert.That(session.HasExited).IsFalse();

            session.Kill();
            await session.WaitForExitAsync().WaitAsync(Timeout);
            while (await reader.MoveNextAsync().AsTask().WaitAsync(Timeout))
                output.Append(Encoding.UTF8.GetString(reader.Current.Data.Span));

            await Assert.That(output.ToString()).DoesNotContain("NEVER_SHOWN");
        }
        finally
        {
            await session.DisposeAsync().AsTask().WaitAsync(Timeout);
        }
    }

    [Test]
    public async Task InheritCursorIsIgnoredOnUnix()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var session = Pty.Start(Spawn("sh", ["-c", "printf 'UNIX_MARKER\\n'"]) with { InheritCursor = true });
        var result = await session.CompleteAsync().WaitAsync(Timeout);

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Contains("UNIX_MARKER")).IsTrue();
        await Assert.That(result.Contains(CursorPositionQuery)).IsFalse();
    }

    /// <summary>
    /// Reads output to its end, answering the first cursor position query with a report for row 5, column 1, then
    /// writing <paramref name="input"/> when given.
    /// </summary>
    private static async Task<string> ReadAnsweringCursorQueryAsync(PtySession session, string? input)
    {
        using var cts = new CancellationTokenSource(Timeout);
        var output = new StringBuilder();
        var answered = false;
        await foreach (var chunk in session.ReadOutputAsync(cts.Token))
        {
            output.Append(Encoding.UTF8.GetString(chunk.Data.Span));
            if (answered || output.ToString().IndexOf(CursorPositionQuery, StringComparison.Ordinal) < 0)
                continue;

            answered = true;
            await session.WriteInputAsync("\e[5;1R"u8.ToArray(), cts.Token);
            if (input is not null)
                await session.WriteInputAsync(input, cancellationToken: cts.Token);
        }

        return output.ToString();
    }

    private static PtyStartInfo Spawn(string fileName, IReadOnlyList<string> arguments) =>
        new() { FileName = fileName, Arguments = arguments, Size = new(80, 24) };

    private static PtyStartInfo WindowsCommand(string command) => Spawn(WindowsComSpec(), ["/c", command]);

    private static string WindowsComSpec() =>
        Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
}
