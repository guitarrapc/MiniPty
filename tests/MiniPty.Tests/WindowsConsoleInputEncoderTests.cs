using System.Text;
using MiniPty.Console.Internal;

namespace MiniPty.Tests;

public sealed class WindowsConsoleInputEncoderTests
{
    [Test]
    public async Task EncodeKeyEvent_KeyUp_ReturnsZero()
    {
        var record = new ConsoleWindowsInterop.KeyEventRecord
        {
            KeyDown = false,
            UnicodeChar = 'a',
        };

        Span<byte> buffer = stackalloc byte[8];
        var written = WindowsConsoleInputEncoder.EncodeKeyEvent(record, buffer);
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task EncodeKeyEvent_UnicodeChar_EmitsUtf8()
    {
        var record = new ConsoleWindowsInterop.KeyEventRecord
        {
            KeyDown = true,
            UnicodeChar = 'é',
        };

        Span<byte> buffer = stackalloc byte[8];
        var written = WindowsConsoleInputEncoder.EncodeKeyEvent(record, buffer);
        var text = Encoding.UTF8.GetString(buffer[..written]);
        await Assert.That(text).IsEqualTo("é");
    }

    [Test]
    public async Task EncodeKeyEvent_SpecialKey_EmitsAnsiSequence()
    {
        var record = new ConsoleWindowsInterop.KeyEventRecord
        {
            KeyDown = true,
            VirtualKeyCode = ConsoleWindowsInterop.VkUp,
        };

        Span<byte> buffer = stackalloc byte[8];
        var written = WindowsConsoleInputEncoder.EncodeKeyEvent(record, buffer);
        var expected = "\x1b[A"u8.ToArray();
        var actual = buffer[..written].ToArray();
        await Assert.That(written).IsEqualTo(expected.Length);
        for (var i = 0; i < expected.Length; i++)
            await Assert.That(actual[i]).IsEqualTo(expected[i]);
    }

    [Test]
    public async Task EncodeKeyEvent_BufferTooSmall_ReturnsZero()
    {
        var record = new ConsoleWindowsInterop.KeyEventRecord
        {
            KeyDown = true,
            VirtualKeyCode = ConsoleWindowsInterop.VkUp,
        };

        Span<byte> buffer = stackalloc byte[2];
        var written = WindowsConsoleInputEncoder.EncodeKeyEvent(record, buffer);
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task EncodeKeyEvent_UnknownNonUnicodeKey_ReturnsZero()
    {
        var record = new ConsoleWindowsInterop.KeyEventRecord
        {
            KeyDown = true,
            VirtualKeyCode = 0x00,
        };

        Span<byte> buffer = stackalloc byte[8];
        var written = WindowsConsoleInputEncoder.EncodeKeyEvent(record, buffer);
        await Assert.That(written).IsEqualTo(0);
    }
}
