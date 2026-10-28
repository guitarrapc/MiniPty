using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// A small independent TUI that looks like a typical terminal application: spinners, job progress bars, a
/// scrolling log, and a modal dialog, with keyboard/mouse selection and resize. The animation clock advances in the
/// input poll loop (no timers or tasks) and stops while paused or while the dialog is open. The application owns
/// hit testing; MiniPty carries the terminal's output and input.
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
            var paused = false;
            var dialog = false;
            var mouseColumn = 0;
            var mouseRow = 0;
            var ticksPerFrame = Stopwatch.Frequency * DemoJobs.FrameMilliseconds / 1000;
            var animationTicks = 0L;
            var last = Stopwatch.GetTimestamp();
            while (true)
            {
                var now = Stopwatch.GetTimestamp();
                if (!paused && !dialog)
                    animationTicks += now - last;
                last = now;

                // The renderer emits nothing for an unchanged view, so drawing every poll costs only a comparison.
                var width = Console.WindowWidth;
                var height = Console.WindowHeight;
                renderer.Draw(new DemoView(width, height, selected, paused, dialog, mouseColumn, mouseRow, animationTicks / ticksPerFrame));

                if (!input.TryRead(out var key))
                {
                    Thread.Sleep(20); // Poll size and the clock even when the user is not typing; no tasks or timers.
                    continue;
                }

                if (key.Command == DemoCommand.Quit)
                    break;
                switch (key.Command)
                {
                    case DemoCommand.Up when !dialog:
                        selected = (selected + DemoJobs.Count - 1) % DemoJobs.Count;
                        break;
                    case DemoCommand.Down when !dialog:
                        selected = (selected + 1) % DemoJobs.Count;
                        break;
                    case DemoCommand.Pause when !dialog:
                        paused = !paused;
                        break;
                    case DemoCommand.Details:
                        dialog = !dialog;
                        break;
                    case DemoCommand.Mouse:
                        mouseColumn = key.Column;
                        mouseRow = key.Row;
                        if (width >= DemoLayout.MinWidth && height >= DemoLayout.MinHeight && key.Column <= width && key.Row <= height)
                            Click(key, ref selected, ref paused, ref dialog);
                        break;
                }
            }
        }
        finally
        {
            // Also reset the scroll region in case the TUI stopped between a log scroll and its reset.
            Console.Write("\u001b[r\u001b[?1000l\u001b[?1006l\u001b[0m\u001b[?25h\u001b[?1049l");
            Console.Out.Flush();
            Console.TreatControlCAsInput = savedControlC;
            if (OperatingSystem.IsWindows())
                SetConsoleMode(outputHandle, savedMode);
        }
        Console.Write("TUI exited cleanly.\r\n");
        return 0;
    }

    /// <summary>Hit testing for a press or wheel report; the dialog is modal and only its Close button reacts.</summary>
    private static void Click(DemoEvent key, ref int selected, ref bool paused, ref bool dialog)
    {
        if (dialog)
        {
            if (key.Button == 0 && key.Row == DemoLayout.CloseRow && key.Column is >= DemoLayout.CloseLeft and <= DemoLayout.CloseRight)
                dialog = false;
            return;
        }

        if (key.Button is 64 or 65)
        {
            selected = (selected + (key.Button == 64 ? DemoJobs.Count - 1 : 1)) % DemoJobs.Count;
            return;
        }

        var job = key.Row - DemoLayout.FirstJobRow;
        if (job is >= 0 and < DemoJobs.Count && key.Column is >= DemoLayout.JobLeft and <= DemoLayout.JobRight)
            selected = job;
        else if (key.Row == DemoLayout.ButtonRow && key.Column is >= DemoLayout.PauseLeft and <= DemoLayout.PauseRight)
            paused = !paused;
        else if (key.Row == DemoLayout.ButtonRow && key.Column is >= DemoLayout.DetailsLeft and <= DemoLayout.DetailsRight)
            dialog = true;
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
