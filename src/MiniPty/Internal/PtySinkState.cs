namespace MiniPty.Internal;

/// <summary>Where a one-shot sink is in its life: its result can be taken once, after output ended.</summary>
internal enum PtySinkState : byte
{
    /// <summary>Output is arriving.</summary>
    Writing,

    /// <summary>Output ended; the segments hold the result until it is taken.</summary>
    Ended,

    /// <summary>The result was taken, and the segments went back to the pool.</summary>
    Taken,

    /// <summary>The completion failed, and the segments went back to the pool.</summary>
    Aborted,
}

internal static class PtySinkStates
{
    /// <summary>Moves an ended sink to <see cref="PtySinkState.Taken"/>, or throws when its result cannot be taken.</summary>
    internal static void Take(ref PtySinkState state)
    {
        switch (state)
        {
            case PtySinkState.Ended:
                state = PtySinkState.Taken;
                return;
            case PtySinkState.Writing:
                throw new InvalidOperationException("PTY output has not ended.");
            case PtySinkState.Taken:
                throw new InvalidOperationException("The result was already taken.");
            default:
                throw new InvalidOperationException("The completion failed, and its output was released.");
        }
    }
}
