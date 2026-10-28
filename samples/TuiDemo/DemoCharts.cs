/// <summary>
/// Chart data for the Charts page, all derived from the animation frame like the jobs: two scrolling signals plotted
/// with braille dots, five bars in eighth blocks, and three sparklines.
/// </summary>
internal static class DemoCharts
{
    internal const int PlotColumns = 60;
    internal const int PlotRows = 8;
    internal const int PlotCells = PlotColumns * PlotRows;
    internal const int BarCount = 5;
    internal const int SparkCount = 3;
    internal const int SparkLength = 22;

    /// <summary>Plot colors: 0 is empty, then the two signals and the dotted zero axis.</summary>
    internal const byte Sine = 1;
    internal const byte Mix = 2;
    internal const byte Axis = 3;

    private const int DotsWide = PlotColumns * 2;
    private const int DotsHigh = PlotRows * 4;

    private static readonly string[] BarLabels = ["GET", "POST", "PUT", "PATCH", "DEL"];
    private static readonly string[] SparkLabels = ["cpu ", "net ", "disk"];

    internal static string BarLabel(int bar) => BarLabels[bar];

    internal static string SparkLabel(int spark) => SparkLabels[spark];

    /// <summary>
    /// Fills one braille dot pattern (the low byte of U+2800 + bits) and one color per plot cell. A signal later in the
    /// order wins a shared cell's color; the axis never overrides a signal.
    /// </summary>
    internal static void Plot(long frame, Span<byte> bits, Span<byte> colors)
    {
        bits.Clear();
        colors.Clear();
        for (var x = 0; x < DotsWide; x += 4)
            SetDot(bits, colors, x, DotsHigh / 2, Axis);
        for (var signal = Sine; signal <= Mix; signal++)
        {
            var previous = -1;
            for (var x = 0; x < DotsWide; x++)
            {
                var y = SignalDot(signal, x, frame);
                // Fill the vertical gap to the previous column so steep slopes stay connected.
                var from = previous < 0 ? y : Math.Min(previous, y);
                var to = previous < 0 ? y : Math.Max(previous, y);
                for (var dot = from; dot <= to; dot++)
                    SetDot(bits, colors, x, dot, signal);
                previous = y;
            }
        }
    }

    /// <summary>Bar height in eighths of a cell, out of the four rows' 32.</summary>
    internal static int BarEighths(int bar, long frame) =>
        (int)Math.Round((0.55 + 0.4 * Math.Sin(frame / 9.0 + bar * 1.3)) * 32);

    /// <summary>Sparkline level 0–7 of series <paramref name="spark"/> at time <paramref name="t"/>.</summary>
    internal static int SparkLevel(int spark, long t) => Math.Clamp((int)(Load(spark, t) * 8), 0, 7);

    internal static int LoadPercent(int spark, long frame) => (int)Math.Round(Load(spark, frame) * 100);

    private static double Load(int spark, long t) =>
        0.5 + 0.45 * Math.Sin(t / (4.0 + spark * 3)) * Math.Cos(t / (11.0 + spark * 2));

    private static int SignalDot(byte signal, int x, long frame)
    {
        var value = signal == Sine
            ? 0.9 * Math.Sin(2 * Math.PI * (x + 3 * frame) / 60.0)
            : 0.55 * Math.Sin(2 * Math.PI * (x + frame) / 41.0) + 0.35 * Math.Sin(2 * Math.PI * (x - 2 * frame) / 17.0);
        return Math.Clamp((int)Math.Round((1 - value) * (DotsHigh - 1) / 2), 0, DotsHigh - 1);
    }

    private static void SetDot(Span<byte> bits, Span<byte> colors, int x, int y, byte color)
    {
        // Braille dot numbering: the left column is bits 0, 1, 2, 6 from the top; the right column 3, 4, 5, 7.
        var bit = (x & 1) == 0
            ? (y & 3) switch { 0 => 0x01, 1 => 0x02, 2 => 0x04, _ => 0x40 }
            : (y & 3) switch { 0 => 0x08, 1 => 0x10, 2 => 0x20, _ => 0x80 };
        var cell = y / 4 * PlotColumns + x / 2;
        bits[cell] |= (byte)bit;
        if (color != Axis || colors[cell] == 0)
            colors[cell] = color;
    }
}
