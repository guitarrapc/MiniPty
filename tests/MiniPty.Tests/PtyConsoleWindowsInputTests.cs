using System.Runtime.InteropServices;
using System.Text;
using MiniPty.Console;
using MiniPty.Console.Internal;

namespace MiniPty.Tests;

/// <summary>
/// Host console input tests. They share one process-wide console input queue (the attached console or one
/// allocated per test), so they run one at a time: a concurrent read would consume another test's records.
/// Each test starts from an empty queue. The reset does not absorb an orphaned console read, so a host terminal
/// that left one behind on detach (conhost keeps a <c>CancelIoEx</c>-cancelled read registered and it swallows the
/// next record) fails the test that follows as well as the dedicated regression tests.
/// </summary>
/// <remarks>
/// Without a console (redirected test host where <c>AllocConsole</c> fails) these tests return early, so a green
/// run does not prove they executed. Run them on a real console after changing the Windows input path.
/// </remarks>
[NotInParallel(nameof(PtyConsoleWindowsInputTests))]
public sealed class PtyConsoleWindowsInputTests
{
    private const string ReadyMarker = "pty-console-ready";
    private const string EchoMarker = "pty-console-echo";

    [Test]
    public async Task WindowsHostTerminal_ReadInput_SkipsQueuedMouseRecordBeforeKey()
    {
        if (!TryAcquireConsole(out var allocatedConsole))
            return;

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
            ReleaseConsole(allocatedConsole);
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
    public async Task WindowsHostTerminal_ReadInput_ReturnsInjectedUnicodeChar()
    {
        if (!TryAcquireConsole(out var allocatedConsole))
            return;

        try
        {
            using var terminal = new WindowsHostTerminal();
            WindowsConsoleInputInjector.InjectUnicodeKeyDown('Z');

            // Bound the read: a wrongly laid-out record yields no VT bytes and would block ReadFile forever.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var bytes = new byte[8];
            var read = terminal.ReadInput(bytes, timeout.Token);
            await Assert.That(Encoding.UTF8.GetString(bytes, 0, read)).IsEqualTo("Z");
        }
        finally
        {
            ReleaseConsole(allocatedConsole);
        }
    }

    [Test]
    public async Task WindowsHostTerminal_ReadInput_EncodesNonAsciiAsUtf8AndRestoresInputCodePage()
    {
        if (!TryAcquireConsole(out var allocatedConsole))
            return;

        var originalCodePage = ConsoleWindowsInterop.GetConsoleCP();
        try
        {
            // Start from a legacy code page so the test fails if the host forwards ReadFile's legacy bytes
            // (437 is installed on every Windows edition; it has no '日' and encodes 'é' as 0x82).
            await Assert.That(ConsoleWindowsInterop.SetConsoleCP(437)).IsTrue();
            using (var terminal = new WindowsHostTerminal())
            {
                WindowsConsoleInputInjector.InjectUnicodeKeyDown('é');
                WindowsConsoleInputInjector.InjectUnicodeKeyDown('日');

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var bytes = new byte[16];
                var read = terminal.ReadInput(bytes, timeout.Token);
                await Assert.That(Convert.ToHexString(bytes, 0, read)).IsEqualTo(Convert.ToHexString("é日"u8));
            }

            await Assert.That(ConsoleWindowsInterop.GetConsoleCP()).IsEqualTo(437u);
        }
        finally
        {
            ConsoleWindowsInterop.SetConsoleCP(originalCodePage);
            ReleaseConsole(allocatedConsole);
        }
    }

    [Test]
    public async Task WindowsHostTerminal_CancelledBlockedRead_DoesNotSwallowNextKey()
    {
        if (!TryAcquireConsole(out var allocatedConsole))
            return;

        try
        {
            using (var terminal = new WindowsHostTerminal())
            {
                using var cancel = new CancellationTokenSource();
                var reader = new Thread(() => terminal.ReadInput(new byte[16], cancel.Token));
                reader.Start();
                await Task.Delay(300); // let the read block in ReadFile
                cancel.Cancel();
                await Assert.That(reader.Join(TimeSpan.FromSeconds(5))).IsTrue();
            }

            // The wake sentinel must not stay queued, and no orphaned read may consume the next key.
            await Assert.That(PendingInputEvents()).IsEqualTo(0u);
            using (var terminal = new WindowsHostTerminal())
            {
                WindowsConsoleInputInjector.InjectUnicodeKeyDown('r');
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var bytes = new byte[8];
                var read = terminal.ReadInput(bytes, timeout.Token);
                await Assert.That(Encoding.UTF8.GetString(bytes, 0, read)).IsEqualTo("r");
            }
        }
        finally
        {
            ReleaseConsole(allocatedConsole);
        }
    }

    [Test]
    public async Task WindowsHostTerminal_ReadInput_AlreadyCancelled_ReturnsZeroAndLeavesQueuedKey()
    {
        if (!TryAcquireConsole(out var allocatedConsole))
            return;

        try
        {
            using var terminal = new WindowsHostTerminal();
            WindowsConsoleInputInjector.InjectUnicodeKeyDown('k');
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();

            // No ReadFile is issued, so the queued key stays for the next read and no sentinel is left behind.
            await Assert.That(terminal.ReadInput(new byte[8], cancelled.Token)).IsEqualTo(0);
            await Assert.That(PendingInputEvents()).IsEqualTo(1u);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var bytes = new byte[8];
            var read = terminal.ReadInput(bytes, timeout.Token);
            await Assert.That(Encoding.UTF8.GetString(bytes, 0, read)).IsEqualTo("k");
        }
        finally
        {
            ReleaseConsole(allocatedConsole);
        }
    }

    [Test]
    public Task Attach_ForwardsInjectedConsoleInput_OnWindows() =>
        AssertAttachForwardsKeyAsync('Q');

    [Test]
    public Task Attach_ForwardsInjectedNonAsciiInput_OnWindows() =>
        AssertAttachForwardsKeyAsync('日');

    [Test]
    public Task Attach_ReattachAfterDetach_ForwardsFirstKey_OnWindows() =>
        AssertAttachForwardsKeyAsync('R', detachOnceFirst: true);

    /// <summary>
    /// Attaches the host console to a child that reports readiness, reads one key, and echoes it back. The key is
    /// injected only after the child is ready; <c>Console.ReadKey</c> returns without Enter, unlike a cooked-mode
    /// <c>Console.In.Read</c>, which would wait for a whole line.
    /// </summary>
    private static async Task AssertAttachForwardsKeyAsync(char key, bool detachOnceFirst = false)
    {
        if (!TryAcquireConsole(out var allocatedConsole))
            return;

        try
        {
            await using var session = Pty.Start(CreateReadKeyEchoStartInfo());
            var output = new StringBuilder();
            var outputLock = new object();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var pumpTask = PumpOutputAsync(session, output, outputLock, cts.Token);

            if (detachOnceFirst)
            {
                // Detach while the input thread is blocked in ReadFile; the first key of the next attach must
                // still arrive. Cancelling that read with CancelIoEx left an orphaned read that swallowed it.
                using (PtyConsoleInput.Attach(session))
                    await Task.Delay(300, cts.Token);
            }

            using (PtyConsoleInput.Attach(session))
            {
                await WaitForTextAsync(output, outputLock, ReadyMarker, cts.Token);
                WindowsConsoleInputInjector.InjectUnicodeKeyDown(key);
                await WaitForTextAsync(output, outputLock, key + EchoMarker, cts.Token);
            }

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
            ReleaseConsole(allocatedConsole);
        }
    }

    /// <summary>
    /// Ensures the process has a console whose input queue holds nothing left by an earlier test. Returns false
    /// (the caller skips) off Windows, or when the test host is redirected and no console can be allocated.
    /// </summary>
    private static bool TryAcquireConsole(out bool allocated)
    {
        allocated = false;
        if (!OperatingSystem.IsWindows())
            return false;

        if (System.Console.IsInputRedirected || System.Console.IsOutputRedirected)
        {
            if (!ConsoleWindowsInterop.AllocConsole())
                return false;
            allocated = true;
        }

        WindowsConsoleInputInjector.ResetInputQueue();
        return true;
    }

    private static uint PendingInputEvents() =>
        ConsoleWindowsInterop.GetNumberOfConsoleInputEvents(
            ConsoleWindowsInterop.GetStdHandle(ConsoleWindowsInterop.StdInputHandle), out var count)
            ? count
            : throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());

    private static void ReleaseConsole(bool allocated)
    {
        if (allocated)
            ConsoleWindowsInterop.FreeConsole();
    }

    /// <summary>
    /// Waits until the child's text, with VT control sequences removed, contains <paramref name="text"/>. ConPTY
    /// may place control sequences between characters written in one call; on exit it was observed emitting
    /// <c>ESC[?9001l ESC[?1004l</c> between an echoed key and the marker that followed it.
    /// </summary>
    private static async Task WaitForTextAsync(
        StringBuilder output,
        object outputLock,
        string text,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            lock (outputLock)
            {
                if (StripControlSequences(output.ToString()).Contains(text, StringComparison.Ordinal))
                    return;
            }

            try
            {
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                lock (outputLock)
                    throw new TimeoutException($"Output did not contain '{text}'. Output: {output.ToString().Replace("\u001b", "<ESC>")}");
            }
        }
    }

    /// <summary>Removes CSI (<c>ESC [ ... final</c>) and OSC (<c>ESC ] ... BEL/ST</c>) sequences and other escapes.</summary>
    private static string StripControlSequences(string text)
    {
        var result = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\u001b' || i + 1 >= text.Length)
            {
                result.Append(text[i]);
                continue;
            }

            var kind = text[++i];
            if (kind == '[')
            {
                // Parameter and intermediate bytes run until a final byte in 0x40-0x7E.
                while (i + 1 < text.Length && (text[i + 1] < '@' || text[i + 1] > '~'))
                    i++;
                i++;
            }
            else if (kind == ']')
            {
                while (i + 1 < text.Length && text[i + 1] != '\u0007' && !(text[i + 1] == '\u001b' && i + 2 < text.Length && text[i + 2] == '\\'))
                    i++;
                i += text.Length > i + 1 && text[i + 1] == '\u001b' ? 2 : 1;
            }
        }

        return result.ToString();
    }

    private static async Task PumpOutputAsync(
        PtySession session,
        StringBuilder sink,
        object outputLock,
        CancellationToken cancellationToken)
    {
        // Stateful decoding: a multi-byte character can be split across output chunks.
        var decoder = Encoding.UTF8.GetDecoder();
        var chars = new char[4096];
        await foreach (var chunk in session.ReadOutputAsync(cancellationToken).ConfigureAwait(false))
        {
            var count = decoder.GetChars(chunk.Data.Span, chars, flush: false);
            lock (outputLock)
                sink.Append(chars, 0, count);
        }
    }

    private static PtyStartInfo CreateReadKeyEchoStartInfo()
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
                "[Console]::Out.Write('" + ReadyMarker + "'); [Console]::Out.Flush(); "
                    + "$k=[Console]::ReadKey($true); [Console]::Out.Write($k.KeyChar); "
                    + "[Console]::Out.Write('" + EchoMarker + "'); [Console]::Out.Flush()",
            ],
            Size = new PtySize(80, 24),
        };
    }
}
