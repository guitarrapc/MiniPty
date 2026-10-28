using System.Text;
using MiniPty.Console.Internal;

namespace MiniPty.Tests;

public sealed class WindowsConsoleInputEncoderTests
{
    [Test]
    public async Task InputRecord_InMemoryLayoutMatchesNativeWindowsLayout()
    {
        // INPUT_RECORD crosses ReadConsoleInput/WriteConsoleInput as a raw pointer, so the managed in-memory
        // offsets are what the console sees. Marshal.OffsetOf reports the marshaled layout instead and passed
        // while a 1-byte bool KeyDown shifted UnicodeChar onto the native scan code.
        var layout = MeasureLayout();
        await Assert.That(layout.RecordSize).IsEqualTo(20);
        await Assert.That(layout.KeyEventSize).IsEqualTo(16);
        await Assert.That(layout.KeyDown).IsEqualTo(4);
        await Assert.That(layout.RepeatCount).IsEqualTo(8);
        await Assert.That(layout.VirtualKeyCode).IsEqualTo(10);
        await Assert.That(layout.VirtualScanCode).IsEqualTo(12);
        await Assert.That(layout.UnicodeChar).IsEqualTo(14);
        await Assert.That(layout.KeyControlKeyState).IsEqualTo(16);
        await Assert.That(layout.MousePosition).IsEqualTo(4);
        await Assert.That(layout.MouseButtonState).IsEqualTo(8);
        await Assert.That(layout.MouseEventFlags).IsEqualTo(16);

        static unsafe Layout MeasureLayout()
        {
            ConsoleWindowsInterop.InputRecord record;
            var start = (byte*)&record;
            return new Layout(
                sizeof(ConsoleWindowsInterop.InputRecord),
                sizeof(ConsoleWindowsInterop.KeyEventRecord),
                (int)((byte*)&record.KeyEvent.KeyDown - start),
                (int)((byte*)&record.KeyEvent.RepeatCount - start),
                (int)((byte*)&record.KeyEvent.VirtualKeyCode - start),
                (int)((byte*)&record.KeyEvent.VirtualScanCode - start),
                (int)((byte*)&record.KeyEvent.UnicodeChar - start),
                (int)((byte*)&record.KeyEvent.ControlKeyState - start),
                (int)((byte*)&record.MouseEvent.MousePosition - start),
                (int)((byte*)&record.MouseEvent.ButtonState - start),
                (int)((byte*)&record.MouseEvent.EventFlags - start));
        }
    }

    private readonly record struct Layout(
        int RecordSize,
        int KeyEventSize,
        int KeyDown,
        int RepeatCount,
        int VirtualKeyCode,
        int VirtualScanCode,
        int UnicodeChar,
        int KeyControlKeyState,
        int MousePosition,
        int MouseButtonState,
        int MouseEventFlags);

    [Test]
    public async Task EncodeKeyEvent_KeyUp_ReturnsZero()
    {
        var record = new ConsoleWindowsInterop.KeyEventRecord
        {
            KeyDown = 0,
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
            KeyDown = 1,
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
            KeyDown = 1,
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
            KeyDown = 1,
            VirtualKeyCode = ConsoleWindowsInterop.VkUp,
        };

        Span<byte> buffer = stackalloc byte[2];
        var written = WindowsConsoleInputEncoder.EncodeKeyEvent(record, buffer);
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task EncodeKeyEvent_UnicodeChar_BufferTooSmall_ReturnsZero()
    {
        var record = new ConsoleWindowsInterop.KeyEventRecord
        {
            KeyDown = 1,
            UnicodeChar = 'é',
        };

        Span<byte> buffer = stackalloc byte[1];
        var written = WindowsConsoleInputEncoder.EncodeKeyEvent(record, buffer);
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    public async Task EncodeKeyEvent_UnknownNonUnicodeKey_ReturnsZero()
    {
        var record = new ConsoleWindowsInterop.KeyEventRecord
        {
            KeyDown = 1,
            VirtualKeyCode = 0x00,
        };

        Span<byte> buffer = stackalloc byte[8];
        var written = WindowsConsoleInputEncoder.EncodeKeyEvent(record, buffer);
        await Assert.That(written).IsEqualTo(0);
    }
}
