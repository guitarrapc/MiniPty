namespace MiniPty.Internal;

/// <summary>
/// Terminal-size bounds applied at spawn and on resize: the largest dimension the platform's PTY API encodes.
/// </summary>
/// <remarks>
/// ConPTY takes a <c>COORD</c> of two signed 16-bit values, so Windows ends at <see cref="short.MaxValue"/>;
/// Unix <c>winsize</c> fields are unsigned 16-bit, so Unix ends at <see cref="ushort.MaxValue"/>. Values are
/// clamped rather than rejected: a GUI host computes its grid from a window that can be zero-sized before layout
/// or wider than any plausible terminal, and neither should fault a size-changed handler. The former 512-cell
/// cap was below what high-resolution displays with small fonts produce, and a silent clamp there made the
/// frontend grid and the PTY disagree on where lines wrap.
/// </remarks>
internal static class PtySizeLimits
{
    internal const int MinDimension = 1;

    internal static readonly int MaxDimension = OperatingSystem.IsWindows() ? short.MaxValue : ushort.MaxValue;

    internal static int Clamp(int dimension) => Math.Clamp(dimension, MinDimension, MaxDimension);

    internal static PtySize Clamp(PtySize size) => new(Clamp(size.Columns), Clamp(size.Rows));
}
