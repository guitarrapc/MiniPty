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
        {
            try
            {
                o.OnForwardedInput(timeProvider.GetElapsedTime(attachTimestamp), data);
            }
            catch (Exception)
            {
                // Observation must not break input forwarding or attach disposal.
            }
        }

        ptyInput.Write(data);
    }
}
