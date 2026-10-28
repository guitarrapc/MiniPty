using System.Runtime.InteropServices;
using System.Text;
using MiniPty.Console;
using MiniPty.Console.Internal;

namespace MiniPty.Tests;

public sealed class PtyConsoleWindowsInputTests
{
    private const string EchoMarker = "pty-console-echo-Q";

    [Test]
    [NotInParallel]
    public async Task WindowsHostTerminal_ReadInput_PreservesQueuedMouseReport()
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
            WriteConsoleBytes("\u001b[?1000h\u001b[?1006h"u8);
            try
            {
                // Queue a real native mouse record before ReadInput starts. The old ReadConsoleInput
                // fallback discarded it; ReadFile must encode it using the negotiated SGR mouse mode.
                InjectMousePress();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var bytes = new byte[64];
                var read = terminal.ReadInput(bytes, timeout.Token);
                await Assert.That(Encoding.UTF8.GetString(bytes, 0, read)).IsEqualTo("\u001b[<0;5;6M");
            }
            finally
            {
                WriteConsoleBytes("\u001b[?1000l\u001b[?1006l"u8);
            }
        }
        finally
        {
            if (allocatedConsole)
                ConsoleWindowsInterop.FreeConsole();
        }

        static unsafe void WriteConsoleBytes(ReadOnlySpan<byte> bytes)
        {
            fixed (byte* pointer = bytes)
            {
                if (!ConsoleWindowsInterop.WriteFile(ConsoleWindowsInterop.GetStdHandle(ConsoleWindowsInterop.StdOutputHandle),
                    pointer, (uint)bytes.Length, out _, IntPtr.Zero))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            }
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
