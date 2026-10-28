using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// A small independent TUI: a colored flow diagram and block-art landscape, keyboard/mouse selection,
/// and resize. The application owns hit testing; MiniPty carries the terminal's output and input.
/// </summary>
internal static partial class DemoTui
{
    internal static int Run()
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;
        var outputHandle = nint.Zero;
        uint savedMode = 0;
        if (OperatingSystem.IsWindows())
        {
            outputHandle = GetStdHandle(-11);
            if (!GetConsoleMode(outputHandle, out savedMode) || !SetConsoleMode(outputHandle, savedMode | 4))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var savedControlC = Console.TreatControlCAsInput;
        try
        {
            Console.TreatControlCAsInput = true;
            using var input = new DemoInput();
            Console.Write("\u001b[?1049h\u001b[?25l\u001b[?1000h\u001b[?1006h");
            var renderer = new DemoRenderer(Console.Out);
            var selected = 0;
            var enabled = false;
            var width = 0;
            var height = 0;
            var dirty = true;
            var mouseColumn = 0;
            var mouseRow = 0;
            while (true)
            {
                var newWidth = Console.WindowWidth;
                var newHeight = Console.WindowHeight;
                if (dirty || width != newWidth || height != newHeight)
                {
                    width = newWidth;
                    height = newHeight;
                    renderer.Draw(new DemoView(width, height, selected, enabled, mouseColumn, mouseRow));
                    dirty = false;
                }

                if (!input.TryRead(out var key))
                {
                    Thread.Sleep(20); // Poll size even when the user is not typing; no per-frame tasks or timers.
                    continue;
                }

                if (key.Command == DemoCommand.Quit)
                    break;
                switch (key.Command)
                {
                    case DemoCommand.Up:
                        selected = (selected + 2) % 3;
                        break;
                    case DemoCommand.Down:
                        selected = (selected + 1) % 3;
                        break;
                    case DemoCommand.Toggle:
                        enabled = !enabled;
                        break;
                    case DemoCommand.Mouse:
                        mouseColumn = key.Column;
                        mouseRow = key.Row;
                        if (width >= 72 && height >= 22 && key.Column <= width && key.Row <= height)
                        {
                            if (key.Button is 64 or 65)
                                selected = (selected + (key.Button == 64 ? 2 : 1)) % 3;
                            else if (key.Row is >= 6 and <= 9)
                            {
                                for (var i = 0; i < 3; i++)
                                {
                                    if (key.Column >= 1 + i * 24 && key.Column <= 20 + i * 24)
                                        selected = i;
                                }
                            }
                            else if (key.Row == 17 && key.Column is >= 34 and <= 41)
                                enabled = !enabled;
                        }
                        break;
                    default:
                        continue;
                }
                dirty = true;
            }
        }
        finally
        {
            Console.Write("\u001b[?1000l\u001b[?1006l\u001b[0m\u001b[?25h\u001b[?1049l");
            Console.Out.Flush();
            Console.TreatControlCAsInput = savedControlC;
            if (OperatingSystem.IsWindows())
                SetConsoleMode(outputHandle, savedMode);
        }
        Console.Write("TUI exited cleanly.\r\n");
        return 0;
    }

    // Only the child needs to enable Windows VT output; the host uses MiniPty.Console for its own modes.
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GetStdHandle(int standardHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleMode(nint handle, out uint mode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleMode(nint handle, uint mode);
}
