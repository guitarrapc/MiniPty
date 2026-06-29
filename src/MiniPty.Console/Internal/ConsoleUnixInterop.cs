using System.Runtime.InteropServices;

namespace MiniPty.Console.Internal;

internal static partial class ConsoleUnixInterop
{
    internal const int StdinFileno = 0;
    internal const int StdoutFileno = 1;
    internal const int EINTR = 4;

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct TermiosBlob
    {
        public const int BlobSize = 128;

        public fixed byte Bytes[BlobSize];
        public int Length;
    }

    [LibraryImport("minipty_unix", SetLastError = true)]
    internal static partial int minipty_console_isatty(int fd);

    [LibraryImport("minipty_unix", SetLastError = true)]
    internal static unsafe partial int minipty_console_termios_save(int fd, TermiosBlob* outBlob);

    [LibraryImport("minipty_unix", SetLastError = true)]
    internal static unsafe partial int minipty_console_termios_restore(int fd, TermiosBlob* snap);

    [LibraryImport("minipty_unix", SetLastError = true)]
    internal static partial int minipty_console_termios_set_raw_input(int fd);

    [LibraryImport("minipty_unix", SetLastError = true)]
    internal static partial int minipty_console_termios_set_raw_output(int fd);

    [LibraryImport("minipty_unix", SetLastError = true)]
    internal static partial int minipty_console_get_winsize(int fd, out ushort rows, out ushort cols);

    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    internal static unsafe partial int Read(int fd, byte* buf, nuint count);
}
