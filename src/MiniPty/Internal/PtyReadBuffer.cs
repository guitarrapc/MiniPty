using System.Buffers;
using System.Text;

namespace MiniPty.Internal;

internal static class PtyReadBuffer
{
    internal const int Size = 4096;

    internal static int GetCharBufferLength(Encoding encoding) => encoding.GetMaxCharCount(Size);

    internal static RentedByteBuffer RentBytes() => new(ArrayPool<byte>.Shared.Rent(Size), Size);

    internal static RentedByteBuffer RentBytes(int length) => new(ArrayPool<byte>.Shared.Rent(length), length);

    internal static RentedCharBuffer RentChars(Encoding encoding) =>
        new(ArrayPool<char>.Shared.Rent(GetCharBufferLength(encoding)), GetCharBufferLength(encoding));

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

    internal readonly struct RentedCharBuffer(char[] buffer, int length) : IDisposable
    {
        private readonly char[] buffer = buffer;

        internal Span<char> Span => buffer.AsSpan(0, length);

        public void Dispose()
        {
            if (buffer is not null)
                ArrayPool<char>.Shared.Return(buffer);
        }
    }
}
