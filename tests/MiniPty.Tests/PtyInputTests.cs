using System.Text;

namespace MiniPty.Tests;

/// <summary>
/// Stdin writes: the synchronous fast path, and the ordered queue that takes over when the transport stops taking
/// input. On Unix the tty input queue fills when the child does not read, and the reader thread writes the queue; on
/// Windows ConPTY stops reading input while its output is backed up, and I/O completions write the queue.
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

    /// <summary>
    /// Input written while output nobody reads has backed up is queued instead of blocking the caller (plan F11), is
    /// delivered once output is read, and EOF follows the last queued byte.
    /// </summary>
    [Test]
    public async Task QueuedInputIsDeliveredWithEofAfterItWhileOutputIsBackedUp()
    {
        const int Lines = 3000;
        var countFile = Path.GetTempFileName();
        try
        {
            await using var session = Pty.Start(FloodThenCountLinesChild(countFile));
            await Task.Delay(BackUpDelay);

            // 21 KB of numbered lines: more than the ConPTY input pipe or a tty input queue takes while the child is
            // blocked writing output.
            var newline = OperatingSystem.IsWindows() ? "\r" : "\n";
            var text = new StringBuilder(Lines * 7);
            for (var i = 0; i < Lines; i++)
                text.Append(i.ToString("D6")).Append(newline);

            var queued = await IssueAsync(() => session.WriteInputAsync(text.ToString()));
            await Assert.That(queued.IsCompleted).IsFalse();

            var last = Task.Run(() => session.Input.Write(Encoding.ASCII.GetBytes(Lines.ToString("D6") + newline)));
            // EOF is staged now, behind everything queued; sent ahead of it, the count would come up short.
            session.SendEof();

            var drain = DrainAsync(session);
            await queued.AsTask().WaitAsync(Timeout);
            await last.WaitAsync(Timeout);
            await session.WaitForExitAsync().WaitAsync(Timeout);
            await drain.WaitAsync(Timeout);

            var count = int.Parse(File.ReadAllText(countFile).Trim());
            await Assert.That(count).IsEqualTo(Lines + 1);
        }
        finally
        {
            File.Delete(countFile);
        }
    }

    /// <summary>Canceling a queued write completes it at once; the child keeps running.</summary>
    [Test]
    public async Task CancelingAQueuedWriteCompletesIt()
    {
        await using var session = await StartWithInputBlockedAsync();
        using var cts = new CancellationTokenSource();

        var write = await IssueAsync(() => session.WriteInputAsync(new byte[4 * 1024 * 1024], cts.Token));
        await Assert.That(write.IsCompleted).IsFalse();

        await cts.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await write.AsTask().WaitAsync(Timeout));
        await Assert.That(session.HasExited).IsFalse();
    }

    /// <summary>Disposal fails a queued write at once, without waiting for the transport.</summary>
    [Test]
    public async Task DisposeFailsAQueuedWrite()
    {
        var session = await StartWithInputBlockedAsync();
        try
        {
            var write = await IssueAsync(() => session.WriteInputAsync(new byte[4 * 1024 * 1024]));
            await Assert.That(write.IsCompleted).IsFalse();

            session.Dispose();

            await Assert.ThrowsAsync<ObjectDisposedException>(async () => await write.AsTask().WaitAsync(Timeout));
        }
        finally
        {
            await session.DisposeAsync().AsTask().WaitAsync(Timeout);
        }
    }

    /// <summary>
    /// Starts a child whose input is not taken: on Unix it never reads, so the tty input queue fills. On Windows
    /// ConPTY takes input while its output flows (plan F9), so the child floods output nobody reads instead (F11).
    /// </summary>
    private static async Task<PtySession> StartWithInputBlockedAsync()
    {
        if (!OperatingSystem.IsWindows())
            return Pty.Start(Spawn("sleep", ["30"]));

        var session = Pty.Start(Spawn(ComSpec, ["/c", "for /L %i in (1,1,100000000) do @echo %i"]));
        await Task.Delay(BackUpDelay);
        return session;
    }

    /// <summary>
    /// Floods 20,000 lines of output, then counts stdin lines until end of input and writes the count to
    /// <paramref name="countFile"/>. Until somebody reads the output, the child is blocked and reads no input.
    /// </summary>
    private static PtyStartInfo FloodThenCountLinesChild(string countFile) =>
        OperatingSystem.IsWindows()
            ? new PtyStartInfo
            {
                FileName = ComSpec,
                // By full path: a PATH with Unix tools first resolves a bare find to another program.
                CommandLine = $"/s /c \"(for /L %i in (1,1,20000) do @echo flood%i) & \"{WindowsFind}\" /v /c \"\" > \"{countFile}\"\"",
                Size = new PtySize(80, 24),
            }
            : Spawn("sh", ["-c", "i=0; while [ $i -lt 20000 ]; do echo flood$i; i=$((i+1)); done; wc -l > \"$1\"", "sh", countFile]);

    /// <summary>
    /// Issues a write off the test thread. The call itself must return at once; one that blocks fails the test
    /// instead of hanging it.
    /// </summary>
    private static Task<ValueTask> IssueAsync(Func<ValueTask> write) => Task.Run(write).WaitAsync(TimeSpan.FromSeconds(5));

    private static async Task DrainAsync(PtySession session)
    {
        await foreach (var _ in session.ReadOutputAsync())
        {
        }
    }

    /// <summary>Long enough for a flooding child to fill the output ring and the transport behind it.</summary>
    private static readonly TimeSpan BackUpDelay = TimeSpan.FromMilliseconds(500);

    private static string ComSpec => Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";

    private static string WindowsFind => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "find.exe");

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
