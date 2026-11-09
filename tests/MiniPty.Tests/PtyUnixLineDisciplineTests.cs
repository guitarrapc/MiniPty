using System.Text;

namespace MiniPty.Tests;

/// <summary>
/// The termios a Unix child starts with is set by MiniPty at spawn rather than left to the kernel default. The
/// Linux and macOS defaults lack <c>IUTF8</c>, and without it erasing a multibyte character removes one byte and
/// leaves invalid UTF-8 on the line the child reads. <see cref="PtySession.InputMode"/> reads two of its flags back
/// through the master, as the child has changed them.
/// </summary>
public sealed class PtyUnixLineDisciplineTests
{
    [Test]
    [Arguments("", true, true)]
    [Arguments("stty -echo; ", false, true)]
    [Arguments("stty -icanon; ", true, false)]
    [Arguments("stty -icanon -echo; ", false, false)]
    public async Task InputModeReportsTheModeTheChildSet(string stty, bool echo, bool canonical)
    {
        if (OperatingSystem.IsWindows())
            return;

        // The child prints the marker only once stty has returned, so the mode is set when the marker arrives. Without
        // stty it is the termios MiniPty sets at spawn. "stty -echo" alone is how a password prompt reads.
        await using var session = Pty.Start(new PtyStartInfo
        {
            FileName = "sh",
            Arguments = ["-c", stty + "printf ready; sleep 30"],
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var reader = session.ReadOutputAsync(cts.Token).GetAsyncEnumerator();
        using var output = new MemoryStream();
        await ReadUntilAsync(reader, output, "ready"u8.ToArray());

        await Assert.That(session.InputMode).IsEqualTo(new PtyInputMode(echo, canonical));
        session.Kill(PtySignal.Kill);
    }

    [Test]
    public async Task InputModeIsNullOnceTheChildHasExited()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var session = Pty.Start(new PtyStartInfo { FileName = "sh", Arguments = ["-c", "exit 0"] });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await session.WaitForExitAsync(cts.Token);

        await Assert.That(session.InputMode).IsNull();
    }

    [Test]
    public async Task InputModeIsNullAfterDisposal()
    {
        if (OperatingSystem.IsWindows())
            return;

        // Disposing again at the end of the test is a no-op; it only matters when an assertion fails first.
        await using var session = Pty.Start(new PtyStartInfo { FileName = "sh", Arguments = ["-c", "printf ready; sleep 30"] });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using (var reader = session.ReadOutputAsync(cts.Token).GetAsyncEnumerator())
        {
            using var output = new MemoryStream();
            await ReadUntilAsync(reader, output, "ready"u8.ToArray());
        }

        await Assert.That(session.InputMode).IsNotNull();
        await session.DisposeAsync();
        await Assert.That(session.InputMode).IsNull();
    }

    [Test]
    public async Task CanonicalEraseRemovesAWholeUtf8Character()
    {
        if (OperatingSystem.IsWindows())
            return;

        // Echo is off, so the only bracketed text in the output is the line the child read. The erase happens in the
        // line discipline as the input is written, so it does not depend on when echo was turned off. FreeBSD erases a
        // whole UTF-8 character only on its echo path (ttydisc_rubchar in tty_ttydisc.c, 14.5 and 15.1), so echo stays
        // on there; the echo holds the character's three bytes and no bracket.
        var echoOff = OperatingSystem.IsFreeBSD() ? "" : "stty -echo; ";
        await using var session = Pty.Start(new PtyStartInfo
        {
            FileName = "sh",
            Arguments = ["-c", echoOff + "printf ready; IFS= read -r line; printf '[%s]' \"$line\""],
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var reader = session.ReadOutputAsync(cts.Token).GetAsyncEnumerator();
        using var output = new MemoryStream();
        await ReadUntilAsync(reader, output, "ready"u8.ToArray());

        // U+3042 (three bytes), then DEL, then the byte that should follow a whole-character erase.
        await session.WriteInputAsync(new byte[] { 0xE3, 0x81, 0x82, 0x7F, (byte)'b', (byte)'\n' });
        while (await reader.MoveNextAsync())
            output.Write(reader.Current.Data.Span);

        var bytes = output.ToArray();
        await Assert.That(Encoding.UTF8.GetString(bytes)).Contains("[b]");
        // The line as the kernel default would have delivered it: the first two bytes of the character survive.
        await Assert.That(bytes.AsSpan().IndexOf(new byte[] { 0xE3, 0x81, (byte)'b' })).IsEqualTo(-1);
        await Assert.That((await session.WaitForExitAsync(cts.Token)).ExitCode).IsEqualTo(0);
    }

    [Test]
    public async Task LinuxReportsTheExplicitInputFlags()
    {
        if (!OperatingSystem.IsLinux())
            return;

        // stty prints each flag as "name" or "-name". iutf8 and imaxbel are both absent from the Linux kernel
        // default, so seeing them set shows the spawn-time termios took effect rather than the default. onlcr is in
        // the default too, so it only checks that the explicit termios keeps output translation.
        await using var session = Pty.Start(new PtyStartInfo { FileName = "sh", Arguments = ["-c", "stty -a"] });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var output = new MemoryStream();
        await foreach (var chunk in session.ReadOutputAsync(cts.Token))
            output.Write(chunk.Data.Span);

        var text = Encoding.UTF8.GetString(output.GetBuffer().AsSpan(0, checked((int)output.Length)));
        await Assert.That(text).Contains(" iutf8");
        await Assert.That(text).DoesNotContain("-iutf8");
        await Assert.That(text).Contains(" imaxbel");
        await Assert.That(text).DoesNotContain("-imaxbel");
        await Assert.That(text).Contains(" onlcr");
    }

    private static async Task ReadUntilAsync(IAsyncEnumerator<PtyOutputChunk> reader, MemoryStream output, ReadOnlyMemory<byte> marker)
    {
        while (output.GetBuffer().AsSpan(0, checked((int)output.Length)).IndexOf(marker.Span) < 0)
        {
            await Assert.That(await reader.MoveNextAsync()).IsTrue();
            output.Write(reader.Current.Data.Span);
        }
    }
}
