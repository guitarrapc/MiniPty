using System.Buffers;

namespace MiniPty.Internal;

internal static class PtyReadBuffer
{
    internal const int Size = 4096;

    internal static RentedByteBuffer RentBytes() => new(ArrayPool<byte>.Shared.Rent(Size), Size);

    internal static RentedByteBuffer RentBytes(int length) => new(ArrayPool<byte>.Shared.Rent(length), length);

    internal readonly struct RentedByteBuffer(byte[] buffer, int length) : IDisposable
    {
        private readonly byte[] buffer = buffer;

        internal Memory<byte> Memory => buffer.AsMemory(0, length);

        internal Span<byte> Span => buffer.AsSpan(0, length);

        public void Dispose()
        {
            if (buffer is not null)
                ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
