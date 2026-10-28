/// <summary>
/// The Charts page: a braille plot over the width and the spare height, then a bar chart and sparklines in the bottom
/// panels. The split between them follows <see cref="ChartsLayout"/>, which the user changes by dragging a border;
/// a changed split redraws the page in place. The plot changes nearly every cell each frame, so the displayed canvas is
/// kept and each tick writes only the span of each row that differs from it. Bars and sparklines are small and are
/// rewritten on every tick. Plot buffers are sized for the tallest plot at the current size, so dragging does not
/// allocate; they are reallocated only when the size changes.
/// </summary>
internal sealed partial class DemoRenderer
{
    private const int PlotLeft = 8;
    private const string Lower = "▁▂▃▄▅▆▇█";
    private const string Highlight = "\u001b[0;1;33m";
    private const string Plain = "\u001b[0m";

    private static readonly string[] BarColors = ["\u001b[36m", "\u001b[32m", "\u001b[33m", "\u001b[35m", "\u001b[31m"];
    private static readonly string[] SparkColors = ["\u001b[32m", "\u001b[36m", "\u001b[35m"];

    private byte[] _plotBits = [];
    private byte[] _plotColors = [];
    private byte[] _nextBits = [];
    private byte[] _nextColors = [];

    private void DrawCharts(DemoView view, DemoGeometry g)
    {
        var c = g.Charts(view.Charts);
        var capacity = c.PlotColumns * c.MaxPlotRows;
        if (_plotBits.Length != capacity)
        {
            _plotBits = new byte[capacity];
            _plotColors = new byte[capacity];
            _nextBits = new byte[capacity];
            _nextColors = new byte[capacity];
        }
        var rowsBorder = view.Dragging == DemoDrag.Rows ? Highlight : Plain;
        var columnsBorder = view.Dragging == DemoDrag.Columns ? Highlight : Plain;

        MoveTo(3, 1).Append("\u001b[0m┌ Signals \u001b[36msine\u001b[0m \u001b[33mmix\u001b[0m ").Append('─', g.Width - 20).Append('┐');
        for (var row = 0; row < c.PlotRows; row++)
        {
            var label = row == 0 ? "  1.0" : row == c.PlotRows / 2 ? "  0.0" : row == c.PlotRows - 1 ? " -1.0" : "     ";
            MoveTo(4 + row, 1).Append("\u001b[0m│\u001b[90m").Append(label).Append("│\u001b[0m");
            MoveTo(4 + row, PlotLeft + c.PlotColumns).Append("    │");
        }
        MoveTo(c.PlotBottom, 1).Append(rowsBorder).Append('└').Append('─', 5).Append('┴').Append('─', g.Width - 8).Append('┘');

        var cells = c.PlotColumns * c.PlotRows;
        DemoCharts.Plot(view.Frame, c.PlotColumns, c.PlotRows, _plotBits.AsSpan(0, cells), _plotColors.AsSpan(0, cells));
        for (var row = 0; row < c.PlotRows; row++)
            DrawPlotSpan(row, 0, c.PlotColumns - 1, _plotBits, _plotColors, c.PlotColumns);

        // The bottom panels' top border: the bar chart, the gap, the Load panel.
        MoveTo(c.Top, 1).Append(rowsBorder).Append("┌ Requests/s ").Append('─', c.BarWidth - 14)
            .Append(columnsBorder == Highlight ? Highlight : rowsBorder).Append('┐').Append(Plain).Append(' ')
            .Append(columnsBorder == Highlight ? Highlight : rowsBorder).Append('┌').Append(rowsBorder).Append(" Load ")
            .Append('─', g.Width - c.LoadLeft - 7).Append('┐').Append(Plain);
        DrawBars(view.Frame, c, columnsBorder);
        MoveTo(c.PageBottom - 1, 1).Append("\u001b[0m│ ");
        for (var bar = 0; bar < DemoCharts.BarCount; bar++)
        {
            _frame.Append(' ');
            AppendFitted(DemoCharts.BarLabel(bar), c.BarSlot - 1);
        }
        _frame.Append(' ', c.BarWidth - 3 - 5 * c.BarSlot).Append(columnsBorder).Append('│').Append(Plain).Append(' ');
        for (var row = c.Top + 1; row < c.PageBottom; row++)
        {
            if ((row - c.Top) % 2 == 0 || row - c.Top > 5)
                MoveTo(row, c.LoadLeft).Append(columnsBorder).Append('│').Append(Plain).Append(' ', g.Width - c.LoadLeft - 1).Append('│');
        }
        DrawSparks(view.Frame, c, columnsBorder);
        MoveTo(c.PageBottom, 1).Append('└').Append('─', c.BarWidth - 2).Append(columnsBorder).Append('┘').Append(Plain).Append(' ')
            .Append(columnsBorder).Append('└').Append(Plain).Append('─', g.Width - c.LoadLeft - 1).Append('┘');
    }

    private void DrawChartsChanges(DemoView view, DemoGeometry g)
    {
        if (view.Frame == _previous.Frame)
            return;

        var c = g.Charts(view.Charts);
        var columns = c.PlotColumns;
        var cells = columns * c.PlotRows;
        DemoCharts.Plot(view.Frame, columns, c.PlotRows, _nextBits.AsSpan(0, cells), _nextColors.AsSpan(0, cells));
        for (var row = 0; row < c.PlotRows; row++)
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

        var border = view.Dragging == DemoDrag.Columns ? Highlight : Plain;
        DrawBars(view.Frame, c, border);
        DrawSparks(view.Frame, c, border);
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

    /// <summary>
    /// The bar rows under the bar chart's top border: five bars in equal slots, each row showing its eighth of the bar
    /// height, so a taller or wider chart shows finer or wider bars. Ends with the right border and the gap column.
    /// </summary>
    private void DrawBars(long frame, in ChartsGeometry c, string border)
    {
        var slot = c.BarSlot;
        var barCells = slot - 3;
        for (var row = 0; row < c.BarRows; row++)
        {
            MoveTo(c.Top + 1 + row, 1).Append("\u001b[0m│ ");
            for (var bar = 0; bar < DemoCharts.BarCount; bar++)
            {
                var eighths = Math.Clamp(DemoCharts.BarEighths(bar, frame, c.BarRows) - (c.BarRows - 1 - row) * 8, 0, 8);
                _frame.Append(' ').Append(BarColors[bar]);
                if (eighths == 0)
                    _frame.Append(' ', barCells);
                else
                    _frame.Append(Lower[eighths - 1], barCells);
                _frame.Append("\u001b[0m").Append(' ', slot - 1 - barCells);
            }
            _frame.Append(' ', c.BarWidth - 3 - 5 * slot).Append(border).Append('│').Append(Plain).Append(' ');
        }
    }

    /// <summary>Rows 1, 3, and 5 of the Load panel: its left border, a label, the recent values, the percent.</summary>
    private void DrawSparks(long frame, in ChartsGeometry c, string border)
    {
        var length = c.SparkLength;
        for (var spark = 0; spark < DemoCharts.SparkCount; spark++)
        {
            MoveTo(c.Top + 1 + spark * 2, c.LoadLeft).Append(border).Append('│').Append(Plain).Append(' ')
                .Append(DemoCharts.SparkLabel(spark)).Append(' ').Append(SparkColors[spark]);
            for (var i = 0; i < length; i++)
                _frame.Append(Lower[DemoCharts.SparkLevel(spark, frame - length + 1 + i)]);
            var percent = DemoCharts.LoadPercent(spark, frame);
            _frame.Append("\u001b[0m ").Append(' ', 3 - Digits(percent)).Append(percent).Append("% │");
        }
    }
}
