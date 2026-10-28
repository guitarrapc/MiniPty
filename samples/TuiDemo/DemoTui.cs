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
            // 1002 (button-event tracking) reports presses, releases, and motion while a button is held, for dragging.
            Console.Write("\u001b[?1049h\u001b[?25l\u001b[?1002h\u001b[?1006h");
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
                state.Fit(width, height);
                renderer.Draw(state.View(width, height, animationTicks / ticksPerFrame));

                // Apply every queued event before the next draw: a burst of drag motion reports, wheel steps, or key
                // repeats then costs one frame, not one per event, so the screen keeps up instead of replaying them.
                var read = false;
                var quit = false;
                while (!quit && input.TryRead(out var key))
                {
                    read = true;
                    quit = state.Quits(key);
                    if (!quit)
                        state.Apply(key, width, height);
                }
                if (quit)
                    break;
                if (!read)
                    Thread.Sleep(20); // Poll size and the clock even when the user is not typing; no tasks or timers.
            }
        }
        finally
        {
            // Also reset the scroll region in case the TUI stopped between a log scroll and its reset.
            Console.Write("\u001b[r\u001b[?1002l\u001b[?1000l\u001b[?1006l\u001b[0m\u001b[?25h\u001b[?1049l");
            Console.Out.Flush();
            Console.TreatControlCAsInput = savedControlC;
            if (OperatingSystem.IsWindows())
                SetConsoleMode(outputHandle, savedMode);
        }
        Console.Write("TUI exited cleanly.\r\n");
        return 0;
    }

    /// <summary>What the user changed: the page, each page's selection, pause, the dialog, the filter, and the Charts split.</summary>
    private struct State
    {
        internal int Page;
        internal int Selected;
        internal bool Paused;
        internal bool Dialog;
        internal int MouseColumn;
        internal int MouseRow;
        internal FileBrowser Files;
        internal ChartsLayout Charts;
        internal DemoDrag Dragging;

        private readonly bool Typing => Page == DemoLayout.FilesPage && Files.Editing && !Dialog;

        internal readonly DemoView View(int width, int height, long frame) =>
            new(width, height, Selected, Paused, Dialog, MouseColumn, MouseRow, frame, Page, Files, Charts, Dragging);

        /// <summary>Q quits except while the filter box takes text, where it is a letter; Ctrl+C always quits.</summary>
        internal readonly bool Quits(DemoEvent key) => key.Command == DemoCommand.Quit && !(Typing && key.Character != '\0');

        /// <summary>After a resize, keeps the Files cursor on a shown row of the new number of tree rows.</summary>
        internal void Fit(int width, int height)
        {
            if (width >= DemoLayout.MinWidth && height >= DemoLayout.MinHeight)
                Files = DemoFiles.Fit(Files, new DemoGeometry(width, height).FileRows);
        }

        /// <summary>Keys act on the shown page; while the dialog is open only Enter, the Close button, and Q react.</summary>
        internal void Apply(DemoEvent key, int width, int height)
        {
            var g = new DemoGeometry(width, height);
            var rows = g.FileRows;
            var full = width >= DemoLayout.MinWidth && height >= DemoLayout.MinHeight;
            if (key.Command == DemoCommand.Mouse)
            {
                MouseColumn = key.Column;
                MouseRow = key.Row;
                if (full && key.Column <= width && key.Row <= height)
                    Click(key, g);
                return;
            }
            if (key.Command is DemoCommand.Drag or DemoCommand.Release)
            {
                MouseColumn = key.Column;
                MouseRow = key.Row;
                if (key.Command == DemoCommand.Release || !full || Page != DemoLayout.ChartsPage)
                    Dragging = DemoDrag.None;
                else if (Dragging != DemoDrag.None)
                    MoveSplit(key, g);
                return;
            }
            if (Typing)
            {
                Type(key, rows);
                return;
            }

            var files = Page == DemoLayout.FilesPage && !Dialog;
            if (files && key.Character == '/')
            {
                Files = Files with { Editing = true };
                return;
            }
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
                    Files = DemoFiles.Toggle(Files, rows);
                    break;
                case DemoCommand.Up or DemoCommand.Down when !Dialog:
                    var delta = key.Command == DemoCommand.Up ? -1 : 1;
                    if (Page == DemoLayout.DashboardPage)
                        Selected = (Selected + delta + DemoJobs.Count) % DemoJobs.Count;
                    else if (files)
                        Files = DemoFiles.Move(Files, delta, rows);
                    break;
                case DemoCommand.PageUp when files:
                    Files = DemoFiles.Move(Files, -rows, rows);
                    break;
                case DemoCommand.PageDown when files:
                    Files = DemoFiles.Move(Files, rows, rows);
                    break;
                case DemoCommand.Home when files:
                    Files = DemoFiles.Move(Files, -DemoFiles.Count, rows);
                    break;
                case DemoCommand.End when files:
                    Files = DemoFiles.Move(Files, DemoFiles.Count, rows);
                    break;
                case DemoCommand.Left when files:
                    Files = DemoFiles.Collapse(Files, rows);
                    break;
                case DemoCommand.Right when files:
                    Files = DemoFiles.Expand(Files, rows);
                    break;
            }
        }

        /// <summary>
        /// The filter box takes printable characters, including shortcut letters and digits; Backspace deletes one
        /// character (both halves of a surrogate pair), Ctrl+U clears, Up/Down still move, and Enter stops typing.
        /// </summary>
        private void Type(DemoEvent key, int rows)
        {
            var filter = Files.Filter ?? string.Empty;
            if (key.Character != '\0')
            {
                Files = DemoFiles.WithFilter(Files, filter + key.Character, rows);
                return;
            }
            switch (key.Command)
            {
                case DemoCommand.Backspace when filter.Length > 0:
                    var cut = filter.Length >= 2 && char.IsLowSurrogate(filter[^1]) ? 2 : 1;
                    Files = DemoFiles.WithFilter(Files, filter[..^cut], rows);
                    break;
                case DemoCommand.ClearText:
                    Files = DemoFiles.WithFilter(Files, string.Empty, rows);
                    break;
                case DemoCommand.Up or DemoCommand.Down:
                    Files = DemoFiles.Move(Files, key.Command == DemoCommand.Up ? -1 : 1, rows);
                    break;
                case DemoCommand.Details:
                    Files = Files with { Editing = false };
                    break;
            }
        }

        /// <summary>
        /// Moves the dragged Charts border to the pointer: the bottom panels' top border to its row, or the gap between
        /// the bar chart and the Load panel to its column. The geometry clamps the split to the current size.
        /// </summary>
        private void MoveSplit(DemoEvent key, DemoGeometry g)
        {
            var current = g.Charts(Charts);
            var wanted = Dragging == DemoDrag.Rows
                ? current.Layout with { BottomRows = g.PageBottom - key.Row + 1 }
                : current.Layout with { BarWidth = key.Column - 1 };
            Charts = g.Charts(wanted).Layout;
        }

        /// <summary>Hit testing for a press or wheel report; the dialog is modal and only its Close button reacts.</summary>
        private void Click(DemoEvent key, DemoGeometry g)
        {
            if (Dialog)
            {
                if (key.Button == 0 && key.Row == DemoLayout.CloseRow && key.Column >= g.CloseLeft && key.Column <= g.CloseRight)
                    Dialog = false;
                return;
            }

            if (key.Button == 0 && key.Row == DemoLayout.TabRow)
            {
                for (var page = 0; page < DemoLayout.PageCount; page++)
                {
                    if (key.Column >= DemoLayout.TabLeft[page] && key.Column <= DemoLayout.TabRight[page])
                    {
                        Page = page;
                        Files = Files with { Editing = false };
                    }
                }
                return;
            }

            var wheel = key.Button is 64 or 65 ? (key.Button == 64 ? -1 : 1) : 0;
            if (Page == DemoLayout.ChartsPage)
            {
                // A press on a border starts a drag: either horizontal border row, or the vertical border, the gap,
                // or the Load panel's left border on the bottom panels' rows. Motion moves it until the release.
                var c = g.Charts(Charts);
                Charts = c.Layout;
                if (wheel != 0)
                    return;
                if (key.Row == c.PlotBottom || key.Row == c.Top)
                    Dragging = DemoDrag.Rows;
                else if (key.Row > c.Top && key.Row <= c.PageBottom && key.Column >= c.BarWidth && key.Column <= c.LoadLeft)
                    Dragging = DemoDrag.Columns;
                return;
            }
            if (Page == DemoLayout.FilesPage)
            {
                var inTree = key.Column >= DemoLayout.FileLeft && key.Column <= g.FileRight;
                if (wheel != 0)
                    Files = DemoFiles.Move(Files, wheel, g.FileRows);
                else if (inTree && key.Row == DemoLayout.FilterRow)
                    Files = Files with { Editing = true };
                else
                {
                    // A click anywhere else stops typing; a click on a row also selects or folds it.
                    Files = Files with { Editing = false };
                    if (inTree)
                        Files = DemoFiles.Click(Files, key.Row - DemoLayout.FirstFileRow, g.FileRows);
                }
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
            if (job is >= 0 and < DemoJobs.Count && key.Column >= DemoLayout.JobLeft && key.Column <= g.JobRight)
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
