using System.Text;

namespace MiniPty.Tests;

/// <summary>
/// Stdin writes: the synchronous fast path, and on Unix the queue the reader thread writes when the child is not
/// reading and the tty input queue is full.
/// </summary>
public sealed class PtyInputTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>A write the terminal takes at once completes before the call returns, allocating nothing.</summary>
    [Test]
    public async Task SmallWritesCompleteSynchronously()
    {
        await using var session = Pty.Start(OperatingSystem.IsWindows()
            ? Spawn(Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe", ["/c", "set /p LINE="])
            : Spawn("sh", ["-c", "IFS= read -r line"]));

        var newline = OperatingSystem.IsWindows() ? "\r" : "\n";
        var text = session.WriteInputAsync("hello" + newline);
        var bytes = session.WriteInputAsync(Encoding.ASCII.GetBytes("world" + newline));

        await Assert.That(text.IsCompletedSuccessfully).IsTrue();
        await Assert.That(bytes.IsCompletedSuccessfully).IsTrue();
    }

    /// <summary>
    /// Input the child is not reading yet is queued, written in issue order as it starts reading, and EOF follows
    /// the last queued byte. A synchronous <c>Input.Write</c> issued meanwhile waits its turn.
    /// </summary>
    [Test]
    public async Task QueuedInputIsDeliveredInOrderWithEofAfterIt()
    {
        if (OperatingSystem.IsWindows())
            return;

        const int Lines = 20000;
        await using var session = Pty.Start(Spawn("sh", ["-c",
            "sleep 1; awk '{ if ($1 + 0 != NR - 1) bad = 1 } END { printf \"RESULT:%s:%d\\n\", (bad ? \"bad\" : \"ok\"), NR }'"]));
        var output = ReadUntilAsync(session, "RESULT:");

        // 140 KB of numbered lines: far more than the tty input queue holds while the child sleeps.
        var text = new StringBuilder(Lines * 7);
        for (var i = 0; i < Lines; i++)
            text.Append(i.ToString("D6")).Append('\n');

        var queued = session.WriteInputAsync(text.ToString());
        await Assert.That(queued.IsCompleted).IsFalse();

        var last = Task.Run(() => session.Input.Write(Encoding.ASCII.GetBytes(Lines.ToString("D6") + "\n")));
        // EOF is staged now, behind everything queued; sent ahead of it, awk would stop early.
        session.SendEof();

        await queued.AsTask().WaitAsync(Timeout);
        await last.WaitAsync(Timeout);
        var result = await output.WaitAsync(Timeout);

        await Assert.That(result).Contains($"RESULT:ok:{Lines + 1}");
    }

    /// <summary>Canceling a queued write completes it at once; the child keeps running.</summary>
    [Test]
    public async Task CancelingAQueuedWriteCompletesIt()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var session = Pty.Start(Spawn("sleep", ["30"]));
        using var cts = new CancellationTokenSource();

        var write = session.WriteInputAsync(new byte[4 * 1024 * 1024], cts.Token);
        await Assert.That(write.IsCompleted).IsFalse();

        await cts.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await write.AsTask().WaitAsync(Timeout));
        await Assert.That(session.HasExited).IsFalse();
    }

    /// <summary>Disposal fails a queued write at once, without waiting for the reader thread.</summary>
    [Test]
    public async Task DisposeFailsAQueuedWrite()
    {
        if (OperatingSystem.IsWindows())
            return;

        var session = Pty.Start(Spawn("sleep", ["30"]));
        var write = session.WriteInputAsync(new byte[4 * 1024 * 1024]);
        await Assert.That(write.IsCompleted).IsFalse();

        session.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await write.AsTask().WaitAsync(Timeout));
        await session.DisposeAsync().AsTask().WaitAsync(Timeout);
    }

    private static async Task<string> ReadUntilAsync(PtySession session, string marker)
    {
        var output = new StringBuilder();
        await foreach (var chunk in session.ReadOutputAsync())
        {
            output.Append(Encoding.ASCII.GetString(chunk.Data.Span));
            var text = output.ToString();
            var index = text.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0 && text.IndexOf('\n', index) >= 0)
                return text;
        }

        return output.ToString();
    }

    private static PtyStartInfo Spawn(string fileName, IReadOnlyList<string> arguments) =>
        new() { FileName = fileName, Arguments = arguments, Size = new PtySize(80, 24) };
}
