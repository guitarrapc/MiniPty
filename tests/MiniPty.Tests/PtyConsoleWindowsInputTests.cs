using System.Runtime.InteropServices;
using System.Text;
using MiniPty.Console;
using MiniPty.Console.Internal;

namespace MiniPty.Tests;

/// <summary>
/// Host console input tests. They share one process-wide console input queue (the attached console or one
/// allocated per test), so they run one at a time: a concurrent read would consume another test's records.
/// </summary>
[NotInParallel(nameof(PtyConsoleWindowsInputTests))]
public sealed class PtyConsoleWindowsInputTests
{
    private const string EchoMarker = "pty-console-echo-Q";

    [Test]
    public async Task WindowsHostTerminal_ReadInput_SkipsQueuedMouseRecordBeforeKey()
    {
        if (!OperatingSystem.IsWindows())
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
            // A native mouse record queued ahead of a key must neither block nor corrupt the key read. The
            // former ReadConsoleInput fallback consumed such records and decoded keys with its own struct
            // layout. WriteConsoleInput records bypass conhost's mouse-mode encoding, so an injected record
            // cannot reproduce a terminal's SGR report; TuiDemo's smoke mode covers that end to end.
            InjectMousePress();
            WindowsConsoleInputInjector.InjectUnicodeKeyDown('k');
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var bytes = new byte[64];
            var read = terminal.ReadInput(bytes, timeout.Token);
            await Assert.That(Encoding.UTF8.GetString(bytes, 0, read)).IsEqualTo("k");
        }
        finally
        {
            if (allocatedConsole)
                ConsoleWindowsInterop.FreeConsole();
        }

        static unsafe void InjectMousePress()
        {
            var record = new ConsoleWindowsInterop.InputRecord
            {
                EventType = ConsoleWindowsInterop.MouseEvent,
                MouseEvent = new ConsoleWindowsInterop.MouseEventRecord
                {
                    MousePosition = new ConsoleWindowsInterop.Coord { X = 4, Y = 5 },
                    ButtonState = 1,
                },
            };
            if (!ConsoleWindowsInterop.WriteConsoleInput(ConsoleWindowsInterop.GetStdHandle(ConsoleWindowsInterop.StdInputHandle),
                &record, 1, out _))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

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

            await WaitForTextAsync(output, outputLock, "Q", EchoMarker, cts.Token);

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

            // Bound the read: a wrongly laid-out record yields no VT bytes and would block ReadFile forever.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Span<byte> buffer = stackalloc byte[8];
            var read = terminal.ReadInput(buffer, timeout.Token);
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

            await WaitForTextAsync(output, outputLock, "Q", EchoMarker, cts.Token);

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

    private static async Task WaitForTextAsync(
        StringBuilder output,
        object outputLock,
        string firstText,
        string secondText,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (outputLock)
            {
                var text = output.ToString();
                if (text.Contains(firstText, StringComparison.Ordinal)
                    && text.Contains(secondText, StringComparison.Ordinal))
                {
                    return;
                }
            }

            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }
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
