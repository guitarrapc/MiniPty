using System.Runtime.CompilerServices;

namespace MiniPty.Console.Internal;

internal static class PtyConsoleInputForward
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Forward(
        Stream ptyInput,
        ReadOnlySpan<byte> data,
        IPtyConsoleInputObserver? observer,
        TimeProvider timeProvider,
        long attachTimestamp)
    {
        var o = observer;
        if (o is not null)
            o.OnForwardedInput(timeProvider.GetElapsedTime(attachTimestamp), data);

        ptyInput.Write(data);
    }
}
