using TerminalEmulator;

namespace MiniPty.Tests;

public sealed class TerminalRunTests
{
    [Test]
    public async Task ClosingLiveSessionObservesExitAndReader()
    {
        var run = new TerminalRun();
        await using var session = MiniPty.Pty.Start(new MiniPty.PtyStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            Arguments = OperatingSystem.IsWindows() ? ["/c", "set /p DUMMY="] : ["-c", "read dummy"],
        });
        var output = DrainAsync(session);
        var wait = run.WaitAsync(session.WaitForExitAsync(), output);
        run.BeginClose();
        await session.DisposeAsync();
        await Assert.That(await wait.WaitAsync(TimeSpan.FromSeconds(10))).IsFalse();
        await Assert.That(output.IsCompleted).IsTrue();
    }

    private static async Task DrainAsync(MiniPty.PtySession session)
    {
        await foreach (var chunk in session.ReadOutputAsync())
            _ = chunk.Data.Length;
    }

    [Test]
    public async Task CloseObservesBothTasksBeforeCompleting()
    {
        var run = new TerminalRun();
        var exit = new TaskCompletionSource();
        var output = new TaskCompletionSource();
        var wait = run.WaitAsync(exit.Task, output.Task);
        run.BeginClose();
        exit.SetException(new ObjectDisposedException("session"));
        await Assert.That(wait.IsCompleted).IsFalse();
        output.SetException(new ObjectDisposedException("session"));
        await Assert.That(await wait).IsFalse();
    }

    [Test]
    public async Task NormalCompletionWaitsForFinalOutput()
    {
        var run = new TerminalRun();
        var output = new TaskCompletionSource();
        var wait = run.WaitAsync(Task.CompletedTask, output.Task);
        await Assert.That(wait.IsCompleted).IsFalse();
        output.SetResult();
        await Assert.That(await wait).IsTrue();
    }

    [Test]
    public async Task CloseAfterSuccessfulExitSkipsUiUpdates()
    {
        var run = new TerminalRun();
        var output = new TaskCompletionSource();
        var wait = run.WaitAsync(Task.CompletedTask, output.Task);
        run.BeginClose();
        output.SetResult();
        await Assert.That(await wait).IsFalse();
    }

    [Test]
    public async Task UnexpectedDisposalRemainsAnError()
    {
        var run = new TerminalRun();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => run.WaitAsync(
            Task.FromException(new ObjectDisposedException("session")), Task.CompletedTask));
    }

    [Test]
    public async Task ClosingDoesNotHideOutputFailures()
    {
        var run = new TerminalRun();
        run.BeginClose();
        await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(
            Task.CompletedTask, Task.FromException(new IOException("read failed"))));
    }

    [Test]
    public async Task DisposalDoesNotMaskAnotherFailureDuringClose()
    {
        var run = new TerminalRun();
        run.BeginClose();
        // WhenAll throws the first failure, but its other failure must prevent the disposal-only suppression.
        await Assert.ThrowsAsync<ObjectDisposedException>(() => run.WaitAsync(
            Task.FromException(new ObjectDisposedException("session")),
            Task.FromException(new IOException("read failed"))));
    }

    [Test]
    public async Task ClosingDoesNotSuppressCancellation()
    {
        var run = new TerminalRun();
        run.BeginClose();
        await Assert.ThrowsAsync<TaskCanceledException>(() => run.WaitAsync(
            Task.FromCanceled(new CancellationToken(true)), Task.CompletedTask));
    }
}
