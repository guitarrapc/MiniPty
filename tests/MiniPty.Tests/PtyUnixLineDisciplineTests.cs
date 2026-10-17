using System.Text;

namespace MiniPty.Tests;

/// <summary>
/// The termios a Unix child starts with is set by MiniPty at spawn rather than left to the kernel default,
/// so canonical-mode editing behaves the same on every Unix. The visible difference from the Linux and macOS
/// defaults is <c>IUTF8</c>: without it, erasing a multibyte character removes one byte and leaves invalid
/// UTF-8 on the line the child reads.
/// </summary>
public sealed class PtyUnixLineDisciplineTests
{
    [Test]
    public async Task CanonicalEraseRemovesAWholeUtf8Character()
    {
        if (OperatingSystem.IsWindows())
            return;

        // Echo is turned off so that the only bracketed text in the output is the line the child read; the erase
        // itself happens in the line discipline when the input is written, whether or not echo is on yet.
        await using var session = Pty.Start(new PtyStartInfo
        {
            FileName = "sh",
            Arguments = ["-c", "stty -echo; printf ready; IFS= read -r line; printf '[%s]' \"$line\""],
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
        // default, so seeing them set shows the spawn-time termios took effect rather than the default.
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
