/// <summary>
/// The Charts page: a braille plot (rows 4–11), a bar chart (13–19, columns 1–35), and sparklines (columns 37–72).
/// The plot changes nearly every cell each frame, so the displayed canvas is kept and each tick writes only the span
/// of each row that differs from it. Bars and sparklines are small and are rewritten on every tick.
/// </summary>
internal sealed partial class DemoRenderer
{
    private const int PlotLeft = 8;
    private const string Lower = "▁▂▃▄▅▆▇█";

    private static readonly string[] BarColors = ["\u001b[36m", "\u001b[32m", "\u001b[33m", "\u001b[35m", "\u001b[31m"];
    private static readonly string[] SparkColors = ["\u001b[32m", "\u001b[36m", "\u001b[35m"];

    private byte[] _plotBits = new byte[DemoCharts.PlotCells];
    private byte[] _plotColors = new byte[DemoCharts.PlotCells];
    private byte[] _nextBits = new byte[DemoCharts.PlotCells];
    private byte[] _nextColors = new byte[DemoCharts.PlotCells];

    private void DrawCharts(DemoView view)
    {
        MoveTo(3, 1).Append("\u001b[0m┌ Signals \u001b[36msine\u001b[0m \u001b[33mmix\u001b[0m ").Append('─', 52).Append('┐');
        for (var row = 0; row < DemoCharts.PlotRows; row++)
        {
            var label = row switch { 0 => "  1.0", 4 => "  0.0", DemoCharts.PlotRows - 1 => " -1.0", _ => "     " };
            MoveTo(4 + row, 1).Append("\u001b[0m│\u001b[90m").Append(label).Append("│\u001b[0m");
            MoveTo(4 + row, PlotLeft + DemoCharts.PlotColumns).Append("    │");
        }
        MoveTo(12, 1).Append('└').Append('─', 5).Append('┴').Append('─', 64).Append('┘');

        DemoCharts.Plot(view.Frame, _plotBits, _plotColors);
        for (var row = 0; row < DemoCharts.PlotRows; row++)
            DrawPlotSpan(row, 0, DemoCharts.PlotColumns - 1, _plotBits, _plotColors);

        MoveTo(13, 1).Append("┌ Requests/s ").Append('─', 21).Append("┐ ┌ Load ").Append('─', 28).Append('┐');
        DrawBars(view.Frame);
        MoveTo(18, 1).Append("\u001b[0m│ ");
        for (var bar = 0; bar < DemoCharts.BarCount; bar++)
        {
            var label = DemoCharts.BarLabel(bar);
            _frame.Append(' ').Append(label).Append(' ', 5 - label.Length);
        }
        _frame.Append("  │ ");
        foreach (var row in (ReadOnlySpan<int>)[15, 17])
            MoveTo(row, 37).Append('│').Append(' ', 34).Append('│');
        DrawSparks(view.Frame);
        MoveTo(19, 1).Append('└').Append('─', 33).Append("┘ └").Append('─', 34).Append('┘');
    }

    private void DrawChartsChanges(DemoView view)
    {
        if (view.Frame == _previous.Frame)
            return;

        DemoCharts.Plot(view.Frame, _nextBits, _nextColors);
        for (var row = 0; row < DemoCharts.PlotRows; row++)
        {
            var start = row * DemoCharts.PlotColumns;
            var first = -1;
            var last = -1;
            for (var column = 0; column < DemoCharts.PlotColumns; column++)
            {
                if (_nextBits[start + column] == _plotBits[start + column] && _nextColors[start + column] == _plotColors[start + column])
                    continue;
                if (first < 0)
                    first = column;
                last = column;
            }
            if (first >= 0)
                DrawPlotSpan(row, first, last, _nextBits, _nextColors);
        }
        (_plotBits, _nextBits) = (_nextBits, _plotBits);
        (_plotColors, _nextColors) = (_nextColors, _plotColors);

        DrawBars(view.Frame);
        DrawSparks(view.Frame);
    }

    /// <summary>Writes plot cells <paramref name="first"/>–<paramref name="last"/> of a row, changing color only when it differs.</summary>
    private void DrawPlotSpan(int row, int first, int last, byte[] bits, byte[] colors)
    {
        MoveTo(4 + row, PlotLeft + first).Append("\u001b[0m");
        var color = (byte)0;
        for (var column = first; column <= last; column++)
        {
            var cell = row * DemoCharts.PlotColumns + column;
            if (bits[cell] == 0)
            {
                _frame.Append(' ');
                continue;
            }
            if (colors[cell] != color)
            {
                color = colors[cell];
                _frame.Append(color switch
                {
                    DemoCharts.Sine => "\u001b[36m",
                    DemoCharts.Mix => "\u001b[33m",
                    _ => "\u001b[90m",
                });
            }
            _frame.Append((char)(0x2800 + bits[cell]));
        }
        _frame.Append("\u001b[0m");
    }

    /// <summary>Rows 14–17: five 3-cell bars in 6-cell slots, each row showing its eighth of the bar height.</summary>
    private void DrawBars(long frame)
    {
        for (var row = 0; row < 4; row++)
        {
            MoveTo(14 + row, 1).Append("\u001b[0m│ ");
            for (var bar = 0; bar < DemoCharts.BarCount; bar++)
            {
                var eighths = Math.Clamp(DemoCharts.BarEighths(bar, frame) - (3 - row) * 8, 0, 8);
                _frame.Append(' ').Append(BarColors[bar]);
                if (eighths == 0)
                    _frame.Append(' ', 3);
                else
                    _frame.Append(Lower[eighths - 1], 3);
                _frame.Append("\u001b[0m  ");
            }
            _frame.Append("  │ ");
        }
    }

    /// <summary>Rows 14, 16, 18 (columns 37–72): a label, the last 22 values, and the current percent.</summary>
    private void DrawSparks(long frame)
    {
        for (var spark = 0; spark < DemoCharts.SparkCount; spark++)
        {
            MoveTo(14 + spark * 2, 37).Append("\u001b[0m│ ").Append(DemoCharts.SparkLabel(spark)).Append(' ').Append(SparkColors[spark]);
            for (var i = 0; i < DemoCharts.SparkLength; i++)
                _frame.Append(Lower[DemoCharts.SparkLevel(spark, frame - DemoCharts.SparkLength + 1 + i)]);
            var percent = DemoCharts.LoadPercent(spark, frame);
            _frame.Append("\u001b[0m ").Append(' ', 3 - Digits(percent)).Append(percent).Append("% │");
        }
    }
}
