using System.Text;

internal readonly record struct DemoView(int Width, int Height, int Selected, bool Enabled, int MouseColumn, int MouseRow)
{
    internal bool Compact => Width < 72 || Height < 22;
}

/// <summary>
/// Keeps the last displayed state and overwrites only controls that changed. The diagram has fixed cell
/// positions, so normal resizing within its minimum size only changes the size label. A screen clear is
/// needed on first draw or a layout transition, and on compact resize where the message may have wrapped.
/// The reusable buffer batches each update before writing, so the terminal does not see a series of tiny
/// writes while the application is still composing it. This belongs to the TUI, not to PTY transport.
/// </summary>
internal sealed class DemoRenderer(TextWriter output)
{
    private readonly StringBuilder _frame = new(4096);
    private DemoView _previous;
    private bool _drawn;

    internal void Draw(DemoView view)
    {
        if (_drawn && view == _previous)
            return;

        var resized = !_drawn || view.Width != _previous.Width || view.Height != _previous.Height;
        var repaint = !_drawn || view.Compact != _previous.Compact || (view.Compact && resized);
        _frame.Clear();
        if (repaint)
            _frame.Append("\u001b[0m\u001b[2J\u001b[H");

        if (view.Compact)
        {
            if (repaint)
                _frame.Append("Resize to at least 72 x 22 (Q quits).");
            if (view.Width >= 20 && view.Height >= 2
                && (repaint || view.MouseColumn != _previous.MouseColumn || view.MouseRow != _previous.MouseRow))
                DrawMouse(2, view);
        }
        else
        {
            if (repaint)
                DrawStatic();
            if (repaint || resized)
                _frame.Append("\u001b[3;1HSize: ").Append(view.Width).Append(" x ").Append(view.Height).Append("\u001b[K");
            for (var i = 0; i < 3; i++)
            {
                if (repaint || (view.Selected != _previous.Selected && (i == view.Selected || i == _previous.Selected)))
                    DrawBox(i, i == view.Selected);
            }
            if (repaint || view.Selected != _previous.Selected || view.Enabled != _previous.Enabled)
            {
                // Fixed-width ON/OFF clears the old value without erasing the adjacent static button.
                _frame.Append("\u001b[17;1HSelected: ").Append(view.Selected + 1).Append("/3   Toggle: ")
                    .Append(view.Enabled ? "ON " : "OFF");
            }
            if (repaint || view.MouseColumn != _previous.MouseColumn || view.MouseRow != _previous.MouseRow)
                DrawMouse(18, view);
        }

        if (_frame.Length > 0)
        {
            foreach (var chunk in _frame.GetChunks())
                output.Write(chunk.Span);
            output.Flush();
        }
        _previous = view;
        _drawn = true;
    }

    private void DrawMouse(int row, DemoView view) =>
        _frame.Append("\u001b[").Append(row).Append(";1HMouse: ").Append(view.MouseColumn).Append(',').Append(view.MouseRow).Append("\u001b[K");

    private void DrawStatic()
    {
        _frame.Append("\u001b[1;36mMiniPty TUI demo\u001b[0m\u001b[4;1HUnicode: 日本語 / café");
        _frame.Append("\u001b[7;22H\u001b[32m→\u001b[7;46H→\u001b[0m");
        _frame.Append("\u001b[11;1HColored bars                 Block-art landscape");
        _frame.Append("\u001b[13;1H\u001b[36mTransport ████████████\u001b[0m");
        _frame.Append("\u001b[14;1H\u001b[33mInput     ████████\u001b[0m");
        _frame.Append("\u001b[15;1H\u001b[35mResize    ██████████\u001b[0m");
        DrawPixels(12, "ssssssssssssyyss");
        DrawPixels(13, "ssssssgsssssyyss");
        DrawPixels(14, "ssgggggggsssssss");
        DrawPixels(15, "gggggggggggggggg");
        _frame.Append("\u001b[17;34H\u001b[30;42m[Toggle]\u001b[0m");
        _frame.Append("\u001b[20;1HClick a box | Wheel/Up/Down: select | Enter: toggle | Q: quit");
        _frame.Append("\u001b[21;1HTerminal text selection: Shift+drag (depends on terminal)");
    }

    private void DrawBox(int index, bool selected)
    {
        var column = 1 + index * 24;
        _frame.Append(selected ? "\u001b[1;30;46m" : index switch
        {
            0 => "\u001b[0;36m", 1 => "\u001b[0;33m", _ => "\u001b[0;35m",
        });
        _frame.Append("\u001b[6;").Append(column).Append("H┌──────────────────┐\u001b[7;").Append(column).Append("H│ ");
        _frame.Append(index switch { 0 => "PTY transport    ", 1 => "Keyboard input   ", _ => "Terminal resize  " });
        _frame.Append("│\u001b[8;").Append(column).Append("H│ ");
        _frame.Append(selected ? "SELECTED         " : "Click to select  ");
        _frame.Append("│\u001b[9;").Append(column).Append("H└──────────────────┘\u001b[0m");
    }

    /// <summary>Two background-colored cells per pixel, using only standard ANSI colors.</summary>
    private void DrawPixels(int row, ReadOnlySpan<char> pixels)
    {
        _frame.Append("\u001b[").Append(row).Append(";37H");
        foreach (var pixel in pixels)
            _frame.Append(pixel switch { 'y' => "\u001b[43m  ", 'g' => "\u001b[42m  ", _ => "\u001b[44m  " });
        _frame.Append("\u001b[0m");
    }
}
