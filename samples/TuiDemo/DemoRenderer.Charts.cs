/// <summary>
/// The Charts page: a braille plot that takes the width and the spare height, then a bar chart (columns 1–35) and
/// sparklines (columns 37 to the right edge) in the last seven page rows. The plot changes nearly every cell each
/// frame, so the displayed canvas is kept and each tick writes only the span of each row that differs from it. Bars and
/// sparklines are small and are rewritten on every tick. The canvas arrays are reallocated only when the size changes.
/// </summary>
internal sealed partial class DemoRenderer
{
    private const int PlotLeft = 8;
    private const string Lower = "▁▂▃▄▅▆▇█";

    private static readonly string[] BarColors = ["\u001b[36m", "\u001b[32m", "\u001b[33m", "\u001b[35m", "\u001b[31m"];
    private static readonly string[] SparkColors = ["\u001b[32m", "\u001b[36m", "\u001b[35m"];

    private byte[] _plotBits = [];
    private byte[] _plotColors = [];
    private byte[] _nextBits = [];
    private byte[] _nextColors = [];

    private void DrawCharts(DemoView view, DemoGeometry g)
    {
        var columns = g.PlotColumns;
        var rows = g.PlotRows;
        if (_plotBits.Length != columns * rows)
        {
            _plotBits = new byte[columns * rows];
            _plotColors = new byte[columns * rows];
            _nextBits = new byte[columns * rows];
            _nextColors = new byte[columns * rows];
        }

        MoveTo(3, 1).Append("\u001b[0m┌ Signals \u001b[36msine\u001b[0m \u001b[33mmix\u001b[0m ").Append('─', g.Width - 20).Append('┐');
        for (var row = 0; row < rows; row++)
        {
            var label = row == 0 ? "  1.0" : row == rows / 2 ? "  0.0" : row == rows - 1 ? " -1.0" : "     ";
            MoveTo(4 + row, 1).Append("\u001b[0m│\u001b[90m").Append(label).Append("│\u001b[0m");
            MoveTo(4 + row, PlotLeft + columns).Append("    │");
        }
        MoveTo(4 + rows, 1).Append('└').Append('─', 5).Append('┴').Append('─', g.Width - 8).Append('┘');

        DemoCharts.Plot(view.Frame, columns, rows, _plotBits, _plotColors);
        for (var row = 0; row < rows; row++)
            DrawPlotSpan(row, 0, columns - 1, _plotBits, _plotColors, columns);

        var top = g.ChartsTop;
        MoveTo(top, 1).Append("┌ Requests/s ").Append('─', 21).Append("┐ ┌ Load ").Append('─', g.Width - 44).Append('┐');
        DrawBars(view.Frame, g);
        MoveTo(top + 5, 1).Append("\u001b[0m│ ");
        for (var bar = 0; bar < DemoCharts.BarCount; bar++)
        {
            var label = DemoCharts.BarLabel(bar);
            _frame.Append(' ').Append(label).Append(' ', 5 - label.Length);
        }
        _frame.Append("  │ ");
        foreach (var row in (ReadOnlySpan<int>)[top + 2, top + 4])
            MoveTo(row, 37).Append('│').Append(' ', g.Width - 38).Append('│');
        DrawSparks(view.Frame, g);
        MoveTo(top + 6, 1).Append('└').Append('─', 33).Append("┘ └").Append('─', g.Width - 38).Append('┘');
    }

    private void DrawChartsChanges(DemoView view, DemoGeometry g)
    {
        if (view.Frame == _previous.Frame)
            return;

        var columns = g.PlotColumns;
        DemoCharts.Plot(view.Frame, columns, g.PlotRows, _nextBits, _nextColors);
        for (var row = 0; row < g.PlotRows; row++)
        {
            var start = row * columns;
            var first = -1;
            var last = -1;
            for (var column = 0; column < columns; column++)
            {
                if (_nextBits[start + column] == _plotBits[start + column] && _nextColors[start + column] == _plotColors[start + column])
                    continue;
                if (first < 0)
                    first = column;
                last = column;
            }
            if (first >= 0)
                DrawPlotSpan(row, first, last, _nextBits, _nextColors, columns);
        }
        (_plotBits, _nextBits) = (_nextBits, _plotBits);
        (_plotColors, _nextColors) = (_nextColors, _plotColors);

        DrawBars(view.Frame, g);
        DrawSparks(view.Frame, g);
    }

    /// <summary>Writes plot cells <paramref name="first"/>–<paramref name="last"/> of a row, changing color only when it differs.</summary>
    private void DrawPlotSpan(int row, int first, int last, byte[] bits, byte[] colors, int columns)
    {
        MoveTo(4 + row, PlotLeft + first).Append("\u001b[0m");
        var color = (byte)0;
        for (var column = first; column <= last; column++)
        {
            var cell = row * columns + column;
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

    /// <summary>Four rows from the bottom panels' top: five 3-cell bars in 6-cell slots, each row its eighth of the height.</summary>
    private void DrawBars(long frame, DemoGeometry g)
    {
        for (var row = 0; row < 4; row++)
        {
            MoveTo(g.ChartsTop + 1 + row, 1).Append("\u001b[0m│ ");
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

    /// <summary>Rows 1, 3, and 5 of the Load panel (columns 37 to the right edge): a label, the recent values, the percent.</summary>
    private void DrawSparks(long frame, DemoGeometry g)
    {
        var length = g.SparkLength;
        for (var spark = 0; spark < DemoCharts.SparkCount; spark++)
        {
            MoveTo(g.ChartsTop + 1 + spark * 2, 37).Append("\u001b[0m│ ").Append(DemoCharts.SparkLabel(spark)).Append(' ').Append(SparkColors[spark]);
            for (var i = 0; i < length; i++)
                _frame.Append(Lower[DemoCharts.SparkLevel(spark, frame - length + 1 + i)]);
            var percent = DemoCharts.LoadPercent(spark, frame);
            _frame.Append("\u001b[0m ").Append(' ', 3 - Digits(percent)).Append(percent).Append("% │");
        }
    }
}
