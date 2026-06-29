using System.Runtime.InteropServices;
using System.Text;
using MiniPty.Console;
using MiniPty.Console.Internal;

namespace MiniPty.Tests;

public sealed class PtyConsoleWindowsInputTests
{
    private const string EchoMarker = "pty-console-echo-Q";

    [Test]
    public async Task Attach_ForwardsInjectedConsoleInput_OnWindows()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var allocatedConsole = false;
        if (System.Console.IsInputRedirected || System.Console.IsOutputRedirected)
        {
            if (!ConsoleWindowsInterop.AllocConsole())
                return;

            allocatedConsole = true;
        }

        try
        {
            await using var session = Pty.Start(CreateEchoOneCharStartInfo());
            var output = new StringBuilder();
            var outputLock = new object();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var pumpTask = PumpOutputAsync(session, output, outputLock, cts.Token);

            using var consoleInput = PtyConsoleInput.Attach(session);

            _ = Task.Run(async () =>
            {
                await Task.Delay(250, cts.Token).ConfigureAwait(false);
                WindowsConsoleInputInjector.InjectUnicodeKeyDown('Q');
            }, cts.Token);

            PumpUntilText(consoleInput, output, outputLock, "Q", EchoMarker, cts.Token);

            cts.Cancel();
            try
            {
                await pumpTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            session.Kill();
        }
        finally
        {
            if (allocatedConsole)
                ConsoleWindowsInterop.FreeConsole();
        }
    }

    [Test]
    public async Task WindowsHostTerminal_ReadInput_ReturnsInjectedUnicodeChar()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var allocatedConsole = false;
        if (System.Console.IsInputRedirected || System.Console.IsOutputRedirected)
        {
            if (!ConsoleWindowsInterop.AllocConsole())
                return;

            allocatedConsole = true;
        }

        try
        {
            using var terminal = new WindowsHostTerminal();
            WindowsConsoleInputInjector.InjectUnicodeKeyDown('Z');

            Span<byte> buffer = stackalloc byte[8];
            var read = terminal.ReadInput(buffer);
            var first = buffer[0];
            await Assert.That(read).IsEqualTo(1);
            await Assert.That(first).IsEqualTo((byte)'Z');
        }
        finally
        {
            if (allocatedConsole)
                ConsoleWindowsInterop.FreeConsole();
        }
    }

    [Test]
    public async Task WindowsHostTerminal_ReadInput_ReturnsInjectedUtf8Byte()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var allocatedConsole = false;
        if (System.Console.IsInputRedirected || System.Console.IsOutputRedirected)
        {
            if (!ConsoleWindowsInterop.AllocConsole())
                return;

            allocatedConsole = true;
        }

        try
        {
            using var terminal = new WindowsHostTerminal();
            WindowsConsoleInputInjector.InjectUtf8Byte((byte)'X');

            Span<byte> buffer = stackalloc byte[8];
            var read = terminal.ReadInput(buffer);
            var first = buffer[0];
            await Assert.That(read).IsEqualTo(1);
            await Assert.That(first).IsEqualTo((byte)'X');
        }
        finally
        {
            if (allocatedConsole)
                ConsoleWindowsInterop.FreeConsole();
        }
    }

    [Test]
    public async Task Attach_ForwardsInjectedUtf8Input_OnWindows()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var allocatedConsole = false;
        if (System.Console.IsInputRedirected || System.Console.IsOutputRedirected)
        {
            if (!ConsoleWindowsInterop.AllocConsole())
                return;

            allocatedConsole = true;
        }

        try
        {
            await using var session = Pty.Start(CreateEchoOneCharStartInfo());
            var output = new StringBuilder();
            var outputLock = new object();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var pumpTask = PumpOutputAsync(session, output, outputLock, cts.Token);

            using var consoleInput = PtyConsoleInput.Attach(session);

            _ = Task.Run(async () =>
            {
                await Task.Delay(250, cts.Token).ConfigureAwait(false);
                WindowsConsoleInputInjector.InjectUtf8Byte((byte)'Q');
            }, cts.Token);

            PumpUntilText(consoleInput, output, outputLock, "Q", EchoMarker, cts.Token);

            cts.Cancel();
            try
            {
                await pumpTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            session.Kill();
        }
        finally
        {
            if (allocatedConsole)
                ConsoleWindowsInterop.FreeConsole();
        }
    }

    private static void PumpUntilText(
        PtyConsoleInputHandle consoleInput,
        StringBuilder output,
        object outputLock,
        string firstText,
        string secondText,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            consoleInput.PumpInputOnce(cancellationToken);

            lock (outputLock)
            {
                var text = output.ToString();
                if (text.Contains(firstText, StringComparison.Ordinal)
                    && text.Contains(secondText, StringComparison.Ordinal))
                {
                    return;
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task PumpOutputAsync(
        PtySession session,
        StringBuilder sink,
        object outputLock,
        CancellationToken cancellationToken)
    {
        await foreach (var chunk in session.ReadOutputAsync(cancellationToken).ConfigureAwait(false))
        {
            var text = Encoding.UTF8.GetString(chunk.Data.Span);
            lock (outputLock)
                sink.Append(text);
        }
    }

    private static PtyStartInfo CreateEchoOneCharStartInfo()
    {
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");

        return new PtyStartInfo
        {
            FileName = powershell,
            Arguments =
            [
                "-NoLogo",
                "-NoProfile",
                "-Command",
                "$b=[Console]::In.Read(); [Console]::Out.Write([char]$b); [Console]::Out.Write('" + EchoMarker + "'); [Console]::Out.Flush()",
            ],
            Size = new PtySize(80, 24),
        };
    }
}
