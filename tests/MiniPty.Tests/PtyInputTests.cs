using System.Text;

namespace MiniPty.Tests;

/// <summary>
/// Stdin writes: the synchronous fast path, and the ordered queue that takes over when the transport stops taking
/// input. On Unix the tty input queue fills when the child does not read, and the reader thread writes the queue. On
/// Windows ConPTY stops reading input while its output is backed up, and I/O completions write the queue.
/// </summary>
public sealed class PtyInputTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>A write the terminal takes at once is complete when the call returns.</summary>
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
        await Task.Delay(IssueDelay);
        // EOF is staged behind everything queued. Sent ahead of it, awk would stop early.
        session.SendEof();

        await queued.AsTask().WaitAsync(Timeout);
        await last.WaitAsync(Timeout);
        var result = await output.WaitAsync(Timeout);

        await Assert.That(result).Contains($"RESULT:ok:{Lines + 1}");
    }

    /// <summary>
    /// Input written while output nobody reads has backed up is queued instead of blocking the caller. It reaches the
    /// child in issue order once output is read, and EOF follows the last queued byte.
    /// </summary>
    [Test]
    public async Task QueuedInputIsDeliveredInOrderWithEofAfterItWhileOutputIsBackedUp()
    {
        const int Lines = 3000;
        var copyFile = Path.GetTempFileName();
        try
        {
            await using var session = Pty.Start(FloodThenCopyInputChild(copyFile));
            await Task.Delay(BackUpDelay);

            // 21 KB of numbered lines: more than the ConPTY input pipe or a tty input queue takes while the child is
            // blocked writing output, and more than one 4 KiB pipe write, so the queued parts must keep their order.
            var queued = await IssueAsync(() => session.WriteInputAsync(NumberedLines(0, Lines)));
            await Assert.That(queued.IsCompleted).IsFalse();

            // A blocking write issued behind the queue waits its turn.
            var last = Task.Run(() => session.Input.Write(Encoding.ASCII.GetBytes(NumberedLines(Lines, 1))));
            await Task.Delay(IssueDelay);
            await Assert.That(last.IsCompleted).IsFalse();

            session.SendEof();
            // Staging ticks pass while the queue is still blocked, so on Windows EOF sent then would cut the copy short.
            // On Unix a full tty refuses EOT anyway, so this does not check the Unix guard.
            await Task.Delay(EofTicks);
            await Assert.That(queued.IsCompleted).IsFalse();

            var drain = DrainAsync(session);
            await queued.AsTask().WaitAsync(Timeout);
            await last.WaitAsync(Timeout);
            await session.WaitForExitAsync().WaitAsync(Timeout);
            await drain.WaitAsync(Timeout);

            await Assert.That(ReadLines(copyFile)).IsEqualTo(ExpectedLines(0, Lines + 1));
        }
        finally
        {
            DeleteBestEffort(copyFile);
        }
    }

    /// <summary>
    /// Canceling a write in the middle of the queue discards only that write. The writes around it complete and reach
    /// the child in order.
    /// </summary>
    [Test]
    public async Task CancelingAQueuedWriteKeepsTheOthersInOrder()
    {
        var copyFile = Path.GetTempFileName();
        try
        {
            await using var session = Pty.Start(FloodThenCopyInputChild(copyFile));
            await Task.Delay(BackUpDelay);

            // The first write stays partly unwritten, so the canceled one behind it has handed nothing over yet.
            var first = await IssueAsync(() => session.WriteInputAsync(NumberedLines(0, QueueFillingLines)));
            using var cts = new CancellationTokenSource();
            var middle = session.WriteInputAsync(Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("BBBBBB" + Newline, 1000))), cts.Token);
            var third = session.WriteInputAsync(NumberedLines(QueueFillingLines, 1000));
            await Assert.That(first.IsCompleted).IsFalse();
            await Assert.That(third.IsCompleted).IsFalse();

            await cts.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await middle.AsTask().WaitAsync(Timeout));

            session.SendEof();
            var drain = DrainAsync(session);
            await first.AsTask().WaitAsync(Timeout);
            await third.AsTask().WaitAsync(Timeout);
            await session.WaitForExitAsync().WaitAsync(Timeout);
            await drain.WaitAsync(Timeout);

            await Assert.That(ReadLines(copyFile)).IsEqualTo(ExpectedLines(0, QueueFillingLines + 1000));
        }
        finally
        {
            DeleteBestEffort(copyFile);
        }
    }

    /// <summary>
    /// A canceled write that the terminal took only part of leaves a partial line. SendEof must still end input, so it
    /// has to go by the last byte the terminal took, not the last byte of the caller's buffer.
    /// </summary>
    [Test]
    public async Task SendEofEndsInputAfterACanceledWriteLeftAPartialLine()
    {
        await using var session = Pty.Start(FloodThenReadToEndChild());
        await Task.Delay(BackUpDelay);

        // The terminal takes only the start of the write, and that start must end inside a line, while the caller's
        // buffer ends with a line terminator. On Linux one 4 MiB line puts the cut inside it. Windows takes whole 4 KiB
        // parts and macOS about 1 KiB, so both get short lines whose length divides neither amount. A long line would
        // not work there: the Windows console hands a line longer than its read buffer to the reader without waiting
        // for the CR, and a macOS tty drops the rest of an overlong line, EOT included.
        byte[] input;
        if (OperatingSystem.IsWindows())
        {
            input = Encoding.ASCII.GetBytes(NumberedLines(0, 100000));
        }
        else if (OperatingSystem.IsMacOS())
        {
            input = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("xxxxxxxx\n", 500000)));
        }
        else
        {
            input = new byte[4 * 1024 * 1024];
            input.AsSpan().Fill((byte)'x');
            input[^1] = (byte)'\n';
        }

        using var cts = new CancellationTokenSource();
        var write = await IssueAsync(() => session.WriteInputAsync(input, cts.Token));
        await Assert.That(write.IsCompleted).IsFalse();
        await cts.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await write.AsTask().WaitAsync(Timeout));

        session.SendEof();
        var drain = DrainAsync(session);
        // Exit code 0: a child that ended because input was closed instead, or that never started, exits otherwise.
        await Assert.That(await session.WaitForExitAsync().WaitAsync(Timeout)).IsEqualTo(0);
        await drain.WaitAsync(Timeout);
    }

    /// <summary>
    /// SendEof before any input, then a write while EOF is still staged: the child still gets end of input after the
    /// write. On Windows, input that never carried bytes ends by closing the pipe after a wait, and a write during
    /// that wait rules the close out. Unix ends input only with EOT, never by closing the master, so it has no such
    /// case.
    /// </summary>
    [Test]
    public async Task SendEofEndsInputWhenAWriteFollowsItWhileStaged()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var copyFile = Path.GetTempFileName();
        try
        {
            await using var session = Pty.Start(CopyInputChild(copyFile));
            session.SendEof();
            // Past the first staging tick, and well inside the wait before an unused input pipe is closed.
            await Task.Delay(EofTicks);
            await session.WriteInputAsync("x" + Newline);

            var drain = DrainAsync(session);
            // Exit code 0: closing the input pipe after bytes ends the child with STATUS_CONTROL_C_EXIT instead.
            await Assert.That(await session.WaitForExitAsync().WaitAsync(Timeout)).IsEqualTo(0);
            await drain.WaitAsync(Timeout);

            await Assert.That(ReadLines(copyFile)).IsEqualTo("x");
        }
        finally
        {
            DeleteBestEffort(copyFile);
        }
    }

    /// <summary>Canceling a queued write completes it at once; the child keeps running.</summary>
    [Test]
    public async Task CancelingAQueuedWriteCompletesIt()
    {
        await using var session = await StartWithInputBlockedAsync();
        using var cts = new CancellationTokenSource();

        var write = await IssueAsync(() => session.WriteInputAsync(BlockingPayload(), cts.Token));
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
            var write = await IssueAsync(() => session.WriteInputAsync(BlockingPayload()));
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
    /// A write still queued when output ends fails with <see cref="IOException"/>. Closing the transport ends output
    /// while the child keeps running, so nothing can take the queued bytes first.
    /// </summary>
    [Test]
    public async Task QueuedWriteFailsWhenOutputEnds()
    {
        await using var session = await StartWithInputBlockedAsync();
        var write = await IssueAsync(() => session.WriteInputAsync(BlockingPayload()));
        await Assert.That(write.IsCompleted).IsFalse();

        // Closed off the test thread, because closing the pseudo-console while its output is backed up may wait for a
        // reader on older Windows builds. The queued write fails before that close, and draining then lets it finish.
        var close = Task.Run(session.CloseOutputTransport);
        await Assert.ThrowsAsync<IOException>(async () => await write.AsTask().WaitAsync(Timeout));
        var drain = DrainAsync(session);
        await close.WaitAsync(Timeout);
        await drain.WaitAsync(Timeout);
    }

    /// <summary>
    /// A queued write does not wait forever once the child is gone. It completes if the terminal still took the bytes,
    /// as a Linux tty can after the child exits, and otherwise fails with <see cref="IOException"/> when output ends.
    /// </summary>
    [Test]
    public async Task QueuedWriteFinishesWhenTheChildExits()
    {
        await using var session = await StartWithInputBlockedAsync();
        var write = await IssueAsync(() => session.WriteInputAsync(BlockingPayload()));
        await Assert.That(write.IsCompleted).IsFalse();

        session.Kill();
        var drain = DrainAsync(session);

        try
        {
            await write.AsTask().WaitAsync(Timeout);
        }
        catch (IOException)
        {
        }

        await drain.WaitAsync(Timeout);
    }

    /// <summary>
    /// A canceled token and a closed transport fail the returned task. The call itself does not throw, so a caller
    /// that does not await at once still sees the failure in the task.
    /// </summary>
    [Test]
    public async Task WriteFailuresAreReportedThroughTheTask()
    {
        await using var session = Pty.Start(IdleChild());
        var bytes = Encoding.ASCII.GetBytes("x" + Newline);
        var shortText = "x" + Newline;
        // Over the 256-byte limit for encoding on the stack, so this text goes through a pooled buffer.
        var longText = new string('x', 1000) + Newline;
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        Task[] canceledWrites =
        [
            Issue(() => session.WriteInputAsync(bytes, canceled.Token)),
            Issue(() => session.WriteInputAsync(shortText, cancellationToken: canceled.Token)),
            Issue(() => session.WriteInputAsync(longText, cancellationToken: canceled.Token)),
            Issue(() => session.Input.WriteAsync(bytes, canceled.Token)),
            Issue(() => session.Input.WriteAsync(bytes, 0, bytes.Length, canceled.Token)),
        ];
        foreach (var write in canceledWrites)
            await Assert.That(write.IsCanceled).IsTrue();

        session.CloseOutputTransport();
        Task[] closedWrites =
        [
            Issue(() => session.WriteInputAsync(bytes)),
            Issue(() => session.WriteInputAsync(shortText)),
            Issue(() => session.WriteInputAsync(longText)),
            Issue(() => session.Input.WriteAsync(bytes)),
            Issue(() => session.Input.WriteAsync(bytes, 0, bytes.Length)),
            Issue(() => Task.Factory.FromAsync(session.Input.BeginWrite, session.Input.EndWrite, bytes, 0, bytes.Length, null)),
        ];
        foreach (var write in closedWrites)
            await Assert.ThrowsAsync<IOException>(async () => await write);
    }

    /// <summary>
    /// A write through the input stream after disposal reports the disposal even when output had already ended, which
    /// closes input first on Unix. The drain returning means the end of output was handled before disposal.
    /// </summary>
    [Test]
    public async Task InputThrowsObjectDisposedAfterDisposeOnceOutputEnded()
    {
        var session = Pty.Start(OperatingSystem.IsWindows() ? Spawn(ComSpec, ["/c", "exit 0"]) : Spawn("sh", ["-c", "exit 0"]));
        await DrainAsync(session).WaitAsync(Timeout);
        session.Dispose();

        Assert.Throws<ObjectDisposedException>(() => session.Input.Write(Encoding.ASCII.GetBytes("x" + Newline)));
        await session.DisposeAsync().AsTask().WaitAsync(Timeout);
    }

    /// <summary>The input stream reports disposal the same way on every platform.</summary>
    [Test]
    public async Task InputThrowsObjectDisposedAfterDispose()
    {
        var session = Pty.Start(IdleChild());
        session.Dispose();

        Assert.Throws<ObjectDisposedException>(() => session.Input.Write(Encoding.ASCII.GetBytes("x" + Newline)));
        await session.DisposeAsync().AsTask().WaitAsync(Timeout);
    }

    /// <summary>
    /// Kill on cancellation also covers cancellation while <see cref="PtySession.CompleteAsync"/> is still writing its
    /// input. A token canceled up front is the deterministic case: the input write is the first step to see it.
    /// </summary>
    [Test]
    public async Task CompleteAsyncCanceledWhileWritingInputKillsTheChild()
    {
        await using var session = Pty.Start(IdleChild());
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await session.CompleteAsync(new PtyCompleteOptions { Input = "x" + Newline }, canceled.Token));

        // Far shorter than the idle child's own 30 seconds, so only a kill ends it in time.
        await session.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(session.HasExited).IsTrue();
    }

    /// <summary>
    /// Starts a child whose input is not taken: on Unix it never reads, so the tty input queue fills. On Windows
    /// ConPTY takes input while its output flows, so the child floods output nobody reads instead.
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
    /// Floods 20,000 lines of output, then copies stdin to <paramref name="copyFile"/> until end of input. Until
    /// somebody reads the output, the child is blocked and reads no input.
    /// </summary>
    private static PtyStartInfo FloodThenCopyInputChild(string copyFile) =>
        OperatingSystem.IsWindows()
            ? WindowsCommandLine($"(for /L %i in (1,1,20000) do @echo flood%i) & \"{WindowsFind}\" /v \"\" > \"{copyFile}\"")
            : Spawn("sh", ["-c", "i=0; while [ $i -lt 20000 ]; do echo flood$i; i=$((i+1)); done; cat > \"$1\"", "sh", copyFile]);

    /// <summary>
    /// Floods output like <see cref="FloodThenCopyInputChild"/>, then reads stdin to its end and discards it. The
    /// Windows reader is .NET console input, which ends at a Ctrl+Z that starts a line but reads past one inside a
    /// short line.
    /// </summary>
    private static PtyStartInfo FloodThenReadToEndChild() =>
        OperatingSystem.IsWindows()
            ? WindowsCommandLine($"(for /L %i in (1,1,20000) do @echo flood%i) & \"{WindowsPowerShell}\" -NoLogo -NoProfile -NonInteractive -Command \"$null = [Console]::In.ReadToEnd()\"")
            : Spawn("sh", ["-c", "i=0; while [ $i -lt 20000 ]; do echo flood$i; i=$((i+1)); done; cat > /dev/null"]);

    /// <summary>Copies stdin to <paramref name="copyFile"/> until end of input.</summary>
    private static PtyStartInfo CopyInputChild(string copyFile) =>
        OperatingSystem.IsWindows()
            ? WindowsCommandLine($"\"{WindowsFind}\" /v \"\" > \"{copyFile}\"")
            : Spawn("sh", ["-c", "cat > \"$1\"", "sh", copyFile]);

    /// <summary>Runs for 30 seconds without reading input.</summary>
    private static PtyStartInfo IdleChild() =>
        OperatingSystem.IsWindows()
            ? Spawn(ComSpec, ["/c", "ping -n 30 127.0.0.1 >nul"])
            : Spawn("sleep", ["30"]);

    private static PtyStartInfo WindowsCommandLine(string command) =>
        new() { FileName = ComSpec, CommandLine = $"/s /c \"{command}\"", Size = new PtySize(80, 24) };

    /// <summary>
    /// Lines enough that a write of them stays partly unwritten while the child is blocked.
    /// </summary>
    private static int QueueFillingLines => OperatingSystem.IsWindows() ? 3000 : 20000;

    /// <summary>
    /// 4 MiB of short complete lines for a child whose input is not taken. A canonical-mode tty may hold back a write
    /// only once a complete line is queued, and may drop the rest of an overlong line, so input without line
    /// terminators could be taken whole and never wait.
    /// </summary>
    private static byte[] BlockingPayload() =>
        Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(new string('z', 63) + Newline, 65536)));

    /// <summary>Line terminator that submits a line: ConPTY needs CR, a Unix tty accepts LF.</summary>
    private static string Newline => OperatingSystem.IsWindows() ? "\r" : "\n";

    private static string NumberedLines(int start, int count)
    {
        var text = new StringBuilder(count * 7);
        for (var i = start; i < start + count; i++)
            text.Append(i.ToString("D6")).Append(Newline);

        return text.ToString();
    }

    private static string ExpectedLines(int start, int count) =>
        string.Join('\n', Enumerable.Range(start, count).Select(i => i.ToString("D6")));

    private static string ReadLines(string path) => string.Join('\n', File.ReadAllLines(path));

    /// <summary>
    /// Deletes a temporary file. After a failed run the child may still hold it open, and that must not replace the
    /// test's own failure.
    /// </summary>
    private static void DeleteBestEffort(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// Issues a write off the test thread. The call itself must return at once; one that blocks fails the test
    /// instead of hanging it.
    /// </summary>
    private static Task<ValueTask> IssueAsync(Func<ValueTask> write) => Task.Run(write).WaitAsync(TimeSpan.FromSeconds(5));

    /// <summary>Issues a call that must neither block nor throw: its outcome belongs in the returned task.</summary>
    private static Task Issue(Func<ValueTask> write)
    {
        try
        {
            return write().AsTask();
        }
        catch (Exception ex)
        {
            return Task.FromException(new InvalidOperationException("The call threw instead of returning a failed task.", ex));
        }
    }

    private static Task Issue(Func<Task> write)
    {
        try
        {
            return write();
        }
        catch (Exception ex)
        {
            return Task.FromException(new InvalidOperationException("The call threw instead of returning a failed task.", ex));
        }
    }

    private static async Task DrainAsync(PtySession session)
    {
        await foreach (var _ in session.ReadOutputAsync())
        {
        }
    }

    /// <summary>Long enough for a flooding child to fill the output ring and the transport behind it.</summary>
    private static readonly TimeSpan BackUpDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>Long enough for a write started on another thread to be issued.</summary>
    private static readonly TimeSpan IssueDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>Several 100 ms EOF staging ticks.</summary>
    private static readonly TimeSpan EofTicks = TimeSpan.FromMilliseconds(400);

    private static string ComSpec => Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";

    // By full path: a PATH with Unix tools first resolves a bare find to another program.
    private static string WindowsFind => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "find.exe");

    private static string WindowsPowerShell =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

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
