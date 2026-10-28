using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// A small independent TUI that looks like a typical terminal application, on three tabs: a dashboard (spinners, job
/// progress bars, a scrolling log, a modal dialog), a file tree, and charts, with keyboard/mouse navigation and resize.
/// The animation clock advances in the input poll loop (no timers or tasks) and stops while paused or while the dialog
/// is open. The application owns hit testing; MiniPty carries the terminal's output and input.
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
            var state = new State { Files = FileBrowser.Initial };
            var ticksPerFrame = Stopwatch.Frequency * DemoJobs.FrameMilliseconds / 1000;
            var animationTicks = 0L;
            var last = Stopwatch.GetTimestamp();
            while (true)
            {
                var now = Stopwatch.GetTimestamp();
                if (!state.Paused && !state.Dialog)
                    animationTicks += now - last;
                last = now;

                // The renderer emits nothing for an unchanged view, so drawing every poll costs only a comparison.
                var width = Console.WindowWidth;
                var height = Console.WindowHeight;
                renderer.Draw(state.View(width, height, animationTicks / ticksPerFrame));

                if (!input.TryRead(out var key))
                {
                    Thread.Sleep(20); // Poll size and the clock even when the user is not typing; no tasks or timers.
                    continue;
                }

                if (key.Command == DemoCommand.Quit)
                    break;
                state.Apply(key, width, height);
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

    /// <summary>What the user changed: the page, each page's selection, pause, and the dashboard's dialog.</summary>
    private struct State
    {
        internal int Page;
        internal int Selected;
        internal bool Paused;
        internal bool Dialog;
        internal int MouseColumn;
        internal int MouseRow;
        internal FileBrowser Files;

        internal readonly DemoView View(int width, int height, long frame) =>
            new(width, height, Selected, Paused, Dialog, MouseColumn, MouseRow, frame, Page, Files);

        /// <summary>Keys act on the shown page; while the dialog is open only Enter, the Close button, and Q react.</summary>
        internal void Apply(DemoEvent key, int width, int height)
        {
            var files = Page == DemoLayout.FilesPage && !Dialog;
            switch (key.Command)
            {
                case DemoCommand.Page when !Dialog:
                    Page = key.Column;
                    break;
                case DemoCommand.NextTab when !Dialog:
                    Page = (Page + 1) % DemoLayout.PageCount;
                    break;
                case DemoCommand.PrevTab when !Dialog:
                    Page = (Page + DemoLayout.PageCount - 1) % DemoLayout.PageCount;
                    break;
                case DemoCommand.Pause when !Dialog:
                    Paused = !Paused;
                    break;
                case DemoCommand.Details when Page == DemoLayout.DashboardPage:
                    Dialog = !Dialog;
                    break;
                case DemoCommand.Details when files:
                    Files = DemoFiles.Toggle(Files);
                    break;
                case DemoCommand.Up or DemoCommand.Down when !Dialog:
                    var delta = key.Command == DemoCommand.Up ? -1 : 1;
                    if (Page == DemoLayout.DashboardPage)
                        Selected = (Selected + delta + DemoJobs.Count) % DemoJobs.Count;
                    else if (files)
                        Files = DemoFiles.Move(Files, delta);
                    break;
                case DemoCommand.PageUp when files:
                    Files = DemoFiles.Move(Files, -DemoFiles.Rows);
                    break;
                case DemoCommand.PageDown when files:
                    Files = DemoFiles.Move(Files, DemoFiles.Rows);
                    break;
                case DemoCommand.Home when files:
                    Files = DemoFiles.Move(Files, -DemoFiles.Count);
                    break;
                case DemoCommand.End when files:
                    Files = DemoFiles.Move(Files, DemoFiles.Count);
                    break;
                case DemoCommand.Left when files:
                    Files = DemoFiles.Collapse(Files);
                    break;
                case DemoCommand.Right when files:
                    Files = DemoFiles.Expand(Files);
                    break;
                case DemoCommand.Mouse:
                    MouseColumn = key.Column;
                    MouseRow = key.Row;
                    if (width >= DemoLayout.MinWidth && height >= DemoLayout.MinHeight && key.Column <= width && key.Row <= height)
                        Click(key);
                    break;
            }
        }

        /// <summary>Hit testing for a press or wheel report; the dialog is modal and only its Close button reacts.</summary>
        private void Click(DemoEvent key)
        {
            if (Dialog)
            {
                if (key.Button == 0 && key.Row == DemoLayout.CloseRow && key.Column is >= DemoLayout.CloseLeft and <= DemoLayout.CloseRight)
                    Dialog = false;
                return;
            }

            if (key.Button == 0 && key.Row == DemoLayout.TabRow)
            {
                for (var page = 0; page < DemoLayout.PageCount; page++)
                {
                    if (key.Column >= DemoLayout.TabLeft[page] && key.Column <= DemoLayout.TabRight[page])
                        Page = page;
                }
                return;
            }

            var wheel = key.Button is 64 or 65 ? (key.Button == 64 ? -1 : 1) : 0;
            if (Page == DemoLayout.FilesPage)
            {
                if (wheel != 0)
                    Files = DemoFiles.Move(Files, wheel);
                else if (key.Column is >= DemoLayout.FileLeft and <= DemoLayout.FileRight)
                    Files = DemoFiles.Click(Files, key.Row - DemoLayout.FirstFileRow);
                return;
            }
            if (Page != DemoLayout.DashboardPage)
                return;

            if (wheel != 0)
            {
                Selected = (Selected + wheel + DemoJobs.Count) % DemoJobs.Count;
                return;
            }

            var job = key.Row - DemoLayout.FirstJobRow;
            if (job is >= 0 and < DemoJobs.Count && key.Column is >= DemoLayout.JobLeft and <= DemoLayout.JobRight)
                Selected = job;
            else if (key.Row == DemoLayout.ButtonRow && key.Column is >= DemoLayout.PauseLeft and <= DemoLayout.PauseRight)
                Paused = !Paused;
            else if (key.Row == DemoLayout.ButtonRow && key.Column is >= DemoLayout.DetailsLeft and <= DemoLayout.DetailsRight)
                Dialog = true;
        }
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
