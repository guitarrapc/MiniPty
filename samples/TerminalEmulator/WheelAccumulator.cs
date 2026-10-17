namespace TerminalEmulator;

/// <summary>
/// Carries fractional wheel movement between events, so scrolling follows distance rather than event count. A
/// mouse notch is a whole step, but a trackpad sends a stream of small fractional deltas, dozens per swipe; treating
/// each as at least one step made a single swipe scroll hundreds of lines. A change of direction drops the carry, so
/// reversing responds at once.
/// </summary>
internal struct WheelAccumulator
{
    private double _lines;

    /// <summary>
    /// Adds <paramref name="lines"/> of movement (positive is up, away from the user) and returns how many whole
    /// steps of <paramref name="step"/> lines to act on now, signed like the movement. The remainder is kept.
    /// </summary>
    public int Add(double lines, int step = 1)
    {
        if (Math.Sign(lines) != Math.Sign(_lines))
            _lines = 0;

        _lines += lines;
        var steps = (int)(_lines / step);
        _lines -= steps * step;
        return steps;
    }
}
