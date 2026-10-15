using System.Buffers;
using System.Text;

namespace MiniPty.Internal;

internal static class PtyIo
{
    internal const int Utf8StackThreshold = 256;

    internal static Task WriteUtf8Async(Stream stream, string input, CancellationToken cancellationToken)
    {
        var byteCount = Encoding.UTF8.GetByteCount(input);
        if (byteCount == 0)
            return Task.CompletedTask;

        if (byteCount <= Utf8StackThreshold)
        {
            Span<byte> buffer = stackalloc byte[byteCount];
            Encoding.UTF8.GetBytes(input, buffer);
            stream.Write(buffer);
            return Task.CompletedTask;
        }

        var rented = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            var written = Encoding.UTF8.GetBytes(input, rented.AsSpan(0, byteCount));
            stream.Write(rented.AsSpan(0, written));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        return Task.CompletedTask;
    }

    internal static int ToWaitMilliseconds(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
            return 0;
        if (timeout >= TimeSpan.FromMilliseconds(int.MaxValue))
            return int.MaxValue;
        return (int)timeout.TotalMilliseconds;
    }
}
