using System.Runtime.InteropServices;

namespace MiniPty.Console.Internal;

internal static partial class ConsoleWindowsInterop
{
    internal const int StdInputHandle = -10;
    internal const int StdOutputHandle = -11;

    internal const uint EnableEchoInput = 0x0004;
    internal const uint EnableLineInput = 0x0002;
    internal const uint EnableProcessedInput = 0x0001;
    internal const uint EnableWindowInput = 0x0008;
    internal const uint EnableVirtualTerminalInput = 0x0200;

    internal const int ErrorOperationAborted = 995;

    internal const uint EnableProcessedOutput = 0x0001;
    internal const uint EnableWrapAtEolOutput = 0x0002;
    internal const uint EnableVirtualTerminalProcessing = 0x0004;

    internal const ushort KeyEvent = 0x0001;
    internal const ushort MouseEvent = 0x0002;
    internal const ushort WindowBufferSizeEvent = 0x0004;

    internal const ushort VkUp = 0x26;
    internal const ushort VkDown = 0x28;
    internal const ushort VkRight = 0x27;
    internal const ushort VkLeft = 0x25;
    internal const ushort VkHome = 0x24;
    internal const ushort VkEnd = 0x23;
    internal const ushort VkInsert = 0x2D;
    internal const ushort VkDelete = 0x2E;
    internal const ushort VkPrior = 0x21;
    internal const ushort VkNext = 0x22;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Coord
    {
        public short X;
        public short Y;
    }

    /// <summary>
    /// <c>KEY_EVENT_RECORD</c>. Records cross P/Invoke as raw <see cref="InputRecord"/> pointers, so the managed
    /// in-memory layout must equal the native one: <c>BOOL bKeyDown</c> is declared as a 4-byte <see cref="int"/>.
    /// A <c>[MarshalAs(Bool)] bool</c> would occupy 1 byte in memory and shift every following field by 2 bytes,
    /// while <c>Marshal.OffsetOf</c> would still report the native offsets.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyEventRecord
    {
        /// <summary>Native <c>BOOL</c>; nonzero for key down.</summary>
        public int KeyDown;

        public ushort RepeatCount;
        public ushort VirtualKeyCode;
        public ushort VirtualScanCode;
        public ushort UnicodeChar;
        public uint ControlKeyState;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowBufferSizeRecord
    {
        public Coord Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseEventRecord
    {
        public Coord MousePosition;
        public uint ButtonState;
        public uint ControlKeyState;
        public uint EventFlags;
    }

    [StructLayout(LayoutKind.Explicit, Size = 20)]
    internal struct InputRecord
    {
        [FieldOffset(0)]
        public ushort EventType;

        [FieldOffset(4)]
        public KeyEventRecord KeyEvent;

        [FieldOffset(4)]
        public WindowBufferSizeRecord WindowBufferSizeEvent;

        [FieldOffset(4)]
        public MouseEventRecord MouseEvent;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SmallRect
    {
        public short Left;
        public short Top;
        public short Right;
        public short Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ConsoleScreenBufferInfo
    {
        public Coord Size;
        public Coord CursorPosition;
        public ushort Attributes;
        public SmallRect Window;
        public Coord MaximumWindowSize;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr GetStdHandle(int nStdHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetConsoleScreenBufferInfo(
        IntPtr hConsoleOutput,
        out ConsoleScreenBufferInfo lpConsoleScreenBufferInfo);

    [LibraryImport("kernel32.dll", EntryPoint = "ReadConsoleInputW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool ReadConsoleInput(
        IntPtr hConsoleInput,
        InputRecord* lpBuffer,
        uint nLength,
        out uint lpNumberOfEventsRead);

    [LibraryImport("kernel32.dll", EntryPoint = "PeekConsoleInputW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool PeekConsoleInput(
        IntPtr hConsoleInput,
        InputRecord* lpBuffer,
        uint nLength,
        out uint lpNumberOfEventsRead);

    [LibraryImport("kernel32.dll", EntryPoint = "WriteConsoleInputW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool WriteConsoleInput(
        IntPtr hConsoleInput,
        InputRecord* lpBuffer,
        uint nLength,
        out uint lpNumberOfEventsWritten);

    [LibraryImport("kernel32.dll")]
    internal static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AllocConsole();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FreeConsole();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool ReadFile(
        IntPtr hFile,
        byte* lpBuffer,
        uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead,
        IntPtr lpOverlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool WriteFile(
        IntPtr hFile,
        byte* lpBuffer,
        uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten,
        IntPtr lpOverlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CancelIoEx(IntPtr hFile, IntPtr lpOverlapped);
}
