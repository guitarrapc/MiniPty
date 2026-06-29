using System.Runtime.InteropServices;
using MiniPty.Console;

namespace MiniPty.Tests;

public sealed class PtyConsoleTests
{
    [Test]
    public async Task Attach_WhenHostNotInteractive_Throws()
    {
        if (!System.Console.IsInputRedirected && !System.Console.IsOutputRedirected)
            return;

        await using var session = Pty.Start(Exit0StartInfo());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
        {
            PtyConsoleInput.Attach(session);
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task Attach_SecondAttachOnSameSession_Throws()
    {
        if (System.Console.IsInputRedirected || System.Console.IsOutputRedirected)
            return;

        await using var session = Pty.Start(Exit0StartInfo());
        using var first = PtyConsoleInput.Attach(session);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
        {
            PtyConsoleInput.Attach(session);
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task Attach_AfterDispose_CanAttachAgain()
    {
        if (System.Console.IsInputRedirected || System.Console.IsOutputRedirected)
            return;

        await using var session = Pty.Start(InteractiveSizeStartInfo());
        using (PtyConsoleInput.Attach(session))
        {
        }

        using var second = PtyConsoleInput.Attach(session);
        await Assert.That(second).IsNotNull();
    }

    [Test]
    public async Task Attach_SyncsInitialSize()
    {
        if (System.Console.IsInputRedirected || System.Console.IsOutputRedirected)
            return;

        await using var session = Pty.Start(InteractiveSizeStartInfo());
        using var console = PtyConsoleInput.Attach(session);

        await Assert.That(session.Size.Columns).IsGreaterThan(0);
        await Assert.That(session.Size.Rows).IsGreaterThan(0);
    }

    [Test]
    public async Task Attach_WhenSessionNull_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            PtyConsoleInput.Attach(null!);
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task PumpInputOnce_AfterDispose_Throws()
    {
        if (System.Console.IsInputRedirected || System.Console.IsOutputRedirected)
            return;

        await using var session = Pty.Start(InteractiveSizeStartInfo());
        var handle = PtyConsoleInput.Attach(session);
        handle.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
        {
            handle.PumpInputOnce();
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task PumpInputUntil_AfterDispose_Throws()
    {
        if (System.Console.IsInputRedirected || System.Console.IsOutputRedirected)
            return;

        await using var session = Pty.Start(InteractiveSizeStartInfo());
        var handle = PtyConsoleInput.Attach(session);
        handle.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
        {
            handle.PumpInputUntil();
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task PumpInputOnce_IsNoOp()
    {
        if (System.Console.IsInputRedirected || System.Console.IsOutputRedirected)
            return;

        await using var session = Pty.Start(InteractiveSizeStartInfo());
        using var handle = PtyConsoleInput.Attach(session);
        handle.PumpInputOnce();
    }

    [Test]
    public async Task PumpInputUntil_ReturnsWhenTokenCanceled()
    {
        if (System.Console.IsInputRedirected || System.Console.IsOutputRedirected)
            return;

        await using var session = Pty.Start(InteractiveSizeStartInfo());
        using var handle = PtyConsoleInput.Attach(session);
        using var cts = new CancellationTokenSource();

        var waitTask = Task.Run(() => handle.PumpInputUntil(cts.Token), CancellationToken.None);
        await Task.Delay(50);
        cts.Cancel();
        await waitTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static PtyStartInfo Exit0StartInfo()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
            return new PtyStartInfo { FileName = cmd, Arguments = ["/c", "exit 0"] };
        }

        return new PtyStartInfo { FileName = "/bin/sh", Arguments = ["-c", "exit 0"] };
    }

    private static PtyStartInfo InteractiveSizeStartInfo()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
            return new PtyStartInfo { FileName = cmd, Arguments = ["/c", "timeout /t 30 /nobreak >nul"] };
        }

        return new PtyStartInfo { FileName = "/bin/sh", Arguments = ["-c", "sleep 30"] };
    }
}
